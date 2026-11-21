using System.Security.Cryptography;
using System.Text;
using Sfx.Core;

namespace Sfx.Web;

/// <summary>One connected cluster, shared by every browser tab; <see cref="UiHost"/> replaces it on a context switch.</summary>
/// <param name="resolved">The context after folder rules, with its folder's label and color; defaults to none.</param>
/// <param name="views">The views in the config; defaults to none.</param>
/// <param name="defaultView">The view the browser starts with: <c>--view</c>, else the context's; null for none.</param>
public sealed partial class UiSession(
    ClusterSession session, ActionLog log, string version, ResolvedContext? resolved = null, ViewCatalog? views = null, string? defaultView = null)
{
    public ViewCatalog Views { get; } = views ?? ViewCatalog.Empty;
    public string? DefaultView => defaultView;

    /// <summary>The view a request names in <c>?view=</c>: none when it's absent or empty, a 400 when it isn't defined.</summary>
    public ViewFilter View(string? name) =>
        string.IsNullOrWhiteSpace(name) ? ViewFilter.None : Views.Find(name) ?? throw new ActionException(Views.Unknown(name.Trim()));

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

    private Task<SystemServices>? _systemServices;

    /// <summary>
    /// Which optional system services the cluster runs; asked once per connection (they rarely change), with one call.
    /// A failed check isn't kept, so the next request asks again.
    /// </summary>
    public Task<SystemServices> GetSystemServicesAsync(CancellationToken cancellationToken)
    {
        var check = _systemServices ??= LoadSystemServicesAsync();
        if (check.IsFaulted)
        {
            _systemServices = null;
        }
        return check.WaitAsync(cancellationToken);
    }

    /// <summary>The system services if they've been checked already; null until then (so nothing is hidden early).</summary>
    public SystemServices? KnownSystemServices => _systemServices is { IsCompletedSuccessfully: true } done ? done.Result : null;

    /// <summary>For tests: what the cluster runs, without asking it.</summary>
    internal void KnowSystemServices(SystemServices services) => _systemServices = Task.FromResult(services);

    public async Task<bool> HasEventStoreAsync(CancellationToken cancellationToken) =>
        (await GetSystemServicesAsync(cancellationToken)).EventStore;

    private async Task<SystemServices> LoadSystemServicesAsync()
    {
        var names = (await Queries.GetServicesAsync(ClusterQueries.SystemApplicationId, CancellationToken.None))
            .Select(svc => svc.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Has(string service) => names.Contains($"{ClusterQueries.SystemApplicationName}/{service}");
        return new SystemServices(Has("EventStoreService"), Has("RepairManagerService"), Has("FaultAnalysisService"));
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

    private static readonly TimeSpan ServiceReplicasLifetime = TimeSpan.FromSeconds(15);

    private readonly StaleCache<IReadOnlyList<ClusterReplicaDto>> _replicas =
        new(TimeSpan.FromMinutes(2), ct => ClusterReplicas.LoadAsync(session.Queries, ct));

    /// <summary>
    /// Every replica in the cluster. One walk serves every tab for 2 minutes; after that the last walk is still served
    /// while one new walk runs in the background, so a slow walk never holds up a poll.
    /// </summary>
    public Task<IReadOnlyList<ClusterReplicaDto>> GetClusterReplicasAsync(CancellationToken cancellationToken) =>
        _replicas.GetAsync(cancellationToken);

    private readonly Lock _serviceReplicasLock = new();
    private readonly Dictionary<string, (DateTime At, Task<IReadOnlyList<ClusterReplicaDto>> Load)> _serviceReplicas = [];

    /// <summary>The replicas of one service; asked at most once per 15 seconds, shared by every tab.</summary>
    public Task<IReadOnlyList<ClusterReplicaDto>> GetServiceReplicasAsync(string applicationId, string serviceId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        Task<IReadOnlyList<ClusterReplicaDto>> load;
        lock (_serviceReplicasLock)
        {
            foreach (var stale in _serviceReplicas.Where(kv => kv.Value.Load.IsCompleted && now - kv.Value.At >= ServiceReplicasLifetime).Select(kv => kv.Key).ToList())
            {
                _serviceReplicas.Remove(stale);
            }

            var key = $"{applicationId}\n{serviceId}";
            // A failed load isn't kept, so the next poll asks again.
            if (!_serviceReplicas.TryGetValue(key, out var entry) || entry.Load.IsFaulted || entry.Load.IsCanceled)
            {
                entry = (now, Task.Run(async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    return await ClusterReplicas.LoadServiceAsync(session.Queries, applicationId, serviceId, timeout.Token);
                }));
                _serviceReplicas[key] = entry;
            }
            load = entry.Load;
        }
        return load.WaitAsync(cancellationToken);
    }

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
