using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Web;

/// <summary>
/// The <see cref="WebActions"/> for application types, new applications and services, service settings, upgrade plans
/// and the image store.
/// </summary>
internal static class LifecycleActions
{
    public const string DefaultPrefix = "default:";
    public const string ParameterPrefix = "param:";

    private static readonly ActionChoice[] Plans = UpgradePlan.All.Select(p => new ActionChoice(p.Id, p.Title, p.Hint)).ToArray();

    private static readonly ActionChoice[] ServiceKinds =
    [
        new(nameof(ServiceKind.Stateful), "Stateful"),
        new(nameof(ServiceKind.Stateless), "Stateless"),
    ];

    private static readonly ActionChoice[] YesNo = [new("yes", "Yes"), new("no", "No")];

    public static IReadOnlyList<ActionDef> All { get; } =
    [
        // Application types: target typeName, version, status, inUse ("true"/"false"), and "default:{name}" per parameter.

        new(
            "create-application",
            "type",
            "Create application",
            Risk.Confirm,
            (t, i) => $"Create {ApplicationName(i["name"])} ({t["typeName"]} {t["version"]})"
                + (Parameters(t, i) is { Count: > 0 } set ? $", setting {string.Join(", ", set.Keys)}" : ""),
            (s, t, i, ct) => s.Lifecycle.CreateApplicationAsync(i["name"], t["typeName"], t["version"], Parameters(t, i), ct),
            "Its default services are created with it.",
            Applies: t => t["status"] == nameof(ApplicationTypeStatus.Available),
            Inputs: t =>
            [
                new("name", "Name", "text", Default: "fabric:/"),
                .. Defaults(t).Select(kv => new ActionInputDto(ParameterPrefix + kv.Key, kv.Key, "text", Default: kv.Value)),
            ],
            Form: true),

        new("unprovision-type", "type", "Unprovision", Risk.Destructive,
            (t, _) => $"Unprovision {t["typeName"]} {t["version"]}",
            (s, t, _, ct) => s.Lifecycle.UnprovisionAsync(t["typeName"], t["version"], ct),
            "No application can be created from or upgraded to this version until it's provisioned again.",
            // While applications use it the cluster refuses, so it isn't offered; the row lists them.
            Applies: t => t["inUse"] == "false" && t["status"] is nameof(ApplicationTypeStatus.Available) or nameof(ApplicationTypeStatus.Failed),
            ConfirmText: t => t["version"]),

        // Applications: WebActions' application target plus serviceTypes ("{name}={Stateful|Stateless}" per line).

        new(
            "create-service",
            "application",
            "Create service",
            Risk.Confirm,
            (t, i) => DescribeService(t, i),
            (s, t, i, ct) => s.Lifecycle.CreateServiceAsync(t["applicationId"], NewService(t, i), ct),
            Applies: t => t["applicationId"] != ClusterQueries.SystemApplicationId && t.Optional("serviceTypes") is not null,
            Inputs: t =>
            {
                var types = ServiceTypes(t);
                var stateful = types.FirstOrDefault().Stateful;
                return
                [
                    new("name", "Name", "text", Default: t["name"] + "/"),
                    new(
                        "type",
                        "Service type",
                        "choice",
                        types.Select(x => new ActionChoice(x.Name, x.Name, x.Stateful ? "Stateful" : "Stateless")).ToList(),
                        types.FirstOrDefault().Name),
                    new("kind", "Kind", "choice", ServiceKinds, stateful ? nameof(ServiceKind.Stateful) : nameof(ServiceKind.Stateless)),
                    new("count", "Replicas (stateful) or instances (stateless, -1 for every node)", "text", Default: stateful ? "3" : "1"),
                    new("partitions", "Partitions", "text", Default: "1"),
                    new("persisted", "Persisted state (stateful)", "choice", YesNo, "yes"),
                ];
            },
            Form: true),

        new(
            "edit-service-settings",
            "service",
            "Edit settings",
            Risk.Confirm,
            (t, _) => $"Change the settings of {t["name"]}",
            (s, t, i, ct) => s.ServiceSettings.UpdateAsync(t["serviceId"], i.Values, ct),
            "Takes effect right away; changing counts adds or removes replicas.",
            Applies: t => t["applicationId"] != ClusterQueries.SystemApplicationId,
            Form: true,
            Prepare: async (s, t, i, ct) =>
            {
                var description = await s.Inspector.GetServiceDescriptionAsync(t["serviceId"], ct);
                var settings = ServiceSettings.Read(description);
                var changes = ServiceSettings.Changes(new ServiceSettingSet(description is StatefulServiceDescription, settings), i.Values);
                return changes.Count == 0
                    ? throw new ActionException("Nothing to change: every value is what the service has now.")
                    : ServiceSettings.Describe(t["name"], settings, changes);
            }),

        // Upgrades: WebActions' upgrade target plus plan (the followed plan's id; empty when unknown).

        new(
            "change-upgrade-plan",
            "upgrade",
            "Change plan",
            Risk.Confirm,
            (t, i) => t.Optional("plan") == i["plan"]
                ? throw new ActionException($"The upgrade of {t["name"]} already runs {UpgradePlan.Find(i["plan"]).Title.ToLowerInvariant()}.")
                : $"Switch the upgrade of {t["name"]} to {UpgradePlan.Find(i["plan"]).Title.ToLowerInvariant()}",
            (s, t, i, ct) => t["kind"] == "cluster"
                ? s.ClusterUpgrades.ChangePlanAsync(UpgradePlan.Find(i["plan"]), ct)
                : s.Lifecycle.ChangeUpgradePlanAsync(t["applicationId"], UpgradePlan.Find(i["plan"]), ct),
            "Applies from the next upgrade domain; manual plans wait to be resumed into each domain.",
            Applies: t => t["state"] is nameof(UpgradeState.RollingForwardInProgress) or nameof(UpgradeState.RollingForwardPending),
            Inputs: t => [new("plan", "Plan", "choice", Plans, t.Optional("plan") ?? UpgradePlan.All[0].Id)]),

        // Image store entries: target path, name, entry ("folder"/"file"), provision ("true" for an unprovisioned package folder).

        new(
            "delete-store-content",
            "store",
            "Delete",
            Risk.Destructive,
            (t, _) => $"Delete {ImageStore.Normalize(t["path"])} from the image store",
            (s, t, _, ct) => s.ImageStore.DeleteAsync(t["path"], ct),
            "Types already provisioned from it keep working; it can't be provisioned again without uploading it.",
            Applies: t => ImageStore.Normalize(t["path"]).Length > 0 && !ImageStore.IsReserved(t["path"]),
            ConfirmText: t => t["name"]),

        new(
            "provision-package",
            "store",
            "Provision",
            Risk.Confirm,
            (t, _) => $"Provision the package in {ImageStore.Normalize(t["path"])}",
            null,
            "Registers the application type in it, so applications can be created from it.",
            Applies: t => t.Optional("provision") == "true",
            Start: async (s, t, _, ct) =>
            {
                var before = (await s.Queries.GetApplicationTypesAsync(ct)).Select(x => (x.Name, x.Version)).ToHashSet();
                await s.Lifecycle.ProvisionAsync(t["path"], ct);
                var started = DateTime.UtcNow;
                return async probeCt => SettleChecks.NewTypeProvisioned(before, await s.Queries.GetApplicationTypesAsync(probeCt), DateTime.UtcNow - started);
            }),
    ];

    /// <summary>"fabric:/name" for what was typed, with or without "fabric:/".</summary>
    public static string ApplicationName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.StartsWith("fabric:/", StringComparison.Ordinal) ? trimmed.TrimEnd('/') : "fabric:/" + trimmed.Trim('/');
    }

    /// <summary>A new service's full name: as typed when it starts with "fabric:/", else under the application.</summary>
    public static string ServiceName(string applicationName, string name)
    {
        var trimmed = name.Trim();
        return trimmed.StartsWith("fabric:/", StringComparison.Ordinal) ? trimmed.TrimEnd('/') : $"{applicationName}/{trimmed.Trim('/')}";
    }

    public static IEnumerable<KeyValuePair<string, string>> Defaults(Args type) => type.Values
        .Where(kv => kv.Key.StartsWith(DefaultPrefix, StringComparison.Ordinal))
        .Select(kv => KeyValuePair.Create(kv.Key[DefaultPrefix.Length..], kv.Value))
        .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Only parameters typed differently from the type's default (or unknown to it, for Core to refuse).</summary>
    public static IReadOnlyDictionary<string, string> Parameters(Args type, Args inputs) => inputs.Values
        .Where(kv => kv.Key.StartsWith(ParameterPrefix, StringComparison.Ordinal))
        .Select(kv => (Name: kv.Key[ParameterPrefix.Length..], kv.Value))
        .Where(p => !type.Values.TryGetValue(DefaultPrefix + p.Name, out var d) || d != p.Value)
        .ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

    /// <summary>The target entry listing the service types an application's version provides.</summary>
    public static string ServiceTypesTarget(IEnumerable<ServiceTypeRow> types) =>
        string.Join("\n", types.Select(t => $"{t.Name}={(t.Stateful ? nameof(ServiceKind.Stateful) : nameof(ServiceKind.Stateless))}"));

    public static IReadOnlyList<(string Name, bool Stateful)> ServiceTypes(Args application) =>
        (application.Optional("serviceTypes") ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.LastIndexOf('=') is var at and > 0
                ? (line[..at], line[(at + 1)..] == nameof(ServiceKind.Stateful))
                : (line, false))
            .ToList();

    /// <summary>The create-service form's service; its name is relative to the application, or full.</summary>
    public static NewService NewService(Args application, Args inputs)
    {
        var name = ServiceName(application["name"], inputs["name"]);
        if (name.TrimEnd('/') == application["name"])
        {
            throw new ActionException("Give the service a name.");
        }
        var stateful = inputs["kind"] == nameof(ServiceKind.Stateful);
        return new NewService(name, inputs["type"], stateful, inputs.Int("count"), inputs.Int("partitions"), inputs.Optional("persisted") != "no");
    }

    private static string DescribeService(Args application, Args inputs)
    {
        var service = NewService(application, inputs);
        var count = service.Stateful
            ? WebActions.Count(service.Count, "replica")
            : service.Count == -1 ? "an instance on every node" : WebActions.Count(service.Count, "instance");
        return $"Create {service.Name} ({service.TypeName}, {(service.Stateful ? "stateful" : "stateless")}, {count}, {WebActions.Count(service.Partitions, "partition")})";
    }

    /// <summary>Null when the upgrade doesn't say.</summary>
    public static UpgradePlan? PlanOf(UpgradeReport report) => UpgradePlan.Of(
        Enum.TryParse<UpgradeMode>(report.Mode, out var mode) ? mode : null,
        Enum.TryParse<FailureAction>(report.OnFailure, out var onFailure) ? onFailure : null);
}
