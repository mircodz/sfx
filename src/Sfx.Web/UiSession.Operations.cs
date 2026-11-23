using Sfx.Core;

namespace Sfx.Web;

/// <summary>Operations started from the browser, followed until the cluster has actually made the change.</summary>
public sealed partial class UiSession
{
    private const int KeptOperations = 50;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FirstPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly Lock _operationsLock = new();
    private readonly List<OperationDto> _operations = [];

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
        OperationProbe? probe;
        try
        {
            probe = OperationWatcher.For(this, actionId, target, inputs, DateTime.UtcNow);
        }
        catch (ActionException)
        {
            probe = null;
        }
        return Track(actionId, title, probe);
    }

    /// <summary>Records an action that ran, followed with <paramref name="probe"/>; done right away when that's null.</summary>
    internal OperationDto Track(string actionId, string title, OperationProbe? probe)
    {
        ForgetUpgrades();
        ForgetStore();
        var started = DateTime.UtcNow;
        var operation = probe is null
            ? new OperationDto(Guid.NewGuid().ToString("N"), actionId, title, started, SettleChecks.Done, "Requested.", started)
            : new OperationDto(Guid.NewGuid().ToString("N"), actionId, title, started, SettleChecks.Running, null, null);
        Add(operation);

        if (probe is not null)
        {
            _ = Task.Run(() => WatchAsync(operation.Id, title, probe));
        }
        return operation;
    }

    /// <summary>
    /// Records an operation the caller drives itself (an upload), with <see cref="Progress"/> and then <see cref="WatchAsync"/>
    /// or <see cref="Finish"/>; returns its id.
    /// </summary>
    internal string Begin(string actionId, string title, string detail)
    {
        var operation = new OperationDto(Guid.NewGuid().ToString("N"), actionId, title, DateTime.UtcNow, SettleChecks.Running, detail, null);
        Add(operation);
        return operation.Id;
    }

    /// <summary>How far a running operation is; <paramref name="progress"/> null once it can't be counted.</summary>
    internal void Progress(string id, string detail, double? progress) => Update(id, SettleChecks.Running, detail, null, progress);

    /// <summary>Ends an operation the caller drives, and logs how it ended.</summary>
    internal void Finish(string id, string title, string state, string detail)
    {
        ForgetStore();
        Update(id, state, detail, DateTime.UtcNow);
        TryLog(title, state == SettleChecks.Done ? "done" : $"failed: {detail}");
    }

    private void Add(OperationDto operation)
    {
        lock (_operationsLock)
        {
            _operations.Insert(0, operation);
            if (_operations.Count > KeptOperations)
            {
                _operations.RemoveRange(KeptOperations, _operations.Count - KeptOperations);
            }
        }
    }

    /// <summary>Polls <paramref name="probe"/> until the operation settles, fails or times out, then logs how it ended.</summary>
    internal async Task WatchAsync(string id, string title, OperationProbe probe)
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

        Finish(id, title, state, detail);
    }

    private void Update(string id, string state, string? detail, DateTime? finished, double? progress = null)
    {
        lock (_operationsLock)
        {
            // Gone when 50 newer ones pushed it out; nothing left to update then.
            var index = _operations.FindIndex(o => o.Id == id);
            if (index >= 0)
            {
                _operations[index] = _operations[index] with { State = state, Detail = detail, Finished = finished, Progress = progress };
            }
        }
    }

    /// <summary>Whether Azure's Service Fabric resource provider manages this cluster; read from the cluster manifest once.</summary>
    internal Task<bool> IsAzureManagedAsync(CancellationToken cancellationToken) => ClusterUpgrades.IsAzureManagedAsync(cancellationToken);

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
