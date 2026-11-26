using Microsoft.ServiceFabric.Common;
using Microsoft.ServiceFabric.Common.Exceptions;
using Sfx.Core;

namespace Sfx.Tests;

public class ClusterOverviewTests
{
    private static readonly NodeRow Node1 = new("_Node_1", "10.0.0.4", "FrontEnd", "Up", HealthState.Ok, "UD1", "fd:/1", true, "10.1", null, "abc123");
    private static readonly NodeRow Node2 = new("_Node_2", "10.0.0.5", "BackEnd", "Up", HealthState.Ok, "UD2", "fd:/2", false, "10.1", null);
    private static readonly ApplicationRow Shop = new("shop", "fabric:/shop", "ShopType", "1.0.0", "Ready", HealthState.Ok);
    private static readonly ApplicationRow Shopping = new("shopping", "fabric:/shopping", "CartType", "1.0.0", "Ready", HealthState.Ok);
    private static readonly ServiceRow Counter = new("shop~Counter", "fabric:/shop/Counter", "CounterType", "Stateful", "Active", HealthState.Ok);

    private static readonly IReadOnlyList<SearchHit> Index =
    [
        new("node", Node1.Name, Node: Node1),
        new("node", Node2.Name, Node: Node2),
        new("application", Shopping.Name, Application: Shopping),
        new("application", Shop.Name, Application: Shop),
        new("service", Counter.Name, Application: Shop, Service: Counter),
    ];

    private static IReadOnlyList<string> Titles(string query) => ClusterOverview.Search(Index, query).Select(h => h.Title).ToList();

    [Fact]
    public void Ranks_exact_names_then_prefixes_then_the_rest()
    {
        // Names starting with the query rank alike, applications before services.
        Assert.Equal(["fabric:/shop", "fabric:/shopping", "fabric:/shop/Counter"], Titles("shop"));
        Assert.Equal(["fabric:/shop/Counter"], Titles("fabric:/shop/c"));
        Assert.Equal(["fabric:/shop/Counter"], Titles("counter"));
    }

    [Fact]
    public void Matches_types_addresses_and_ids_with_every_word()
    {
        Assert.Equal(["_Node_2"], Titles("backend"));
        Assert.Equal(["_Node_1"], Titles("10.0.0.4"));
        Assert.Equal(["_Node_1"], Titles("abc123"));
        Assert.Equal(["fabric:/shopping"], Titles("carttype"));
        Assert.Equal(["fabric:/shop/Counter"], Titles("shop countertype"));
        Assert.Empty(Titles("shop backend"));
        Assert.Empty(Titles("   "));
    }

    [Fact]
    public void Search_keeps_to_the_limit()
    {
        Assert.Equal(2, ClusterOverview.Search(Index, "_node", limit: 2).Count);
        Assert.Single(ClusterOverview.Search(Index, "fabric", limit: 1));
    }

    [Fact]
    public async Task Finds_a_partition_with_its_service_and_application()
    {
        var id = Guid.NewGuid();
        var cluster = new FakeCluster()
            .On("GetServiceNameInfoAsync", new ServiceNameInfo("shop~Counter", new ServiceName("fabric:/shop/Counter")))
            .On("GetApplicationNameInfoAsync", new ApplicationNameInfo("shop", new ApplicationName("fabric:/shop")))
            .On(
                "GetApplicationInfoAsync",
                new ApplicationInfo("shop", new ApplicationName("fabric:/shop"), "ShopType", "1.0.0", ApplicationStatus.Ready, healthState: HealthState.Ok))
            .On("GetServiceInfoAsync", new StatefulServiceInfo("shop~Counter", new ServiceName("fabric:/shop/Counter"), "CounterType", healthState: HealthState.Ok, serviceStatus: ServiceStatus.Active))
            .On("GetPartitionInfoAsync", new StatefulServicePartitionInfo(
                HealthState.Ok,
                ServicePartitionStatus.Ready,
                new SingletonPartitionInformation(new PartitionId(id)),
                targetReplicaSetSize: 3,
                minReplicaSetSize: 2));

        var hit = await new ClusterOverview(new ClusterQueries(cluster.Client)).FindPartitionAsync(id, default);

        Assert.NotNull(hit);
        Assert.Equal("partition", hit.Kind);
        Assert.Equal(Shop, hit.Application);
        Assert.Equal(Counter, hit.Service);
        Assert.Equal(new PartitionId(id), hit.Partition!.Id);
        Assert.Equal("3/2", hit.Partition.ReplicaSet);
        Assert.Equal(("shop", "shop~Counter"), (cluster.CallsTo("GetServiceInfoAsync")[0][0], cluster.CallsTo("GetServiceInfoAsync")[0][1]));
    }

    [Fact]
    public async Task Finds_a_system_partition()
    {
        var id = Guid.NewGuid();
        var cluster = new FakeCluster()
            .On("GetServiceNameInfoAsync", new ServiceNameInfo("System~NamingService", new ServiceName("fabric:/System/NamingService")))
            .On("GetApplicationNameInfoAsync", new ApplicationNameInfo("System", new ApplicationName("fabric:/System")))
            .On("GetApplicationHealthAsync", new ApplicationHealth(HealthState.Warning))
            .On("GetServiceInfoAsync", new StatefulServiceInfo("System~NamingService", new ServiceName("fabric:/System/NamingService"), "NamingStoreService"))
            .On(
                "GetPartitionInfoAsync",
                new StatefulServicePartitionInfo(HealthState.Ok, partitionInformation: new SingletonPartitionInformation(new PartitionId(id))));

        var hit = await new ClusterOverview(new ClusterQueries(cluster.Client)).FindPartitionAsync(id, default);

        Assert.Equal(("System", HealthState.Warning), (hit!.Application!.Id, hit.Application.Health));
        Assert.Equal("fabric:/System/NamingService", hit.Service!.Name);
    }

    [Fact]
    public async Task A_partition_that_does_not_exist_is_not_found()
    {
        var cluster = new FakeCluster().Throws(
            "GetServiceNameInfoAsync",
            new ServiceFabricException("gone", FabricErrorCodes.FABRIC_E_PARTITION_NOT_FOUND, false));

        Assert.Null(await new ClusterOverview(new ClusterQueries(cluster.Client)).FindPartitionAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Lists_what_is_unhealthy_with_reasons_errors_first()
    {
        static HealthEvent Event(HealthState state, string text) => new(sourceId: "Watchdog", property: "P", healthState: state, description: text);
        var cluster = new FakeCluster()
            .On("GetClusterHealthAsync", new ClusterHealth(
                HealthState.Error,
                nodeHealthStates: [new NodeHealthState(
                    HealthState.Warning,
                    new NodeName("_Node_1")), new NodeHealthState(HealthState.Ok, new NodeName("_Node_2"))],
                applicationHealthStates: [new ApplicationHealthState(HealthState.Error, new ApplicationName("fabric:/shop/eu"))]))
            .On("GetNodeHealthAsync", new NodeHealth(HealthState.Warning, [Event(HealthState.Warning, "Disk is 95% full.")], name: new NodeName("_Node_1")))
            .On("GetApplicationHealthAsync", args => new ApplicationHealth(
                HealthState.Error,
                [Event(HealthState.Error, "Shop is down.")],
                name: new ApplicationName("fabric:/shop/eu"),
                serviceHealthStates: [new ServiceHealthState(HealthState.Error, new ServiceName("fabric:/shop/eu/Counter"))]))
            .On("GetServiceHealthAsync", new ServiceHealth(HealthState.Error, [Event(HealthState.Error, "No primary.")]));

        var unhealthy = await new ClusterOverview(new ClusterQueries(cluster.Client)).GetUnhealthyAsync(default);

        Assert.Equal(
            [
                new UnhealthyEntity("application", "fabric:/shop/eu", "shop~eu", HealthState.Error, "Shop is down."),
                new UnhealthyEntity("service", "fabric:/shop/eu/Counter", "shop~eu~Counter", HealthState.Error, "No primary.", "shop~eu"),
                new UnhealthyEntity("node", "_Node_1", "_Node_1", HealthState.Warning, "Disk is 95% full."),
            ],
            unhealthy);
        Assert.Equal("shop~eu", cluster.CallsTo("GetApplicationHealthAsync").Single()[0]);
    }

    [Fact]
    public async Task Builds_the_search_index_from_nodes_applications_and_services()
    {
        var cluster = new FakeCluster()
            .Paged("GetNodeInfoListAsync", new NodeInfo(name: new NodeName("_Node_1"), type: "FrontEnd", ipAddressOrFQDN: "10.0.0.4"))
            .On("GetApplicationHealthAsync", new ApplicationHealth(HealthState.Ok))
            .Paged("GetApplicationInfoListAsync", new ApplicationInfo("shop", new ApplicationName("fabric:/shop"), "ShopType", "1.0.0"))
            .On("GetServiceInfoListAsync", args => (string)args[0]! == "System"
                ? new PagedData<ServiceInfo>(new ContinuationToken(""), [])
                : new PagedData<ServiceInfo>(
                    new ContinuationToken(""),
                    [new StatelessServiceInfo("shop~Web", new ServiceName("fabric:/shop/Web"), "WebType")]));

        var index = await new ClusterOverview(new ClusterQueries(cluster.Client)).GetSearchIndexAsync(default);

        Assert.Equal(
            [("node", "_Node_1"), ("application", "fabric:/System"), ("application", "fabric:/shop"), ("service", "fabric:/shop/Web")],
            index.Select(h => (h.Kind, h.Title)));
        Assert.Equal("shop", index[^1].Application!.Id);
    }

    [Fact]
    public async Task Summarizes_version_health_and_counts()
    {
        var cluster = new FakeCluster()
            .On("GetClusterVersionAsync", new ClusterVersion("10.1.2"))
            .On("GetClusterHealthAsync", new ClusterHealth(
                HealthState.Warning,
                healthStatistics: new HealthStatistics([new EntityKindHealthStateCount(EntityKind.Node, new HealthStateCount(4, 1, 0))])));

        var summary = await new ClusterOverview(new ClusterQueries(cluster.Client)).GetSummaryAsync(default);

        Assert.Equal("10.1.2", summary.CodeVersion);
        Assert.Equal(HealthState.Warning, summary.Health.State);
        Assert.Equal([new HealthCount("Node", 4, 1, 0)], summary.Counts);
    }
}

public class CoreClusterUpgradeTests
{
    private const string AzureManifest = """
        <ClusterManifest xmlns="http://schemas.microsoft.com/2011/01/fabric" Name="c" Version="1.0">
          <Infrastructure><PaaS><Roles /><Votes /></PaaS></Infrastructure>
        </ClusterManifest>
        """;

    private const string OwnManifest = """
        <ClusterManifest xmlns="http://schemas.microsoft.com/2011/01/fabric" Name="c" Version="1.0">
          <Infrastructure><WindowsServer IsScaleMin="false" /></Infrastructure>
        </ClusterManifest>
        """;

    private static FakeCluster Cluster(string manifest) => new FakeCluster()
        .On("GetClusterManifestAsync", new ClusterManifest(manifest))
        .On(
            "GetProvisionedFabricCodeVersionInfoListAsync",
            (IEnumerable<FabricCodeVersionInfo>)[new FabricCodeVersionInfo("10.0"), new FabricCodeVersionInfo("10.1")])
        .On("GetProvisionedFabricConfigVersionInfoListAsync", (IEnumerable<FabricConfigVersionInfo>)[new FabricConfigVersionInfo("1.0")])
        .OnCall("StartClusterUpgradeAsync")
        .OnCall("ProvisionClusterAsync")
        .OnCall("UnprovisionClusterAsync");

    [Fact]
    public async Task Refuses_clusters_azure_manages()
    {
        var cluster = Cluster(AzureManifest);
        var upgrades = new ClusterUpgrades(cluster.Client);

        var ex = await Assert.ThrowsAsync<RefusedException>(() => upgrades.StartAsync("10.1", null, UpgradePlan.MonitoredRollback, null, default));
        Assert.Equal(ClusterUpgrades.AzureManagedNote, ex.Message);
        Assert.True(ex.Conflict);
        await Assert.ThrowsAsync<RefusedException>(() => upgrades.ProvisionAsync("code.cab", null, default));
        await Assert.ThrowsAsync<RefusedException>(() => upgrades.UnprovisionAsync("10.0", null, default));
        Assert.Empty(cluster.CallsTo("StartClusterUpgradeAsync"));
        Assert.Single(cluster.CallsTo("GetClusterManifestAsync"));
    }

    [Fact]
    public async Task Refuses_versions_that_are_not_provisioned()
    {
        var upgrades = new ClusterUpgrades(Cluster(OwnManifest).Client);

        var ex = await Assert.ThrowsAsync<RefusedException>(() => upgrades.StartAsync("11.0", null, UpgradePlan.MonitoredRollback, null, default));

        Assert.Equal("Code version 11.0 isn't provisioned in this cluster.", ex.Message);
        Assert.True(ex.Conflict);
    }

    [Fact]
    public async Task Starts_an_upgrade_with_the_plan()
    {
        var cluster = Cluster(OwnManifest);

        await new ClusterUpgrades(cluster.Client).StartAsync("10.1", "", UpgradePlan.UnmonitoredManual, null, default);

        var description = Assert.IsType<StartClusterUpgradeDescription>(Assert.Single(cluster.CallsTo("StartClusterUpgradeAsync"))[0]);
        Assert.Equal("10.1", description.CodeVersion);
        Assert.Null(description.ConfigVersion);
        Assert.Equal(UpgradeMode.UnmonitoredManual, description.RollingUpgradeMode);
    }

    [Fact]
    public async Task Lists_provisioned_versions_newest_first()
    {
        var versions = await new ClusterUpgrades(Cluster(OwnManifest).Client).GetProvisionedVersionsAsync(default);

        Assert.Equal(["10.1", "10.0"], versions.Code);
        Assert.Equal(["1.0"], versions.Config);
    }

    [Theory]
    [InlineData(UpgradeState.RollingForwardPending, "UD1", "UD1")]
    [InlineData(UpgradeState.RollingForwardInProgress, "UD1", null)]
    [InlineData(UpgradeState.RollingForwardPending, "", null)]
    public async Task Knows_the_domain_a_manual_upgrade_waits_for(UpgradeState state, string next, string? expected)
    {
        var cluster = new FakeCluster().On("GetClusterUpgradeProgressAsync", new ClusterUpgradeProgressObject(upgradeState: state, nextUpgradeDomain: next));

        Assert.Equal(expected, await new ClusterUpgrades(cluster.Client).GetNextDomainAsync(default));
    }

    [Fact]
    public void Ids_follow_names()
    {
        Assert.Equal("shop", ClusterInspector.IdOf("fabric:/shop"));
        Assert.Equal("shop~eu~Counter", ClusterInspector.IdOf("fabric:/shop/eu/Counter"));
        Assert.Equal("fabric:/shop/eu", ClusterInspector.NameOf(ClusterInspector.IdOf("fabric:/shop/eu")));
    }

    [Fact]
    public void A_health_report_carries_its_one_line_reason()
    {
        var unhealthy = HealthReport.From(new NodeHealth(
            HealthState.Warning,
            [new HealthEvent(sourceId: "W", property: "P", healthState: HealthState.Warning, description: "Disk  full.")]));
        var healthy = HealthReport.From(new NodeHealth(
            HealthState.Ok,
            [new HealthEvent(sourceId: "W", property: "P", healthState: HealthState.Warning, description: "Old news.")]));

        Assert.Equal("Disk full.", unhealthy.Reason);
        Assert.Null(healthy.Reason);
    }
}
