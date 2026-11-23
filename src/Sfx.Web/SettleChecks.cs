namespace Sfx.Web;

/// <summary>
/// Whether a tracked operation has finished, judged from what the cluster reports. Each check takes data already
/// fetched, so <see cref="OperationWatcher"/> does the asking and these stay easy to test.
/// </summary>
internal static class SettleChecks
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";

    /// <summary>How far the cluster's clock may be behind ours before an upgrade it started for us looks older than the request.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>A replica or instance moved off <paramref name="fromNode"/> (to <paramref name="toNode"/>, if one was asked for).</summary>
    /// <param name="replicas">The partition's replicas now: id, node and role.</param>
    public static (string State, string? Detail) ReplicaMove(
        string role, string replicaId, string fromNode, string? toNode, IReadOnlyList<(string Id, string Node, string Role)> replicas)
    {
        // Moving a primary swaps roles with a secondary, so its replica id may change: follow the role instead.
        if (role == "Primary")
        {
            var primary = replicas.FirstOrDefault(r => r.Role == "Primary");
            if (primary.Node is null)
            {
                return (Running, "Waiting for a new primary.");
            }
            var moved = toNode is null ? primary.Node != fromNode : primary.Node == toNode;
            return moved ? (Done, $"Primary on {primary.Node}.") : (Running, $"Primary still on {primary.Node}.");
        }

        if (replicas.Any(r => r.Id == replicaId && r.Node == fromNode))
        {
            return (Running, $"Still on {fromNode}.");
        }
        if (toNode is not null && !replicas.Any(r => r.Node == toNode))
        {
            return (Running, $"Not on {toNode} yet.");
        }
        return (Done, toNode is null ? $"Moved off {fromNode}." : $"On {toNode}.");
    }

    /// <summary>Every partition has the requested number of ready replicas or instances.</summary>
    /// <param name="requested">The target count; -1 (an instance on every node) can't be counted, so it's done right away.</param>
    /// <param name="partitions">Per partition: its status and how many of its replicas or instances are ready.</param>
    public static (string State, string? Detail) ScaleProgress(int requested, IReadOnlyList<(string Status, int Count)> partitions, bool stateful = true)
    {
        if (requested == -1)
        {
            return (Done, "An instance on every node.");
        }

        var unit = stateful ? "replicas" : "instances";
        var at = partitions.Count(p => p.Status == "Ready" && p.Count == requested);
        var detail = $"{at} of {partitions.Count} partitions at {requested} {unit}.";
        return partitions.Count > 0 && at == partitions.Count ? (Done, detail) : (Running, detail);
    }

    /// <summary>A node reached <paramref name="expected"/> (Up, Down or Disabled).</summary>
    public static (string State, string? Detail) NodeStatus(string expected, string actual) =>
        actual == expected ? (Done, $"Node is {actual}.") : (Running, $"Node is {(actual.Length > 0 ? actual : "Unknown")}.");

    /// <summary>A node is up again, and has been up for less time than since the restart was asked for.</summary>
    public static (string State, string? Detail) NodeRestart(string status, TimeSpan? uptime, TimeSpan sinceRequested)
    {
        if (status != "Up")
        {
            return (Running, $"Node is {(status.Length > 0 ? status : "Unknown")}.");
        }
        return uptime is { } up && up < sinceRequested ? (Done, "Node is Up again.") : (Running, "Waiting for the node to restart.");
    }

    /// <summary>Whether the upgrade the cluster reports is the one an operation started, rather than an earlier one.</summary>
    public static bool IsThisUpgrade(DateTime? upgradeStartedUtc, DateTime operationStartedUtc) =>
        upgradeStartedUtc is { } at && at >= operationStartedUtc - ClockSkew;

    /// <summary>An upgrade going forward: done when it completes, failed when it rolls back or fails.</summary>
    public static (string State, string? Detail) UpgradeOutcome(
        string state, string failureReason, string currentDomain, string nextDomain, int domainsDone, int domains) => state switch
    {
        "RollingForwardCompleted" => (Done, "Upgrade completed."),
        "RollingBackCompleted" => (Failed, Reason("Rolled back", failureReason)),
        "RollingBackInProgress" => (Running, Reason("Rolling back", failureReason)),
        "Failed" => (Failed, Reason("Failed", failureReason)),
        "RollingForwardPending" => (Running, $"Waiting to be resumed into UD {nextDomain}."),
        _ => (Running, $"UD {(currentDomain.Length > 0 ? currentDomain : "-")}, {domainsDone} of {domains} domains done."),
    };

    /// <summary>An upgrade rolling back: done once it's back on the previous version.</summary>
    public static (string State, string? Detail) RollbackOutcome(string state, string failureReason, string currentDomain) => state switch
    {
        "RollingBackCompleted" => (Done, "Rolled back."),
        "RollingForwardCompleted" => (Failed, "The upgrade completed before it could roll back."),
        "Failed" => (Failed, Reason("Failed", failureReason)),
        _ => (Running, currentDomain.Length > 0 ? $"Rolling back, UD {currentDomain}." : "Rolling back."),
    };

    /// <summary>How long a provisioned type may take to be listed at all before the provisioning counts as lost.</summary>
    public static readonly TimeSpan ListingWait = TimeSpan.FromMinutes(2);

    /// <summary>A new application or service reached <paramref name="ready"/> (Ready, Active).</summary>
    /// <param name="status">Its status now; null while the cluster doesn't list it.</param>
    /// <param name="noun">"application" or "service".</param>
    public static (string State, string? Detail) EntityStatus(string? status, string ready, string noun) => status switch
    {
        null or "" => (Running, $"Waiting for the {noun} to appear."),
        _ when status == ready => (Done, $"{Capital(noun)} is {ready}."),
        "Failed" => (Failed, $"{Capital(noun)} failed."),
        _ => (Running, $"{Capital(noun)} is {status}."),
    };

    /// <summary>A type version provisioned: Available. <paramref name="row"/> is null while the cluster doesn't list it.</summary>
    public static (string State, string? Detail) TypeProvisioned(Sfx.Core.ApplicationTypeRow? row, string name, string version, TimeSpan elapsed) => row?.Status switch
    {
        "Available" => (Done, $"{name} {version} is available."),
        "Failed" => (Failed, row.StatusDetails is { Length: > 0 } details ? $"Provisioning failed: {details.TrimEnd('.')}." : "Provisioning failed."),
        "Provisioning" => (Running, $"Provisioning {name} {version}."),
        null when elapsed >= ListingWait => (Failed, $"The cluster never listed {name} {version}; check the package's manifests."),
        null => (Running, $"Waiting for the cluster to list {name} {version}."),
        var other => (Running, $"{name} {version} is {other}."),
    };

    /// <summary>
    /// A package provisioned whose type and version weren't known up front: judged by the type versions the cluster
    /// lists now that it didn't before.
    /// </summary>
    public static (string State, string? Detail) NewTypeProvisioned(
        IReadOnlySet<(string Name, string Version)> before, IReadOnlyList<Sfx.Core.ApplicationTypeRow> now, TimeSpan elapsed)
    {
        var added = now.Where(t => !before.Contains((t.Name, t.Version))).ToList();
        if (added.FirstOrDefault(t => t.Status == "Provisioning") is { } provisioning)
        {
            return TypeProvisioned(provisioning, provisioning.Name, provisioning.Version, elapsed);
        }
        if (added.FirstOrDefault(t => t.Status == "Failed") is { } failed)
        {
            return TypeProvisioned(failed, failed.Name, failed.Version, elapsed);
        }
        if (added.FirstOrDefault(t => t.Status == "Available") is { } available)
        {
            return TypeProvisioned(available, available.Name, available.Version, elapsed);
        }
        return elapsed >= ListingWait
            ? (Failed, "The cluster never listed a new application type; check the package's manifests.")
            : (Running, "Waiting for the cluster to list the application type.");
    }

    /// <summary>A type version unprovisioned: no longer listed.</summary>
    public static (string State, string? Detail) TypeUnprovisioned(Sfx.Core.ApplicationTypeRow? row) => row?.Status switch
    {
        null => (Done, "Unprovisioned."),
        "Failed" => (Failed, row.StatusDetails is { Length: > 0 } details ? $"Failed: {details.TrimEnd('.')}." : "Failed."),
        var status => (Running, $"{status}."),
    };

    /// <summary>A running upgrade switched to the plan <paramref name="wanted"/>.</summary>
    /// <param name="current">The plan it follows now, by id; null when it doesn't say.</param>
    public static (string State, string? Detail) PlanSwitched(string? current, Sfx.Core.UpgradePlan wanted, bool inProgress) =>
        current == wanted.Id ? (Done, $"Now {wanted.Title.ToLowerInvariant()}.")
        : !inProgress ? (Done, "The upgrade is no longer running.")
        : (Running, "Waiting for the upgrade to switch.");

    private static string Capital(string noun) => noun.Length == 0 ? noun : char.ToUpperInvariant(noun[0]) + noun[1..];

    private static string Reason(string what, string failureReason) =>
        failureReason is { Length: > 0 } and not "None" ? $"{what}: {failureReason}." : $"{what}.";
}
