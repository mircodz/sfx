using Microsoft.ServiceFabric.Common;
using Microsoft.ServiceFabric.Common.Exceptions;
using Sfx.Core;

namespace Sfx.Web;

/// <summary>Every replica in the cluster, found by walking applications, services and partitions.</summary>
internal static class ClusterReplicas
{
    private const int Parallelism = 16;

    /// <summary>
    /// The whole walk. An application, service or partition that can't be listed is skipped and the walk goes on, so one
    /// transient error doesn't lose the other 60,000 replicas; when most at one level fail, the cluster isn't answering and
    /// the walk fails (the caller then keeps what it had).
    /// </summary>
    public static async Task<IReadOnlyList<ClusterReplicaDto>> LoadAsync(ClusterQueries queries, CancellationToken cancellationToken)
    {
        // One gate for every level, so the whole walk has at most this many requests open.
        using var gate = new SemaphoreSlim(Parallelism);

        async Task<IReadOnlyList<TOut>> Each<TIn, TItem, TOut>(
            IReadOnlyList<TIn> items, Func<TIn, Task<IReadOnlyList<TItem>>> query, Func<TIn, TItem, TOut> select)
        {
            var failed = 0;
            var perItem = await Task.WhenAll(items.Select(async item =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    return (await query(item)).Select(found => select(item, found));
                }
                catch (ServiceFabricException ex) when (ClusterQueries.IsGone(ex))
                {
                    return [];
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref failed);
                    return [];
                }
                finally
                {
                    gate.Release();
                }
            }));
            if (failed > 0 && failed * 2 >= items.Count)
            {
                throw new InvalidOperationException($"The cluster didn't answer for {failed} of {items.Count} items while listing replicas.");
            }
            return perItem.SelectMany(found => found).ToList();
        }

        var applications = await queries.GetApplicationsAsync(cancellationToken);
        var services = await Each(applications, a => queries.GetServicesAsync(a.Id, cancellationToken), (a, svc) => (App: a, Service: svc));
        var partitions = await Each(services, s => queries.GetPartitionsAsync(s.Service.Id, cancellationToken), (s, p) => (s.App, s.Service, Partition: p));
        var replicas = await Each(partitions, p => queries.GetReplicasAsync(p.Partition.Id, cancellationToken), (p, r) => Row(
            p.App.Id, p.App.Name, p.Service.Id, p.Service.Name, p.Service.Kind, p.Partition, r));
        return Sort(replicas);
    }

    /// <summary>
    /// The replicas of one service: its partitions, then each one's replicas, <see cref="Bounded.Parallelism"/> at a time.
    /// Names come from the ids ("orders~Counter" is fabric:/orders/Counter), so there's nothing else to ask.
    /// </summary>
    public static async Task<IReadOnlyList<ClusterReplicaDto>> LoadServiceAsync(
        ClusterQueries queries, string applicationId, string serviceId, CancellationToken cancellationToken)
    {
        var partitions = await queries.GetPartitionsAsync(serviceId, cancellationToken);
        var perPartition = await Bounded.SelectAsync(partitions, async p =>
        {
            try
            {
                return (Partition: p, Replicas: await queries.GetReplicasAsync(p.Id, cancellationToken));
            }
            // Gone since the service's partitions were listed.
            catch (ServiceFabricException ex) when (ClusterQueries.IsGone(ex))
            {
                return (Partition: p, Replicas: (IReadOnlyList<ReplicaRow>)[]);
            }
        });

        var application = ClusterInspector.NameOf(applicationId);
        var service = ClusterInspector.NameOf(serviceId);
        return Sort(perPartition.SelectMany(p => p.Replicas.Select(r => Row(applicationId, application, serviceId, service, p.Partition.Kind, p.Partition, r))));
    }

    private static ClusterReplicaDto Row(
        string applicationId, string application, string serviceId, string service, string serviceKind, PartitionRow partition, ReplicaRow replica) =>
        new(applicationId, application, serviceId, service, serviceKind,
            partition.Id.ToString(), partition.Status, Dto.Health(partition.Health),
            replica.Id, replica.NodeName, replica.Role, replica.Status, Dto.Health(replica.Health));

    private static IReadOnlyList<ClusterReplicaDto> Sort(IEnumerable<ClusterReplicaDto> replicas) => replicas
        .OrderBy(r => r.Application, StringComparer.Ordinal)
        .ThenBy(r => r.Service, StringComparer.Ordinal)
        .ThenBy(r => r.PartitionId, StringComparer.Ordinal)
        .ThenBy(r => r.Role != nameof(ReplicaRole.Primary))
        .ThenBy(r => r.Role, StringComparer.Ordinal)
        .ThenBy(r => r.Node, StringComparer.Ordinal)
        .ToList();
}
