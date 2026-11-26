using Cellar.Widgets;
using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Tui;

/// <summary>
/// Cluster upgrades: <c>u</c> on the cluster starts one to provisioned code and/or config versions, or steers the
/// running one. Clusters Azure manages are left to Azure.
/// </summary>
public sealed partial class Views
{
    private const string Keep = "keep";

    private ClusterUpgrades? _clusterUpgrades;

    private ClusterUpgrades ClusterUpgrades => _clusterUpgrades ??= new ClusterUpgrades(queries.Client);

    /// <summary>What a cluster upgrade is started from: the versions it can go to, and the upgrade before it.</summary>
    internal sealed record ClusterUpgradeStart(ClusterUpgradeOptions Options, UpgradeReport? Last);

    /// <summary><c>u</c> on the cluster: the controls of the running upgrade, or the form to start one.</summary>
    /// <param name="target">What a destructive confirmation asks to type: the context's name.</param>
    private void UpgradeCluster(string target) =>
        host.Load("reading the cluster's versions", async ct =>
        {
            var options = ClusterUpgrades.GetOptionsAsync(ct);
            UpgradeReport? last = null;
            try
            {
                last = UpgradeReport.From(await _inspect.GetClusterUpgradeAsync(ct));
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }
            return new ClusterUpgradeStart(await options, last);
        }, start =>
        {
            if (start.Last is { InProgress: true })
            {
                ClusterUpgradeControls(target);
            }
            else if (start.Options.AzureManaged)
            {
                host.Warn(start.Options.Note ?? ClusterUpgrades.AzureManagedNote);
            }
            else if (!start.Options.CodeVersions.Any(v => v != start.Options.CodeVersion) && !start.Options.ConfigVersions.Any(v => v != start.Options.ConfigVersion))
            {
                host.Warn("no other code or config version is provisioned to upgrade to");
            }
            else
            {
                host.Edit(
                    "Upgrade the cluster",
                    _ => Task.FromResult(ClusterUpgradeFields(start.Options)),
                    fields => ClusterUpgradeOperation(target, start, fields));
            }
        });

    internal static IReadOnlyList<FormField> ClusterUpgradeFields(ClusterUpgradeOptions options) =>
    [
        new FormField("code", "Code version", Keep)
        {
            Kind = FormFieldKind.Choice,
            Choices = [Keep, .. options.CodeVersions.Where(v => v != options.CodeVersion)],
            Note = options.CodeVersion.Length > 0 ? $"now {options.CodeVersion}" : null,
            Hint = "a provisioned Service Fabric version, or keep today's",
        },
        new FormField("config", "Config version", Keep)
        {
            Kind = FormFieldKind.Choice,
            Choices = [Keep, .. options.ConfigVersions.Where(v => v != options.ConfigVersion)],
            Note = options.ConfigVersion.Length > 0 ? $"now {options.ConfigVersion}" : null,
            Hint = "a provisioned cluster configuration version, or keep today's",
        },
        new FormField("plan", "Plan", UpgradePlan.All[0].Title)
        {
            Kind = FormFieldKind.Choice,
            Choices = UpgradePlan.All.Select(p => p.Title).ToList(),
            Hint = string.Join("; ", UpgradePlan.All.Select(p => $"{p.Title.ToLowerInvariant()}: {p.Hint}")),
        },
        .. PolicyFields(cluster: true),
    ];

    internal Operation ClusterUpgradeOperation(string target, ClusterUpgradeStart start, IReadOnlyList<FormField> fields)
    {
        string? Version(string key) => fields.First(f => f.Key == key).Value is { Length: > 0 } value && value != Keep ? value : null;
        var code = Version("code");
        var config = Version("config");
        if (code is null && config is null)
        {
            throw new RefusedException("choose a code version, a config version or both");
        }
        var plan = PlanOf(fields);
        var policy = ParsePolicy(fields, plan, cluster: true);
        var expected = $"code {code ?? start.Options.CodeVersion}, config {config ?? start.Options.ConfigVersion}";
        return new Operation(
            ClusterUpgrades.Describe(code, config, plan, policy is not null),
            target,
            ActionRisk.Destructive,
            (ct, _) => ClusterUpgrades.StartAsync(code, config, plan, policy, ct),
            "Every node restarts, one upgrade domain at a time; :upgrades shows how far it got.")
        {
            Details = Changes(fields),
            Track = async (_, ct) => Tracking.Upgrade(UpgradeReport.From(await _inspect.GetClusterUpgradeAsync(ct)), expected, start.Last?.Started),
            TrackFor = TimeSpan.FromHours(4),
        };
    }

    private void ClusterUpgradeControls(string target) =>
        host.Pick("The cluster is upgrading", _ => Task.FromResult<IReadOnlyList<Choice>>(
            [
                new("Resume into the next domain", "manual upgrades wait for this", ResumeClusterUpgrade),
                new("Change how it proceeds", "monitored, manual, automatic…", ChangeClusterUpgradePlan),
                new("Roll back", "nodes already upgraded go back", () => RollbackClusterUpgrade(target)),
            ]));

    private void ChangeClusterUpgradePlan() =>
        PickPlan("Continue the cluster upgrade as", plan =>
            host.Run(Op(
                $"Continue the cluster upgrade: {plan.Title.ToLowerInvariant()}",
                "cluster",
                ActionRisk.Confirm,
                ct => ClusterUpgrades.ChangePlanAsync(plan, ct))));

    private void ResumeClusterUpgrade() =>
        host.Load("reading the cluster's upgrade", ClusterUpgrades.GetNextDomainAsync, domain =>
        {
            if (domain is null)
            {
                host.Warn("the cluster's upgrade isn't waiting to start a domain");
                return;
            }
            host.Run(Op($"Resume the cluster upgrade into domain {domain}", "cluster", ActionRisk.Confirm, ct => ClusterUpgrades.ResumeAsync(domain, ct)));
        });

    private void RollbackClusterUpgrade(string target) =>
        host.Run(Op(
            "Roll back the cluster upgrade",
            target,
            ActionRisk.Destructive,
            ct => ClusterUpgrades.RollbackAsync(ct),
            "Nodes already upgraded go back to the previous version, one domain at a time.") with
        {
            Track = async (_, ct) => RolledBack(UpgradeReport.From(await _inspect.GetClusterUpgradeAsync(ct))),
            TrackFor = TimeSpan.FromHours(4),
        });

    /// <summary>The cluster's upgrade actions, on its page.</summary>
    private IReadOnlyList<EntityAction<string>> ClusterUpgradeActions() =>
    [
        new("Upgrade the cluster, or steer its running upgrade", ActionRisk.Confirm, UpgradeCluster, [Upgrade], ["upgrade"]),
    ];

    /// <summary>On the upgrades page: <c>u</c> on the cluster's row starts or steers one, on a running application's steers it.</summary>
    private IReadOnlyList<EntityAction<UpgradeRow>> UpgradeRowActions(string clusterName) =>
    [
        new(
            "Upgrade the cluster, or steer its running upgrade",
            ActionRisk.Confirm,
            _ => UpgradeCluster(clusterName),
            [Upgrade],
            ["upgrade"],
            AppliesTo: u => u.ApplicationId is null),

        new(
            "Steer the running upgrade",
            ActionRisk.Confirm,
            u => ApplicationUpgradeControls(u.ApplicationId!, u.Name),
            [Upgrade],
            AppliesTo: u => u.ApplicationId is not null && u.Report.InProgress),

        new(
            "Change how the running upgrade proceeds",
            ActionRisk.Confirm,
            u =>
            {
                if (u.ApplicationId is { } id)
                {
                    ChangeApplicationUpgradePlan(id, u.Name);
                }
                else
                {
                    ChangeClusterUpgradePlan();
                }
            },
            Commands: ["upgrade-mode"],
            AppliesTo: u => u.Report.InProgress),

        new(
            "Resume the upgrade into the next domain",
            ActionRisk.Confirm,
            u =>
            {
                if (u.ApplicationId is { } id)
                {
                    ResumeApplicationUpgrade(id, u.Name);
                }
                else
                {
                    ResumeClusterUpgrade();
                }
            },
            Commands: ["resume"],
            AppliesTo: u => u.Report.InProgress),

        new(
            "Roll back the running upgrade",
            ActionRisk.Confirm,
            u =>
            {
                if (u.ApplicationId is { } id)
                {
                    RollbackApplicationUpgrade(id, u.Name);
                }
                else
                {
                    RollbackClusterUpgrade(clusterName);
                }
            },
            Commands: ["rollback"],
            AppliesTo: u => u.Report.InProgress),
    ];
}
