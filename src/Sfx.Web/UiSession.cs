using System.Security.Cryptography;
using System.Text;
using Sfx.Core;

namespace Sfx.Web;

/// <summary>One connected cluster, shared by every browser tab; <see cref="UiHost"/> replaces it on a context switch.</summary>
/// <param name="resolved">The context after folder rules, with its folder's label and color; defaults to none.</param>
public sealed partial class UiSession(ClusterSession session, ActionLog log, string version, ResolvedContext? resolved = null)
{
    public string? Label => resolved?.Label;
    public string? Color => resolved?.Color;
    public ClusterSession Cluster => session;
    public ClusterContext Context => session.Context;
    public ClusterQueries Queries { get; } = session.Queries;
    public ClusterInspector Inspector { get; } = new(session.Queries.Client);
    public ClusterOperations Operations { get; } = new(session.Queries.Client);
    public DeployedQueries Deployed { get; } = new(session.Queries.Client);
    public UpgradeQueries Upgrades { get; } = new(session.Queries);
    public EventQueries Events { get; } = new(session.Queries);
    public string Version => version;

    public bool ReadOnly => session.Context.ReadOnly;

    /// <summary>Whether the riskiest actions are offered; starts as the context says, then toggled from the browser.</summary>
    public bool Power { get; set; } = !session.Context.ReadOnly && session.Context.Power;

    private Task<bool>? _eventStore;

    /// <summary>Whether the cluster runs the event store; asked once per connection, since it rarely changes.</summary>
    public Task<bool> HasEventStoreAsync(CancellationToken cancellationToken)
    {
        // A failed check isn't kept, so the next request asks again.
        var check = _eventStore ??= Queries.HasSystemServiceAsync("EventStoreService", CancellationToken.None);
        if (check.IsFaulted)
        {
            _eventStore = null;
        }
        return check.WaitAsync(cancellationToken);
    }

    private (DateTime At, bool Enabled)? _defaultServicesUpgrade;

    /// <summary>ClusterManager/EnableDefaultServicesUpgrade, read from the cluster manifest at most once a minute.</summary>
    public async Task<bool> DefaultServicesUpgradeEnabledAsync(CancellationToken cancellationToken)
    {
        if (_defaultServicesUpgrade is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(1))
        {
            return cached.Enabled;
        }
        var enabled = DefaultServices.UpgradeEnabled(await Inspector.GetClusterManifestAsync(cancellationToken));
        _defaultServicesUpgrade = (DateTime.UtcNow, enabled);
        return enabled;
    }

    private readonly Cached<IReadOnlyList<ClusterReplicaDto>> _replicas =
        new(TimeSpan.FromSeconds(15), ct => ClusterReplicas.LoadAsync(session.Queries, ct));

    /// <summary>Every replica in the cluster; one walk serves every tab for 15 seconds.</summary>
    public Task<IReadOnlyList<ClusterReplicaDto>> GetClusterReplicasAsync(CancellationToken cancellationToken) =>
        _replicas.GetAsync(cancellationToken);

    private readonly Lock _logLock = new();
    private readonly byte[] _signingKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>Appends to the action log; returns why it couldn't, rather than throwing.</summary>
    public string? TryLog(string action, string outcome)
    {
        try
        {
            lock (_logLock)
            {
                log.Append(session.Context.Name, session.Endpoint.ToString(), action, outcome);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    public IReadOnlyList<ActionLogEntry> ReadLog(int count)
    {
        lock (_logLock)
        {
            return log.ReadLast(count);
        }
    }

    /// <summary>A MAC over an action and its target, keyed per run, so the browser can hand both back but not change them.</summary>
    public string Sign(string actionId, IReadOnlyDictionary<string, string> target)
    {
        // The context is part of what's signed, and each connection has its own key: a page left open on one cluster
        // can't run its actions on another after a switch.
        var canonical = session.Context.Name + "\n" + actionId + "\n" +
            string.Join("\n", target.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        return Convert.ToHexStringLower(HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(canonical)));
    }

    public bool Verify(string actionId, IReadOnlyDictionary<string, string> target, string? signature) =>
        signature is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(actionId, target)), Encoding.UTF8.GetBytes(signature));
}
