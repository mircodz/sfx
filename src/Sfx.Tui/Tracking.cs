using Sfx.Core;

namespace Sfx.Tui;

/// <summary>
/// Whether a change the cluster accepted has been carried out, judged from what it reports now. Each check takes
/// data already fetched, so they stay easy to test.
/// </summary>
public static class Tracking
{
    /// <summary>A replica or instance moved off <paramref name="fromNode"/> (to <paramref name="toNode"/>, if one was asked for).</summary>
    public static OperationState ReplicaMove(string role, string replicaId, string fromNode, string? toNode, IReadOnlyList<ReplicaRow> replicas)
    {
        // Moving a primary swaps roles with a secondary, so its replica id may change: follow the role instead.
        if (role == "Primary")
        {
            if (replicas.FirstOrDefault(r => r.Role == "Primary") is not { } primary)
            {
                return OperationState.Running("waiting for a new primary");
            }
            var moved = toNode is null ? primary.NodeName != fromNode : primary.NodeName == toNode;
            return moved ? OperationState.Finished($"primary on {primary.NodeName}") : OperationState.Running($"primary still on {primary.NodeName}");
        }

        if (replicas.Any(r => r.Id == replicaId && r.NodeName == fromNode))
        {
            return OperationState.Running($"still on {fromNode}");
        }
        if (toNode is not null && !replicas.Any(r => r.NodeName == toNode))
        {
            return OperationState.Running($"not on {toNode} yet");
        }
        return OperationState.Finished(toNode is null ? $"moved off {fromNode}" : $"on {toNode}");
    }

    /// <summary>Every partition has the requested number of ready replicas or instances.</summary>
    /// <param name="requested">The target count; -1 (an instance on every node) can't be counted, so it's done right away.</param>
    public static OperationState Scale(int requested, IReadOnlyList<(string Status, int Ready)> partitions, bool stateful)
    {
        if (requested == -1)
        {
            return OperationState.Finished("an instance on every node");
        }

        var at = partitions.Count(p => p.Status == "Ready" && p.Ready == requested);
        var detail = $"{at} of {partitions.Count} partitions at {requested} {(stateful ? "replicas" : "instances")}";
        return partitions.Count > 0 && at == partitions.Count
            ? OperationState.Finished(detail)
            : OperationState.Running(detail, partitions.Count == 0 ? null : (double)at / partitions.Count);
    }

    /// <summary>A node reached <paramref name="expected"/> (Up, Down or Disabled).</summary>
    public static OperationState NodeStatus(string expected, string actual) =>
        actual == expected
            ? OperationState.Finished($"node is {actual}")
            : OperationState.Running($"node is {(actual.Length > 0 ? actual : "Unknown")}");

    /// <summary>A node is up again, and has been up for less time than since the restart was asked for.</summary>
    public static OperationState NodeRestart(NodeRow? node, TimeSpan sinceRequested)
    {
        var status = node?.Status ?? "";
        if (status != "Up")
        {
            return OperationState.Running($"node is {(status.Length > 0 ? status : "Unknown")}");
        }
        return node!.Uptime is { } up && up < sinceRequested
            ? OperationState.Finished("node is Up again")
            : OperationState.Running("waiting for the node to restart");
    }

    /// <summary>Something deleted is no longer listed.</summary>
    public static OperationState Gone(bool stillListed) =>
        stillListed ? OperationState.Running("still there") : OperationState.Finished("gone");

    /// <summary>
    /// A provisioned type version is available. Without a name (a package in the image store whose type isn't known),
    /// any type version that wasn't listed <paramref name="before"/> counts.
    /// </summary>
    /// <param name="before">"name version" of the types listed when provisioning was asked for.</param>
    public static OperationState TypeProvisioned(string? name, string? version, IReadOnlySet<string> before, IReadOnlyList<ApplicationTypeRow> now)
    {
        var candidates = name is not null && version is not null
            ? now.Where(t => t.Name == name && t.Version == version).ToList()
            : now.Where(t => !before.Contains(Key(t)) || t.Status != "Available").ToList();
        if (candidates.FirstOrDefault(t => t.Status == "Failed") is { } failed)
        {
            return OperationState.Failure($"{failed.Name} {failed.Version} failed to provision" + (failed.StatusDetails.Length > 0 ? $": {failed.StatusDetails}" : ""));
        }
        if (candidates.FirstOrDefault(t => t.Status != "Available") is { } busy)
        {
            return OperationState.Running($"{busy.Name} {busy.Version} is {busy.Status.ToLowerInvariant()}");
        }
        return candidates.FirstOrDefault() is { } done
            ? OperationState.Finished($"{done.Name} {done.Version} is available")
            : OperationState.Running("waiting for the type to show up");
    }

    /// <summary>How a type version is known in <see cref="TypeProvisioned"/>'s list of what was there before.</summary>
    public static string Key(ApplicationTypeRow type) => $"{type.Name} {type.Version}";

    /// <summary>
    /// An upgrade to <paramref name="target"/> has rolled through every domain; rolling back or failing ends it too.
    /// Until the cluster reports the new target (or a new start time), the upgrade it shows is the previous one.
    /// </summary>
    /// <param name="startedBefore">When the upgrade the cluster showed before this one started, if any.</param>
    public static OperationState Upgrade(UpgradeReport report, string? target, string? startedBefore = null)
    {
        if ((target is not null && report.Target.Length > 0 && report.Target != target)
            || (startedBefore is { Length: > 0 } && report.Started == startedBefore))
        {
            return OperationState.Running("waiting for the upgrade to start");
        }

        var done = report.Domains.Count(d => d.State == "Completed");
        var progress = report.Domains.Count == 0 ? (double?)null : (double)done / report.Domains.Count;
        var domains = report.Domains.Count == 0 ? "" : $"{done} of {report.Domains.Count} domains";
        return report.State switch
        {
            "RollingForwardCompleted" => OperationState.Finished("upgraded" + (domains.Length > 0 ? $", {domains}" : "")),
            "RollingBackCompleted" => OperationState.Failure("rolled back" + (report.FailureReason is { Length: > 0 } and not "None" ? $": {report.FailureReason}" : "")),
            "Failed" => OperationState.Failure("failed" + (report.FailureReason is { Length: > 0 } and not "None" ? $": {report.FailureReason}" : "")),
            "RollingBackInProgress" => OperationState.Running("rolling back", progress),
            "RollingForwardPending" => OperationState.Running(
                $"waiting to start {(report.NextDomain.Length > 0 ? "domain " + report.NextDomain : "the next domain")}" + (domains.Length > 0 ? $" ({domains})" : ""),
                progress),
            _ => OperationState.Running(domains.Length > 0 ? domains : "upgrading", progress),
        };
    }

    /// <summary>Something created is listed now.</summary>
    public static OperationState Created(bool listed) =>
        listed ? OperationState.Finished("created") : OperationState.Running("not listed yet");
}
