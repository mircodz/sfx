using Sfx.Core;
using Sfx.Tui;

namespace Sfx.Tests;

public sealed class ActivityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sfx-activity-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ActionLog Log() => new(Path.Combine(_dir, "actions.log"));

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Reads_back_what_it_appends_and_skips_lines_it_cannot_parse()
    {
        var log = Log();
        var written = log.Append("dev", "http://localhost:19080/", "Restart replica 1", "ok");
        File.AppendAllText(log.Path, "not a log line\n");
        log.Append("dev", "http://localhost:19080/", "Remove replica 2", "failed: nope");

        var entries = log.ReadLast();

        Assert.Equal(2, entries.Count);
        Assert.Equal(written, entries[0]);
        Assert.False(entries[1].Succeeded);
        Assert.Equal([entries[1]], log.ReadLast(1));
    }

    [Fact]
    public void Shows_each_operation_once_running_or_logged()
    {
        var log = Log();
        log.Append("prod", "https://prod/", "Earlier change", "ok");
        var clock = new ManualClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var activity = new SessionActivity(log, clock);

        var done = activity.Start("dev", "Restart replica 1");
        clock.Now += TimeSpan.FromSeconds(2);
        done.Fail("nope");
        activity.Logged(done, log.Append("dev", "http://dev/", "Restart replica 1", "failed: nope"));
        clock.Now += TimeSpan.FromSeconds(1);
        activity.Start("dev", "Move replica 2");

        var rows = activity.Rows();

        Assert.Equal(["Move replica 2", "Restart replica 1", "Earlier change"], rows.Select(r => r.Action));
        Assert.Equal(["running", "failed", "ok"], rows.Select(r => r.Result));
        Assert.Equal("nope", rows[1].Detail);
        Assert.Equal(TimeSpan.FromSeconds(2), rows[1].Took);
        Assert.Null(rows[2].Took);
    }

    [Fact]
    public void A_tracked_change_shows_where_it_has_got_to_until_it_is_done()
    {
        var log = Log();
        var activity = new SessionActivity(log);

        var task = activity.Start("dev", "Restart node _Node_1");
        activity.Logged(task, log.Append("dev", "http://dev/", "Restart node _Node_1", "ok"));
        task.Report(null, "requested");
        task.Report(0.5, "restarting");

        var running = Assert.Single(activity.Rows());
        Assert.Equal(("running", "restarting"), (running.Result, running.Detail));
        Assert.Equal(0.5, Assert.Single(activity.Tasks.Snapshot()).Progress);

        task.Succeed("up again");
        var done = Assert.Single(activity.Rows());
        Assert.Equal(("ok", "up again"), (done.Result, done.Detail));
    }

    [Fact]
    public void A_change_left_to_the_cluster_shows_as_canceled_with_the_reason()
    {
        var activity = new SessionActivity(Log());

        activity.Start("dev", "Scale fabric:/App/Svc").Cancel("stopped following it after 10m: 2 of 3 partitions");

        var row = Assert.Single(activity.Rows());
        Assert.Equal("canceled", row.Result);
        Assert.StartsWith("stopped following it", row.Detail);
    }
}
