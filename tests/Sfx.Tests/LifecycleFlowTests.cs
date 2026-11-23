using System.Text;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Terminal;
using Cellar.Widgets;
using Microsoft.ServiceFabric.Common;
using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Tui;
using static Sfx.Tui.Styles;

namespace Sfx.Tests;

/// <summary><c>:provision</c> and <c>:upload</c> complete paths on this machine: folders and packages.</summary>
public sealed class PathCompletionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sfx-paths-").FullName;

    public PathCompletionTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "pkgs", "VotingPkg"));
        Directory.CreateDirectory(Path.Combine(_dir, ".hidden"));
        File.WriteAllText(Path.Combine(_dir, "app.sfpkg"), "");
        File.WriteAllText(Path.Combine(_dir, "Build.ZIP"), "");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Folders_end_in_a_slash_and_only_packages_are_offered_among_files()
    {
        Assert.Equal(["app.sfpkg", "Build.ZIP", "pkgs/"], PathCompletion.Complete("", _dir));
        Assert.Equal(["pkgs/", "pkgs/VotingPkg/"], PathCompletion.Complete("P", _dir));
        Assert.Equal(["pkgs/VotingPkg/"], PathCompletion.Complete("pkgs/", _dir));
        Assert.Empty(PathCompletion.Complete("nope/", _dir));
    }

    [Fact]
    public void Hidden_entries_show_once_a_dot_is_typed()
    {
        Assert.DoesNotContain(".hidden/", PathCompletion.Complete("", _dir));
        Assert.Equal([".hidden/"], PathCompletion.Complete(".", _dir));
    }

    [Fact]
    public void Absolute_paths_keep_the_form_typed_and_the_home_folder_is_tilde()
    {
        var typed = _dir + "/pk";
        Assert.Equal([_dir + "/pkgs/", _dir + "/pkgs/VotingPkg/"], PathCompletion.Complete(typed));
        Assert.Equal(["~/"], PathCompletion.Complete("~"));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, "pkgs"), PathCompletion.Expand("~/pkgs"));
        Assert.Equal(home, PathCompletion.Expand("~"));
        Assert.Equal(Path.Combine(_dir, "app.sfpkg"), PathCompletion.Expand("app.sfpkg", _dir));
    }

    [Theory]
    [InlineData("app.sfpkg", true)]
    [InlineData("app.SFPKG", true)]
    [InlineData("app.zip", true)]
    [InlineData("app.tar", false)]
    public void Packages_are_sfpkg_or_zip(string name, bool package) => Assert.Equal(package, PathCompletion.IsPackage(name));
}

/// <summary>The shell's new keys and commands, and what they complete.</summary>
public sealed class ShellFeatureKeymapTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sfx-shell-").FullName;
    private readonly App _app = new(new FakeTerminal());

    public void Dispose()
    {
        _app.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private Shell Shell(bool readOnly = false)
    {
        var store = ContextStore.Load(Path.Combine(_dir, "config.toml"));
        var context = new ClusterContext("dev", "http://127.0.0.1:1", [], new AuthOptions(), ReadOnly: readOnly);
        var session = new ClusterSession(context, new Uri("http://127.0.0.1:1"), new ClusterQueries(null!));
        return new Shell(_app, session, new ClusterConnector(BuiltInAuth.Providers), store, TimeSpan.Zero);
    }

    private static KeyEvent Char(char c, KeyModifiers modifiers = KeyModifiers.None) => new(Key.Char, new Rune(c), modifiers);

    [Fact]
    public void Find_and_the_last_error_have_keys()
    {
        var keymap = Shell().Keymap;

        Assert.Equal("find", keymap.Find(Char('f', KeyModifiers.Control))?.Id);
        Assert.Equal("last-error", keymap.Find(Char('!'))?.Id);
    }

    [Theory]
    [InlineData("find voting", "find", "voting")]
    [InlineData("f _Node_1", "find", "_Node_1")]
    [InlineData("error", "last-error", null)]
    [InlineData("health", "go.health", null)]
    [InlineData("unhealthy", "go.health", null)]
    [InlineData("cluster-health", "go.cluster-health", null)]
    [InlineData("types", "go.types", null)]
    [InlineData("apptypes", "go.types", null)]
    [InlineData("store", "go.store", null)]
    [InlineData("imagestore", "go.store", null)]
    [InlineData("provision ~/pkgs/Voting.sfpkg", "provision", "~/pkgs/Voting.sfpkg")]
    public void New_commands_match_with_their_argument(string text, string id, string? argument)
    {
        var match = Shell().Keymap.FindCommand(text);

        Assert.NotNull(match);
        Assert.Equal(id, match.Value.Action.Id);
        Assert.Equal(argument, match.Value.Argument);
    }

    [Fact]
    public void Provisioning_is_not_offered_on_a_read_only_context()
    {
        Assert.Null(Shell(readOnly: true).Keymap.FindCommand("provision x"));
        Assert.NotNull(Shell(readOnly: true).Keymap.FindCommand("types"));
    }

    [Fact]
    public void Provision_completes_folders_and_packages_on_this_machine()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "VotingPkg"));
        File.WriteAllText(Path.Combine(_dir, "Voting.sfpkg"), "");
        File.WriteAllText(Path.Combine(_dir, "readme.md"), "");
        var shell = Shell();
        var source = new KeymapCompletionSource(shell.Keymap) { Arguments = shell.Arguments };

        var items = source.Complete($"provision {_dir}/Vot").Select(i => i.Text).ToList();

        Assert.Equal([$"provision {_dir}/Voting.sfpkg", $"provision {_dir}/VotingPkg/"], items);
    }
}

/// <summary>The summary line, what's unhealthy, and find: each opens where the entity is.</summary>
public class OverviewPageTests
{
    private readonly FakeHost _host = new();
    private readonly Views _views;

    public OverviewPageTests() => _views = new Views(new ClusterQueries(null!), _host);

    private static readonly NodeRow Node1 = new("_Node_1", "10.0.0.4", "FrontEnd", "Up", HealthState.Ok, "0", "fd:/0", true, "10.0", null, "id1");
    private static readonly ApplicationRow Voting = new("Voting", "fabric:/Voting", "VotingType", "1.0.0", "Ready", HealthState.Ok);
    private static readonly ServiceRow Web = new("Voting~Web", "fabric:/Voting/Web", "WebType", "Stateless", "Active", HealthState.Ok);
    private static readonly PartitionRow Partition = new(PartitionId.Parse("2d5a8f0e-0000-4000-8000-000000000001"), "Stateless", "Singleton", "Ready", HealthState.Ok, "1");

    private static readonly IReadOnlyList<SearchHit> Index =
    [
        new("node", Node1.Name, Node: Node1),
        new("application", Voting.Name, Application: Voting),
        new("service", Web.Name, Application: Voting, Service: Web),
    ];

    [Fact]
    public void The_summary_line_counts_each_kind_with_its_warnings_and_errors()
    {
        var summary = new ClusterSummary("10.1.2", new HealthReport(HealthState.Warning, [], []),
        [
            new HealthCount("Node", 4, 1, 0),
            new HealthCount("Application", 2, 0, 1),
            new HealthCount("Service", 7, 0, 0),
        ]);

        Assert.Equal(" ▲ Warning  ·  5 nodes ⚠1  ·  3 apps ✗1  ·  7 services  ·  v10.1.2", Views.SummaryLine(summary).PlainText);
    }

    [Fact]
    public void Find_lists_matches_and_opens_the_hit_under_its_parents_with_rows_selected()
    {
        var choices = _views.SearchChoices(Index, "web");

        var choice = Assert.Single(choices);
        Assert.Equal("fabric:/Voting/Web", choice.Title);
        Assert.Equal("service · WebType", choice.Subtitle);

        choice.Pick();
        var (_, pages, replace) = Assert.Single(_host.Opens);
        Assert.True(replace);
        Assert.Equal(3, pages.Count);
        Assert.IsType<ResourceView<ApplicationRow>>(pages[0]);
        Assert.IsType<ResourceView<ServiceRow>>(pages[1]);
        Assert.IsType<ResourceView<PartitionRow>>(pages[2]);
    }

    [Fact]
    public void Nodes_are_found_by_address_and_open_on_their_page()
    {
        var choice = Assert.Single(_views.SearchChoices(Index, "10.0.0.4"));
        Assert.Equal("_Node_1  10.0.0.4", choice.Title);
        Assert.Equal("node · FrontEnd", choice.Subtitle);

        var pages = _views.PathTo(Index[0]);
        Assert.Equal(2, pages.Count);
        Assert.IsType<ResourceView<NodeRow>>(pages[0]);
    }

    [Fact]
    public void A_partition_goes_all_the_way_down_to_its_replicas()
    {
        var pages = _views.PathTo(new SearchHit("partition", "p", Application: Voting, Service: Web, Partition: Partition));

        Assert.Equal(4, pages.Count);
        Assert.IsType<ResourceView<ReplicaRow>>(pages[3]);
    }

    [Fact]
    public void A_guid_is_looked_up_as_a_partition_and_nothing_is_listed_while_the_index_loads()
    {
        var choice = Assert.Single(_views.SearchChoices(null, " 2d5a8f0e-0000-4000-8000-000000000001 "));
        Assert.Equal("partition 2d5a8f0e-0000-4000-8000-000000000001", choice.Title);
        Assert.Empty(_views.SearchChoices(null, "web"));
        Assert.Empty(_views.SearchChoices(Index, "   "));
    }

    [Fact]
    public void The_search_box_follows_what_is_typed_and_enter_opens_the_selected_result()
    {
        var opened = new List<string>();
        var closed = 0;
        var picker = new SearchPicker(query => query.Length == 0 ? [] :
            [new Choice("a " + query, null, () => opened.Add("a")), new Choice("b " + query, null, () => opened.Add("b"))])
        {
            OnClose = () => closed++,
        };
        Assert.Empty(picker.Results);

        foreach (var c in "web")
        {
            picker.OnEvent(new KeyEvent(Key.Char, new Rune(c), KeyModifiers.None));
        }
        Assert.Equal("web", picker.Query);
        Assert.Equal(["a web", "b web"], picker.Results.Select(r => r.Title));

        picker.OnEvent(new KeyEvent(Key.Down, default, KeyModifiers.None));
        picker.OnEvent(new KeyEvent(Key.Enter, default, KeyModifiers.None));
        Assert.Equal(["b"], opened);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void A_replica_goes_to_its_node_with_o()
    {
        var service = Web with { Kind = "Stateful" };
        var replica = new ReplicaRow("131", "_Node_2", "Primary", "Ready", HealthState.Ok, "addr");
        var action = _views.ReplicaActions(service, Partition).Single(a => a.Title == "Go to its node");

        Assert.Equal(ActionRisk.None, action.Risk);
        Assert.Equal("o", Assert.Single(action.Keys!).ToString());
        Assert.True(action.Applies(replica));
    }
}

/// <summary>Application types: creating an application of one, and provisioning a package.</summary>
public class ApplicationTypeFormTests
{
    private readonly FakeHost _host = new();
    private readonly Views _views;

    public ApplicationTypeFormTests() => _views = new Views(new ClusterQueries(null!), _host);

    private static readonly ApplicationTypeRow VotingType = new("VotingType", "1.0.0", "Available",
        new Dictionary<string, string> { ["Web_InstanceCount"] = "-1", ["Port"] = "8080" });

    [Fact]
    public void The_create_form_names_the_application_after_the_type_and_lists_its_parameters_with_their_defaults()
    {
        var fields = Views.CreateApplicationFields(VotingType);

        Assert.Equal(["name", "version", "param:Port", "param:Web_InstanceCount"], fields.Select(f => f.Key));
        Assert.Equal("fabric:/Voting", fields[0].Value);
        Assert.True(fields[1].ReadOnly);
        Assert.Equal("8080", fields[2].Value);
        Assert.Equal("give it a name", fields[0].Check("fabric:/"));
    }

    [Fact]
    public void Creating_sends_only_the_parameters_that_were_changed()
    {
        var fields = Views.CreateApplicationFields(VotingType);
        fields[0].Value = "sfx-test-voting";
        fields[2].Value = "9090";

        var operation = _views.CreateApplicationOperation(VotingType, fields);

        Assert.Equal("Create fabric:/sfx-test-voting (VotingType 1.0.0)", operation.Title);
        Assert.Equal("fabric:/sfx-test-voting", operation.Target);
        Assert.Equal(ActionRisk.Confirm, operation.Risk);
        Assert.Null(operation.Warning);
        Assert.Equal("Port  8080 → 9090", Assert.Single(operation.Details!).PlainText);
        Assert.NotNull(operation.Track);
    }

    [Fact]
    public void Types_offer_create_while_available_and_unprovision_with_the_version_to_type()
    {
        var actions = _views.ApplicationTypeActions();
        var create = actions.Single(a => a.Title.StartsWith("Create", StringComparison.Ordinal));
        Assert.True(create.Applies(VotingType));
        Assert.False(create.Applies(VotingType with { Status = "Provisioning" }));

        actions.Single(a => a.Title.StartsWith("Unprovision", StringComparison.Ordinal)).Run(VotingType);
        var operation = Assert.Single(_host.Operations);
        Assert.Equal("Unprovision VotingType 1.0.0", operation.Title);
        Assert.Equal(ActionRisk.Destructive, operation.Risk);
        Assert.Equal("1.0.0", operation.Target);
    }

    [Fact]
    public void Create_opens_a_form_that_can_be_submitted_as_it_is()
    {
        _views.ApplicationTypeActions().Single(a => a.Title.StartsWith("Create", StringComparison.Ordinal)).Run(VotingType);

        Assert.Equal("Create an application of VotingType 1.0.0", Assert.Single(_host.Edits).Title);
        Assert.True(Assert.Single(_host.EditUnchanged));
    }

    [Fact]
    public void Provisioning_a_local_package_says_where_it_goes_and_reports_progress()
    {
        var operation = _views.ProvisionLocalOperation("/pkgs/Voting.sfpkg", "VotingType", "2.0.0", "VotingType_2.0.0");

        Assert.Equal("Provision VotingType 2.0.0", operation.Title);
        Assert.Equal(
            ["From     /pkgs/Voting.sfpkg", "Type     VotingType", "Version  2.0.0", "To       image store › VotingType_2.0.0"],
            operation.Details!.Select(d => d.PlainText));
        Assert.NotNull(operation.Progress);

        Sfx.Tui.OperationState? reported = null;
        operation.Progress!.Attach(state => reported = state);
        operation.Progress.Report(0.5, "uploading 1 KB of 2 KB");
        Assert.Equal(0.5, reported?.Fraction);
        Assert.Equal("uploading 1 KB of 2 KB", reported?.Detail);
    }

    [Fact]
    public void Provision_is_refused_on_a_read_only_context()
    {
        _host.ReadOnly = true;
        _views.ProvisionLocal("/nowhere/Voting.sfpkg");

        Assert.Equal(["this context is read-only"], _host.Warnings);
        Assert.Empty(_host.Operations);
    }

    [Fact]
    public void A_package_that_cannot_be_read_is_reported()
    {
        _views.ProvisionLocal("/nowhere/at/all/Voting.sfpkg");

        Assert.Single(_host.Errors);
        Assert.Empty(_host.Operations);
    }
}

/// <summary>One form edits an application's parameters or upgrades it; the review says what changes.</summary>
public class ApplicationUpgradeFormTests
{
    private readonly FakeHost _host = new();
    private readonly Views _views;

    public ApplicationUpgradeFormTests() => _views = new Views(new ClusterQueries(null!), _host);

    private static readonly ApplicationRow App = new("Voting", "fabric:/Voting", "VotingType", "1.0.0", "Ready", HealthState.Ok);

    private static Views.UpgradeFormState State(params ApplicationTypeRow[] versions) => new()
    {
        App = App,
        Versions = versions,
        Current = new Dictionary<string, string> { ["A"] = "x" },
    };

    private static readonly ApplicationTypeRow V1 = new("VotingType", "1.0.0", "Available", new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" });
    private static readonly ApplicationTypeRow V2 = new("VotingType", "2.0.0", "Available", new Dictionary<string, string> { ["B"] = "2", ["C"] = "3" });
    private static readonly ApplicationTypeRow V10 = new("VotingType", "10.0.0", "Available", new Dictionary<string, string> { ["B"] = "2" });

    private static FormField Field(IReadOnlyList<FormField> fields, string key) => fields.Single(f => f.Key == key);

    [Fact]
    public void Editing_keeps_the_version_and_upgrading_picks_the_newest_other_one()
    {
        var edit = Views.UpgradeFields(State(V1, V2, V10), otherVersion: false);
        Assert.Equal("1.0.0", Field(edit, "version").Value);
        Assert.Equal(["1.0.0", "2.0.0", "10.0.0"], Field(edit, "version").Choices);
        Assert.Equal(UpgradePlan.MonitoredRollback.Title, Field(edit, "plan").Value);
        Assert.Equal(["param:A", "param:B", "param:C"], edit.Where(f => f.Key.StartsWith("param:", StringComparison.Ordinal)).Select(f => f.Key));
        Assert.Equal("x", Field(edit, "param:A").Value);
        Assert.Equal("default 1", Field(edit, "param:A").Note);
        Assert.Equal("in 2.0.0", Field(edit, "param:C").Note);
        Assert.Contains(edit, f => f.Key == "policy:healthCheckWait");

        Assert.Equal("10.0.0", Field(Views.UpgradeFields(State(V1, V2, V10), otherVersion: true), "version").Value);
    }

    [Fact]
    public void Upgrading_with_no_other_version_provisioned_is_refused()
    {
        var refused = Assert.Throws<RefusedException>(() => Views.UpgradeFields(State(V1), otherVersion: true));
        Assert.Contains(":provision", refused.Message);
    }

    [Fact]
    public void Nothing_changed_is_refused()
    {
        var fields = Views.UpgradeFields(State(V1, V2), otherVersion: false);

        Assert.Contains("nothing to change", Assert.Throws<RefusedException>(() => _views.UpgradeOperation(State(V1, V2), fields)).Message);
    }

    [Fact]
    public void A_parameter_change_is_an_upgrade_to_the_same_version_that_sets_and_resets()
    {
        var state = State(V1, V2);
        var fields = Views.UpgradeFields(state, otherVersion: false);
        Field(fields, "param:A").Value = "";
        Field(fields, "param:B").Value = "5";

        var operation = _views.UpgradeOperation(state, fields);

        Assert.Equal("Upgrade fabric:/Voting to 1.0.0 (monitored, roll back on failure), setting B, resetting A", operation.Title);
        Assert.Equal("fabric:/Voting", operation.Target);
        Assert.Contains("same version", operation.Warning);
        Assert.Equal(TimeSpan.FromHours(2), operation.TrackFor);
    }

    [Fact]
    public void Parameters_the_new_version_does_not_define_are_said_to_be_dropped()
    {
        var state = State(V1, V2);
        var fields = Views.UpgradeFields(state, otherVersion: true);
        Field(fields, "plan").Value = UpgradePlan.UnmonitoredManual.Title;

        var operation = _views.UpgradeOperation(state, fields);

        Assert.Equal("Upgrade fabric:/Voting to 2.0.0 (unmonitored, manual)", operation.Title);
        Assert.Contains(operation.Details!, d => d.PlainText == "dropped, 2.0.0 doesn't define them: A");
    }

    [Fact]
    public void A_parameter_the_target_version_lacks_is_refused()
    {
        var state = State(V1, V2);
        var fields = Views.UpgradeFields(state, otherVersion: true);
        Field(fields, "param:A").Value = "y";

        Assert.Throws<RefusedException>(() => _views.UpgradeOperation(state, fields));
    }

    [Fact]
    public void Policies_take_durations_and_are_refused_where_they_do_not_apply()
    {
        IReadOnlyList<FormField> Policy(bool cluster, params (string Key, string Value)[] values)
        {
            var fields = Views.PolicyFields(cluster).ToList();
            foreach (var (key, value) in values)
            {
                fields.Single(f => f.Key == "policy:" + key).Value = value;
            }
            return fields;
        }

        Assert.Null(Views.ParsePolicy(Policy(false), UpgradePlan.MonitoredRollback, cluster: false));

        var policy = Views.ParsePolicy(Policy(false, ("healthCheckWait", "5m"), ("forceRestart", "true"), ("maxPercentUnhealthyServices", "20")),
            UpgradePlan.MonitoredRollback, cluster: false);
        Assert.Equal(TimeSpan.FromMinutes(5), policy?.HealthCheckWait);
        Assert.True(policy?.ForceRestart);
        Assert.Equal(20, policy?.MaxPercentUnhealthyServices);

        Assert.Contains("monitored", Assert.Throws<RefusedException>(() =>
            Views.ParsePolicy(Policy(false, ("healthCheckWait", "5m")), UpgradePlan.UnmonitoredAuto, cluster: false)).Message);
        Assert.Contains("duration", Assert.Throws<RefusedException>(() =>
            Views.ParsePolicy(Policy(false, ("upgradeTimeout", "soon")), UpgradePlan.MonitoredRollback, cluster: false)).Message);
        Assert.Equal(10, Views.ParsePolicy(Policy(true, ("maxPercentUnhealthyNodes", "10")), UpgradePlan.MonitoredRollback, cluster: true)?.MaxPercentUnhealthyNodes);
        Assert.DoesNotContain(Views.PolicyFields(cluster: true), f => f.Key == "policy:maxPercentUnhealthyServices");
    }

    [Fact]
    public void While_upgrading_u_steers_the_upgrade_and_otherwise_starts_one()
    {
        var actions = _views.ApplicationActions();
        string[] Titles(ApplicationRow app) => actions.Where(a => a.Applies(app) && a.Keys?.Any(k => k.ToString() == "u") == true).Select(a => a.Title).ToArray();

        Assert.Equal(["Upgrade to another version"], Titles(App));
        Assert.Equal(["Steer the running upgrade"], Titles(App with { Status = "Upgrading" }));
        Assert.DoesNotContain(actions, a => a.Applies(App with { Id = ClusterQueries.SystemApplicationId }) && a.Title == "Edit parameters, or upgrade");
    }

    [Theory]
    [InlineData("Ready", "Upgrade to another version")]
    [InlineData("Upgrading", "Steer the running upgrade")]
    public async Task The_u_key_finds_the_action_that_fits_the_row(string status, string title)
    {
        var page = new ResourceView<ApplicationRow>("applications", "apps", _ => Task.FromResult<IReadOnlyList<ApplicationRow>>([App with { Status = status }]), a => a.Id)
            .Acting(_views.ApplicationActions(), _host)
            .Column("NAME", Constraint.Fill(1), a => Plain(a.Name), a => a.Name);
        (await page.LoadAsync(CancellationToken.None))();
        page.Widget.Render(new Cellar.Rendering.Surface(60, 10), new Rect(0, 0, 60, 10));
        var keymap = new Keymap();
        keymap.AddScope("page", () => page.Actions);

        Assert.Equal(title, keymap.Find(new KeyEvent(Key.Char, new Rune('u'), KeyModifiers.None))?.Title);
        Assert.Equal(status == "Ready" ? "Edit parameters, or upgrade" : null, keymap.Find(new KeyEvent(Key.Char, new Rune('e'), KeyModifiers.None))?.Title);
    }

    [Fact]
    public void Ctrl_n_creates_an_application_or_a_service_unless_read_only()
    {
        var ctrlN = new KeyEvent(Key.Char, new Rune('n'), KeyModifiers.Control);
        Keymap Of(IResourceView page)
        {
            var keymap = new Keymap();
            keymap.AddScope("page", () => page.Actions);
            return keymap;
        }

        Assert.Equal("page.create-application", Of(_views.Applications()).Find(ctrlN)?.Id);
        Assert.Equal("page.create-service", Of(_views.Services(App)).Find(ctrlN)?.Id);
        Assert.Null(Of(_views.Services(App with { Id = ClusterQueries.SystemApplicationId })).Find(ctrlN));

        _host.ReadOnly = true;
        Assert.Null(Of(_views.Applications()).Find(ctrlN));
        Assert.Null(Of(_views.Services(App)).Find(ctrlN));
    }

    [Fact]
    public void Rolling_back_asks_to_confirm_and_follows_the_upgrade_back()
    {
        _views.ApplicationActions().Single(a => a.Title == "Roll back the running upgrade").Run(App with { Status = "Upgrading" });

        var operation = Assert.Single(_host.Operations);
        Assert.Equal("Roll back the upgrade of fabric:/Voting", operation.Title);
        Assert.NotNull(operation.Track);
    }

    [Fact]
    public void Steering_offers_resume_change_and_roll_back()
    {
        _views.ApplicationActions().Single(a => a.Title == "Steer the running upgrade").Run(App with { Status = "Upgrading" });

        var (title, choices) = Assert.Single(_host.Picks);
        Assert.Equal("fabric:/Voting is upgrading", title);
        Assert.Equal(["Resume into the next domain", "Change how it proceeds", "Roll back"], choices.Select(c => c.Title));

        choices[1].Pick();
        Assert.Equal(UpgradePlan.All.Select(p => p.Title), _host.Picks[1].Choices.Select(c => c.Title));
        _host.Picks[1].Choices[2].Pick();
        Assert.Equal("Continue the upgrade of fabric:/Voting: unmonitored, automatic", Assert.Single(_host.Operations).Title);
    }
}

/// <summary>Creating a service, and editing its settings in one update.</summary>
public class ServiceFormTests
{
    private readonly FakeHost _host = new();
    private readonly Views _views;

    public ServiceFormTests() => _views = new Views(new ClusterQueries(null!), _host);

    private static readonly ApplicationRow App = new("Voting", "fabric:/Voting", "VotingType", "1.0.0", "Ready", HealthState.Ok);

    private static readonly IReadOnlyList<ServiceTypeRow> Types =
    [
        new("VotingDataType", true, "VotingData", "1.0.0"),
        new("VotingWebType", false, "VotingWeb", "1.0.0"),
    ];

    private static FormField Field(IReadOnlyList<FormField> fields, string key) => fields.Single(f => f.Key == key);

    [Fact]
    public void The_create_form_starts_from_the_first_type_and_checks_the_kind_against_the_type()
    {
        var fields = Views.CreateServiceFields(Types);

        Assert.Equal("VotingData", Field(fields, "name").Value);
        Assert.Equal("Stateful", Field(fields, "kind").Value);
        Assert.Equal("3", Field(fields, "count").Value);

        Field(fields, "type").Value = "VotingWebType";
        Assert.Equal("VotingWebType is a stateless service type", Field(fields, "kind").Check("Stateful"));
        Assert.Null(Field(fields, "kind").Check("Stateless"));
    }

    [Fact]
    public void A_service_is_created_under_its_application_and_described_in_the_review()
    {
        var fields = Views.CreateServiceFields(Types);
        Field(fields, "name").Value = "sfx-test-data";
        Field(fields, "partitions").Value = "4";

        var operation = _views.CreateServiceOperation(App, Types, fields);

        Assert.Equal("Create fabric:/Voting/sfx-test-data", operation.Title);
        Assert.Equal(
            ["Type        VotingDataType (stateful)", "Size        3 replicas (min 2), persisted", "Partitions  4 (Int64 ranges)"],
            operation.Details!.Select(d => d.PlainText));
        Assert.NotNull(operation.Track);
    }

    [Fact]
    public void Stateless_services_need_an_instance_or_every_node()
    {
        var fields = Views.CreateServiceFields(Types);
        Field(fields, "type").Value = "VotingWebType";
        Field(fields, "kind").Value = "Stateless";
        Field(fields, "count").Value = "0";

        Assert.Throws<RefusedException>(() => _views.CreateServiceOperation(App, Types, fields));

        Field(fields, "count").Value = "-1";
        Assert.Contains("Size        an instance on every node", _views.CreateServiceOperation(App, Types, fields).Details!.Select(d => d.PlainText));
    }

    [Theory]
    [InlineData("Web", "fabric:/Voting/Web")]
    [InlineData("/Web/", "fabric:/Voting/Web")]
    [InlineData("fabric:/Other/Web", "fabric:/Other/Web")]
    public void Service_names_go_under_the_application_unless_given_in_full(string name, string full) =>
        Assert.Equal(full, Views.ServiceName(App.Name, name));

    private static readonly ServiceSettingSet Stateful = new(true,
    [
        new("count", "Target replicas", SettingKind.Count, "3", Min: 1),
        new("min-replicas", "Min replicas", SettingKind.Count, "2", Min: 1),
        new("quorum-loss-wait", "Quorum loss wait", SettingKind.Seconds, ""),
        new("move-cost", "Default move cost", SettingKind.Choice, "Low", ["Zero", "Low", "Medium", "High"]),
    ]);

    private static readonly ServiceRow Data = new("Voting~Data", "fabric:/Voting/Data", "VotingDataType", "Stateful", "Active", HealthState.Ok);

    [Fact]
    public void Every_setting_is_a_field_checked_as_the_cluster_would()
    {
        var fields = Views.SettingFields(Stateful);

        Assert.Equal(["count", "min-replicas", "quorum-loss-wait", "move-cost"], fields.Select(f => f.Key));
        Assert.Equal(FormFieldKind.Choice, Field(fields, "move-cost").Kind);
        Assert.Equal("cluster default", Field(fields, "quorum-loss-wait").Note);
        Assert.NotNull(Field(fields, "quorum-loss-wait").Check("soon"));
        Assert.Null(Field(fields, "quorum-loss-wait").Check("5m"));
        Assert.NotNull(Field(fields, "count").Check("0"));
    }

    [Fact]
    public void Settings_change_in_one_update_and_unchanged_is_refused()
    {
        var fields = Views.SettingFields(Stateful);
        Assert.Throws<RefusedException>(() => _views.SettingsOperation(Data, Stateful, fields));

        Field(fields, "count").Value = "5";
        Field(fields, "quorum-loss-wait").Value = "5m";
        var operation = _views.SettingsOperation(Data, Stateful, fields);

        Assert.Equal("Change Target replicas to 5 and Quorum loss wait to 300s on fabric:/Voting/Data", operation.Title);
        Assert.Contains("added or dropped", operation.Warning);
        Assert.Equal(2, operation.Details!.Count);
    }

    [Fact]
    public void Services_are_created_and_edited_only_in_user_applications()
    {
        var system = App with { Id = ClusterQueries.SystemApplicationId, Name = "fabric:/System" };

        Assert.Contains(_views.ServiceActions(App), a => a.Title == "Edit settings" && a.Applies(Data));
        Assert.DoesNotContain(_views.ServiceActions(system), a => a.Title == "Edit settings" && a.Applies(Data));
    }
}

/// <summary>Cluster upgrades start from provisioned versions and are destructive to confirm.</summary>
public class ClusterUpgradeFormTests
{
    private readonly FakeHost _host = new();
    private readonly Views _views;

    public ClusterUpgradeFormTests() => _views = new Views(new ClusterQueries(null!), _host);

    private static readonly ClusterUpgradeOptions Options = new("10.0", "1", ["10.1", "10.0"], ["2", "1"], false, null);

    [Fact]
    public void The_form_offers_the_other_provisioned_versions_or_keeping_today_s()
    {
        var fields = Views.ClusterUpgradeFields(Options);

        Assert.Equal(["keep", "10.1"], fields.Single(f => f.Key == "code").Choices);
        Assert.Equal(["keep", "2"], fields.Single(f => f.Key == "config").Choices);
        Assert.Equal("now 10.0", fields.Single(f => f.Key == "code").Note);
        Assert.Contains(fields, f => f.Key == "policy:maxPercentUnhealthyNodes");
    }

    [Fact]
    public void Keeping_both_is_refused_and_an_upgrade_asks_for_the_context_name()
    {
        var fields = Views.ClusterUpgradeFields(Options);
        var start = new Views.ClusterUpgradeStart(Options, null);
        Assert.Throws<RefusedException>(() => _views.ClusterUpgradeOperation("dev", start, fields));

        fields.Single(f => f.Key == "code").Value = "10.1";
        var operation = _views.ClusterUpgradeOperation("dev", start, fields);

        Assert.Equal("Upgrade the cluster to code 10.1 (monitored, roll back on failure)", operation.Title);
        Assert.Equal(ActionRisk.Destructive, operation.Risk);
        Assert.Equal("dev", operation.Target);
        Assert.Equal(TimeSpan.FromHours(4), operation.TrackFor);
    }
}

/// <summary>Following provisions and upgrades until the cluster says they're done.</summary>
public class LifecycleTrackingTests
{
    private static ApplicationTypeRow Type(string version, string status, string details = "") =>
        new("VotingType", version, status, new Dictionary<string, string>(), details);

    [Fact]
    public void A_provisioned_type_settles_once_available()
    {
        Assert.Equal("waiting for the type to show up", Tracking.TypeProvisioned("VotingType", "2.0.0", new HashSet<string>(), [Type("1.0.0", "Available")]).Detail);
        Assert.Equal("VotingType 2.0.0 is provisioning", Tracking.TypeProvisioned("VotingType", "2.0.0", new HashSet<string>(), [Type("2.0.0", "Provisioning")]).Detail);
        Assert.True(Tracking.TypeProvisioned("VotingType", "2.0.0", new HashSet<string>(), [Type("2.0.0", "Available")]).Done);

        var failed = Tracking.TypeProvisioned("VotingType", "2.0.0", new HashSet<string>(), [Type("2.0.0", "Failed", "bad manifest")]);
        Assert.True(failed.Failed);
        Assert.Equal("VotingType 2.0.0 failed to provision: bad manifest", failed.Detail);
    }

    [Fact]
    public void A_package_provisioned_from_the_store_is_the_type_that_is_new()
    {
        var before = new HashSet<string> { "VotingType 1.0.0" };

        Assert.False(Tracking.TypeProvisioned(null, null, before, [Type("1.0.0", "Available")]).Done);
        Assert.Equal("VotingType 3.0.0 is available", Tracking.TypeProvisioned(null, null, before, [Type("1.0.0", "Available"), Type("3.0.0", "Available")]).Detail);
    }

    private static UpgradeReport Report(string state, string target, string started = "t1", params (string Name, string State)[] domains) =>
        new(state, "Monitored", target, "", started, null, "None", "", "", domains.Select(d => new UpgradeDomainRow(d.Name, d.State)).ToList(), "", [], []);

    [Fact]
    public void An_upgrade_waits_for_its_target_then_counts_domains()
    {
        Assert.Equal("waiting for the upgrade to start", Tracking.Upgrade(Report("RollingForwardCompleted", "1.0.0"), "2.0.0").Detail);
        Assert.Equal("waiting for the upgrade to start", Tracking.Upgrade(Report("RollingForwardCompleted", "1.0.0", "t0"), "1.0.0", "t0").Detail);

        var running = Tracking.Upgrade(Report("RollingForwardInProgress", "2.0.0", "t1", ("UD0", "Completed"), ("UD1", "InProgress")), "2.0.0", "t0");
        Assert.False(running.Done);
        Assert.Equal("1 of 2 domains", running.Detail);
        Assert.Equal(0.5, running.Fraction);

        Assert.True(Tracking.Upgrade(Report("RollingForwardCompleted", "2.0.0"), "2.0.0").Done);
        var rolledBack = Tracking.Upgrade(Report("RollingBackCompleted", "2.0.0"), "2.0.0");
        Assert.True(rolledBack.Failed);
        Assert.Equal("rolled back", rolledBack.Detail);
    }

    [Fact]
    public void Created_things_settle_once_listed() =>
        Assert.Equal((false, true), (Tracking.Created(false).Done, Tracking.Created(true).Done));

    [Theory]
    [InlineData(null, "")]
    [InlineData(512L, "512 B")]
    [InlineData(3_500L, "3.4 KB")]
    public void Sizes_read_as_bytes(long? bytes, string text) => Assert.Equal(text, Bytes(bytes));

    [Fact]
    public void Store_usage_sums_what_its_parts_use_and_says_what_is_free()
    {
        var usage = new ImageStoreUsage(10_000_000, 4_000_000, new ImageStoreUse(1024, 1), new ImageStoreUse(1024, 2), null, null);

        Assert.Equal("2.0 KB used · 3.8 MB free", Views.UsageText(usage));
        Assert.Null(Views.UsageText(null));
        Assert.Null(Views.UsageText(new ImageStoreUsage(null, null, null, null, null, null)));
    }
}

/// <summary>The startup picker: the contexts page on its own.</summary>
public sealed class ContextPickerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sfx-picker-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ContextStore Store(string text)
    {
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, text);
        return ContextStore.Load(path);
    }

    [Fact]
    public void It_asks_only_with_saved_contexts_none_active_and_nothing_given()
    {
        var saved = Store("""
            [contexts.dev]
            endpoint = "http://localhost:19080"
            """);
        Assert.True(SfxHost.NeedsPick(Sfx.Tui.CommandLine.Parse([], new HashSet<string>()), saved));
        Assert.False(SfxHost.NeedsPick(Sfx.Tui.CommandLine.Parse(["-c", "dev"], new HashSet<string>()), saved));
        Assert.False(SfxHost.NeedsPick(Sfx.Tui.CommandLine.Parse(["http://localhost:19080"], new HashSet<string>()), saved));

        saved.Active = "dev";
        Assert.False(SfxHost.NeedsPick(Sfx.Tui.CommandLine.Parse([], new HashSet<string>()), saved));
        Assert.False(SfxHost.NeedsPick(Sfx.Tui.CommandLine.Parse([], new HashSet<string>()), Store("")));
    }

    [Fact]
    public async Task Enter_picks_the_selected_context_and_q_quits()
    {
        var store = Store("""
            [contexts.alpha]
            endpoint = "a"

            [contexts.beta]
            endpoint = "b"
            """);
        using var app = new App(new FakeTerminal());
        ClusterContext? picked = null;
        var view = Views.Contexts(store, () => "", context => picked = context);
        (await view.LoadAsync(CancellationToken.None))();
        view.Widget.HasFocus = true;
        ContextPicker.SelectFirst(view, new Size(60, 10));
        Assert.Equal("alpha", view.Selected?.Context.Name);

        Assert.True(ContextPicker.Handle(app, view, new KeyEvent(Key.Char, new Rune('j'), KeyModifiers.None)));
        ContextPicker.Handle(app, view, new KeyEvent(Key.Enter, default, KeyModifiers.None));
        Assert.Equal("beta", picked?.Name);

        Assert.True(ContextPicker.Handle(app, view, new KeyEvent(Key.Char, new Rune('q'), KeyModifiers.None)));
        Assert.False(ContextPicker.Handle(app, view, new KeyEvent(Key.Char, new Rune('x'), KeyModifiers.None)));
    }
}
