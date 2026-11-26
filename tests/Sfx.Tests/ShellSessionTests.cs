using Cellar.Widgets;
using Microsoft.ServiceFabric.Common;
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

    private static readonly ServiceRow Service = new("App~Svc", "fabric:/App/Svc", "SvcType", "Stateful", "Active", HealthState.Ok);

    private static readonly PartitionRow Partition = new(PartitionId.Parse(Guid.NewGuid().ToString()), "Stateful", "Singleton", "Ready", HealthState.Ok, "3/2");

    private string LogPath => Path.Combine(_dir, "actions.log");

    private ClusterSession Session(string name, FakeCluster? cluster = null)
    {
        var context = _store.Find(name)!;
        return new(context, new Uri(context.Endpoint), new ClusterQueries(cluster?.Client!));
    }

    private Shell Shell(bool readOnly = false, FakeCluster? cluster = null)
    {
        var session = Session("dev", cluster);
        session = session with { Context = session.Context with { ReadOnly = readOnly } };
        return new Shell(_app, session, new ClusterConnector(BuiltInAuth.Providers), _store, TimeSpan.Zero, readOnly);
    }

    /// <summary>Starts resetting the partition's load and returns its confirmation, which has opened.</summary>
    private ConfirmDialog ResetLoad(Shell shell)
    {
        shell.Views.PartitionActions(Service).Single(a => a.Title == "Reset partition load").Run(Partition);
        return Assert.IsType<ConfirmDialog>(_app.TopOverlay?.Content);
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(25);
        }
        Assert.True(done());
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

    [Fact]
    public void The_confirmation_says_which_cluster_it_is_for()
    {
        Assert.StartsWith("dev: Reset the load of partition", ResetLoad(Shell(cluster: new FakeCluster())).Headline);
    }

    [Fact]
    public void Confirming_after_a_switch_does_nothing_and_says_so()
    {
        var dev = new FakeCluster().OnCall("ResetPartitionLoadAsync");
        var prod = new FakeCluster().OnCall("ResetPartitionLoadAsync");
        var shell = Shell(cluster: dev);
        var dialog = ResetLoad(shell);

        shell.Use(Session("prod", prod));
        Assert.True(dialog.Confirm());

        Assert.Equal("You switched to prod; this was for dev. Nothing was done.", shell.Message);
        Assert.Empty(dev.CallsTo("ResetPartitionLoadAsync"));
        Assert.Empty(prod.CallsTo("ResetPartitionLoadAsync"));
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public async Task An_action_still_running_at_a_switch_is_logged_against_its_own_cluster()
    {
        using var gate = new ManualResetEventSlim();
        var dev = new FakeCluster().OnCall("ResetPartitionLoadAsync", _ => gate.Wait(TimeSpan.FromSeconds(5)));
        var shell = Shell(cluster: dev);
        Assert.True(ResetLoad(shell).Confirm());
        await Until(() => dev.CallsTo("ResetPartitionLoadAsync").Count == 1);

        shell.Use(Session("prod", new FakeCluster()));
        gate.Set();
        await Until(() => File.Exists(LogPath) && File.ReadAllText(LogPath).Length > 0);

        var line = Assert.Single(File.ReadAllLines(LogPath));
        Assert.Contains("dev", line);
        Assert.Contains("127.0.0.1:1", line);
        Assert.DoesNotContain("prod", line);
    }
}
