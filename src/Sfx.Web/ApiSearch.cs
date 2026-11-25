using Sfx.Core;

namespace Sfx.Web;

/// <summary>A search result, with the ids the browser routes by.</summary>
/// <param name="Kind">node, application, service or partition.</param>
/// <param name="Sub">A node's type and address, an application's type and version, a service's type, or a partition's service.</param>
/// <param name="Node">A node's name.</param>
/// <param name="ApplicationId">The application's id (e.g. "orders" or "shop~orders"), for an application, service or partition.</param>
/// <param name="ServiceId">The service's id (e.g. "orders~Counter"), for a service or partition.</param>
public sealed record SearchHitDto(
    string Kind,
    string Title,
    string? Sub,
    string? Node = null,
    string? ApplicationId = null,
    string? ServiceId = null,
    string? PartitionId = null);

/// <summary>GET /api/search?q=: nodes, applications and services by name, type or address; a partition by its id.</summary>
internal static class ApiSearch
{
    public const int Limit = 20;

    public static void Map(RouteGroupBuilder api) =>
        api.MapGet("/search", (UiSession s, string? q, CancellationToken ct) => SearchAsync(
            q,
            ct => s.GetSearchIndexAsync(ct),
            (id, ct) => s.Overview.FindPartitionAsync(id, ct),
            ct));

    /// <summary>A partition id is looked up directly (one hit or none); other text searches the index, best first.</summary>
    internal static async Task<IReadOnlyList<SearchHitDto>> SearchAsync(
        string? query,
        Func<CancellationToken, Task<IReadOnlyList<SearchHit>>> index,
        Func<Guid, CancellationToken, Task<SearchHit?>> findPartition,
        CancellationToken ct)
    {
        var text = query?.Trim() ?? "";
        if (text.Length == 0)
        {
            return [];
        }
        if (Guid.TryParse(text, out var id))
        {
            return await findPartition(id, ct) is { } partition ? [Hit(partition)] : [];
        }
        return ClusterOverview.Search(await index(ct), text, Limit).Select(Hit).ToList();
    }

    internal static SearchHitDto Hit(SearchHit hit) => hit.Kind switch
    {
        "node" => new(hit.Kind, hit.Title, Join(hit.Node?.NodeType, hit.Node?.Address), Node: hit.Node?.Name ?? hit.Title),
        "application" => new(hit.Kind, hit.Title, Join(hit.Application?.TypeName, hit.Application?.TypeVersion, " "), ApplicationId: hit.Application?.Id),
        "service" => new(
            hit.Kind,
            hit.Title,
            hit.Service?.TypeName is { Length: > 0 } type ? type : null,
            ApplicationId: hit.Application?.Id,
            ServiceId: hit.Service?.Id),
        _ => new(
            hit.Kind,
            hit.Title,
            hit.Service?.Name,
            ApplicationId: hit.Application?.Id,
            ServiceId: hit.Service?.Id,
            PartitionId: hit.Partition?.Id.ToString() ?? hit.Title),
    };

    private static string? Join(string? a, string? b, string separator = ", ")
    {
        var parts = new[] { a, b }.Where(p => !string.IsNullOrEmpty(p)).ToList();
        return parts.Count == 0 ? null : string.Join(separator, parts);
    }
}
