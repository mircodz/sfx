using Sfx.Core;

namespace Sfx.Web;

public sealed partial class UiSession
{
    private Cached<ApiOverview.Overview>? _overview;
    private Cached<IReadOnlyList<UpgradeRow>>? _upgrades;

    /// <summary>Shared by every tab.</summary>
    internal Task<ApiOverview.Overview> GetOverviewAsync(CancellationToken cancellationToken) =>
        LazyInitializer.EnsureInitialized(ref _overview, () => new Cached<ApiOverview.Overview>(
            TimeSpan.FromSeconds(10),
            ct => ApiOverview.LoadAsync(this, ct)))
            .GetAsync(cancellationToken);

    /// <summary>The cluster's and every application's last upgrade; cached because it costs a call per application.</summary>
    internal Task<IReadOnlyList<UpgradeRow>> GetUpgradesAsync(CancellationToken cancellationToken) =>
        LazyInitializer.EnsureInitialized(ref _upgrades, () => new Cached<IReadOnlyList<UpgradeRow>>(
            TimeSpan.FromSeconds(60),
            ct => Upgrades.GetUpgradesAsync(ct)))
            .GetAsync(cancellationToken);

    /// <summary>Called after an action so what it changed isn't served from before it.</summary>
    private void ForgetUpgrades()
    {
        _overview?.Invalidate();
        _upgrades?.Invalidate();
    }
}
