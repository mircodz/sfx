using Microsoft.ServiceFabric.Common;

namespace Sfx.Core;

/// <summary>
/// How an upgrade moves between upgrade domains, and what it does when a health check fails. The same plans serve
/// application and cluster upgrades, starting one or changing one under way.
/// </summary>
/// <param name="Id">A stable name for forms and commands, e.g. "monitored-rollback".</param>
/// <param name="Hint">A few words on when to pick it.</param>
/// <param name="OnFailure">For monitored plans: what a failed health check does; null for unmonitored ones.</param>
public sealed record UpgradePlan(string Id, string Title, string Hint, UpgradeMode Mode, FailureAction? OnFailure)
{
    public static UpgradePlan MonitoredRollback { get; } =
        new("monitored-rollback", "Monitored, roll back on failure", "safest", UpgradeMode.Monitored, FailureAction.Rollback);

    public static UpgradePlan MonitoredManual { get; } =
        new("monitored-manual", "Monitored, stop on failure", "pauses for you", UpgradeMode.Monitored, FailureAction.Manual);

    public static UpgradePlan UnmonitoredAuto { get; } =
        new("unmonitored-auto", "Unmonitored, automatic", "no health checks", UpgradeMode.UnmonitoredAuto, null);

    public static UpgradePlan UnmonitoredManual { get; } =
        new("unmonitored-manual", "Unmonitored, manual", "resume each domain", UpgradeMode.UnmonitoredManual, null);

    /// <summary>Every plan, safest first; the first is the default.</summary>
    public static IReadOnlyList<UpgradePlan> All { get; } = [MonitoredRollback, MonitoredManual, UnmonitoredAuto, UnmonitoredManual];

    public bool Monitored => Mode == UpgradeMode.Monitored;

    /// <summary>The plan with this <see cref="Id"/>; a <see cref="RefusedException"/> otherwise.</summary>
    public static UpgradePlan Find(string id) =>
        All.FirstOrDefault(p => p.Id == id) ?? throw new RefusedException($"Unknown upgrade mode '{id}'.");

    /// <summary>
    /// The plan a running upgrade follows, from the mode and failure action it reports; null when it doesn't say. A
    /// monitored upgrade without a failure action rolls back, as the cluster does.
    /// </summary>
    public static UpgradePlan? Of(UpgradeMode? mode, FailureAction? onFailure) => mode switch
    {
        UpgradeMode.Monitored => onFailure == FailureAction.Manual ? MonitoredManual : MonitoredRollback,
        UpgradeMode.UnmonitoredAuto => UnmonitoredAuto,
        UpgradeMode.UnmonitoredManual => UnmonitoredManual,
        _ => null,
    };

    /// <summary>What a running upgrade is switched to, for <c>UpdateApplicationUpgrade</c> and <c>UpdateClusterUpgrade</c>.</summary>
    internal RollingUpgradeUpdateDescription UpdateDescription() => new(Mode, failureAction: OnFailure);
}
