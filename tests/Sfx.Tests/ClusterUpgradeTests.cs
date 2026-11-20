using Microsoft.ServiceFabric.Common;
using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Web;

namespace Sfx.Tests;

public class ClusterUpgradeTests
{
    private static UiSession Session(bool readOnly = false)
    {
        var context = new ClusterContext("test", "localhost", [], new AuthOptions(), readOnly, false);
        var cluster = new ClusterSession(context, new Uri("http://localhost:19080"), new ClusterQueries(null!));
        return new UiSession(cluster, new ActionLog(Path.Combine(Path.GetTempPath(), $"sfx-test-{Guid.NewGuid():N}.log")), "0.0.0");
    }

    private static Dictionary<string, string> Cluster(string azureManaged) => new() { ["name"] = "test", ["azureManaged"] = azureManaged };

    [Fact]
    public void Maps_a_monitored_upgrade_and_its_policy()
    {
        var policy = new UpgradePolicy(
            HealthCheckWait: TimeSpan.FromSeconds(30), UpgradeDomainTimeout: TimeSpan.FromHours(1), ReplicaSetCheckTimeout: TimeSpan.FromMinutes(5),
            ForceRestart: true, ConsiderWarningAsError: true, MaxPercentUnhealthyNodes: 10, MaxPercentUnhealthyApplications: 20);

        var d = ClusterOperations.ClusterUpgradeDescription("10.1.1951.9590", "2.0", UpgradeMode.Monitored, FailureAction.Manual, policy);

        Assert.Equal("10.1.1951.9590", d.CodeVersion);
        Assert.Equal("2.0", d.ConfigVersion);
        Assert.Equal(UpgradeKind.Rolling, d.UpgradeKind);
        Assert.Equal(UpgradeMode.Monitored, d.RollingUpgradeMode);
        Assert.Equal(300, d.UpgradeReplicaSetCheckTimeoutInSeconds);
        Assert.True(d.ForceRestart);
        Assert.Equal(FailureAction.Manual, d.MonitoringPolicy.FailureAction);
        Assert.Equal("30000", d.MonitoringPolicy.HealthCheckWaitDurationInMilliseconds);
        Assert.Equal("3600000", d.MonitoringPolicy.UpgradeDomainTimeoutInMilliseconds);
        Assert.Null(d.MonitoringPolicy.HealthCheckStableDurationInMilliseconds);
        Assert.True(d.ClusterHealthPolicy.ConsiderWarningAsError);
        Assert.Equal(10, d.ClusterHealthPolicy.MaxPercentUnhealthyNodes);
        Assert.Equal(20, d.ClusterHealthPolicy.MaxPercentUnhealthyApplications);
    }

    [Fact]
    public void Leaves_out_what_is_not_changing_or_not_set()
    {
        var d = ClusterOperations.ClusterUpgradeDescription(null, "2.0", UpgradeMode.UnmonitoredAuto, null, null);

        Assert.Null(d.CodeVersion);
        Assert.Equal("2.0", d.ConfigVersion);
        Assert.Null(d.MonitoringPolicy);
        Assert.Null(d.ClusterHealthPolicy);
        Assert.Null(d.ForceRestart);
        Assert.Null(d.UpgradeReplicaSetCheckTimeoutInSeconds);
    }

    [Fact]
    public void A_monitored_upgrade_without_a_policy_rolls_back_on_failure()
    {
        var d = ClusterOperations.ClusterUpgradeDescription("10.1", "", UpgradeMode.Monitored, null, null);

        Assert.Null(d.ConfigVersion);
        Assert.Equal(FailureAction.Rollback, d.MonitoringPolicy.FailureAction);
        Assert.Null(d.ClusterHealthPolicy);
    }

    [Fact]
    public void Needs_a_version()
    {
        Assert.Throws<ArgumentException>(() => ClusterOperations.ClusterUpgradeDescription(null, "", UpgradeMode.Monitored, null, null));
    }

    [Theory]
    [InlineData("<Infrastructure><PaaS><Roles /><Votes /></PaaS></Infrastructure>", true)]
    [InlineData("<Infrastructure><WindowsServer IsScaleMin=\"false\" /></Infrastructure>", false)]
    [InlineData("<Infrastructure><Linux /></Infrastructure>", false)]
    [InlineData("<Infrastructure><Blackbird /></Infrastructure>", false)]
    [InlineData("", false)]
    public void Recognizes_clusters_azure_manages(string infrastructure, bool azure)
    {
        var manifest = $"""
            <ClusterManifest xmlns="http://schemas.microsoft.com/2011/01/fabric" Name="c" Version="1.0">
              <NodeTypes />
              {infrastructure}
              <FabricSettings><Section Name="PaaS"><Parameter Name="X" Value="1" /></Section></FabricSettings>
            </ClusterManifest>
            """;

        Assert.Equal(azure, ClusterKind.IsAzureManaged(manifest));
    }

    [Fact]
    public void A_manifest_that_does_not_parse_is_not_azure_managed()
    {
        Assert.False(ClusterKind.IsAzureManaged("not xml"));
    }

    [Fact]
    public void Offers_the_upgrade_only_when_azure_does_not_manage_the_cluster()
    {
        var action = WebActions.For(Session(), "cluster", Cluster("false")).Single(a => a.Id == "cluster.upgrade");

        Assert.Equal("confirm", action.Risk);
        Assert.True(action.Form);
        Assert.Equal("Nodes restart domain by domain on the new version or configuration.", action.Warning);
        Assert.Equal(["codeVersion", "configVersion", "mode"], action.Inputs.Select(i => i.Name));
        Assert.Equal(["version", "version", "choice"], action.Inputs.Select(i => i.Kind));
        Assert.Equal("monitored-rollback", action.Inputs[2].Default);

        Assert.DoesNotContain(WebActions.For(Session(), "cluster", Cluster("true")), a => a.Id == "cluster.upgrade");
        Assert.DoesNotContain(WebActions.For(Session(), "cluster", Cluster("unknown")), a => a.Id == "cluster.upgrade");
        Assert.Empty(WebActions.For(Session(readOnly: true), "cluster", Cluster("false")));
    }

    [Fact]
    public async Task Refuses_to_upgrade_a_cluster_azure_manages()
    {
        var session = Session();
        var target = Cluster("true");
        var request = new ActionRequest(target, new() { ["codeVersion"] = "10.1", ["mode"] = "monitored-rollback" }, null, session.Sign("cluster.upgrade", target));

        var ex = await Assert.ThrowsAsync<ActionException>(() => WebActions.RunAsync(session, "cluster.upgrade", request, default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Needs_a_code_or_config_version()
    {
        var session = Session();
        var action = WebActions.For(session, "cluster", Cluster("false")).Single(a => a.Id == "cluster.upgrade");

        var ex = await Assert.ThrowsAsync<ActionException>(() => WebActions.RunAsync(
            session, action.Id, new ActionRequest(new(action.Target), new() { ["codeVersion"] = "", ["mode"] = "monitored-rollback" }, null, action.Signature), default));
        Assert.Equal("Choose a code version, a config version or both.", ex.Message);
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public async Task Refuses_application_limits_before_reaching_the_cluster()
    {
        var session = Session();
        var action = WebActions.For(session, "cluster", Cluster("false")).Single(a => a.Id == "cluster.upgrade");
        var inputs = new Dictionary<string, string>
        {
            ["codeVersion"] = "10.1", ["mode"] = "monitored-rollback", ["policy.maxPercentUnhealthyServices"] = "10",
        };

        var ex = await Assert.ThrowsAsync<ActionException>(() =>
            WebActions.RunAsync(session, action.Id, new ActionRequest(new(action.Target), inputs, null, action.Signature), default));
        Assert.Equal("Max unhealthy services only applies to application upgrades.", ex.Message);
    }

    [Theory]
    [InlineData("10.1", "2.0", "monitored-rollback", "Upgrade the cluster to code 10.1, config 2.0 (monitored, roll back on failure)")]
    [InlineData("10.1", "", "unmonitored-auto", "Upgrade the cluster to code 10.1 (unmonitored, automatic)")]
    [InlineData(null, "2.0", "monitored-manual", "Upgrade the cluster to config 2.0 (monitored, stop on failure)")]
    public void Describes_only_what_changes(string? code, string config, string mode, string expected)
    {
        var inputs = new Dictionary<string, string> { ["configVersion"] = config, ["mode"] = mode };
        if (code is not null)
        {
            inputs["codeVersion"] = code;
        }

        Assert.Equal(expected, ClusterUpgrades.Describe(new Args(inputs), Label(mode), Mode(mode)));
    }

    [Fact]
    public void Says_when_the_policy_is_custom()
    {
        var inputs = new Args(new Dictionary<string, string> { ["codeVersion"] = "10.1", ["policy.maxPercentUnhealthyNodes"] = "10" });

        Assert.EndsWith(", custom policy", ClusterUpgrades.Describe(inputs, "Monitored, roll back on failure", UpgradeMode.Monitored));
    }

    private static string Label(string mode) => mode switch
    {
        "monitored-rollback" => "Monitored, roll back on failure",
        "monitored-manual" => "Monitored, stop on failure",
        _ => "Unmonitored, automatic",
    };

    private static UpgradeMode Mode(string mode) => mode.StartsWith("monitored", StringComparison.Ordinal) ? UpgradeMode.Monitored : UpgradeMode.UnmonitoredAuto;
}

public class ClusterUpgradePolicyTests
{
    private static Args Inputs(params (string Name, string Value)[] values) =>
        new(values.ToDictionary(v => "policy." + v.Name, v => v.Value));

    [Fact]
    public void Reads_cluster_limits_for_a_cluster_upgrade()
    {
        var policy = UpgradePolicies.Parse(Inputs(("maxPercentUnhealthyNodes", "10"), ("maxPercentUnhealthyApplications", "0")), monitored: true, cluster: true);

        Assert.Equal(new UpgradePolicy(MaxPercentUnhealthyNodes: 10, MaxPercentUnhealthyApplications: 0), policy);
        Assert.True(policy!.HasClusterHealthPolicy);
        Assert.False(policy.HasHealthPolicy);
    }

    [Fact]
    public void Reads_the_shared_settings_for_a_cluster_upgrade()
    {
        var policy = UpgradePolicies.Parse(
            Inputs(("healthCheckWait", "30"), ("forceRestart", "true"), ("considerWarningAsError", "true")), monitored: true, cluster: true);

        Assert.Equal(new UpgradePolicy(HealthCheckWait: TimeSpan.FromSeconds(30), ForceRestart: true, ConsiderWarningAsError: true), policy);
    }

    [Theory]
    [InlineData("maxPercentUnhealthyNodes")]
    [InlineData("maxPercentUnhealthyApplications")]
    public void Cluster_limits_need_a_monitored_upgrade(string name)
    {
        var ex = Assert.Throws<ActionException>(() => UpgradePolicies.Parse(Inputs((name, "10")), monitored: false, cluster: true));

        Assert.Equal("Timeouts and health checks only apply to monitored upgrades.", ex.Message);
    }

    [Theory]
    [InlineData("maxPercentUnhealthyNodes", "101", "Max unhealthy nodes must be a percentage from 0 to 100.")]
    [InlineData("maxPercentUnhealthyApplications", "x", "Max unhealthy applications must be a percentage from 0 to 100.")]
    public void Names_the_cluster_limit_it_cannot_read(string name, string value, string message)
    {
        var ex = Assert.Throws<ActionException>(() => UpgradePolicies.Parse(Inputs((name, value)), monitored: true, cluster: true));

        Assert.Equal(message, ex.Message);
    }

    [Theory]
    [InlineData("maxPercentUnhealthyNodes", "Max unhealthy nodes only applies to cluster upgrades.")]
    [InlineData("maxPercentUnhealthyApplications", "Max unhealthy applications only applies to cluster upgrades.")]
    public void Cluster_limits_are_refused_for_an_application(string name, string message)
    {
        var ex = Assert.Throws<ActionException>(() => UpgradePolicies.Parse(Inputs((name, "10")), monitored: true));

        Assert.Equal(message, ex.Message);
    }

    [Theory]
    [InlineData("maxPercentUnhealthyDeployedApplications", "Max unhealthy deployed applications only applies to application upgrades.")]
    [InlineData("maxPercentUnhealthyServices", "Max unhealthy services only applies to application upgrades.")]
    [InlineData("maxPercentUnhealthyPartitionsPerService", "Max unhealthy partitions per service only applies to application upgrades.")]
    [InlineData("maxPercentUnhealthyReplicasPerPartition", "Max unhealthy replicas per partition only applies to application upgrades.")]
    public void Application_limits_are_refused_for_the_cluster(string name, string message)
    {
        var ex = Assert.Throws<ActionException>(() => UpgradePolicies.Parse(Inputs((name, "10")), monitored: true, cluster: true));

        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void Empty_cluster_limits_mean_the_clusters_default()
    {
        Assert.Null(UpgradePolicies.Parse(Inputs(("maxPercentUnhealthyNodes", ""), ("maxPercentUnhealthyServices", "")), monitored: false, cluster: true));
        Assert.Null(UpgradePolicies.Parse(Inputs(("maxPercentUnhealthyNodes", "")), monitored: false));
    }
}
