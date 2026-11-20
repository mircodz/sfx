using System.Globalization;
using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Web;

/// <summary>Asks the cluster how far an operation got; one poll, returning its state and what to show.</summary>
internal delegate Task<(string State, string? Detail)> OperationProbe(CancellationToken cancellationToken);

/// <summary>Picks, per action, how to tell from the cluster that a change it asked for has actually happened.</summary>
internal static class OperationWatcher
{
    private static readonly string[] CountedRoles = ["Primary", "ActiveSecondary", "Instance"];

    /// <summary>The probe for an action, or null when there's nothing to wait for: it's done once requested.</summary>
    public static OperationProbe? For(UiSession s, string actionId, Args target, Args inputs, DateTime startedUtc) => actionId switch
    {
        "replica.move" => async ct =>
        {
            var replicas = await s.Queries.GetReplicasAsync(target.Partition("partitionId"), ct);
            return SettleChecks.ReplicaMove(
                target["role"], target["replicaId"], target["node"], inputs.Optional("node"),
                replicas.Select(r => (r.Id, r.NodeName, r.Role)).ToList());
        },

        "service.scale" => async ct =>
        {
            var requested = inputs.Int("count");
            var partitions = await s.Queries.GetPartitionsAsync(target["serviceId"], ct);
            var counts = await Task.WhenAll(partitions.Select(async p =>
            {
                var replicas = await s.Queries.GetReplicasAsync(p.Id, ct);
                return (p.Status, replicas.Count(r => r.Status == nameof(ReplicaStatus.Ready) && CountedRoles.Contains(r.Role)));
            }));
            return SettleChecks.ScaleProgress(requested, counts, target["kind"] == nameof(ServiceKind.Stateful));
        },

        "node.deactivate" or "node.remove-data" => NodeReaches(s, target["node"], nameof(NodeStatus.Disabled)),
        "node.activate" or "node.start" => NodeReaches(s, target["node"], nameof(NodeStatus.Up)),
        "node.stop" => NodeReaches(s, target["node"], nameof(NodeStatus.Down)),

        "node.restart" => async ct =>
        {
            var node = await s.Inspector.GetNodeAsync(target["node"], ct);
            TimeSpan? uptime = long.TryParse(node.NodeUpTimeInSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : null;
            return SettleChecks.NodeRestart(node.NodeStatus?.ToString() ?? "", uptime, DateTime.UtcNow - startedUtc);
        },

        "application.upgrade" => async ct =>
        {
            var report = UpgradeReport.From(await s.Inspector.GetApplicationUpgradeAsync(target["applicationId"], ct));
            return report.Target == inputs["version"] && SettleChecks.IsThisUpgrade(Started(report), startedUtc)
                ? Forward(report)
                : (SettleChecks.Running, "Waiting for the upgrade to start.");
        },

        "cluster.upgrade" => async ct =>
        {
            var progress = await s.Inspector.GetClusterUpgradeAsync(ct);
            var report = UpgradeReport.From(progress);
            var matches = (inputs.Optional("codeVersion") is not { } code || progress.CodeVersion == code)
                && (inputs.Optional("configVersion") is not { } config || progress.ConfigVersion == config);
            return matches && SettleChecks.IsThisUpgrade(Started(report), startedUtc)
                ? Forward(report)
                : (SettleChecks.Running, "Waiting for the upgrade to start.");
        },

        "upgrade.resume" => async ct => Forward(await UpgradeAsync(s, target, ct)),

        "upgrade.rollback" => async ct =>
        {
            var report = await UpgradeAsync(s, target, ct);
            return SettleChecks.RollbackOutcome(report.State, report.FailureReason, report.CurrentDomain);
        },

        _ => null,
    };

    private static OperationProbe NodeReaches(UiSession s, string node, string expected) => async ct =>
        SettleChecks.NodeStatus(expected, (await s.Inspector.GetNodeAsync(node, ct)).NodeStatus?.ToString() ?? "");

    private static async Task<UpgradeReport> UpgradeAsync(UiSession s, Args target, CancellationToken ct) =>
        target["kind"] == "cluster"
            ? UpgradeReport.From(await s.Inspector.GetClusterUpgradeAsync(ct))
            : UpgradeReport.From(await s.Inspector.GetApplicationUpgradeAsync(target["applicationId"], ct));

    private static (string State, string? Detail) Forward(UpgradeReport report) =>
        SettleChecks.UpgradeOutcome(
            report.State, report.FailureReason, report.CurrentDomain, report.NextDomain,
            report.Domains.Count(d => d.State == nameof(UpgradeDomainState.Completed)), report.Domains.Count);

    private static DateTime? Started(UpgradeReport report) => new UpgradeRow("", "", null, "", report).StartedUtc;
}
