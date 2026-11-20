using Sfx.Web;

namespace Sfx.Tests;

public class StaleCacheTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(done());
    }

    [Fact]
    public async Task Serves_the_old_value_while_a_refresh_runs()
    {
        var clock = new Clock();
        var loads = 0;
        var release = new TaskCompletionSource();
        var cache = new StaleCache<int>(TimeSpan.FromMinutes(2), async _ =>
        {
            if (Interlocked.Increment(ref loads) > 1)
            {
                await release.Task;
            }
            return loads;
        }, time: clock);

        Assert.Equal(1, await cache.GetAsync(default));
        clock.Now += TimeSpan.FromMinutes(3);

        // Expired: the old value comes back at once, and only one refresh starts however many ask.
        Assert.Equal(1, await cache.GetAsync(default));
        Assert.Equal(1, await cache.GetAsync(default));
        Assert.True(cache.Refreshing);
        await Until(() => Volatile.Read(ref loads) == 2);
        Assert.Equal(1, await cache.GetAsync(default));

        release.SetResult();
        await Until(() => !cache.Refreshing);
        Assert.Equal(2, await cache.GetAsync(default));
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_old_value_and_the_next_caller_tries_again()
    {
        var clock = new Clock();
        var loads = 0;
        var cache = new StaleCache<string>(TimeSpan.FromMinutes(2), _ => Interlocked.Increment(ref loads) switch
        {
            1 => Task.FromResult("first"),
            2 => throw new TimeoutException(),
            _ => Task.FromResult("third"),
        }, time: clock);

        Assert.Equal("first", await cache.GetAsync(default));
        clock.Now += TimeSpan.FromMinutes(3);

        Assert.Equal("first", await cache.GetAsync(default));
        await Until(() => !cache.Refreshing);
        Assert.Equal(2, loads);

        Assert.Equal("first", await cache.GetAsync(default));
        await Until(() => !cache.Refreshing);
        Assert.Equal("third", await cache.GetAsync(default));
        Assert.Equal(3, loads);
    }

    [Fact]
    public async Task Serves_a_fresh_value_without_loading()
    {
        var clock = new Clock();
        var loads = 0;
        var cache = new StaleCache<int>(TimeSpan.FromMinutes(2), _ => Task.FromResult(Interlocked.Increment(ref loads)), time: clock);

        Assert.Equal(1, await cache.GetAsync(default));
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal(1, await cache.GetAsync(default));
        Assert.False(cache.Refreshing);
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task The_first_load_is_waited_for_and_its_failure_is_not_kept()
    {
        var loads = 0;
        var cache = new StaleCache<int>(TimeSpan.FromMinutes(2), _ =>
            Interlocked.Increment(ref loads) == 1 ? throw new InvalidOperationException("No cluster.") : Task.FromResult(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(default));
        await Until(() => !cache.Refreshing);
        Assert.Equal(5, await cache.GetAsync(default));
    }

    [Fact]
    public async Task A_caller_giving_up_does_not_cancel_the_first_load()
    {
        var release = new TaskCompletionSource();
        var cache = new StaleCache<string>(TimeSpan.FromMinutes(2), async ct =>
        {
            await release.Task.WaitAsync(ct);
            return "done";
        });
        using var gone = new CancellationTokenSource();

        var abandoned = cache.GetAsync(gone.Token);
        var waiting = cache.GetAsync(default);
        gone.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.Equal("done", await waiting);
    }
}
