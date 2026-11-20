using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Web;

namespace Sfx.Tests;

public class WebActionTests
{
    // The checks under test all run before anything reaches the cluster, so no client is needed.
    private static UiSession Session(bool readOnly = false, bool power = false)
    {
        var context = new ClusterContext("test", "localhost", [], new AuthOptions(), readOnly, power);
        var cluster = new ClusterSession(context, new Uri("http://localhost:19080"), new ClusterQueries(null!));
        return new UiSession(cluster, new ActionLog(Path.Combine(Path.GetTempPath(), $"sfx-test-{Guid.NewGuid():N}.log")), "0.0.0");
    }

    private static Dictionary<string, string> UpNode => new() { ["node"] = "_Node_1", ["status"] = "Up" };

    [Fact]
    public void Read_only_sessions_get_no_actions()
    {
        Assert.Empty(WebActions.For(Session(readOnly: true), "node", UpNode));
    }

    [Fact]
    public void Power_actions_need_power()
    {
        var down = new Dictionary<string, string> { ["node"] = "_Node_1", ["status"] = "Down" };

        Assert.DoesNotContain(WebActions.For(Session(), "node", down), a => a.Id == "node.remove-state");
        Assert.Contains(WebActions.For(Session(power: true), "node", down), a => a.Id == "node.remove-state");
    }

    [Fact]
    public void Offers_only_what_applies()
    {
        var ids = WebActions.For(Session(), "node", UpNode).Select(a => a.Id).ToList();

        Assert.Contains("node.deactivate", ids);
        Assert.DoesNotContain("node.activate", ids);
    }

    [Fact]
    public async Task Refuses_a_target_it_did_not_offer()
    {
        var session = Session();
        var action = WebActions.For(session, "node", UpNode).Single(a => a.Id == "node.deactivate");
        var forged = new Dictionary<string, string>(action.Target) { ["node"] = "_Node_2" };

        var ex = await Assert.ThrowsAsync<ActionException>(() =>
            WebActions.RunAsync(session, action.Id, new ActionRequest(forged, new() { ["intent"] = "Pause" }, null, action.Signature), default));
        Assert.Equal(403, ex.Status);
    }

    [Fact]
    public async Task Refuses_a_choice_it_did_not_offer()
    {
        // RemoveData is its own destructive action; it mustn't be reachable through the plain deactivate.
        var session = Session();
        var action = WebActions.For(session, "node", UpNode).Single(a => a.Id == "node.deactivate");

        var ex = await Assert.ThrowsAsync<ActionException>(() =>
            WebActions.RunAsync(session, action.Id, new ActionRequest(new(action.Target), new() { ["intent"] = "RemoveData" }, null, action.Signature), default));
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public async Task Destructive_actions_need_the_name_typed()
    {
        var session = Session();
        var action = WebActions.For(session, "node", UpNode).Single(a => a.Id == "node.restart");

        Assert.Equal("_Node_1", action.ConfirmText);
        var ex = await Assert.ThrowsAsync<ActionException>(() =>
            WebActions.RunAsync(session, action.Id, new ActionRequest(new(action.Target), null, "_Node_", action.Signature), default));
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public async Task Power_can_be_turned_off_under_an_offered_action()
    {
        var session = Session(power: true);
        var down = new Dictionary<string, string> { ["node"] = "_Node_1", ["status"] = "Down" };
        var action = WebActions.For(session, "node", down).Single(a => a.Id == "node.remove-state");
        session.Power = false;

        var ex = await Assert.ThrowsAsync<ActionException>(() =>
            WebActions.RunAsync(session, action.Id, new ActionRequest(new(action.Target), null, "_Node_1", action.Signature), default));
        Assert.Equal(403, ex.Status);
    }

    [Fact]
    public async Task Fault_analysis_actions_need_the_service()
    {
        var session = Session(power: true);
        var up = new Dictionary<string, string> { ["node"] = "_Node_1", ["status"] = "Up" };
        var stop = WebActions.For(session, "node", up).Single(a => a.Id == "node.stop");

        session.KnowSystemServices(new SystemServices(EventStore: false, RepairManager: false, FaultAnalysis: false));

        Assert.DoesNotContain(WebActions.For(session, "node", up), a => a.Id == "node.stop");
        var ex = await Assert.ThrowsAsync<ActionException>(() =>
            WebActions.RunAsync(session, stop.Id, new ActionRequest(new(stop.Target), new() { ["seconds"] = "60" }, "_Node_1", stop.Signature), default));
        Assert.Equal(409, ex.Status);
    }
}

public class DefaultServicesTests
{
    private const string Manifest = """
        <ApplicationManifest xmlns="http://schemas.microsoft.com/2011/01/fabric" ApplicationTypeName="DemoAppType" ApplicationTypeVersion="1.0.0">
          <Parameters>
            <Parameter Name="Counter_TargetReplicaSetSize" DefaultValue="3" />
            <Parameter Name="Counter_PartitionCount" DefaultValue="1" />
            <Parameter Name="Frontend_InstanceCount" DefaultValue="-1" />
            <Parameter Name="Shard" DefaultValue="a" />
            <Parameter Name="Unused" DefaultValue="x" />
          </Parameters>
          <DefaultServices>
            <Service Name="Counter">
              <StatefulService ServiceTypeName="CounterType" TargetReplicaSetSize="[Counter_TargetReplicaSetSize]" MinReplicaSetSize="2">
                <UniformInt64Partition PartitionCount="[Counter_PartitionCount]" LowKey="0" HighKey="1023" />
              </StatefulService>
            </Service>
            <Service Name="Frontend">
              <StatelessService ServiceTypeName="FrontendType" InstanceCount="[Frontend_InstanceCount]">
                <NamedPartition><Partition Name="[Shard]" /></NamedPartition>
              </StatelessService>
            </Service>
          </DefaultServices>
        </ApplicationManifest>
        """;

    [Fact]
    public void Finds_which_parameters_default_services_use()
    {
        var uses = DefaultServices.ParameterUses(Manifest);

        Assert.Contains(new DefaultServiceUse("Counter_TargetReplicaSetSize", "Counter", "TargetReplicaSetSize", false), uses);
        Assert.Contains(new DefaultServiceUse("Frontend_InstanceCount", "Frontend", "InstanceCount", false), uses);
        Assert.DoesNotContain(uses, u => u.Parameter == "Unused");
    }

    [Fact]
    public void Marks_parameters_that_set_partitioning()
    {
        var uses = DefaultServices.ParameterUses(Manifest);

        Assert.Contains(new DefaultServiceUse("Counter_PartitionCount", "Counter", "PartitionCount", true), uses);
        Assert.Contains(new DefaultServiceUse("Shard", "Frontend", "Name", true), uses);
    }

    [Theory]
    [InlineData("""<Parameter Name="EnableDefaultServicesUpgrade" Value="true" />""", true)]
    [InlineData("""<Parameter Name="EnableDefaultServicesUpgrade" Value="false" />""", false)]
    [InlineData("", false)]
    public void Reads_whether_upgrades_may_change_default_services(string parameter, bool enabled)
    {
        var manifest = $"""
            <ClusterManifest xmlns="http://schemas.microsoft.com/2011/01/fabric">
              <FabricSettings><Section Name="ClusterManager">{parameter}</Section></FabricSettings>
            </ClusterManifest>
            """;

        Assert.Equal(enabled, DefaultServices.UpgradeEnabled(manifest));
    }

    [Fact]
    public void Recognizes_the_clusters_refusal()
    {
        Assert.True(DefaultServices.IsRefusal(new InvalidOperationException("aka.ms/upgrade-defaultservices")));
        Assert.False(DefaultServices.IsRefusal(new InvalidOperationException("timeout")));
    }
}
