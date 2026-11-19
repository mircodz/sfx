using Sfx.Core;
using Sfx.Core.Auth;

namespace Sfx.Tests;

/// <summary>Runs only when SFX_TEST_CLUSTER points at an unsecured cluster, e.g. a local dev cluster.</summary>
public sealed class ClusterFactAttribute : FactAttribute
{
    public static string? Endpoint => Environment.GetEnvironmentVariable("SFX_TEST_CLUSTER");

    public ClusterFactAttribute()
    {
        if (string.IsNullOrEmpty(Endpoint))
        {
            Skip = "Set SFX_TEST_CLUSTER (e.g. localhost) to run against a cluster.";
        }
    }
}

public class ClusterIntegrationTests
{
    private static async Task<ClusterQueries> ConnectAsync()
    {
        var connection = new ClusterConnection(ClusterEndpoint.Parse(ClusterFactAttribute.Endpoint!, secure: false), [NoAuth.Instance]);
        return new ClusterQueries(await ClusterClientFactory.CreateAsync(connection));
    }

    [ClusterFact]
    public async Task Lists_nodes()
    {
        var queries = await ConnectAsync();

        var nodes = await queries.GetNodesAsync(CancellationToken.None);

        Assert.NotEmpty(nodes);
        Assert.All(nodes, n => Assert.False(string.IsNullOrEmpty(n.Name)));
    }

    [ClusterFact]
    public async Task Drills_from_system_app_down_to_replicas()
    {
        var queries = await ConnectAsync();

        var apps = await queries.GetApplicationsAsync(CancellationToken.None);
        var system = Assert.Single(apps, a => a.Id == ClusterQueries.SystemApplicationId);

        var services = await queries.GetServicesAsync(system.Id, CancellationToken.None);
        var naming = Assert.Single(services, s => s.Name == "fabric:/System/NamingService");

        var partitions = await queries.GetPartitionsAsync(naming.Id, CancellationToken.None);
        Assert.NotEmpty(partitions);

        var replicas = await queries.GetReplicasAsync(partitions[0].Id, CancellationToken.None);
        Assert.Contains(replicas, r => r.Role == "Primary");
    }

    [ClusterFact]
    public async Task Inspects_cluster_nodes_and_system_services()
    {
        var queries = await ConnectAsync();
        var inspect = new ClusterInspector(queries.Client);
        var ct = CancellationToken.None;

        Assert.Contains("<ClusterManifest", await inspect.GetClusterManifestAsync(ct));
        Assert.NotEqual(Microsoft.ServiceFabric.Common.HealthState.Unknown, (await inspect.GetClusterHealthAsync(ct)).State);

        var node = (await queries.GetNodesAsync(ct))[0].Name;
        Assert.Contains("IpAddressOrFQDN", Inspect.Json(await inspect.GetNodeAsync(node, ct)));
        Assert.NotEmpty((await inspect.GetNodeHealthAsync(node, ct)).Events);

        var naming = (await queries.GetServicesAsync(ClusterQueries.SystemApplicationId, ct)).Single(s => s.Name == "fabric:/System/NamingService");
        Assert.Contains("NamingStoreService", Inspect.Json(await inspect.GetServiceDescriptionAsync(naming.Id, ct)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => inspect.GetApplicationManifestAsync("System", "", ct));
    }

    [ClusterFact]
    public async Task Reads_settings_upgrade_load_and_deployed_entities()
    {
        var queries = await ConnectAsync();
        var inspect = new ClusterInspector(queries.Client);
        var deployed = new DeployedQueries(queries.Client);
        var ct = CancellationToken.None;

        var settings = ClusterSettings.Parse(await inspect.GetClusterManifestAsync(ct));
        // Sections differ by platform (only Windows dev clusters have Setup), so just check some were read.
        Assert.NotEmpty(settings);
        Assert.All(settings, s => Assert.False(string.IsNullOrEmpty(s.Name)));

        var upgrade = UpgradeReport.From(await inspect.GetClusterUpgradeAsync(ct));
        Assert.NotEmpty(upgrade.State);

        Assert.NotNull(await inspect.GetClusterLoadAsync(ct));
        var node = (await queries.GetNodesAsync(ct))[0].Name;
        Assert.NotNull((await inspect.GetNodeLoadAsync(node, ct)).NodeLoadMetricInformation);
        Assert.NotNull(await deployed.GetApplicationsAsync(node, ct));
    }

    /// <summary>Changes the cluster (pauses a node, then activates it), so it also needs SFX_TEST_MUTATE=1.</summary>
    [ClusterFact]
    public async Task Deactivates_and_activates_a_node()
    {
        if (Environment.GetEnvironmentVariable("SFX_TEST_MUTATE") != "1")
        {
            return;
        }

        var queries = await ConnectAsync();
        var ops = new ClusterOperations(queries.Client);
        var ct = CancellationToken.None;
        var node = (await queries.GetNodesAsync(ct)).OrderBy(n => n.Name).Last(n => n.Status == "Up").Name;

        await ops.DeactivateNodeAsync(node, Microsoft.ServiceFabric.Common.DeactivationIntent.Pause, ct);
        try
        {
            var status = await WaitForStatusAsync(queries, node, s => s is "Disabled" or "Disabling");
            Assert.Contains(status, new[] { "Disabled", "Disabling" });
        }
        finally
        {
            await ops.ActivateNodeAsync(node, ct);
        }

        Assert.Equal("Up", await WaitForStatusAsync(queries, node, s => s == "Up"));
    }

    private static async Task<string> WaitForStatusAsync(ClusterQueries queries, string node, Func<string, bool> done)
    {
        var status = "";
        for (var attempt = 0; attempt < 30; attempt++)
        {
            status = (await queries.GetNodesAsync(CancellationToken.None)).Single(n => n.Name == node).Status;
            if (done(status))
            {
                break;
            }
            await Task.Delay(1000);
        }
        return status;
    }
}
