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

    public Task RecoverAllPartitionsAsync(CancellationToken cancellationToken) =>
        client.Partitions.RecoverAllPartitionsAsync(cancellationToken: cancellationToken);

    public Task RecoverSystemPartitionsAsync(CancellationToken cancellationToken) =>
        client.Partitions.RecoverSystemPartitionsAsync(cancellationToken: cancellationToken);

    // Deployed code packages

    public Task RestartCodePackageAsync(string node, string applicationId, DeployedCodePackageRow codePackage, CancellationToken cancellationToken) =>
        client.CodePackages.RestartDeployedCodePackageAsync(
            new NodeName(node),
            applicationId,
            new RestartDeployedCodePackageDescription(
                codePackage.ServiceManifestName, codePackage.Name, codePackage.InstanceId, codePackage.ActivationId),
            cancellationToken: cancellationToken);
}
