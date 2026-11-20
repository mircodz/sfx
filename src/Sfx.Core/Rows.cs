using Microsoft.ServiceFabric.Common;

namespace Sfx.Core;

public sealed record NodeRow(
    string Name,
    string Address,
    string NodeType,
    string Status,
    HealthState Health,
    string UpgradeDomain,
    string FaultDomain,
    bool IsSeed,
    string CodeVersion,
    TimeSpan? Uptime,
    string Id = "");

public sealed record ApplicationRow(string Id, string Name, string TypeName, string TypeVersion, string Status, HealthState Health);

public sealed record ServiceRow(string Id, string Name, string TypeName, string Kind, string Status, HealthState Health);

/// <param name="PartitionKind">How the service is partitioned: Singleton, Int64Range or Named.</param>
/// <param name="LowKey">For an Int64Range partition, its lowest key; as text, since keys go beyond what JSON numbers hold.</param>
/// <param name="HighKey">For an Int64Range partition, its highest key.</param>
/// <param name="Name">For a Named partition, its name.</param>
public sealed record PartitionRow(
    PartitionId Id, string Kind, string PartitionKind, string Status, HealthState Health, string ReplicaSet,
    string? LowKey = null, string? HighKey = null, string? Name = null);

public sealed record ReplicaRow(string Id, string NodeName, string Role, string Status, HealthState Health, string Address);
