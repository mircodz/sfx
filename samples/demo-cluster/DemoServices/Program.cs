using System.Fabric;
using System.Fabric.Description;
using Microsoft.ServiceFabric.Data.Collections;
using Microsoft.ServiceFabric.Services.Runtime;

// One process hosts both service types, so every demo application shares one small code package.
await ServiceRuntime.RegisterServiceAsync("CounterType", context => new Counter(context));
await ServiceRuntime.RegisterServiceAsync("FrontendType", context => new Frontend(context));
await Task.Delay(Timeout.Infinite);

/// <summary>A stateful service that counts in a reliable dictionary and reports the count as load.</summary>
internal sealed class Counter(StatefulServiceContext context) : StatefulService(context)
{
    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        var counts = await StateManager.GetOrAddAsync<IReliableDictionary<string, long>>("counts");
        while (!cancellationToken.IsCancellationRequested)
        {
            using (var tx = StateManager.CreateTransaction())
            {
                var ticks = await counts.AddOrUpdateAsync(tx, "ticks", 1, (_, value) => value + 1);
                await tx.CommitAsync();
                Partition.ReportLoad([new LoadMetric("Ticks", (int)(ticks % 100))]);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }
}

/// <summary>A stateless service that does nothing but report a varying load.</summary>
internal sealed class Frontend(StatelessServiceContext context) : StatelessService(context)
{
    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        var random = new Random();
        while (!cancellationToken.IsCancellationRequested)
        {
            Partition.ReportLoad([new LoadMetric("Requests", random.Next(0, 50))]);
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }
}
