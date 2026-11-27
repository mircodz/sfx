using System.Text.Json;
using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Web;

/// <summary>
/// What the browser's read endpoints return, as methods <see cref="Api"/> maps and the scripting CLI calls, so both
/// give the same data. Missing entities throw an <see cref="ActionException"/> with status 404, or whatever the
/// cluster throws.
/// </summary>
internal static class ApiReads
{
    private const int Parallelism = 8;

    /// <summary>The options the web server writes JSON with: ASP.NET's defaults, which it doesn't change.</summary>
    public static JsonSerializerOptions Json => new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;

    public static async Task<ClusterDto> ClusterAsync(UiSession s, CancellationToken ct)
    {
        var version = s.Inspector.GetClusterVersionAsync(ct);
        var health = s.Inspector.GetClusterHealthAsync(ct);
        var target = new Dictionary<string, string> { ["name"] = s.Context.Name, ["azureManaged"] = await s.AzureManagedTargetAsync(ct) };
        return new ClusterDto(
            s.Context.Name,
            s.Cluster.Endpoint.ToString(),
            (await version).Version ?? "",
            Dto.Health(await health),
            WebActions.For(s, "cluster", target));
    }

    /// <summary>With why each unhealthy one is.</summary>
    public static async Task<IReadOnlyList<NodeDto>> NodesAsync(UiSession s, CancellationToken ct) =>
        await ApiReasons.NodesAsync(s, (await s.Queries.GetNodesAsync(ct)).Select(n => Node(s, n)).ToList(), ct);

    public static async Task<NodeDetailDto> NodeAsync(UiSession s, string name, CancellationToken ct)
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
            .Select(m => new MetricDto(
                m.Name ?? "",
                m.CurrentNodeLoad ?? Api.Number(m.NodeLoad),
                Api.Capacity(m.NodeCapacity, declared.Declares(node.NodeType, m.Name!))))
            .OrderBy(m => m.Capacity is null)
            .ThenBy(m => m.Name)
            .ToList();
        return new NodeDetailDto(Node(s, node), Dto.Health(await health), metrics, await replicas);
    }

    /// <summary>With why each unhealthy one is.</summary>
    public static async Task<IReadOnlyList<ApplicationDto>> ApplicationsAsync(UiSession s, CancellationToken ct) =>
        await ApiReasons.ApplicationsAsync(s, (await s.Queries.GetApplicationsAsync(ct)).Select(Dto.Application).ToList(), ct);

    public static async Task<ApplicationDetailDto> ApplicationAsync(UiSession s, string appId, CancellationToken ct)
    {
        if (appId == ClusterQueries.SystemApplicationId)
        {
            var services = ServicesAsync(s, appId, ct);
            var health = s.Inspector.GetApplicationHealthAsync(appId, ct);
            var system = (await s.Queries.GetApplicationsAsync(ct)).First(a => a.Id == appId);
            return new ApplicationDetailDto(
                Dto.Application(system) with { Health = Dto.Health((await health).State) },
                Dto.Health(await health),
                [],
                [],
                null,
                await services,
                [],
                new DefaultServicesDto(false, []),
                UpgradeDefaultsDto.For("<ApplicationManifest />"));
        }

        // Asked first so an unknown id is a 404 that says so, not whichever query fails first.
        var info = await s.Inspector.GetApplicationAsync(appId, ct);
        var appServices = ServicesAsync(s, appId, ct);
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
                k,
                current.TryGetValue(k, out var v) ? v : defaults[k],
                defaults.TryGetValue(k, out var d) ? d : null,
                current.ContainsKey(k)))
            .ToList();

        var row = new ApplicationRow(
            info.Id,
            info.Name.ToString(),
            info.TypeName,
            info.TypeVersion,
            info.Status?.ToString() ?? "",
            info.HealthState ?? HealthState.Unknown);
        var report = UpgradeReport.From(await upgrade);
        var upgradeRow = new UpgradeRow(row.Name, "application", row.Id, report.Target != row.TypeVersion ? row.TypeVersion : "", report);

        var target = new Dictionary<string, string>
        {
            ["applicationId"] = row.Id,
            ["name"] = row.Name,
            ["typeName"] = row.TypeName,
            ["version"] = row.TypeVersion,
            ["serviceTypes"] = await serviceTypes,
        };
        return new ApplicationDetailDto(
            Dto.Application(row),
            Dto.Health(await appHealth),
            parameters,
            types.OrderByDescending(t => t.Version, VersionComparer.Instance)
                .Select(t => new VersionDto(t.Version, t.DefaultParameterList ?? new Dictionary<string, string>()))
                .ToList(),
            upgradeRow.StartedUtc is null ? null : Dto.Upgrade(upgradeRow, Api.UpgradeActions(s, upgradeRow)),
            await appServices,
            WebActions.For(s, "application", target),
            new DefaultServicesDto(await defaultServicesUpgrade, DefaultServices.ParameterUses(await manifest)),
            UpgradeDefaultsDto.For(await manifest));
    }

    /// <summary>An application's services, as its page lists them, with why each unhealthy one is.</summary>
    public static async Task<IReadOnlyList<ServiceDto>> ServicesAsync(UiSession s, string appId, CancellationToken ct) =>
        await ApiReasons.ServicesAsync(s, (await s.Queries.GetServicesAsync(appId, ct)).Select(Dto.Service).ToList(), ct);

    public static async Task<ServiceDetailDto> ServiceAsync(UiSession s, string appId, string serviceId, CancellationToken ct)
    {
        // Asked first so an unknown id is a 404 that says so, not whichever query fails first.
        var service = Api.Service(await s.Inspector.GetServiceAsync(appId, serviceId, ct));
        var description = s.Inspector.GetServiceDescriptionAsync(serviceId, ct);
        var health = s.Inspector.GetServiceHealthAsync(serviceId, ct);
        var partitions = PartitionsAsync(s, serviceId, ct);

        var d = await description;
        return new ServiceDetailDto(
            service,
            appId,
            Dto.Health(await health),
            Describe(d),
            await partitions,
            WebActions.For(s, "service", Api.ServiceTarget(service, appId, d)));
    }

    /// <summary>A service's partitions, as its page lists them, with why each unhealthy one is.</summary>
    public static async Task<IReadOnlyList<PartitionDto>> PartitionsAsync(UiSession s, string serviceId, CancellationToken ct) =>
        await ApiReasons.PartitionsAsync(s, (await s.Queries.GetPartitionsAsync(serviceId, ct)).Select(Dto.Partition).ToList(), ct);

    public static async Task<PartitionDetailDto> PartitionAsync(UiSession s, string appId, string serviceId, string partitionId, CancellationToken ct)
    {
        var id = new PartitionId(Guid.TryParse(partitionId, out var guid) ? guid : throw new ActionException("Not a partition id."));
        var svc = Api.Service(await s.Inspector.GetServiceAsync(appId, serviceId, ct));
        var partition = (await s.Queries.GetPartitionsAsync(serviceId, ct)).FirstOrDefault(p => p.Id.ToString() == id.ToString())
            ?? throw new ActionException($"There's no partition {partitionId} in {svc.Name}.", StatusCodes.Status404NotFound);
        var health = s.Inspector.GetPartitionHealthAsync(id, ct);
        var load = s.Inspector.GetPartitionLoadAsync(id, ct);
        var replicas = s.Queries.GetReplicasAsync(id, ct);

        var l = await load;
        var metrics = (l.PrimaryLoadMetricReports ?? []).Select(m => new MetricDto($"{m.Name} (primary)", Api.Number(m.CurrentValue ?? m.Value), null))
            .Concat((l.SecondaryLoadMetricReports ?? []).Select(m => new MetricDto($"{m.Name} (secondary)", Api.Number(m.CurrentValue ?? m.Value), null)))
            .ToList();

        var replicaDtos = (await replicas).Select(r => new ReplicaDto(
            r.Id,
            r.NodeName,
            r.Role,
            r.Status,
            Dto.Health(r.Health),
            r.Address,
            WebActions.For(s, "replica", new Dictionary<string, string>
            {
                ["partitionId"] = partition.Id.ToString(),
                ["replicaId"] = r.Id,
                ["node"] = r.NodeName,
                ["role"] = r.Role,
                ["serviceId"] = svc.Id,
                ["serviceKind"] = svc.Kind,
            }))).ToList();

        var target = new Dictionary<string, string> { ["partitionId"] = partition.Id.ToString(), ["serviceId"] = svc.Id };
        return new PartitionDetailDto(
            Dto.Partition(partition),
            svc,
            Dto.Health(await health),
            metrics,
            replicaDtos,
            WebActions.For(s, "partition", target));
    }

    /// <summary>The ones needing attention first, then the latest.</summary>
    public static async Task<IReadOnlyList<UpgradeDto>> UpgradesAsync(UiSession s, CancellationToken ct) =>
        (await s.GetUpgradesAsync(ct))
            .OrderBy(u => u.Urgency)
            .ThenByDescending(u => u.StartedUtc)
            .Select(u => Dto.Upgrade(u, Api.UpgradeActions(s, u)))
            .ToList();

    /// <summary>Each provisioned version, by name then version.</summary>
    public static async Task<IReadOnlyList<ApplicationTypeDto>> TypesAsync(UiSession s, CancellationToken ct)
    {
        var types = s.Queries.GetApplicationTypesAsync(ct);
        var apps = (await s.Queries.GetApplicationsAsync(ct))
            .Where(a => a.Id != ClusterQueries.SystemApplicationId)
            .ToLookup(a => (a.TypeName, a.TypeVersion), a => a.Name);
        return (await types)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Version, VersionComparer.Instance)
            .Select(t => ApiLifecycle.Type(s, t, apps[(t.Name, t.Version)].Order(StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }

    public static async Task<RepairsDto> RepairsAsync(UiSession s, CancellationToken ct)
    {
        try
        {
            return new RepairsDto((await s.Repairs.GetRepairTasksAsync(ct)).Select(ApiRepairs.Repair).ToList(), null);
        }
        catch (InvalidOperationException ex)
        {
            // RepairQueries throws this when the cluster has no repair manager.
            return new RepairsDto([], ex.Message);
        }
    }

    /// <summary>The action log's last 200 entries, newest first.</summary>
    public static IReadOnlyList<ActivityDto> Activity(UiSession s) => s.ReadLog(200)
        .Reverse()
        .Select(e => new ActivityDto(e.Time, e.User, e.Context, e.Action, e.Outcome, e.Succeeded))
        .ToList();

    /// <summary>A folder of the image store; the root also has the store's usage and which folders hold packages.</summary>
    public static async Task<StoreDto> StoreAsync(UiSession s, string? path, CancellationToken ct)
    {
        var folder = ImageStore.Normalize(path);
        var root = folder.Length == 0;
        var listing = s.ImageStore.GetContentAsync(folder, ct);
        var kind = ApiImageStore.KindAsync(s, ct);
        var usage = root ? s.ImageStore.GetUsageAsync(ct) : Task.FromResult<ImageStoreUsage?>(null);
        var packages = root
            ? ApiImageStore.PackagesAsync(s, ct)
            : Task.FromResult<IReadOnlyDictionary<string, ImageStorePackage>>(new Dictionary<string, ImageStorePackage>());
        var l = await listing;
        return ApiImageStore.Listing(s, l, await kind, await usage, await packages);
    }

    public static async Task<StoreSizeDto> StoreSizeAsync(UiSession s, string? path, CancellationToken ct) =>
        new(await s.GetFolderSizeAsync(path ?? "", ct));

    private static NodeDto Node(UiSession s, NodeRow n) => new(
        n.Name,
        n.Address,
        n.NodeType,
        n.Status,
        Dto.Health(n.Health),
        n.UpgradeDomain,
        n.FaultDomain,
        n.IsSeed,
        n.CodeVersion,
        n.Uptime is { } up ? (long)up.TotalSeconds : null,
        WebActions.For(s, "node", new Dictionary<string, string> { ["node"] = n.Name, ["status"] = n.Status }));

    /// <summary>
    /// Includes system services' replicas (as application "fabric:/System", which the node's application list leaves
    /// out). A node that isn't up may not answer; it then shows none rather than failing the page.
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
                ClusterQueries.SystemApplicationId,
                ClusterQueries.SystemApplicationName,
                "System",
                "",
                HealthState.Unknown);
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

    /// <summary>For the create-service action; empty when the service types can't be read.</summary>
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

    private static string? Seconds(long? seconds) => seconds is null ? null : $"{seconds}s";

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
