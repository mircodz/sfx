using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Web;

// What the browser gets. Health is always one of the UI's words: Healthy, Warning, Error or Unknown.

/// <param name="EventStore">Whether the cluster runs the event store; without it there are no events to show.</param>
/// <param name="RepairManager">Whether it runs the repair manager; without it there are no repair tasks.</param>
/// <param name="FaultAnalysis">Whether it runs the Fault Analysis Service, which stopping nodes and restarting partitions need.</param>
/// <param name="Views">The views in the config, by name; any of them can be passed as <c>?view=</c>.</param>
/// <param name="DefaultView">The view to start with: <c>--view</c>, else the current context's; null for none.</param>
public sealed record SessionDto(
    string Context, string Endpoint, bool ReadOnly, bool Power, string Version, bool EventStore, string? Folder, string? Label, string? Color,
    bool RepairManager, bool FaultAnalysis, IReadOnlyList<ViewDto> Views, string? DefaultView);

/// <summary>A view: the applications it shows (<paramref name="Apps"/>; all when empty) minus those it excludes.</summary>
public sealed record ViewDto(string Name, IReadOnlyList<string> Apps, IReadOnlyList<string> Exclude);

/// <summary>How the view in <c>?view=</c> splits the cluster's applications; health and counts elsewhere stay cluster-wide.</summary>
/// <param name="Applications">Applications in the view.</param>
/// <param name="HiddenApplications">Applications the view leaves out.</param>
/// <param name="HiddenUnhealthy">Of those left out, how many are in warning or error.</param>
public sealed record ViewSummaryDto(string Name, int Applications, int HiddenApplications, int HiddenUnhealthy);

/// <summary>The optional system services a cluster may run; what isn't there isn't offered.</summary>
public sealed record SystemServices(bool EventStore, bool RepairManager, bool FaultAnalysis);

public sealed record EvaluationDto(int Depth, string Kind, string Health, string Description);

public sealed record HealthEventDto(string Source, string Property, string Health, string Description, DateTime? Modified, bool Expired);

public sealed record HealthDto(string Health, IReadOnlyList<EvaluationDto> Evaluations, IReadOnlyList<HealthEventDto> Events);

public sealed record ClusterDto(string Name, string Endpoint, string CodeVersion, HealthDto Health, IReadOnlyList<ActionDto> Actions);

/// <summary>How many entities of one <paramref name="Kind"/> (Node, Application, Service, Partition or Replica) are in each state.</summary>
public sealed record HealthCountDto(string Kind, long Healthy, long Warning, long Error);

/// <summary>One load metric across the cluster, without each node's load.</summary>
/// <param name="ClusterCapacity">Null when unlimited.</param>
/// <param name="Balanced">Whether the resource balancer found the metric balanced on its last run.</param>
/// <param name="MinNode">
/// The name of the node with the least load (its id when the node isn't known); null when the cluster doesn't say, or
/// when no node has any of this load.
/// </param>
/// <param name="MaxNode">The name of the node with the most load, likewise.</param>
public sealed record LoadSummaryDto(
    string Name, double ClusterLoad, double? ClusterCapacity, bool? Balanced, string? MinNode, double? MinLoad, string? MaxNode, double? MaxLoad);

/// <summary>The cluster at a glance, cheap to ask even of a large cluster.</summary>
/// <param name="Health">The cluster's evaluations and unhealthy events.</param>
/// <param name="RunningUpgrades">
/// The cluster's upgrade when it's running, pending or failed, and the applications' that are running (those in the view).
/// </param>
/// <param name="View">The view in <c>?view=</c> and what it hides; null without one.</param>
public sealed record OverviewDto(
    string Name,
    string Endpoint,
    string CodeVersion,
    HealthDto Health,
    IReadOnlyList<HealthCountDto> Counts,
    IReadOnlyList<LoadSummaryDto> Load,
    IReadOnlyList<UpgradeDto> RunningUpgrades,
    ViewSummaryDto? View);

public sealed record NodeDto(
    string Name, string Address, string NodeType, string Status, string Health, string UpgradeDomain, string FaultDomain,
    bool IsSeed, string CodeVersion, long? UptimeSeconds, IReadOnlyList<ActionDto> Actions, string? Reason = null);

public sealed record MetricDto(string Name, double Value, double? Capacity);

public sealed record DeployedReplicaDto(
    string Application, string ApplicationId, string Service, string PartitionId, string Id, string Role, string Status, string CodePackage);

/// <param name="HiddenReplicas">How many replicas on the node the view in <c>?view=</c> leaves out; 0 without one.</param>
public sealed record NodeDetailDto(
    NodeDto Node, HealthDto Health, IReadOnlyList<MetricDto> Load, IReadOnlyList<DeployedReplicaDto> Replicas, int HiddenReplicas);

public sealed record ApplicationDto(string Id, string Name, string TypeName, string TypeVersion, string Status, string Health, string? Reason = null);

/// <param name="Overridden">Set on the application, rather than taken from the version's default.</param>
public sealed record ParameterDto(string Name, string Value, string? Default, bool Overridden);

/// <summary>A provisioned version of an application type and the parameters it declares, with their defaults.</summary>
public sealed record VersionDto(string Version, IReadOnlyDictionary<string, string> Defaults);

public sealed record ServiceDto(string Id, string Name, string TypeName, string Kind, string Status, string Health, string? Reason = null);

public sealed record ApplicationDetailDto(
    ApplicationDto Application,
    HealthDto Health,
    IReadOnlyList<ParameterDto> Parameters,
    IReadOnlyList<VersionDto> Versions,
    UpgradeDto? Upgrade,
    IReadOnlyList<ServiceDto> Services,
    IReadOnlyList<ActionDto> Actions,
    DefaultServicesDto DefaultServices,
    UpgradeDefaultsDto UpgradeDefaults);

/// <summary>What an upgrade of this application uses for anything its policy leaves out. Times in seconds; null is no limit.</summary>
public sealed record UpgradeDefaultsDto(
    long HealthCheckWait,
    long HealthCheckStable,
    long HealthCheckRetry,
    long? UpgradeDomainTimeout,
    long? UpgradeTimeout,
    long? ReplicaSetCheckTimeout,
    HealthPolicyDefaults HealthPolicy)
{
    public static UpgradeDefaultsDto For(string applicationManifest) => new(
        (long)UpgradeDefaults.HealthCheckWait.TotalSeconds,
        (long)UpgradeDefaults.HealthCheckStable.TotalSeconds,
        (long)UpgradeDefaults.HealthCheckRetry.TotalSeconds,
        null,
        null,
        null,
        UpgradeDefaults.HealthPolicy(applicationManifest));
}

/// <param name="UpgradeEnabled">ClusterManager/EnableDefaultServicesUpgrade: whether upgrades may change default services.</param>
/// <param name="Uses">Which parameters the current version's default services use, and for what.</param>
public sealed record DefaultServicesDto(bool UpgradeEnabled, IReadOnlyList<DefaultServiceUse> Uses);

public sealed record ServiceDetailDto(
    ServiceDto Service,
    string ApplicationId,
    HealthDto Health,
    IReadOnlyList<FieldDto> Description,
    IReadOnlyList<PartitionDto> Partitions,
    IReadOnlyList<ActionDto> Actions);

public sealed record FieldDto(string Name, string Value);

/// <param name="Scheme">Singleton, Int64Range or Named.</param>
/// <param name="LowKey">An Int64Range partition's lowest key, as a string (it may not fit a JavaScript number); else null.</param>
/// <param name="HighKey">An Int64Range partition's highest key.</param>
/// <param name="Name">A Named partition's name; else null.</param>
public sealed record PartitionDto(
    string Id, string Kind, string Scheme, string Status, string Health, string ReplicaSet, string? Reason = null,
    string? LowKey = null, string? HighKey = null, string? Name = null);

public sealed record ReplicaDto(string Id, string NodeName, string Role, string Status, string Health, string Address, IReadOnlyList<ActionDto> Actions);

/// <summary>One replica or instance anywhere in the cluster, with where it lives.</summary>
public sealed record ClusterReplicaDto(
    string ApplicationId,
    string Application,
    string ServiceId,
    string Service,
    string ServiceKind,
    string PartitionId,
    string PartitionStatus,
    string PartitionHealth,
    string ReplicaId,
    string Node,
    string Role,
    string Status,
    string Health);

public sealed record PartitionDetailDto(
    PartitionDto Partition,
    ServiceDto Service,
    HealthDto Health,
    IReadOnlyList<MetricDto> Load,
    IReadOnlyList<ReplicaDto> Replicas,
    IReadOnlyList<ActionDto> Actions);

public sealed record DomainDto(string Name, string State);

public sealed record UpgradingNodeDto(string Node, string Phase, IReadOnlyList<string> PendingSafetyChecks);

public sealed record UpgradeDto(
    string Name,
    string Kind,
    string? ApplicationId,
    string From,
    string Target,
    string State,
    string Mode,
    DateTime? Started,
    string CurrentDomain,
    string NextDomain,
    IReadOnlyList<DomainDto> Domains,
    IReadOnlyList<UpgradingNodeDto> Nodes,
    string FailureReason,
    string HealthCheckPhase,
    IReadOnlyList<EvaluationDto> Evaluations,
    IReadOnlyList<ActionDto> Actions);

/// <param name="EntityKind">What <paramref name="Entity"/> is: cluster, node, application, service, partition or replica.</param>
public sealed record EventDto(DateTime Time, string Kind, string EntityKind, string Category, string Entity, string Details);

public sealed record EventsDto(IReadOnlyList<EventDto> Events, string? Unavailable);

public sealed record ActivityDto(DateTimeOffset Time, string User, string Context, string Action, string Outcome, bool Succeeded);

public sealed record TextDto(string Text, string Format);

internal static class Dto
{
    public static string Health(HealthState? state) => state switch
    {
        HealthState.Ok => "Healthy",
        HealthState.Warning => "Warning",
        HealthState.Error => "Error",
        _ => "Unknown",
    };

    public static HealthDto Health(HealthReport report) => new(
        Health(report.State),
        report.Evaluations.Select(Evaluation).ToList(),
        report.Events
            .OrderBy(e => e.State switch { HealthState.Error => 0, HealthState.Warning => 1, HealthState.Ok => 2, _ => 3 })
            .ThenByDescending(e => e.LastModifiedUtc)
            .Select(e => new HealthEventDto(e.Source, e.Property, Health(e.State), e.Description, e.LastModifiedUtc, e.IsExpired))
            .ToList());

    private static readonly EntityKind[] CountedKinds = [EntityKind.Node, EntityKind.Application, EntityKind.Service, EntityKind.Partition, EntityKind.Replica];

    /// <summary>The counts of nodes, applications, services, partitions and replicas, in that order; other kinds are left out.</summary>
    public static IReadOnlyList<HealthCountDto> HealthCounts(HealthStatistics? statistics)
    {
        var counts = statistics?.HealthStateCountList ?? [];
        return CountedKinds
            .SelectMany(kind => counts.Where(c => c.EntityKind == kind).Take(1).Select(c => new HealthCountDto(
                kind.ToString(), c.HealthStateCount?.OkCount ?? 0, c.HealthStateCount?.WarningCount ?? 0, c.HealthStateCount?.ErrorCount ?? 0)))
            .ToList();
    }

    public static EvaluationDto Evaluation(HealthEvaluationRow e) => new(e.Depth, e.Kind, Health(e.State), e.Description);

    public static ApplicationDto Application(ApplicationRow a) => new(a.Id, a.Name, a.TypeName, a.TypeVersion, a.Status, Health(a.Health));

    public static ServiceDto Service(ServiceRow s) => new(s.Id, s.Name, s.TypeName, s.Kind, s.Status, Health(s.Health));

    public static PartitionDto Partition(PartitionRow p) => new(
        p.Id.ToString(), p.Kind, p.PartitionKind, p.Status, Health(p.Health), p.ReplicaSet, LowKey: p.LowKey, HighKey: p.HighKey, Name: p.Name);

    public static UpgradeDto Upgrade(UpgradeRow row, IReadOnlyList<ActionDto> actions)
    {
        var r = row.Report;
        return new UpgradeDto(
            row.Name,
            row.Kind,
            row.ApplicationId,
            row.From,
            r.Target,
            r.State,
            r.Mode,
            row.StartedUtc,
            r.CurrentDomain,
            r.NextDomain,
            r.Domains.Select(d => new DomainDto(d.Name, d.State)).ToList(),
            r.CurrentNodes.Select(n => new UpgradingNodeDto(n.Node, n.Phase, n.PendingSafetyChecks)).ToList(),
            r.FailureReason,
            r.HealthCheckPhase,
            r.Evaluations.Select(Evaluation).ToList(),
            actions);
    }
}
