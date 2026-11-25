using Cellar.Layout;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Sfx.Core;
using static Sfx.Tui.Styles;

namespace Sfx.Tui;

/// <summary>One change sfx made or is making, as the activity view shows it.</summary>
/// <param name="Result">"running", "ok", "failed" or "canceled".</param>
/// <param name="Detail">Where a running change has got to, or why it failed.</param>
/// <param name="Took">How long it ran; only known for this session's operations.</param>
public sealed record ActivityRow(DateTimeOffset Time, string Context, string Action, string Result, string Detail, TimeSpan? Took, string User);

/// <summary>
/// The changes of this session, running ones included, as Cellar <see cref="Cellar.Widgets.Activity"/> tasks (the
/// footer's strip shows them), with the context each was made in. Finished ones are also in the action log;
/// <see cref="Rows"/> merges both, so each change shows once.
/// </summary>
public sealed class SessionActivity(ActionLog log, TimeProvider? clock = null)
{
    private readonly object _lock = new();
    private readonly Dictionary<long, (string Context, ActionLogEntry? Logged)> _tasks = [];

    public Activity Tasks { get; } = new(clock) { KeepFinished = 100 };

    public ActivityTask Start(string context, string action)
    {
        var task = Tasks.Start(action);
        lock (_lock)
        {
            _tasks[task.Id] = (context, null);
        }
        return task;
    }

    /// <summary>Notes the task's line in the action log, so <see cref="Rows"/> doesn't list it twice.</summary>
    public void Logged(ActivityTask task, ActionLogEntry? entry)
    {
        lock (_lock)
        {
            if (_tasks.TryGetValue(task.Id, out var known))
            {
                _tasks[task.Id] = known with { Logged = entry };
            }
        }
    }

    /// <summary>This session's operations and the action log's earlier entries, newest first.</summary>
    public IReadOnlyList<ActivityRow> Rows()
    {
        var now = Tasks.Clock.GetUtcNow();
        var snapshot = Tasks.Snapshot();
        List<ActivityRow> session = [];
        HashSet<ActionLogEntry> logged = [];
        lock (_lock)
        {
            foreach (var task in snapshot)
            {
                var (context, entry) = _tasks.GetValueOrDefault(task.Id, ("", null));
                if (entry is not null)
                {
                    logged.Add(entry);
                }
                session.Add(new ActivityRow(
                    task.Started.ToLocalTime(),
                    context,
                    task.Title,
                    task.State switch
                    {
                        ActivityState.Running => "running",
                        ActivityState.Succeeded => "ok",
                        ActivityState.Failed => "failed",
                        _ => "canceled",
                    },
                    task.Message ?? "",
                    task.Elapsed(now),
                    Environment.UserName));
            }
        }

        var earlier = log.ReadLast()
            .Where(entry => !logged.Contains(entry))
            .Select(entry => new ActivityRow(
                entry.Time,
                entry.Context,
                entry.Action,
                entry.Succeeded ? "ok" : "failed",
                entry.Succeeded ? "" : entry.Outcome.StartsWith("failed: ", StringComparison.Ordinal) ? entry.Outcome[8..] : entry.Outcome,
                null,
                entry.User));

        return session.Concat(earlier).OrderByDescending(row => row.Time).ToList();
    }
}

public sealed partial class Views
{
    /// <summary>Changes made from sfx: this session's (running ones too, with where they've got to), then the action log's.</summary>
    public static IResourceView Activity(SessionActivity activity) =>
        new ResourceView<ActivityRow>("activity", "activity", _ => Task.Run(activity.Rows), a => $"{a.Time:O}{a.Action}")
            {
                Empty = "nothing changed through sfx yet",
            }
            .Column("TIME", Constraint.Length(19), a => Plain(When(a.Time)), a => a.Time)
            .Column("RESULT", Constraint.Length(8), a => Result(a.Result), a => a.Result)
            .Column("ACTION", Constraint.Fill(3), a => Plain(a.Action), a => a.Action)
            .Column("CONTEXT", Constraint.Fill(1), a => Plain(a.Context), a => a.Context)
            .Column("DETAIL", Constraint.Fill(2), Detail, a => a.Detail)
            .Column("TOOK", Constraint.Length(6), a => Plain(a.Took is { } took ? Seconds(took) : ""), a => a.Took, Justify.Right)
            .Sorting("TIME", SortState.Descending);

    /// <summary>"14:02:11" today, "Nov 18 14:02" before.</summary>
    private static string When(DateTimeOffset time) =>
        time.LocalDateTime.Date == DateTime.Today ? time.LocalDateTime.ToString("HH:mm:ss") : time.LocalDateTime.ToString("MMM dd HH:mm");

    private static string Seconds(TimeSpan took) => took.TotalSeconds < 60 ? $"{took.TotalSeconds:0.0}s" : Duration(took);

    private static StyledText Detail(ActivityRow row) =>
        row.Detail.Length == 0 ? Muted("")
        : row.Result == "failed" ? Colored(row.Detail, Theme.Current.Error)
        : Muted(row.Detail);

    private static StyledText Result(string result) => result switch
    {
        "ok" => Colored("ok", Theme.Current.Success),
        "running" => Colored("running", Theme.Current.Warning),
        "canceled" => Muted("canceled"),
        _ => Colored(result, Theme.Current.Error),
    };
}
