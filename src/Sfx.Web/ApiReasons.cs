namespace Sfx.Web;

/// <summary>
/// Adds <c>reason</c> to the unhealthy rows of the node, application, service and partition lists. It's a filter over
/// the /api group rather than part of each endpoint: it rewrites whatever those endpoints return, by type.
/// </summary>
internal static class ApiReasons
{
    public static void Map(RouteGroupBuilder api) => api.AddEndpointFilter(async (context, next) =>
    {
        var result = await next(context);
        // The session the handler used, so a context switch in between can't mix two clusters.
        if (context.Arguments.OfType<UiSession>().FirstOrDefault() is not { } s)
        {
            return result;
        }
        var ct = context.HttpContext.RequestAborted;
        return result switch
        {
            // GET /nodes
            IReadOnlyList<NodeDto> nodes => await WithReasonsAsync(s, "node", nodes, n => n.Name, n => n.Health, (n, r) => n with { Reason = r }, ct),
            // GET /applications
            IReadOnlyList<ApplicationDto> apps => await WithReasonsAsync(s, "application", apps, a => a.Id, a => a.Health, (a, r) => a with { Reason = r }, ct),
            // GET /applications/{appId}
            ApplicationDetailDto app => app with
            {
                Services = await WithReasonsAsync(s, "service", app.Services, v => v.Id, v => v.Health, (v, r) => v with { Reason = r }, ct),
            },
            // GET /applications/{appId}/services/{serviceId}
            ServiceDetailDto service => service with
            {
                Partitions = await WithReasonsAsync(s, "partition", service.Partitions, p => p.Id, p => p.Health, (p, r) => p with { Reason = r }, ct),
            },
            _ => result,
        };
    });

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
