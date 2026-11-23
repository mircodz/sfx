using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Web;

/// <param name="Capacity">Null when unlimited.</param>
public sealed record NodeLoadDto(string Node, double Load, double? Capacity);

/// <param name="ClusterCapacity">Null when unlimited.</param>
/// <param name="Balanced">Whether the resource balancer found the metric balanced on its last run.</param>
/// <param name="Nodes">Each node's load; empty unless asked for with <c>?nodes=true</c>.</param>
/// <param name="MinNode">
/// The name of the node with the least load (its id when the node isn't known); null when the cluster doesn't say, or
/// when no node has any of this load.
/// </param>
/// <param name="MaxNode">The name of the node with the most load, likewise.</param>
public sealed record LoadMetricDto(
    string Name, double ClusterLoad, double? ClusterCapacity, bool? Balanced, IReadOnlyList<NodeLoadDto> Nodes,
    string? MinNode = null, double? MinLoad = null, string? MaxNode = null, double? MaxLoad = null);

public sealed record LoadDto(IReadOnlyList<LoadMetricDto> Metrics);

/// <summary>
/// GET /api/load: each load metric across the cluster, from one call. With <c>?nodes=true</c> also its load on every node
/// that may host replicas (up, enabling, disabling or disabled), which is a call per node.
/// </summary>
internal static class ApiLoad
{
    public static void Map(RouteGroupBuilder api) =>
        api.MapGet("/load", (UiSession s, bool? nodes, CancellationToken ct) => nodes is true ? s.GetNodeLoadAsync(ct) : s.GetLoadAsync(ct));

    /// <summary>The cluster's load metrics, with users' metrics only ("__" ones are the cluster's), those with a capacity first.</summary>
    /// <param name="capacities">What the manifest declares, so a total capacity of 0 counts when every node type declares it.</param>
    /// <param name="nodeNames">Node names by id, for the least and most loaded nodes; an id not in it is shown as is.</param>
    public static IReadOnlyList<LoadSummaryDto> Summaries(
        ClusterLoadInfo load, NodeCapacities? capacities = null, IReadOnlyDictionary<string, string>? nodeNames = null) =>
        UserMetrics(load)
            .Select(m =>
            {
                var min = m.MinimumNodeLoad ?? OptionalNumber(m.MinNodeLoadValue);
                var max = m.MaximumNodeLoad ?? OptionalNumber(m.MaxNodeLoadValue);
                // With no load anywhere, the cluster still names some node as least and most loaded; that says nothing.
                var idle = max == 0;
                string? Node(NodeId? id) => idle || NodeId(id) is not { } nodeId ? null : nodeNames?.GetValueOrDefault(nodeId) ?? nodeId;
                return new LoadSummaryDto(
                    m.Name!,
                    m.CurrentClusterLoad ?? Api.Number(m.ClusterLoad),
                    Api.Capacity(m.ClusterCapacity, (capacities ?? NodeCapacities.None).DeclaredEverywhere(m.Name!)),
                    m.IsBalancedAfter ?? m.IsBalancedBefore,
                    Node(m.MinNodeLoadNodeId),
                    min,
                    Node(m.MaxNodeLoadNodeId),
                    max);
            })
            .OrderBy(m => m.ClusterCapacity is null)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// <see cref="Summaries"/> with what it needs from the session: declared capacities, and the names of the least and
    /// most loaded nodes, from the session's node names (no call when they're known) unless <paramref name="nodes"/> has them.
    /// </summary>
    public static async Task<IReadOnlyList<LoadSummaryDto>> SummariesAsync(
        UiSession s, ClusterLoadInfo load, IReadOnlyList<NodeRow>? nodes, CancellationToken ct)
    {
        var capacities = s.GetDeclaredCapacitiesAsync(ct);
        var names = nodes is not null
            ? NamesById(nodes)
            : await s.GetNodeNamesAsync(
                UserMetrics(load).SelectMany(m => new[] { NodeId(m.MinNodeLoadNodeId), NodeId(m.MaxNodeLoadNodeId) }).OfType<string>(), ct);
        return Summaries(load, await capacities, names);
    }

    public static IReadOnlyDictionary<string, string> NamesById(IEnumerable<NodeRow> nodes) =>
        nodes.Where(n => n.Id.Length > 0).DistinctBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(n => n.Id, n => n.Name, StringComparer.OrdinalIgnoreCase);

    // "__NamingServiceReplicaCount__" and the like are the cluster's own bookkeeping, not load anyone set.
    private static IEnumerable<LoadMetricInformation> UserMetrics(ClusterLoadInfo load) =>
        (load.LoadMetricInformation ?? []).Where(m => m.Name is { } name && !name.StartsWith("__", StringComparison.Ordinal));

    public static async Task<LoadDto> LoadAsync(UiSession s, bool includeNodes, CancellationToken ct)
    {
        var cluster = s.Inspector.GetClusterLoadAsync(ct);
        var capacities = await s.GetDeclaredCapacitiesAsync(ct);
        var perNode = Enumerable.Empty<(string Metric, NodeLoadDto Load)>().ToLookup(m => m.Metric, m => m.Load);
        IReadOnlyList<NodeRow>? nodes = null;
        if (includeNodes)
        {
            nodes = await s.Queries.GetNodesAsync(ct);
            var hosts = nodes.Where(n => ClusterQueries.MayHostReplicas(n.Status)).ToList();
            var loads = await Bounded.SelectAsync(hosts, async n =>
            {
                try
                {
                    return (Node: n, Load: (NodeLoadInfo?)await s.Inspector.GetNodeLoadAsync(n.Name, ct));
                }
                // A disabled or disabling node may not answer; it's left out rather than failing every node's.
                catch (Exception) when (n.Status != nameof(NodeStatus.Up) && !ct.IsCancellationRequested)
                {
                    return (Node: n, Load: null);
                }
            });
            perNode = loads
                .SelectMany(n => (n.Load?.NodeLoadMetricInformation ?? []).Select(m => (Metric: m.Name ?? "", Load: new NodeLoadDto(
                    n.Node.Name, m.CurrentNodeLoad ?? Api.Number(m.NodeLoad),
                    Api.Capacity(m.NodeCapacity, capacities.Declares(n.Node.NodeType, m.Name ?? ""))))))
                .ToLookup(m => m.Metric, m => m.Load);
        }

        return new LoadDto((await SummariesAsync(s, await cluster, nodes, ct))
            .Select(m => new LoadMetricDto(
                m.Name, m.ClusterLoad, m.ClusterCapacity, m.Balanced,
                perNode[m.Name].OrderBy(n => n.Node, StringComparer.Ordinal).ToList(),
                m.MinNode, m.MinLoad, m.MaxNode, m.MaxLoad))
            .ToList());
    }

    private static string? NodeId(NodeId? id) => string.IsNullOrEmpty(id?.Id) ? null : id.Id;

    private static double? OptionalNumber(string? value) => string.IsNullOrWhiteSpace(value) ? null : Api.Number(value);
}
