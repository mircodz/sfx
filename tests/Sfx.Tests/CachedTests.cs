using Sfx.Web;

namespace Sfx.Tests;

public class CachedTests
{
    [Fact]
    public async Task Callers_during_a_load_share_it()
    {
        var loads = 0;
        var release = new TaskCompletionSource();
        var cached = new Cached<int>(TimeSpan.FromMinutes(1), async _ =>
        {
            Interlocked.Increment(ref loads);
            await release.Task;
            return 42;
        });

        var first = cached.GetAsync(default);
        var second = cached.GetAsync(default);
        release.SetResult();

        Assert.Equal(new[] { 42, 42 }, await Task.WhenAll(first, second));
        Assert.Equal(42, await cached.GetAsync(default));
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task Loads_again_once_expired()
    {
        var loads = 0;
        var cached = new Cached<int>(TimeSpan.Zero, _ => Task.FromResult(Interlocked.Increment(ref loads)));

        Assert.Equal(1, await cached.GetAsync(default));
        Assert.Equal(2, await cached.GetAsync(default));
    }

    [Fact]
    public async Task Does_not_keep_a_failure()
    {
        var loads = 0;
        var cached = new Cached<int>(TimeSpan.FromMinutes(1), _ =>
            Interlocked.Increment(ref loads) == 1 ? throw new TimeoutException() : Task.FromResult(7));

        await Assert.ThrowsAsync<TimeoutException>(() => cached.GetAsync(default));
        Assert.Equal(7, await cached.GetAsync(default));
    }

    [Fact]
    public async Task A_caller_giving_up_does_not_cancel_the_load_for_others()
    {
        var release = new TaskCompletionSource();
        var cached = new Cached<string>(TimeSpan.FromMinutes(1), async ct =>
        {
            await release.Task.WaitAsync(ct);
            return "done";
        });
        using var gone = new CancellationTokenSource();

        var abandoned = cached.GetAsync(gone.Token);
        var waiting = cached.GetAsync(default);
        gone.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.Equal("done", await waiting);
    }
}
