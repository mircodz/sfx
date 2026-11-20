using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Web;

namespace Sfx.Tests;

public class UiHostTests
{
    private static (UiHost Host, ContextStore Store) Host(string toml, string current = "dev")
    {
        var path = Path.Combine(Path.GetTempPath(), $"sfx-host-{Guid.NewGuid():N}.toml");
        File.WriteAllText(path, toml);
        var store = ContextStore.Load(path);
        var context = store.Find(current) ?? new ClusterContext(current, "localhost", [], new AuthOptions());
        var session = new ClusterSession(context, new Uri("http://localhost:19080"), new ClusterQueries(null!));
        var log = new ActionLog(Path.Combine(Path.GetTempPath(), $"sfx-host-{Guid.NewGuid():N}.log"));
        return (new UiHost(session, store, new ClusterConnector(BuiltInAuth.Providers), log, "0.0.0"), store);
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
        var prod = contexts.Contexts.Single(c => c.Name == "prod-weu");
        Assert.Equal("prod/europe", prod.Folder);
        Assert.Equal("PROD", prod.Label);
        Assert.Equal("danger", prod.Color);
        Assert.True(prod.ReadOnly);
        Assert.Contains(contexts.Folders, f => f.Path == "prod" && f.ReadOnly);
        Assert.Null(contexts.Connecting);
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
