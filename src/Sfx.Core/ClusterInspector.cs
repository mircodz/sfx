using Microsoft.ServiceFabric.Client;
using Microsoft.ServiceFabric.Common;

namespace Sfx.Core;

/// <summary>Fetches the details of one entity: its info, health, manifests, load and upgrade state.</summary>
public sealed class ClusterInspector(IServiceFabricClient client)
{
    private const string SystemApplicationTypeName = "System";

    // Nodes

    public Task<NodeInfo> GetNodeAsync(string node, CancellationToken cancellationToken) =>
        client.Nodes.GetNodeInfoAsync(new NodeName(node), cancellationToken: cancellationToken);

    public async Task<HealthReport> GetNodeHealthAsync(string node, CancellationToken cancellationToken) =>
        HealthReport.From(await client.Nodes.GetNodeHealthAsync(new NodeName(node), cancellationToken: cancellationToken));

    public Task<NodeLoadInfo> GetNodeLoadAsync(string node, CancellationToken cancellationToken) =>
        client.Nodes.GetNodeLoadInfoAsync(new NodeName(node), cancellationToken: cancellationToken);

    // Applications

    public Task<ApplicationInfo> GetApplicationAsync(string applicationId, CancellationToken cancellationToken) =>
        client.Applications.GetApplicationInfoAsync(applicationId, excludeApplicationParameters: false, cancellationToken: cancellationToken);

    public async Task<HealthReport> GetApplicationHealthAsync(string applicationId, CancellationToken cancellationToken) =>
        HealthReport.From(await client.Applications.GetApplicationHealthAsync(applicationId, cancellationToken: cancellationToken));

    public async Task<string> GetApplicationManifestAsync(string typeName, string typeVersion, CancellationToken cancellationToken)
    {
        RequireUserApplication(typeName);
        return (await client.ApplicationTypes.GetApplicationManifestAsync(typeName, typeVersion, cancellationToken: cancellationToken)).Manifest;
    }

    public Task<ApplicationUpgradeProgressInfo> GetApplicationUpgradeAsync(string applicationId, CancellationToken cancellationToken) =>
        client.Applications.GetApplicationUpgradeAsync(applicationId, cancellationToken: cancellationToken);

    // Services

    public Task<ServiceInfo> GetServiceAsync(string applicationId, string serviceId, CancellationToken cancellationToken) =>
        client.Services.GetServiceInfoAsync(applicationId, serviceId, cancellationToken: cancellationToken);

    public Task<ServiceDescription> GetServiceDescriptionAsync(string serviceId, CancellationToken cancellationToken) =>
        client.Services.GetServiceDescriptionAsync(serviceId, cancellationToken: cancellationToken);

    public async Task<HealthReport> GetServiceHealthAsync(string serviceId, CancellationToken cancellationToken) =>
        HealthReport.From(await client.Services.GetServiceHealthAsync(serviceId, cancellationToken: cancellationToken));

    /// <summary>The manifest of the service package that provides <paramref name="serviceTypeName"/>.</summary>
    public async Task<string> GetServiceManifestAsync(
        string applicationTypeName, string applicationTypeVersion, string serviceTypeName, CancellationToken cancellationToken)
    {
        RequireUserApplication(applicationTypeName);
        var serviceType = await client.ServiceTypes.GetServiceTypeInfoByNameAsync(
            applicationTypeName, applicationTypeVersion, serviceTypeName, cancellationToken: cancellationToken);
        var manifest = await client.ServiceTypes.GetServiceManifestAsync(
            applicationTypeName, applicationTypeVersion, serviceType.ServiceManifestName, cancellationToken: cancellationToken);
        return manifest.Manifest;
    }

    /// <summary>A service manifest by its name, as deployed service packages are named.</summary>
    public async Task<string> GetServiceManifestByNameAsync(string applicationId, string manifestName, CancellationToken cancellationToken)
    {
        var app = await GetApplicationAsync(applicationId, cancellationToken);
        RequireUserApplication(app.TypeName);
        return (await client.ServiceTypes.GetServiceManifestAsync(app.TypeName, app.TypeVersion, manifestName, cancellationToken: cancellationToken)).Manifest;
    }

    // Partitions and replicas

    public Task<ServicePartitionInfo> GetPartitionAsync(PartitionId partitionId, CancellationToken cancellationToken) =>
        client.Partitions.GetPartitionInfoAsync(partitionId, cancellationToken: cancellationToken);

    public async Task<HealthReport> GetPartitionHealthAsync(PartitionId partitionId, CancellationToken cancellationToken) =>
        HealthReport.From(await client.Partitions.GetPartitionHealthAsync(partitionId, cancellationToken: cancellationToken));

    public Task<PartitionLoadInformation> GetPartitionLoadAsync(PartitionId partitionId, CancellationToken cancellationToken) =>
        client.Partitions.GetPartitionLoadInformationAsync(partitionId, cancellationToken: cancellationToken);

    public Task<ReplicaInfo> GetReplicaAsync(PartitionId partitionId, string replicaId, CancellationToken cancellationToken) =>
        client.Replicas.GetReplicaInfoAsync(partitionId, ReplicaId.Parse(replicaId), cancellationToken: cancellationToken);

    public async Task<HealthReport> GetReplicaHealthAsync(PartitionId partitionId, string replicaId, CancellationToken cancellationToken) =>
        HealthReport.From(await client.Replicas.GetReplicaHealthAsync(partitionId, ReplicaId.Parse(replicaId), cancellationToken: cancellationToken));

    // Cluster

    public Task<ClusterVersion> GetClusterVersionAsync(CancellationToken cancellationToken) =>
        client.Cluster.GetClusterVersionAsync(cancellationToken: cancellationToken);

    public async Task<HealthReport> GetClusterHealthAsync(CancellationToken cancellationToken) =>
        HealthReport.From(await client.Cluster.GetClusterHealthAsync(cancellationToken: cancellationToken));

    public async Task<string> GetClusterManifestAsync(CancellationToken cancellationToken) =>
        (await client.Cluster.GetClusterManifestAsync(cancellationToken: cancellationToken)).Manifest;

    public Task<ClusterUpgradeProgressObject> GetClusterUpgradeAsync(CancellationToken cancellationToken) =>
        client.Cluster.GetClusterUpgradeProgressAsync(cancellationToken: cancellationToken);

    /// <summary>The cluster configuration JSON. Only standalone clusters have one.</summary>
    public async Task<string> GetClusterConfigurationAsync(CancellationToken cancellationToken) =>
        Inspect.PrettyJson((await client.Cluster.GetClusterConfigurationAsync("1.0.0", cancellationToken: cancellationToken)).ClusterConfigurationValue);

    public Task<ClusterLoadInfo> GetClusterLoadAsync(CancellationToken cancellationToken) =>
        client.Cluster.GetClusterLoadAsync(cancellationToken: cancellationToken);

    private static void RequireUserApplication(string applicationTypeName)
    {
        if (applicationTypeName == SystemApplicationTypeName)
        {
            throw new InvalidOperationException("System services have no application or service manifest; see the cluster manifest.");
        }
    }
}
