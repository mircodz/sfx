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

internal sealed class FakeTerminal : ITerminal
{
    public ColorDepth ColorDepth => ColorDepth.TrueColor;
    public Size Size { get; set; } = new(120, 40);
    public event Action<Size>? Resized { add { } remove { } }
    public void EnterRawMode() { }
    public void LeaveRawMode() { }
    public void Write(string ansi) { }
    public int ReadInput(Span<byte> buffer, TimeSpan timeout) => 0;
    public void Dispose() { }
}

/// <summary>The shell's keys and commands, as its keymap declares them.</summary>
public sealed class ShellKeymapTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sfx-shell-").FullName;
    private readonly App _app = new(new FakeTerminal());
    private readonly Keymap _keymap;

    public ShellKeymapTests()
    {
        var store = ContextStore.Load(Path.Combine(_dir, "config.toml"));
        var context = new ClusterContext("dev", "http://127.0.0.1:1", [], new AuthOptions());
        var session = new ClusterSession(context, new Uri("http://127.0.0.1:1"), new ClusterQueries(null!));
        _keymap = new Shell(_app, session, new ClusterConnector(BuiltInAuth.Providers), store, TimeSpan.Zero).Keymap;
    }

    public void Dispose()
    {
        _app.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static KeyEvent Char(char c, KeyModifiers modifiers = KeyModifiers.None) => new(Key.Char, new Rune(c), modifiers);

    private static KeyEvent Named(Key key) => new(key, default, KeyModifiers.None);

    private string? IdFor(KeyEvent key) => _keymap.Find(key)?.Id;

    [Fact]
    public void Keeps_the_keys_sfx_always_had()
    {
        Assert.Equal("command", IdFor(Char(':')));
        Assert.Equal("filter", IdFor(Char('/')));
        Assert.Equal("palette", IdFor(Char('p', KeyModifiers.Control)));
        Assert.Equal("help", IdFor(Char('?')));
        Assert.Equal("refresh", IdFor(Char('r')));
        Assert.Equal("copy", IdFor(Char('y')));
        Assert.Equal("quit", IdFor(Char('q')));
        Assert.Equal("quit", IdFor(Char('c', KeyModifiers.Control)));
        Assert.Equal("back", IdFor(Named(Key.Backspace)));
        Assert.Equal("back", IdFor(Char('h')));
        Assert.Equal("escape", IdFor(Named(Key.Escape)));
        Assert.Equal("go.contexts", IdFor(Char('c')));
        Assert.Equal("open", IdFor(Named(Key.Enter)));
        Assert.Equal("open", IdFor(Char('l')));
        Assert.Equal("down", IdFor(Char('j')));
        Assert.Equal("last", IdFor(Char('G', KeyModifiers.Shift)));
    }

    [Theory]
    [InlineData("nodes", "go.nodes", null)]
    [InlineData("no", "go.nodes", null)]
    [InlineData("apps", "go.applications", null)]
    [InlineData("events 6h", "go.events", "6h")]
    [InlineData("  CONTEXT   prod west ", "go.contexts", "prod west")]
    [InlineData("ctx", "go.contexts", null)]
    [InlineData("refresh off", "refresh", "off")]
    [InlineData("q", "quit", null)]
    [InlineData(":quit", "quit", null)]
    [InlineData("power", "power", null)]
    [InlineData("group type", "group", "type")]
    public void Commands_match_any_alias_and_pass_the_rest_as_argument(string text, string id, string? argument)
    {
        var match = _keymap.FindCommand(text);

        Assert.NotNull(match);
        Assert.Equal(id, match.Value.Action.Id);
        Assert.Equal(argument, match.Value.Argument);
    }

    [Theory]
    [InlineData("view")]
    [InlineData("nope")]
    [InlineData("   ")]
    public void Unknown_commands_match_nothing(string text)
    {
        Assert.Null(_keymap.FindCommand(text));
    }

    [Fact]
    public void Help_lists_every_global_action_under_a_group()
    {
        var groups = _keymap.Global.Declared.GroupBy(a => a.Group).Select(g => g.Key).ToList();

        Assert.Equal(["General", "Movement", "Navigation", "Pages", "Rows"], groups.Order(StringComparer.Ordinal));
        Assert.All(_keymap.Global.Declared, a => Assert.False(string.IsNullOrWhiteSpace(a.Title)));
        Assert.Equal(_keymap.Global.Declared.Count, _keymap.Global.Declared.Select(a => a.Id).Distinct().Count());
    }
}

/// <summary>A table page's own keys: they win over the shell's, and follow the row, the marks and the context's mode.</summary>
public class PageKeymapTests
{
    private readonly FakeHost _host = new();

    private static NodeRow Node(string name, string status) => new(name, "host", "NodeType0", status, HealthState.Ok, "1", "fd:/1", false, "10.0", null);

    private static KeyEvent Char(char c, KeyModifiers modifiers = KeyModifiers.None) => new(Key.Char, new Rune(c), modifiers);

    private async Task<(ResourceView<NodeRow> Page, Keymap Keymap, List<string> Ran)> Page(params NodeRow[] rows)
    {
        var views = new Views(new ClusterQueries(null!), _host);
        var page = new ResourceView<NodeRow>("nodes", "nodes", _ => Task.FromResult<IReadOnlyList<NodeRow>>(rows), n => n.Name)
            .Acting(views.NodeActions(), _host)
            .Describing(_ => new DescribeView("node", "x", []))
            .Column("NAME", Constraint.Fill(1), n => Plain(n.Name), n => n.Name)
            .Column("TYPE", Constraint.Fill(1), n => Plain(n.NodeType), n => n.NodeType);
        (await page.LoadAsync(CancellationToken.None))();
        page.Widget.Render(new Cellar.Rendering.Surface(60, 10), new Rect(0, 0, 60, 10));

        List<string> ran = [];
        var keymap = new Keymap();
        keymap.Global.Add(new AppAction("refresh", "Refresh", () => ran.Add("refresh")) { Keys = ["r"] });
        keymap.Global.Add(new AppAction("describe-anything", "Describe", () => ran.Add("global d")) { Keys = ["d"] });
        keymap.AddScope("page", () => page.Actions);
        return (page, keymap, ran);
    }

    [Fact]
    public async Task Row_actions_follow_the_selected_row()
    {
        var (_, keymap, _) = await Page(Node("_Node_1", "Up"));

        Assert.Equal("row.restart-node", keymap.Find(Char('r', KeyModifiers.Control))?.Id);
        Assert.Equal("row.describe", keymap.Find(Char('d'))?.Id);
        Assert.Equal("refresh", keymap.Find(Char('r'))?.Id);
        // Activating is for a disabled node.
        Assert.DoesNotContain(keymap.Actions, a => a.Id == "row.activate-node" && a.IsAvailable);
    }

    [Fact]
    public async Task Read_only_contexts_offer_no_action_that_changes_the_cluster()
    {
        _host.ReadOnly = true;
        var (page, keymap, _) = await Page(Node("_Node_1", "Up"));

        Assert.Null(keymap.Find(Char('r', KeyModifiers.Control)));
        Assert.Equal("row.describe", keymap.Find(Char('d'))?.Id);
        Assert.DoesNotContain(page.Actions, a => a.Id.StartsWith("row.restart", StringComparison.Ordinal) && a.IsAvailable);
    }

    [Fact]
    public async Task Power_actions_wait_for_power()
    {
        var (page, _, _) = await Page(Node("_Node_1", "Down"));
        bool Offered() => page.Actions.Any(a => a.Id == "row.remove-node-state" && a.IsAvailable);

        Assert.False(Offered());
        _host.Power = true;
        Assert.True(Offered());
    }

    [Fact]
    public async Task With_rows_marked_an_action_runs_on_those_it_applies_to_after_one_confirmation()
    {
        var up = Node("_Node_1", "Up");
        var down = Node("_Node_2", "Down");
        var (page, keymap, _) = await Page(up, down, Node("_Node_3", "Up"));

        page.Table.SetMarked(up, true);
        page.Table.SetMarked(down, true);
        Assert.Equal(2, page.MarkCount);
        Assert.Contains("2 marked", ((Panel)page.Widget).Title);

        keymap.Find(Char('r', KeyModifiers.Control))!.Run(null);

        var (title, operations, note) = Assert.Single(_host.Batches);
        Assert.Equal("Restart node: 1 marked row", title);
        Assert.Equal("Restart _Node_1", Assert.Single(operations).Title);
        Assert.Contains("1 marked row is left out", note);
        // Confirmed: the marks are cleared.
        Assert.Equal(0, page.MarkCount);
    }

    [Fact]
    public async Task Grouping_by_a_column_says_so_in_the_title_and_unknown_columns_are_refused()
    {
        var (page, _, _) = await Page(Node("_Node_1", "Up"), Node("_Node_2", "Up") with { NodeType = "Other" });

        Assert.True(page.GroupBy("type"));
        Assert.Equal(2, page.Table.Groups.Count);
        Assert.Contains("by type", ((Panel)page.Widget).Title);
        Assert.False(page.GroupBy("nope"));
        Assert.True(page.GroupBy(null));
        Assert.DoesNotContain("by type", ((Panel)page.Widget).Title);
    }

    [Fact]
    public async Task Filtering_keeps_matching_rows_and_counts_them_in_the_title()
    {
        var (page, _, _) = await Page(Node("_Node_1", "Up"), Node("_Node_2", "Up"));

        page.Filter = "node_2";

        Assert.Equal("_Node_2", Assert.Single(page.Table.FilteredRows).Name);
        Assert.Equal(" nodes [1] /node_2 ", ((Panel)page.Widget).Title);
    }
}
