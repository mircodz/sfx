using Sfx.Core;

namespace Sfx.Web;

public sealed partial class UiSession
{
    private static readonly TimeSpan ReasonLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReasonTimeout = TimeSpan.FromSeconds(30);

    private readonly Lock _reasonsLock = new();
    private readonly Dictionary<string, (DateTime At, Task<string?> Reason)> _reasons = [];

    // Shared by every request, so several tabs polling at once still ask the cluster at most this many at a time.
    private readonly SemaphoreSlim _reasonGate = new(8);

    /// <summary>
    /// Why each entity is unhealthy, by id, for entities of one <paramref name="kind"/> (node, application, service or
    /// partition) with the health they're listed with. Each is asked at most once per 30 seconds, and again as soon as its
    /// health changes; ones whose reason can't be had are left out.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, string>> GetReasonsAsync(
        string kind, IEnumerable<(string Id, string Health)> entities, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var pending = new List<(string Id, Task<string?> Reason)>();
        lock (_reasonsLock)
        {
            foreach (var stale in _reasons.Where(kv => kv.Value.Reason.IsCompleted && now - kv.Value.At >= ReasonLifetime).Select(kv => kv.Key).ToList())
            {
                _reasons.Remove(stale);
            }

            foreach (var (id, health) in entities.DistinctBy(e => e.Id))
            {
                var key = ReasonKey(kind, id, health);
                // A failed lookup isn't kept, so the next poll asks again.
                if (!_reasons.TryGetValue(key, out var entry) || entry.Reason.IsFaulted || entry.Reason.IsCanceled)
                {
                    entry = (now, Task.Run(() => FetchReasonAsync(kind, id)));
                    _reasons[key] = entry;
                }
                pending.Add((id, entry.Reason));
            }
        }

        var reasons = new Dictionary<string, string>();
        foreach (var (id, reason) in pending)
        {
            try
            {
                if (await reason.WaitAsync(cancellationToken) is { } text)
                {
                    reasons[id] = text;
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
        return reasons;
    }

    /// <summary>Keyed by health too, so an entity going from warning to error isn't explained by its old reason.</summary>
    internal static string ReasonKey(string kind, string id, string health) => $"{kind}\n{id}\n{health}";

    private async Task<string?> FetchReasonAsync(string kind, string id)
    {
        await _reasonGate.WaitAsync();
        try
        {
            using var timeout = new CancellationTokenSource(ReasonTimeout);
            return await HealthReasons.FetchAsync(Queries.Client, kind, id, timeout.Token);
        }
        finally
        {
            _reasonGate.Release();
        }
    }
}
