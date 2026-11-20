using System.Globalization;
using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Web;

public sealed record ActionChoice(string Value, string Label, string? Hint = null);

/// <param name="Kind">How the browser asks for it: number, choice, flag, node (a node that's up, or none to let the
/// cluster choose), version (a provisioned version of the app's type) or parameters (the app's parameters).</param>
public sealed record ActionInputDto(string Name, string Label, string Kind, IReadOnlyList<ActionChoice>? Choices = null, string? Default = null);

/// <param name="Risk">"confirm" asks first; "destructive" also has the user type <paramref name="ConfirmText"/>.</param>
/// <param name="Target">Identifies the entity; the browser sends it back unchanged to run the action.</param>
/// <param name="Signature">Proves the server offered this action on this target, so a request can't change either.</param>
/// <param name="Form">Edited in a side panel with a review step, rather than confirmed in a dialog.</param>
public sealed record ActionDto(
    string Id,
    string Title,
    string Risk,
    string? Warning,
    string? ConfirmText,
    IReadOnlyDictionary<string, string> Target,
    IReadOnlyList<ActionInputDto> Inputs,
    string Signature,
    bool Form);

public sealed record ActionRequest(Dictionary<string, string> Target, Dictionary<string, string>? Inputs, string? Confirm, string? Signature);

public sealed record ActionResult(string Message);

/// <summary>A request the server refuses: bad input, or not allowed in this session.</summary>
public sealed class ActionException(string message, int status = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int Status => status;
}

internal sealed class Args(IReadOnlyDictionary<string, string> values)
{
    public IReadOnlyDictionary<string, string> Values => values;

    public string this[string name] =>
        values.TryGetValue(name, out var value) ? value : throw new ActionException($"Missing '{name}'.");

    public string? Optional(string name) => values.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    public bool Flag(string name) => Optional(name) is "true" or "1" or "on";

    public int Int(string name) =>
        int.TryParse(this[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ActionException($"'{name}' must be a whole number.");

    public PartitionId Partition(string name) =>
        Guid.TryParse(this[name], out var id) ? new PartitionId(id) : throw new ActionException($"'{name}' isn't a partition id.");
}

internal enum Risk { Confirm, Destructive }

/// <param name="Describe">What happens, for the confirmation and the action log, given the target and inputs.</param>
/// <param name="ConfirmText">What to type to confirm a destructive action.</param>
internal sealed record ActionDef(
    string Id,
    string Kind,
    string Title,
    Risk Risk,
    Func<Args, Args, string> Describe,
    Func<UiSession, Args, Args, CancellationToken, Task> Run,
    string? Warning = null,
    bool Power = false,
    Func<Args, bool>? Applies = null,
    Func<Args, string>? ConfirmText = null,
    Func<Args, IReadOnlyList<ActionInputDto>>? Inputs = null,
    bool Form = false);

/// <summary>
/// Everything the browser can change, mirroring the TUI's actions (src/Sfx.Tui/EntityActions.cs): read-only sessions get
/// none, power actions need power unlocked, and every run is checked again here and written to the action log.
/// </summary>
internal static class WebActions
{
    private const string NotForProduction = "Uses the Fault Analysis Service; meant for testing, not production.";

    private static readonly ActionChoice[] UpgradeModes =
    [
        new("monitored-rollback", "Monitored, roll back on failure"),
        new("monitored-manual", "Monitored, stop on failure"),
        new("unmonitored-auto", "Unmonitored, automatic"),
        new("unmonitored-manual", "Unmonitored, manual"),
    ];

    private static readonly IReadOnlyList<ActionDef> All =
    [
        // Replicas: target partitionId, replicaId, node, role, serviceId, serviceKind

        new("replica.restart", "replica", "Restart", Risk.Confirm,
            (t, _) => $"Restart replica {t["replicaId"]} on {t["node"]}",
            (s, t, _, ct) => s.Operations.RestartReplicaAsync(t["node"], t.Partition("partitionId"), t["replicaId"], ct),
            "Its process restarts; a primary hands over to a secondary first.",
            Applies: t => t["serviceKind"] == nameof(ServiceKind.Stateful)),

        new("replica.move", "replica", "Move", Risk.Confirm,
            (t, i) => $"Move {Describe(t)} from {t["node"]} to {i.Optional("node") ?? "a node the cluster picks"}",
            (s, t, i, ct) => s.Operations.MoveReplicaAsync(
                t["role"], t["serviceId"], t.Partition("partitionId"), t["node"], i.Optional("node"), i.Flag("ignoreConstraints"), ct),
            Applies: t => ClusterOperations.CanMove(t["role"]),
            Inputs: t =>
            [
                new("node", "Move to", "node", Default: ""),
                new("ignoreConstraints", "Ignore placement constraints", "flag"),
            ]),

        new("replica.remove", "replica", "Remove", Risk.Destructive,
            (t, _) => $"Remove {Describe(t)} from {t["node"]}",
            (s, t, _, ct) => s.Operations.RemoveReplicaAsync(t["node"], t.Partition("partitionId"), t["replicaId"], force: false, ct),
            "The cluster closes it and builds a replacement elsewhere.",
            ConfirmText: t => t["replicaId"]),

        new("replica.force-remove", "replica", "Force remove", Risk.Destructive,
            (t, _) => $"Force-remove {Describe(t)} from {t["node"]}",
            (s, t, _, ct) => s.Operations.RemoveReplicaAsync(t["node"], t.Partition("partitionId"), t["replicaId"], force: true, ct),
            "Skips the graceful close: whatever state is on that node is dropped.",
            Power: true,
            ConfirmText: t => t["replicaId"]),

        // Nodes: target node, status

        new("node.activate", "node", "Activate", Risk.Confirm,
            (t, _) => $"Activate {t["node"]}",
            (s, t, _, ct) => s.Operations.ActivateNodeAsync(t["node"], ct),
            "The cluster starts placing replicas on it again.",
            Applies: t => t["status"] is nameof(NodeStatus.Disabled) or nameof(NodeStatus.Disabling)),

        new("node.deactivate", "node", "Deactivate", Risk.Confirm,
            (t, i) => $"Deactivate {t["node"]} ({i["intent"].ToLowerInvariant()})",
            (s, t, i, ct) => s.Operations.DeactivateNodeAsync(t["node"], Enum.Parse<DeactivationIntent>(i["intent"]), ct),
            "Pause stops placing replicas on it; restart also moves primaries off and closes replicas.",
            Applies: t => t["status"] is nameof(NodeStatus.Up) or nameof(NodeStatus.Enabling),
            Inputs: _ =>
            [
                new("intent", "Why", "choice",
                [
                    new(nameof(DeactivationIntent.Pause), "Pause", "Replicas stay."),
                    new(nameof(DeactivationIntent.Restart), "Restart", "For a restart."),
                ], nameof(DeactivationIntent.Pause)),
            ]),

        new("node.remove-data", "node", "Deactivate and remove data", Risk.Destructive,
            (t, _) => $"Deactivate {t["node"]} (remove data)",
            (s, t, _, ct) => s.Operations.DeactivateNodeAsync(t["node"], DeactivationIntent.RemoveData, ct),
            "Moves every replica off: the node's data is expected to be lost, e.g. for a reimage.",
            Applies: t => t["status"] is nameof(NodeStatus.Up) or nameof(NodeStatus.Enabling),
            ConfirmText: t => t["node"]),

        new("node.restart", "node", "Restart", Risk.Destructive,
            (t, _) => $"Restart {t["node"]}",
            (s, t, _, ct) => s.Operations.RestartNodeAsync(t["node"], ct),
            "Every replica on it restarts or fails over to another node.",
            Applies: t => t["status"] == nameof(NodeStatus.Up),
            ConfirmText: t => t["node"]),

        new("node.stop", "node", "Stop for a while", Risk.Destructive,
            (t, i) => $"Stop {t["node"]} for {i.Int("seconds")}s",
            (s, t, i, ct) => s.Operations.StopNodeAsync(t["node"], TimeSpan.FromSeconds(Positive(i.Int("seconds"))), ct),
            NotForProduction,
            Power: true,
            Applies: t => t["status"] == nameof(NodeStatus.Up),
            ConfirmText: t => t["node"],
            Inputs: _ => [new("seconds", "Seconds", "number", Default: "600")]),

        new("node.start", "node", "Start", Risk.Confirm,
            (t, _) => $"Start {t["node"]}",
            (s, t, _, ct) => s.Operations.StartNodeAsync(t["node"], ct),
            NotForProduction,
            Power: true,
            Applies: t => t["status"] == nameof(NodeStatus.Down)),

        new("node.remove-state", "node", "Remove node state", Risk.Destructive,
            (t, _) => $"Remove the state of {t["node"]}",
            (s, t, _, ct) => s.Operations.RemoveNodeStateAsync(t["node"], ct),
            "Tells the cluster the node and its data are gone for good; replicas on it are treated as lost.",
            Power: true,
            Applies: t => t["status"] == nameof(NodeStatus.Down),
            ConfirmText: t => t["node"]),

        // Code packages: target node, applicationId, application, servicePackage, codePackage, instanceId, activationId

        new("codepackage.restart", "codepackage", "Restart", Risk.Confirm,
            (t, _) => $"Restart code package {t["codePackage"]} of {t["application"]} on {t["node"]}",
            (s, t, _, ct) => s.Operations.RestartCodePackageAsync(
                t["node"], t["applicationId"], t["servicePackage"], t["codePackage"], t["instanceId"], t["activationId"], ct),
            "Its process is killed and started again; replicas in it fail over.",
            Applies: t => t.Optional("instanceId") is not null),

        // Partitions: target partitionId, serviceId

        new("partition.reset-load", "partition", "Reset load", Risk.Confirm,
            (t, _) => $"Reset the load of partition {t["partitionId"]}",
            (s, t, _, ct) => s.Operations.ResetPartitionLoadAsync(t.Partition("partitionId"), ct),
            "Reported load goes back to the service's defaults."),

        new("partition.restart", "partition", "Restart", Risk.Confirm,
            (t, i) => $"Restart partition {t["partitionId"]} ({(i["mode"] == nameof(RestartPartitionMode.OnlyActiveSecondaries) ? "only active secondaries" : "all replicas or instances")})",
            (s, t, i, ct) => s.Operations.RestartPartitionAsync(t["serviceId"], t.Partition("partitionId"), Enum.Parse<RestartPartitionMode>(i["mode"]), ct),
            NotForProduction,
            Power: true,
            Inputs: _ =>
            [
                new("mode", "Restart", "choice",
                [
                    new(nameof(RestartPartitionMode.AllReplicasOrInstances), "All replicas or instances"),
                    new(nameof(RestartPartitionMode.OnlyActiveSecondaries), "Only active secondaries"),
                ], nameof(RestartPartitionMode.AllReplicasOrInstances)),
            ]),

        new("partition.recover", "partition", "Recover from quorum loss", Risk.Destructive,
            (t, _) => $"Recover partition {t["partitionId"]}",
            (s, t, _, ct) => s.Operations.RecoverPartitionAsync(t.Partition("partitionId"), ct),
            "Forces the partition out of quorum loss with the replicas that are left; recent writes may be lost.",
            Power: true,
            ConfirmText: t => t["partitionId"]),

        // Services: target serviceId, applicationId, name, kind, count, min

        new("service.scale", "service", "Scale", Risk.Confirm,
            (t, i) => t["kind"] == nameof(ServiceKind.Stateful)
                ? $"Scale {t["name"]} to {Count(i.Int("count"), "replica")}" + (i.Optional("min") is { } min ? $" (min {min})" : "")
                : $"Scale {t["name"]} to {(i.Int("count") == -1 ? "every node" : Count(i.Int("count"), "instance"))}",
            (s, t, i, ct) => t["kind"] == nameof(ServiceKind.Stateful)
                ? s.Operations.ScaleStatefulServiceAsync(t["serviceId"], i.Int("count"), i.Optional("min") is null ? null : i.Int("min"), ct)
                : s.Operations.ScaleStatelessServiceAsync(t["serviceId"], i.Int("count"), ct),
            "The cluster adds or removes replicas to match; nothing restarts.",
            Applies: t => t["applicationId"] != ClusterQueries.SystemApplicationId && t["count"].Length > 0,
            Inputs: t => t["kind"] == nameof(ServiceKind.Stateful)
                ?
                [
                    new("count", "Target replica set size", "number", Default: t["count"]),
                    new("min", "Minimum replica set size", "number", Default: t["min"]),
                ]
                : [new("count", "Instance count (-1 for every node)", "number", Default: t["count"])],
            Form: true),

        new("service.recover", "service", "Recover partitions", Risk.Confirm,
            (t, _) => $"Recover the partitions of {t["name"]}",
            (s, t, _, ct) => s.Operations.RecoverServicePartitionsAsync(t["serviceId"], ct),
            "Partitions stuck in quorum loss are forced out of it; recent writes may be lost."),

        new("service.delete", "service", "Delete", Risk.Destructive,
            (t, i) => $"Delete {t["name"]}" + (i.Flag("force") ? " (force)" : ""),
            (s, t, i, ct) => s.Operations.DeleteServiceAsync(t["serviceId"], i.Flag("force"), ct),
            "Its replicas and their state are removed.",
            Power: true,
            Applies: t => t["applicationId"] != ClusterQueries.SystemApplicationId,
            ConfirmText: t => t["name"],
            Inputs: _ => [new("force", "Force (skip graceful close)", "flag")]),

        // Applications: target applicationId, name, typeName, version

        new("application.upgrade", "application", "Edit", Risk.Confirm,
            (t, i) => $"Upgrade {t["name"]} to {i["version"]} ({Mode(i["mode"]).Label.ToLowerInvariant()})" + ChangedParameters(i)
                + (UpgradePolicies.Parse(i, ParseMode(i["mode"]).Mode == UpgradeMode.Monitored) is null ? "" : ", custom policy"),
            async (s, t, i, ct) =>
            {
                var (mode, failure) = ParseMode(i["mode"]);
                var policy = UpgradePolicies.Parse(i, mode == UpgradeMode.Monitored);
                var current = await s.Inspector.GetApplicationAsync(t["applicationId"], ct);
                var versions = await s.Inspector.GetApplicationTypeVersionsAsync(current.TypeName, ct);
                var allowed = (versions.FirstOrDefault(v => v.Version == i["version"])
                        ?? throw new ActionException($"Version {i["version"]} of {current.TypeName} isn't provisioned."))
                    .DefaultParameterList?.Keys.ToHashSet() ?? [];

                // Keep today's values the target version still has; a parameter it dropped would make the cluster refuse.
                var parameters = (current.Parameters ?? new Dictionary<string, string>())
                    .Where(kv => allowed.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                var currentDefaults = versions.FirstOrDefault(v => v.Version == current.TypeVersion)?.DefaultParameterList ?? new Dictionary<string, string>();
                var changed = new List<string>();
                foreach (var (key, value) in i.Values.Where(kv => kv.Key.StartsWith("param:", StringComparison.Ordinal)))
                {
                    var name = key["param:".Length..];
                    parameters[allowed.Contains(name) ? name : throw new ActionException($"Version {i["version"]} has no parameter '{name}'.")] = value;
                    // New in the target version: nothing running uses it yet, so it can't change an existing service.
                    var was = current.Parameters?.GetValueOrDefault(name) ?? currentDefaults.GetValueOrDefault(name);
                    if (was is not null && was != value)
                    {
                        changed.Add(name);
                    }
                }

                // "reset:Name" takes a value off the application, so the parameter follows its version's default again.
                var targetDefaults = versions.First(v => v.Version == i["version"]).DefaultParameterList ?? new Dictionary<string, string>();
                foreach (var key in i.Values.Keys.Where(k => k.StartsWith("reset:", StringComparison.Ordinal)))
                {
                    var name = key["reset:".Length..];
                    if (i.Values.ContainsKey("param:" + name))
                    {
                        throw new ActionException($"'{name}' can't be both set and reset to its default.");
                    }
                    parameters.Remove(name);
                    if (current.Parameters?.GetValueOrDefault(name) is { } was && was != targetDefaults.GetValueOrDefault(name))
                    {
                        changed.Add(name);
                    }
                }

                // Catch what the cluster would refuse with only "aka.ms/upgrade-defaultservices", and say why.
                // Only services that exist can be refused; default services new in the target version are simply created.
                var existing = (await s.Queries.GetServicesAsync(t["applicationId"], ct))
                    .Select(svc => svc.Name[(t["name"].Length + 1)..])
                    .ToHashSet();
                var uses = DefaultServices.ParameterUses(await s.Inspector.GetApplicationManifestAsync(current.TypeName, i["version"], ct));
                var enabled = await s.DefaultServicesUpgradeEnabledAsync(ct);
                foreach (var use in uses.Where(u => changed.Contains(u.Parameter) && existing.Contains(u.Service)))
                {
                    if (use.Partitioning)
                    {
                        throw new ActionException(
                            $"{use.Parameter} sets how {use.Service} is partitioned ({use.Setting}), which can't change once the service exists. " +
                            "Repartitioning means creating the service again.", StatusCodes.Status409Conflict);
                    }
                    if (!enabled)
                    {
                        throw new ActionException(
                            $"{use.Parameter} sets the {use.Setting} of the default service {use.Service}, and this cluster doesn't let " +
                            "upgrades change default services (ClusterManager/EnableDefaultServicesUpgrade is off). " +
                            (use.Setting is "InstanceCount" or "TargetReplicaSetSize" or "MinReplicaSetSize"
                                ? $"Scale {use.Service} instead."
                                : "Turn that setting on, or change the service directly."),
                            StatusCodes.Status409Conflict);
                    }
                }

                try
                {
                    await s.Operations.UpgradeApplicationAsync(t["applicationId"], t["name"], i["version"], parameters, mode, failure, policy, ct);
                }
                catch (Exception ex) when (DefaultServices.IsRefusal(ex))
                {
                    throw new ActionException(
                        $"The cluster refused: version {i["version"]} changes a default service, and this cluster doesn't let upgrades " +
                        "change default services (ClusterManager/EnableDefaultServicesUpgrade is off, see aka.ms/upgrade-defaultservices).",
                        StatusCodes.Status409Conflict);
                }
            },
            "Services restart domain by domain with the new version or parameters.",
            Applies: t => t["applicationId"] != ClusterQueries.SystemApplicationId,
            Inputs: t =>
            [
                new("version", "Version", "version", Default: t["version"]),
                new("mode", "Upgrade mode", "choice", UpgradeModes, "monitored-rollback"),
                new("parameters", "Parameters", "parameters"),
            ],
            Form: true),

        new("application.delete", "application", "Delete", Risk.Destructive,
            (t, i) => $"Delete {t["name"]}" + (i.Flag("force") ? " (force)" : ""),
            (s, t, i, ct) => s.Operations.DeleteApplicationAsync(t["applicationId"], i.Flag("force"), ct),
            "All its services, replicas and their state are removed.",
            Power: true,
            Applies: t => t["applicationId"] != ClusterQueries.SystemApplicationId,
            ConfirmText: t => t["name"],
            Inputs: _ => [new("force", "Force (skip graceful close)", "flag")]),

        // Upgrades: target kind (cluster or application), applicationId, name, state, nextDomain

        new("upgrade.resume", "upgrade", "Resume into next domain", Risk.Confirm,
            (t, _) => $"Resume the upgrade of {t["name"]} into {t["nextDomain"]}",
            (s, t, _, ct) => t["kind"] == "cluster"
                ? s.Operations.ResumeClusterUpgradeAsync(t["nextDomain"], ct)
                : s.Operations.ResumeApplicationUpgradeAsync(t["applicationId"], t["nextDomain"], ct),
            Applies: t => t["state"] == nameof(UpgradeState.RollingForwardPending) && t["nextDomain"].Length > 0),

        new("upgrade.rollback", "upgrade", "Roll back", Risk.Confirm,
            (t, _) => $"Roll back the upgrade of {t["name"]}",
            (s, t, _, ct) => t["kind"] == "cluster"
                ? s.Operations.RollbackClusterUpgradeAsync(ct)
                : s.Operations.RollbackApplicationUpgradeAsync(t["applicationId"], ct),
            "Domains already upgraded go back to the previous version.",
            Applies: t => t["state"] is nameof(UpgradeState.RollingForwardInProgress) or nameof(UpgradeState.RollingForwardPending)),

        // Cluster: target name, azureManaged ("true", "false" or "unknown")

        new("cluster.upgrade", "cluster", "Upgrade", Risk.Confirm,
            (_, i) => ClusterUpgrades.Describe(i, Mode(i["mode"]).Label, ParseMode(i["mode"]).Mode),
            (s, _, i, ct) => ClusterUpgrades.RunAsync(s, i, ParseMode(i["mode"]).Mode, ParseMode(i["mode"]).Failure, ct),
            "Nodes restart domain by domain on the new version or configuration.",
            // Azure's resource provider owns the upgrades of the clusters it manages.
            Applies: t => t.Optional("azureManaged") == "false",
            Inputs: _ =>
            [
                new("codeVersion", "Code version", "version"),
                new("configVersion", "Config version", "version"),
                new("mode", "Upgrade mode", "choice", UpgradeModes, "monitored-rollback"),
            ],
            Form: true),

        new("cluster.recover-all", "cluster", "Recover every partition in quorum loss", Risk.Destructive,
            (_, _) => "Recover all partitions in quorum loss",
            (s, _, _, ct) => s.Operations.RecoverAllPartitionsAsync(ct),
            "Every partition stuck in quorum loss is forced out; recent writes may be lost.",
            Power: true,
            ConfirmText: t => t["name"]),

        new("cluster.recover-system", "cluster", "Recover system partitions", Risk.Destructive,
            (_, _) => "Recover system partitions in quorum loss",
            (s, _, _, ct) => s.Operations.RecoverSystemPartitionsAsync(ct),
            "System services stuck in quorum loss are forced out; recent writes may be lost.",
            Power: true,
            ConfirmText: t => t["name"]),
    ];

    private static readonly Dictionary<string, ActionDef> ById = All.ToDictionary(a => a.Id);

    /// <summary>The actions this session may run on one entity.</summary>
    public static IReadOnlyList<ActionDto> For(UiSession session, string kind, IReadOnlyDictionary<string, string> target)
    {
        if (session.ReadOnly)
        {
            return [];
        }

        var args = new Args(target);
        return All
            .Where(a => a.Kind == kind && (!a.Power || session.Power) && (a.Applies?.Invoke(args) ?? true))
            .Select(a => new ActionDto(
                a.Id,
                a.Title,
                a.Risk == Risk.Destructive ? "destructive" : "confirm",
                a.Warning,
                a.ConfirmText?.Invoke(args),
                target,
                a.Inputs?.Invoke(args) ?? [],
                session.Sign(a.Id, target),
                a.Form))
            .ToList();
    }

    public static async Task<ActionResult> RunAsync(UiSession session, string id, ActionRequest request, CancellationToken cancellationToken)
    {
        if (!ById.TryGetValue(id, out var action))
        {
            throw new ActionException($"No action '{id}'.", StatusCodes.Status404NotFound);
        }
        if (session.ReadOnly)
        {
            throw new ActionException("This context is read-only.", StatusCodes.Status403Forbidden);
        }
        if (action.Power && !session.Power)
        {
            throw new ActionException("Unlock power actions first.", StatusCodes.Status403Forbidden);
        }

        // The target comes back from the browser; only run it if this server offered exactly this action on exactly this target.
        if (!session.Verify(id, request.Target ?? [], request.Signature))
        {
            throw new ActionException("This action wasn't offered on this target. Reload the page and try again.", StatusCodes.Status403Forbidden);
        }

        var target = new Args(request.Target ?? []);
        var inputs = new Args(request.Inputs ?? []);
        if (!(action.Applies?.Invoke(target) ?? true))
        {
            throw new ActionException($"'{action.Title}' doesn't apply here.", StatusCodes.Status409Conflict);
        }
        foreach (var input in action.Inputs?.Invoke(target) ?? [])
        {
            if (input is { Kind: "choice", Choices: { } choices } && !choices.Any(c => c.Value == inputs.Optional(input.Name)))
            {
                throw new ActionException($"'{input.Label}' must be one of: {string.Join(", ", choices.Select(c => c.Label))}.");
            }
        }
        if (action.ConfirmText?.Invoke(target) is { } expected && request.Confirm != expected)
        {
            throw new ActionException($"Type '{expected}' to confirm.");
        }

        var title = action.Describe(target, inputs);
        try
        {
            await action.Run(session, target, inputs, cancellationToken);
        }
        catch (Exception ex) when (ex is not ActionException)
        {
            session.TryLog(title, ex.GetBaseException().Message);
            throw;
        }

        // The change is made: whatever happens to the log, don't report it as failed, or a retry would repeat it.
        var logError = session.TryLog(title, "ok");
        session.Track(action.Id, title, target, inputs);
        return logError is not null
            ? new ActionResult($"{title}. Done, but the action log couldn't be written ({logError})")
            : new ActionResult(title);
    }

    private static string Describe(Args replica) =>
        replica["role"] == "Instance" ? $"instance {replica["replicaId"]}" : $"{replica["role"].ToLowerInvariant()} replica {replica["replicaId"]}";

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static int Positive(int n) => n > 0 ? n : throw new ActionException("Must be more than 0.");

    private static ActionChoice Mode(string value) =>
        UpgradeModes.FirstOrDefault(m => m.Value == value) ?? throw new ActionException($"Unknown upgrade mode '{value}'.");

    private static (UpgradeMode Mode, FailureAction? Failure) ParseMode(string value) => Mode(value).Value switch
    {
        "monitored-rollback" => (UpgradeMode.Monitored, FailureAction.Rollback),
        "monitored-manual" => (UpgradeMode.Monitored, FailureAction.Manual),
        "unmonitored-auto" => (UpgradeMode.UnmonitoredAuto, null),
        _ => (UpgradeMode.UnmonitoredManual, null),
    };

    private static string ChangedParameters(Args inputs)
    {
        var set = inputs.Values.Keys.Where(k => k.StartsWith("param:", StringComparison.Ordinal)).Select(k => k["param:".Length..]).ToList();
        var reset = inputs.Values.Keys.Where(k => k.StartsWith("reset:", StringComparison.Ordinal)).Select(k => k["reset:".Length..]).ToList();
        return (set.Count == 0 ? "" : $", setting {string.Join(", ", set)}") + (reset.Count == 0 ? "" : $", resetting {string.Join(", ", reset)}");
    }
}
