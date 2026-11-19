using Microsoft.ServiceFabric.Common;

namespace Sfx.Core;

/// <summary>A repair task: infrastructure work, like a reboot or patching, that the cluster coordinates.</summary>
/// <param name="Targets">The nodes it's for, comma-separated.</param>
/// <param name="Impact">How it affects each node, e.g. "_Node_1 Restart".</param>
/// <param name="Changed">When it last moved to a new state.</param>
public sealed record RepairTaskRow(
    string Id,
    string State,
    string Action,
    string Targets,
    string Impact,
    string Executor,
    string Result,
    DateTime? Created,
    DateTime? Changed,
    RepairTask Task)
{
    public bool IsActive => State is not ("Completed" or "Invalid" or "");
}

public sealed class RepairQueries(ClusterQueries queries)
{
    /// <summary>Every repair task the repair manager knows, active ones first, then newest first.</summary>
    public async Task<IReadOnlyList<RepairTaskRow>> GetRepairTasksAsync(CancellationToken cancellationToken)
    {
        if (!await queries.HasSystemServiceAsync("RepairManagerService", cancellationToken))
        {
            throw new InvalidOperationException(
                "This cluster doesn't run the repair manager (RepairManagerService); Azure clusters with Silver durability or more do.");
        }

        var tasks = await queries.Client.Repairs.GetRepairTaskListAsync(cancellationToken: cancellationToken) ?? [];

        return tasks.Select(ToRow)
            .OrderByDescending(t => t.IsActive)
            .ThenByDescending(t => t.Changed ?? t.Created)
            .ToList();
    }

    private static RepairTaskRow ToRow(RepairTask task)
    {
        var history = task.History;
        var changed = new[]
        {
            history?.CreatedUtcTimestamp, history?.ClaimedUtcTimestamp, history?.PreparingUtcTimestamp,
            history?.ApprovedUtcTimestamp, history?.ExecutingUtcTimestamp, history?.RestoringUtcTimestamp,
            history?.CompletedUtcTimestamp,
        }.Where(t => t is { Year: > 1 }).Max();

        return new RepairTaskRow(
            task.TaskId ?? "",
            task.State?.ToString() ?? "",
            task.Action ?? "",
            task.Target is NodeRepairTargetDescription nodes ? string.Join(", ", nodes.NodeNames ?? []) : task.Target?.Kind?.ToString() ?? "",
            task.Impact is NodeRepairImpactDescription impact
                ? string.Join(", ", (impact.NodeImpactList ?? []).Select(n => $"{n.NodeName} {n.ImpactLevel}"))
                : "",
            task.Executor ?? "",
            task.ResultStatus?.ToString() ?? "",
            history?.CreatedUtcTimestamp is { Year: > 1 } created ? created : null,
            changed,
            task);
    }
}
