using System.Text.Json.Nodes;
using Cellar.Primitives;
using Cellar.Rendering;
using Cellar.Terminal;
using Cellar.Text;
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

public class TextViewTests
{
    private static TextView View(params string[] lines)
    {
        var view = new TextView();
        view.SetLines(lines.Select(line => StyledText.Of(line)).ToList());
        view.Render(new Surface(40, 3), new Rect(0, 0, 40, 3));
        return view;
    }

    // Leaves out the last column, where the scrollbar is.
    private static string Row(Surface surface, int y) =>
        string.Concat(Enumerable.Range(0, surface.Width - 1).Select(x => surface.Get(x, y).Grapheme ?? " ")).TrimEnd();

    [Fact]
    public void Find_counts_matches_and_steps_through_them_wrapping_around()
    {
        var view = View("alpha beta", "gamma", "Beta delta beta");

        view.Find("beta");
        Assert.Equal(3, view.MatchCount);
        Assert.Equal(1, view.CurrentMatch);

        view.NextMatch(1);
        view.NextMatch(1);
        Assert.Equal(3, view.CurrentMatch);
        view.NextMatch(1);
        Assert.Equal(1, view.CurrentMatch);
        view.NextMatch(-1);
        Assert.Equal(3, view.CurrentMatch);

        view.Find("");
        Assert.Equal(0, view.MatchCount);
    }

    [Fact]
    public void Scrolls_with_keys_and_jumps_to_a_match_out_of_view()
    {
        var view = View(Enumerable.Range(0, 20).Select(i => $"line {i}").ToArray());
        var surface = new Surface(40, 3);

        view.OnEvent(new KeyEvent(Key.Down, default, KeyModifiers.None));
        view.Render(surface, new Rect(0, 0, 40, 3));
        Assert.Equal("line 1", Row(surface, 0));

        view.Find("line 15");
        surface.Clear(Style.Default);
        view.Render(surface, new Rect(0, 0, 40, 3));
        Assert.Contains("line 15", Enumerable.Range(0, 3).Select(y => Row(surface, y)));

        view.OnEvent(new KeyEvent(Key.Home, default, KeyModifiers.None));
        surface.Clear(Style.Default);
        view.Render(surface, new Rect(0, 0, 40, 3));
        Assert.Equal("line 0", Row(surface, 0));
    }

    [Fact]
    public void Text_joins_the_plain_lines_for_copying()
    {
        Assert.Equal("a\nb", View("a", "b").Text);
    }
}
