using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Tests;

public class ServiceSettingsTests
{
    private static StatefulServiceDescription Stateful() => new(
        new ServiceName("fabric:/shop/Counter"),
        "CounterType",
        new SingletonPartitionSchemeDescription(),
        3,
        2,
        true,
        placementConstraints: "NodeType == A",
        defaultMoveCost: MoveCost.Low,
        quorumLossWaitDurationSeconds: 120,
        replicaRestartWaitDurationSeconds: 60);

    private static StatelessServiceDescription Stateless() => new(
        new ServiceName("fabric:/shop/Web"),
        "WebType",
        new SingletonPartitionSchemeDescription(),
        5,
        minInstanceCount: 2);

    private static ServiceSetting Setting(string key, bool stateful = true) =>
        ServiceSettings.Read(stateful ? Stateful() : Stateless()).Single(s => s.Key == key);

    [Theory]
    [InlineData("30", 30L)]
    [InlineData("30s", 30L)]
    [InlineData("5m", 300L)]
    [InlineData("2H", 7200L)]
    [InlineData("1d", 86400L)]
    [InlineData(" 45 ", 45L)]
    [InlineData("soon", null)]
    [InlineData("-5", null)]
    [InlineData("5w", null)]
    [InlineData("", null)]
    public void Reads_durations(string text, long? seconds) => Assert.Equal(seconds, ServiceSettings.Seconds(text));

    [Theory]
    [InlineData(SettingKind.Seconds, "5m", "300")]
    [InlineData(SettingKind.Count, "-1", "-1")]
    [InlineData(SettingKind.Text, " NodeType == A ", "NodeType == A")]
    public void Parses_service_setting_values(SettingKind kind, string text, string expected) =>
        Assert.Equal(expected, ServiceSettings.Parse(new ServiceSetting("k", "K", kind, ""), text));

    [Fact]
    public void Refuses_bad_values_with_what_it_expects()
    {
        Assert.Equal(
            "Quorum loss wait must be a number of seconds, or a number with s, m, h or d (like 30s, 5m, 1h).",
            Assert.Throws<RefusedException>(() => ServiceSettings.Parse(Setting("quorum-loss-wait"), "soon")).Message);
        Assert.Equal(
            "Target replicas must be a whole number of 1 or more.",
            Assert.Throws<RefusedException>(() => ServiceSettings.Parse(Setting("count"), "0")).Message);
        Assert.Equal(
            "Instances must be a whole number of 1 or more, or -1 for every node.",
            Assert.Throws<RefusedException>(() => ServiceSettings.Parse(Setting("count", stateful: false), "0")).Message);
        Assert.Equal(
            "Min instance percentage must be a whole number from 0 to 100.",
            Assert.Throws<RefusedException>(() => ServiceSettings.Parse(Setting("min-instance-percent", stateful: false), "101")).Message);
    }

    [Fact]
    public void Choices_take_their_listed_casing()
    {
        var moveCost = Setting("move-cost");

        Assert.Equal("High", ServiceSettings.Parse(moveCost, "high"));
        Assert.Equal("true", ServiceSettings.Parse(Setting("drop-source-on-move"), "TRUE"));
        Assert.StartsWith(
            "Default move cost must be one of: Zero, Low, Medium, High",
            Assert.Throws<RefusedException>(() => ServiceSettings.Parse(moveCost, "huge")).Message);
    }

    [Fact]
    public void Reads_the_editable_settings_of_each_kind()
    {
        var stateful = ServiceSettings.Read(Stateful()).ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal("3", stateful["count"]);
        Assert.Equal("2", stateful["min-replicas"]);
        Assert.Equal("120", stateful["quorum-loss-wait"]);
        Assert.Equal("", stateful["standby-keep"]);
        Assert.Equal("NodeType == A", stateful["placement-constraints"]);
        Assert.Equal("Low", stateful["move-cost"]);
        Assert.False(stateful.ContainsKey("min-instances"));

        var stateless = ServiceSettings.Read(Stateless()).ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal("5", stateless["count"]);
        Assert.Equal("2", stateless["min-instances"]);
        Assert.False(stateless.ContainsKey("min-replicas"));
    }

    [Fact]
    public void An_update_sets_only_its_fields_and_their_flags()
    {
        var update = Assert.IsType<StatefulServiceUpdateDescription>(
            ServiceSettings.BuildUpdate(stateful: true, new Dictionary<string, string> { ["quorum-loss-wait"] = "300" }));
        Assert.Equal("4", update.Flags);
        Assert.Equal("300", update.QuorumLossWaitDurationSeconds);
        Assert.Null(update.TargetReplicaSetSize);
        Assert.Null(update.PlacementConstraints);

        var stateless = Assert.IsType<StatelessServiceUpdateDescription>(
            ServiceSettings.BuildUpdate(stateful: false, new Dictionary<string, string> { ["move-cost"] = "High", ["min-instances"] = "1", ["count"] = "4" }));
        Assert.Equal((512 | 4096 | 1).ToString(), stateless.Flags);
        Assert.Equal(MoveCost.High, stateless.DefaultMoveCost);
        Assert.Equal(1, stateless.MinInstanceCount);
        Assert.Equal(4, stateless.InstanceCount);

        var combined = Assert.IsType<StatefulServiceUpdateDescription>(ServiceSettings.BuildUpdate(stateful: true, new Dictionary<string, string>
        {
            ["count"] = "5",
            ["min-replicas"] = "3",
            ["placement-constraints"] = "",
            ["replica-restart-wait"] = "60",
            ["standby-keep"] = "600",
            ["placement-time-limit"] = "30",
            ["drop-source-on-move"] = "true",
            ["dns-name"] = "counter",
        }));
        Assert.Equal((1 | 16 | 32 | 2 | 8 | 2048 | 65536 | 131072).ToString(), combined.Flags);
        Assert.Equal(5, combined.TargetReplicaSetSize);
        Assert.Equal(3, combined.MinReplicaSetSize);
        Assert.Equal("", combined.PlacementConstraints);
        Assert.True(combined.DropSourceReplicaOnMove);
        Assert.Equal("counter", combined.ServiceDnsName);

        var waits = Assert.IsType<StatelessServiceUpdateDescription>(ServiceSettings.BuildUpdate(stateful: false, new Dictionary<string, string>
        {
            ["min-instance-percent"] = "50",
            ["instance-close-delay"] = "10",
            ["instance-restart-wait"] = "20",
        }));
        Assert.Equal((8192 | 16384 | 32768).ToString(), waits.Flags);
    }

    [Fact]
    public void An_update_refuses_a_setting_the_kind_does_not_have()
    {
        Assert.Throws<ArgumentException>(() => ServiceSettings.BuildUpdate(stateful: false, new Dictionary<string, string> { ["min-replicas"] = "1" }));
    }

    [Fact]
    public void Changes_leave_out_unchanged_values_and_check_minimums_against_targets()
    {
        var current = new ServiceSettingSet(true, ServiceSettings.Read(Stateful()));

        Assert.Equal(
            new Dictionary<string, string> { ["quorum-loss-wait"] = "300" },
            ServiceSettings.Changes(current, new Dictionary<string, string> { ["count"] = "3", ["quorum-loss-wait"] = "5m", ["replica-restart-wait"] = "1m" }));
        Assert.Equal(
            "Min replicas (2) can't be more than target replicas (1).",
            Assert.Throws<RefusedException>(() => ServiceSettings.Changes(current, new Dictionary<string, string> { ["count"] = "1" })).Message);
        Assert.Equal(
            "A stateful service has no setting 'min-instances'.",
            Assert.Throws<RefusedException>(() => ServiceSettings.Changes(current, new Dictionary<string, string> { ["min-instances"] = "1" })).Message);

        var stateless = new ServiceSettingSet(false, ServiceSettings.Read(Stateless()));
        Assert.Equal(
            "Min instances (2) can't be more than instances (1).",
            Assert.Throws<RefusedException>(() => ServiceSettings.Changes(stateless, new Dictionary<string, string> { ["count"] = "1" })).Message);
        Assert.Single(ServiceSettings.Changes(stateless, new Dictionary<string, string> { ["count"] = "-1" }));
    }

    [Fact]
    public async Task Updates_several_settings_in_one_call()
    {
        var cluster = new FakeCluster().On("GetServiceDescriptionAsync", Stateful()).OnCall("UpdateServiceAsync");
        var settings = new ServiceSettings(cluster.Client);

        var applied = await settings.UpdateAsync("shop~Counter", new Dictionary<string, string>
        {
            ["quorum-loss-wait"] = "5m",
            ["move-cost"] = "high",
        }, default);

        Assert.Equal(new Dictionary<string, string> { ["quorum-loss-wait"] = "300", ["move-cost"] = "High" }, applied);
        var args = Assert.Single(cluster.CallsTo("UpdateServiceAsync"));
        Assert.Equal("shop~Counter", args[0]);
        Assert.Equal((4 | 512).ToString(), Assert.IsType<StatefulServiceUpdateDescription>(args[1]).Flags);

        Assert.Empty(await settings.UpdateAsync("shop~Counter", new Dictionary<string, string> { ["count"] = "3" }, default));
        Assert.Single(cluster.CallsTo("UpdateServiceAsync"));
    }

    [Fact]
    public void Describes_the_changes()
    {
        var settings = ServiceSettings.Read(Stateful());

        Assert.Equal(
            "Change Quorum loss wait to 300s and Default move cost to High on fabric:/shop/Counter",
            ServiceSettings.Describe("fabric:/shop/Counter", settings, new Dictionary<string, string>
            {
                ["quorum-loss-wait"] = "300",
                ["move-cost"] = "High",
            }));
        Assert.Equal(
            "Change Placement constraints to nothing on fabric:/shop/Counter",
            ServiceSettings.Describe("fabric:/shop/Counter", settings, new Dictionary<string, string> { ["placement-constraints"] = "" }));
    }
}
