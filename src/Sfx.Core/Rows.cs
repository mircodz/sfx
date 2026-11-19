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
    TimeSpan? Uptime);

public sealed record ApplicationRow(string Id, string Name, string TypeName, string TypeVersion, string Status, HealthState Health);

public sealed record ServiceRow(string Id, string Name, string TypeName, string Kind, string Status, HealthState Health);

public sealed record PartitionRow(PartitionId Id, string Kind, string PartitionKind, string Status, HealthState Health, string ReplicaSet);

public sealed record ReplicaRow(string Id, string NodeName, string Role, string Status, HealthState Health, string Address);
