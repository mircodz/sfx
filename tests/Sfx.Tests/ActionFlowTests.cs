using System.Text;
using Cellar.Terminal;
using Microsoft.ServiceFabric.Common;
using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Tui;

namespace Sfx.Tests;

/// <summary>Records what actions ask the screen to do, instead of showing dialogs.</summary>
internal sealed class FakeHost : IActionHost
{
    public List<Operation> Operations { get; } = [];
    public List<(string Title, IReadOnlyList<Choice> Choices)> Picks { get; } = [];
    public List<(string Label, string Initial, Action<string> Submit)> Asks { get; } = [];

    public void Run(Operation operation) => Operations.Add(operation);

    public void Pick(string title, Func<CancellationToken, Task<IReadOnlyList<Choice>>> load) =>
        Picks.Add((title, load(CancellationToken.None).GetAwaiter().GetResult()));

    public void Ask(string label, string initial, Action<string> submit) => Asks.Add((label, initial, submit));
}

public class EntityActionTests
{
    private readonly FakeHost _host = new();
    private readonly Views _views;

    public EntityActionTests() => _views = new Views(new ClusterQueries(null!), _host);

    private static NodeRow Node(string status) => new("_Node_1", "host", "NodeType0", status, HealthState.Ok, "1", "fd:/1", false, "10.0", null);

    private static ServiceRow Service(string kind) => new("App~Svc", "fabric:/App/Svc", "SvcType", kind, "Active", HealthState.Ok);

    private static readonly PartitionRow Partition = new(PartitionId.Parse(Guid.NewGuid().ToString()), "Stateful", "Singleton", "Ready", HealthState.Ok, "3/2");

    private static IEnumerable<string> Titles<T>(IEnumerable<EntityAction<T>> actions, T subject) =>
        actions.Where(a => a.Applies(subject)).Select(a => a.Title);

    [Fact]
    public void Node_actions_follow_the_node_status()
    {
        var up = Titles(_views.NodeActions(), Node("Up")).ToList();
        Assert.Contains("Restart node", up);
        Assert.Contains("Deactivate node (pause, restart, remove data)", up);
        Assert.DoesNotContain("Activate node", up);
        Assert.DoesNotContain("Remove node state", up);

        var disabled = Titles(_views.NodeActions(), Node("Disabled")).ToList();
        Assert.Contains("Activate node", disabled);
        Assert.DoesNotContain("Restart node", disabled);

        Assert.Contains("Remove node state", Titles(_views.NodeActions(), Node("Down")));
    }

    [Fact]
    public void Stateless_instances_move_and_remove_but_do_not_restart()
    {
        var instance = new ReplicaRow("132", "_Node_1", "Instance", "Ready", HealthState.Ok, "addr");
        var titles = Titles(_views.ReplicaActions(Service("Stateless"), Partition), instance).ToList();

        Assert.DoesNotContain("Restart replica", titles);
        Assert.Contains("Remove replica", titles);
        Assert.Contains("Move replica to another node", titles);
    }

    [Fact]
    public void Removing_a_replica_asks_to_type_its_id()
    {
        var primary = new ReplicaRow("131", "_Node_2", "Primary", "Ready", HealthState.Ok, "addr");
        var remove = _views.ReplicaActions(Service("Stateful"), Partition).Single(a => a.Title == "Remove replica");

        remove.Run(primary);

        var operation = Assert.Single(_host.Operations);
        Assert.Equal(ActionRisk.Destructive, operation.Risk);
        Assert.Equal("131", operation.Target);
        Assert.Equal("Remove primary replica 131 from _Node_2", operation.Title);
    }

    [Fact]
    public void Deactivating_offers_each_intent_and_remove_data_is_destructive()
    {
        var deactivate = _views.NodeActions().Single(a => a.Title.StartsWith("Deactivate", StringComparison.Ordinal));

        deactivate.Run(Node("Up"));
        var (_, choices) = Assert.Single(_host.Picks);
        Assert.Equal(["Pause", "Restart", "Remove data"], choices.Select(c => c.Title));

        choices[2].Pick();
        Assert.Equal(ActionRisk.Destructive, Assert.Single(_host.Operations).Risk);
    }

    [Fact]
    public void System_application_and_its_services_cannot_be_deleted()
    {
        var system = new ApplicationRow(ClusterQueries.SystemApplicationId, "fabric:/System", "System", "", "Ready", HealthState.Ok);
        var user = system with { Id = "App", Name = "fabric:/App" };

        Assert.Empty(Titles(_views.ApplicationActions(), system));
        Assert.Contains("Delete application", Titles(_views.ApplicationActions(), user));
        Assert.DoesNotContain("Delete service", Titles(_views.ServiceActions(system), Service("Stateful")));
    }

    [Fact]
    public void Power_actions_are_marked_and_bound_keys_stay_consistent()
    {
        var replica = _views.ReplicaActions(Service("Stateful"), Partition);
        var node = _views.NodeActions();

        Assert.True(replica.Single(a => a.Title == "Force-remove replica").Power);
        Assert.True(node.Single(a => a.Title == "Remove node state").Power);
        // ctrl+r restarts on every entity that can restart.
        Assert.Equal(KeyChord.Ctrl('r'), replica.Single(a => a.Title == "Restart replica").Keys![0]);
        Assert.Equal(KeyChord.Ctrl('r'), node.Single(a => a.Title == "Restart node").Keys![0]);
    }
}

public class ConfirmDialogTests
{
    private static KeyEvent Key(char c) => new(Cellar.Terminal.Key.Char, new Rune(c), KeyModifiers.None);

    private static readonly KeyEvent Enter = new(Cellar.Terminal.Key.Enter, default, KeyModifiers.None);

    private static Operation Op(ActionRisk risk, params OperationOption[] options) =>
        new("Remove replica 131", "131", risk, (_, _) => Task.CompletedTask, Options: options);

    [Fact]
    public void Plain_confirmation_runs_on_y_with_the_options_toggled_on()
    {
        IReadOnlySet<char>? confirmed = null;
        var dialog = new ConfirmDialog(Op(ActionRisk.Confirm, new OperationOption('i', "ignore constraints")), o => confirmed = o, () => { });

        dialog.OnEvent(Key('i'));
        dialog.OnEvent(Key('y'));

        Assert.Equal(['i'], confirmed!);
    }

    [Fact]
    public void Destructive_confirmation_needs_the_exact_target()
    {
        var confirmed = false;
        var cancelled = false;
        var dialog = new ConfirmDialog(Op(ActionRisk.Destructive), _ => confirmed = true, () => cancelled = true);

        // y is just a character here, not a yes.
        dialog.OnEvent(Key('y'));
        dialog.OnEvent(Enter);
        Assert.False(confirmed);

        dialog.OnEvent(new KeyEvent(Cellar.Terminal.Key.Backspace, default, KeyModifiers.None));
        foreach (var c in "131")
        {
            dialog.OnEvent(Key(c));
        }
        dialog.OnEvent(Enter);
        Assert.True(confirmed);
        Assert.False(cancelled);
    }

    [Fact]
    public void Escape_cancels()
    {
        var cancelled = false;
        var dialog = new ConfirmDialog(Op(ActionRisk.Destructive), _ => { }, () => cancelled = true);

        dialog.OnEvent(new KeyEvent(Cellar.Terminal.Key.Escape, default, KeyModifiers.None));

        Assert.True(cancelled);
    }
}

public class ReportTests
{
    [Fact]
    public void Upgrade_report_reads_domains_and_the_nodes_being_upgraded()
    {
        var progress = new ApplicationUpgradeProgressInfo(
            "fabric:/App", "AppType", "2.0.0",
            [new UpgradeDomainInfo("0", UpgradeDomainState.Completed), new UpgradeDomainInfo("1", UpgradeDomainState.InProgress), new UpgradeDomainInfo("2", UpgradeDomainState.Pending)],
            null, UpgradeState.RollingForwardInProgress, "2", UpgradeMode.Monitored, null, "65000", null, null,
            new CurrentUpgradeDomainProgressInfo("1", [new NodeUpgradeProgressInfo(new NodeName("_Node_1"), NodeUpgradePhase.PreUpgradeSafetyCheck, [], "00:01:00")]),
            null, "2026-11-19T12:00:00Z", null, null, null, null, false, null, null, null);

        var report = UpgradeReport.From(progress);
        var text = string.Join('\n', Lines.Upgrade(report).Select(l => l.PlainText));

        Assert.True(report.InProgress);
        Assert.Equal(TimeSpan.FromSeconds(65), report.Duration);
        Assert.Contains("1  in progress", text);
        Assert.Contains("Upgrading in 1", text);
        Assert.Contains("_Node_1  PreUpgradeSafetyCheck", text);
    }

    [Fact]
    public void Cluster_settings_come_from_the_manifest_fabric_settings()
    {
        const string manifest = """
            <ClusterManifest xmlns="http://schemas.microsoft.com/2011/01/fabric" Name="Dev">
              <FabricSettings>
                <Section Name="Setup"><Parameter Name="FabricDataRoot" Value="C:\SfDevCluster\Data" /></Section>
                <Section Name="Security"><Parameter Name="ClusterCredentialType" Value="None" /><Parameter Name="Secret" Value="x" IsEncrypted="true" /></Section>
              </FabricSettings>
            </ClusterManifest>
            """;

        var sections = ClusterSettings.Parse(manifest);
        var text = string.Join('\n', Lines.Settings(sections).Select(l => l.PlainText));

        Assert.Equal(["Setup", "Security"], sections.Select(s => s.Name));
        Assert.True(sections[1].Parameters[1].IsEncrypted);
        Assert.Contains("ClusterCredentialType  None", text);
        Assert.Contains("Secret                 (encrypted)", text);
    }

    [Fact]
    public void Action_log_appends_one_tab_separated_line_per_action()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sfx-{Guid.NewGuid():N}", "actions.log");
        try
        {
            var log = new ActionLog(path);
            log.Append("local", "http://localhost:19080/", "Restart replica 1", "ok");
            log.Append("local", "http://localhost:19080/", "Remove replica 2", "failed: nope\nreally");

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.EndsWith("\tlocal\thttp://localhost:19080/\tRemove replica 2\tfailed: nope really", lines[1]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}

public class ContextModeTests
{
    [Fact]
    public void Read_only_and_power_round_trip_without_becoming_auth_options()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sfx-{Guid.NewGuid():N}", "config.toml");
        try
        {
            var store = ContextStore.Load(path);
            store.Set(new ClusterContext("prod", "https://prod", ["cert"], new AuthOptions(), ReadOnly: true));
            store.Set(new ClusterContext("dev", "localhost", [], new AuthOptions(), Power: true));
            store.Save();

            var reloaded = ContextStore.Load(path);
            Assert.True(reloaded.Find("prod")!.ReadOnly);
            Assert.True(reloaded.Find("dev")!.Power);
            Assert.Empty(reloaded.Find("prod")!.Options.Keys);
            Assert.DoesNotContain(new ClusterConnector(BuiltInAuth.Providers).Resolve(reloaded.Find("dev")!), p => p.Name != "none");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Command_line_reads_readonly_and_power_flags()
    {
        var options = CommandLine.Parse(["localhost", "--readonly", "--power"], new HashSet<string>());

        Assert.True(options.ReadOnly);
        Assert.True(options.Power);
        Assert.Equal(["localhost"], options.Arguments);
    }
}
