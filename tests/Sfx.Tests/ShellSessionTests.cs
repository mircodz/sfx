using Cellar.Widgets;
using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Tui;

namespace Sfx.Tests;

/// <summary>What carries over when the shell switches context, and what doesn't.</summary>
public sealed class ShellSessionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sfx-session-").FullName;
    private readonly App _app = new(new FakeTerminal());
    private readonly ContextStore _store;

    public ShellSessionTests()
    {
        _store = ContextStore.Load(Path.Combine(_dir, "config.toml"));
        _store.Set(new ClusterContext("dev", "http://127.0.0.1:1", [], new AuthOptions()));
        _store.Set(new ClusterContext("prod", "http://127.0.0.1:2", [], new AuthOptions(), Power: true));
        _store.Save();
    }

    public void Dispose()
    {
        _app.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static ClusterSession Session(ClusterContext context) =>
        new(context, new Uri(context.Endpoint), new ClusterQueries(null!));

    private Shell Shell(bool readOnly = false)
    {
        var context = _store.Find("dev")! with { ReadOnly = readOnly };
        return new Shell(_app, Session(context), new ClusterConnector(BuiltInAuth.Providers), _store, TimeSpan.Zero, readOnly);
    }

    [Fact]
    public void Readonly_on_the_command_line_holds_for_every_context_switched_to()
    {
        var prod = Shell(readOnly: true).Resolve(_store.Find("prod")!).Context;

        Assert.True(prod.ReadOnly);
        Assert.False(prod.Power);
    }

    [Fact]
    public void Without_readonly_a_switch_takes_the_contexts_own_mode()
    {
        var shell = Shell();

        Assert.False(shell.Resolve(_store.Find("dev")!).Context.ReadOnly);
        Assert.True(shell.Resolve(_store.Find("prod")!).Context.Power);
    }

    [Fact]
    public async Task The_contexts_page_shows_every_context_read_only_under_readonly()
    {
        var page = Views.Contexts(_store, () => "dev", _ => { }, Shell(readOnly: true).Resolve);

        (await page.LoadAsync(CancellationToken.None))();

        Assert.All(page.Table.Rows, row => Assert.True(row.Context.ReadOnly));
    }
}
