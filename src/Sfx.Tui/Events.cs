using Cellar.Layout;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Sfx.Core;
using static Sfx.Tui.Styles;

namespace Sfx.Tui;

/// <summary>The cluster's event store: what happened to nodes, applications, services and partitions.</summary>
public sealed partial class Views
{
    public static readonly TimeSpan DefaultEventWindow = TimeSpan.FromHours(1);

    private EventQueries? _events;

    private EventQueries EventQueries => _events ??= new EventQueries(queries);

    /// <summary>Events of the last <paramref name="window"/>, newest first; with a view, cluster and node events and those of its applications.</summary>
    public IResourceView Events(TimeSpan window) =>
        new ResourceView<EventRow>("events", $"events {RefreshInterval.Format(window)}", ct => LoadEventsAsync(window, ct),
                e => e.Event.EventInstanceId?.ToString() ?? $"{e.TimeUtc:O}{e.Kind}{e.Entity}")
            .InView(View, e => e.EntityKind is "cluster" or "node" ? null : e.Application ?? "")
            .Describing(DescribeEvent)
            .Column("TIME", Constraint.Length(19), e => Plain(When(new DateTimeOffset(e.TimeUtc, TimeSpan.Zero))), e => e.TimeUtc)
            .Column("KIND", Constraint.Fill(2), e => EventKind(e.Kind), e => e.Kind)
            .Column("ENTITY", Constraint.Fill(2), e => Plain(e.Entity), e => e.Entity)
            .Column("DETAILS", Constraint.Fill(5), e => Muted(e.Details), e => e.Details)
            .Sorting("TIME", SortState.Descending);

    /// <summary>The events, with the application each is about when there's a view to filter them by.</summary>
    private async Task<IReadOnlyList<EventRow>> LoadEventsAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var events = await EventQueries.GetEventsAsync(window, cancellationToken);
        return View().IsNone ? events : await EventQueries.WithApplicationsAsync(events, cancellationToken);
    }

    public static IResourceView DescribeEvent(EventRow e) =>
        new DescribeView("event", e.Kind,
        [
            Tab("Fields", _ => Task.FromResult(Lines.Fields(Inspect.Fields(e.Event)))),
            Tab("Raw", _ => Task.FromResult(Lines.Json(e.Event))),
        ]);

    /// <summary>Events about something going wrong stand out; the rest read as plain text.</summary>
    private static StyledText EventKind(string kind) =>
        new[] { "Down", "Failed", "Error", "Aborted", "Lost", "Rollback", "Expired", "Exited", "Closed" }.Any(word => kind.Contains(word, StringComparison.Ordinal))
            ? Colored(kind, Theme.Current.Warning)
            : Plain(kind);
}
