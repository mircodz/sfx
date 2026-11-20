using System.Globalization;
using Microsoft.ServiceFabric.Client;
using Microsoft.ServiceFabric.Common;

namespace Sfx.Core;

/// <summary>Everything that changes the cluster. One method per operation, so the TUI and a CLI run the same code.</summary>
public sealed class ClusterOperations(IServiceFabricClient client)
{
    // Replicas

    public Task RestartReplicaAsync(string node, PartitionId partition, string replica, CancellationToken cancellationToken) =>
        client.Replicas.RestartReplicaAsync(new NodeName(node), partition, ReplicaId.Parse(replica), cancellationToken: cancellationToken);

    public Task RemoveReplicaAsync(string node, PartitionId partition, string replica, bool force, CancellationToken cancellationToken) =>
        client.Replicas.RemoveReplicaAsync(new NodeName(node), partition, ReplicaId.Parse(replica), force, cancellationToken: cancellationToken);

    /// <param name="target">The node to move to, or null to let the cluster pick one.</param>
    public Task MoveReplicaAsync(
        string role, string serviceId, PartitionId partition, string currentNode, string? target, bool ignoreConstraints,
        CancellationToken cancellationToken)
    {
        var current = new NodeName(currentNode);
        var to = target is null ? null : new NodeName(target);
        return role switch
        {
            "Primary" => client.Partitions.MovePrimaryReplicaAsync(partition, to, ignoreConstraints, cancellationToken: cancellationToken),
            "ActiveSecondary" or "IdleSecondary" =>
                client.Partitions.MoveSecondaryReplicaAsync(partition, current, to, ignoreConstraints, cancellationToken: cancellationToken),
            "ActiveAuxiliary" or "IdleAuxiliary" or "PrimaryAuxiliary" =>
                client.Partitions.MoveAuxiliaryReplicaAsync(serviceId, partition, current, to, ignoreConstraints, cancellationToken: cancellationToken),
            "Instance" => client.Partitions.MoveInstanceAsync(serviceId, partition, current, to, ignoreConstraints, cancellationToken: cancellationToken),
            _ => throw new InvalidOperationException($"A replica in role '{role}' can't be moved."),
        };
    }

    public static bool CanMove(string role) =>
        role is "Primary" or "ActiveSecondary" or "IdleSecondary" or "ActiveAuxiliary" or "IdleAuxiliary" or "PrimaryAuxiliary" or "Instance";

    // Nodes

    public Task ActivateNodeAsync(string node, CancellationToken cancellationToken) =>
        client.Nodes.EnableNodeAsync(new NodeName(node), cancellationToken: cancellationToken);

    public Task DeactivateNodeAsync(string node, DeactivationIntent intent, CancellationToken cancellationToken) =>
        client.Nodes.DisableNodeAsync(new NodeName(node), new DeactivationIntentDescription(intent, "sfx"), cancellationToken: cancellationToken);

    public async Task RestartNodeAsync(string node, CancellationToken cancellationToken)
    {
        // The instance id makes the restart a no-op if the node already restarted in the meantime.
        var info = await client.Nodes.GetNodeInfoAsync(new NodeName(node), cancellationToken: cancellationToken);
        await client.Nodes.RestartNodeAsync(new NodeName(node), new RestartNodeDescription(info.InstanceId, CreateFabricDump.False), cancellationToken: cancellationToken);
    }

    public Task RemoveNodeStateAsync(string node, CancellationToken cancellationToken) =>
        client.Nodes.RemoveNodeStateAsync(new NodeName(node), cancellationToken: cancellationToken);

    /// <summary>Stops the node for <paramref name="duration"/>; needs the Fault Analysis Service.</summary>
    public async Task StopNodeAsync(string node, TimeSpan duration, CancellationToken cancellationToken)
    {
        var info = await client.Nodes.GetNodeInfoAsync(new NodeName(node), cancellationToken: cancellationToken);
        await client.Faults.StartNodeTransitionAsync(
            new NodeName(node), Guid.NewGuid(), NodeTransitionType.Stop, info.InstanceId, (int)duration.TotalSeconds, cancellationToken: cancellationToken);
    }

    /// <summary>Starts a node stopped with <see cref="StopNodeAsync"/>; needs the Fault Analysis Service.</summary>
    public async Task StartNodeAsync(string node, CancellationToken cancellationToken)
    {
        var info = await client.Nodes.GetNodeInfoAsync(new NodeName(node), cancellationToken: cancellationToken);
        await client.Faults.StartNodeTransitionAsync(
            new NodeName(node), Guid.NewGuid(), NodeTransitionType.Start, info.InstanceId, 0, cancellationToken: cancellationToken);
    }

    // Partitions

    /// <summary>Restarts the partition's replicas; needs the Fault Analysis Service.</summary>
    public Task RestartPartitionAsync(string serviceId, PartitionId partition, RestartPartitionMode mode, CancellationToken cancellationToken) =>
        client.Faults.StartPartitionRestartAsync(serviceId, partition, Guid.NewGuid(), mode, cancellationToken: cancellationToken);

    public Task RecoverPartitionAsync(PartitionId partition, CancellationToken cancellationToken) =>
        client.Partitions.RecoverPartitionAsync(partition, cancellationToken: cancellationToken);

    public Task ResetPartitionLoadAsync(PartitionId partition, CancellationToken cancellationToken) =>
        client.Partitions.ResetPartitionLoadAsync(partition, cancellationToken: cancellationToken);

    // Services, applications, cluster

    public Task RecoverServicePartitionsAsync(string serviceId, CancellationToken cancellationToken) =>
        client.Partitions.RecoverServicePartitionsAsync(serviceId, cancellationToken: cancellationToken);

    public Task DeleteServiceAsync(string serviceId, bool force, CancellationToken cancellationToken) =>
        client.Services.DeleteServiceAsync(serviceId, force, cancellationToken: cancellationToken);

    public Task DeleteApplicationAsync(string applicationId, bool force, CancellationToken cancellationToken) =>
        client.Applications.DeleteApplicationAsync(applicationId, force, cancellationToken: cancellationToken);

    /// <summary>
    /// Upgrades an application to <paramref name="targetVersion"/> (which may be its current version, to change only parameters).
    /// </summary>
    /// <param name="parameters">Every parameter the application should have afterwards; ones left out go back to their defaults.</param>
    /// <param name="failureAction">For monitored upgrades: what to do when a health check fails.</param>
    /// <param name="policy">Timeouts and health limits; null, or null fields, leave the cluster's defaults.</param>
    public Task UpgradeApplicationAsync(
        string applicationId, string applicationName, string targetVersion, IReadOnlyDictionary<string, string> parameters,
        UpgradeMode mode, FailureAction? failureAction, UpgradePolicy? policy, CancellationToken cancellationToken) =>
        client.Applications.StartApplicationUpgradeAsync(
            applicationId,
            new ApplicationUpgradeDescription(
                name: applicationName,
                targetApplicationTypeVersion: targetVersion,
                upgradeKind: UpgradeKind.Rolling,
                parameters: parameters,
                rollingUpgradeMode: mode,
                upgradeReplicaSetCheckTimeoutInSeconds: policy?.ReplicaSetCheckTimeout is { } check ? (long)check.TotalSeconds : null,
                forceRestart: policy?.ForceRestart,
                monitoringPolicy: mode == UpgradeMode.Monitored ? MonitoringPolicy(failureAction, policy) : null,
                applicationHealthPolicy: policy is { HasHealthPolicy: true } ? HealthPolicy(policy) : null),
            cancellationToken: cancellationToken);

    // The SDK's defaults for these are false and 0, not "unset", so every field is passed explicitly.
    private static ApplicationHealthPolicy HealthPolicy(UpgradePolicy policy) =>
        new(
            considerWarningAsError: policy.ConsiderWarningAsError,
            maxPercentUnhealthyDeployedApplications: policy.MaxPercentUnhealthyDeployedApplications,
            defaultServiceTypeHealthPolicy: policy.HasServiceTypeHealthPolicy
                ? new ServiceTypeHealthPolicy(
                    maxPercentUnhealthyPartitionsPerService: policy.MaxPercentUnhealthyPartitionsPerService,
                    maxPercentUnhealthyReplicasPerPartition: policy.MaxPercentUnhealthyReplicasPerPartition,
                    maxPercentUnhealthyServices: policy.MaxPercentUnhealthyServices)
                : null,
            serviceTypeHealthPolicyMap: null);

    private static string? Milliseconds(TimeSpan? duration) =>
        duration is { } d ? ((long)d.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>Moves a manual upgrade on into <paramref name="upgradeDomain"/>.</summary>
    public Task ResumeApplicationUpgradeAsync(string applicationId, string upgradeDomain, CancellationToken cancellationToken) =>
        client.Applications.ResumeApplicationUpgradeAsync(applicationId, new ResumeApplicationUpgradeDescription(upgradeDomain), cancellationToken: cancellationToken);

    public Task RollbackApplicationUpgradeAsync(string applicationId, CancellationToken cancellationToken) =>
        client.Applications.RollbackApplicationUpgradeAsync(applicationId, cancellationToken: cancellationToken);

    public Task ResumeClusterUpgradeAsync(string upgradeDomain, CancellationToken cancellationToken) =>
        client.Cluster.ResumeClusterUpgradeAsync(new ResumeClusterUpgradeDescription(upgradeDomain), cancellationToken: cancellationToken);

    public Task RollbackClusterUpgradeAsync(CancellationToken cancellationToken) =>
        client.Cluster.RollbackClusterUpgradeAsync(cancellationToken: cancellationToken);

    /// <summary>Upgrades the cluster's code, configuration or both to versions already provisioned.</summary>
    /// <param name="codeVersion">The Service Fabric version to move to, or null to keep today's.</param>
    /// <param name="configVersion">The cluster configuration version to move to, or null to keep today's.</param>
    /// <param name="failureAction">For monitored upgrades: what to do when a health check fails.</param>
    /// <param name="policy">Timeouts and health limits; null, or null fields, leave the cluster's defaults.</param>
    public Task UpgradeClusterAsync(
        string? codeVersion, string? configVersion, UpgradeMode mode, FailureAction? failureAction, UpgradePolicy? policy,
        CancellationToken cancellationToken) =>
        client.Cluster.StartClusterUpgradeAsync(
            ClusterUpgradeDescription(codeVersion, configVersion, mode, failureAction, policy), cancellationToken: cancellationToken);

    internal static StartClusterUpgradeDescription ClusterUpgradeDescription(
        string? codeVersion, string? configVersion, UpgradeMode mode, FailureAction? failureAction, UpgradePolicy? policy)
    {
        if (string.IsNullOrEmpty(codeVersion) && string.IsNullOrEmpty(configVersion))
        {
            throw new ArgumentException("A cluster upgrade needs a code version, a config version or both.");
        }

        return new StartClusterUpgradeDescription(
            codeVersion: string.IsNullOrEmpty(codeVersion) ? null : codeVersion,
            configVersion: string.IsNullOrEmpty(configVersion) ? null : configVersion,
            upgradeKind: UpgradeKind.Rolling,
            rollingUpgradeMode: mode,
            upgradeReplicaSetCheckTimeoutInSeconds: policy?.ReplicaSetCheckTimeout is { } check ? (long)check.TotalSeconds : null,
            forceRestart: policy?.ForceRestart,
            monitoringPolicy: mode == UpgradeMode.Monitored ? MonitoringPolicy(failureAction, policy) : null,
            clusterHealthPolicy: policy is { HasClusterHealthPolicy: true }
                ? new ClusterHealthPolicy(
                    considerWarningAsError: policy.ConsiderWarningAsError,
                    maxPercentUnhealthyNodes: policy.MaxPercentUnhealthyNodes,
                    maxPercentUnhealthyApplications: policy.MaxPercentUnhealthyApplications)
                : null);
    }

    private static MonitoringPolicyDescription MonitoringPolicy(FailureAction? failureAction, UpgradePolicy? policy) =>
        new(
            failureAction: failureAction ?? FailureAction.Rollback,
            healthCheckWaitDurationInMilliseconds: Milliseconds(policy?.HealthCheckWait),
            healthCheckStableDurationInMilliseconds: Milliseconds(policy?.HealthCheckStable),
            healthCheckRetryTimeoutInMilliseconds: Milliseconds(policy?.HealthCheckRetry),
            upgradeTimeoutInMilliseconds: Milliseconds(policy?.UpgradeTimeout),
            upgradeDomainTimeoutInMilliseconds: Milliseconds(policy?.UpgradeDomainTimeout));

    /// <summary>Sets how many instances a stateless service runs (-1: one on every node).</summary>
    public Task ScaleStatelessServiceAsync(string serviceId, int instanceCount, CancellationToken cancellationToken) =>
        client.Services.UpdateServiceAsync(
            serviceId,
            new StatelessServiceUpdateDescription(flags: ((int)ServiceUpdateFlags.Count).ToString(CultureInfo.InvariantCulture), instanceCount: instanceCount),
            cancellationToken: cancellationToken);

    /// <summary>Sets a stateful service's target replica set size and, optionally, its minimum.</summary>
    public Task ScaleStatefulServiceAsync(string serviceId, int targetReplicaSetSize, int? minReplicaSetSize, CancellationToken cancellationToken)
    {
        var flags = ServiceUpdateFlags.Count | (minReplicaSetSize is null ? 0 : ServiceUpdateFlags.MinReplicaSetSize);
        return client.Services.UpdateServiceAsync(
            serviceId,
            new StatefulServiceUpdateDescription(
                flags: ((int)flags).ToString(CultureInfo.InvariantCulture),
                targetReplicaSetSize: targetReplicaSetSize,
                minReplicaSetSize: minReplicaSetSize),
            cancellationToken: cancellationToken);
    }

    /// <summary>Which fields of a service update are set; values from the REST API's ServiceUpdateDescription.Flags.</summary>
    [Flags]
    private enum ServiceUpdateFlags
    {
        Count = 1,
        MinReplicaSetSize = 16,
    }

    public Task RecoverAllPartitionsAsync(CancellationToken cancellationToken) =>
        client.Partitions.RecoverAllPartitionsAsync(cancellationToken: cancellationToken);

    public Task RecoverSystemPartitionsAsync(CancellationToken cancellationToken) =>
        client.Partitions.RecoverSystemPartitionsAsync(cancellationToken: cancellationToken);

    // Deployed code packages

    public Task RestartCodePackageAsync(string node, string applicationId, DeployedCodePackageRow codePackage, CancellationToken cancellationToken) =>
        RestartCodePackageAsync(
            node, applicationId, codePackage.ServiceManifestName, codePackage.Name, codePackage.InstanceId, codePackage.ActivationId, cancellationToken);

    /// <param name="instanceId">The running instance; if the code package restarted since, the cluster refuses rather than restart it again.</param>
    /// <param name="activationId">The service package's activation; empty for shared activation.</param>
    public Task RestartCodePackageAsync(
        string node, string applicationId, string serviceManifestName, string codePackageName, string instanceId, string activationId,
        CancellationToken cancellationToken) =>
        client.CodePackages.RestartDeployedCodePackageAsync(
            new NodeName(node),
            applicationId,
            new RestartDeployedCodePackageDescription(serviceManifestName, codePackageName, instanceId, activationId),
            cancellationToken: cancellationToken);
}
