using System.Globalization;
using Microsoft.ServiceFabric.Common;

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
        var client = queries.Client;
        var cluster = client.Cluster.GetClusterUpgradeProgressAsync(cancellationToken: cancellationToken);
        var apps = (await queries.GetApplicationsAsync(cancellationToken))
            .Where(a => a.Id != ClusterQueries.SystemApplicationId)
            .ToList();

        using var gate = new SemaphoreSlim(Parallelism);
        var upgrades = await Task.WhenAll(apps.Select(async app =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var upgrade = await client.Applications.GetApplicationUpgradeAsync(app.Id, cancellationToken: cancellationToken);
                var report = UpgradeReport.From(upgrade);
                // While an upgrade runs the application still reports the version it's leaving.
                var from = app.TypeVersion != report.Target ? app.TypeVersion : "";
                return new UpgradeRow(app.Name, "application", app.Id, from, report);
            }
            finally
            {
                gate.Release();
            }
        }));

        var rows = new List<UpgradeRow> { new("cluster", "cluster", null, "", UpgradeReport.From(await cluster)) };
        rows.AddRange(upgrades);
        return rows.Where(HasRun).ToList();
    }

    /// <summary>What was never upgraded still reports a completed upgrade, but one that never started.</summary>
    private static bool HasRun(UpgradeRow row) => row.StartedUtc is not null;
}
