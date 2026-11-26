using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Web;

namespace Sfx.Tests;

public class SettleCheckTests
{
    private const string Running = SettleChecks.Running;
    private const string Done = SettleChecks.Done;
    private const string Failed = SettleChecks.Failed;

    [Fact]
    public void A_secondary_is_moving_while_it_is_still_on_its_node()
    {
        var replicas = new List<(string, string, string)> { ("1", "_Node_0", "Primary"), ("2", "_Node_1", "ActiveSecondary") };

        Assert.Equal((Running, "Still on _Node_1."), SettleChecks.ReplicaMove("ActiveSecondary", "2", "_Node_1", null, replicas));
    }

    [Fact]
    public void A_secondary_has_moved_once_it_is_off_its_node()
    {
        // The replacement gets a new replica id.
        var replicas = new List<(string, string, string)> { ("1", "_Node_0", "Primary"), ("7", "_Node_3", "ActiveSecondary") };

        Assert.Equal((Done, "Moved off _Node_1."), SettleChecks.ReplicaMove("ActiveSecondary", "2", "_Node_1", null, replicas));
        Assert.Equal((Done, "On _Node_3."), SettleChecks.ReplicaMove("ActiveSecondary", "2", "_Node_1", "_Node_3", replicas));
    }

    [Fact]
    public void A_move_to_a_node_waits_for_that_node()
    {
        var replicas = new List<(string, string, string)> { ("1", "_Node_0", "Primary") };

        Assert.Equal((Running, "Not on _Node_3 yet."), SettleChecks.ReplicaMove("Instance", "2", "_Node_1", "_Node_3", replicas));
    }

    [Fact]
    public void A_primary_move_follows_the_role()
    {
        var before = new List<(string, string, string)> { ("1", "_Node_0", "Primary"), ("2", "_Node_1", "ActiveSecondary") };
        var after = new List<(string, string, string)> { ("1", "_Node_0", "ActiveSecondary"), ("2", "_Node_1", "Primary") };
        var none = new List<(string, string, string)> { ("1", "_Node_0", "ActiveSecondary") };

        Assert.Equal((Running, "Primary still on _Node_0."), SettleChecks.ReplicaMove("Primary", "1", "_Node_0", null, before));
        Assert.Equal((Done, "Primary on _Node_1."), SettleChecks.ReplicaMove("Primary", "1", "_Node_0", null, after));
        Assert.Equal((Done, "Primary on _Node_1."), SettleChecks.ReplicaMove("Primary", "1", "_Node_0", "_Node_1", after));
        Assert.Equal((Running, "Primary still on _Node_1."), SettleChecks.ReplicaMove("Primary", "1", "_Node_0", "_Node_2", after));
        Assert.Equal((Running, "Waiting for a new primary."), SettleChecks.ReplicaMove("Primary", "1", "_Node_0", null, none));
    }

    [Fact]
    public void Scaling_counts_partitions_at_the_requested_size()
    {
        var partitions = new List<(string, int)> { ("Ready", 5), ("Ready", 5), ("Ready", 3) };

        Assert.Equal((Running, "2 of 3 partitions at 5 replicas."), SettleChecks.ScaleProgress(5, partitions));
        Assert.Equal((Running, "2 of 3 partitions at 5 instances."), SettleChecks.ScaleProgress(5, partitions, stateful: false));
    }

    [Fact]
    public void Scaling_is_done_when_every_ready_partition_has_the_count()
    {
        Assert.Equal((Done, "2 of 2 partitions at 3 replicas."), SettleChecks.ScaleProgress(3, [("Ready", 3), ("Ready", 3)]));
        Assert.Equal(Running, SettleChecks.ScaleProgress(3, [("Ready", 3), ("Reconfiguring", 3)]).State);
        Assert.Equal(Running, SettleChecks.ScaleProgress(3, []).State);
    }

    [Fact]
    public void Scaling_to_every_node_is_done_at_once()
    {
        Assert.Equal((Done, "An instance on every node."), SettleChecks.ScaleProgress(-1, [("Ready", 2)], stateful: false));
    }

    [Theory]
    [InlineData("Disabled", "Disabled", Done, "Node is Disabled.")]
    [InlineData("Disabled", "Disabling", Running, "Node is Disabling.")]
    [InlineData("Up", "Enabling", Running, "Node is Enabling.")]
    [InlineData("Down", "", Running, "Node is Unknown.")]
    public void A_node_is_done_at_the_expected_status(string expected, string actual, string state, string detail)
    {
        Assert.Equal((state, detail), SettleChecks.NodeStatus(expected, actual));
    }

    [Fact]
    public void A_restart_is_done_when_the_node_came_up_since()
    {
        var since = TimeSpan.FromMinutes(2);

        Assert.Equal((Done, "Node is Up again."), SettleChecks.NodeRestart("Up", TimeSpan.FromSeconds(30), since));
        Assert.Equal((Running, "Waiting for the node to restart."), SettleChecks.NodeRestart("Up", TimeSpan.FromDays(3), since));
        Assert.Equal((Running, "Waiting for the node to restart."), SettleChecks.NodeRestart("Up", null, since));
        Assert.Equal((Running, "Node is Down."), SettleChecks.NodeRestart("Down", null, since));
    }

    [Fact]
    public void An_upgrade_is_this_one_when_it_started_after_the_request()
    {
        var requested = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(SettleChecks.IsThisUpgrade(requested.AddSeconds(1), requested));
        Assert.True(SettleChecks.IsThisUpgrade(requested.AddSeconds(-30), requested));
        Assert.False(SettleChecks.IsThisUpgrade(requested.AddHours(-1), requested));
        Assert.False(SettleChecks.IsThisUpgrade(null, requested));
    }

    [Theory]
    [InlineData("RollingForwardCompleted", "None", Done, "Upgrade completed.")]
    [InlineData("RollingBackCompleted", "HealthCheck", Failed, "Rolled back: HealthCheck.")]
    [InlineData("RollingBackCompleted", "None", Failed, "Rolled back.")]
    [InlineData("Failed", "UpgradeDomainTimeout", Failed, "Failed: UpgradeDomainTimeout.")]
    [InlineData("Failed", "", Failed, "Failed.")]
    [InlineData("RollingBackInProgress", "HealthCheck", Running, "Rolling back: HealthCheck.")]
    [InlineData("RollingForwardPending", "", Running, "Waiting to be resumed into UD 2.")]
    [InlineData("RollingForwardInProgress", "", Running, "UD 1, 1 of 3 domains done.")]
    public void An_upgrade_settles_when_it_completes_or_fails(string state, string reason, string expected, string detail)
    {
        Assert.Equal((expected, detail), SettleChecks.UpgradeOutcome(state, reason, "1", "2", 1, 3));
    }

    [Fact]
    public void An_upgrade_between_domains_says_so()
    {
        Assert.Equal((Running, "UD -, 0 of 3 domains done."), SettleChecks.UpgradeOutcome("RollingForwardInProgress", "", "", "0", 0, 3));
    }

    [Theory]
    [InlineData("RollingBackCompleted", "", "", Done, "Rolled back.")]
    [InlineData("RollingBackInProgress", "", "1", Running, "Rolling back, UD 1.")]
    [InlineData("RollingBackInProgress", "", "", Running, "Rolling back.")]
    [InlineData("RollingForwardCompleted", "", "", Failed, "The upgrade completed before it could roll back.")]
    [InlineData("Failed", "Interrupted", "", Failed, "Failed: Interrupted.")]
    public void A_rollback_settles_when_it_is_back(string state, string reason, string domain, string expected, string detail)
    {
        Assert.Equal((expected, detail), SettleChecks.RollbackOutcome(state, reason, domain));
    }
}

public class OperationTrackingTests
{
    private static UiSession Session()
    {
        var context = new ClusterContext("test", "localhost", [], new AuthOptions(), false, false);
        var cluster = new ClusterSession(context, new Uri("http://localhost:19080"), new ClusterQueries(null!));
        return new UiSession(cluster, new ActionLog(Path.Combine(Path.GetTempPath(), $"sfx-test-{Guid.NewGuid():N}.log")), "0.0.0");
    }

    private static Args Empty => new(new Dictionary<string, string>());

    [Fact]
    public void Actions_with_nothing_to_wait_for_are_done_when_requested()
    {
        var session = Session();

        var operation = session.Track("partition.reset-load", "Reset the load of partition 1", Empty, Empty);

        Assert.Equal(SettleChecks.Done, operation.State);
        Assert.Equal("Requested.", operation.Detail);
        Assert.Equal(operation.Started, operation.Finished);
        Assert.Equal([operation], session.GetOperations());
    }

    [Fact]
    public void Keeps_the_last_50_newest_first()
    {
        var session = Session();

        for (var i = 0; i < 55; i++)
        {
            session.Track("partition.reset-load", $"Reset {i}", Empty, Empty);
        }

        var operations = session.GetOperations();
        Assert.Equal(50, operations.Count);
        Assert.Equal("Reset 54", operations[0].Title);
        Assert.Equal("Reset 5", operations[^1].Title);
    }

    [Fact]
    public void Settled_operations_count_as_succeeded_in_the_log()
    {
        Assert.True(new ActionLogEntry(DateTimeOffset.Now, "u", "c", "e", "a", "done").Succeeded);
        Assert.False(new ActionLogEntry(DateTimeOffset.Now, "u", "c", "e", "a", "failed: Rolled back.").Succeeded);
    }
}
