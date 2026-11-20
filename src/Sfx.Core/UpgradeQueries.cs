using System.Globalization;
using Microsoft.ServiceFabric.Common;
using Microsoft.ServiceFabric.Common.Exceptions;

namespace Sfx.Core;

/// <summary>An upgrade of the cluster or of one application, running or the last one that ran.</summary>
/// <param name="Kind">"cluster" or "application".</param>
/// <param name="From">The version before the upgrade, when known; empty otherwise.</param>
public sealed record UpgradeRow(string Name, string Kind, string? ApplicationId, string From, UpgradeReport Report)
{
    /// <summary>Null when it never ran: the cluster then reports the year 1.</summary>
    /// <remarks>The client library hands the UTC time back as text in the current culture's format.</remarks>
    public DateTime? StartedUtc =>
        (TryParse(CultureInfo.CurrentCulture, out var at) || TryParse(CultureInfo.InvariantCulture, out at)) && at.Year > 1 ? at : null;

    private bool TryParse(CultureInfo culture, out DateTime utc) =>
        DateTime.TryParse(Report.Started, culture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);

    public int DomainsDone => Report.Domains.Count(d => d.State == nameof(UpgradeDomainState.Completed));

    /// <summary>Running first, then waiting for someone, then failed or rolled back, then done.</summary>
    public int Urgency => Report.State switch
    {
        nameof(UpgradeState.RollingForwardInProgress) or nameof(UpgradeState.RollingBackInProgress) => 0,
        nameof(UpgradeState.RollingForwardPending) => 1,
        nameof(UpgradeState.Failed) or nameof(UpgradeState.RollingBackCompleted) => 2,
        _ => 3,
    };
}

public sealed class UpgradeQueries(ClusterQueries queries)
{
    private const int Parallelism = 8;

    /// <summary>The cluster's last upgrade and every application's, leaving out what was never upgraded.</summary>
    public async Task<IReadOnlyList<UpgradeRow>> GetUpgradesAsync(CancellationToken cancellationToken)
    {
        var cluster = queries.Client.Cluster.GetClusterUpgradeProgressAsync(cancellationToken: cancellationToken);
        var apps = (await queries.GetApplicationsAsync(cancellationToken))
            .Where(a => a.Id != ClusterQueries.SystemApplicationId)
            .ToList();
        var upgrades = await GetApplicationUpgradesAsync(apps, cancellationToken);

        var rows = new List<UpgradeRow> { new("cluster", "cluster", null, "", UpgradeReport.From(await cluster)) };
        rows.AddRange(upgrades);
        return rows.Where(HasRun).ToList();
    }

    /// <summary>
    /// Only the upgrades under way: the cluster's when it's running, pending or failed, and those of the applications
    /// listed as upgrading, so a large cluster costs one call per running upgrade rather than one per application.
    /// </summary>
    public async Task<IReadOnlyList<UpgradeRow>> GetRunningUpgradesAsync(CancellationToken cancellationToken)
    {
        var cluster = queries.Client.Cluster.GetClusterUpgradeProgressAsync(cancellationToken: cancellationToken);
        var upgrading = (await queries.GetApplicationsAsync(cancellationToken))
            .Where(a => a.Id != ClusterQueries.SystemApplicationId && a.Status == nameof(ApplicationStatus.Upgrading))
            .ToList();
        var upgrades = await GetApplicationUpgradesAsync(upgrading, cancellationToken);

        var rows = new List<UpgradeRow>();
        var clusterReport = UpgradeReport.From(await cluster);
        if (clusterReport.InProgress || clusterReport.State == nameof(UpgradeState.Failed))
        {
            rows.Add(new UpgradeRow("cluster", "cluster", null, "", clusterReport));
        }
        rows.AddRange(upgrades);
        return rows;
    }

    /// <summary>Each application's last upgrade, at most <see cref="Parallelism"/> asked at a time; ones deleted meanwhile are left out.</summary>
    private async Task<IReadOnlyList<UpgradeRow>> GetApplicationUpgradesAsync(IReadOnlyList<ApplicationRow> apps, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(Parallelism);
        var upgrades = await Task.WhenAll(apps.Select(async app =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var upgrade = await queries.Client.Applications.GetApplicationUpgradeAsync(app.Id, cancellationToken: cancellationToken);
                var report = UpgradeReport.From(upgrade);
                // While an upgrade runs the application still reports the version it's leaving.
                var from = app.TypeVersion != report.Target ? app.TypeVersion : "";
                return new UpgradeRow(app.Name, "application", app.Id, from, report);
            }
            catch (ServiceFabricException ex) when (ClusterQueries.IsGone(ex))
            {
                return null;
            }
            finally
            {
                gate.Release();
            }
        }));
        return upgrades.OfType<UpgradeRow>().ToList();
    }

    /// <summary>What was never upgraded still reports a completed upgrade, but one that never started.</summary>
    private static bool HasRun(UpgradeRow row) => row.StartedUtc is not null;
}
