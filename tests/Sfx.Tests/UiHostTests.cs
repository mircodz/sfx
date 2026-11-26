using Microsoft.AspNetCore.Http;
using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Web;

namespace Sfx.Tests;

public class UiHostTests
{
    private static (UiHost Host, ContextStore Store) Host(string toml, string current = "dev", bool readOnly = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sfx-host-{Guid.NewGuid():N}.toml");
        File.WriteAllText(path, toml);
        var store = ContextStore.Load(path);
        var context = store.Find(current) ?? new ClusterContext(current, "localhost", [], new AuthOptions());
        var session = new ClusterSession(context, new Uri("http://localhost:19080"), new ClusterQueries(null!));
        var log = new ActionLog(Path.Combine(Path.GetTempPath(), $"sfx-host-{Guid.NewGuid():N}.log"));
        return (new UiHost(session, store, new ClusterConnector(BuiltInAuth.Providers), log, "0.0.0", readOnly), store);
    }

    private const string Config = """
        [contexts.dev]
        endpoint = "localhost"

        [contexts.prod-weu]
        endpoint = "prod-weu:19080"
        folder = "prod/europe"

        [folders.prod]
        label = "PROD"
        color = "danger"
        readonly = true
        """;

    [Fact]
    public void Lists_contexts_with_folder_rules_applied()
    {
        var (host, _) = Host(Config);
        var contexts = host.Contexts();

        Assert.Equal("dev", contexts.Current);
        Assert.Equal(host.Current.Id, contexts.Session);
        var prod = contexts.Contexts.Single(c => c.Name == "prod-weu");
        Assert.Equal("prod/europe", prod.Folder);
        Assert.Equal("PROD", prod.Label);
        Assert.Equal("danger", prod.Color);
        Assert.True(prod.ReadOnly);
        Assert.Contains(contexts.Folders, f => f.Path == "prod" && f.ReadOnly);
        Assert.Null(contexts.Connecting);
    }

    [Fact]
    public void Command_line_readonly_holds_for_every_context_switched_to()
    {
        var (host, store) = Host(Config.Replace("endpoint = \"localhost\"", "endpoint = \"localhost\"\npower = true"), readOnly: true);

        var dev = host.Resolve(store.Find("dev")!).Context;
        Assert.True(dev.ReadOnly);
        Assert.False(dev.Power);
        Assert.All(host.Contexts().Contexts, c => Assert.True(c.ReadOnly));
    }

    [Fact]
    public void Without_readonly_a_context_connects_as_saved()
    {
        var (host, store) = Host(Config.Replace("endpoint = \"localhost\"", "endpoint = \"localhost\"\npower = true"));

        var dev = host.Resolve(store.Find("dev")!).Context;
        Assert.False(dev.ReadOnly);
        Assert.True(dev.Power);
        Assert.True(host.Resolve(store.Find("prod-weu")!).Context.ReadOnly);
    }

    private static UiSession Connected(string context) => new(
        new ClusterSession(new ClusterContext(context, "localhost", [], new AuthOptions()), new Uri("http://localhost:19080"), new ClusterQueries(null!)),
        new ActionLog(Path.Combine(Path.GetTempPath(), $"sfx-host-{Guid.NewGuid():N}.log")),
        "0.0.0");

    private static HttpContext From(string? session)
    {
        var context = new DefaultHttpContext();
        if (session is not null)
        {
            context.Request.Headers[WebServer.SessionHeader] = session;
        }
        return context;
    }

    [Fact]
    public void Changes_need_the_connection_the_tab_shows()
    {
        var (host, _) = Host(Config);
        var dev = host.Current;

        host.Expect(dev, From(dev.Id));

        var prod = Connected("prod-weu");
        host.Use(prod);
        var ex = Assert.Throws<ActionException>(() => host.Expect(host.Current, From(dev.Id)));
        Assert.Equal(409, ex.Status);
        Assert.Equal("This tab is on dev but sfx ui switched to prod-weu. Reload to continue.", ex.Message);
        Assert.Equal(409, Assert.Throws<ActionException>(() => host.Expect(host.Current, From(null))).Status);
        host.Expect(host.Current, From(prod.Id));
    }

    [Fact]
    public void Reconnecting_to_the_same_context_is_a_new_connection()
    {
        var (host, _) = Host(Config);
        var before = host.Current;
        host.Use(Connected("dev"));

        var ex = Assert.Throws<ActionException>(() => host.Expect(host.Current, From(before.Id)));
        Assert.Equal("sfx ui reconnected to dev since this tab loaded it. Reload to continue.", ex.Message);
    }

    [Fact]
    public void Lists_a_command_line_cluster_that_is_not_saved()
    {
        var (host, _) = Host(Config, current: "adhoc:19080");

        var adhoc = host.Contexts().Contexts.First();
        Assert.Equal("adhoc:19080", adhoc.Name);
        Assert.False(adhoc.Saved);
    }

    [Fact]
    public void Refuses_a_context_that_is_not_saved()
    {
        var (host, _) = Host(Config);

        var ex = Assert.Throws<ActionException>(() => host.Switch("nope"));
        Assert.Equal(404, ex.Status);
    }

    [Fact]
    public async Task A_failed_switch_keeps_the_current_cluster_and_says_why()
    {
        // Nothing listens on this port, so connecting fails fast.
        var (host, _) = Host(Config.Replace("prod-weu:19080", "127.0.0.1:1"));

        host.Switch("prod-weu");
        for (var i = 0; i < 100 && host.Contexts().Connecting?.Error is null; i++)
        {
            await Task.Delay(100);
        }

        var contexts = host.Contexts();
        Assert.Equal("dev", contexts.Current);
        Assert.Equal("dev", host.Current.Context.Name);
        Assert.Contains("Still on dev", contexts.Connecting?.Error);

        host.Dismiss();
        Assert.Null(host.Contexts().Connecting);
    }
}
