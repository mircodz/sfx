using Microsoft.ServiceFabric.Common;
using Microsoft.ServiceFabric.Common.Exceptions;
using Sfx.Core;
using Sfx.Web;

namespace Sfx.Tests;

public class EntityMappingTests
{
    [Fact]
    public void Partitions_carry_their_keys()
    {
        var id = Guid.NewGuid();
        var ranged = Dto.Partition(new PartitionRow(
            new PartitionId(id), "Stateful", "Int64Range", "Ready", HealthState.Ok, "3/3", LowKey: "-9223372036854775808", HighKey: "9223372036854775807"));
        var named = Dto.Partition(new PartitionRow(new PartitionId(id), "Stateless", "Named", "Ready", HealthState.Ok, "1", Name: "east"));

        Assert.Equal(("Int64Range", "-9223372036854775808", "9223372036854775807", (string?)null), (ranged.Scheme, ranged.LowKey, ranged.HighKey, ranged.Name));
        Assert.Equal(("Named", (string?)null, (string?)null, "east"), (named.Scheme, named.LowKey, named.HighKey, named.Name));
    }

    [Fact]
    public void Health_events_come_errors_first_then_newest_first()
    {
        HealthEventRow Event(string property, HealthState state, int minute) =>
            new("source", property, state, "", new DateTime(2024, 1, 1, 0, minute, 0, DateTimeKind.Utc), false);
        var report = new HealthReport(HealthState.Error,
        [
            Event("ok", HealthState.Ok, 59),
            Event("old warning", HealthState.Warning, 1),
            Event("error", HealthState.Error, 0),
            Event("new warning", HealthState.Warning, 30),
        ], []);

        Assert.Equal(["error", "new warning", "old warning", "ok"], Dto.Health(report).Events.Select(e => e.Property));
    }

    [Fact]
    public void Events_of_nested_applications_and_their_services_are_named_from_their_ids()
    {
        var at = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var app = EventQueries.ToRow(new ApplicationHealthReportExpiredEvent(
            Guid.NewGuid(), at, "shop~orders", 1, "source", "property", "Warning", 0, 0, "", false, at));
        var service = EventQueries.ToRow(new ServiceHealthReportExpiredEvent(
            Guid.NewGuid(), at, "shop~orders~Counter", 1, "source", "property", "Warning", 0, 0, "", false, at));

        Assert.Equal("fabric:/shop/orders", app.Entity);
        Assert.Equal("fabric:/shop/orders/Counter", service.Entity);
        Assert.True(EventQueries.IsAbout(app, "fabric:/shop/orders"));
        Assert.True(EventQueries.IsAbout(service, "fabric:/shop/orders"));
        Assert.True(EventQueries.IsAbout(service, "fabric:/shop/orders/Counter"));
        Assert.False(EventQueries.IsAbout(app, "fabric:/shop/order"));
        Assert.False(EventQueries.IsAbout(app, "fabric:/shop/orders/Counter"));
    }

    [Fact]
    public void A_partitions_events_include_its_replicas()
    {
        var partition = Guid.NewGuid().ToString();
        var replica = new EventRow(DateTime.UtcNow, "ReplicaCreated", "", $"{partition} / 132", "replica", "", null!);

        Assert.True(EventQueries.IsAbout(replica, partition));
        Assert.False(EventQueries.IsAbout(replica, Guid.NewGuid().ToString()));
    }

    [Fact]
    public void Ids_turn_into_names()
    {
        Assert.Equal("fabric:/orders", ClusterInspector.NameOf("orders"));
        Assert.Equal("fabric:/shop/orders/Counter", ClusterInspector.NameOf("shop~orders~Counter"));
    }

    [Theory]
    [InlineData("Up", true)]
    [InlineData("Disabling", true)]
    [InlineData("Disabled", true)]
    [InlineData("Enabling", true)]
    [InlineData("Down", false)]
    [InlineData("Removed", false)]
    [InlineData("Unknown", false)]
    [InlineData("Invalid", false)]
    public void Nodes_that_may_host_replicas(string status, bool hosts) =>
        Assert.Equal(hosts, ClusterQueries.MayHostReplicas(status));

    [Fact]
    public async Task A_missing_entity_is_not_found()
    {
        var empty = await Assert.ThrowsAsync<NotFoundException>(() =>
            ClusterInspector.Required<ApplicationInfo>(() => Task.FromResult<ApplicationInfo?>(null), "There's no application fabric:/x on this cluster."));
        var gone = await Assert.ThrowsAsync<NotFoundException>(() => ClusterInspector.Required<ApplicationInfo>(
            () => throw new ServiceFabricException("Gone.", FabricErrorCodes.FABRIC_E_APPLICATION_NOT_FOUND, false), "There's no application fabric:/x on this cluster."));

        Assert.Equal("There's no application fabric:/x on this cluster.", empty.Message);
        Assert.Equal("There's no application fabric:/x on this cluster.", gone.Message);
        await Assert.ThrowsAsync<ServiceFabricException>(() => ClusterInspector.Required<ApplicationInfo>(
            () => throw new ServiceFabricException("Busy.", FabricErrorCodes.FABRIC_E_TIMEOUT, true), "Missing."));
    }

    [Fact]
    public void A_reason_is_asked_again_when_health_changes() =>
        Assert.NotEqual(UiSession.ReasonKey("service", "orders~Counter", "Warning"), UiSession.ReasonKey("service", "orders~Counter", "Error"));
}
