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
}
