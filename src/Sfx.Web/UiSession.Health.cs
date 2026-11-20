using Microsoft.ServiceFabric.Common;

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
    /// Why each entity is unhealthy, by id, for ids of one <paramref name="kind"/> (node, application, service or partition).
    /// Each is asked at most once per 30 seconds; ones whose reason can't be had are left out.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, string>> GetReasonsAsync(string kind, IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var pending = new List<(string Id, Task<string?> Reason)>();
        lock (_reasonsLock)
        {
            foreach (var stale in _reasons.Where(kv => kv.Value.Reason.IsCompleted && now - kv.Value.At >= ReasonLifetime).Select(kv => kv.Key).ToList())
            {
                _reasons.Remove(stale);
            }

            foreach (var id in ids.Distinct())
            {
                var key = $"{kind}\n{id}";
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

    private async Task<string?> FetchReasonAsync(string kind, string id)
    {
        await _reasonGate.WaitAsync();
        try
        {
            using var timeout = new CancellationTokenSource(ReasonTimeout);
            var client = Queries.Client;
            EntityHealth health = kind switch
            {
                "node" => await client.Nodes.GetNodeHealthAsync(new NodeName(id), cancellationToken: timeout.Token),
                "application" => await client.Applications.GetApplicationHealthAsync(id, cancellationToken: timeout.Token),
                "service" => await client.Services.GetServiceHealthAsync(id, cancellationToken: timeout.Token),
                "partition" => await client.Partitions.GetPartitionHealthAsync(new PartitionId(Guid.Parse(id)), cancellationToken: timeout.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };
            return Reasons.From(health);
        }
        finally
        {
            _reasonGate.Release();
        }
    }
}
