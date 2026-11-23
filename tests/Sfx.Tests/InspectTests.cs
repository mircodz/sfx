using System.Text;
using System.Text.Json.Nodes;
using Cellar.Primitives;
using Cellar.Rendering;
using Cellar.Terminal;
using Cellar.Text;
using Cellar.Widgets;
using Microsoft.ServiceFabric.Common;
using Sfx.Core;
using Sfx.Tui;

namespace Sfx.Tests;

public class InspectTests
{
    private static HealthEvent Event(string source, string property, HealthState state, string description) =>
        new(source, property, state, null, description, "1", false, null, false, null, new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc), null, null, null);

    private static NodeHealth UnhealthyNode()
    {
        var unhealthy = Event("Watchdog", "Disk", HealthState.Error, "Disk is full.\nFree some space.");
        var byEvent = new EventHealthEvaluation(HealthState.Error, "Error event: Watchdog/Disk.", false, unhealthy);
        var byNode = new NodeHealthEvaluation(HealthState.Error, "Node _Node_1 is unhealthy.", new NodeName("_Node_1"), [new HealthEvaluationWrapper(byEvent)]);
        var nodes = new NodesHealthEvaluation(HealthState.Error, "1 of 5 nodes is unhealthy.", 0, 5, [new HealthEvaluationWrapper(byNode)]);

        return new NodeHealth(
            HealthState.Error,
            [Event("System.FM", "State", HealthState.Ok, "Fabric node is up."), unhealthy],
            [new HealthEvaluationWrapper(nodes)],
            null!,
            new NodeName("_Node_1"));
    }

    [Fact]
    public void HealthReport_flattens_nested_evaluations_depth_first()
    {
        var report = HealthReport.From(UnhealthyNode());

        Assert.Equal(HealthState.Error, report.State);
        Assert.Equal(2, report.Events.Count);
        Assert.Equal(
            [(0, "Nodes"), (1, "Node"), (2, "Event")],
            report.Evaluations.Select(e => (e.Depth, e.Kind)));
    }

    [Fact]
    public void Health_lines_explain_the_cause_before_listing_events()
    {
        var text = string.Join('\n', Lines.Health(HealthReport.From(UnhealthyNode())).Select(line => line.PlainText));

        Assert.Contains("State  Error", text);
        var why = text.IndexOf("Why it's unhealthy", StringComparison.Ordinal);
        var events = text.IndexOf("Events (2)", StringComparison.Ordinal);
        Assert.True(why >= 0 && why < events);
        // Errors sort before Ok, and multi-line descriptions stay readable.
        Assert.True(text.IndexOf("Watchdog  Disk", events, StringComparison.Ordinal) < text.IndexOf("System.FM", events, StringComparison.Ordinal));
        Assert.Contains("    Free some space.", text);
    }

    [Fact]
    public void Json_uses_runtime_types_names_and_enums()
    {
        var json = JsonNode.Parse(Inspect.Json(UnhealthyNode()))!;

        Assert.Equal("_Node_1", json["Name"]!.GetValue<string>());
        Assert.Equal("Error", json["AggregatedHealthState"]!.GetValue<string>());
        // Declared as the base HealthEvaluation, but the derived type's properties are kept.
        var nodes = json["UnhealthyEvaluations"]![0]!["HealthEvaluation"]!;
        Assert.Equal(5, nodes["TotalCount"]!.GetValue<long>());
        Assert.Equal("_Node_1", nodes["UnhealthyEvaluations"]![0]!["HealthEvaluation"]!["NodeName"]!.GetValue<string>());
    }

    [Fact]
    public void Fields_list_scalars_and_compact_nested_values()
    {
        var fields = Inspect.Fields(new { Name = "fabric:/App", Count = 3, Tags = new[] { "a", "b" }, Missing = (string?)null }, "Count");

        Assert.Equal([new Field("Name", "fabric:/App"), new Field("Tags", "[\"a\",\"b\"]")], fields);
    }

    [Fact]
    public void PrettyJson_indents_a_compact_document()
    {
        Assert.Equal("{\n  \"a\": [\n    1\n  ]\n}", Inspect.PrettyJson("{\"a\":[1]}").ReplaceLineEndings("\n"));
    }
}

public class DescribeViewTests
{
    private static async Task<DescribeView> Loaded(params string[][] tabs)
    {
        var view = new DescribeView("node", "_Node_1",
            tabs.Select((lines, i) => new DescribeTab($"Tab {i}",
                _ => Task.FromResult<IReadOnlyList<StyledText>>(lines.Select(line => StyledText.Of(line)).ToList()))).ToList());
        (await view.LoadAsync(CancellationToken.None))();
        view.Widget.Render(new Surface(40, 6), new Rect(0, 0, 40, 6));
        return view;
    }

    private static string Title(DescribeView view) => ((Panel)view.Widget).Title!;

    private static KeyEvent Char(char c) => new(Key.Char, new Rune(c), KeyModifiers.None);

    [Fact]
    public async Task Searching_counts_the_matches_in_the_title_and_n_steps_through_them_wrapping_around()
    {
        var view = await Loaded(["alpha beta", "gamma", "Beta delta beta"]);
        var next = view.Actions.Single(a => a.Id == "reading.next-match");
        var previous = view.Actions.Single(a => a.Id == "reading.previous-match");

        view.Filter = "beta";
        Assert.Equal(" node _Node_1 /beta (1/3) ", Title(view));

        next.Run(null);
        next.Run(null);
        Assert.Contains("(3/3)", Title(view));
        next.Run(null);
        Assert.Contains("(1/3)", Title(view));
        previous.Run(null);
        Assert.Contains("(3/3)", Title(view));

        view.Filter = "nothing";
        Assert.Contains("/nothing (no match)", Title(view));
        view.Filter = "";
        Assert.Equal(" node _Node_1 ", Title(view));
    }

    [Fact]
    public async Task The_search_follows_to_the_next_tab()
    {
        var view = await Loaded(["beta"], ["no", "beta beta"]);
        view.Filter = "beta";

        view.Actions.Single(a => a.Id == "reading.next-tab").Run(null);
        (await view.LoadAsync(CancellationToken.None))();

        Assert.Equal(2, view.Active.MatchCount);
        Assert.Contains("(1/2)", Title(view));
    }

    [Fact]
    public async Task Reading_keys_scroll_but_back_open_copy_and_escape_are_left_to_the_shell()
    {
        var view = await Loaded(Enumerable.Range(0, 20).Select(i => $"line {i}").ToArray());

        Assert.True(view.Widget.OnEvent(Char('j')));
        Assert.Equal(1, view.Active.TopLine);
        foreach (var c in "hly")
        {
            Assert.False(view.Widget.OnEvent(Char(c)));
        }
        Assert.False(view.Widget.OnEvent(new KeyEvent(Key.Escape, default, KeyModifiers.None)));
    }

    [Fact]
    public async Task A_refresh_keeps_the_place_being_read()
    {
        var view = await Loaded(Enumerable.Range(0, 20).Select(i => $"line {i}").ToArray());
        view.Active.ScrollToLine(7);

        (await view.LoadAsync(CancellationToken.None))();

        Assert.Equal(7, view.Active.TopLine);
    }

    [Fact]
    public async Task Line_numbers_and_wrapping_toggle_and_copy_takes_the_plain_text()
    {
        var view = await Loaded(["a", "b"]);

        view.Actions.Single(a => a.Id == "reading.line-numbers").Run(null);
        view.Actions.Single(a => a.Id == "reading.wrap").Run(null);

        Assert.True(view.Active.ShowLineNumbers);
        Assert.True(view.Active.Wrap);
        Assert.Equal("a\nb", view.CopyText);
    }
}
