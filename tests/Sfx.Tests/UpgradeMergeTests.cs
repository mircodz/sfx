using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Tests;

public class UpgradeMergeTests
{
    private const string ApplicationManifest = """
        <ApplicationManifest xmlns="http://schemas.microsoft.com/2011/01/fabric" ApplicationTypeName="OrdersType" ApplicationTypeVersion="2.0">
          <Policies>
            <HealthPolicy ConsiderWarningAsError="true" MaxPercentUnhealthyDeployedApplications="20">
              <DefaultServiceTypeHealthPolicy MaxPercentUnhealthyServices="10" MaxPercentUnhealthyPartitionsPerService="15" MaxPercentUnhealthyReplicasPerPartition="5" />
              <ServiceTypeHealthPolicy ServiceTypeName="GatewayType" MaxPercentUnhealthyPartitionsPerService="30" />
            </HealthPolicy>
          </Policies>
        </ApplicationManifest>
        """;

    private const string ClusterManifest = """
        <ClusterManifest xmlns="http://schemas.microsoft.com/2011/01/fabric" Name="test" Version="3.0">
          <FabricSettings>
            <Section Name="HealthManager/ClusterHealthPolicy">
              <Parameter Name="ConsiderWarningAsError" Value="True" />
              <Parameter Name="MaxPercentUnhealthyApplications" Value="10" />
              <Parameter Name="MaxPercentUnhealthyNodes" Value="20" />
              <Parameter Name="ApplicationTypeMaxPercentUnhealthyApplications-OrdersType" Value="0" />
            </Section>
          </FabricSettings>
        </ClusterManifest>
        """;

    [Fact]
    public void An_application_policy_keeps_the_manifest_values_it_does_not_set()
    {
        var policy = ClusterOperations.HealthPolicy(new UpgradePolicy(MaxPercentUnhealthyServices: 50), ApplicationManifest);

        Assert.True(policy.ConsiderWarningAsError);
        Assert.Equal(20, policy.MaxPercentUnhealthyDeployedApplications);
        Assert.Equal(50, policy.DefaultServiceTypeHealthPolicy.MaxPercentUnhealthyServices);
        Assert.Equal(15, policy.DefaultServiceTypeHealthPolicy.MaxPercentUnhealthyPartitionsPerService);
        Assert.Equal(5, policy.DefaultServiceTypeHealthPolicy.MaxPercentUnhealthyReplicasPerPartition);
        var gateway = Assert.Single(policy.ServiceTypeHealthPolicyMap);
        Assert.Equal("GatewayType", gateway.Key);
        Assert.Equal(30, gateway.Value.MaxPercentUnhealthyPartitionsPerService);
    }

    [Fact]
    public void An_application_policy_without_a_manifest_falls_back_to_service_fabric_defaults()
    {
        var policy = ClusterOperations.HealthPolicy(new UpgradePolicy(ConsiderWarningAsError: true), null);

        Assert.True(policy.ConsiderWarningAsError);
        Assert.Equal(0, policy.MaxPercentUnhealthyDeployedApplications);
        Assert.Equal(0, policy.DefaultServiceTypeHealthPolicy.MaxPercentUnhealthyServices);
        Assert.Null(policy.ServiceTypeHealthPolicyMap);
    }

    [Fact]
    public void A_cluster_policy_keeps_the_manifest_values_it_does_not_set()
    {
        var d = ClusterOperations.ClusterUpgradeDescription(
            "10.1",
            null,
            UpgradeMode.Monitored,
            null,
            new UpgradePolicy(MaxPercentUnhealthyNodes: 5),
            ClusterManifest);

        Assert.True(d.ClusterHealthPolicy.ConsiderWarningAsError);
        Assert.Equal(5, d.ClusterHealthPolicy.MaxPercentUnhealthyNodes);
        Assert.Equal(10, d.ClusterHealthPolicy.MaxPercentUnhealthyApplications);
        var orders = Assert.Single(d.ClusterHealthPolicy.ApplicationTypeHealthPolicyMap);
        Assert.Equal(("OrdersType", 0), (orders.Key, orders.Value));
    }

    [Fact]
    public void Reads_the_cluster_health_policy_from_the_manifest()
    {
        var defaults = UpgradeDefaults.ClusterHealthPolicy(ClusterManifest);

        Assert.True(defaults.ConsiderWarningAsError);
        Assert.Equal(20, defaults.MaxPercentUnhealthyNodes);
        Assert.Equal(10, defaults.MaxPercentUnhealthyApplications);
        Assert.Equal(new Dictionary<string, int> { ["OrdersType"] = 0 }, defaults.ApplicationTypes);
    }

    [Fact]
    public void The_config_version_comes_from_the_cluster_manifest()
    {
        Assert.Equal("3.0", ClusterSettings.ConfigVersion(ClusterManifest));
        Assert.Null(ClusterSettings.ConfigVersion("<ClusterManifest />"));
    }

    [Fact]
    public void A_parameter_left_to_its_default_changes_when_the_default_does()
    {
        var changed = DefaultServices.ChangedParameters(
            current: new Dictionary<string, string> { ["Pinned"] = "1" },
            currentDefaults: new Dictionary<string, string> { ["Count"] = "3", ["Pinned"] = "2", ["Same"] = "x" },
            upgraded: new Dictionary<string, string> { ["Pinned"] = "1" },
            targetDefaults: new Dictionary<string, string> { ["Count"] = "5", ["Pinned"] = "9", ["Same"] = "x", ["New"] = "7" });

        Assert.Equal(["Count"], changed);
    }

    [Fact]
    public void Setting_or_resetting_a_parameter_counts_only_when_its_value_moves()
    {
        var current = new Dictionary<string, string> { ["Size"] = "3", ["Mode"] = "fast" };
        var defaults = new Dictionary<string, string> { ["Size"] = "1", ["Mode"] = "fast", ["Name"] = "a" };

        // Size goes back to its default (1), Mode is reset to a default equal to its value, Name is set to what it was.
        var changed = DefaultServices.ChangedParameters(current, defaults, new Dictionary<string, string> { ["Name"] = "a" }, defaults);

        Assert.Equal(["Size"], changed);
    }

    [Fact]
    public void Service_groups_count_as_default_services()
    {
        var manifest = """
            <ApplicationManifest xmlns="http://schemas.microsoft.com/2011/01/fabric">
              <DefaultServices>
                <ServiceGroup Name="Cart">
                  <StatefulServiceGroup ServiceTypeName="CartType" TargetReplicaSetSize="[Cart_Replicas]" MinReplicaSetSize="3">
                    <UniformInt64Partition PartitionCount="[Cart_Partitions]" LowKey="0" HighKey="9" />
                    <Members><Member ServiceTypeName="ItemsType" Name="Items" /></Members>
                  </StatefulServiceGroup>
                </ServiceGroup>
              </DefaultServices>
            </ApplicationManifest>
            """;

        Assert.Equal(
            [
                new DefaultServiceUse("Cart_Replicas", "Cart", "TargetReplicaSetSize", false),
                new DefaultServiceUse("Cart_Partitions", "Cart", "PartitionCount", true),
            ],
            DefaultServices.ParameterUses(manifest));
    }
}
