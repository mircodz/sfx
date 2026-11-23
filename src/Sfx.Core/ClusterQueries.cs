using Microsoft.ServiceFabric.Client;
using Microsoft.ServiceFabric.Common;
using Microsoft.ServiceFabric.Common.Exceptions;

namespace Sfx.Core;

public sealed class ClusterQueries(IServiceFabricClient client)
{
    public const string SystemApplicationId = "System";
    public const string SystemApplicationName = "fabric:/System";

    public IServiceFabricClient Client => client;

    public async Task<IReadOnlyList<NodeRow>> GetNodesAsync(CancellationToken cancellationToken)
    {
        var nodes = await PageAsync(token => client.Nodes.GetNodeInfoListAsync(
            continuationToken: token, nodeStatusFilter: NodeStatusFilter.All, cancellationToken: cancellationToken));

        return nodes.Select(n => new NodeRow(
            n.Name.ToString(),
            n.IpAddressOrFQDN ?? "",
            n.Type ?? "",
            Text(n.NodeStatus),
            n.HealthState ?? HealthState.Unknown,
            n.UpgradeDomain ?? "",
            n.FaultDomain ?? "",
            n.IsSeedNode ?? false,
            n.CodeVersion ?? "",
            long.TryParse(n.NodeUpTimeInSeconds, out var seconds) ? TimeSpan.FromSeconds(seconds) : null,
            n.Id?.Id ?? "")).ToList();
    }

    /// <summary>User applications, with the system application first (the cluster doesn't list it).</summary>
    public async Task<IReadOnlyList<ApplicationRow>> GetApplicationsAsync(CancellationToken cancellationToken)
    {
        var system = GetSystemApplicationHealthAsync(cancellationToken);
        var apps = await PageAsync(token => client.Applications.GetApplicationInfoListAsync(
            excludeApplicationParameters: true, continuationToken: token, cancellationToken: cancellationToken));

        var rows = new List<ApplicationRow>
        {
            new(SystemApplicationId, SystemApplicationName, "System", "", "Ready", await system),
        };
        rows.AddRange(apps.Select(Row));
        return rows;
    }

    /// <summary>One application's row; null when there's no such application. The system application works too.</summary>
    public async Task<ApplicationRow?> GetApplicationAsync(string applicationId, CancellationToken cancellationToken)
    {
        if (applicationId == SystemApplicationId)
        {
            return new(SystemApplicationId, SystemApplicationName, "System", "", "Ready", await GetSystemApplicationHealthAsync(cancellationToken));
        }
        var app = await OrNullAsync(() => client.Applications.GetApplicationInfoAsync(
            applicationId, excludeApplicationParameters: true, cancellationToken: cancellationToken));
        return app is null ? null : Row(app);
    }

    private static ApplicationRow Row(ApplicationInfo a) =>
        new(a.Id, a.Name.ToString(), a.TypeName ?? "", a.TypeVersion ?? "", Text(a.Status), a.HealthState ?? HealthState.Unknown);

    /// <summary>
    /// The system application's health, which the application list doesn't carry: one small call that leaves out its
    /// events, services and deployed applications. Unknown when the cluster won't say.
    /// </summary>
    private async Task<HealthState> GetSystemApplicationHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            var health = await client.Applications.GetApplicationHealthAsync(
                SystemApplicationId, eventsHealthStateFilter: HealthFilter.None, deployedApplicationsHealthStateFilter: HealthFilter.None,
                servicesHealthStateFilter: HealthFilter.None, excludeHealthStatistics: true, cancellationToken: cancellationToken);
            return health?.AggregatedHealthState ?? HealthState.Unknown;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthState.Unknown;
        }
    }

    public async Task<IReadOnlyList<ServiceRow>> GetServicesAsync(string applicationId, CancellationToken cancellationToken)
    {
        var services = await PageAsync(token => client.Services.GetServiceInfoListAsync(
            applicationId, continuationToken: token, cancellationToken: cancellationToken));

        return services.Select(Row).ToList();
    }

    /// <summary>One service's row; null when there's no such service.</summary>
    public async Task<ServiceRow?> GetServiceAsync(string applicationId, string serviceId, CancellationToken cancellationToken)
    {
        var service = await OrNullAsync(() => client.Services.GetServiceInfoAsync(applicationId, serviceId, cancellationToken: cancellationToken));
        return service is null ? null : Row(service);
    }

    private static ServiceRow Row(ServiceInfo s) =>
        new(s.Id, s.Name.ToString(), s.TypeName ?? "", Text(s.ServiceKind), Text(s.ServiceStatus), s.HealthState ?? HealthState.Unknown);

    public async Task<IReadOnlyList<PartitionRow>> GetPartitionsAsync(string serviceId, CancellationToken cancellationToken)
    {
        var partitions = await PageAsync(token => client.Partitions.GetPartitionInfoListAsync(
            serviceId, continuationToken: token, cancellationToken: cancellationToken));

        return partitions.Select(Row).ToList();
    }

    /// <summary>One partition's row; null when there's no such partition.</summary>
    public async Task<PartitionRow?> GetPartitionAsync(PartitionId partitionId, CancellationToken cancellationToken)
    {
        var partition = await OrNullAsync(() => client.Partitions.GetPartitionInfoAsync(partitionId, cancellationToken: cancellationToken));
        return partition is null ? null : Row(partition);
    }

    private static PartitionRow Row(ServicePartitionInfo p) => new(
        p.PartitionInformation.Id,
        Text(p.ServiceKind),
        Text(p.PartitionInformation.ServicePartitionKind),
        Text(p.PartitionStatus),
        p.HealthState ?? HealthState.Unknown,
        p switch
        {
            StatefulServicePartitionInfo s => $"{s.TargetReplicaSetSize}/{s.MinReplicaSetSize}",
            StatelessServicePartitionInfo s => s.InstanceCount == -1 ? "all nodes" : $"{s.InstanceCount}",
            _ => "",
        },
        (p.PartitionInformation as Int64RangePartitionInformation)?.LowKey,
        (p.PartitionInformation as Int64RangePartitionInformation)?.HighKey,
        (p.PartitionInformation as NamedPartitionInformation)?.Name);

    public async Task<IReadOnlyList<ReplicaRow>> GetReplicasAsync(PartitionId partitionId, CancellationToken cancellationToken)
    {
        var replicas = await PageAsync(token => client.Replicas.GetReplicaInfoListAsync(
            partitionId, continuationToken: token, cancellationToken: cancellationToken));

        return replicas.Select(r => new ReplicaRow(
            r switch
            {
                StatefulServiceReplicaInfo s => s.ReplicaId?.ToString() ?? "",
                StatelessServiceInstanceInfo s => s.InstanceId?.ToString() ?? "",
                _ => "",
            },
            r.NodeName?.ToString() ?? "",
            r is StatefulServiceReplicaInfo stateful ? Text(stateful.ReplicaRole) : "Instance",
            Text(r.ReplicaStatus),
            r.HealthState ?? HealthState.Unknown,
            r.Address ?? "")).ToList();
    }

    /// <summary>Every provisioned version of every application type, by name then version (newest first).</summary>
    public async Task<IReadOnlyList<ApplicationTypeRow>> GetApplicationTypesAsync(CancellationToken cancellationToken)
    {
        var types = await PageAsync(token => client.ApplicationTypes.GetApplicationTypeInfoListAsync(
            excludeApplicationParameters: false, continuationToken: token, cancellationToken: cancellationToken));

        return types
            .Select(t => new ApplicationTypeRow(
                t.Name ?? "", t.Version ?? "", Text(t.Status), t.DefaultParameterList ?? new Dictionary<string, string>(), t.StatusDetails ?? ""))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(t => t.Version, VersionComparer.Instance)
            .ToList();
    }

    /// <summary>The service types one version of an application type provides.</summary>
    public async Task<IReadOnlyList<ServiceTypeRow>> GetServiceTypesAsync(string typeName, string typeVersion, CancellationToken cancellationToken)
    {
        var types = await client.ServiceTypes.GetServiceTypeInfoListAsync(typeName, typeVersion, cancellationToken: cancellationToken);
        return (types ?? [])
            .Select(t => new ServiceTypeRow(
                t.ServiceTypeDescription?.ServiceTypeName ?? "",
                t.ServiceTypeDescription?.IsStateful ?? t.ServiceTypeDescription is StatefulServiceTypeDescription,
                t.ServiceManifestName ?? "",
                t.ServiceManifestVersion ?? ""))
            .ToList();
    }

    /// <summary>
    /// Whether a system service runs, e.g. <c>EventStoreService</c>. Optional ones are missing on many clusters, and
    /// asking them anything then waits for the gateway's timeout instead of failing.
    /// </summary>
    public async Task<bool> HasSystemServiceAsync(string name, CancellationToken cancellationToken) =>
        (await GetServicesAsync(SystemApplicationId, cancellationToken)).Any(s => s.Name == $"{SystemApplicationName}/{name}");

    /// <summary>
    /// Whether a node in this status can have anything deployed on it to ask about: Up, but also Disabling and Disabled
    /// (deactivated nodes keep their replicas and code packages until moved off) and Enabling; not Down, Removed or unknown.
    /// </summary>
    public static bool MayHostReplicas(string nodeStatus) =>
        nodeStatus is nameof(NodeStatus.Up) or nameof(NodeStatus.Disabling) or nameof(NodeStatus.Disabled) or nameof(NodeStatus.Enabling);

    /// <summary>Whether the cluster says the entity doesn't exist, e.g. because it was deleted since its parent was listed.</summary>
    public static bool IsGone(ServiceFabricException ex) =>
        ex.ErrorCode.ToString() is var code && (code.Contains("NOT_FOUND", StringComparison.Ordinal) || code.Contains("DOES_NOT_EXIST", StringComparison.Ordinal));

    /// <summary>What the query returns; null when the cluster says the entity doesn't exist (or answers with nothing).</summary>
    private static async Task<T?> OrNullAsync<T>(Func<Task<T>> query) where T : class
    {
        try
        {
            return await query();
        }
        catch (ServiceFabricException ex) when (IsGone(ex))
        {
            return null;
        }
    }

    /// <summary>Follows continuation tokens until the last page.</summary>
    public static async Task<List<T>> PageAsync<T>(Func<ContinuationToken?, Task<PagedData<T>>> query)
    {
        var all = new List<T>();
        ContinuationToken? token = null;
        do
        {
            var page = await query(token);
            all.AddRange(page.Data ?? []);
            token = page.ContinuationToken;
        }
        while (token?.Next == true);

        return all;
    }

    private static string Text<TEnum>(TEnum? value) where TEnum : struct, Enum => value?.ToString() ?? "";
}
