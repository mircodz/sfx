using Cellar.Layout;
using Cellar.Text;
using Sfx.Core;
using static Sfx.Tui.Styles;

namespace Sfx.Tui;

public sealed partial class Views
{
    private UpgradeQueries? _upgrades;

    private UpgradeQueries UpgradeQueries => _upgrades ??= new UpgradeQueries(queries);

    /// <summary>The cluster's upgrade and every application's, running ones first.</summary>
    /// <param name="clusterName">The context's name, which confirming a cluster upgrade asks to type.</param>
    public IResourceView Upgrades(string clusterName = "cluster") =>
        new ResourceView<UpgradeRow>("upgrades", "upgrades", UpgradeQueries.GetUpgradesAsync, u => u.Kind + u.Name) { Empty = "no upgrades" }
            .HighlightingChanges()
            .Describing(u => DescribeUpgrade(u, clusterName))
            .Acting(UpgradeRowActions(clusterName), host)
            .Column("NAME", Constraint.Fill(2), u => Plain(u.Name), u => u.Name)
            .Column("KIND", Constraint.Length(11), u => Plain(u.Kind), u => u.Kind)
            .Column("VERSION", Constraint.Fill(2), u => Plain(u.From.Length > 0 ? $"{u.From} → {u.Report.Target}" : u.Report.Target), u => u.Report.Target)
            .Column("MODE", Constraint.Length(17), u => Plain(u.Report.Mode), u => u.Report.Mode)
            .Column(
                "PROGRESS",
                Constraint.Length(14),
                u => Progress(u.DomainsDone, u.Report.Domains.Count, UpgradeColor(u.Report.State)),
                u => u.Report.Domains.Count == 0 ? 0 : (double)u.DomainsDone / u.Report.Domains.Count)
            .Column("STATE", Constraint.Length(18), State, u => u.Urgency)
            .Column("STARTED", Constraint.Length(8), u => Plain(Age(u.StartedUtc)), u => u.StartedUtc, Justify.Right)
            .Column("TOOK", Constraint.Length(7), u => Plain(Duration(u.Report.Duration)), u => u.Report.Duration, Justify.Right)
            .Sorting("STATE");

    /// <summary>Once every domain is done, the upgrade still runs its last checks before it completes.</summary>
    private static StyledText State(UpgradeRow upgrade) =>
        upgrade.Report.InProgress && upgrade.Report.Domains.Count > 0 && upgrade.DomainsDone == upgrade.Report.Domains.Count
            ? Colored("finishing", Cellar.Theming.Theme.Current.Warning)
            : UpgradeState(upgrade.Report.State);

    public IResourceView DescribeUpgrade(UpgradeRow upgrade, string clusterName = "cluster") =>
        new DescribeView(
            "upgrade",
            upgrade.Name,
            [
                Tab("Progress", async ct => Lines.Upgrade(upgrade.ApplicationId is { } id
                    ? UpgradeReport.From(await _inspect.GetApplicationUpgradeAsync(id, ct))
                    : UpgradeReport.From(await _inspect.GetClusterUpgradeAsync(ct)))),
                Tab("Raw", async ct => Lines.Json(upgrade.ApplicationId is { } id
                    ? await _inspect.GetApplicationUpgradeAsync(id, ct)
                    : await _inspect.GetClusterUpgradeAsync(ct))),
            ],
            actions: Bind(UpgradeRowActions(clusterName), upgrade, "Upgrade"));
}
