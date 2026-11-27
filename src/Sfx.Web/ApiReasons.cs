namespace Sfx.Web;

/// <summary>Adds <c>reason</c> to the unhealthy rows of node, application, service and partition lists.</summary>
internal static class ApiReasons
{
    public static Task<IReadOnlyList<NodeDto>> NodesAsync(UiSession s, IReadOnlyList<NodeDto> nodes, CancellationToken ct) =>
        WithReasonsAsync(s, "node", nodes, n => n.Name, n => n.Health, (n, r) => n with { Reason = r }, ct);

    public static Task<IReadOnlyList<ApplicationDto>> ApplicationsAsync(UiSession s, IReadOnlyList<ApplicationDto> apps, CancellationToken ct) =>
        WithReasonsAsync(s, "application", apps, a => a.Id, a => a.Health, (a, r) => a with { Reason = r }, ct);

    public static Task<IReadOnlyList<ServiceDto>> ServicesAsync(UiSession s, IReadOnlyList<ServiceDto> services, CancellationToken ct) =>
        WithReasonsAsync(s, "service", services, v => v.Id, v => v.Health, (v, r) => v with { Reason = r }, ct);

    public static Task<IReadOnlyList<PartitionDto>> PartitionsAsync(UiSession s, IReadOnlyList<PartitionDto> partitions, CancellationToken ct) =>
        WithReasonsAsync(s, "partition", partitions, p => p.Id, p => p.Health, (p, r) => p with { Reason = r }, ct);

    private static async Task<IReadOnlyList<T>> WithReasonsAsync<T>(
        UiSession s,
        string kind,
        IReadOnlyList<T> rows,
        Func<T, string> id,
        Func<T, string> health,
        Func<T, string, T> withReason,
        CancellationToken cancellationToken)
    {
        var unhealthy = rows.Where(r => health(r) is "Warning" or "Error").Select(r => (id(r), health(r))).ToList();
        if (unhealthy.Count == 0)
        {
            return rows;
        }
        var reasons = await s.GetReasonsAsync(kind, unhealthy, cancellationToken);
        return rows.Select(r => health(r) is "Warning" or "Error" && reasons.TryGetValue(id(r), out var reason) ? withReason(r, reason) : r).ToList();
    }
}
