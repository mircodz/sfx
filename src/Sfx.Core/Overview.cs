using Microsoft.ServiceFabric.Client.Exceptions;
using Microsoft.ServiceFabric.Common;
using Microsoft.ServiceFabric.Common.Exceptions;

namespace Sfx.Core;

/// <summary>How many entities of one kind are in each health state.</summary>
/// <param name="Kind">Node, Application, Service, Partition or Replica.</param>
public sealed record HealthCount(string Kind, long Healthy, long Warning, long Error);

/// <summary>The cluster at a glance, in two calls however large it is.</summary>
/// <param name="Counts">Nodes, applications, services, partitions and replicas (system ones included), in that order.</param>
public sealed record ClusterSummary(string CodeVersion, HealthReport Health, IReadOnlyList<HealthCount> Counts);

/// <summary>A node, application or service that isn't healthy, and why in one line.</summary>
/// <param name="Kind">"node", "application" or "service".</param>
/// <param name="Id">The node's name, or the application's or service's id.</param>
/// <param name="Reason">What the cluster says is wrong; null when it doesn't say.</param>
/// <param name="ApplicationId">For a service, its application's id.</param>
public sealed record UnhealthyEntity(string Kind, string Name, string Id, HealthState Health, string? Reason, string? ApplicationId = null);

/// <summary>A search result, with the rows on the way to it: application, then service, then partition.</summary>
/// <param name="Kind">"node", "application", "service" or "partition".</param>
public sealed record SearchHit(
    string Kind,
    string Title,
    NodeRow? Node = null,
    ApplicationRow? Application = null,
    ServiceRow? Service = null,
    PartitionRow? Partition = null);

/// <summary>Cluster-wide views: the summary, everything unhealthy and why, search, and finding a partition by id.</summary>
public sealed class ClusterOverview(ClusterQueries queries)
{
    private static readonly EntityKind[] CountedKinds = [EntityKind.Node, EntityKind.Application, EntityKind.Service, EntityKind.Partition, EntityKind.Replica];
    private static readonly string[] KindOrder = ["node", "application", "service", "partition"];

    private readonly ClusterInspector _inspector = new(queries.Client);

    /// <summary>
    /// The cluster's version, health (its evaluations and unhealthy events) and entity counts per health state, from the
    /// cluster's health statistics rather than listing anything.
    /// </summary>
    public async Task<ClusterSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var version = _inspector.GetClusterVersionAsync(cancellationToken);
        var health = await _inspector.GetClusterHealthSummaryAsync(cancellationToken);
        return new ClusterSummary((await version).Version ?? "", HealthReport.From(health), Counts(health.HealthStatistics));
    }

    /// <summary>The counts of nodes, applications, services, partitions and replicas, in that order; other kinds are left out.</summary>
    public static IReadOnlyList<HealthCount> Counts(HealthStatistics? statistics)
    {
        var counts = statistics?.HealthStateCountList ?? [];
        return CountedKinds
            .SelectMany(kind => counts.Where(c => c.EntityKind == kind).Take(1).Select(c => new HealthCount(
                kind.ToString(),
                c.HealthStateCount?.OkCount ?? 0,
                c.HealthStateCount?.WarningCount ?? 0,
                c.HealthStateCount?.ErrorCount ?? 0)))
            .ToList();
    }

    /// <summary>
    /// Unhealthy nodes, applications and their unhealthy services, each with the reason the cluster gives; errors
    /// first. Only unhealthy entities are asked about, a call each.
    /// </summary>
    public async Task<IReadOnlyList<UnhealthyEntity>> GetUnhealthyAsync(CancellationToken cancellationToken)
    {
        var client = queries.Client;
        var health = await client.Cluster.GetClusterHealthAsync(
            nodesHealthStateFilter: HealthFilter.WarningOrError,
            applicationsHealthStateFilter: HealthFilter.WarningOrError,
            eventsHealthStateFilter: HealthFilter.None,
            excludeHealthStatistics: true,
            cancellationToken: cancellationToken);

        var nodes = (health.NodeHealthStates ?? []).Where(n => IsUnhealthy(n.AggregatedHealthState)).ToList();
        var apps = (health.ApplicationHealthStates ?? []).Where(a => IsUnhealthy(a.AggregatedHealthState)).ToList();

        var nodeEntities = Bounded.SelectAsync(nodes, async n =>
        {
            var name = n.Name?.ToString() ?? "";
            var reason = await ReasonAsync(() => HealthReasons.FetchAsync(client, "node", name, cancellationToken), cancellationToken);
            return (IReadOnlyList<UnhealthyEntity>)[new UnhealthyEntity("node", name, name, n.AggregatedHealthState ?? HealthState.Unknown, reason)];
        });
        var appEntities = Bounded.SelectAsync(apps, async a =>
        {
            var name = a.Name?.ToString() ?? "";
            var id = ClusterInspector.IdOf(name);
            ApplicationHealth appHealth;
            try
            {
                appHealth = await client.Applications.GetApplicationHealthAsync(
                    id,
                    eventsHealthStateFilter: HealthFilter.WarningOrError,
                    deployedApplicationsHealthStateFilter: HealthFilter.None,
                    servicesHealthStateFilter: HealthFilter.WarningOrError,
                    excludeHealthStatistics: true,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (IsQueryFailure(ex, cancellationToken))
            {
                return (IReadOnlyList<UnhealthyEntity>)[new UnhealthyEntity("application", name, id, a.AggregatedHealthState ?? HealthState.Unknown, null)];
            }

            var services = await Bounded.SelectAsync(
                (appHealth.ServiceHealthStates ?? []).Where(s => IsUnhealthy(s.AggregatedHealthState)).ToList(),
                async s =>
                {
                    var serviceName = s.ServiceName?.ToString() ?? "";
                    var serviceId = ClusterInspector.IdOf(serviceName);
                    var reason = await ReasonAsync(() => HealthReasons.FetchAsync(client, "service", serviceId, cancellationToken), cancellationToken);
                    return new UnhealthyEntity("service", serviceName, serviceId, s.AggregatedHealthState ?? HealthState.Unknown, reason, id);
                });
            return (IReadOnlyList<UnhealthyEntity>)
            [
                new UnhealthyEntity(
                    "application",
                    name,
                    id,
                    appHealth.AggregatedHealthState ?? a.AggregatedHealthState ?? HealthState.Unknown,
                    HealthReasons.From(appHealth)),
                .. services,
            ];
        });

        return (await nodeEntities).Concat(await appEntities)
            .SelectMany(e => e)
            .OrderByDescending(e => e.Health == HealthState.Error)
            .ThenBy(e => Array.IndexOf(KindOrder, e.Kind))
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Every node, application and service, to search with <see cref="Search"/>: a call for the nodes, the applications,
    /// and one per application for its services.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> GetSearchIndexAsync(CancellationToken cancellationToken)
    {
        var nodes = queries.GetNodesAsync(cancellationToken);
        var apps = await queries.GetApplicationsAsync(cancellationToken);
        var services = await Bounded.SelectAsync(apps, async app =>
        {
            try
            {
                return (App: app, Services: await queries.GetServicesAsync(app.Id, cancellationToken));
            }
            // Deleted since it was listed.
            catch (ServiceFabricException ex) when (ClusterQueries.IsGone(ex))
            {
                return (App: app, Services: (IReadOnlyList<ServiceRow>)[]);
            }
        });
        return (await nodes).Select(n => new SearchHit("node", n.Name, Node: n))
            .Concat(apps.Select(a => new SearchHit("application", a.Name, Application: a)))
            .Concat(services.SelectMany(entry => entry.Services.Select(s => new SearchHit("service", s.Name, Application: entry.App, Service: s))))
            .ToList();
    }

    /// <summary>
    /// The hits every word of <paramref name="query"/> matches, by name (with or without "fabric:/"), type, node type,
    /// address or id, case aside. Exact names come first, then names starting with the query, then the rest.
    /// </summary>
    public static IReadOnlyList<SearchHit> Search(IEnumerable<SearchHit> index, string query, int limit = 50)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            return [];
        }
        var whole = query.Trim();
        return index
            .Select(hit => (Hit: hit, Fields: Fields(hit).Where(f => f.Length > 0).ToList()))
            .Where(h => terms.All(term => h.Fields.Any(f => f.Contains(term, StringComparison.OrdinalIgnoreCase))))
            .Select(h => (h.Hit, Rank: Rank(h.Hit, whole)))
            .OrderBy(h => h.Rank)
            .ThenBy(h => Array.IndexOf(KindOrder, h.Hit.Kind))
            .ThenBy(h => h.Hit.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(h => h.Hit)
            .ToList();
    }

    private static IEnumerable<string> Fields(SearchHit hit)
    {
        yield return hit.Title;
        if (hit.Node is { } node)
        {
            yield return node.NodeType;
            yield return node.Address;
            yield return node.Id;
        }
        else if (hit.Service is { } service)
        {
            yield return service.TypeName;
            yield return service.Id;
        }
        else if (hit.Application is { } app)
        {
            yield return app.TypeName;
            yield return app.Id;
        }
        if (hit.Partition is { } partition)
        {
            yield return partition.Id.ToString();
        }
    }

    /// <summary>0 for the exact name, 1 for a name starting with the query, 2 for a name containing it, 3 for the rest.</summary>
    private static int Rank(SearchHit hit, string query)
    {
        var names = new[] { hit.Title, ShortName(hit.Title), LastPart(hit.Title) };
        return names.Any(n => n.Equals(query, StringComparison.OrdinalIgnoreCase)) ? 0
            : names.Any(n => n.StartsWith(query, StringComparison.OrdinalIgnoreCase)) ? 1
            : hit.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2
            : 3;
    }

    private static string ShortName(string name) => name.StartsWith("fabric:/", StringComparison.Ordinal) ? name["fabric:/".Length..] : name;

    private static string LastPart(string name) => name[(name.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Finds a partition by id, with its service and application: a few calls however large the cluster. Null when
    /// there's no such partition.
    /// </summary>
    public async Task<SearchHit?> FindPartitionAsync(Guid partitionId, CancellationToken cancellationToken)
    {
        var client = queries.Client;
        var id = new PartitionId(partitionId);
        ServiceNameInfo? serviceName;
        try
        {
            serviceName = await client.Partitions.GetServiceNameInfoAsync(id, cancellationToken: cancellationToken);
        }
        catch (ServiceFabricException ex) when (ClusterQueries.IsGone(ex))
        {
            return null;
        }
        if (serviceName?.Id is not { Length: > 0 } serviceId)
        {
            return null;
        }

        var partition = queries.GetPartitionAsync(id, cancellationToken);
        var appName = await client.Services.GetApplicationNameInfoAsync(serviceId, cancellationToken: cancellationToken);
        var applicationId = appName?.Id ?? ClusterQueries.SystemApplicationId;
        var application = queries.GetApplicationAsync(applicationId, cancellationToken);
        var service = await queries.GetServiceAsync(applicationId, serviceId, cancellationToken);
        return await partition is { } row
            ? new SearchHit("partition", row.Id.ToString(), Application: await application, Service: service, Partition: row)
            : null;
    }

    private static bool IsUnhealthy(HealthState? state) => state is HealthState.Warning or HealthState.Error;

    private static bool IsQueryFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is ServiceFabricException or ServiceFabricRequestException && !cancellationToken.IsCancellationRequested;

    /// <summary>The reason, or null when asking failed: one entity the cluster won't talk about doesn't spoil the list.</summary>
    private static async Task<string?> ReasonAsync(Func<Task<string?>> fetch, CancellationToken cancellationToken)
    {
        try
        {
            return await fetch();
        }
        catch (Exception ex) when (IsQueryFailure(ex, cancellationToken))
        {
            return null;
        }
    }
}
