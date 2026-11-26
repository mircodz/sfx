using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Tests;

public class ReasonTests
{
    private static HealthEvent Event(HealthState state, string description) =>
        new(sourceId: "Watchdog", property: "Disk", healthState: state, description: description);

    private static HealthEvaluationWrapper Wrap(HealthEvaluation evaluation) => new(evaluation);

    private static NodeHealth Node(IEnumerable<HealthEvaluationWrapper>? evaluations, params HealthEvent[] events) =>
        new(HealthState.Warning, events, evaluations, null, new NodeName("_Node_1"));

    [Fact]
    public void Uses_the_unhealthy_event_behind_an_event_evaluation()
    {
        var health = Node([Wrap(new EventHealthEvaluation(
            HealthState.Warning,
            "Unhealthy event: SourceId='Watchdog'.",
            false,
            Event(HealthState.Warning, "Disk is 95% full.")))]);

        Assert.Equal("Disk is 95% full.", HealthReasons.From(health));
    }

    [Fact]
    public void Falls_back_to_the_evaluation_when_the_event_says_nothing()
    {
        var health = Node([Wrap(new EventHealthEvaluation(HealthState.Error, "Unhealthy event: SourceId='Watchdog'.", false, Event(HealthState.Error, "  ")))]);

        Assert.Equal("Unhealthy event: SourceId='Watchdog'.", HealthReasons.From(health));
    }

    [Fact]
    public void Follows_nested_evaluations_to_the_first_leaf()
    {
        var health = Node(
            [
                Wrap(new NodesHealthEvaluation(
                    HealthState.Error,
                    "1 of 3 nodes are unhealthy.",
                    0,
                    3,
                    [
                        Wrap(new NodeHealthEvaluation(
                            HealthState.Error,
                            "Node _Node_1 is unhealthy.",
                            new NodeName("_Node_1"),
                            [
                                Wrap(new EventHealthEvaluation(HealthState.Error, "Unhealthy event.", false, Event(HealthState.Error, "Lease lost."))),
                            ])),
                        Wrap(new NodeHealthEvaluation(HealthState.Warning, "Node _Node_2 is unhealthy.", new NodeName("_Node_2"), [])),
                    ])),
                Wrap(new EventHealthEvaluation(HealthState.Warning, "Second.", false, Event(HealthState.Warning, "Not this one."))),
            ]);

        Assert.Equal("Lease lost.", HealthReasons.From(health));
    }

    [Fact]
    public void Without_evaluations_uses_the_first_event_that_is_not_ok()
    {
        var health = Node(null, Event(HealthState.Ok, "Fine."), Event(HealthState.Warning, "Certificate expires soon."), Event(HealthState.Error, "Later."));

        Assert.Equal("Certificate expires soon.", HealthReasons.From(health));
    }

    [Fact]
    public void Has_no_reason_when_nothing_explains_it()
    {
        Assert.Null(HealthReasons.From(Node(null, Event(HealthState.Ok, "Fine."))));
    }

    [Fact]
    public void Collapses_whitespace_and_cuts_long_text()
    {
        Assert.Equal("Disk is full on D:.", HealthReasons.Clean("  Disk is\r\n  full\ton D:. \n"));

        var cut = HealthReasons.Clean(string.Concat(Enumerable.Repeat("word ", 100)))!;
        Assert.Equal(HealthReasons.MaxLength, cut.Length);
        Assert.EndsWith("…", cut);
        Assert.Equal(new string('x', 160), HealthReasons.Clean(new string('x', 160)));
    }
}
