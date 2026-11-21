using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Web;

/// <summary>
/// GET /api/overview: the cluster at a glance, in a handful of calls however large it is: health with counts per entity
/// kind (no per-entity states), load per metric, and only the upgrades under way. With <c>?view=</c>, health and counts
/// stay cluster-wide; the upgrades are those in the view, and <c>view</c> says how many applications it hides.
/// </summary>
internal static class ApiOverview
{
    public static void Map(RouteGroupBuilder api) => api.MapGet("/overview", async (UiSession s, string? view, CancellationToken ct) =>
    {
        var filter = s.View(view);
        var overview = s.GetOverviewAsync(ct);
        var summary = filter.IsNone ? null : Summary(filter, await s.GetApplicationListAsync(ct));
        var o = await overview;
        // Actions depend on the session's power and read-only setting, so they're added per request rather than cached.
        return new OverviewDto(
            s.Context.Name, s.Cluster.Endpoint.ToString(), o.CodeVersion, o.Health, o.Counts, o.Load,
            o.RunningUpgrades.Where(u => u.InView(filter)).Select(u => Dto.Upgrade(u, Api.UpgradeActions(s, u))).ToList(),
            summary);
    });

    /// <summary>How many applications are in the view, and how many it hides, unhealthy ones apart.</summary>
    internal static ViewSummaryDto Summary(ViewFilter view, IReadOnlyList<ApplicationRow> applications)
    {
        var hidden = applications.Where(a => !view.Includes(a.Name)).ToList();
        return new ViewSummaryDto(
            view.Name!,
            applications.Count - hidden.Count,
            hidden.Count,
            hidden.Count(a => a.Health is HealthState.Warning or HealthState.Error));
    }

    /// <summary>What the overview shows, before actions are added.</summary>
    internal sealed record Overview(
        string CodeVersion, HealthDto Health, IReadOnlyList<HealthCountDto> Counts, IReadOnlyList<LoadSummaryDto> Load, IReadOnlyList<UpgradeRow> RunningUpgrades);

    public static async Task<Overview> LoadAsync(UiSession s, CancellationToken ct)
    {
        var version = s.Inspector.GetClusterVersionAsync(ct);
        var health = s.Inspector.GetClusterHealthSummaryAsync(ct);
        var load = s.Inspector.GetClusterLoadAsync(ct);
        var upgrades = s.Upgrades.GetRunningUpgradesAsync(ct);

        var h = await health;
        var summaries = await ApiLoad.SummariesAsync(s, await load, null, ct);
        return new Overview(
            (await version).Version ?? "",
            Dto.Health(HealthReport.From(h)),
            Dto.HealthCounts(h.HealthStatistics),
            summaries,
            (await upgrades).OrderBy(u => u.Urgency).ThenByDescending(u => u.StartedUtc).ToList());
    }
}
