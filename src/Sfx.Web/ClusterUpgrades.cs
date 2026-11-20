using Microsoft.ServiceFabric.Common;
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

/// <summary>Upgrading the cluster's code or configuration: the options endpoint, and the "cluster.upgrade" action's work.</summary>
internal static class ClusterUpgrades
{
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/cluster/upgrade-options", async (UiSession s, CancellationToken ct) =>
        {
            var version = s.Inspector.GetClusterVersionAsync(ct);
            var upgrade = s.Inspector.GetClusterUpgradeAsync(ct);
            var codes = s.Inspector.GetProvisionedCodeVersionsAsync(ct);
            var configs = s.Inspector.GetProvisionedConfigVersionsAsync(ct);
            var azure = await s.IsAzureManagedAsync(ct);
            var progress = await upgrade;
            return new ClusterUpgradeOptionsDto(
                (await version).Version ?? progress.CodeVersion ?? "",
                progress.ConfigVersion ?? "",
                Newest(await codes),
                Newest(await configs),
                azure,
                azure ? UiSession.AzureManagedNote : null);
        });
    }

    public static string Describe(Args inputs, string modeLabel, UpgradeMode mode)
    {
        var (code, config) = Versions(inputs);
        var to = string.Join(", ", new[] { code is null ? null : $"code {code}", config is null ? null : $"config {config}" }.OfType<string>());
        return $"Upgrade the cluster to {to} ({modeLabel.ToLowerInvariant()})"
            + (UpgradePolicies.Parse(inputs, mode == UpgradeMode.Monitored, cluster: true) is null ? "" : ", custom policy");
    }

    public static async Task RunAsync(UiSession s, Args inputs, UpgradeMode mode, FailureAction? failure, CancellationToken ct)
    {
        var (code, config) = Versions(inputs);
        var policy = UpgradePolicies.Parse(inputs, mode == UpgradeMode.Monitored, cluster: true);

        // The target said it wasn't when the action was offered; check again rather than trust a page left open.
        if (await s.IsAzureManagedAsync(ct))
        {
            throw new ActionException(UiSession.AzureManagedNote, StatusCodes.Status409Conflict);
        }
        if (code is not null && !(await s.Inspector.GetProvisionedCodeVersionsAsync(ct)).Contains(code))
        {
            throw new ActionException($"Code version {code} isn't provisioned in this cluster.", StatusCodes.Status409Conflict);
        }
        if (config is not null && !(await s.Inspector.GetProvisionedConfigVersionsAsync(ct)).Contains(config))
        {
            throw new ActionException($"Config version {config} isn't provisioned in this cluster.", StatusCodes.Status409Conflict);
        }

        await s.Operations.UpgradeClusterAsync(code, config, mode, failure, policy, ct);
    }

    private static (string? Code, string? Config) Versions(Args inputs)
    {
        var code = inputs.Optional("codeVersion");
        var config = inputs.Optional("configVersion");
        return code is null && config is null
            ? throw new ActionException("Choose a code version, a config version or both.")
            : (code, config);
    }

    private static List<string> Newest(IEnumerable<string> versions) =>
        versions.Distinct().OrderDescending(VersionComparer.Instance).ToList();
}
