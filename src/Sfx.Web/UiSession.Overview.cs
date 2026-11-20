using Sfx.Core;

namespace Sfx.Web;

public sealed partial class UiSession
{
    private Cached<ApiOverview.Overview>? _overview;
    private Cached<IReadOnlyList<UpgradeRow>>? _upgrades;

    /// <summary>The overview, asked at most once per 10 seconds, shared by every tab.</summary>
    internal Task<ApiOverview.Overview> GetOverviewAsync(CancellationToken cancellationToken) =>
        LazyInitializer.EnsureInitialized(ref _overview, () => new Cached<ApiOverview.Overview>(
                TimeSpan.FromSeconds(10), ct => ApiOverview.LoadAsync(this, ct)))
            .GetAsync(cancellationToken);

    /// <summary>The cluster's and every application's last upgrade (a call per application), asked at most once a minute.</summary>
    internal Task<IReadOnlyList<UpgradeRow>> GetUpgradesAsync(CancellationToken cancellationToken) =>
        LazyInitializer.EnsureInitialized(ref _upgrades, () => new Cached<IReadOnlyList<UpgradeRow>>(
                TimeSpan.FromSeconds(60), ct => Upgrades.GetUpgradesAsync(ct)))
            .GetAsync(cancellationToken);

    /// <summary>After an action, what it may have changed is asked again rather than served from before it.</summary>
    private void ForgetUpgrades()
    {
        _overview?.Invalidate();
        _upgrades?.Invalidate();
    }
}
