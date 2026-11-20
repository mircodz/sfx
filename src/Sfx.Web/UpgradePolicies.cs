using System.Globalization;
using Sfx.Core;

namespace Sfx.Web;

/// <summary>Reads the "policy.*" inputs of an application or cluster upgrade; a missing or empty one leaves the cluster's default.</summary>
internal static class UpgradePolicies
{
    public const string Prefix = "policy.";

    private static readonly (string Name, string Label)[] ApplicationOnly =
    [
        ("maxPercentUnhealthyDeployedApplications", "Max unhealthy deployed applications"),
        ("maxPercentUnhealthyServices", "Max unhealthy services"),
        ("maxPercentUnhealthyPartitionsPerService", "Max unhealthy partitions per service"),
        ("maxPercentUnhealthyReplicasPerPartition", "Max unhealthy replicas per partition"),
    ];

    private static readonly (string Name, string Label)[] ClusterOnly =
    [
        ("maxPercentUnhealthyNodes", "Max unhealthy nodes"),
        ("maxPercentUnhealthyApplications", "Max unhealthy applications"),
    ];

    /// <summary>The policy the inputs ask for, or null when they set none of it.</summary>
    /// <param name="monitored">Whether the upgrade mode is monitored; timeouts, health checks and limits mean nothing otherwise.</param>
    /// <param name="cluster">
    /// Whether it's for a cluster upgrade, which judges nodes and applications rather than services, partitions and replicas.
    /// </param>
    public static UpgradePolicy? Parse(Args inputs, bool monitored, bool cluster = false)
    {
        // A limit the upgrade doesn't use would be silently ignored by the cluster; say so instead.
        foreach (var (name, label) in cluster ? ApplicationOnly : ClusterOnly)
        {
            if (inputs.Optional(Prefix + name) is not null)
            {
                throw new ActionException($"{label} only applies to {(cluster ? "application" : "cluster")} upgrades.");
            }
        }

        var policy = new UpgradePolicy(
            HealthCheckWait: Seconds(inputs, "healthCheckWait", "Health check wait"),
            HealthCheckStable: Seconds(inputs, "healthCheckStable", "Health check stable duration"),
            HealthCheckRetry: Seconds(inputs, "healthCheckRetry", "Health check retry timeout"),
            UpgradeDomainTimeout: Seconds(inputs, "upgradeDomainTimeout", "Upgrade domain timeout"),
            UpgradeTimeout: Seconds(inputs, "upgradeTimeout", "Upgrade timeout"),
            ReplicaSetCheckTimeout: Seconds(inputs, "replicaSetCheckTimeout", "Replica set check timeout"),
            ForceRestart: Bool(inputs, "forceRestart", "Force restart"),
            ConsiderWarningAsError: Bool(inputs, "considerWarningAsError", "Consider warnings as errors"),
            MaxPercentUnhealthyDeployedApplications: Percent(inputs, "maxPercentUnhealthyDeployedApplications", "Max unhealthy deployed applications"),
            MaxPercentUnhealthyServices: Percent(inputs, "maxPercentUnhealthyServices", "Max unhealthy services"),
            MaxPercentUnhealthyPartitionsPerService: Percent(inputs, "maxPercentUnhealthyPartitionsPerService", "Max unhealthy partitions per service"),
            MaxPercentUnhealthyReplicasPerPartition: Percent(inputs, "maxPercentUnhealthyReplicasPerPartition", "Max unhealthy replicas per partition"),
            MaxPercentUnhealthyNodes: Percent(inputs, "maxPercentUnhealthyNodes", "Max unhealthy nodes"),
            MaxPercentUnhealthyApplications: Percent(inputs, "maxPercentUnhealthyApplications", "Max unhealthy applications"));

        if (!monitored && (policy.HasMonitoring || policy.HasHealthPolicy || policy.HasClusterHealthPolicy))
        {
            throw new ActionException("Timeouts and health checks only apply to monitored upgrades.");
        }
        return policy == new UpgradePolicy() ? null : policy;
    }

    private static TimeSpan? Seconds(Args inputs, string name, string label) =>
        inputs.Optional(Prefix + name) is not { } text ? null
        : uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? TimeSpan.FromSeconds(seconds)
        : throw new ActionException($"{label} must be a whole number of seconds.");

    private static bool? Bool(Args inputs, string name, string label) => inputs.Optional(Prefix + name) switch
    {
        null => null,
        "true" => true,
        "false" => false,
        _ => throw new ActionException($"{label} must be true or false."),
    };

    private static int? Percent(Args inputs, string name, string label) =>
        inputs.Optional(Prefix + name) is not { } text ? null
        : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) && percent <= 100 ? percent
        : throw new ActionException($"{label} must be a percentage from 0 to 100.");
}
