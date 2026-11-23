using System.Globalization;
using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Web;

/// <summary>The read endpoints under /api, plus actions and the power toggle. Ids are Service Fabric's (e.g. "orders~Counter").</summary>
internal static class Api
{
    private const int Parallelism = 8;

    /// <summary>
    /// Every handler takes the current <see cref="UiSession"/> as a parameter (resolved per request from
    /// <see cref="UiHost"/>), so switching context takes effect on the next request.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app, UiHost host)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/contexts", () => host.Contexts());
        api.MapPost("/contexts/{name}/connect", (string name) =>
        {
            host.Switch(name);
            return Results.Accepted();
        });
        api.MapPost("/contexts/dismiss", () => host.Dismiss());

        api.MapGet("/session", (UiSession s, CancellationToken ct) => SessionInfoAsync(s, ct));

        api.MapPost("/session/power", async (UiSession s, PowerRequest request, CancellationToken ct) =>
        {
            if (s.ReadOnly)
            {
                throw new ActionException("This context is read-only.", StatusCodes.Status403Forbidden);
            }
            s.Power = request.On;
            return await SessionInfoAsync(s, ct);
        });

        api.MapGet("/cluster", async (UiSession s, CancellationToken ct) =>
        {
            var version = s.Inspector.GetClusterVersionAsync(ct);
            var health = s.Inspector.GetClusterHealthAsync(ct);
            var target = new Dictionary<string, string> { ["name"] = s.Context.Name, ["azureManaged"] = await s.AzureManagedTargetAsync(ct) };
            return new ClusterDto(
                s.Context.Name, s.Cluster.Endpoint.ToString(), (await version).Version ?? "", Dto.Health(await health),
                WebActions.For(s, "cluster", target));
        });

        api.MapGet("/cluster/manifest", async (UiSession s, CancellationToken ct) => Text(await s.Inspector.GetClusterManifestAsync(ct)));

        api.MapGet("/nodes", async (UiSession s, CancellationToken ct) => (await s.Queries.GetNodesAsync(ct)).Select(n => Node(s, n)).ToList());

        api.MapGet("/nodes/{name}", async (UiSession s, string name, CancellationToken ct) =>
        {
            var nodes = s.Queries.GetNodesAsync(ct);
            var node = (await nodes).FirstOrDefault(n => n.Name == name)
                ?? throw new ActionException($"There's no node {name} on this cluster.", StatusCodes.Status404NotFound);
            var health = s.Inspector.GetNodeHealthAsync(name, ct);
            var load = s.Inspector.GetNodeLoadAsync(name, ct);
            var capacities = s.GetDeclaredCapacitiesAsync(ct);
            var replicas = DeployedReplicasAsync(s, node, ct);

            // "__NamingServiceReplicaCount__" and the like are the cluster's own bookkeeping, not load anyone set.
            var declared = await capacities;
            var metrics = ((await load).NodeLoadMetricInformation ?? [])
                .Where(m => m.Name is { } n && !n.StartsWith("__", StringComparison.Ordinal))
                .Select(m => new MetricDto(m.Name ?? "", m.CurrentNodeLoad ?? Number(m.NodeLoad), Capacity(m.NodeCapacity, declared.Declares(node.NodeType, m.Name!))))
                .OrderBy(m => m.Capacity is null)
                .ThenBy(m => m.Name)
                .ToList();
            return new NodeDetailDto(Node(s, node), Dto.Health(await health), metrics, await replicas);
        });

        api.MapGet("/applications", async (UiSession s, CancellationToken ct) => (await s.Queries.GetApplicationsAsync(ct)).Select(Dto.Application).ToList());

        api.MapGet("/applications/{appId}", async (UiSession s, string appId, CancellationToken ct) =>
        {
            if (appId == ClusterQueries.SystemApplicationId)
            {
                var services = s.Queries.GetServicesAsync(appId, ct);
                var health = s.Inspector.GetApplicationHealthAsync(appId, ct);
                var system = (await s.Queries.GetApplicationsAsync(ct)).First(a => a.Id == appId);
                return new ApplicationDetailDto(
                    Dto.Application(system) with { Health = Dto.Health((await health).State) },
                    Dto.Health(await health), [], [], null, (await services).Select(Dto.Service).ToList(), [], new DefaultServicesDto(false, []),
                    UpgradeDefaultsDto.For("<ApplicationManifest />"));
            }

            // Asked first, so an unknown id is a 404 that says so rather than whichever query fails first.
            var info = await s.Inspector.GetApplicationAsync(appId, ct);
            var appServices = s.Queries.GetServicesAsync(appId, ct);
            var appHealth = s.Inspector.GetApplicationHealthAsync(appId, ct);
            var versions = s.Inspector.GetApplicationTypeVersionsAsync(info.TypeName, ct);
            var upgrade = s.Inspector.GetApplicationUpgradeAsync(appId, ct);
            var manifest = s.Inspector.GetApplicationManifestAsync(info.TypeName, info.TypeVersion, ct);
            var defaultServicesUpgrade = s.DefaultServicesUpgradeEnabledAsync(ct);
            // Only for the create-service form, so a read-only session doesn't ask.
            var serviceTypes = s.ReadOnly ? Task.FromResult("") : ServiceTypesTargetAsync(s, info.TypeName, info.TypeVersion, ct);

            var current = info.Parameters ?? new Dictionary<string, string>();
            var types = await versions;
            var defaults = types.FirstOrDefault(t => t.Version == info.TypeVersion)?.DefaultParameterList ?? new Dictionary<string, string>();
            var parameters = current.Keys.Union(defaults.Keys)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(k => new ParameterDto(
                    k, current.TryGetValue(k, out var v) ? v : defaults[k], defaults.TryGetValue(k, out var d) ? d : null, current.ContainsKey(k)))
                .ToList();

            var row = new ApplicationRow(info.Id, info.Name.ToString(), info.TypeName, info.TypeVersion, info.Status?.ToString() ?? "", info.HealthState ?? HealthState.Unknown);
            var report = UpgradeReport.From(await upgrade);
            var upgradeRow = new UpgradeRow(row.Name, "application", row.Id, report.Target != row.TypeVersion ? row.TypeVersion : "", report);

            var target = new Dictionary<string, string>
            {
                ["applicationId"] = row.Id, ["name"] = row.Name, ["typeName"] = row.TypeName, ["version"] = row.TypeVersion,
                ["serviceTypes"] = await serviceTypes,
            };
            return new ApplicationDetailDto(
                Dto.Application(row),
                Dto.Health(await appHealth),
                parameters,
                types.OrderByDescending(t => t.Version, VersionComparer.Instance)
                    .Select(t => new VersionDto(t.Version, t.DefaultParameterList ?? new Dictionary<string, string>()))
                    .ToList(),
                upgradeRow.StartedUtc is null ? null : Dto.Upgrade(upgradeRow, UpgradeActions(s, upgradeRow)),
                (await appServices).Select(Dto.Service).ToList(),
                WebActions.For(s, "application", target),
                new DefaultServicesDto(await defaultServicesUpgrade, DefaultServices.ParameterUses(await manifest)),
                UpgradeDefaultsDto.For(await manifest));
        });

        api.MapGet("/applications/{appId}/manifest", async (UiSession s, string appId, CancellationToken ct) =>
        {
            RequireUserApplication(appId);
            var info = await s.Inspector.GetApplicationAsync(appId, ct);
            return Text(await s.Inspector.GetApplicationManifestAsync(info.TypeName, info.TypeVersion, ct));
        });

        api.MapGet("/applications/{appId}/services/{serviceId}", async (UiSession s, string appId, string serviceId, CancellationToken ct) =>
        {
            // Asked first, so an unknown id is a 404 that says so rather than whichever query fails first.
            var service = Service(await s.Inspector.GetServiceAsync(appId, serviceId, ct));
            var description = s.Inspector.GetServiceDescriptionAsync(serviceId, ct);
            var health = s.Inspector.GetServiceHealthAsync(serviceId, ct);
            var partitions = s.Queries.GetPartitionsAsync(serviceId, ct);

            var d = await description;
            return new ServiceDetailDto(
                service, appId, Dto.Health(await health), Describe(d),
                (await partitions).Select(Dto.Partition).ToList(),
                WebActions.For(s, "service", ServiceTarget(service, appId, d)));
        });

        api.MapGet("/applications/{appId}/services/{serviceId}/manifest", async (UiSession s, string appId, string serviceId, CancellationToken ct) =>
        {
            RequireUserApplication(appId);
            var app = s.Inspector.GetApplicationAsync(appId, ct);
            var service = s.Inspector.GetServiceAsync(appId, serviceId, ct);
            var a = await app;
            return Text(await s.Inspector.GetServiceManifestAsync(a.TypeName, a.TypeVersion, (await service).TypeName, ct));
        });

        api.MapGet("/applications/{appId}/services/{serviceId}/partitions/{partitionId}",
            async (UiSession s, string appId, string serviceId, string partitionId, CancellationToken ct) =>
            {
                var id = new PartitionId(Guid.TryParse(partitionId, out var guid) ? guid : throw new ActionException("Not a partition id."));
                var svc = Service(await s.Inspector.GetServiceAsync(appId, serviceId, ct));
                var partition = (await s.Queries.GetPartitionsAsync(serviceId, ct)).FirstOrDefault(p => p.Id.ToString() == id.ToString())
                    ?? throw new ActionException($"There's no partition {partitionId} in {svc.Name}.", StatusCodes.Status404NotFound);
                var health = s.Inspector.GetPartitionHealthAsync(id, ct);
                var load = s.Inspector.GetPartitionLoadAsync(id, ct);
                var replicas = s.Queries.GetReplicasAsync(id, ct);

                var l = await load;
                var metrics = (l.PrimaryLoadMetricReports ?? []).Select(m => new MetricDto($"{m.Name} (primary)", Number(m.CurrentValue ?? m.Value), null))
                    .Concat((l.SecondaryLoadMetricReports ?? []).Select(m => new MetricDto($"{m.Name} (secondary)", Number(m.CurrentValue ?? m.Value), null)))
                    .ToList();

                var replicaDtos = (await replicas).Select(r => new ReplicaDto(
                    r.Id, r.NodeName, r.Role, r.Status, Dto.Health(r.Health), r.Address,
                    WebActions.For(s, "replica", new Dictionary<string, string>
                    {
                        ["partitionId"] = partition.Id.ToString(), ["replicaId"] = r.Id, ["node"] = r.NodeName, ["role"] = r.Role,
                        ["serviceId"] = svc.Id, ["serviceKind"] = svc.Kind,
                    }))).ToList();

                var target = new Dictionary<string, string> { ["partitionId"] = partition.Id.ToString(), ["serviceId"] = svc.Id };
                return new PartitionDetailDto(
                    Dto.Partition(partition), svc, Dto.Health(await health), metrics, replicaDtos, WebActions.For(s, "partition", target));
            });

        api.MapGet("/replicas", (UiSession s, CancellationToken ct) => s.GetClusterReplicasAsync(ct));

        api.MapGet("/applications/{appId}/services/{serviceId}/replicas", (UiSession s, string appId, string serviceId, CancellationToken ct) =>
            s.GetServiceReplicasAsync(appId, serviceId, ct));

        api.MapGet("/upgrades", async (UiSession s, CancellationToken ct) =>
            (await s.GetUpgradesAsync(ct))
                .OrderBy(u => u.Urgency)
                .ThenByDescending(u => u.StartedUtc)
                .Select(u => Dto.Upgrade(u, UpgradeActions(s, u)))
                .ToList());

        // ?entity= narrows to one node, application ("fabric:/orders"), service or partition id, and what's under it.
        api.MapGet("/events", async (UiSession s, int? hours, string? entity, CancellationToken ct) =>
        {
            try
            {
                var events = await s.Events.GetEventsAsync(TimeSpan.FromHours(Math.Clamp(hours ?? 1, 1, 24 * 7)), ct);
                return new EventsDto(events
                    .Where(e => entity is null || EventQueries.IsAbout(e, entity))
                    .Take(500)
                    .Select(e => new EventDto(e.TimeUtc, e.Kind, e.EntityKind, e.Category, e.Entity, e.Details))
                    .ToList(), null);
            }
            catch (InvalidOperationException ex)
            {
                return new EventsDto([], ex.Message);
            }
        });

        // The entity as the cluster describes it, for the Details tab.
        api.MapGet("/nodes/{name}/raw", async (UiSession s, string name, CancellationToken ct) => Raw(await s.Inspector.GetNodeAsync(name, ct)));
        api.MapGet("/applications/{appId}/raw", async (UiSession s, string appId, CancellationToken ct) => Raw(await s.Inspector.GetApplicationAsync(appId, ct)));
        api.MapGet("/applications/{appId}/services/{serviceId}/raw", async (UiSession s, string appId, string serviceId, CancellationToken ct) =>
        {
            var info = s.Inspector.GetServiceAsync(appId, serviceId, ct);
            var description = s.Inspector.GetServiceDescriptionAsync(serviceId, ct);
            return Raw(new { Info = await info, Description = await description });
        });
        api.MapGet("/applications/{appId}/services/{serviceId}/partitions/{partitionId}/raw",
            async (UiSession s, string appId, string serviceId, string partitionId, CancellationToken ct) =>
                Raw(await s.Inspector.GetPartitionAsync(new PartitionId(Guid.TryParse(partitionId, out var g) ? g : throw new ActionException("Not a partition id.")), ct)));

        api.MapGet("/activity", (UiSession s) => s.ReadLog(200)
            .Reverse()
            .Select(e => new ActivityDto(e.Time, e.User, e.Context, e.Action, e.Outcome, e.Succeeded))
            .ToList());

        api.MapPost("/actions/{id}", (UiSession s, string id, ActionRequest request, CancellationToken ct) => WebActions.RunAsync(s, id, request, ct));
        ApiClusterUpgrades.Map(api);
        ApiOperations.Map(api);
        ApiReasons.Map(api);
        ApiPackages.Map(api);
        ApiRepairs.Map(api);
        ApiLoad.Map(api);
        ApiOverview.Map(api);
        ApiLifecycle.Map(api);
        ApiSearch.Map(api);
        ApiImageStore.Map(api);
    }

    public sealed record PowerRequest(bool On);

    private static async Task<SessionDto> SessionInfoAsync(UiSession s, CancellationToken ct)
    {
        var imageStore = s.ImageStoreBrowsableAsync(ct);
        var services = await s.GetSystemServicesAsync(ct);
        return new(s.Context.Name, s.Cluster.Endpoint.ToString(), s.ReadOnly, s.Power, s.Version, services.EventStore, s.Context.Folder, s.Label, s.Color,
            services.RepairManager, services.FaultAnalysis, await imageStore);
    }

    /// <summary>
    /// The replicas on a node, system services' included (as application "fabric:/System", which the node's application
    /// list leaves out). A node that isn't up may not answer; it then shows none rather than failing the page.
    /// </summary>
    private static async Task<IReadOnlyList<DeployedReplicaDto>> DeployedReplicasAsync(UiSession s, NodeRow node, CancellationToken ct)
    {
        if (!ClusterQueries.MayHostReplicas(node.Status))
        {
            return [];
        }
        try
        {
            var system = new DeployedApplicationRow(
                ClusterQueries.SystemApplicationId, ClusterQueries.SystemApplicationName, "System", "", HealthState.Unknown);
            var apps = (await s.Deployed.GetApplicationsAsync(node.Name, ct)).Where(a => a.Id != system.Id).Prepend(system);
            var perApp = await ForEachLimited(apps, async a =>
            {
                try
                {
                    return (await s.Deployed.GetReplicasAsync(node.Name, a.Id, null, ct))
                        .Select(r => new DeployedReplicaDto(a.Name, a.Id, r.ServiceName, r.PartitionId.ToString(), r.Id, r.Role, r.Status, r.CodePackage));
                }
                // Older clusters may not list system replicas by application; the rest of the node still shows.
                catch (Exception) when (a.Id == system.Id && !ct.IsCancellationRequested)
                {
                    return [];
                }
            });
            return perApp.SelectMany(r => r).OrderBy(r => r.Application).ThenBy(r => r.Service).ToList();
        }
        catch (Exception) when (node.Status != nameof(NodeStatus.Up) && !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    private static NodeDto Node(UiSession s, NodeRow n) => new(
        n.Name, n.Address, n.NodeType, n.Status, Dto.Health(n.Health), n.UpgradeDomain, n.FaultDomain, n.IsSeed, n.CodeVersion,
        n.Uptime is { } up ? (long)up.TotalSeconds : null,
        WebActions.For(s, "node", new Dictionary<string, string> { ["node"] = n.Name, ["status"] = n.Status }));

    internal static ServiceDto Service(ServiceInfo s) => new(
        s.Id, s.Name.ToString(), s.TypeName ?? "", s.ServiceKind.ToString() ?? "", s.ServiceStatus?.ToString() ?? "", Dto.Health(s.HealthState));

    internal static IReadOnlyList<ActionDto> UpgradeActions(UiSession s, UpgradeRow u) =>
        WebActions.For(s, "upgrade", new Dictionary<string, string>
        {
            ["kind"] = u.Kind, ["applicationId"] = u.ApplicationId ?? "", ["name"] = u.Name, ["state"] = u.Report.State,
            ["nextDomain"] = u.Report.NextDomain, ["plan"] = LifecycleActions.PlanOf(u.Report)?.Id ?? "",
        });

    /// <summary>The target of a service's actions, with its counts from its description.</summary>
    internal static Dictionary<string, string> ServiceTarget(ServiceDto service, string appId, ServiceDescription d)
    {
        var (count, min) = d switch
        {
            StatefulServiceDescription f => (f.TargetReplicaSetSize?.ToString(CultureInfo.InvariantCulture), f.MinReplicaSetSize?.ToString(CultureInfo.InvariantCulture)),
            StatelessServiceDescription l => (l.InstanceCount?.ToString(CultureInfo.InvariantCulture), null),
            _ => (null, null),
        };
        return new Dictionary<string, string>
        {
            ["serviceId"] = service.Id, ["applicationId"] = appId, ["name"] = service.Name, ["kind"] = service.Kind,
            ["count"] = count ?? "", ["min"] = min ?? "",
        };
    }

    /// <summary>The service types an application's version provides, for its create-service action; none when they can't be read.</summary>
    private static async Task<string> ServiceTypesTargetAsync(UiSession s, string typeName, string typeVersion, CancellationToken ct)
    {
        try
        {
            return LifecycleActions.ServiceTypesTarget(await s.Queries.GetServiceTypesAsync(typeName, typeVersion, ct));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return "";
        }
    }

    private static IReadOnlyList<FieldDto> Describe(ServiceDescription d)
    {
        var fields = new List<FieldDto>();
        void Add(string name, object? value)
        {
            if (value is not null && value.ToString() is { Length: > 0 } text)
            {
                fields.Add(new FieldDto(name, text));
            }
        }

        Add("Service type", d.ServiceTypeName);
        Add("Partitioning", d.PartitionDescription switch
        {
            SingletonPartitionSchemeDescription => "Singleton",
            UniformInt64RangePartitionSchemeDescription u => $"Int64 range, {u.Count} partitions ({u.LowKey} to {u.HighKey})",
            NamedPartitionSchemeDescription n => $"Named: {string.Join(", ", n.Names ?? [])}",
            _ => null,
        });
        switch (d)
        {
            case StatefulServiceDescription f:
                Add("Target replica set size", f.TargetReplicaSetSize);
                Add("Min replica set size", f.MinReplicaSetSize);
                Add("Persisted state", f.HasPersistedState is true ? "Yes" : "No");
                Add("Replica restart wait", Seconds(f.ReplicaRestartWaitDurationSeconds));
                Add("Quorum loss wait", Seconds(f.QuorumLossWaitDurationSeconds));
                Add("Standby replica keep", Seconds(f.StandByReplicaKeepDurationSeconds));
                break;
            case StatelessServiceDescription l:
                Add("Instance count", l.InstanceCount == -1 ? "-1 (every node)" : l.InstanceCount);
                Add("Min instance count", l.MinInstanceCount);
                break;
        }
        Add("Placement constraints", d.PlacementConstraints);
        Add("Activation mode", d.ServicePackageActivationMode);
        Add("DNS name", d.ServiceDnsName);
        Add("Default move cost", d.DefaultMoveCost);
        foreach (var m in d.ServiceLoadMetrics ?? [])
        {
            Add($"Load metric {m.Name}", $"weight {m.Weight}, primary {m.PrimaryDefaultLoad}, secondary {m.SecondaryDefaultLoad}");
        }
        return fields;
    }

    private static void RequireUserApplication(string appId)
    {
        if (appId == ClusterQueries.SystemApplicationId)
        {
            throw new ActionException("System services have no application or service manifest; see the cluster manifest.", StatusCodes.Status404NotFound);
        }
    }

    private static string? Seconds(long? seconds) => seconds is null ? null : $"{seconds}s";

    private static TextDto Raw(object value) => new(Inspect.Json(value), "json");

    private static TextDto Text(string text)
    {
        var (pretty, format) = Inspect.TryPretty(text);
        return new TextDto(pretty, format.ToString().ToLowerInvariant());
    }

    internal static double Number(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>
    /// A capacity, or null for no limit. Unset capacities come back as 0, -1 or a huge number; 0 is only a real limit
    /// (nothing with that load may be placed) when the cluster manifest <paramref name="declared"/> it.
    /// </summary>
    internal static double? Capacity(string? value, bool declared = false) => Number(value) switch
    {
        _ when string.IsNullOrWhiteSpace(value) => null,
        >= 1e12 or < 0 => null,
        0 when !declared => null,
        var n => n,
    };

    private static async Task<IReadOnlyList<TOut>> ForEachLimited<TIn, TOut>(IEnumerable<TIn> items, Func<TIn, Task<TOut>> run)
    {
        using var gate = new SemaphoreSlim(Parallelism);
        return await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync();
            try
            {
                return await run(item);
            }
            finally
            {
                gate.Release();
            }
        }));
    }
}
