using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Web;

/// <param name="Result">How it ended (e.g. Succeeded, Cancelled), once it has.</param>
public sealed record RepairDto(
    string Id,
    string Action,
    string State,
    string Executor,
    IReadOnlyList<string> Nodes,
    string Description,
    DateTime? Created,
    DateTime? Completed,
    string Result);

/// <param name="Unavailable">Why there are no repair tasks to show, when the cluster has no repair manager.</param>
public sealed record RepairsDto(IReadOnlyList<RepairDto> Repairs, string? Unavailable);

/// <summary>Active repair tasks first, then newest first.</summary>
internal static class ApiRepairs
{
    public static void Map(RouteGroupBuilder api) =>
        api.MapGet("/repairs", async (UiSession s, CancellationToken ct) =>
        {
            try
            {
                return new RepairsDto((await s.Repairs.GetRepairTasksAsync(ct)).Select(Repair).ToList(), null);
            }
            catch (InvalidOperationException ex)
            {
                // RepairQueries throws this when the cluster has no repair manager.
                return new RepairsDto([], ex.Message);
            }
        });

    private static RepairDto Repair(RepairTaskRow row) => new(
        row.Id,
        row.Action,
        row.State,
        row.Executor,
        row.Task.Target is NodeRepairTargetDescription nodes ? (nodes.NodeNames ?? []).ToList() : [],
        row.Task.Description ?? "",
        row.Created,
        row.Task.History?.CompletedUtcTimestamp is { Year: > 1 } completed ? completed : null,
        row.Result is "Invalid" ? "" : row.Result);
}
