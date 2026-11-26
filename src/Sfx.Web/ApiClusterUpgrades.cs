using Sfx.Core;

namespace Sfx.Web;

/// <summary>What a cluster upgrade can move to. Versions are newest first.</summary>
/// <param name="AzureManaged">Azure's resource provider owns this cluster's upgrades; sfx doesn't offer one then.</param>
/// <param name="Note">Why there's no upgrade here, when there isn't.</param>
public sealed record ClusterUpgradeOptionsDto(
    string CodeVersion,
    string ConfigVersion,
    IReadOnlyList<string> CodeVersions,
    IReadOnlyList<string> ConfigVersions,
    bool AzureManaged,
    string? Note);

/// <summary>The upgrade itself is the "cluster.upgrade" action.</summary>
internal static class ApiClusterUpgrades
{
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/cluster/upgrade-options", async (UiSession s, CancellationToken ct) =>
        {
            var o = await s.ClusterUpgrades.GetOptionsAsync(ct);
            return new ClusterUpgradeOptionsDto(o.CodeVersion, o.ConfigVersion, o.CodeVersions, o.ConfigVersions, o.AzureManaged, o.Note);
        });
    }
}
