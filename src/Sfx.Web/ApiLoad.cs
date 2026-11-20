using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Web;

/// <param name="Capacity">Null when unlimited.</param>
public sealed record NodeLoadDto(string Node, double Load, double? Capacity);

/// <param name="ClusterCapacity">Null when unlimited.</param>
/// <param name="Balanced">Whether the resource balancer found the metric balanced on its last run.</param>
public sealed record LoadMetricDto(string Name, double ClusterLoad, double? ClusterCapacity, bool? Balanced, IReadOnlyList<NodeLoadDto> Nodes);

public sealed record LoadDto(IReadOnlyList<LoadMetricDto> Metrics);

/// <summary>GET /api/load: each load metric across the cluster and on every node that's up.</summary>
internal static class ApiLoad
{
    public static void Map(RouteGroupBuilder api) => api.MapGet("/load", (UiSession s, CancellationToken ct) => s.GetLoadAsync(ct));

    public static async Task<LoadDto> LoadAsync(ClusterQueries queries, CancellationToken ct)
    {
        var inspector = new ClusterInspector(queries.Client);
        var cluster = inspector.GetClusterLoadAsync(ct);
        var up = (await queries.GetNodesAsync(ct)).Where(n => n.Status == nameof(NodeStatus.Up)).Select(n => n.Name);
        var nodes = await Bounded.SelectAsync(up, n => inspector.GetNodeLoadAsync(n, ct));

        var perNode = nodes
            .SelectMany(n => (n.NodeLoadMetricInformation ?? []).Select(m => (Metric: m.Name ?? "", Load: new NodeLoadDto(
                n.NodeName?.ToString() ?? "", m.CurrentNodeLoad ?? Api.Number(m.NodeLoad), Api.Capacity(m.NodeCapacity)))))
            .ToLookup(m => m.Metric, m => m.Load);

        // "__NamingServiceReplicaCount__" and the like are the cluster's own bookkeeping, not load anyone set.
        var metrics = ((await cluster).LoadMetricInformation ?? [])
            .Where(m => m.Name is { } name && !name.StartsWith("__", StringComparison.Ordinal))
            .Select(m => new LoadMetricDto(
                m.Name!,
                m.CurrentClusterLoad ?? Api.Number(m.ClusterLoad),
                Api.Capacity(m.ClusterCapacity),
                m.IsBalancedAfter ?? m.IsBalancedBefore,
                perNode[m.Name!].OrderBy(n => n.Node, StringComparer.Ordinal).ToList()))
            .OrderBy(m => m.ClusterCapacity is null)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
        return new LoadDto(metrics);
    }
}
