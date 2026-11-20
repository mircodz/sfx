using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Web;

namespace Sfx.Tests;

public class CodePackageActionTests
{
    private static UiSession Session(bool readOnly = false)
    {
        var context = new ClusterContext("test", "localhost", [], new AuthOptions(), readOnly, false);
        var cluster = new ClusterSession(context, new Uri("http://localhost:19080"), new ClusterQueries(null!));
        return new UiSession(cluster, new ActionLog(Path.Combine(Path.GetTempPath(), $"sfx-test-{Guid.NewGuid():N}.log")), "0.0.0");
    }

    private static Dictionary<string, string> CodePackage(string instanceId) => new()
    {
        ["node"] = "_Node_1", ["applicationId"] = "orders", ["application"] = "fabric:/orders", ["servicePackage"] = "CounterPkg",
        ["codePackage"] = "Code", ["instanceId"] = instanceId, ["activationId"] = "",
    };

    [Fact]
    public void Offers_restart_for_a_running_code_package()
    {
        var action = Assert.Single(WebActions.For(Session(), "codepackage", CodePackage("133000000000000000")));

        Assert.Equal("codepackage.restart", action.Id);
        Assert.Equal("confirm", action.Risk);
        Assert.Equal("Its process is killed and started again; replicas in it fail over.", action.Warning);
    }

    [Fact]
    public void Offers_nothing_without_an_instance_id()
    {
        Assert.Empty(WebActions.For(Session(), "codepackage", CodePackage("")));
    }

    [Fact]
    public void Read_only_sessions_get_no_restart()
    {
        Assert.Empty(WebActions.For(Session(readOnly: true), "codepackage", CodePackage("133000000000000000")));
    }

    [Fact]
    public async Task Refuses_to_run_without_an_instance_id()
    {
        var session = Session();
        var target = CodePackage("");

        var ex = await Assert.ThrowsAsync<ActionException>(() =>
            WebActions.RunAsync(session, "codepackage.restart", new ActionRequest(target, null, null, session.Sign("codepackage.restart", target)), default));
        Assert.Equal(409, ex.Status);
    }
}
