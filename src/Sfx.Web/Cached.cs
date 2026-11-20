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

    private async Task<(T, DateTime)> LoadAsync()
    {
        using var timeout = new CancellationTokenSource(LoadTimeout);
        return (await load(timeout.Token), DateTime.UtcNow);
    }
}
