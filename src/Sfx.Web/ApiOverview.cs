using Sfx.Core;

namespace Sfx.Web;

/// <summary>
/// A fixed handful of calls however large the cluster: health counts per entity kind (no per-entity states), load per
/// metric, and only running upgrades.
/// </summary>
internal static class ApiOverview
{
    public static void Map(RouteGroupBuilder api) => api.MapGet("/overview", async (UiSession s, CancellationToken ct) =>
    {
        var o = await s.GetOverviewAsync(ct);
        // Actions depend on the session's power and read-only setting, so they're added per request, not cached.
        return new OverviewDto(
            s.Context.Name,
            s.Cluster.Endpoint.ToString(),
            o.CodeVersion,
            o.Health,
            o.Counts,
            o.Load,
            o.RunningUpgrades.Select(u => Dto.Upgrade(u, Api.UpgradeActions(s, u))).ToList());
    });

    /// <summary>The cached overview, without actions.</summary>
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
