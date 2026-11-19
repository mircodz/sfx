using System.Globalization;
using Microsoft.ServiceFabric.Common;

namespace Sfx.Core;

/// <summary>One event from the cluster's event store.</summary>
/// <param name="Kind">The event type without its "Event" suffix, e.g. "NodeDown".</param>
/// <param name="Entity">What it's about: a node, application, service or partition, or "cluster".</param>
/// <param name="Details">The event's own fields, as "Name=value" pairs.</param>
public sealed record EventRow(DateTime TimeUtc, string Kind, string Category, string Entity, string Details, FabricEvent Event);

public sealed class EventQueries(ClusterQueries queries)
{
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ssZ";

    private static readonly HashSet<string> CommonFields =
    [
        nameof(FabricEvent.EventInstanceId), nameof(FabricEvent.Category), nameof(FabricEvent.TimeStamp),
        nameof(FabricEvent.HasCorrelatedEvents), nameof(FabricEvent.Kind),
        "NodeName", "ApplicationId", "ServiceId", "PartitionId", "ReplicaId",
    ];

    /// <summary>Events of the cluster, nodes, applications, services and partitions in the last <paramref name="window"/>, newest first.</summary>
    public async Task<IReadOnlyList<EventRow>> GetEventsAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        if (!await queries.HasSystemServiceAsync("EventStoreService", cancellationToken))
        {
            throw new InvalidOperationException(NoEventStore);
        }

        var end = DateTime.UtcNow;
        var start = (end - window).ToString(TimeFormat, CultureInfo.InvariantCulture);
        var until = end.ToString(TimeFormat, CultureInfo.InvariantCulture);
        var store = queries.Client.EventsStore;

        var lists = await Task.WhenAll(
            Events(store.GetClusterEventListAsync(start, until, cancellationToken: cancellationToken)),
            Events(store.GetNodesEventListAsync(start, until, cancellationToken: cancellationToken)),
            Events(store.GetApplicationsEventListAsync(start, until, cancellationToken: cancellationToken)),
            Events(store.GetServicesEventListAsync(start, until, cancellationToken: cancellationToken)),
            Events(store.GetPartitionsEventListAsync(start, until, cancellationToken: cancellationToken)));

        return lists.SelectMany(events => events)
            .Select(ToRow)
            .OrderByDescending(e => e.TimeUtc)
            .ToList();
    }

    private const string NoEventStore =
        "This cluster doesn't run the event store (EventStoreService); Azure clusters do by default, standalone ones can enable it.";

    private static async Task<IEnumerable<FabricEvent>> Events<T>(Task<IEnumerable<T>> events) where T : FabricEvent =>
        (await events ?? []).Cast<FabricEvent>();

    private static EventRow ToRow(FabricEvent e)
    {
        var kind = e.GetType().Name is var name && name.EndsWith("Event", StringComparison.Ordinal) ? name[..^5] : e.GetType().Name;
        var entity = e switch
        {
            NodeEvent n => n.NodeName?.ToString() ?? "",
            ApplicationEvent a => "fabric:/" + a.ApplicationId,
            ServiceEvent s => "fabric:/" + s.ServiceId?.Replace('~', '/'),
            PartitionEvent p => p.PartitionId?.ToString() ?? "",
            ReplicaEvent r => $"{r.PartitionId} / {r.ReplicaId}",
            _ => "cluster",
        };
        var details = string.Join("  ", Inspect.Fields(e)
            .Where(f => !CommonFields.Contains(f.Name) && f.Value.Length > 0)
            .Select(f => $"{f.Name}={f.Value}"));
        return new EventRow(e.TimeStamp ?? DateTime.MinValue, kind, e.Category ?? "", entity, details, e);
    }
}
