using Sfx.Core;
using Sfx.Tui;

namespace Sfx.Tests;

public sealed class ActivityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sfx-activity-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ActionLog Log() => new(Path.Combine(_dir, "actions.log"));

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
        var activity = new ActivityTracker(log);

        var done = activity.Start("dev", "Restart replica 1");
        activity.Finish(done, false, "nope", log.Append("dev", "http://dev/", "Restart replica 1", "failed: nope"));
        activity.Start("dev", "Move replica 2");

        var rows = activity.Rows();

        Assert.Equal(["Move replica 2", "Restart replica 1", "Earlier change"], rows.Select(r => r.Action));
        Assert.Equal(["running", "failed", "ok"], rows.Select(r => r.Result));
        Assert.Equal("nope", rows[1].Detail);
        Assert.NotNull(rows[1].Took);
        Assert.Null(rows[2].Took);
    }
}
