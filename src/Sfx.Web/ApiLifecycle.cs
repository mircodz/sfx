using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Web;

/// <summary>A provisioned application type version.</summary>
/// <param name="Status">Provisioning, Available, Unprovisioning or Failed.</param>
/// <param name="StatusDetails">Why provisioning failed, when it did; null otherwise.</param>
/// <param name="Applications">Names (fabric:/…) of the applications on this version.</param>
/// <param name="Actions">create-application and unprovision-type, where they apply.</param>
public sealed record ApplicationTypeDto(
    string Name,
    string Version,
    string Status,
    string? StatusDetails,
    IReadOnlyDictionary<string, string> DefaultParameters,
    IReadOnlyList<string> Applications,
    IReadOnlyList<ActionDto> Actions);

/// <param name="Manifest">The service manifest that declares it, and that manifest's version.</param>
public sealed record ServiceTypeDto(string Name, bool Stateful, string Manifest, string ManifestVersion);

/// <param name="Kind">How it's typed in: count (a whole number within min and max), seconds ("30", "30s", "5m", "1h", "2d"), text, choice or flag ("true"/"false").</param>
/// <param name="Value">Its value now; empty when the cluster's default applies.</param>
public sealed record ServiceSettingDto(string Key, string Title, string Kind, string Value, IReadOnlyList<string>? Choices, int? Min, int? Max);

/// <param name="Action">edit-service-settings, taking the changed settings by key; null when it isn't offered (read-only, or a system service).</param>
public sealed record ServiceSettingsDto(bool Stateful, IReadOnlyList<ServiceSettingDto> Settings, ActionDto? Action);

internal static class ApiLifecycle
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/types", async (UiSession s, CancellationToken ct) =>
        {
            var types = s.Queries.GetApplicationTypesAsync(ct);
            var apps = (await s.Queries.GetApplicationsAsync(ct))
                .Where(a => a.Id != ClusterQueries.SystemApplicationId)
                .ToLookup(a => (a.TypeName, a.TypeVersion), a => a.Name);
            return (await types)
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Version, VersionComparer.Instance)
                .Select(t => Type(s, t, apps[(t.Name, t.Version)].Order(StringComparer.OrdinalIgnoreCase).ToList()))
                .ToList();
        });

        api.MapGet("/types/{name}/{version}/services", async (UiSession s, string name, string version, CancellationToken ct) =>
            (await s.Queries.GetServiceTypesAsync(name, version, ct))
                .Select(t => new ServiceTypeDto(t.Name, t.Stateful, t.Manifest, t.ManifestVersion))
                .ToList());

        api.MapGet("/applications/{appId}/services/{serviceId}/settings", async (UiSession s, string appId, string serviceId, CancellationToken ct) =>
        {
            // Asked first so an unknown id is a 404 that says so.
            var service = Api.Service(await s.Inspector.GetServiceAsync(appId, serviceId, ct));
            var description = await s.Inspector.GetServiceDescriptionAsync(serviceId, ct);
            var action = WebActions.For(s, "service", Api.ServiceTarget(service, appId, description)).FirstOrDefault(a => a.Id == "edit-service-settings");
            return new ServiceSettingsDto(description is StatefulServiceDescription, ServiceSettings.Read(description).Select(Setting).ToList(), action);
        });
    }

    internal static ApplicationTypeDto Type(UiSession s, ApplicationTypeRow t, IReadOnlyList<string> applications)
    {
        var target = new Dictionary<string, string>
        {
            ["typeName"] = t.Name,
            ["version"] = t.Version,
            ["status"] = t.Status,
            ["inUse"] = applications.Count > 0 ? "true" : "false",
        };
        foreach (var (name, value) in t.DefaultParameters)
        {
            target[LifecycleActions.DefaultPrefix + name] = value;
        }
        return new ApplicationTypeDto(
            t.Name,
            t.Version,
            t.Status,
            t.StatusDetails is { Length: > 0 } details ? details : null,
            t.DefaultParameters,
            applications,
            WebActions.For(s, "type", target));
    }

    internal static ServiceSettingDto Setting(ServiceSetting s) =>
        new(s.Key, s.Title, s.Kind.ToString().ToLowerInvariant(), s.Value, s.Choices, s.Min, s.Max);
}
