using Sfx.Core;

namespace Sfx.Web;

/// <summary>
/// GET /api/overview: the cluster at a glance, in a handful of calls however large it is: health with counts per entity
/// kind (no per-entity states), load per metric, and only the upgrades under way.
/// </summary>
internal static class ApiOverview
{
    public static void Map(RouteGroupBuilder api) => api.MapGet("/overview", async (UiSession s, CancellationToken ct) =>
    {
        var o = await s.GetOverviewAsync(ct);
        // Actions depend on the session's power and read-only setting, so they're added per request rather than cached.
        return new OverviewDto(
            s.Context.Name,
            s.Cluster.Endpoint.ToString(),
            o.CodeVersion,
            o.Health,
            o.Counts,
            o.Load,
            o.RunningUpgrades.Select(u => Dto.Upgrade(u, Api.UpgradeActions(s, u))).ToList());
    });

    /// <summary>What the overview shows, before actions are added.</summary>
    internal sealed record Overview(
        string CodeVersion,
        HealthDto Health,
        IReadOnlyList<HealthCountDto> Counts,
        IReadOnlyList<LoadSummaryDto> Load,
        IReadOnlyList<UpgradeRow> RunningUpgrades);

    public static async Task<Overview> LoadAsync(UiSession s, CancellationToken ct)
    {
        var summary = s.Overview.GetSummaryAsync(ct);
        var load = s.Inspector.GetClusterLoadAsync(ct);
        var upgrades = s.Upgrades.GetRunningUpgradesAsync(ct);

        var h = await summary;
        var summaries = await ApiLoad.SummariesAsync(s, await load, null, ct);
        return new Overview(
            h.CodeVersion,
            Dto.Health(h.Health),
            Dto.HealthCounts(h.Counts),
            summaries,
            (await upgrades).OrderBy(u => u.Urgency).ThenByDescending(u => u.StartedUtc).ToList());
    }
}
