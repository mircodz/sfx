using System.Globalization;
using Cellar.Terminal;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Tui;

/// <summary>
/// What can be done to each kind of entity. The same key means the same thing everywhere:
/// ctrl+r restart, ctrl+k remove, ctrl+d delete, m move, x deactivate, a activate, s scale, e edit, u upgrade,
/// ctrl+n create, p provision, o go to the node. Rarer actions have only a
/// <c>:</c> command (and the palette). Power actions show up after <c>:power</c>. Changes the cluster takes a while to
/// carry out are followed until they're done, in the activity strip.
/// </summary>
public sealed partial class Views
{
    private static readonly KeySequence Restart = "ctrl+r";
    private static readonly KeySequence Remove = "ctrl+k";
    private static readonly KeySequence Delete = "ctrl+d";
    private static readonly KeySequence Move = "m";
    private static readonly KeySequence Deactivate = "x";
    private static readonly KeySequence Activate = "a";
    private static readonly KeySequence Scale = "s";
    private static readonly KeySequence Create = "ctrl+n";
    private static readonly KeySequence Edit = "e";
    private static readonly KeySequence Upgrade = "u";
    private static readonly KeySequence Provision = "p";
    private static readonly KeySequence Upload = "u";
    private static readonly KeySequence GoToNode = "o";

    private const string NotForProduction = "Uses the Fault Analysis Service; meant for testing, not production.";

    private Operation Op(string title, string target, ActionRisk risk, Func<CancellationToken, Task> run, string? warning = null) =>
        new(title, target, risk, (ct, _) => run(ct), warning);

    public IReadOnlyList<EntityAction<ReplicaRow>> ReplicaActions(ServiceRow service, PartitionRow partition) =>
    [
        new("Restart replica", ActionRisk.Confirm,
            r => host.Run(Op($"Restart replica {r.Id} on {r.NodeName}", r.Id, ActionRisk.Confirm,
                ct => _ops.RestartReplicaAsync(r.NodeName, partition.Id, r.Id, ct),
                "Its process restarts; a primary hands over to a secondary first.")),
            [Restart], ["restart"],
            AppliesTo: _ => service.Kind == nameof(ServiceKind.Stateful)),

        new("Remove replica", ActionRisk.Destructive,
            r => host.Run(Op($"Remove {Describe(r)} from {r.NodeName}", r.Id, ActionRisk.Destructive,
                ct => _ops.RemoveReplicaAsync(r.NodeName, partition.Id, r.Id, force: false, ct),
                "The cluster closes it and builds a replacement elsewhere.")),
            [Remove], ["remove"]),

        new("Force-remove replica", ActionRisk.Destructive,
            r => host.Run(Op($"Force-remove {Describe(r)} from {r.NodeName}", r.Id, ActionRisk.Destructive,
                ct => _ops.RemoveReplicaAsync(r.NodeName, partition.Id, r.Id, force: true, ct),
                "Skips the graceful close: whatever state is on that node is dropped.")),
            Commands: ["remove-force"], Power: true),

        new("Move replica to another node", ActionRisk.Confirm,
            r => PickNode($"Move {Describe(r)} off {r.NodeName} to", exclude: r.NodeName, target =>
                host.Run(new Operation(
                    $"Move {Describe(r)} from {r.NodeName} to {target ?? "a node the cluster picks"}",
                    r.Id,
                    ActionRisk.Confirm,
                    (ct, options) => _ops.MoveReplicaAsync(r.Role, service.Id, partition.Id, r.NodeName, target, options.Contains('i'), ct),
                    r.Role == "Primary" ? "Clients see a failover while the primary moves." : null,
                    [new OperationOption('i', "ignore placement constraints")])
                {
                    Track = async (_, ct) => Tracking.ReplicaMove(r.Role, r.Id, r.NodeName, target, await queries.GetReplicasAsync(partition.Id, ct)),
                })),
            [Move], ["move"],
            AppliesTo: r => ClusterOperations.CanMove(r.Role)),

        new("Go to its node", ActionRisk.None, r => OpenNode(r.NodeName), [GoToNode], ["goto-node"],
            AppliesTo: r => r.NodeName.Length > 0),
    ];

    public IReadOnlyList<EntityAction<NodeRow>> NodeActions() =>
    [
        new("Activate node", ActionRisk.Confirm,
            n => host.Run(Op($"Activate {n.Name}", n.Name, ActionRisk.Confirm,
                ct => _ops.ActivateNodeAsync(n.Name, ct), "The cluster starts placing replicas on it again.") with
            {
                Track = (_, ct) => NodeReaches(n.Name, nameof(NodeStatus.Up), ct),
            }),
            [Activate], ["activate"],
            AppliesTo: n => n.Status is nameof(NodeStatus.Disabled) or nameof(NodeStatus.Disabling)),

        new("Deactivate node (pause, restart, remove data)", ActionRisk.Confirm,
            n => host.Pick($"Deactivate {n.Name}: why?", _ => Task.FromResult<IReadOnlyList<Choice>>(
            [
                DeactivateChoice(n, DeactivationIntent.Pause, ActionRisk.Confirm,
                    "Pause", "replicas stay", "Stop placing replicas on it; the ones there keep running."),
                DeactivateChoice(n, DeactivationIntent.Restart, ActionRisk.Confirm,
                    "Restart", "for a restart", "Move primaries off and close replicas so the node can be restarted."),
                DeactivateChoice(n, DeactivationIntent.RemoveData, ActionRisk.Destructive,
                    "Remove data", "data is lost", "Move every replica off: the node's data is expected to be lost (e.g. reimage)."),
            ])),
            [Deactivate], ["deactivate"],
            AppliesTo: n => n.Status is nameof(NodeStatus.Up) or nameof(NodeStatus.Enabling)),

        new("Restart node", ActionRisk.Destructive,
            n => host.Run(Op($"Restart {n.Name}", n.Name, ActionRisk.Destructive,
                ct => _ops.RestartNodeAsync(n.Name, ct), "Every replica on it restarts or fails over to another node.") with
            {
                Track = async (since, ct) => Tracking.NodeRestart(await FindNodeAsync(n.Name, ct), since),
            }),
            [Restart], ["restart"],
            AppliesTo: n => n.Status == nameof(NodeStatus.Up)),

        new("Stop node for a while", ActionRisk.Destructive,
            n => host.Ask("stop for how many seconds?", "600",
                text => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? null : "a whole number of seconds",
                text =>
                {
                    var seconds = int.Parse(text, CultureInfo.InvariantCulture);
                    host.Run(Op($"Stop {n.Name} for {seconds}s", n.Name, ActionRisk.Destructive,
                        ct => _ops.StopNodeAsync(n.Name, TimeSpan.FromSeconds(seconds), ct), NotForProduction) with
                    {
                        Track = (_, ct) => NodeReaches(n.Name, nameof(NodeStatus.Down), ct),
                    });
                }),
            Commands: ["stop"], Power: true,
            AppliesTo: n => n.Status == nameof(NodeStatus.Up)),

        new("Start a stopped node", ActionRisk.Confirm,
            n => host.Run(Op($"Start {n.Name}", n.Name, ActionRisk.Confirm, ct => _ops.StartNodeAsync(n.Name, ct), NotForProduction) with
            {
                Track = (_, ct) => NodeReaches(n.Name, nameof(NodeStatus.Up), ct),
            }),
            Commands: ["start"], Power: true,
            AppliesTo: n => n.Status == nameof(NodeStatus.Down)),

        new("Remove node state", ActionRisk.Destructive,
            n => host.Run(Op($"Remove the state of {n.Name}", n.Name, ActionRisk.Destructive,
                ct => _ops.RemoveNodeStateAsync(n.Name, ct),
                "Tells the cluster the node and its data are gone for good; replicas on it are treated as lost.")),
            Commands: ["remove-node-state"], Power: true,
            AppliesTo: n => n.Status == nameof(NodeStatus.Down)),
    ];

    public IReadOnlyList<EntityAction<PartitionRow>> PartitionActions(ServiceRow service) =>
    [
        new("Restart partition", ActionRisk.Confirm,
            p => host.Pick($"Restart partition {p.Id}", _ => Task.FromResult<IReadOnlyList<Choice>>(
            [
                RestartPartitionChoice(service, p, RestartPartitionMode.AllReplicasOrInstances, "All replicas or instances"),
                RestartPartitionChoice(service, p, RestartPartitionMode.OnlyActiveSecondaries, "Only active secondaries"),
            ])),
            [Restart], ["restart"], Power: true),

        new("Recover partition from quorum loss", ActionRisk.Destructive,
            p => host.Run(Op($"Recover partition {p.Id}", p.Id.ToString(), ActionRisk.Destructive,
                ct => _ops.RecoverPartitionAsync(p.Id, ct),
                "Forces the partition out of quorum loss with the replicas that are left; recent writes may be lost.")),
            Commands: ["recover"], Power: true),

        new("Reset partition load", ActionRisk.Confirm,
            p => host.Run(Op($"Reset the load of partition {p.Id}", p.Id.ToString(), ActionRisk.Confirm,
                ct => _ops.ResetPartitionLoadAsync(p.Id, ct), "Reported load goes back to the service's defaults.")),
            Commands: ["reset-load"]),
    ];

    public IReadOnlyList<EntityAction<ServiceRow>> ServiceActions(ApplicationRow app) =>
    [
        new("Scale service", ActionRisk.Confirm,
            s => host.Edit($"Scale {s.Name}", ct => ScaleFieldsAsync(s, ct), fields => ScaleOperation(s, fields)),
            [Scale], ["scale"],
            AppliesTo: s => app.Id != ClusterQueries.SystemApplicationId && s.Kind is nameof(ServiceKind.Stateful) or nameof(ServiceKind.Stateless)),

        new("Recover the service's partitions", ActionRisk.Confirm,
            s => host.Run(Op($"Recover the partitions of {s.Name}", s.Name, ActionRisk.Confirm,
                ct => _ops.RecoverServicePartitionsAsync(s.Id, ct),
                "Partitions stuck in quorum loss are forced out of it; recent writes may be lost.")),
            Commands: ["recover"]),

        new("Delete service", ActionRisk.Destructive,
            s => host.Run(new Operation($"Delete {s.Name}", s.Name, ActionRisk.Destructive,
                (ct, options) => _ops.DeleteServiceAsync(s.Id, options.Contains('f'), ct),
                "Its replicas and their state are removed.",
                [new OperationOption('f', "force (skip graceful close)")])
            {
                Track = async (_, ct) => Tracking.Gone((await queries.GetServicesAsync(app.Id, ct)).Any(other => other.Id == s.Id)),
            }),
            [Delete], ["delete"], Power: true,
            AppliesTo: _ => app.Id != ClusterQueries.SystemApplicationId),

        .. ServiceLifecycleActions(app),
    ];

    public IReadOnlyList<EntityAction<ApplicationRow>> ApplicationActions() =>
    [
        new("Delete application", ActionRisk.Destructive,
            a => host.Run(new Operation($"Delete {a.Name}", a.Name, ActionRisk.Destructive,
                (ct, options) => _ops.DeleteApplicationAsync(a.Id, options.Contains('f'), ct),
                "All its services, replicas and their state are removed.",
                [new OperationOption('f', "force (skip graceful close)")])
            {
                Track = async (_, ct) => Tracking.Gone((await queries.GetApplicationsAsync(ct)).Any(other => other.Id == a.Id)),
            }),
            [Delete], ["delete"], Power: true,
            AppliesTo: a => a.Id != ClusterQueries.SystemApplicationId),

        .. ApplicationLifecycleActions(),
    ];

    public IReadOnlyList<EntityAction<string>> ClusterActions() =>
    [
        new("Recover every partition in quorum loss", ActionRisk.Destructive,
            name => host.Run(Op("Recover all partitions in quorum loss", name, ActionRisk.Destructive,
                ct => _ops.RecoverAllPartitionsAsync(ct), "Every partition stuck in quorum loss is forced out; recent writes may be lost.")),
            Commands: ["recover"], Power: true),

        new("Recover system partitions in quorum loss", ActionRisk.Destructive,
            name => host.Run(Op("Recover system partitions in quorum loss", name, ActionRisk.Destructive,
                ct => _ops.RecoverSystemPartitionsAsync(ct), "System services stuck in quorum loss are forced out; recent writes may be lost.")),
            Commands: ["recover-system"], Power: true),

        .. ClusterUpgradeActions(),
    ];

    public IReadOnlyList<EntityAction<DeployedCodePackageRow>> CodePackageActions(string node, DeployedApplicationRow app) =>
    [
        new("Restart code package", ActionRisk.Confirm,
            c => host.Run(Op($"Restart code package {c.Name} of {app.Name} on {node}", c.Name, ActionRisk.Confirm,
                ct => _ops.RestartCodePackageAsync(node, app.Id, c, ct), "Its process is killed and started again; replicas in it fail over.")),
            [Restart], ["restart"],
            AppliesTo: c => c.InstanceId.Length > 0),
    ];

    private static string Describe(ReplicaRow replica) =>
        replica.Role == "Instance" ? $"instance {replica.Id}" : $"{replica.Role.ToLowerInvariant()} replica {replica.Id}";

    private Choice DeactivateChoice(NodeRow node, DeactivationIntent intent, ActionRisk risk, string title, string hint, string explanation) =>
        new(title, hint, () => host.Run(Op($"Deactivate {node.Name} ({title.ToLowerInvariant()})", node.Name, risk,
            ct => _ops.DeactivateNodeAsync(node.Name, intent, ct), explanation) with
        {
            Track = (_, ct) => NodeReaches(node.Name, nameof(NodeStatus.Disabled), ct),
        }));

    private async Task<NodeRow?> FindNodeAsync(string name, CancellationToken cancellationToken) =>
        (await queries.GetNodesAsync(cancellationToken)).FirstOrDefault(node => node.Name == name);

    private async Task<OperationState> NodeReaches(string name, string status, CancellationToken cancellationToken) =>
        Tracking.NodeStatus(status, (await FindNodeAsync(name, cancellationToken))?.Status ?? "");

    /// <summary>The scale form: a stateful service's target and minimum replica set sizes, or a stateless one's instance count.</summary>
    private async Task<IReadOnlyList<FormField>> ScaleFieldsAsync(ServiceRow service, CancellationToken cancellationToken) =>
        await _inspect.GetServiceDescriptionAsync(service.Id, cancellationToken) switch
        {
            StatefulServiceDescription stateful => StatefulScaleFields(stateful.TargetReplicaSetSize, stateful.MinReplicaSetSize),
            StatelessServiceDescription stateless => StatelessScaleFields(stateless.InstanceCount),
            _ => StatelessScaleFields(null),
        };

    internal static IReadOnlyList<FormField> StatefulScaleFields(int? targetSize, int? minSize)
    {
        var target = new FormField("count", "Target replica set size", Number(targetSize))
        {
            Kind = FormFieldKind.Number, Integer = true, Min = 1, Required = true,
            Hint = "how many replicas each partition keeps",
        };
        var min = new FormField("min", "Minimum replica set size", Number(minSize))
        {
            Kind = FormFieldKind.Number, Integer = true, Min = 1, Required = true,
            Hint = "below this, a partition stops taking writes",
            Validate = value => int.TryParse(value, CultureInfo.InvariantCulture, out var m) && int.TryParse(target.Value, CultureInfo.InvariantCulture, out var t) && m > t
                ? "can't be more than the target size" : null,
        };
        return [target, min];
    }

    internal static IReadOnlyList<FormField> StatelessScaleFields(int? instances) =>
    [
        new FormField("count", "Instance count", Number(instances))
        {
            Kind = FormFieldKind.Number, Integer = true, Min = -1, Required = true,
            Note = "-1 runs one on every node",
            Validate = value => value.Trim() == "0" ? "at least 1, or -1 for every node" : null,
        },
    ];

    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>What the scale form asks for, as an operation to review: each changed size, before and after.</summary>
    internal Operation ScaleOperation(ServiceRow service, IReadOnlyList<FormField> fields)
    {
        var count = int.Parse(fields[0].Value, CultureInfo.InvariantCulture);
        var stateful = service.Kind == nameof(ServiceKind.Stateful);
        int? min = fields.Count > 1 && fields[1].IsChanged ? int.Parse(fields[1].Value, CultureInfo.InvariantCulture) : null;
        var title = stateful
            ? $"Scale {service.Name} to {Count(count, "replica")}" + (min is { } m ? $" (min {m})" : "")
            : $"Scale {service.Name} to {(count == -1 ? "every node" : Count(count, "instance"))}";
        return new Operation(title, service.Name, ActionRisk.Confirm,
            (ct, _) => stateful
                ? _ops.ScaleStatefulServiceAsync(service.Id, count, min, ct)
                : _ops.ScaleStatelessServiceAsync(service.Id, count, ct),
            "The cluster adds or removes replicas to match; nothing restarts.")
        {
            Details = Changes(fields),
            Track = async (_, ct) => Tracking.Scale(count, await PartitionCountsAsync(service.Id, ct), stateful),
        };
    }

    /// <summary>Each partition's status and how many of its replicas or instances are ready.</summary>
    private async Task<IReadOnlyList<(string Status, int Ready)>> PartitionCountsAsync(string serviceId, CancellationToken cancellationToken)
    {
        var partitions = await queries.GetPartitionsAsync(serviceId, cancellationToken);
        var counts = new List<(string, int)>();
        foreach (var partition in partitions)
        {
            var replicas = await queries.GetReplicasAsync(partition.Id, cancellationToken);
            counts.Add((partition.Status, replicas.Count(r => r.Status == "Ready")));
        }
        return counts;
    }

    private static string Count(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")}";

    /// <summary>"Target replica set size  3 → 5" for every field the form changed.</summary>
    public static IReadOnlyList<StyledText> Changes(IEnumerable<FormField> fields)
    {
        var changed = fields.Where(field => field.IsChanged).ToList();
        var width = changed.Count == 0 ? 0 : changed.Max(field => field.Label.Length) + 2;
        return changed
            .Select(field => StyledText.Of(field.Label.PadRight(width)).Fg(Theme.Current.Muted)
                .Append(field.OriginalValue.Length == 0 ? "-" : field.OriginalValue).Fg(Theme.Current.Foreground)
                .Append(" → ").Fg(Theme.Current.Muted)
                .Append(field.Value.Length == 0 ? "-" : field.Value).Bold().Fg(Theme.Current.Accent))
            .ToList();
    }

    private Choice RestartPartitionChoice(ServiceRow service, PartitionRow partition, RestartPartitionMode mode, string title) =>
        new(title, null, () => host.Run(Op($"Restart partition {partition.Id} ({title.ToLowerInvariant()})", partition.Id.ToString(),
            ActionRisk.Confirm, ct => _ops.RestartPartitionAsync(service.Id, partition.Id, mode, ct), NotForProduction)));

    /// <summary>Picks a node that's up, or "let the cluster choose" (null).</summary>
    private void PickNode(string title, string exclude, Action<string?> pick) =>
        host.Pick(title, async ct =>
        {
            var nodes = await queries.GetNodesAsync(ct);
            return
            [
                new Choice("Let the cluster choose", "best fit", () => pick(null)),
                .. nodes
                    .Where(n => n.Name != exclude && n.Status == nameof(NodeStatus.Up))
                    .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(n => new Choice(n.Name, $"UD {n.UpgradeDomain} · FD {n.FaultDomain}", () => pick(n.Name))),
            ];
        });

    private IEnumerable<AppAction> Bind<T>(IEnumerable<EntityAction<T>> actions, T subject, string group) =>
        actions.Where(action => action.Applies(subject)).Select(action => action.Bind(subject, group, host));
}
