using Microsoft.ServiceFabric.Common;
using Microsoft.ServiceFabric.Common.Exceptions;
using Sfx.Core;

namespace Sfx.Web;

/// <summary>Every replica in the cluster, found by walking applications, services and partitions.</summary>
internal static class ClusterReplicas
{
    private const int Parallelism = 16;

    public static async Task<IReadOnlyList<ClusterReplicaDto>> LoadAsync(ClusterQueries queries, CancellationToken cancellationToken)
    {
        // One gate for every level, so the whole walk has at most this many requests open.
        using var gate = new SemaphoreSlim(Parallelism);

        async Task<IReadOnlyList<TOut>> Each<TIn, TItem, TOut>(
            IEnumerable<TIn> items, Func<TIn, Task<IReadOnlyList<TItem>>> query, Func<TIn, TItem, TOut> select)
        {
            var perItem = await Task.WhenAll(items.Select(async item =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    return (await query(item)).Select(found => select(item, found));
                }
                // Deleted since its parent was listed.
                catch (ServiceFabricException ex) when (ex.ErrorCode.ToString() is var code && (code.Contains("NOT_FOUND") || code.Contains("DOES_NOT_EXIST")))
                {
                    return [];
                }
                finally
                {
                    gate.Release();
                }
            }));
            return perItem.SelectMany(found => found).ToList();
        }

        var applications = await queries.GetApplicationsAsync(cancellationToken);
        var services = await Each(applications, a => queries.GetServicesAsync(a.Id, cancellationToken), (a, svc) => (App: a, Service: svc));
        var partitions = await Each(services, s => queries.GetPartitionsAsync(s.Service.Id, cancellationToken), (s, p) => (s.App, s.Service, Partition: p));
        var replicas = await Each(partitions, p => queries.GetReplicasAsync(p.Partition.Id, cancellationToken), (p, r) => new ClusterReplicaDto(
            p.App.Id, p.App.Name, p.Service.Id, p.Service.Name, p.Service.Kind,
            p.Partition.Id.ToString(), p.Partition.Status, Dto.Health(p.Partition.Health),
            r.Id, r.NodeName, r.Role, r.Status, Dto.Health(r.Health)));

        return replicas
            .OrderBy(r => r.Application, StringComparer.Ordinal)
            .ThenBy(r => r.Service, StringComparer.Ordinal)
            .ThenBy(r => r.PartitionId, StringComparer.Ordinal)
            .ThenBy(r => r.Role != nameof(ReplicaRole.Primary))
            .ThenBy(r => r.Role, StringComparer.Ordinal)
            .ThenBy(r => r.Node, StringComparer.Ordinal)
            .ToList();
    }
}
