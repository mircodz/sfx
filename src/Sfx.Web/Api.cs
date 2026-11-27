using System.Globalization;
using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Web;

/// <summary>Read endpoints, actions and the power toggle; ids are Service Fabric's (e.g. "orders~Counter").</summary>
internal static class Api
{
    /// <summary>
    /// Handlers take the current <see cref="UiSession"/> as a parameter (resolved per request from
    /// <see cref="UiHost"/>), so a context switch takes effect on the next request.
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

        api.MapPost("/session/power", async (UiSession s, PowerRequest request, HttpContext http, CancellationToken ct) =>
        {
            host.Expect(s, http);
            if (s.ReadOnly)
            {
                throw new ActionException("This context is read-only.", StatusCodes.Status403Forbidden);
            }
            s.Power = request.On;
            return await SessionInfoAsync(s, ct);
        });

        api.MapGet("/cluster", (UiSession s, CancellationToken ct) => ApiReads.ClusterAsync(s, ct));

        api.MapGet("/cluster/manifest", async (UiSession s, CancellationToken ct) => Text(await s.Inspector.GetClusterManifestAsync(ct)));

        api.MapGet("/nodes", (UiSession s, CancellationToken ct) => ApiReads.NodesAsync(s, ct));

        api.MapGet("/nodes/{name}", (UiSession s, string name, CancellationToken ct) => ApiReads.NodeAsync(s, name, ct));

        api.MapGet("/applications", (UiSession s, CancellationToken ct) => ApiReads.ApplicationsAsync(s, ct));

        api.MapGet("/applications/{appId}", (UiSession s, string appId, CancellationToken ct) => ApiReads.ApplicationAsync(s, appId, ct));

        api.MapGet("/applications/{appId}/manifest", async (UiSession s, string appId, CancellationToken ct) =>
        {
            RequireUserApplication(appId);
            var info = await s.Inspector.GetApplicationAsync(appId, ct);
            return Text(await s.Inspector.GetApplicationManifestAsync(info.TypeName, info.TypeVersion, ct));
        });

        api.MapGet(
            "/applications/{appId}/services/{serviceId}",
            (UiSession s, string appId, string serviceId, CancellationToken ct) => ApiReads.ServiceAsync(s, appId, serviceId, ct));

        api.MapGet("/applications/{appId}/services/{serviceId}/manifest", async (UiSession s, string appId, string serviceId, CancellationToken ct) =>
        {
            RequireUserApplication(appId);
            var app = s.Inspector.GetApplicationAsync(appId, ct);
            var service = s.Inspector.GetServiceAsync(appId, serviceId, ct);
            var a = await app;
            return Text(await s.Inspector.GetServiceManifestAsync(a.TypeName, a.TypeVersion, (await service).TypeName, ct));
        });

        api.MapGet(
            "/applications/{appId}/services/{serviceId}/partitions/{partitionId}",
            (UiSession s, string appId, string serviceId, string partitionId, CancellationToken ct) =>
                ApiReads.PartitionAsync(s, appId, serviceId, partitionId, ct));

        api.MapGet("/replicas", (UiSession s, CancellationToken ct) => s.GetClusterReplicasAsync(ct));

        api.MapGet("/applications/{appId}/services/{serviceId}/replicas", (UiSession s, string appId, string serviceId, CancellationToken ct) =>
            s.GetServiceReplicasAsync(appId, serviceId, ct));

        api.MapGet("/upgrades", (UiSession s, CancellationToken ct) => ApiReads.UpgradesAsync(s, ct));

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
        api.MapGet(
            "/applications/{appId}/raw",
            async (UiSession s, string appId, CancellationToken ct) => Raw(await s.Inspector.GetApplicationAsync(appId, ct)));
        api.MapGet("/applications/{appId}/services/{serviceId}/raw", async (UiSession s, string appId, string serviceId, CancellationToken ct) =>
        {
            var info = s.Inspector.GetServiceAsync(appId, serviceId, ct);
            var description = s.Inspector.GetServiceDescriptionAsync(serviceId, ct);
            return Raw(new { Info = await info, Description = await description });
        });
        api.MapGet(
            "/applications/{appId}/services/{serviceId}/partitions/{partitionId}/raw",
            async (UiSession s, string appId, string serviceId, string partitionId, CancellationToken ct) =>
                Raw(await s.Inspector.GetPartitionAsync(
                    new PartitionId(Guid.TryParse(partitionId, out var g) ? g : throw new ActionException("Not a partition id.")),
                    ct)));

        api.MapGet("/activity", (UiSession s) => ApiReads.Activity(s));

        api.MapPost("/actions/{id}", (UiSession s, string id, ActionRequest request, HttpContext http, IHostApplicationLifetime life, CancellationToken ct) =>
        {
            host.Expect(s, http);
            return WebActions.RunAsync(s, id, request, ct, life.ApplicationStopping);
        });
        ApiClusterUpgrades.Map(api);
        ApiOperations.Map(api);
        ApiPackages.Map(api);
        ApiRepairs.Map(api);
        ApiLoad.Map(api);
        ApiOverview.Map(api);
        ApiLifecycle.Map(api);
        ApiSearch.Map(api);
        ApiImageStore.Map(api, host);
    }

    public sealed record PowerRequest(bool On);

    private static async Task<SessionDto> SessionInfoAsync(UiSession s, CancellationToken ct)
    {
        var imageStore = s.ImageStoreBrowsableAsync(ct);
        var services = await s.GetSystemServicesAsync(ct);
        return new(
            s.Id,
            s.Context.Name,
            s.Cluster.Endpoint.ToString(),
            s.ReadOnly,
            s.Power,
            s.Version,
            services.EventStore,
            s.Context.Folder,
            s.Label,
            s.Color,
            services.RepairManager,
            services.FaultAnalysis,
            await imageStore);
    }

    internal static ServiceDto Service(ServiceInfo s) => new(
        s.Id,
        s.Name.ToString(),
        s.TypeName ?? "",
        s.ServiceKind.ToString() ?? "",
        s.ServiceStatus?.ToString() ?? "",
        Dto.Health(s.HealthState));

    internal static IReadOnlyList<ActionDto> UpgradeActions(UiSession s, UpgradeRow u) =>
        WebActions.For(s, "upgrade", new Dictionary<string, string>
        {
            ["kind"] = u.Kind,
            ["applicationId"] = u.ApplicationId ?? "",
            ["name"] = u.Name,
            ["state"] = u.Report.State,
            ["nextDomain"] = u.Report.NextDomain,
            ["plan"] = LifecycleActions.PlanOf(u.Report)?.Id ?? "",
        });

    /// <summary>A service's action target, with counts from its description.</summary>
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
            ["serviceId"] = service.Id,
            ["applicationId"] = appId,
            ["name"] = service.Name,
            ["kind"] = service.Kind,
            ["count"] = count ?? "",
            ["min"] = min ?? "",
        };
    }

    private static void RequireUserApplication(string appId)
    {
        if (appId == ClusterQueries.SystemApplicationId)
        {
            throw new ActionException("System services have no application or service manifest; see the cluster manifest.", StatusCodes.Status404NotFound);
        }
    }

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
}
