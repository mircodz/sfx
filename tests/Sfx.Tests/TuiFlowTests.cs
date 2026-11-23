using Cellar.Widgets;
using Microsoft.ServiceFabric.Common;
using Sfx.Core;
using Sfx.Tui;

namespace Sfx.Tests;

/// <summary>How the terminal follows a change after the cluster accepted it.</summary>
public class TrackingTests
{
    private static ReplicaRow Replica(string id, string node, string role) => new(id, node, role, "Ready", HealthState.Ok, "addr");

    [Fact]
    public void A_secondary_has_moved_once_it_is_off_its_node_and_on_the_one_asked_for()
    {
        Assert.Equal("still on _Node_1", Tracking.ReplicaMove("ActiveSecondary", "2", "_Node_1", null, [Replica("2", "_Node_1", "ActiveSecondary")]).Detail);
        Assert.False(Tracking.ReplicaMove("ActiveSecondary", "2", "_Node_1", "_Node_3", [Replica("3", "_Node_2", "ActiveSecondary")]).Done);

        var moved = Tracking.ReplicaMove("ActiveSecondary", "2", "_Node_1", "_Node_3", [Replica("4", "_Node_3", "ActiveSecondary")]);
        Assert.True(moved.Done);
        Assert.Equal("on _Node_3", moved.Detail);
    }

    [Fact]
    public void A_primary_is_followed_by_its_role_as_its_id_may_change()
    {
        Assert.Equal("waiting for a new primary", Tracking.ReplicaMove("Primary", "1", "_Node_1", null, []).Detail);
        Assert.False(Tracking.ReplicaMove("Primary", "1", "_Node_1", null, [Replica("1", "_Node_1", "Primary")]).Done);
        Assert.True(Tracking.ReplicaMove("Primary", "1", "_Node_1", null, [Replica("9", "_Node_2", "Primary")]).Done);
    }

    [Fact]
    public void Scaling_counts_the_partitions_at_the_new_size()
    {
        var running = Tracking.Scale(3, [("Ready", 3), ("Ready", 2)], stateful: true);
        Assert.False(running.Done);
        Assert.Equal("1 of 2 partitions at 3 replicas", running.Detail);
        Assert.Equal(0.5, running.Fraction);

        Assert.True(Tracking.Scale(3, [("Ready", 3), ("Ready", 3)], stateful: true).Done);
        Assert.True(Tracking.Scale(-1, [], stateful: false).Done);
        Assert.False(Tracking.Scale(2, [], stateful: false).Done);
    }

    [Fact]
    public void A_restarted_node_is_done_once_it_has_been_up_for_less_than_since_the_request()
    {
        NodeRow Node(string status, TimeSpan? uptime) => new("_Node_1", "host", "NodeType0", status, HealthState.Ok, "1", "fd:/1", false, "10.0", uptime);

        Assert.Equal("node is Down", Tracking.NodeRestart(Node("Down", null), TimeSpan.FromSeconds(30)).Detail);
        Assert.Equal("node is Unknown", Tracking.NodeRestart(null, TimeSpan.FromSeconds(30)).Detail);
        Assert.False(Tracking.NodeRestart(Node("Up", TimeSpan.FromDays(1)), TimeSpan.FromSeconds(30)).Done);
        Assert.True(Tracking.NodeRestart(Node("Up", TimeSpan.FromSeconds(10)), TimeSpan.FromSeconds(30)).Done);
    }

    [Fact]
    public void Node_status_and_deletes_settle_when_the_cluster_says_so()
    {
        Assert.True(Tracking.NodeStatus("Disabled", "Disabled").Done);
        Assert.Equal("node is Disabling", Tracking.NodeStatus("Disabled", "Disabling").Detail);
        Assert.Equal(("gone", true), (Tracking.Gone(false).Detail, Tracking.Gone(false).Done));
        Assert.False(Tracking.Gone(true).Done);
    }
}

/// <summary>Edits go through a form, then a review of what changed.</summary>
public class ScaleFormTests
{
    private readonly FakeHost _host = new();
    private readonly Views _views;

    public ScaleFormTests() => _views = new Views(new ClusterQueries(null!), _host);

    private static ServiceRow Service(string kind) => new("App~Svc", "fabric:/App/Svc", "SvcType", kind, "Active", HealthState.Ok);

    [Fact]
    public void Stateful_services_scale_their_replica_set_and_the_review_lists_each_change()
    {
        var fields = Views.StatefulScaleFields(3, 2);
        fields[0].Value = "5";

        var operation = _views.ScaleOperation(Service("Stateful"), fields);

        Assert.Equal("Scale fabric:/App/Svc to 5 replicas", operation.Title);
        Assert.Equal(ActionRisk.Confirm, operation.Risk);
        Assert.NotNull(operation.Track);
        var change = Assert.Single(operation.Details).PlainText;
        Assert.Equal("Target replica set size  3 → 5", change);
    }

    [Fact]
    public void The_minimum_is_checked_against_the_target_and_sent_only_when_changed()
    {
        var fields = Views.StatefulScaleFields(3, 2);
        Assert.Equal("can't be more than the target size", fields[1].Check("4"));
        Assert.Null(fields[1].Check("3"));

        fields[1].Value = "3";
        Assert.Equal("Scale fabric:/App/Svc to 3 replicas (min 3)", _views.ScaleOperation(Service("Stateful"), fields).Title);
    }

    [Fact]
    public void Stateless_services_scale_their_instances_and_minus_one_means_every_node()
    {
        var fields = Views.StatelessScaleFields(2);
        Assert.Equal("at least 1, or -1 for every node", fields[0].Check("0"));
        Assert.NotNull(fields[0].Check("-2"));

        fields[0].Value = "-1";
        Assert.Equal("Scale fabric:/App/Svc to every node", _views.ScaleOperation(Service("Stateless"), fields).Title);
        fields[0].Value = "1";
        Assert.Equal("Scale fabric:/App/Svc to 1 instance", _views.ScaleOperation(Service("Stateless"), fields).Title);
    }

    [Fact]
    public void Only_user_services_scale()
    {
        var app = new ApplicationRow("App", "fabric:/App", "AppType", "1.0", "Ready", HealthState.Ok);
        var system = app with { Id = ClusterQueries.SystemApplicationId, Name = "fabric:/System" };

        Assert.Contains(_views.ServiceActions(app), a => a.Title == "Scale service" && a.Applies(Service("Stateful")));
        Assert.DoesNotContain(_views.ServiceActions(system), a => a.Title == "Scale service" && a.Applies(Service("Stateful")));
    }

    [Fact]
    public void Stopping_a_node_asks_for_whole_seconds()
    {
        var node = new NodeRow("_Node_1", "host", "NodeType0", "Up", HealthState.Ok, "1", "fd:/1", false, "10.0", null);
        _views.NodeActions().Single(a => a.Title == "Stop node for a while").Run(node);

        var (_, initial, validate, submit) = Assert.Single(_host.Asks);
        Assert.Equal("600", initial);
        Assert.Equal("a whole number of seconds", validate!("ten"));
        Assert.Equal("a whole number of seconds", validate("0"));
        Assert.Null(validate("60"));

        submit("60");
        var operation = Assert.Single(_host.Operations);
        Assert.Equal("Stop _Node_1 for 60s", operation.Title);
        Assert.Equal(ActionRisk.Destructive, operation.Risk);
    }
}

public sealed class CommandHistoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sfx-history-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File => Path.Combine(_dir, "history");

    [Fact]
    public void Each_prompt_keeps_its_own_history_across_runs()
    {
        var first = new CommandHistory(File);
        first.For(':').Add("ctx prod");
        first.For('/').Add("payments");
        first.For(':').Add("events 6h");

        var again = new CommandHistory(File);

        Assert.Equal(["ctx prod", "events 6h"], again.For(':').Entries);
        Assert.Equal(["payments"], again.For('/').Entries);
    }

    [Fact]
    public void Trims_the_file_to_the_newest_entries()
    {
        System.IO.File.WriteAllLines(File, Enumerable.Range(0, 1000).Select(i => $":cmd {i}"));

        var history = new CommandHistory(File).For(':');

        Assert.Equal(200, history.Count);
        Assert.Equal("cmd 999", history.Entries[^1]);
        Assert.Equal(400, System.IO.File.ReadAllLines(File).Length);
    }

    [Fact]
    public void A_history_it_cannot_write_is_only_lost()
    {
        var history = new CommandHistory(Path.Combine(_dir, "missing", "history")).For(':');

        history.Add("nodes");

        Assert.Equal(["nodes"], history.Entries);
    }
}

public sealed class ContextsPageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sfx-contexts-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Contexts_are_grouped_by_folder_and_a_read_only_folder_says_so()
    {
        var path = Path.Combine(_dir, "config.toml");
        System.IO.File.WriteAllText(path, """
            [contexts.weu]
            endpoint = "weu"
            folder = "prod/europe"

            [contexts.dev]
            endpoint = "dev"

            [contexts.local]
            endpoint = "localhost"

            [folders.prod]
            label = "PROD"
            readonly = true
            """);
        var store = ContextStore.Load(path);
        ClusterContext? used = null;
        var page = Views.Contexts(store, () => "dev", context => used = context);

        (await page.LoadAsync(CancellationToken.None))();

        var groups = page.Table.Groups;
        Assert.Equal(2, groups.Count);
        Assert.Equal(["dev", "local"], groups.Single(g => g.Value.Length == 0).Rows.Select(c => c.Context.Name).Order());
        var prod = groups.Single(g => g.Value.Length > 0);
        Assert.Equal("prod/europe PROD", prod.Value);
        Assert.True(Assert.Single(prod.Rows).Context.ReadOnly);
        Assert.Contains("by folder", ((Panel)page.Widget).Title);

        page.OnOpen!(prod.Rows[0]);
        Assert.Equal("weu", used?.Name);
    }
}
