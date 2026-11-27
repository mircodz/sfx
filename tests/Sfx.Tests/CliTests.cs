using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ServiceFabric.Common;
using Microsoft.ServiceFabric.Common.Exceptions;
using Sfx.Cli;
using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Tui;
using Sfx.Web;
using StoreFile = Microsoft.ServiceFabric.Common.FileInfo;

namespace Sfx.Tests;

/// <summary>sfx get, describe, health, actions and store, over a fake cluster.</summary>
public sealed class CliTests : IDisposable
{
    private readonly string _scratch = Path.Combine(AppContext.BaseDirectory, "scratch", $"cli-{Guid.NewGuid():N}");

    public CliTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private static readonly DateTime Modified = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static readonly HealthEvent Disk = new(
        sourceId: "System.Disk",
        property: "Space",
        healthState: HealthState.Warning,
        description: "Disk is 95% full.",
        isExpired: false,
        lastModifiedUtcTimestamp: Modified);

    private static readonly ApplicationInfo Orders =
        new("orders", new ApplicationName("fabric:/orders"), "OrdersType", "1.0.0", ApplicationStatus.Ready, healthState: HealthState.Ok);

    private static readonly ApplicationInfo ShopOrders =
        new("shop~orders", new ApplicationName("fabric:/shop/orders"), "OrdersType", "1.0.0", ApplicationStatus.Ready, healthState: HealthState.Error);

    private static readonly Dictionary<string, ServiceInfo[]> Services = new()
    {
        ["orders"] =
        [
            new StatefulServiceInfo(
                "orders~Counter",
                new ServiceName("fabric:/orders/Counter"),
                "CounterType",
                healthState: HealthState.Ok,
                serviceStatus: ServiceStatus.Active),
        ],
        ["shop~orders"] =
        [
            new StatefulServiceInfo(
                "shop~orders~Counter",
                new ServiceName("fabric:/shop/orders/Counter"),
                "CounterType",
                healthState: HealthState.Ok,
                serviceStatus: ServiceStatus.Active),
        ],
        ["System"] = [],
    };

    private static FakeCluster Cluster() => new FakeCluster()
        .Paged(
            "GetNodeInfoListAsync",
            new NodeInfo(
                name: new NodeName("_Node_0"),
                ipAddressOrFQDN: "10.0.0.4",
                type: "FrontEnd",
                codeVersion: "10.1",
                nodeStatus: NodeStatus.Up,
                healthState: HealthState.Ok,
                isSeedNode: true,
                upgradeDomain: "0",
                faultDomain: "fd:/0"),
            new NodeInfo(
                name: new NodeName("_Node_1"),
                ipAddressOrFQDN: "10.0.0.5",
                type: "FrontEnd",
                codeVersion: "10.1",
                nodeStatus: NodeStatus.Up,
                healthState: HealthState.Warning,
                upgradeDomain: "1",
                faultDomain: "fd:/1"))
        .On("GetNodeHealthAsync", new NodeHealth(HealthState.Warning, [Disk]))
        .Paged("GetApplicationInfoListAsync", Orders, ShopOrders)
        .On("GetApplicationInfoAsync", args => (string)args[0]! switch
        {
            "orders" => Orders,
            "shop~orders" => ShopOrders,
            _ => null,
        })
        .On("GetApplicationHealthAsync", new ApplicationHealth(HealthState.Ok))
        .On("GetServiceInfoListAsync", args => new PagedData<ServiceInfo>(new ContinuationToken(""), Services.GetValueOrDefault((string)args[0]!, [])))
        .On("GetServiceInfoAsync", args => Services.GetValueOrDefault((string)args[0]!, []).FirstOrDefault(s => s.Id == (string)args[1]!))
        .On("GetApplicationNameInfoAsync", args => (string)args[0]! switch
        {
            "orders~Counter" => new ApplicationNameInfo("orders", new ApplicationName("fabric:/orders")),
            "shop~orders~Counter" => new ApplicationNameInfo("shop~orders", new ApplicationName("fabric:/shop/orders")),
            _ => throw new ServiceFabricException("no such service", FabricErrorCodes.FABRIC_E_SERVICE_DOES_NOT_EXIST, false),
        });

    private UiSession Session(FakeCluster cluster, bool readOnly = false, bool power = false)
    {
        var context = new ClusterContext("test", "localhost", [], new AuthOptions(), readOnly, power);
        var session = new ClusterSession(context, new Uri("http://localhost:19080"), new ClusterQueries(cluster.Client));
        var ui = new UiSession(session, new ActionLog(Path.Combine(_scratch, "actions.log")), "0.0.0");
        ui.KnowSystemServices(new SystemServices(EventStore: false, RepairManager: false, FaultAnalysis: true));
        return ui;
    }

    private sealed record Result(int Code, string Out, string Error, bool Connected);

    private static async Task<Result> Run(UiSession? session, params string[] args)
    {
        CliOptions options;
        IReadOnlyList<string> rest;
        try
        {
            (options, rest) = CliOptions.Split(args);
        }
        catch (CliFailure failure)
        {
            return new Result(failure.Code, "", $"sfx: {failure.Message}\n", false);
        }
        using var output = new StringWriter();
        using var error = new StringWriter();
        var connected = false;
        var code = await Scripting.RunAsync(
            rest,
            options,
            help: false,
            _ =>
            {
                connected = true;
                return Task.FromResult(session ?? throw new HttpRequestException("Connection refused"));
            },
            new CliConsole(output, error, Color: false),
            default);
        return new Result(code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"), connected);
    }

    private Task<Result> Sfx(params string[] args) => Run(Session(Cluster()), args);

    /// <summary>As the web server writes it, without the signatures the CLI leaves out.</summary>
    private static string Unsigned<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, ApiReads.Json)!;
        Strip(node);
        return node.ToJsonString(ApiReads.Json);

        static void Strip(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    o.Remove("signature");
                    o.Select(p => p.Value).ToList().ForEach(Strip);
                    break;
                case JsonArray a:
                    a.ToList().ForEach(Strip);
                    break;
            }
        }
    }

    private static string Compact(string json) => JsonNode.Parse(json)!.ToJsonString(ApiReads.Json);

    [Fact]
    public async Task Get_nodes_prints_aligned_columns()
    {
        var result = await Sfx("get", "nodes");

        Assert.Equal(0, result.Code);
        Assert.Equal(
            """
            NAME      STATUS   HEALTH    TYPE       ADDRESS    UD   FD
            _Node_0   Up       Healthy   FrontEnd   10.0.0.4   0    fd:/0
            _Node_1   Up       Warning   FrontEnd   10.0.0.5   1    fd:/1

            """,
            result.Out);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task Get_nodes_wide_adds_columns_and_no_headers_drops_the_header()
    {
        var result = await Sfx("get", "node", "-o", "wide", "--no-headers", "--health", "warning");

        Assert.Equal("_Node_1   Up   Warning   FrontEnd   10.0.0.5   1   fd:/1   no   10.1   -   Disk is 95% full.\n", result.Out);
    }

    [Fact]
    public async Task Get_json_is_what_the_web_api_returns_without_signatures()
    {
        var session = Session(Cluster());

        var nodes = await Run(session, "get", "nodes", "-o", "json");
        var apps = await Run(session, "get", "applications", "-o", "json");
        var services = await Run(session, "get", "services", "fabric:/orders", "-o", "json");

        Assert.Equal(Unsigned(await ApiReads.NodesAsync(session, default)), Compact(nodes.Out));
        Assert.Contains("\"actions\": [", nodes.Out);
        Assert.DoesNotContain("signature", nodes.Out);
        Assert.Equal(Unsigned(await ApiReads.ApplicationsAsync(session, default)), Compact(apps.Out));
        Assert.Equal(Unsigned(await ApiReads.ServicesAsync(session, "orders", default)), Compact(services.Out));
    }

    [Fact]
    public async Task A_single_entity_is_an_object_in_json()
    {
        var session = Session(Cluster());

        var node = await Run(session, "get", "node", "_node_1", "-o", "json");

        Assert.Equal(Unsigned((await ApiReads.NodesAsync(session, default))[1]), Compact(node.Out));
    }

    [Fact]
    public async Task The_web_route_answers_the_same_json_as_the_shared_read()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        const string token = "0123456789abcdef0123456789abcdef";
        var cluster = Cluster();
        var context = new ClusterContext("test", "localhost", [], new AuthOptions());
        var session = new ClusterSession(context, new Uri("http://localhost:19080"), new ClusterQueries(cluster.Client));
        var store = ContextStore.Load(Path.Combine(_scratch, "config.toml"));
        var host = new UiHost(session, store, new ClusterConnector(BuiltInAuth.Providers), new ActionLog(Path.Combine(_scratch, "actions.log")), "0.0.0");
        using var stop = new CancellationTokenSource();
        var server = WebServer.RunAsync(host, new WebOptions(port, OpenBrowser: false, Token: token), _ => { }, stop.Token);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        string body = "";
        for (var i = 0; i < 50; i++)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/api/nodes")
                {
                    Headers = { { WebServer.TokenHeader, token }, { WebServer.SessionHeader, host.Current.Id } },
                };
                body = await (await http.SendAsync(request)).Content.ReadAsStringAsync();
                break;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(100);
            }
        }

        Assert.Equal(JsonSerializer.Serialize(await ApiReads.NodesAsync(host.Current, default), ApiReads.Json), body);
        Assert.Equal(Unsigned(await ApiReads.NodesAsync(host.Current, default)), Compact((await Run(host.Current, "get", "nodes", "-o", "json")).Out));
        await stop.CancelAsync();
        await server;
    }

    [Fact]
    public async Task Name_output_is_what_sfx_takes_back()
    {
        Assert.Equal("node/_Node_0\nnode/_Node_1\n", (await Sfx("get", "nodes", "-o", "name")).Out);
        Assert.Equal("fabric:/shop/orders\n", (await Sfx("get", "apps", "-o", "name", "--filter", "shop")).Out);
        Assert.Equal("fabric:/orders/Counter\n", (await Sfx("get", "svc", "orders", "-o", "name")).Out);
    }

    [Fact]
    public async Task Short_names_resolve_and_case_is_forgiven()
    {
        var exact = await Sfx("get", "services", "orders/Counter", "-o", "name");
        var app = await Sfx("get", "app", "ORDERS", "-o", "name");

        Assert.Equal((0, "fabric:/orders/Counter\n"), (exact.Code, exact.Out));
        Assert.Equal((0, "fabric:/orders\n"), (app.Code, app.Out));
    }

    [Fact]
    public async Task An_ambiguous_short_name_is_a_usage_error_listing_the_matches()
    {
        var result = await Sfx("get", "services", "Counter");

        Assert.Equal(2, result.Code);
        Assert.Equal("sfx: 'Counter' could be any of fabric:/orders/Counter, fabric:/shop/orders/Counter; give the full name.\n", result.Error);
        Assert.Empty(result.Out);
    }

    [Fact]
    public async Task A_missing_entity_exits_1()
    {
        var service = await Sfx("get", "services", "fabric:/nothing");
        var node = await Sfx("get", "nodes", "_Node_9");

        Assert.Equal((1, "sfx: There's no application or service fabric:/nothing.\n"), (service.Code, service.Error));
        Assert.Equal((1, "sfx: There's no node _Node_9 on this cluster.\n"), (node.Code, node.Error));
    }

    [Fact]
    public async Task A_node_name_where_an_application_is_expected_gets_a_hint()
    {
        var result = await Sfx("get", "services", "_Node_0");

        Assert.Equal(1, result.Code);
        Assert.Equal("sfx: There's no application or service fabric:/_Node_0. For the node, write node/_Node_0.\n", result.Error);
    }

    [Fact]
    public async Task Usage_errors_exit_2_before_connecting()
    {
        foreach (string[] args in (string[][])
            [
                ["get"],
                ["get", "widgets"],
                ["get", "nodes", "-o", "yaml"],
                ["get", "services"],
                ["get", "types", "--health", "error"],
                ["get", "nodes", "--health", "purple"],
                ["describe"],
                ["describe", "store:/x"],
                ["health", "type/OrdersType"],
                ["store", "ls", "-o", "wide"],
                ["store", "rm"],
                ["actions", "cluster", "--filter", "x"],
                ["get", "nodes", "a", "b"],
            ])
        {
            var result = await Run(null, args);
            Assert.True(result.Code == 2, $"{string.Join(' ', args)}: {result.Code} {result.Error}");
            Assert.False(result.Connected, string.Join(' ', args));
            Assert.Empty(result.Out);
        }
    }

    [Fact]
    public async Task Errors_go_to_stderr_as_one_line_or_as_json()
    {
        var plain = await Run(null, "get", "nodes");
        var json = await Run(null, "get", "nodes", "-o", "json");

        Assert.Equal((5, "sfx: could not connect: Connection refused\n"), (plain.Code, plain.Error));
        Assert.Equal((5, "{\"error\":\"could not connect: Connection refused\"}\n"), (json.Code, json.Error));
        Assert.Empty(json.Out);
    }

    [Fact]
    public void Usage_errors_found_by_the_host_follow_the_output_format()
    {
        using var plain = new StringWriter();
        using var json = new StringWriter();

        Assert.Equal(2, Scripting.Fail(["get", "nodes", "--port", "x"], "--port expects a port number, got 'x'.", plain));
        Assert.Equal(2, Scripting.Fail(["get", "nodes", "-ojson", "--port", "x"], "--port expects a port number, got 'x'.", json));

        Assert.Equal("sfx: --port expects a port number, got 'x'.\n", plain.ToString().ReplaceLineEndings("\n"));
        Assert.Equal("{\"error\":\"--port expects a port number, got 'x'.\"}\n", json.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task A_failed_cluster_call_exits_1()
    {
        var cluster = Cluster().Throws("GetNodeInfoListAsync", new ServiceFabricException("The cluster is busy.", FabricErrorCodes.E_FAIL, false));

        var result = await Run(Session(cluster), "get", "nodes");

        Assert.Equal((1, "sfx: The cluster is busy.\n"), (result.Code, result.Error));
    }

    [Fact]
    public async Task Describe_shows_the_page_as_text_and_json()
    {
        var cluster = Cluster()
            .On(
                "GetNodeLoadInfoAsync",
                new NodeLoadInfo(new NodeName("_Node_1"), [new NodeLoadMetricInformation("Memory", nodeCapacity: "100", nodeLoad: "40")]))
            .On("GetClusterManifestAsync", new ClusterManifest("<ClusterManifest />"))
            .Paged<DeployedApplicationInfo>("GetDeployedApplicationInfoListAsync");
        var session = Session(cluster);

        var text = await Run(session, "describe", "node/_Node_1");
        var json = await Run(session, "describe", "node/_Node_1", "-o", "json");

        Assert.Equal(0, text.Code);
        Assert.Equal(
            """
            Name:            _Node_1
            Status:          Up
            Health:          Warning
            Type:            FrontEnd
            Address:         10.0.0.5
            Upgrade domain:  1
            Fault domain:    fd:/1
            Seed:            no
            Version:         10.1

            Why it's unhealthy
              Warning  System.Disk Space: Disk is 95% full.
              See sfx health node/_Node_1 for more.

            Load (1)
              METRIC   LOAD   CAPACITY
              Memory     40        100

            Replicas (0)
              APPLICATION   SERVICE   PARTITION   REPLICA   ROLE   STATUS

            """,
            text.Out);
        Assert.Equal(Unsigned(await ApiReads.NodeAsync(session, "_Node_1", default)), Compact(json.Out));
    }

    [Fact]
    public async Task Health_prints_the_evaluation_chain_down_to_the_events()
    {
        var node = new NodeHealthEvaluation(
            HealthState.Warning,
            "Node '_Node_1' is Warning.",
            "_Node_1",
            [new HealthEvaluationWrapper(new EventHealthEvaluation(HealthState.Warning, "Disk is 95% full.", false, Disk))]);
        var cluster = Cluster().On(
            "GetClusterHealthAsync",
            new ClusterHealth(
                HealthState.Warning,
                [],
                [
                    new HealthEvaluationWrapper(
                        new NodesHealthEvaluation(HealthState.Warning, "1 of 2 nodes is unhealthy.", 0, 2, [new HealthEvaluationWrapper(node)])),
                ]));
        var session = Session(cluster);

        var text = await Run(session, "health", "cluster");
        var json = await Run(session, "health", "cluster", "-o", "json");

        Assert.Equal(
            """
            State  Warning

            Why it's unhealthy
              Warning  Nodes
                1 of 2 nodes is unhealthy.
                Warning  Node
                  Node '_Node_1' is Warning.
                  Warning  Event
                    Disk is 95% full.

            Events (0)

            """,
            text.Out);
        var health = JsonNode.Parse(json.Out)!;
        Assert.Equal("Warning", (string?)health["health"]);
        Assert.Equal([0, 1, 2], health["evaluations"]!.AsArray().Select(e => (int)e!["depth"]!));
    }

    [Fact]
    public async Task Health_lists_the_events_with_when_they_changed()
    {
        var result = await Sfx("health", "node/_Node_1");

        Assert.EndsWith(
            """
            Events (1)
              Warning  System.Disk  Space
                Disk is 95% full.
                updated 2026-01-02 03:04:05Z

            """,
            result.Out);
    }

    [Fact]
    public async Task Actions_lists_what_the_context_allows()
    {
        var plain = await Run(Session(Cluster()), "actions", "node/_Node_0");
        var power = await Run(Session(Cluster(), power: true), "actions", "node/_Node_0", "-o", "json");
        var readOnly = await Run(Session(Cluster(), readOnly: true), "actions", "node/_Node_0");

        Assert.Equal(0, plain.Code);
        Assert.StartsWith("ID", plain.Out);
        Assert.Contains("node.deactivate", plain.Out);
        Assert.DoesNotContain("node.stop", plain.Out);
        Assert.Contains("intent=", plain.Out);
        Assert.Equal("The riskiest actions need --power.\n", plain.Error);

        var actions = JsonNode.Parse(power.Out)!.AsArray();
        var stop = actions.Single(a => (string?)a!["id"] == "node.stop")!;
        Assert.True((bool)stop["power"]!);
        Assert.Null(stop["signature"]);
        Assert.Equal("_Node_0", (string?)stop["confirmText"]);
        Assert.False((bool)actions.Single(a => (string?)a!["id"] == "node.deactivate")!["power"]!);

        Assert.Equal("ID   TITLE   RISK   POWER   CONFIRM   ARGS\n", readOnly.Out);
        Assert.Equal("This context is read-only; it offers no actions.\n", readOnly.Error);
    }

    private static readonly Guid PartitionId = new("11111111-1111-1111-1111-111111111111");

    private static readonly StatefulServicePartitionInfo Partition = new(
        HealthState.Ok,
        ServicePartitionStatus.Ready,
        new SingletonPartitionInformation(new PartitionId(PartitionId)),
        targetReplicaSetSize: 3,
        minReplicaSetSize: 2);

    private static FakeCluster Partitioned() => Cluster()
        .On("GetServiceNameInfoAsync", new ServiceNameInfo("orders~Counter", new ServiceName("fabric:/orders/Counter")))
        .On("GetPartitionInfoAsync", Partition)
        .Paged<ServicePartitionInfo>("GetPartitionInfoListAsync", Partition)
        .Paged<ReplicaInfo>(
            "GetReplicaInfoListAsync",
            Replica(1, "_Node_0", ReplicaRole.Primary, HealthState.Ok),
            Replica(2, "_Node_1", ReplicaRole.ActiveSecondary, HealthState.Warning));

    private static StatefulServiceReplicaInfo Replica(long id, string node, ReplicaRole role, HealthState health) =>
        new(ReplicaStatus.Ready, health, new NodeName(node), $"addr{id}", replicaRole: role, replicaId: new ReplicaId(id));

    [Fact]
    public async Task Partitions_and_replicas_are_listed_per_service_or_cluster_wide()
    {
        var session = Session(Partitioned());

        var partitions = await Run(session, "get", "partitions", "orders/Counter");
        var replicas = await Run(session, "get", "replicas", "fabric:/orders/Counter", "--health", "warning", "-o", "name");
        var all = await Run(session, "get", "replicas", "-o", "name");
        var one = await Run(session, "get", "replica", $"replica/{PartitionId}/2", "-o", "json");
        var app = await Run(session, "get", "partitions", "orders");

        Assert.Equal(
            """
            ID                                     KIND       PARTITIONING   REPLICAS   STATUS   HEALTH
            11111111-1111-1111-1111-111111111111   Stateful   Singleton      3/2        Ready    Healthy

            """,
            partitions.Out);
        Assert.Equal($"replica/{PartitionId}/2\n", replicas.Out);
        Assert.Equal(4, all.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal("_Node_1", (string?)JsonNode.Parse(one.Out)!["node"]);
        Assert.Equal((2, "sfx: Partitions are listed per service, and fabric:/orders isn't one.\n"), (app.Code, app.Error));
    }

    private FakeCluster Store() => Cluster()
        .On("GetImageStoreConnectionStringAsync", "fabric:ImageStore")
        .Throws("GetImageStoreInfoAsync", new ServiceFabricException("not supported", FabricErrorCodes.E_INVALIDARG, false))
        .Paged("GetApplicationTypeInfoListAsync", new ApplicationTypeInfo("OrdersType", "1.0.0", status: ApplicationTypeStatus.Available))
        .On("GetImageStoreRootContentAsync", new ImageStoreContent(
            [new StoreFile("512", new FileVersion("1"), Modified, "notes.txt")],
            [new FolderInfo("OrdersType_1.0.0", "3"), new FolderInfo("Logs", "1")]))
        .On("GetImageStoreContentAsync", args => (string)args[0]! switch
        {
            "OrdersType_1.0.0" => new ImageStoreContent([new StoreFile("100", null, null, @"OrdersType_1.0.0\ApplicationManifest.xml")], []),
            "Logs" => new ImageStoreContent([new StoreFile("1", null, null, @"Logs\x.log")], []),
            _ => null,
        })
        .On("GetImageStoreFolderSizeAsync", args => (string)args[0]! switch
        {
            "OrdersType_1.0.0" => new FolderSizeInfo("OrdersType_1.0.0", "3145728"),
            _ => new FolderSizeInfo("Logs", "2048"),
        });

    [Fact]
    public async Task Store_ls_lists_folders_then_files()
    {
        var session = Session(Store());

        var names = await Run(session, "store", "ls");
        var json = await Run(session, "store", "ls", "store:/", "-o", "json");

        Assert.Equal((0, "Logs/\nOrdersType_1.0.0/\nnotes.txt\n"), (names.Code, names.Out));
        Assert.Equal(Unsigned(await ApiReads.StoreAsync(session, "", default)), Compact(json.Out));
    }

    [Fact]
    public async Task Store_ls_long_shows_sizes_and_packages()
    {
        var result = await Run(Session(Store()), "store", "ls", "-l");

        Assert.Equal(
            """
             SIZE   FILES   MODIFIED               NAME                PACKAGE
                -       1                          Logs/
                -       3                          OrdersType_1.0.0/   OrdersType@1.0.0 (Available)
            512 B           2026-01-02 03:04:05Z   notes.txt

            """,
            result.Out);
    }

    [Fact]
    public async Task Store_du_sorts_by_size_and_totals()
    {
        var result = await Run(Session(Store()), "store", "du");

        Assert.Equal(
            """
              SIZE   NAME
            3.0 MB   OrdersType_1.0.0/
            2.0 KB   Logs/
             512 B   notes.txt
            3.0 MB   total

            """,
            result.Out);
    }

    [Fact]
    public async Task Get_operations_lists_the_action_log()
    {
        var session = Session(Cluster());
        new ActionLog(Path.Combine(_scratch, "actions.log")).Append("test", "http://localhost:19080", "Restart node _Node_0", "Requested.");

        var result = await Run(session, "get", "operations", "-o", "wide");

        Assert.StartsWith("TIME                   RESULT   CONTEXT   ACTION                 USER", result.Out);
        Assert.Contains("   test      Restart node _Node_0   ", result.Out);
        Assert.Equal(Unsigned(ApiReads.Activity(session)), Compact((await Run(session, "get", "operations", "-o", "json")).Out));
    }

    [Fact]
    public void Colour_only_on_a_terminal_without_NO_COLOR()
    {
        Assert.True(CliConsole.UseColor(terminal: true, noColor: null));
        Assert.True(CliConsole.UseColor(terminal: true, noColor: ""));
        Assert.False(CliConsole.UseColor(terminal: true, noColor: "1"));
        Assert.False(CliConsole.UseColor(terminal: false, noColor: null));
    }

    [Fact]
    public async Task Only_the_health_column_is_coloured()
    {
        using var output = new StringWriter();
        var (options, rest) = CliOptions.Split(["get", "nodes"]);
        var console = new CliConsole(output, TextWriter.Null, Color: true);

        await Scripting.RunAsync(rest, options, false, _ => Task.FromResult(Session(Cluster())), console, default);

        var lines = output.ToString().ReplaceLineEndings("\n").Split('\n');
        Assert.DoesNotContain('\e', lines[0]);
        Assert.Contains("_Node_0   Up       \e[32mHealthy\e[0m   FrontEnd", lines[1]);
        Assert.Contains("\e[33mWarning\e[0m", lines[2]);
        var plain = output.ToString().Replace("\e[32m", "").Replace("\e[33m", "").Replace("\e[0m", "");
        Assert.Equal((await Sfx("get", "nodes")).Out, plain.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Splits_the_scripting_options_from_the_rest()
    {
        var (options, rest) = CliOptions.Split(
            ["get", "-c", "prod", "nodes", "-ojson", "--filter=web", "--health", "warning,error", "--no-headers", "--readonly"]);

        Assert.Equal(["get", "-c", "prod", "nodes", "--readonly"], rest);
        Assert.Equal("json", options.Output);
        Assert.Equal("web", options.Filter);
        Assert.Equal(["Warning", "Error"], options.Health!.Order(StringComparer.Ordinal).Reverse());
        Assert.True(options.NoHeaders);
        Assert.Equal("-o", options.Given);
        Assert.Equal(2, Assert.Throws<CliFailure>(() => CliOptions.Split(["get", "nodes", "-o"])).Code);
    }

    [Fact]
    public async Task Never_picks_a_context_and_says_how_to_choose_one()
    {
        var store = ContextStore.Load(Path.Combine(_scratch, "config.toml"));
        store.Set(new ClusterContext("prod", "http://prod:19080", [], new AuthOptions()));
        store.Save();
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (cli, rest) = CliOptions.Split(["get", "nodes"]);
        var options = CommandLine.Parse(rest, flags);
        using var error = new StringWriter();

        var code = await SfxHost.RunScriptingAsync(
            options,
            cli,
            () => ContextStore.Load(store.Path),
            new ClusterConnector(BuiltInAuth.Providers),
            new CliConsole(TextWriter.Null, error, false),
            default);

        Assert.Null(store.Active);
        Assert.Equal(2, code);
        Assert.Equal(
            "sfx: No cluster given and no active context. Pass -c <context> or --endpoint <endpoint>, or run 'sfx context add'.\n",
            error.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Checks_the_arguments_before_loading_contexts()
    {
        var (cli, rest) = CliOptions.Split(["get", "nodes", "-o", "yaml"]);
        var loaded = false;

        var code = await SfxHost.RunScriptingAsync(
            CommandLine.Parse(rest, new HashSet<string>()),
            cli,
            () =>
            {
                loaded = true;
                return ContextStore.Load(Path.Combine(_scratch, "config.toml"));
            },
            new ClusterConnector(BuiltInAuth.Providers),
            new CliConsole(TextWriter.Null, TextWriter.Null, false),
            default);

        Assert.Equal(2, code);
        Assert.False(loaded);
    }

    [Fact]
    public void An_endpoint_is_never_taken_from_the_scripting_arguments()
    {
        var store = ContextStore.Load(Path.Combine(_scratch, "config.toml"));
        var (_, rest) = CliOptions.Split(["get", "nodes", "--endpoint", "http://dev:19080"]);

        Assert.False(SfxHost.NeedsPick(CommandLine.Parse(rest, new HashSet<string>()), store));
        Assert.Equal(["get", "nodes"], CommandLine.Parse(rest, new HashSet<string>()).Arguments);
    }

    [Fact]
    public async Task Help_needs_no_cluster()
    {
        using var output = new StringWriter();
        var (options, rest) = CliOptions.Split(["store"]);

        var code = await Scripting.RunAsync(
            rest,
            options,
            help: true,
            _ => throw new InvalidOperationException(),
            new CliConsole(output, TextWriter.Null, false),
            default);

        Assert.Equal(0, code);
        Assert.StartsWith("usage: sfx store ls", output.ToString());
    }

    private sealed class FailingAuthProvider(string name, Func<AuthContext, IClusterAuth> create) : IClusterAuthProvider
    {
        public string Name => name;

        public string Description => "";

        public bool RequiresTls => false;

        public IReadOnlyList<AuthOptionInfo> Options => [];

        public IClusterAuth Create(AuthContext context) => create(context);
    }

    private async Task<(int Code, string Error)> Connect(IClusterAuthProvider provider)
    {
        var store = ContextStore.Load(Path.Combine(_scratch, "config.toml"));
        store.Set(new ClusterContext("prod", "http://prod:19080", [provider.Name], new AuthOptions()));
        store.Save();
        var (cli, rest) = CliOptions.Split(["get", "nodes", "-c", "prod"]);
        using var error = new StringWriter();

        var code = await SfxHost.RunScriptingAsync(
            CommandLine.Parse(rest, new HashSet<string>()),
            cli,
            () => ContextStore.Load(store.Path),
            new ClusterConnector([.. BuiltInAuth.Providers, provider]),
            new CliConsole(TextWriter.Null, error, false),
            default);
        return (code, error.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task A_connect_that_times_out_cannot_connect()
    {
        var (code, error) = await Connect(new FailingAuthProvider("slow", _ => throw new TaskCanceledException()));

        Assert.Equal(5, code);
        Assert.Equal("sfx: could not connect to prod: timed out after 30s\n", error);
    }

    [Fact]
    public async Task A_call_that_times_out_is_a_cluster_failure()
    {
        var cluster = Cluster().Throws("GetNodeInfoListAsync", new TaskCanceledException());

        var result = await Run(Session(cluster), "get", "nodes");

        Assert.Equal(1, result.Code);
        Assert.Equal("sfx: the cluster didn't answer: timed out after 30s\n", result.Error);
    }

    [Fact]
    public async Task Cancelling_is_not_a_timeout()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var (options, rest) = CliOptions.Split(["get", "nodes"]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scripting.RunAsync(
            rest,
            options,
            help: false,
            ct => Task.FromException<UiSession>(new OperationCanceledException(ct)),
            new CliConsole(TextWriter.Null, TextWriter.Null, false),
            cancel.Token));
    }
}
