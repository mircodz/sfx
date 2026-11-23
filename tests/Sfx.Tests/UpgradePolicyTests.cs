using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Web;

namespace Sfx.Tests;

public class UpgradePolicyTests
{
    private static Dictionary<string, string> Inputs(params (string Name, string Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);

    [Fact]
    public void No_policy_inputs_mean_no_policy()
    {
        Assert.Null(UpgradePolicy.Parse(new Dictionary<string, string>(), monitored: true));
    }

    [Fact]
    public void Empty_inputs_mean_the_clusters_default()
    {
        var inputs = Inputs(("healthCheckWait", ""), ("forceRestart", ""), ("maxPercentUnhealthyServices", ""));

        Assert.Null(UpgradePolicy.Parse(inputs, monitored: true));
        Assert.Null(UpgradePolicy.Parse(inputs, monitored: false));
    }

    [Fact]
    public void Reads_durations_as_seconds()
    {
        var policy = UpgradePolicy.Parse(
            Inputs(
                ("healthCheckWait", "30"), ("healthCheckStable", "120"), ("healthCheckRetry", "600"),
                ("upgradeDomainTimeout", "3600"), ("upgradeTimeout", "0"), ("replicaSetCheckTimeout", "4294967295")),
            monitored: true);

        Assert.Equal(
            new UpgradePolicy(
                HealthCheckWait: TimeSpan.FromSeconds(30),
                HealthCheckStable: TimeSpan.FromMinutes(2),
                HealthCheckRetry: TimeSpan.FromMinutes(10),
                UpgradeDomainTimeout: TimeSpan.FromHours(1),
                UpgradeTimeout: TimeSpan.Zero,
                ReplicaSetCheckTimeout: TimeSpan.FromSeconds(uint.MaxValue)),
            policy);
    }

    [Fact]
    public void Reads_flags()
    {
        var policy = UpgradePolicy.Parse(Inputs(("forceRestart", "true"), ("considerWarningAsError", "false")), monitored: true);

        Assert.Equal(new UpgradePolicy(ForceRestart: true, ConsiderWarningAsError: false), policy);
    }

    [Fact]
    public void Reads_percentages()
    {
        var policy = UpgradePolicy.Parse(
            Inputs(
                ("maxPercentUnhealthyDeployedApplications", "0"), ("maxPercentUnhealthyServices", "10"),
                ("maxPercentUnhealthyPartitionsPerService", "20"), ("maxPercentUnhealthyReplicasPerPartition", "100")),
            monitored: true);

        Assert.Equal(
            new UpgradePolicy(
                MaxPercentUnhealthyDeployedApplications: 0, MaxPercentUnhealthyServices: 10,
                MaxPercentUnhealthyPartitionsPerService: 20, MaxPercentUnhealthyReplicasPerPartition: 100),
            policy);
        Assert.True(policy!.HasHealthPolicy);
        Assert.True(policy.HasServiceTypeHealthPolicy);
    }

    [Theory]
    [InlineData("healthCheckWait", "soon", "Health check wait must be a whole number of seconds.")]
    [InlineData("healthCheckStable", "1.5", "Health check stable duration must be a whole number of seconds.")]
    [InlineData("healthCheckRetry", "-1", "Health check retry timeout must be a whole number of seconds.")]
    [InlineData("upgradeDomainTimeout", "PT1H", "Upgrade domain timeout must be a whole number of seconds.")]
    [InlineData("upgradeTimeout", "99999999999", "Upgrade timeout must be a whole number of seconds.")]
    [InlineData("replicaSetCheckTimeout", " 5", "Replica set check timeout must be a whole number of seconds.")]
    [InlineData("forceRestart", "yes", "Force restart must be true or false.")]
    [InlineData("considerWarningAsError", "1", "Consider warnings as errors must be true or false.")]
    [InlineData("maxPercentUnhealthyServices", "ten", "Max unhealthy services must be a percentage from 0 to 100.")]
    [InlineData("maxPercentUnhealthyDeployedApplications", "101", "Max unhealthy deployed applications must be a percentage from 0 to 100.")]
    [InlineData("maxPercentUnhealthyPartitionsPerService", "-5", "Max unhealthy partitions per service must be a percentage from 0 to 100.")]
    [InlineData("maxPercentUnhealthyReplicasPerPartition", "50%", "Max unhealthy replicas per partition must be a percentage from 0 to 100.")]
    public void Names_the_field_it_cannot_read(string name, string value, string message)
    {
        var ex = Assert.Throws<RefusedException>(() => UpgradePolicy.Parse(Inputs((name, value)), monitored: true));

        Assert.Equal(message, ex.Message);
        Assert.False(ex.Conflict);
    }

    [Theory]
    [InlineData("healthCheckWait", "30")]
    [InlineData("healthCheckStable", "30")]
    [InlineData("healthCheckRetry", "30")]
    [InlineData("upgradeTimeout", "3600")]
    [InlineData("upgradeDomainTimeout", "600")]
    [InlineData("considerWarningAsError", "true")]
    [InlineData("maxPercentUnhealthyDeployedApplications", "10")]
    [InlineData("maxPercentUnhealthyServices", "10")]
    [InlineData("maxPercentUnhealthyPartitionsPerService", "10")]
    [InlineData("maxPercentUnhealthyReplicasPerPartition", "10")]
    public void Timeouts_and_health_checks_need_a_monitored_upgrade(string name, string value)
    {
        Assert.NotNull(UpgradePolicy.Parse(Inputs((name, value)), monitored: true));
        var ex = Assert.Throws<RefusedException>(() => UpgradePolicy.Parse(Inputs((name, value)), monitored: false));

        Assert.Equal("Timeouts and health checks only apply to monitored upgrades.", ex.Message);
    }

    [Theory]
    [InlineData("replicaSetCheckTimeout", "60")]
    [InlineData("forceRestart", "true")]
    public void Other_settings_apply_to_any_upgrade(string name, string value)
    {
        Assert.NotNull(UpgradePolicy.Parse(Inputs((name, value)), monitored: false));
    }

    [Fact]
    public async Task The_action_refuses_monitoring_settings_on_an_unmonitored_upgrade_before_reaching_the_cluster()
    {
        var context = new ClusterContext("test", "localhost", [], new AuthOptions(), false, false);
        var cluster = new ClusterSession(context, new Uri("http://localhost:19080"), new ClusterQueries(null!));
        var session = new UiSession(cluster, new ActionLog(Path.Combine(Path.GetTempPath(), $"sfx-test-{Guid.NewGuid():N}.log")), "0.0.0");
        var target = new Dictionary<string, string>
        {
            ["applicationId"] = "orders", ["name"] = "fabric:/orders", ["typeName"] = "OrdersType", ["version"] = "1.0.0",
        };
        var action = WebActions.For(session, "application", target).Single(a => a.Id == "application.upgrade");
        var inputs = new Dictionary<string, string>
        {
            ["version"] = "1.0.0", ["mode"] = "unmonitored-auto", ["policy.upgradeTimeout"] = "3600",
        };

        var ex = await Assert.ThrowsAsync<ActionException>(() =>
            WebActions.RunAsync(session, action.Id, new ActionRequest(new(action.Target), inputs, null, action.Signature), default));
        Assert.Equal("Timeouts and health checks only apply to monitored upgrades.", ex.Message);
    }
}
