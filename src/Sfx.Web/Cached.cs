namespace Sfx.Web;

/// <summary>
/// A value loaded at most once per <paramref name="lifetime"/>, for queries too heavy to run on every poll of every tab.
/// Callers that arrive during a load share it; a failed load isn't kept, so the next caller tries again.
/// </summary>
internal sealed class Cached<T>(TimeSpan lifetime, Func<CancellationToken, Task<T>> load)
{
    // The load is shared, so one caller going away mustn't cancel it for the others; this bounds it instead.
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(2);

    private readonly Lock _lock = new();
    private Task<(T Value, DateTime LoadedAt)>? _load;

    public async Task<T> GetAsync(CancellationToken cancellationToken)
    {
        Task<(T Value, DateTime LoadedAt)> current;
        lock (_lock)
        {
            if (_load is null
                || _load.IsFaulted
                || _load.IsCanceled
                || (_load.IsCompletedSuccessfully && DateTime.UtcNow - _load.Result.LoadedAt >= lifetime))
            {
                _load = Task.Run(LoadAsync);
            }
            current = _load;
        }
        return (await current.WaitAsync(cancellationToken)).Value;
    }

    /// <summary>Drops the value, e.g. after an action changed what it shows, so the next caller loads it again.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _load = null;
        }
    }

    private async Task<(T, DateTime)> LoadAsync()
    {
        using var timeout = new CancellationTokenSource(LoadTimeout);
        return (await load(timeout.Token), DateTime.UtcNow);
    }
}

/// <summary>
/// A value served stale while it's refreshed, for loads that take long on a large cluster (walking every replica).
/// Within <paramref name="lifetime"/> the value is served as is; after it, the first caller starts a refresh in the
/// background and every caller gets the previous value right away. One refresh runs at a time, and a failed one keeps
/// the previous value (the next caller tries again). Only the very first load is waited for.
/// </summary>
/// <param name="timeout">Bounds a refresh nobody waits for, so a hung one can't stop the next; defaults to 10 minutes.</param>
internal sealed class StaleCache<T>(TimeSpan lifetime, Func<CancellationToken, Task<T>> load, TimeSpan? timeout = null, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private (T Value, DateTimeOffset LoadedAt)? _value;
    private Task<T>? _refresh;

    /// <summary>Whether a refresh is running.</summary>
    public bool Refreshing
    {
        get
        {
            lock (_lock)
            {
                return _refresh is not null;
            }
        }
    }

    public async Task<T> GetAsync(CancellationToken cancellationToken)
    {
        Task<T> first;
        lock (_lock)
        {
            if (_value is { } fresh && _time.GetUtcNow() - fresh.LoadedAt < lifetime)
            {
                return fresh.Value;
            }
            _refresh ??= Task.Run(RefreshAsync);
            if (_value is { } stale)
            {
                return stale.Value;
            }
            first = _refresh;
        }
        return await first.WaitAsync(cancellationToken);
    }

    private async Task<T> RefreshAsync()
    {
        try
        {
            using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(10), _time);
            var value = await load(cancel.Token);
            lock (_lock)
            {
                _value = (value, _time.GetUtcNow());
            }
            return value;
        }
        finally
        {
            lock (_lock)
            {
                _refresh = null;
            }
        }
    }
}
