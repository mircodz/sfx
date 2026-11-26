using Microsoft.ServiceFabric.Client;
using Microsoft.ServiceFabric.Common;

namespace Sfx.Core;

/// <summary>What a cluster upgrade can move to. Versions are newest first.</summary>
/// <param name="CodeVersion">The Service Fabric version the cluster runs; empty when it doesn't say.</param>
/// <param name="ConfigVersion">The cluster configuration version it runs; empty when neither its upgrade nor its manifest says.</param>
/// <param name="AzureManaged">Azure's resource provider owns this cluster's upgrades; sfx doesn't start one then.</param>
/// <param name="Note">Why there's no upgrade here, when there isn't.</param>
public sealed record ClusterUpgradeOptions(
    string CodeVersion,
    string ConfigVersion,
    IReadOnlyList<string> CodeVersions,
    IReadOnlyList<string> ConfigVersions,
    bool AzureManaged,
    string? Note);

/// <summary>The Service Fabric code and cluster configuration versions provisioned, ready to upgrade to; newest first.</summary>
public sealed record ProvisionedVersions(IReadOnlyList<string> Code, IReadOnlyList<string> Config);

/// <summary>
/// Cluster (Service Fabric) upgrades: what's provisioned, starting one with the checks the cluster wouldn't explain, and
/// steering one under way. Clusters Azure manages are refused: their resource provider owns their upgrades.
/// </summary>
public sealed class ClusterUpgrades(IServiceFabricClient client)
{
    /// <summary>What Azure says to anyone upgrading a cluster it manages.</summary>
    public const string AzureManagedNote =
        "This cluster is managed by Azure: upgrade it from the Azure portal or with ARM, so it doesn't fight the resource provider.";

    private readonly ClusterInspector _inspector = new(client);
    private readonly ClusterOperations _operations = new(client);
    private Task<bool>? _azureManaged;

    /// <summary>
    /// Whether Azure's Service Fabric resource provider manages this cluster (see <see cref="ClusterKind.IsAzureManaged"/>);
    /// read from the cluster manifest once per instance. A failed read isn't kept, so the next call asks again.
    /// </summary>
    public Task<bool> IsAzureManagedAsync(CancellationToken cancellationToken)
    {
        var check = _azureManaged ??= LoadAzureManagedAsync();
        if (check.IsFaulted || check.IsCanceled)
        {
            _azureManaged = null;
        }
        return check.WaitAsync(cancellationToken);
    }

    private async Task<bool> LoadAzureManagedAsync() =>
        ClusterKind.IsAzureManaged(await _inspector.GetClusterManifestAsync(CancellationToken.None));

    /// <summary>The versions the cluster runs and those it can move to, and whether sfx may upgrade it at all.</summary>
    public async Task<ClusterUpgradeOptions> GetOptionsAsync(CancellationToken cancellationToken)
    {
        var version = _inspector.GetClusterVersionAsync(cancellationToken);
        var upgrade = ProgressAsync(cancellationToken);
        var provisioned = GetProvisionedVersionsAsync(cancellationToken);
        var azure = await IsAzureManagedAsync(cancellationToken);
        var progress = await upgrade;
        var versions = await provisioned;
        return new ClusterUpgradeOptions(
            (await version).Version ?? progress?.CodeVersion ?? "",
            await CurrentConfigVersionAsync(progress, cancellationToken),
            versions.Code,
            versions.Config,
            azure,
            azure ? AzureManagedNote : null);
    }

    public async Task<ProvisionedVersions> GetProvisionedVersionsAsync(CancellationToken cancellationToken)
    {
        var code = _inspector.GetProvisionedCodeVersionsAsync(cancellationToken);
        var config = _inspector.GetProvisionedConfigVersionsAsync(cancellationToken);
        return new ProvisionedVersions(Newest(await code), Newest(await config));
    }

    /// <summary>The cluster's last upgrade, or null when it can't say (as with no upgrade yet on some versions).</summary>
    private async Task<ClusterUpgradeProgressObject?> ProgressAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inspector.GetClusterUpgradeAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// The config version the cluster runs. Before its first upgrade there's no upgrade to report one, so it's read from
    /// the cluster manifest instead; empty only when neither says.
    /// </summary>
    private async Task<string> CurrentConfigVersionAsync(ClusterUpgradeProgressObject? progress, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(progress?.ConfigVersion))
        {
            return progress.ConfigVersion;
        }
        try
        {
            return ClusterSettings.ConfigVersion(await _inspector.GetClusterManifestAsync(cancellationToken)) ?? "";
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return "";
        }
    }

    /// <summary>"Upgrade the cluster to code 10.1, config 2.0 (monitored, roll back on failure)", for confirmations and the action log.</summary>
    public static string Describe(string? codeVersion, string? configVersion, UpgradePlan plan, bool customPolicy)
    {
        var (code, config) = Versions(codeVersion, configVersion);
        var to = string.Join(", ", new[] { code is null ? null : $"code {code}", config is null ? null : $"config {config}" }.OfType<string>());
        return $"Upgrade the cluster to {to} ({plan.Title.ToLowerInvariant()})" + (customPolicy ? ", custom policy" : "");
    }

    /// <summary>
    /// Upgrades the cluster's code, configuration or both to versions already provisioned. Refuses, before asking the
    /// cluster to upgrade, when neither version is given, when Azure manages the cluster, when a version isn't
    /// provisioned, or when the policy doesn't fit the plan.
    /// </summary>
    /// <param name="codeVersion">The Service Fabric version to move to, or null (or empty) to keep today's.</param>
    /// <param name="configVersion">The cluster configuration version to move to, or null (or empty) to keep today's.</param>
    /// <param name="policy">
    /// Timeouts and health limits; null, or null fields, leave the cluster's defaults. Health limits left out keep the
    /// cluster manifest's values.
    /// </param>
    public async Task StartAsync(string? codeVersion, string? configVersion, UpgradePlan plan, UpgradePolicy? policy, CancellationToken cancellationToken)
    {
        var (code, config) = Versions(codeVersion, configVersion);
        policy?.Require(plan.Monitored, cluster: true);

        // Checked again rather than trusting whatever offered the upgrade, which may have been on screen for a while.
        if (await IsAzureManagedAsync(cancellationToken))
        {
            throw new RefusedException(AzureManagedNote, conflict: true);
        }
        if (code is not null && !(await _inspector.GetProvisionedCodeVersionsAsync(cancellationToken)).Contains(code))
        {
            throw new RefusedException($"Code version {code} isn't provisioned in this cluster.", conflict: true);
        }
        if (config is not null && !(await _inspector.GetProvisionedConfigVersionsAsync(cancellationToken)).Contains(config))
        {
            throw new RefusedException($"Config version {config} isn't provisioned in this cluster.", conflict: true);
        }

        // Health policy fields left out keep the cluster's current values, which only its manifest has.
        var manifest = policy is { HasClusterHealthPolicy: true } ? await _inspector.GetClusterManifestAsync(cancellationToken) : null;
        await _operations.UpgradeClusterAsync(code, config, plan.Mode, plan.OnFailure, policy, manifest, cancellationToken);
    }

    /// <summary>Switches the running upgrade to another plan, e.g. from manual to automatic.</summary>
    public Task ChangePlanAsync(UpgradePlan plan, CancellationToken cancellationToken) =>
        _operations.ChangeClusterUpgradePlanAsync(plan, cancellationToken);

    /// <summary>The upgrade domain a manual upgrade waits to start next; null when it isn't waiting for one.</summary>
    public async Task<string?> GetNextDomainAsync(CancellationToken cancellationToken) =>
        NextDomain(UpgradeReport.From(await _inspector.GetClusterUpgradeAsync(cancellationToken)));

    /// <summary>Lets a manual upgrade move on into <paramref name="upgradeDomain"/>.</summary>
    public Task ResumeAsync(string upgradeDomain, CancellationToken cancellationToken) =>
        _operations.ResumeClusterUpgradeAsync(upgradeDomain, cancellationToken);

    /// <summary>Lets a manual upgrade move on into the domain it waits for; returns that domain.</summary>
    public async Task<string> ResumeNextAsync(CancellationToken cancellationToken)
    {
        var domain = await GetNextDomainAsync(cancellationToken)
            ?? throw new RefusedException("The cluster's upgrade isn't waiting to start an upgrade domain.", conflict: true);
        await ResumeAsync(domain, cancellationToken);
        return domain;
    }

    public Task RollbackAsync(CancellationToken cancellationToken) => _operations.RollbackClusterUpgradeAsync(cancellationToken);

    /// <summary>
    /// Registers a Service Fabric code package and/or cluster manifest already copied to the image store, at their
    /// paths relative to it, so the cluster can upgrade to them.
    /// </summary>
    public async Task ProvisionAsync(string? codePath, string? clusterManifestPath, CancellationToken cancellationToken)
    {
        if (Blank(codePath) is null && Blank(clusterManifestPath) is null)
        {
            throw new RefusedException("Give the image store path of a code package, a cluster manifest or both.");
        }
        if (await IsAzureManagedAsync(cancellationToken))
        {
            throw new RefusedException(AzureManagedNote, conflict: true);
        }
        await client.Cluster.ProvisionClusterAsync(
            new ProvisionFabricDescription(Blank(codePath), Blank(clusterManifestPath)),
            cancellationToken: cancellationToken);
    }

    /// <summary>Removes provisioned code and/or config versions the cluster no longer runs.</summary>
    public async Task UnprovisionAsync(string? codeVersion, string? configVersion, CancellationToken cancellationToken)
    {
        if (Blank(codeVersion) is null && Blank(configVersion) is null)
        {
            throw new RefusedException("Choose a code version, a config version or both.");
        }
        if (await IsAzureManagedAsync(cancellationToken))
        {
            throw new RefusedException(AzureManagedNote, conflict: true);
        }
        await client.Cluster.UnprovisionClusterAsync(
            new UnprovisionFabricDescription(Blank(codeVersion), Blank(configVersion)),
            cancellationToken: cancellationToken);
    }

    /// <summary>The domain an upgrade waits to start: only a pending upgrade (manual, or stopped on failure) waits.</summary>
    internal static string? NextDomain(UpgradeReport report) =>
        report.State == nameof(UpgradeState.RollingForwardPending) && report.NextDomain.Length > 0 ? report.NextDomain : null;

    private static (string? Code, string? Config) Versions(string? codeVersion, string? configVersion)
    {
        var (code, config) = (Blank(codeVersion), Blank(configVersion));
        return code is null && config is null
            ? throw new RefusedException("Choose a code version, a config version or both.")
            : (code, config);
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static List<string> Newest(IEnumerable<string> versions) =>
        versions.Distinct().OrderDescending(VersionComparer.Instance).ToList();
}
