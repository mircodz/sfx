using Cellar.Layout;
using Cellar.Text;
using Cellar.Theming;
using Sfx.Core;
using static Sfx.Tui.Styles;

namespace Sfx.Tui;

/// <summary>One change sfx made or is making, as the activity view shows it.</summary>
/// <param name="Result">"running", "ok" or "failed".</param>
/// <param name="Took">How long it ran; only known for this session's operations.</param>
public sealed record ActivityRow(
    DateTimeOffset Time,
    string Context,
    string Action,
    string Result,
    string Detail,
    TimeSpan? Took,
    string User);

/// <summary>
/// The operations of this session, including those still running. Finished ones are also in the action log;
/// <see cref="Rows"/> merges both, so each operation shows once.
/// </summary>
public sealed class ActivityTracker(ActionLog log)
{
    private readonly object _lock = new();
    private readonly List<Entry> _operations = [];

    public sealed class Entry
    {
        internal Entry(string context, string action)
        {
            Context = context;
            Action = action;
        }

        public DateTimeOffset Started { get; } = DateTimeOffset.Now;

        public string Context { get; }

        public string Action { get; }

        public DateTimeOffset? Finished { get; internal set; }

        public bool? Succeeded { get; internal set; }

        public string Detail { get; internal set; } = "";

        /// <summary>Its line in the action log, once written.</summary>
        public ActionLogEntry? Logged { get; internal set; }
    }

    public Entry Start(string context, string action)
    {
        var operation = new Entry(context, action);
        lock (_lock)
        {
            _operations.Add(operation);
        }
        return operation;
    }

    public void Finish(Entry operation, bool succeeded, string detail, ActionLogEntry? logged)
    {
        lock (_lock)
        {
            operation.Finished = DateTimeOffset.Now;
            operation.Succeeded = succeeded;
            operation.Detail = detail;
            operation.Logged = logged;
        }
    }

    /// <summary>This session's operations and the action log's earlier entries, newest first.</summary>
    public IReadOnlyList<ActivityRow> Rows()
    {
        List<ActivityRow> session;
        HashSet<ActionLogEntry> logged;
        lock (_lock)
        {
            session = _operations.Select(o => new ActivityRow(
                o.Started,
                o.Context,
                o.Action,
                o.Succeeded switch { null => "running", true => "ok", false => "failed" },
                o.Detail,
                (o.Finished ?? DateTimeOffset.Now) - o.Started,
                Environment.UserName)).ToList();
            logged = _operations.Select(o => o.Logged).OfType<ActionLogEntry>().ToHashSet();
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
    /// <summary>Changes made from sfx: this session's (running ones too), then the action log's.</summary>
    public static IResourceView Activity(ActivityTracker activity) =>
        new ResourceView<ActivityRow>("activity", "activity", _ => Task.Run(activity.Rows), a => $"{a.Time:O}{a.Action}")
            .Column("TIME", Constraint.Length(19), a => Plain(When(a.Time)), a => a.Time)
            .Column("RESULT", Constraint.Length(8), a => Result(a.Result), a => a.Result)
            .Column("ACTION", Constraint.Fill(3), a => Plain(a.Action), a => a.Action)
            .Column("CONTEXT", Constraint.Fill(1), a => Plain(a.Context), a => a.Context)
            .Column("DETAIL", Constraint.Fill(2), a => a.Detail.Length > 0 ? Colored(a.Detail, Theme.Current.Error) : Muted(""), a => a.Detail)
            .Column("TOOK", Constraint.Length(6), a => Plain(a.Took is { } took ? Seconds(took) : ""), a => a.Took, Justify.Right)
            .Sorting("TIME", Cellar.Widgets.SortState.Descending);

    /// <summary>"14:02:11" today, "Nov 18 14:02" before.</summary>
    private static string When(DateTimeOffset time) =>
        time.LocalDateTime.Date == DateTime.Today ? time.LocalDateTime.ToString("HH:mm:ss") : time.LocalDateTime.ToString("MMM dd HH:mm");

    private static string Seconds(TimeSpan took) => took.TotalSeconds < 60 ? $"{took.TotalSeconds:0.0}s" : Duration(took);

    private static StyledText Result(string result) => result switch
    {
        "ok" => Colored("ok", Theme.Current.Success),
        "running" => Colored("running", Theme.Current.Warning),
        _ => Colored(result, Theme.Current.Error),
    };
}
