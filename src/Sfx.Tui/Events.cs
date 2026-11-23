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

    /// <summary>Events of the last <paramref name="window"/>, newest first.</summary>
    public IResourceView Events(TimeSpan window) =>
        new ResourceView<EventRow>("events", $"events {RefreshInterval.Format(window)}",
                ct => EventQueries.GetEventsAsync(window, ct), e => e.Event.EventInstanceId?.ToString() ?? $"{e.TimeUtc:O}{e.Kind}{e.Entity}")
            {
                Empty = $"no events in the last {RefreshInterval.Format(window)}; :events 6h looks further back",
            }
            .Describing(DescribeEvent)
            .Column("TIME", Constraint.Length(19), e => Plain(When(new DateTimeOffset(e.TimeUtc, TimeSpan.Zero))), e => e.TimeUtc)
            .Column("KIND", Constraint.Fill(2), e => EventKind(e.Kind), e => e.Kind)
            .Column("ENTITY", Constraint.Fill(2), e => Plain(e.Entity), e => e.Entity)
            .Column("DETAILS", Constraint.Fill(5), e => Muted(e.Details), e => e.Details)
            .Sorting("TIME", SortState.Descending);

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
