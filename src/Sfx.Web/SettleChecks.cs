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

    private static string Reason(string what, string failureReason) =>
        failureReason is { Length: > 0 } and not "None" ? $"{what}: {failureReason}." : $"{what}.";
}
