using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.ServiceFabric.Common;
using Microsoft.ServiceFabric.Common.Exceptions;

namespace Sfx.Core;

/// <summary>One event from the cluster's event store.</summary>
/// <param name="Kind">The event type without its "Event" suffix, e.g. "NodeDown".</param>
/// <param name="Entity">What it's about: a node, application, service or partition, or "cluster".</param>
/// <param name="EntityKind">What kind of entity that is: "cluster", "node", "application", "service", "partition" or "replica".</param>
/// <param name="Details">The event's own fields, as "Name=value" pairs.</param>
/// <param name="Application">
/// The name of the application the entity belongs to, once <see cref="EventQueries.WithApplicationsAsync"/> has looked
/// it up: empty when it can't be told (e.g. deleted since), null for cluster and node events, which belong to none.
/// </param>
public sealed record EventRow(
    DateTime TimeUtc, string Kind, string Category, string Entity, string EntityKind, string Details, FabricEvent Event, string? Application = null);

public sealed class EventQueries(ClusterQueries queries)
{
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ssZ";
    private const int Parallelism = 8;

    // A partition never moves to another service, so what's found stays good for the session.
    private readonly ConcurrentDictionary<string, string> _partitionServices = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Sets each event's <see cref="EventRow.Application"/>: from the name for application and service events, and for
    /// partition and replica events by asking which service the partition belongs to, once per partition per session.
    /// </summary>
    public async Task<IReadOnlyList<EventRow>> WithApplicationsAsync(IReadOnlyList<EventRow> events, CancellationToken cancellationToken)
    {
        if (events.All(e => e.EntityKind is "cluster" or "node"))
        {
            return events;
        }

        var applications = (await queries.GetApplicationsAsync(cancellationToken)).Select(a => a.Name).ToList();
        var unknown = events
            .Select(PartitionOf)
            .OfType<PartitionId>()
            .DistinctBy(id => id.ToString())
            .Where(id => !_partitionServices.ContainsKey(id.ToString()))
            .ToList();

        using var gate = new SemaphoreSlim(Parallelism);
        await Task.WhenAll(unknown.Select(async id =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var info = await queries.Client.Partitions.GetServiceNameInfoAsync(id, cancellationToken: cancellationToken);
                _partitionServices[id.ToString()] = info?.Name?.ToString() ?? "";
            }
            catch (ServiceFabricException ex) when (ClusterQueries.IsGone(ex))
            {
                _partitionServices[id.ToString()] = "";
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Asked again next time; until then the event's application can't be told.
            }
            finally
            {
                gate.Release();
            }
        }));

        return events.Select(e => e with { Application = ApplicationOf(e, applications, _partitionServices) }).ToList();
    }

    /// <summary>
    /// The application an event's entity belongs to: null for the cluster and nodes; for a service or partition, the
    /// application whose name its service's name is under; empty when that can't be told.
    /// </summary>
    /// <param name="partitionServices">Service names by partition id.</param>
    public static string? ApplicationOf(EventRow e, IReadOnlyList<string> applications, IReadOnlyDictionary<string, string> partitionServices)
    {
        switch (e.EntityKind)
        {
            case "cluster" or "node":
                return null;
            case "application":
                return e.Entity;
            case "service":
                return Owner(e.Entity, applications);
            default:
                return PartitionOf(e) is { } partition && partitionServices.TryGetValue(partition.ToString(), out var service)
                    ? Owner(service, applications)
                    : "";
        }
    }

    /// <summary>Whether the event shows in the view: cluster and node events always do; the rest through their application.</summary>
    public static bool InView(EventRow e, ViewFilter view) =>
        view.IsNone || e.EntityKind is "cluster" or "node" || view.Includes(e.Application ?? "");

    /// <summary>The application a service name is under (the longest one, should names nest); empty when none is.</summary>
    private static string Owner(string service, IReadOnlyList<string> applications) =>
        applications
            .Where(app => service.StartsWith(app + "/", StringComparison.OrdinalIgnoreCase) || service.Equals(app, StringComparison.OrdinalIgnoreCase))
            .MaxBy(app => app.Length) ?? "";

    private static PartitionId? PartitionOf(EventRow e) => e.Event switch
    {
        PartitionEvent p => p.PartitionId,
        ReplicaEvent r => r.PartitionId,
        _ => null,
    };

    private const string NoEventStore =
        "This cluster doesn't run the event store (EventStoreService); Azure clusters do by default, standalone ones can enable it.";

    private static async Task<IEnumerable<FabricEvent>> Events<T>(Task<IEnumerable<T>> events) where T : FabricEvent =>
        (await events ?? []).Cast<FabricEvent>();

    /// <summary>
    /// Whether an event is about <paramref name="entity"/> (a node name, an application or service name, or a partition id)
    /// or something under it: "fabric:/orders" covers "fabric:/orders/Counter", a partition covers its replicas.
    /// </summary>
    public static bool IsAbout(EventRow e, string entity) =>
        e.Entity == entity
        || e.Entity.StartsWith(entity + "/", StringComparison.Ordinal)
        || (e.EntityKind == "replica" && e.Entity.StartsWith(entity + ReplicaSeparator, StringComparison.Ordinal));

    private const string ReplicaSeparator = " / ";

    internal static EventRow ToRow(FabricEvent e)
    {
        var kind = e.GetType().Name is var name && name.EndsWith("Event", StringComparison.Ordinal) ? name[..^5] : e.GetType().Name;
        var (entity, entityKind) = e switch
        {
            NodeEvent n => (n.NodeName?.ToString() ?? "", "node"),
            ApplicationEvent a => (ClusterInspector.NameOf(a.ApplicationId ?? ""), "application"),
            ServiceEvent s => (ClusterInspector.NameOf(s.ServiceId ?? ""), "service"),
            PartitionEvent p => (p.PartitionId?.ToString() ?? "", "partition"),
            ReplicaEvent r => ($"{r.PartitionId}{ReplicaSeparator}{r.ReplicaId}", "replica"),
            _ => ("cluster", "cluster"),
        };
        var details = string.Join("  ", Inspect.Fields(e)
            .Where(f => !CommonFields.Contains(f.Name) && f.Value.Length > 0)
            .Select(f => $"{f.Name}={f.Value}"));
        return new EventRow(e.TimeStamp ?? DateTime.MinValue, kind, e.Category ?? "", entity, entityKind, details, e);
    }
}
