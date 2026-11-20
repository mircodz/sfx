using Sfx.Core;

namespace Sfx.Web;

/// <summary>Operations started from the browser, followed until the cluster has actually made the change.</summary>
public sealed partial class UiSession
{
    private const int KeptOperations = 50;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FirstPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>What Azure says to anyone upgrading a cluster it manages.</summary>
    internal const string AzureManagedNote =
        "This cluster is managed by Azure: upgrade it from the Azure portal or with ARM, so it doesn't fight the resource provider.";

    private readonly Lock _operationsLock = new();
    private readonly List<OperationDto> _operations = [];
    private Cached<bool>? _azureManaged;

    /// <summary>The last operations, newest first.</summary>
    internal IReadOnlyList<OperationDto> GetOperations()
    {
        lock (_operationsLock)
        {
            return [.. _operations];
        }
    }

    /// <summary>
    /// Records an action that ran, and watches the cluster in the background until it settles, fails or times out.
    /// Actions with nothing to wait for are done right away.
    /// </summary>
    internal OperationDto Track(string actionId, string title, Args target, Args inputs)
    {
        ForgetUpgrades();
        var started = DateTime.UtcNow;
        OperationProbe? probe;
        try
        {
            probe = OperationWatcher.For(this, actionId, target, inputs, started);
        }
        catch (ActionException)
        {
            probe = null;
        }

        var operation = probe is null
            ? new OperationDto(Guid.NewGuid().ToString("N"), actionId, title, started, SettleChecks.Done, "Requested.", started)
            : new OperationDto(Guid.NewGuid().ToString("N"), actionId, title, started, SettleChecks.Running, null, null);
        lock (_operationsLock)
        {
            _operations.Insert(0, operation);
            if (_operations.Count > KeptOperations)
            {
                _operations.RemoveRange(KeptOperations, _operations.Count - KeptOperations);
            }
        }

        if (probe is not null)
        {
            _ = Task.Run(() => WatchAsync(operation.Id, title, probe));
        }
        return operation;
    }

    private async Task WatchAsync(string id, string title, OperationProbe probe)
    {
        // Not tied to any request: the operation outlives the one that started it.
        using var timeout = new CancellationTokenSource(OperationTimeout);
        string state, detail;
        try
        {
            await Task.Delay(FirstPoll, timeout.Token);
            while (true)
            {
                try
                {
                    var (s, d) = await probe(timeout.Token);
                    if (s != SettleChecks.Running)
                    {
                        (state, detail) = (s, d ?? "");
                        break;
                    }
                    Update(id, s, d, finished: null);
                }
                catch (Exception) when (!timeout.IsCancellationRequested)
                {
                    // A blip talking to the cluster; the next poll asks again.
                }
                await Task.Delay(PollInterval, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            (state, detail) = (SettleChecks.Failed, "Didn't finish within 30 minutes; check the cluster.");
        }

        Update(id, state, detail, DateTime.UtcNow);
        TryLog(title, state == SettleChecks.Done ? "done" : $"failed: {detail}");
    }

    private void Update(string id, string state, string? detail, DateTime? finished)
    {
        lock (_operationsLock)
        {
            // Gone when 50 newer ones pushed it out; nothing left to update then.
            var index = _operations.FindIndex(o => o.Id == id);
            if (index >= 0)
            {
                _operations[index] = _operations[index] with { State = state, Detail = detail, Finished = finished };
            }
        }
    }

    /// <summary>Whether Azure's Service Fabric resource provider manages this cluster; read from the cluster manifest once.</summary>
    internal Task<bool> IsAzureManagedAsync(CancellationToken cancellationToken) =>
        LazyInitializer.EnsureInitialized(ref _azureManaged, () => new Cached<bool>(
                TimeSpan.MaxValue, async ct => ClusterKind.IsAzureManaged(await Inspector.GetClusterManifestAsync(ct))))
            .GetAsync(cancellationToken);

    /// <summary>The cluster target's "azureManaged": "true", "false", or "unknown" when the manifest couldn't be read.</summary>
    internal async Task<string> AzureManagedTargetAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await IsAzureManagedAsync(cancellationToken) ? "true" : "false";
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return "unknown";
        }
    }
}
