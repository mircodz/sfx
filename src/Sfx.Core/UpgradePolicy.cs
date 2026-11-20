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
}
