using Microsoft.ServiceFabric.Client;
using Microsoft.ServiceFabric.Common;
using Microsoft.ServiceFabric.Common.Exceptions;

namespace Sfx.Core;

/// <summary>
/// Values for the health queries' <c>…HealthStateFilter</c> parameters. Asking for no children's states keeps a large
/// cluster's answer small: an entity's own evaluations still say which children are unhealthy and why.
/// </summary>
public static class HealthFilter
{
    public const int None = 1;
    public const int WarningOrError = 4 | 8;
}

/// <summary>The entity asked about isn't on the cluster; the message says which, in plain words.</summary>
public sealed class NotFoundException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Fetches the details of one entity: its info, health, manifests, load and upgrade state.</summary>
public sealed class ClusterInspector(IServiceFabricClient client)
{
    private const string SystemApplicationTypeName = "System";

    // Nodes

    public Task<NodeInfo> GetNodeAsync(string node, CancellationToken cancellationToken) =>
        Required(() => client.Nodes.GetNodeInfoAsync(new NodeName(node), cancellationToken: cancellationToken), $"There's no node {node} on this cluster.");

    public async Task<HealthReport> GetNodeHealthAsync(string node, CancellationToken cancellationToken) =>
        HealthReport.From(await client.Nodes.GetNodeHealthAsync(new NodeName(node), cancellationToken: cancellationToken));

    public Task<NodeLoadInfo> GetNodeLoadAsync(string node, CancellationToken cancellationToken) =>
        client.Nodes.GetNodeLoadInfoAsync(new NodeName(node), cancellationToken: cancellationToken);

    // Applications

    public Task<ApplicationInfo> GetApplicationAsync(string applicationId, CancellationToken cancellationToken) =>
        Required(
            () => client.Applications.GetApplicationInfoAsync(applicationId, excludeApplicationParameters: false, cancellationToken: cancellationToken),
            $"There's no application {NameOf(applicationId)} on this cluster.");

    public async Task<HealthReport> GetApplicationHealthAsync(string applicationId, CancellationToken cancellationToken) =>
        HealthReport.From(await client.Applications.GetApplicationHealthAsync(
            applicationId,
            deployedApplicationsHealthStateFilter: HealthFilter.None,
            servicesHealthStateFilter: HealthFilter.None,
            excludeHealthStatistics: true,
            cancellationToken: cancellationToken));

    public async Task<string> GetApplicationManifestAsync(string typeName, string typeVersion, CancellationToken cancellationToken)
    {
        RequireUserApplication(typeName);
        return (await client.ApplicationTypes.GetApplicationManifestAsync(typeName, typeVersion, cancellationToken: cancellationToken)).Manifest;
    }

    /// <summary>The provisioned versions of an application type, with each one's default parameters.</summary>
    public async Task<IReadOnlyList<ApplicationTypeInfo>> GetApplicationTypeVersionsAsync(string typeName, CancellationToken cancellationToken)
    {
        RequireUserApplication(typeName);
        return await ClusterQueries.PageAsync(token => client.ApplicationTypes.GetApplicationTypeInfoListByNameAsync(
            typeName,
            continuationToken: token,
            cancellationToken: cancellationToken));
    }

    public Task<ApplicationUpgradeProgressInfo> GetApplicationUpgradeAsync(string applicationId, CancellationToken cancellationToken) =>
        client.Applications.GetApplicationUpgradeAsync(applicationId, cancellationToken: cancellationToken);

    // Services

    public Task<ServiceInfo> GetServiceAsync(string applicationId, string serviceId, CancellationToken cancellationToken) =>
        Required(
            () => client.Services.GetServiceInfoAsync(applicationId, serviceId, cancellationToken: cancellationToken),
            $"There's no service {NameOf(serviceId)} in {NameOf(applicationId)} on this cluster.");

    public Task<ServiceDescription> GetServiceDescriptionAsync(string serviceId, CancellationToken cancellationToken) =>
        client.Services.GetServiceDescriptionAsync(serviceId, cancellationToken: cancellationToken);

    public async Task<HealthReport> GetServiceHealthAsync(string serviceId, CancellationToken cancellationToken) =>
        HealthReport.From(await client.Services.GetServiceHealthAsync(
            serviceId,
            partitionsHealthStateFilter: HealthFilter.None,
            excludeHealthStatistics: true,
            cancellationToken: cancellationToken));

    /// <summary>The manifest of the service package that provides <paramref name="serviceTypeName"/>.</summary>
    public async Task<string> GetServiceManifestAsync(
        string applicationTypeName,
        string applicationTypeVersion,
        string serviceTypeName,
        CancellationToken cancellationToken)
    {
        RequireUserApplication(applicationTypeName);
        var serviceType = await client.ServiceTypes.GetServiceTypeInfoByNameAsync(
            applicationTypeName,
            applicationTypeVersion,
            serviceTypeName,
            cancellationToken: cancellationToken);
        var manifest = await client.ServiceTypes.GetServiceManifestAsync(
            applicationTypeName,
            applicationTypeVersion,
            serviceType.ServiceManifestName,
            cancellationToken: cancellationToken);
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
        Required(
            () => client.Partitions.GetPartitionInfoAsync(partitionId, cancellationToken: cancellationToken),
            $"There's no partition {partitionId} on this cluster.");

    public async Task<HealthReport> GetPartitionHealthAsync(PartitionId partitionId, CancellationToken cancellationToken) =>
        HealthReport.From(await client.Partitions.GetPartitionHealthAsync(
            partitionId,
            replicasHealthStateFilter: HealthFilter.None,
            excludeHealthStatistics: true,
            cancellationToken: cancellationToken));

    public Task<PartitionLoadInformation> GetPartitionLoadAsync(PartitionId partitionId, CancellationToken cancellationToken) =>
        client.Partitions.GetPartitionLoadInformationAsync(partitionId, cancellationToken: cancellationToken);

    public Task<ReplicaInfo> GetReplicaAsync(PartitionId partitionId, string replicaId, CancellationToken cancellationToken) =>
        client.Replicas.GetReplicaInfoAsync(partitionId, ReplicaId.Parse(replicaId), cancellationToken: cancellationToken);

    public async Task<HealthReport> GetReplicaHealthAsync(PartitionId partitionId, string replicaId, CancellationToken cancellationToken) =>
        HealthReport.From(await client.Replicas.GetReplicaHealthAsync(partitionId, ReplicaId.Parse(replicaId), cancellationToken: cancellationToken));

    // Cluster

    public Task<ClusterVersion> GetClusterVersionAsync(CancellationToken cancellationToken) =>
        client.Cluster.GetClusterVersionAsync(cancellationToken: cancellationToken);

    /// <summary>The cluster's own events and evaluations, without every node's and application's state.</summary>
    public async Task<HealthReport> GetClusterHealthAsync(CancellationToken cancellationToken) =>
        HealthReport.From(await client.Cluster.GetClusterHealthAsync(
            nodesHealthStateFilter: HealthFilter.None,
            applicationsHealthStateFilter: HealthFilter.None,
            excludeHealthStatistics: true,
            cancellationToken: cancellationToken));

    /// <summary>
    /// The cluster's evaluations and unhealthy events, with how many nodes, applications, services, partitions and replicas
    /// (system ones included) are in each state, but no per-entity states.
    /// </summary>
    public Task<ClusterHealth> GetClusterHealthSummaryAsync(CancellationToken cancellationToken) =>
        client.Cluster.GetClusterHealthAsync(
            nodesHealthStateFilter: HealthFilter.None,
            applicationsHealthStateFilter: HealthFilter.None,
            eventsHealthStateFilter: HealthFilter.WarningOrError,
            excludeHealthStatistics: false,
            includeSystemApplicationHealthStatistics: true,
            cancellationToken: cancellationToken);

    public async Task<string> GetClusterManifestAsync(CancellationToken cancellationToken) =>
        (await client.Cluster.GetClusterManifestAsync(cancellationToken: cancellationToken)).Manifest;

    public Task<ClusterUpgradeProgressObject> GetClusterUpgradeAsync(CancellationToken cancellationToken) =>
        client.Cluster.GetClusterUpgradeProgressAsync(cancellationToken: cancellationToken);

    /// <summary>The Service Fabric versions provisioned for a cluster upgrade, in no particular order.</summary>
    public async Task<IReadOnlyList<string>> GetProvisionedCodeVersionsAsync(CancellationToken cancellationToken) =>
        (await client.Cluster.GetProvisionedFabricCodeVersionInfoListAsync(cancellationToken: cancellationToken))
            .Select(v => v.CodeVersion)
            .OfType<string>()
            .ToList();

    /// <summary>The cluster configuration versions provisioned for a cluster upgrade, in no particular order.</summary>
    public async Task<IReadOnlyList<string>> GetProvisionedConfigVersionsAsync(CancellationToken cancellationToken) =>
        (await client.Cluster.GetProvisionedFabricConfigVersionInfoListAsync(cancellationToken: cancellationToken))
            .Select(v => v.ConfigVersion)
            .OfType<string>()
            .ToList();

    /// <summary>The cluster configuration JSON. Only standalone clusters have one.</summary>
    public async Task<string> GetClusterConfigurationAsync(CancellationToken cancellationToken) =>
        Inspect.PrettyJson((await client.Cluster.GetClusterConfigurationAsync("1.0.0", cancellationToken: cancellationToken)).ClusterConfigurationValue);

    public Task<ClusterLoadInfo> GetClusterLoadAsync(CancellationToken cancellationToken) =>
        client.Cluster.GetClusterLoadAsync(cancellationToken: cancellationToken);

    /// <summary>"fabric:/orders/Counter" for the id "orders~Counter": ids are names without "fabric:/" and with '~' for '/'.</summary>
    public static string NameOf(string id) => "fabric:/" + id.Replace('~', '/');

    /// <summary>"orders~Counter" for the name "fabric:/orders/Counter", the other way from <see cref="NameOf"/>; "fabric:/" may be left out.</summary>
    public static string IdOf(string name) =>
        (name.StartsWith("fabric:/", StringComparison.Ordinal) ? name["fabric:/".Length..] : name).Replace('/', '~');

    /// <summary>
    /// The cluster answers a lookup of something that isn't there with nothing at all (HTTP 204) or with a not-found
    /// error, depending on the query; both become a <see cref="NotFoundException"/> saying what's missing.
    /// </summary>
    internal static async Task<T> Required<T>(Func<Task<T?>> query, string missing) where T : class
    {
        try
        {
            return await query() ?? throw new NotFoundException(missing);
        }
        catch (ServiceFabricException ex) when (ClusterQueries.IsGone(ex))
        {
            throw new NotFoundException(missing, ex);
        }
    }

    private static void RequireUserApplication(string applicationTypeName)
    {
        if (applicationTypeName == SystemApplicationTypeName)
        {
            throw new InvalidOperationException("System services have no application or service manifest; see the cluster manifest.");
        }
    }
}
