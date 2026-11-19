using Microsoft.ServiceFabric.Client;
using Microsoft.ServiceFabric.Common;

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
            long.TryParse(n.NodeUpTimeInSeconds, out var seconds) ? TimeSpan.FromSeconds(seconds) : null)).ToList();
    }

    /// <summary>User applications, with the system application first (the cluster doesn't list it).</summary>
    public async Task<IReadOnlyList<ApplicationRow>> GetApplicationsAsync(CancellationToken cancellationToken)
    {
        var apps = await PageAsync(token => client.Applications.GetApplicationInfoListAsync(
            excludeApplicationParameters: true, continuationToken: token, cancellationToken: cancellationToken));

        var rows = new List<ApplicationRow>
        {
            new(SystemApplicationId, SystemApplicationName, "System", "", "Ready", HealthState.Unknown),
        };
        rows.AddRange(apps.Select(a => new ApplicationRow(
            a.Id, a.Name.ToString(), a.TypeName ?? "", a.TypeVersion ?? "", Text(a.Status), a.HealthState ?? HealthState.Unknown)));
        return rows;
    }

    public async Task<IReadOnlyList<ServiceRow>> GetServicesAsync(string applicationId, CancellationToken cancellationToken)
    {
        var services = await PageAsync(token => client.Services.GetServiceInfoListAsync(
            applicationId, continuationToken: token, cancellationToken: cancellationToken));

        return services.Select(s => new ServiceRow(
            s.Id, s.Name.ToString(), s.TypeName ?? "", Text(s.ServiceKind), Text(s.ServiceStatus), s.HealthState ?? HealthState.Unknown)).ToList();
    }

    public async Task<IReadOnlyList<PartitionRow>> GetPartitionsAsync(string serviceId, CancellationToken cancellationToken)
    {
        var partitions = await PageAsync(token => client.Partitions.GetPartitionInfoListAsync(
            serviceId, continuationToken: token, cancellationToken: cancellationToken));

        return partitions.Select(p => new PartitionRow(
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
            })).ToList();
    }

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

    /// <summary>
    /// Whether a system service runs, e.g. <c>EventStoreService</c>. Optional ones are missing on many clusters, and
    /// asking them anything then waits for the gateway's timeout instead of failing.
    /// </summary>
    public async Task<bool> HasSystemServiceAsync(string name, CancellationToken cancellationToken) =>
        (await GetServicesAsync(SystemApplicationId, cancellationToken)).Any(s => s.Name == $"{SystemApplicationName}/{name}");

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
