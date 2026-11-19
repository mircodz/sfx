using Cellar.Layout;
using Cellar.Text;
using Cellar.Theming;
using Sfx.Core;
using static Sfx.Tui.Styles;

namespace Sfx.Tui;

/// <summary>Repair tasks: reboots, patching and other infrastructure work on nodes.</summary>
public sealed partial class Views
{
    private RepairQueries? _repairs;

    private RepairQueries RepairQueries => _repairs ??= new RepairQueries(queries);

    public IResourceView Repairs() =>
        new ResourceView<RepairTaskRow>("repair tasks", "repairs", RepairQueries.GetRepairTasksAsync, t => t.Id)
            .Describing(DescribeRepairTask)
            .Column("ID", Constraint.Fill(3), t => Plain(t.Id), t => t.Id)
            .Column("STATE", Constraint.Length(10), t => RepairState(t.State), t => t.IsActive ? 0 : 1)
            .Column("ACTION", Constraint.Fill(2), t => Plain(t.Action), t => t.Action)
            .Column("NODES", Constraint.Fill(2), t => Plain(t.Targets), t => t.Targets)
            .Column("IMPACT", Constraint.Fill(2), t => Plain(t.Impact), t => t.Impact)
            .Column("EXECUTOR", Constraint.Fill(2), t => Plain(t.Executor), t => t.Executor)
            .Column("RESULT", Constraint.Length(10), t => Plain(t.Result is "Invalid" or "Pending" ? "" : t.Result), t => t.Result)
            .Column("CHANGED", Constraint.Length(8), t => Plain(Age(t.Changed)), t => t.Changed, Justify.Right)
            .Sorting("STATE");

    public static IResourceView DescribeRepairTask(RepairTaskRow task) =>
        new DescribeView("repair task", task.Id,
        [
            Tab("Overview", _ => Task.FromResult(Lines.Fields(Inspect.Fields(task.Task, "History", "Target", "Impact")
                .Append(new Field("Nodes", task.Targets))
                .Append(new Field("Impact", task.Impact))))),
            Tab("History", _ => Task.FromResult(Lines.Fields(Inspect.Fields(task.Task.History ?? new())
                .Where(f => !f.Value.StartsWith("0001-01-01", StringComparison.Ordinal))))),
            Tab("Raw", _ => Task.FromResult(Lines.Json(task.Task))),
        ]);

    private static StyledText RepairState(string state) => state switch
    {
        "Completed" => Colored(state, Theme.Current.Success),
        "Executing" or "Restoring" or "Preparing" or "Approved" => Colored(state, Theme.Current.Warning),
        "" => Muted("-"),
        _ => Plain(state),
    };
}
