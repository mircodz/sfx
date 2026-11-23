using System.Globalization;

namespace Sfx.Core;

/// <summary>How an application or cluster upgrade is paced and judged. Anything null is left to the cluster's default.</summary>
/// <param name="ReplicaSetCheckTimeout">How long to wait for a replica set to be safe before upgrading a domain anyway.</param>
/// <param name="ForceRestart">Restart processes even when only configuration or data changed.</param>
/// <param name="MaxPercentUnhealthyNodes">Cluster upgrades only.</param>
/// <param name="MaxPercentUnhealthyApplications">Cluster upgrades only.</param>
public sealed record UpgradePolicy(
    TimeSpan? HealthCheckWait = null,
    TimeSpan? HealthCheckStable = null,
    TimeSpan? HealthCheckRetry = null,
    TimeSpan? UpgradeDomainTimeout = null,
    TimeSpan? UpgradeTimeout = null,
    TimeSpan? ReplicaSetCheckTimeout = null,
    bool? ForceRestart = null,
    bool? ConsiderWarningAsError = null,
    int? MaxPercentUnhealthyDeployedApplications = null,
    int? MaxPercentUnhealthyServices = null,
    int? MaxPercentUnhealthyPartitionsPerService = null,
    int? MaxPercentUnhealthyReplicasPerPartition = null,
    int? MaxPercentUnhealthyNodes = null,
    int? MaxPercentUnhealthyApplications = null)
{
    /// <summary>Whether any health check timing or upgrade timeout is set; those only apply to monitored upgrades.</summary>
    public bool HasMonitoring =>
        HealthCheckWait is not null || HealthCheckStable is not null || HealthCheckRetry is not null
        || UpgradeDomainTimeout is not null || UpgradeTimeout is not null;

    /// <summary>Whether any part of an application upgrade's health policy is set; it only applies to monitored upgrades.</summary>
    public bool HasHealthPolicy => HasServiceTypeHealthPolicy || ConsiderWarningAsError is not null || MaxPercentUnhealthyDeployedApplications is not null;

    /// <summary>Whether any part of a cluster upgrade's health policy is set; it only applies to monitored upgrades.</summary>
    public bool HasClusterHealthPolicy =>
        ConsiderWarningAsError is not null || MaxPercentUnhealthyNodes is not null || MaxPercentUnhealthyApplications is not null;

    public bool HasServiceTypeHealthPolicy =>
        MaxPercentUnhealthyServices is not null || MaxPercentUnhealthyPartitionsPerService is not null || MaxPercentUnhealthyReplicasPerPartition is not null;

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

    /// <summary>
    /// Refuses a policy the upgrade wouldn't use: limits of the other kind of upgrade, which the cluster would silently
    /// ignore, and timeouts or health checks on an unmonitored upgrade.
    /// </summary>
    public void Require(bool monitored, bool cluster)
    {
        (bool Set, string Label)[] other = cluster
            ?
            [
                (MaxPercentUnhealthyDeployedApplications is not null, ApplicationOnly[0].Label),
                (MaxPercentUnhealthyServices is not null, ApplicationOnly[1].Label),
                (MaxPercentUnhealthyPartitionsPerService is not null, ApplicationOnly[2].Label),
                (MaxPercentUnhealthyReplicasPerPartition is not null, ApplicationOnly[3].Label),
            ]
            : [(MaxPercentUnhealthyNodes is not null, ClusterOnly[0].Label), (MaxPercentUnhealthyApplications is not null, ClusterOnly[1].Label)];
        if (other.FirstOrDefault(o => o.Set) is { Set: true, Label: var label })
        {
            throw new RefusedException($"{label} only applies to {(cluster ? "application" : "cluster")} upgrades.");
        }
        if (!monitored && (HasMonitoring || HasHealthPolicy || HasClusterHealthPolicy))
        {
            throw new RefusedException("Timeouts and health checks only apply to monitored upgrades.");
        }
    }

    /// <summary>
    /// Reads a policy typed into a form, by field name: "healthCheckWait", "healthCheckStable", "healthCheckRetry",
    /// "upgradeDomainTimeout", "upgradeTimeout" and "replicaSetCheckTimeout" in whole seconds; "forceRestart" and
    /// "considerWarningAsError" as true or false; the "maxPercentUnhealthy…" limits as 0 to 100. A missing or empty
    /// field leaves the cluster's default; null when none is set. Throws a <see cref="RefusedException"/> naming the field
    /// it can't read.
    /// </summary>
    /// <param name="monitored">Whether the upgrade is monitored; timeouts, health checks and limits mean nothing otherwise.</param>
    /// <param name="cluster">
    /// Whether it's for a cluster upgrade, which judges nodes and applications rather than services, partitions and replicas.
    /// </param>
    public static UpgradePolicy? Parse(IReadOnlyDictionary<string, string> fields, bool monitored, bool cluster = false)
    {
        string? Field(string name) => fields.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

        // A limit the upgrade doesn't use would be silently ignored by the cluster; say so instead.
        foreach (var (name, label) in cluster ? ApplicationOnly : ClusterOnly)
        {
            if (Field(name) is not null)
            {
                throw new RefusedException($"{label} only applies to {(cluster ? "application" : "cluster")} upgrades.");
            }
        }

        TimeSpan? Seconds(string name, string label) =>
            Field(name) is not { } text ? null
            : uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? TimeSpan.FromSeconds(seconds)
            : throw new RefusedException($"{label} must be a whole number of seconds.");

        bool? Bool(string name, string label) => Field(name) switch
        {
            null => null,
            "true" => true,
            "false" => false,
            _ => throw new RefusedException($"{label} must be true or false."),
        };

        int? Percent(string name, string label) =>
            Field(name) is not { } text ? null
            : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) && percent <= 100 ? percent
            : throw new RefusedException($"{label} must be a percentage from 0 to 100.");

        var policy = new UpgradePolicy(
            HealthCheckWait: Seconds("healthCheckWait", "Health check wait"),
            HealthCheckStable: Seconds("healthCheckStable", "Health check stable duration"),
            HealthCheckRetry: Seconds("healthCheckRetry", "Health check retry timeout"),
            UpgradeDomainTimeout: Seconds("upgradeDomainTimeout", "Upgrade domain timeout"),
            UpgradeTimeout: Seconds("upgradeTimeout", "Upgrade timeout"),
            ReplicaSetCheckTimeout: Seconds("replicaSetCheckTimeout", "Replica set check timeout"),
            ForceRestart: Bool("forceRestart", "Force restart"),
            ConsiderWarningAsError: Bool("considerWarningAsError", "Consider warnings as errors"),
            MaxPercentUnhealthyDeployedApplications: Percent("maxPercentUnhealthyDeployedApplications", "Max unhealthy deployed applications"),
            MaxPercentUnhealthyServices: Percent("maxPercentUnhealthyServices", "Max unhealthy services"),
            MaxPercentUnhealthyPartitionsPerService: Percent("maxPercentUnhealthyPartitionsPerService", "Max unhealthy partitions per service"),
            MaxPercentUnhealthyReplicasPerPartition: Percent("maxPercentUnhealthyReplicasPerPartition", "Max unhealthy replicas per partition"),
            MaxPercentUnhealthyNodes: Percent("maxPercentUnhealthyNodes", "Max unhealthy nodes"),
            MaxPercentUnhealthyApplications: Percent("maxPercentUnhealthyApplications", "Max unhealthy applications"));

        policy.Require(monitored, cluster);
        return policy == new UpgradePolicy() ? null : policy;
    }
}
