using Microsoft.ServiceFabric.Common;
using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Tui;
using Sfx.Web;

namespace Sfx.Tests;

public sealed class ViewTests : IDisposable
{
    private static readonly IReadOnlySet<string> NoFlags = new HashSet<string>();
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sfx-{Guid.NewGuid():N}", "config.toml");
    private readonly ClusterConnector _connector = new(BuiltInAuth.Providers);

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ViewFilter View(string[] apps, string[]? exclude = null) =>
        ViewFilter.From(new ViewDefinition("test", apps, exclude ?? []));

    [Theory]
    [InlineData("fabric:/payments", "fabric:/payments", true)]
    [InlineData("fabric:/payments", "FABRIC:/Payments", true)]
    [InlineData("fabric:/payments", "fabric:/payments/api", false)]
    [InlineData("fabric:/payments", "fabric:/payments-v2", false)]
    [InlineData("fabric:/payments/*", "fabric:/payments/api", true)]
    [InlineData("fabric:/payments/*", "fabric:/payments/api/v2", false)]
    [InlineData("fabric:/payments/*", "fabric:/payments", false)]
    [InlineData("fabric:/shared-*", "fabric:/shared-cache", true)]
    [InlineData("fabric:/shared-*", "fabric:/shared-", true)]
    [InlineData("fabric:/shared-*", "fabric:/shared-cache/x", false)]
    [InlineData("fabric:/*", "fabric:/orders", true)]
    [InlineData("fabric:/*", "fabric:/shop/orders", false)]
    [InlineData("fabric:/**", "fabric:/shop/orders", true)]
    [InlineData("fabric:/payments/**", "fabric:/payments/a/b", true)]
    [InlineData("fabric:/payments/**", "fabric:/payments", false)]
    [InlineData("fabric:/**/legacy", "fabric:/shop/orders/legacy", true)]
    [InlineData("fabric:/**/legacy", "fabric:/shop/legacy-ui", false)]
    [InlineData("fabric:/a.b", "fabric:/aXb", false)]
    [InlineData("fabric:/a+(b)", "fabric:/a+(b)", true)]
    public void Globs_match_within_or_across_segments(string pattern, string application, bool matches)
    {
        Assert.Equal(matches, View([pattern]).Includes(application));
    }

    [Fact]
    public void Exclude_removes_from_apps_and_alone_keeps_everything_else()
    {
        var payments = View(["fabric:/payments/**", "fabric:/shared-*"], ["fabric:/payments/legacy"]);
        Assert.True(payments.Includes("fabric:/payments/api"));
        Assert.True(payments.Includes("fabric:/shared-cache"));
        Assert.False(payments.Includes("fabric:/payments/legacy"));
        Assert.False(payments.Includes("fabric:/orders"));
        Assert.False(payments.Includes("fabric:/System"));

        var noSystem = View([], ["fabric:/System"]);
        Assert.False(noSystem.Includes("fabric:/system"));
        Assert.True(noSystem.Includes("fabric:/orders"));
        Assert.True(noSystem.Includes(""));

        Assert.True(ViewFilter.None.IsNone);
        Assert.True(ViewFilter.None.Includes("fabric:/anything"));
        Assert.Null(ViewFilter.None.Name);
    }

    [Theory]
    [InlineData("payments/*")]
    [InlineData("/payments")]
    [InlineData("*")]
    public void Patterns_must_be_fabric_names(string pattern)
    {
        var error = Assert.Throws<ViewConfigurationException>(() =>
            ViewFilter.From(new ViewDefinition("payments", ["fabric:/ok"], [pattern]), "~/.sfx/config.toml"));

        Assert.Contains("View 'payments' in ~/.sfx/config.toml", error.Message);
        Assert.Contains($"'{pattern}'", error.Message);
        Assert.Contains("fabric:/", error.Message);
    }

    [Fact]
    public void A_view_needs_a_pattern()
    {
        var error = Assert.Throws<ViewConfigurationException>(() => ViewFilter.From(new ViewDefinition("empty", [], [])));
        Assert.Contains("View 'empty' has no patterns", error.Message);
    }

    [Fact]
    public void Reads_view_tables_beside_contexts()
    {
        Write("""
            [contexts.prod-weu]
            endpoint = "prod-weu"
            view = " payments "

            [views.payments]
            apps = ["fabric:/payments", "fabric:/payments/*", "fabric:/shared-*"]
            owner = "team"

            [views.no-system]
            exclude = "fabric:/System"
            """);
        var store = ContextStore.Load(_path);

        Assert.Equal(["no-system", "payments"], store.Views.Select(v => v.Name));
        var payments = store.Views[1];
        Assert.Equal(["fabric:/payments", "fabric:/payments/*", "fabric:/shared-*"], payments.Apps);
        Assert.Empty(payments.Exclude);
        Assert.Equal(["fabric:/System"], store.Views[0].Exclude);

        var context = store.Find("prod-weu")!;
        Assert.Equal("payments", context.View);
        Assert.Empty(context.Options.Keys);
        _connector.Resolve(context);
    }

    [Fact]
    public void A_config_without_views_has_none()
    {
        Write("""
            [contexts.dev]
            endpoint = "localhost"
            """);
        var store = ContextStore.Load(_path);

        Assert.Empty(store.Views);
        Assert.Null(store.Find("dev")!.View);
        Assert.Empty(ViewCatalog.From(store).Views);
    }

    [Fact]
    public void Patterns_that_are_not_text_name_the_view()
    {
        Write("""
            [views.broken]
            apps = [1, 2]
            """);

        var error = Assert.Throws<ViewConfigurationException>(() => ContextStore.Load(_path).Views);
        Assert.Contains("View 'broken'", error.Message);
        Assert.Contains("apps", error.Message);
    }

    [Fact]
    public void Context_view_round_trips()
    {
        var store = ContextStore.Load(_path);
        store.Set(new ClusterContext("weu", "weu", [], new AuthOptions(), View: "payments"));
        store.Set(new ClusterContext("dev", "localhost", [], new AuthOptions(), View: " "));
        store.Save();

        var text = File.ReadAllText(_path);
        Assert.Contains("view = \"payments\"", text);
        Assert.Single(text.Split('\n'), line => line.StartsWith("view", StringComparison.Ordinal));

        var reloaded = ContextStore.Load(_path);
        Assert.Equal("payments", reloaded.Find("weu")!.View);
        Assert.Null(reloaded.Find("dev")!.View);
    }

    [Fact]
    public void Catalog_finds_views_by_name_whatever_the_case_and_says_which_there_are()
    {
        var catalog = new ViewCatalog(
            [new ViewDefinition("payments", ["fabric:/payments"], []), new ViewDefinition("no-system", [], ["fabric:/System"])],
            "~/.sfx/config.toml");

        Assert.Equal("payments", catalog.Find("Payments")?.Name);
        Assert.Null(catalog.Find("nope"));
        Assert.Null(catalog.Find(null));
        Assert.Equal("There's no view nope. Views: no-system, payments.", catalog.Unknown("nope"));
        Assert.Equal("No views are defined in ~/.sfx/config.toml.", ViewCatalog.Empty.Unknown("nope"));
    }

    [Fact]
    public void A_context_naming_an_undefined_view_opens_without_one_and_says_so()
    {
        var catalog = new ViewCatalog([new ViewDefinition("payments", ["fabric:/payments"], [])], "~/.sfx/config.toml");
        var context = new ClusterContext("weu", "weu", [], new AuthOptions(), View: "gone");

        Assert.Same(ViewFilter.None, catalog.DefaultFor(context, out var warning));
        Assert.Contains("'gone'", warning);
        Assert.Contains("weu", warning);

        Assert.Equal("payments", catalog.DefaultFor(context with { View = "PAYMENTS" }, out warning).Name);
        Assert.Null(warning);
        Assert.True(catalog.DefaultFor(context with { View = null }, out warning).IsNone);
        Assert.Null(warning);
    }

    [Fact]
    public void Command_line_view_overrides_the_contexts_and_must_be_defined()
    {
        var catalog = new ViewCatalog(
            [new ViewDefinition("payments", ["fabric:/payments"], []), new ViewDefinition("orders", ["fabric:/orders"], [])],
            "~/.sfx/config.toml");
        var context = new ClusterContext("weu", "weu", [], new AuthOptions(), View: "payments");

        Assert.Equal("orders", SfxHost.SelectView(CommandLine.Parse(["--view", "orders"], NoFlags), context, catalog, out _).Name);
        Assert.Equal("payments", SfxHost.SelectView(CommandLine.Parse([], NoFlags), context, catalog, out _).Name);
        var error = Assert.Throws<UsageException>(() => SfxHost.SelectView(CommandLine.Parse(["--view=nope"], NoFlags), context, catalog, out _));
        Assert.Equal("There's no view nope. Views: orders, payments.", error.Message);
    }

    [Fact]
    public void View_option_is_not_an_auth_option()
    {
        var options = CommandLine.Parse(["ui", "-c", "prod", "--view", "payments"], NoFlags);

        Assert.Equal("payments", options.View);
        Assert.Empty(options.AuthOptions.Keys);
    }

    [Theory]
    [InlineData("context", "use", "prod", "--view", "payments")]
    [InlineData("context", "set-view", "prod", "x", "--view", "payments")]
    [InlineData("localhost", "--none")]
    [InlineData("context", "add", "a", "b", "--none")]
    public void View_options_only_apply_where_they_mean_something(params string[] args)
    {
        Assert.Throws<UsageException>(() => CommandLine.Parse(args, NoFlags));
    }

    [Fact]
    public void Context_set_view_sets_and_clears_the_view()
    {
        Write("""
            [contexts.weu]
            endpoint = "weu.example.com"
            readonly = true

            [views.payments]
            apps = ["fabric:/payments/*"]
            """);

        Assert.Equal("Context 'weu' opens with view 'payments'.", Run("context", "set-view", "WEU", "Payments").Trim());
        var viewed = ContextStore.Load(_path).Find("weu")!;
        Assert.Equal("payments", viewed.View);
        Assert.True(viewed.ReadOnly);

        Assert.Equal("Context 'weu' opens without a view.", Run("context", "set-view", "weu", "--none").Trim());
        Assert.Null(ContextStore.Load(_path).Find("weu")!.View);

        var unknown = Assert.Throws<UsageException>(() => Run("context", "set-view", "weu", "nope"));
        Assert.Equal("There's no view nope. Views: payments.", unknown.Message);
        Assert.Throws<UsageException>(() => Run("context", "set-view", "weu"));
        Assert.Throws<UsageException>(() => Run("context", "set-view", "weu", "payments", "--none"));
        Assert.Throws<UsageException>(() => Run("context", "set-view", "nope", "payments"));
    }

    [Fact]
    public void Context_add_takes_a_defined_view()
    {
        Write("""
            [views.payments]
            apps = ["fabric:/payments/*"]
            """);

        Run("context", "add", "weu", "weu.example.com", "--view", "PAYMENTS");
        Assert.Equal("payments", ContextStore.Load(_path).Find("weu")!.View);
        Assert.Throws<UsageException>(() => Run("context", "add", "us", "us.example.com", "--view", "nope"));
    }

    [Fact]
    public void Context_list_and_show_have_the_view_once_a_context_has_one()
    {
        Write("""
            context = "dev"

            [contexts.dev]
            endpoint = "localhost"

            [contexts.weu]
            endpoint = "weu.example.com"
            view = "payments"

            [contexts.us]
            endpoint = "us.example.com"
            view = "gone"

            [views.payments]
            apps = ["fabric:/payments/*"]
            """);

        Assert.Equal("""
              NAME  AUTH          MODE      VIEW                ENDPOINT
            * dev   none          normal    -                   localhost
              us    none          normal    gone (not defined)  us.example.com
              weu   none          normal    payments            weu.example.com

            """, Run("context", "list").ReplaceLineEndings("\n"));
        Assert.Contains("view              payments", Run("context", "show", "weu"));
        Assert.DoesNotContain("view", Run("context", "show", "dev"));
    }

    [Fact]
    public void View_list_shows_patterns_and_the_contexts_that_use_each()
    {
        Write("""
            [contexts.weu]
            endpoint = "weu"
            view = "payments"

            [contexts.us]
            endpoint = "us"
            view = "gone"

            [views.payments]
            apps = ["fabric:/payments", "fabric:/payments/*"]
            exclude = ["fabric:/payments/legacy"]

            [views.no-system]
            exclude = ["fabric:/System"]
            """);

        var output = new StringWriter();
        ViewCommand.Run(["list"], ContextStore.Load(_path), output);

        Assert.Equal("""
            no-system
              exclude   fabric:/System
            payments
              apps      fabric:/payments, fabric:/payments/*
              exclude   fabric:/payments/legacy
              contexts  weu
            Context 'us' uses view 'gone', which isn't defined; it opens without a view.

            """, output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void View_list_without_views_says_how_to_add_one()
    {
        var output = new StringWriter();
        ViewCommand.Run([], ContextStore.Load(_path), output);

        Assert.StartsWith("No views in ", output.ToString());
        Assert.Throws<UsageException>(() => ViewCommand.Run(["add"], ContextStore.Load(_path), output));
    }

    [Fact]
    public void Upgrades_of_the_cluster_are_always_in_the_view()
    {
        var report = new UpgradeReport("RollingForwardInProgress", "Monitored", "2.0", "", "", TimeSpan.Zero, "", "", "", [], "", [], []);
        var payments = View(["fabric:/payments"]);

        Assert.True(new UpgradeRow("cluster", "cluster", null, "", report).InView(payments));
        Assert.True(new UpgradeRow("fabric:/payments", "application", "payments", "", report).InView(payments));
        Assert.False(new UpgradeRow("fabric:/orders", "application", "orders", "", report).InView(payments));
    }

    [Fact]
    public void Events_belong_to_the_application_their_entity_is_under()
    {
        var at = DateTime.UtcNow;
        var partition = new PartitionId(Guid.NewGuid());
        var unknownPartition = new PartitionId(Guid.NewGuid());
        string[] apps = ["fabric:/System", "fabric:/shop", "fabric:/shop/orders"];
        var services = new Dictionary<string, string> { [partition.ToString()] = "fabric:/shop/orders/Counter" };

        EventRow Row(string entity, string kind, FabricEvent e) => new(at, "Kind", "", entity, kind, "", e);
        var node = Row("_Node_0", "node", null!);
        var cluster = Row("cluster", "cluster", null!);
        var app = Row("fabric:/gone", "application", null!);
        var service = Row("fabric:/shop/orders/Counter", "service", null!);
        var orphan = Row("fabric:/elsewhere/Counter", "service", null!);
        var partitionEvent = Row(partition.ToString(), "partition", PartitionReport(partition, at));
        var replica = Row($"{partition} / 1", "replica",
            new ChaosReplicaRemovalScheduledEvent(Guid.NewGuid(), at, partition, new ReplicaId(1), Guid.NewGuid(), Guid.NewGuid(), "", "", false));
        var unknown = Row(unknownPartition.ToString(), "partition", PartitionReport(unknownPartition, at));

        Assert.Null(EventQueries.ApplicationOf(node, apps, services));
        Assert.Null(EventQueries.ApplicationOf(cluster, apps, services));
        Assert.Equal("fabric:/gone", EventQueries.ApplicationOf(app, apps, services));
        Assert.Equal("fabric:/shop/orders", EventQueries.ApplicationOf(service, apps, services));
        Assert.Equal("", EventQueries.ApplicationOf(orphan, apps, services));
        Assert.Equal("fabric:/shop/orders", EventQueries.ApplicationOf(partitionEvent, apps, services));
        Assert.Equal("fabric:/shop/orders", EventQueries.ApplicationOf(replica, apps, services));
        Assert.Equal("", EventQueries.ApplicationOf(unknown, apps, services));
    }

    private static PartitionNewHealthReportEvent PartitionReport(PartitionId partition, DateTime at) =>
        new(Guid.NewGuid(), at, partition, "source", "property", "Warning", 0, 0, "", false, at);

    [Fact]
    public void Cluster_and_node_events_are_always_in_the_view()
    {
        var at = DateTime.UtcNow;
        var orders = View(["fabric:/orders"]);
        EventRow Row(string kind, string? application) => new(at, "Kind", "", "x", kind, "", null!, application);

        Assert.True(EventQueries.InView(Row("cluster", null), orders));
        Assert.True(EventQueries.InView(Row("node", null), orders));
        Assert.True(EventQueries.InView(Row("service", "fabric:/orders"), orders));
        Assert.False(EventQueries.InView(Row("service", "fabric:/shop"), orders));
        Assert.False(EventQueries.InView(Row("partition", ""), orders));
        Assert.True(EventQueries.InView(Row("partition", ""), View([], ["fabric:/System"])));
        Assert.True(EventQueries.InView(Row("partition", "fabric:/shop"), ViewFilter.None));
    }

    [Fact]
    public void Overview_counts_what_the_view_hides()
    {
        ApplicationRow App(string name, HealthState health) => new(name, name, "T", "1", "Ready", health);
        var apps = new[]
        {
            App("fabric:/System", HealthState.Warning),
            App("fabric:/payments", HealthState.Error),
            App("fabric:/orders", HealthState.Error),
            App("fabric:/shop", HealthState.Ok),
            App("fabric:/stock", HealthState.Warning),
        };

        var summary = ApiOverview.Summary(ViewFilter.From(new ViewDefinition("payments", ["fabric:/payments"], [])), apps);

        Assert.Equal(new ViewSummaryDto("payments", 1, 4, 3), summary);
    }

    [Fact]
    public void Sessions_resolve_the_view_a_request_names()
    {
        var catalog = new ViewCatalog([new ViewDefinition("payments", ["fabric:/payments"], [])], "~/.sfx/config.toml");
        var context = new ClusterContext("test", "localhost", [], new AuthOptions());
        var cluster = new ClusterSession(context, new Uri("http://localhost:19080"), new ClusterQueries(null!));
        var session = new UiSession(cluster, new ActionLog(Path.Combine(Path.GetTempPath(), $"sfx-test-{Guid.NewGuid():N}.log")), "0.0.0", null, catalog, "payments");

        Assert.True(session.View(null).IsNone);
        Assert.True(session.View(" ").IsNone);
        Assert.Equal("payments", session.View("PAYMENTS").Name);
        var error = Assert.Throws<ActionException>(() => session.View("nope"));
        Assert.Equal(400, error.Status);
        Assert.Equal("There's no view nope. Views: payments.", error.Message);
        Assert.Equal("payments", session.DefaultView);
    }

    [Fact]
    public void Ui_host_starts_with_the_command_line_view_else_the_contexts_and_lists_context_views()
    {
        Write("""
            [contexts.dev]
            endpoint = "localhost"
            view = "payments"

            [contexts.weu]
            endpoint = "weu"
            view = "gone"

            [views.payments]
            apps = ["fabric:/payments/*"]

            [views.orders]
            apps = ["fabric:/orders"]
            """);
        var store = ContextStore.Load(_path);
        var catalog = ViewCatalog.From(store);
        UiHost Host(string? view) => new(
            new ClusterSession(store.Find("dev")!, new Uri("http://localhost:19080"), new ClusterQueries(null!)),
            store, _connector, new ActionLog(Path.Combine(Path.GetDirectoryName(_path)!, "actions.log")), "0.0.0", catalog, view);

        Assert.Equal("payments", Host(null).Current.DefaultView);
        Assert.Equal("orders", Host("orders").Current.DefaultView);
        Assert.Equal(["orders", "payments"], Host(null).Current.Views.Definitions.Select(v => v.Name));

        var contexts = Host(null).Contexts().Contexts;
        Assert.Equal("payments", contexts.Single(c => c.Name == "dev").View);
        Assert.Null(contexts.Single(c => c.Name == "weu").View);
    }

    [Fact]
    public async Task Lists_leave_out_rows_outside_the_view_and_say_how_many()
    {
        var view = ViewFilter.None;
        var list = new ResourceView<string>("applications", "apps",
                _ => Task.FromResult<IReadOnlyList<string>>(["fabric:/payments", "fabric:/orders", "fabric:/System"]), s => s)
            .InView(() => view, s => s == "fabric:/System" ? null : s)
            .Column("NAME", Cellar.Layout.Constraint.Fill(1), s => Cellar.Text.StyledText.Of(s));

        (await list.LoadAsync(default))();
        Assert.Equal(3, list.Table.Rows.Count);

        view = View(["fabric:/payments"]);
        (await list.LoadAsync(default))();
        Assert.Equal(["fabric:/System", "fabric:/payments"], list.Table.Rows.Order(StringComparer.Ordinal));
        Assert.Contains("1 hidden by the view", ((Cellar.Widgets.Panel)list.Widget).Title);
    }

    private void Write(string toml)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, toml);
    }

    private string Run(params string[] args)
    {
        var options = CommandLine.Parse(args, NoFlags);
        var output = new StringWriter();
        ContextCommand.Run(options.Arguments.Skip(1).ToList(), options, ContextStore.Load(_path), _connector, output);
        return output.ToString();
    }
}
