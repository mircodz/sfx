using Microsoft.ServiceFabric.Common;
using Sfx.Core;
using Sfx.Web;

namespace Sfx.Tests;

public class OverviewTests
{
    private static EntityKindHealthStateCount Count(EntityKind kind, long ok, long warning, long error) =>
        new(kind, new HealthStateCount(ok, warning, error));

    [Fact]
    public void Health_counts_come_in_entity_order_without_other_kinds()
    {
        var statistics = new HealthStatistics(
            [
                Count(EntityKind.Replica, 60_000, 3, 1),
                Count(EntityKind.DeployedApplication, 5, 0, 0),
                Count(EntityKind.Partition, 20_000, 2, 0),
                Count(EntityKind.Node, 398, 1, 1),
                Count(EntityKind.Service, 3_000, 0, 0),
                Count(EntityKind.DeployedServicePackage, 7, 0, 0),
                Count(EntityKind.Application, 999, 1, 0),
            ]);

        Assert.Equal(
            [
                new HealthCountDto("Node", 398, 1, 1),
                new HealthCountDto("Application", 999, 1, 0),
                new HealthCountDto("Service", 3_000, 0, 0),
                new HealthCountDto("Partition", 20_000, 2, 0),
                new HealthCountDto("Replica", 60_000, 3, 1),
            ],
            Dto.HealthCounts(statistics));
    }

    [Fact]
    public void Health_counts_leave_out_kinds_the_cluster_did_not_count()
    {
        var statistics = new HealthStatistics([Count(EntityKind.Application, 1, 0, 0), new EntityKindHealthStateCount(EntityKind.Node, null)]);

        Assert.Equal([new HealthCountDto("Node", 0, 0, 0), new HealthCountDto("Application", 1, 0, 0)], Dto.HealthCounts(statistics));
        Assert.Empty(ClusterOverview.Counts(null));
    }

    private static LoadMetricInformation Metric(
        string name,
        string capacity = "0",
        double? load = null,
        string clusterLoad = "0",
        bool? balancedBefore = null,
        bool? balancedAfter = null,
        string? minValue = null,
        double? minimum = null,
        string? minNode = null,
        string? maxValue = null,
        double? maximum = null,
        string? maxNode = null) =>
        new(
            name,
            balancedBefore,
            balancedAfter,
            null,
            null,
            null,
            null,
            null,
            capacity,
            clusterLoad,
            load,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            minValue,
            minimum,
            minNode is null ? null : new NodeId(minNode),
            maxValue,
            maximum,
            maxNode is null ? null : new NodeId(maxNode),
            null);

    [Fact]
    public void Load_summaries_skip_system_metrics_and_put_capacity_first()
    {
        var load = new ClusterLoadInfo(
            null,
            null,
            [
                Metric("__NamingServiceReplicaCount__", load: 12),
                Metric("Zeta", load: 1),
                Metric("MemoryMB", capacity: "4096", load: 1024),
                Metric("Alpha", capacity: "1e300", clusterLoad: "2.5"),
            ]);

        var summaries = ApiLoad.Summaries(load);

        Assert.Equal(["MemoryMB", "Alpha", "Zeta"], summaries.Select(m => m.Name));
        Assert.Equal(4096, summaries[0].ClusterCapacity);
        Assert.Equal(1024, summaries[0].ClusterLoad);
        // An unset capacity comes back huge; the load falls back to the older text field.
        Assert.Null(summaries[1].ClusterCapacity);
        Assert.Equal(2.5, summaries[1].ClusterLoad);
    }

    [Fact]
    public void Load_summaries_carry_the_least_and_most_loaded_nodes()
    {
        var load = new ClusterLoadInfo(
            null,
            null,
            [
                Metric("Cpu", balancedBefore: false, balancedAfter: true, minimum: 1.5, minValue: "9", minNode: "a1", maximum: 7, maxNode: "b2"),
                Metric("Disk", balancedBefore: false, minValue: "3", minNode: "c3", maxValue: "8.25", maxNode: ""),
                Metric("Net"),
            ]);

        var summaries = ApiLoad.Summaries(load);

        Assert.Equal(new LoadSummaryDto("Cpu", 0, null, true, "a1", 1.5, "b2", 7), summaries[0]);
        Assert.Equal(new LoadSummaryDto("Disk", 0, null, false, "c3", 3, null, 8.25), summaries[1]);
        Assert.Equal(new LoadSummaryDto("Net", 0, null, null, null, null, null, null), summaries[2]);
    }

    [Fact]
    public void A_cluster_capacity_of_zero_counts_when_every_node_type_declares_it()
    {
        var load = new ClusterLoadInfo(null, null, [Metric("Licenses", capacity: "0"), Metric("Other", capacity: "0")]);
        var capacities = new NodeCapacities(new Dictionary<string, IReadOnlyDictionary<string, double>>
        {
            ["FrontEnd"] = new Dictionary<string, double> { ["Licenses"] = 0 },
            ["BackEnd"] = new Dictionary<string, double> { ["Licenses"] = 0, ["Other"] = 0 },
        });

        var summaries = ApiLoad.Summaries(load, capacities);

        Assert.Equal(("Licenses", (double?)0), (summaries[0].Name, summaries[0].ClusterCapacity));
        Assert.Equal(("Other", (double?)null), (summaries[1].Name, summaries[1].ClusterCapacity));
    }

    [Fact]
    public void Least_and_most_loaded_nodes_are_named()
    {
        var load = new ClusterLoadInfo(
            null,
            null,
            [
                Metric("Cpu", minimum: 1, minNode: "A1B2", maximum: 7, maxNode: "ffff"),
                Metric("Idle", minimum: 0, minNode: "a1b2", maximum: 0, maxNode: "c3d4"),
            ]);
        var names = ApiLoad.NamesById(
            [
                new NodeRow("_Node_0", "", "FrontEnd", "Up", HealthState.Ok, "0", "fd:/0", false, "", null, "a1b2"),
                new NodeRow("_Node_1", "", "FrontEnd", "Up", HealthState.Ok, "1", "fd:/1", false, "", null, "c3d4"),
            ]);

        var summaries = ApiLoad.Summaries(load, nodeNames: names);

        // Ids are matched regardless of case; one not in the list is shown as is.
        Assert.Equal(new LoadSummaryDto("Cpu", 0, null, null, "_Node_0", 1, "ffff", 7), summaries[0]);
        // No load anywhere: no node is the least or most loaded.
        Assert.Equal(new LoadSummaryDto("Idle", 0, null, null, null, 0, null, 0), summaries[1]);
    }
}
