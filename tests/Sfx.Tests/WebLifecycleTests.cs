using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.ServiceFabric.Client.Exceptions;
using Microsoft.ServiceFabric.Common;
using Microsoft.ServiceFabric.Common.Exceptions;
using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Web;
using StoreFile = Microsoft.ServiceFabric.Common.FileInfo;

namespace Sfx.Tests;

/// <summary>The browser API's application types, new services, service settings, upgrade plans, search and image store.</summary>
public sealed class WebLifecycleTests : IDisposable
{
    // Under the test's own output folder rather than the system's temporary one; removed afterwards.
    private readonly string _scratch = Path.Combine(AppContext.BaseDirectory, "scratch", $"web-{Guid.NewGuid():N}");

    public WebLifecycleTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private UiSession Session(FakeCluster? cluster = null, bool readOnly = false, bool power = false)
    {
        var context = new ClusterContext("test", "localhost", [], new AuthOptions(), readOnly, power);
        var session = new ClusterSession(context, new Uri("http://localhost:19080"), new ClusterQueries(cluster?.Client!));
        return new UiSession(session, new ActionLog(Path.Combine(_scratch, "actions.log")), "0.0.0");
    }

    private static Task<ActionResult> Run(UiSession session, ActionDto action, Dictionary<string, string>? inputs = null, string? confirm = null) =>
        WebActions.RunAsync(session, action.Id, new ActionRequest(new(action.Target), inputs, confirm, action.Signature), default);

    // Application types

    private static ApplicationTypeRow Type(string status = "Available") =>
        new("ShopType", "1.0.0", status, new Dictionary<string, string> { ["Port"] = "8080", ["Count"] = "3" });

    [Fact]
    public void A_type_offers_creating_an_application_with_its_parameters_prefilled()
    {
        var dto = ApiLifecycle.Type(Session(), Type(), []);

        var create = dto.Actions.Single(a => a.Id == "create-application");
        Assert.True(create.Form);
        Assert.Equal(
            [("name", "text", "fabric:/"), ("param:Count", "text", "3"), ("param:Port", "text", "8080")],
            create.Inputs.Select(i => (i.Name, i.Kind, i.Default)).ToList());
        var unprovision = dto.Actions.Single(a => a.Id == "unprovision-type");
        Assert.Equal("destructive", unprovision.Risk);
        Assert.Equal("1.0.0", unprovision.ConfirmText);
    }

    [Fact]
    public void A_type_in_use_is_not_offered_for_unprovisioning()
    {
        var dto = ApiLifecycle.Type(Session(), Type(), ["fabric:/shop"]);

        Assert.Equal(["fabric:/shop"], dto.Applications);
        Assert.DoesNotContain(dto.Actions, a => a.Id == "unprovision-type");
        Assert.Contains(dto.Actions, a => a.Id == "create-application");
    }

    [Fact]
    public void A_type_still_provisioning_offers_nothing()
    {
        Assert.Empty(ApiLifecycle.Type(Session(), Type("Provisioning"), []).Actions);
        Assert.Empty(ApiLifecycle.Type(Session(readOnly: true), Type(), []).Actions);
    }

    [Fact]
    public async Task Creating_an_application_sends_only_the_parameters_that_differ_from_the_defaults()
    {
        var cluster = new FakeCluster()
            .Paged("GetApplicationTypeInfoListByNameAsync", new ApplicationTypeInfo("ShopType", "1.0.0", new Dictionary<string, string>
            {
                ["Port"] = "8080",
                ["Count"] = "3",
            }))
            .OnCall("CreateApplicationAsync");
        var session = Session(cluster);
        var create = ApiLifecycle.Type(session, Type(), []).Actions.Single(a => a.Id == "create-application");

        var result = await Run(session, create, new() { ["name"] = "shop", ["param:Port"] = "8080", ["param:Count"] = "5" });

        Assert.Equal("Create fabric:/shop (ShopType 1.0.0), setting Count", result.Message);
        var description = (ApplicationDescription)cluster.CallsTo("CreateApplicationAsync").Single()[0]!;
        Assert.Equal("fabric:/shop", description.Name.ToString());
        Assert.Equal(new Dictionary<string, string> { ["Count"] = "5" }, description.Parameters);
    }

    // Creating a service

    private static Dictionary<string, string> Application(string serviceTypes) => new()
    {
        ["applicationId"] = "shop",
        ["name"] = "fabric:/shop",
        ["typeName"] = "ShopType",
        ["version"] = "1.0.0",
        ["serviceTypes"] = serviceTypes,
    };

    [Fact]
    public void An_application_offers_creating_a_service_of_its_types()
    {
        var create = WebActions.For(Session(), "application", Application("CounterType=Stateful\nWebType=Stateless")).Single(a => a.Id == "create-service");

        Assert.True(create.Form);
        var inputs = create.Inputs.ToDictionary(i => i.Name);
        Assert.Equal(["name", "type", "kind", "count", "partitions", "persisted"], create.Inputs.Select(i => i.Name));
        Assert.Equal("fabric:/shop/", inputs["name"].Default);
        Assert.Equal([("CounterType", "Stateful"), ("WebType", "Stateless")], inputs["type"].Choices!.Select(c => (c.Value, c.Hint)));
        Assert.Equal("CounterType", inputs["type"].Default);
        Assert.Equal("Stateful", inputs["kind"].Default);
        Assert.Equal("3", inputs["count"].Default);
        Assert.Equal("1", inputs["partitions"].Default);
        Assert.Equal("yes", inputs["persisted"].Default);
    }

    [Fact]
    public void Creating_a_service_is_not_offered_without_service_types_or_on_the_system_application()
    {
        Assert.DoesNotContain(WebActions.For(Session(), "application", Application("")), a => a.Id == "create-service");
        var system = Application("X=Stateful");
        system["applicationId"] = ClusterQueries.SystemApplicationId;
        Assert.DoesNotContain(WebActions.For(Session(), "application", system), a => a.Id == "create-service");
    }

    [Fact]
    public async Task Creating_a_service_needs_a_name_and_a_type_it_offered()
    {
        var session = Session();
        var create = WebActions.For(session, "application", Application("CounterType=Stateful")).Single(a => a.Id == "create-service");
        Dictionary<string, string> Inputs(string name, string type) => new()
        {
            ["name"] = name,
            ["type"] = type,
            ["kind"] = "Stateful",
            ["count"] = "3",
            ["partitions"] = "1",
            ["persisted"] = "yes",
        };

        var unnamed = await Assert.ThrowsAsync<ActionException>(() => Run(session, create, Inputs("fabric:/shop/", "CounterType")));
        Assert.Equal("Give the service a name.", unnamed.Message);
        var other = await Assert.ThrowsAsync<ActionException>(() => Run(session, create, Inputs("Counter", "OtherType")));
        Assert.Equal(400, other.Status);
    }

    [Fact]
    public void A_new_service_is_named_under_its_application()
    {
        var service = LifecycleActions.NewService(
            new Args(Application("")),
            new Args(new Dictionary<string, string>
            {
                ["name"] = "Counter",
                ["type"] = "CounterType",
                ["kind"] = "Stateless",
                ["count"] = "-1",
                ["partitions"] = "2",
            }));

        Assert.Equal(new NewService("fabric:/shop/Counter", "CounterType", false, -1, 2, true), service);
    }

    // Service settings

    private static StatefulServiceDescription Counter() => new(
        new ServiceName("fabric:/shop/Counter"),
        "CounterType",
        new SingletonPartitionSchemeDescription(),
        3,
        2,
        true,
        defaultMoveCost: MoveCost.Low,
        quorumLossWaitDurationSeconds: 120);

    private static Dictionary<string, string> CounterTarget() => new()
    {
        ["serviceId"] = "shop~Counter",
        ["applicationId"] = "shop",
        ["name"] = "fabric:/shop/Counter",
        ["kind"] = "Stateful",
        ["count"] = "3",
        ["min"] = "2",
    };

    private (FakeCluster Cluster, UiSession Session, ActionDto Action) Settings()
    {
        var cluster = new FakeCluster().On("GetServiceDescriptionAsync", Counter()).OnCall("UpdateServiceAsync");
        var session = Session(cluster);
        return (cluster, session, WebActions.For(session, "service", CounterTarget()).Single(a => a.Id == "edit-service-settings"));
    }

    [Theory]
    [InlineData("quorum-loss-wait", "soon", "Quorum loss wait must be a number of seconds")]
    [InlineData("count", "0", "Target replicas must be a whole number of 1 or more.")]
    [InlineData("min-replicas", "4", "Min replicas (4) can't be more than target replicas (3).")]
    [InlineData("instances", "2", "A stateful service has no setting 'instances'.")]
    [InlineData("quorum-loss-wait", "120", "Nothing to change")]
    public async Task Service_settings_are_checked_before_anything_changes(string key, string value, string message)
    {
        var (cluster, session, action) = Settings();

        var ex = await Assert.ThrowsAsync<ActionException>(() => Run(session, action, new() { [key] = value }));

        Assert.Equal(400, ex.Status);
        Assert.StartsWith(message, ex.Message);
        Assert.Empty(cluster.CallsTo("UpdateServiceAsync"));
        Assert.Empty(session.GetOperations());
    }

    [Fact]
    public async Task Service_settings_change_in_one_update()
    {
        var (cluster, session, action) = Settings();

        var result = await Run(session, action, new() { ["quorum-loss-wait"] = "5m", ["move-cost"] = "high" });

        Assert.Equal("Change Quorum loss wait to 300s and Default move cost to High on fabric:/shop/Counter", result.Message);
        Assert.Single(cluster.CallsTo("UpdateServiceAsync"));
        Assert.Equal("done", session.GetOperations().Single().State);
    }

    [Fact]
    public void Settings_are_offered_on_user_services_only()
    {
        var system = CounterTarget();
        system["applicationId"] = ClusterQueries.SystemApplicationId;

        Assert.DoesNotContain(WebActions.For(Session(), "service", system), a => a.Id == "edit-service-settings");
        Assert.DoesNotContain(WebActions.For(Session(readOnly: true), "service", CounterTarget()), a => a.Id == "edit-service-settings");
    }

    [Fact]
    public void Settings_are_described_in_the_browsers_words()
    {
        var setting = ApiLifecycle.Setting(ServiceSettings.Read(Counter()).Single(s => s.Key == "move-cost"));

        Assert.Equal("choice", setting.Kind);
        Assert.Equal("Low", setting.Value);
        Assert.Contains("High", setting.Choices!);
    }

    // Upgrade plans

    private static UpgradeRow Upgrade(string state, string mode = "Monitored", string onFailure = "Manual", string kind = "application") =>
        new(
            kind == "cluster" ? "cluster" : "fabric:/shop",
            kind,
            kind == "cluster" ? null : "shop",
            "1.0.0",
            new UpgradeReport(state, mode, "2.0.0", "UD2", "", null, "None", "", "", [], "UD1", [], [], onFailure));

    [Theory]
    [InlineData("RollingForwardInProgress", true)]
    [InlineData("RollingForwardPending", true)]
    [InlineData("RollingBackInProgress", false)]
    [InlineData("RollingForwardCompleted", false)]
    [InlineData("Failed", false)]
    public void The_plan_can_change_only_while_an_upgrade_goes_forward(string state, bool offered)
    {
        Assert.Equal(offered, Api.UpgradeActions(Session(), Upgrade(state)).Any(a => a.Id == "change-upgrade-plan"));
        Assert.Equal(offered, Api.UpgradeActions(Session(), Upgrade(state, kind: "cluster")).Any(a => a.Id == "change-upgrade-plan"));
        Assert.Empty(Api.UpgradeActions(Session(readOnly: true), Upgrade(state)));
    }

    [Theory]
    [InlineData("Monitored", "Manual", "monitored-manual")]
    [InlineData("Monitored", "", "monitored-rollback")]
    [InlineData("UnmonitoredManual", "", "unmonitored-manual")]
    [InlineData("UnmonitoredAuto", "Rollback", "unmonitored-auto")]
    public void The_current_plan_is_preselected(string mode, string onFailure, string plan)
    {
        var change = Api.UpgradeActions(Session(), Upgrade("RollingForwardInProgress", mode, onFailure)).Single(a => a.Id == "change-upgrade-plan");

        var input = Assert.Single(change.Inputs);
        Assert.Equal(("plan", "choice", plan), (input.Name, input.Kind, input.Default));
        Assert.Equal(UpgradePlan.All.Select(p => p.Id), input.Choices!.Select(c => c.Value));
        Assert.Equal(plan, Dto.Upgrade(Upgrade("RollingForwardInProgress", mode, onFailure), []).Plan);
    }

    [Fact]
    public async Task Changing_to_the_plan_it_already_follows_is_refused()
    {
        var session = Session();
        var change = Api.UpgradeActions(session, Upgrade("RollingForwardPending")).Single(a => a.Id == "change-upgrade-plan");

        var ex = await Assert.ThrowsAsync<ActionException>(() => Run(session, change, new() { ["plan"] = "monitored-manual" }));

        Assert.Equal("The upgrade of fabric:/shop already runs monitored, stop on failure.", ex.Message);
    }

    [Fact]
    public async Task Changing_the_plan_updates_the_running_upgrade()
    {
        var cluster = new FakeCluster().OnCall("UpdateApplicationUpgradeAsync");
        var session = Session(cluster);
        var change = Api.UpgradeActions(session, Upgrade("RollingForwardPending")).Single(a => a.Id == "change-upgrade-plan");

        var result = await Run(session, change, new() { ["plan"] = "unmonitored-auto" });

        Assert.Equal("Switch the upgrade of fabric:/shop to unmonitored, automatic", result.Message);
        Assert.Single(cluster.CallsTo("UpdateApplicationUpgradeAsync"));
    }

    [Fact]
    public void Resume_and_roll_back_stay_for_manual_upgrades_of_both_kinds()
    {
        foreach (var kind in new[] { "application", "cluster" })
        {
            var ids = Api.UpgradeActions(Session(), Upgrade("RollingForwardPending", "UnmonitoredManual", "", kind)).Select(a => a.Id).ToList();
            Assert.Equal(["upgrade.resume", "upgrade.rollback", "change-upgrade-plan"], ids);
        }
    }

    // Search

    private static readonly NodeRow Node = new("_Node_1", "10.0.0.4", "FrontEnd", "Up", HealthState.Ok, "UD1", "fd:/1", false, "10.1", null);
    private static readonly ApplicationRow Shop = new("shop~orders", "fabric:/shop/orders", "OrdersType", "1.0.0", "Ready", HealthState.Ok);
    private static readonly ServiceRow Counter2 = new(
        "shop~orders~Counter",
        "fabric:/shop/orders/Counter",
        "CounterType",
        "Stateful",
        "Active",
        HealthState.Ok);

    private static readonly IReadOnlyList<SearchHit> Index =
    [
        new("node", Node.Name, Node: Node),
        new("application", Shop.Name, Application: Shop),
        new("service", Counter2.Name, Application: Shop, Service: Counter2),
    ];

    [Fact]
    public async Task Text_searches_the_index_with_the_ids_the_browser_routes_by()
    {
        var hits = await ApiSearch.SearchAsync("orders", _ => Task.FromResult(Index), (_, _) => throw new InvalidOperationException(), default);

        Assert.Equal(
            [
                new SearchHitDto("application", "fabric:/shop/orders", "OrdersType 1.0.0", ApplicationId: "shop~orders"),
                new SearchHitDto("service", "fabric:/shop/orders/Counter", "CounterType", ApplicationId: "shop~orders", ServiceId: "shop~orders~Counter"),
            ],
            hits);
        var node = Assert.Single(await ApiSearch.SearchAsync(
            "frontend",
            _ => Task.FromResult(Index),
            (_, _) => throw new InvalidOperationException(),
            default));
        Assert.Equal(new SearchHitDto("node", "_Node_1", "FrontEnd, 10.0.0.4", Node: "_Node_1"), node);
    }

    [Fact]
    public async Task A_partition_id_is_looked_up_rather_than_searched()
    {
        var id = Guid.NewGuid();
        var partition = new PartitionRow(new PartitionId(id), "Stateful", "Singleton", "Ready", HealthState.Ok, "3");
        Guid? asked = null;

        var hits = await ApiSearch.SearchAsync(
            $" {id} ",
            _ => throw new InvalidOperationException("The index isn't needed."),
            (g, _) =>
            {
                asked = g;
                return Task.FromResult<SearchHit?>(new SearchHit("partition", id.ToString(), Application: Shop, Service: Counter2, Partition: partition));
            },
            default);

        Assert.Equal(id, asked);
        Assert.Equal(
            [new SearchHitDto(
                "partition",
                id.ToString(),
                "fabric:/shop/orders/Counter",
                ApplicationId: "shop~orders",
                ServiceId: "shop~orders~Counter",
                PartitionId: id.ToString())],
            hits);
        Assert.Empty(await ApiSearch.SearchAsync(
            id.ToString(),
            _ => throw new InvalidOperationException(),
            (_, _) => Task.FromResult<SearchHit?>(null),
            default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_query_finds_nothing_without_asking(string? query) =>
        Assert.Empty(await ApiSearch.SearchAsync(query, _ => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException(), default));

    [Fact]
    public async Task Search_returns_at_most_twenty()
    {
        var nodes = Enumerable.Range(1, 30).Select(i => new SearchHit("node", $"_Node_{i}", Node: Node with { Name = $"_Node_{i}" })).ToList();

        Assert.Equal(
            ApiSearch.Limit,
            (await ApiSearch.SearchAsync(
                "node",
                _ => Task.FromResult<IReadOnlyList<SearchHit>>(nodes),
                (_, _) => throw new InvalidOperationException(),
                default)).Count);
    }

    // Image store

    private static readonly ImageStoreListing Root = new(
        "",
        [new ImageStoreFolder("Logs", "Logs", 2), new ImageStoreFolder("ShopType_2.0.0", "ShopType_2.0.0", 4), new ImageStoreFolder("Store", "Store", 10)],
        [new ImageStoreFile("notes.txt", "notes.txt", 12, "", null)]);

    private static readonly Dictionary<string, ImageStorePackage> Packages = new()
    {
        ["ShopType_2.0.0"] = new ImageStorePackage("ShopType_2.0.0", "ShopType_2.0.0", "ShopType", null, null),
    };

    [Fact]
    public void Store_entries_offer_delete_except_the_clusters_own_and_provision_for_packages_not_provisioned()
    {
        var store = ApiImageStore.Listing(Session(), Root, "Native", null, Packages);

        var folders = store.Folders.ToDictionary(f => f.Name);
        Assert.True(folders["Store"].Reserved);
        Assert.Empty(folders["Store"].Actions);
        Assert.Equal(["delete-store-content"], folders["Logs"].Actions.Select(a => a.Id));
        Assert.Equal("Logs", folders["Logs"].Actions[0].ConfirmText);
        Assert.Equal("destructive", folders["Logs"].Actions[0].Risk);
        Assert.Equal(["delete-store-content", "provision-package"], folders["ShopType_2.0.0"].Actions.Select(a => a.Id));
        Assert.Equal(new StorePackageDto("ShopType", null, false, null), folders["ShopType_2.0.0"].Package);
        Assert.Null(folders["Logs"].Package);
        Assert.Equal(["delete-store-content"], store.Files.Single().Actions.Select(a => a.Id));
        Assert.Null(store.Files.Single().Version);
    }

    [Fact]
    public void Inside_a_reserved_folder_nothing_can_be_deleted()
    {
        var listing = new ImageStoreListing(
            @"Store\ShopType",
            [],
            [new ImageStoreFile(@"Store\ShopType\ApplicationManifest.1.0.0.xml", "ApplicationManifest.1.0.0.xml", 1, "3", null)]);

        Assert.Empty(ApiImageStore.Listing(Session(), listing, "Native", null, new Dictionary<string, ImageStorePackage>()).Files.Single().Actions);
    }

    [Fact]
    public void A_provisioned_package_is_not_offered_for_provisioning_and_read_only_sessions_get_no_store_actions()
    {
        var provisioned = new Dictionary<string, ImageStorePackage>
        {
            ["ShopType_2.0.0"] = new(
                "ShopType_2.0.0",
                "ShopType_2.0.0",
                "ShopType",
                "2.0.0",
                new ApplicationTypeRow("ShopType", "2.0.0", "Available", new Dictionary<string, string>())),
        };

        var folder = ApiImageStore.Listing(Session(), Root, "Native", null, provisioned).Folders.Single(f => f.Name == "ShopType_2.0.0");
        Assert.Equal(["delete-store-content"], folder.Actions.Select(a => a.Id));
        Assert.Equal(new StorePackageDto("ShopType", "2.0.0", true, "Available"), folder.Package);

        var readOnly = ApiImageStore.Listing(Session(readOnly: true), Root, "Native", null, Packages);
        Assert.All(readOnly.Folders, f => Assert.Empty(f.Actions));
        Assert.All(readOnly.Files, f => Assert.Empty(f.Actions));
    }

    [Fact]
    public async Task The_image_store_is_hidden_only_when_the_cluster_says_it_cannot_be_browsed()
    {
        var blob = new FakeCluster()
            .Throws("GetImageStoreRootContentAsync", new ServiceFabricException("not supported", FabricErrorCodes.E_INVALIDARG, false))
            .On("GetImageStoreConnectionStringAsync", "xstore:AccountName=x");
        var native = new FakeCluster().On("GetImageStoreRootContentAsync", new ImageStoreContent([new StoreFile("1", null, null, "a.txt")], []));
        var failing = new FakeCluster().Throws("GetImageStoreRootContentAsync", new HttpRequestException("down"));

        Assert.False(await Session(blob).ImageStoreBrowsableAsync(default));
        Assert.True(await Session(native).ImageStoreBrowsableAsync(default));
        Assert.True(await Session(failing).ImageStoreBrowsableAsync(default));
    }

    // Uploads

    [Theory]
    [InlineData("MyAppPkg/ApplicationManifest.xml", "MyAppPkg/ApplicationManifest.xml")]
    [InlineData(@"MyAppPkg\Code\app.dll", "MyAppPkg/Code/app.dll")]
    [InlineData("MyAppPkg//./Code/app.dll", "MyAppPkg/Code/app.dll")]
    [InlineData("app.sfpkg", "app.sfpkg")]
    public void Upload_paths_are_normalized(string fileName, string relative) => Assert.Equal(relative, PackageUpload.RelativePath(fileName));

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("MyAppPkg/../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData(@"\\server\share\x")]
    [InlineData(@"C:\Windows\x.dll")]
    [InlineData("C:x.dll")]
    [InlineData("")]
    [InlineData("a/<b>")]
    public void Upload_paths_outside_the_package_are_refused(string fileName)
    {
        var ex = Assert.Throws<ActionException>(() => PackageUpload.RelativePath(fileName));
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public void The_folder_every_file_shares_is_the_package_root()
    {
        Assert.Equal("MyAppPkg", PackageUpload.CommonFolder(["MyAppPkg/ApplicationManifest.xml", "MyAppPkg/Code/app.dll"]));
        Assert.Equal("", PackageUpload.CommonFolder(["ApplicationManifest.xml", "Code/app.dll"]));
        Assert.Equal("", PackageUpload.CommonFolder(["A/ApplicationManifest.xml", "B/app.dll"]));
        Assert.Equal("", PackageUpload.CommonFolder(["MyAppPkg/x", "MyAppPkgOther/y"]));
        Assert.Equal("", PackageUpload.CommonFolder([]));
    }

    [Fact]
    public void Upload_progress_reads_as_files_and_bytes()
    {
        Assert.Equal("12 of 40 files, 18 of 52 MB", PackageUpload.Progress(new UploadProgress(12, 40, 18L << 20, 52L << 20)));
        Assert.Equal("0 of 2 files, 0 of 3.5 KB", PackageUpload.Progress(new UploadProgress(0, 2, 0, 3584)));
    }

    private const string Manifest = """
        <ApplicationManifest ApplicationTypeName="MyAppType" ApplicationTypeVersion="1.2.0" xmlns="http://schemas.microsoft.com/2011/01/fabric" />
        """;

    private static async Task<(IReadOnlyList<string> Files, IReadOnlyDictionary<string, string> Fields)> ReceiveAsync(
        string folder,
        params (string Name, string? FileName, string Content)[] parts)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (name, fileName, content) in parts)
        {
            if (fileName is null)
            {
                form.Add(new StringContent(content), name);
            }
            else
            {
                form.Add(new StringContent(content), name, fileName);
            }
        }
        var boundary = form.Headers.ContentType!.Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');
        using var body = new MemoryStream();
        await form.CopyToAsync(body);
        body.Position = 0;
        return await ApiImageStore.ReceiveAsync(new MultipartReader(boundary, body), folder, default);
    }

    [Fact]
    public async Task A_folder_upload_is_saved_and_opened_from_the_folder_its_files_share()
    {
        var files = Path.Combine(_scratch, "files");
        Directory.CreateDirectory(files);

        var (saved, fields) = await ReceiveAsync(
            files,
            ("provision", null, "false"),
            ("files", "MyAppPkg/ApplicationManifest.xml", Manifest),
            ("files", "MyAppPkg/CounterPkg/ServiceManifest.xml", "<ServiceManifest />"),
            ("storePath", null, "Custom/Path"));

        Assert.Equal(["MyAppPkg/ApplicationManifest.xml", "MyAppPkg/CounterPkg/ServiceManifest.xml"], saved);
        Assert.Equal("false", fields["provision"]);
        Assert.Equal("Custom/Path", fields["storePath"]);
        Assert.True(File.Exists(Path.Combine(files, "MyAppPkg", "CounterPkg", "ServiceManifest.xml")));
        using var package = ApiImageStore.Open(files, saved);
        Assert.Equal(("MyAppType", "1.2.0", "MyAppType_1.2.0"), (package.TypeName, package.TypeVersion, package.StorePath));
        Assert.Equal(Path.Combine(files, "MyAppPkg"), package.Folder);
    }

    [Fact]
    public async Task An_upload_that_is_not_a_package_says_so_without_the_temporary_path()
    {
        var files = Path.Combine(_scratch, "files");
        Directory.CreateDirectory(files);
        var (saved, _) = await ReceiveAsync(files, ("files", "MyAppPkg/Code/app.dll", "x"));

        var ex = Assert.Throws<ActionException>(() => ApiImageStore.Open(files, saved));

        Assert.Equal(400, ex.Status);
        Assert.Equal("MyAppPkg has no ApplicationManifest.xml at its root, so it isn't an application package.", ex.Message);
        Assert.Equal(
            "Choose a package to upload: a package folder, or an .sfpkg or .zip file.",
            Assert.Throws<ActionException>(() => ApiImageStore.Open(files, [])).Message);
    }

    [Fact]
    public async Task An_upload_with_a_path_outside_the_package_or_a_file_twice_is_refused()
    {
        var files = Path.Combine(_scratch, "files");
        Directory.CreateDirectory(files);

        await Assert.ThrowsAsync<ActionException>(() => ReceiveAsync(files, ("files", "../escape.txt", "x")));
        Assert.False(File.Exists(Path.Combine(_scratch, "escape.txt")));
        var twice = await Assert.ThrowsAsync<ActionException>(() => ReceiveAsync(files, ("files", "P/a.txt", "1"), ("files", "P/a.txt", "2")));
        Assert.Equal("'P/a.txt' is in the upload twice.", twice.Message);
    }

    [Theory]
    [InlineData(null, "MyAppType_1.2.0")]
    [InlineData(" /Apps\\Shop/ ", "Apps/Shop")]
    public void Uploads_go_to_the_store_path_given_or_the_packages_own(string? given, string path) =>
        Assert.Equal(path, ApiImageStore.StorePath(given, "MyAppType_1.2.0"));

    [Theory]
    [InlineData("Store/Mine")]
    [InlineData("a/../b")]
    [InlineData("a//b")]
    public void Uploads_to_reserved_or_odd_store_paths_are_refused(string given) =>
        Assert.Throws<ActionException>(() => ApiImageStore.StorePath(given, "x"));

    [Fact]
    public async Task Read_only_sessions_cannot_upload()
    {
        var ex = await Assert.ThrowsAsync<ActionException>(() => ApiImageStore.UploadAsync(Session(readOnly: true), new DefaultHttpContext()));

        Assert.Equal(403, ex.Status);
        Assert.Equal("This context is read-only.", ex.Message);
    }

    [Fact]
    public async Task Uploads_must_be_multipart()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";

        var ex = await Assert.ThrowsAsync<ActionException>(() => ApiImageStore.UploadAsync(Session(), context));
        Assert.Equal(415, ex.Status);
    }

    [Fact]
    public void Operations_show_progress_while_it_can_be_counted()
    {
        var session = Session();
        var id = session.Begin(ApiImageStore.UploadActionId, "Upload MyAppType 1.2.0", "Starting the upload.");

        session.Progress(id, "Uploading 1 of 2 files, 1 of 2 KB", 0.5);
        Assert.Equal(("running", 0.5), (session.GetOperations().Single().State, session.GetOperations().Single().Progress));

        session.Finish(id, "Upload MyAppType 1.2.0", SettleChecks.Done, "Uploaded to MyAppType_1.2.0.");
        var done = session.GetOperations().Single();
        Assert.Equal(("done", null, "Uploaded to MyAppType_1.2.0."), (done.State, done.Progress, done.Detail));
        Assert.NotNull(done.Finished);
        Assert.Contains(session.ReadLog(10), e => e.Action == "Upload MyAppType 1.2.0" && e.Outcome == "done");
    }

    // Settling

    [Fact]
    public void A_provisioned_type_settles_when_available_and_fails_with_the_clusters_reason()
    {
        ApplicationTypeRow Row(string status, string details = "") => new("T", "1", status, new Dictionary<string, string>(), details);

        Assert.Equal(SettleChecks.Running, SettleChecks.TypeProvisioned(null, "T", "1", TimeSpan.FromSeconds(5)).State);
        Assert.Equal(SettleChecks.Failed, SettleChecks.TypeProvisioned(null, "T", "1", TimeSpan.FromMinutes(3)).State);
        Assert.Equal((SettleChecks.Running, "Provisioning T 1."), SettleChecks.TypeProvisioned(Row("Provisioning"), "T", "1", TimeSpan.Zero));
        Assert.Equal((SettleChecks.Done, "T 1 is available."), SettleChecks.TypeProvisioned(Row("Available"), "T", "1", TimeSpan.Zero));
        Assert.Equal(
            (SettleChecks.Failed, "Provisioning failed: bad manifest."),
            SettleChecks.TypeProvisioned(Row("Failed", "bad manifest"), "T", "1", TimeSpan.Zero));

        var before = new HashSet<(string, string)> { ("T", "1") };
        Assert.Equal(SettleChecks.Running, SettleChecks.NewTypeProvisioned(before, [Row("Available")], TimeSpan.FromSeconds(5)).State);
        Assert.Equal(
            (SettleChecks.Done, "U 2 is available."),
            SettleChecks.NewTypeProvisioned(before, [Row("Available"), new("U", "2", "Available", new Dictionary<string, string>())], TimeSpan.Zero));
    }

    [Fact]
    public void New_entities_unprovisioning_and_plan_changes_settle()
    {
        Assert.Equal((SettleChecks.Running, "Waiting for the service to appear."), SettleChecks.EntityStatus(null, "Active", "service"));
        Assert.Equal((SettleChecks.Done, "Application is Ready."), SettleChecks.EntityStatus("Ready", "Ready", "application"));
        Assert.Equal(SettleChecks.Done, SettleChecks.TypeUnprovisioned(null).State);
        Assert.Equal(SettleChecks.Running, SettleChecks.TypeUnprovisioned(new("T", "1", "Unprovisioning", new Dictionary<string, string>())).State);
        Assert.Equal(SettleChecks.Done, SettleChecks.PlanSwitched("unmonitored-auto", UpgradePlan.UnmonitoredAuto, true).State);
        Assert.Equal(SettleChecks.Running, SettleChecks.PlanSwitched("monitored-manual", UpgradePlan.UnmonitoredAuto, true).State);
        Assert.Equal(SettleChecks.Done, SettleChecks.PlanSwitched("monitored-manual", UpgradePlan.UnmonitoredAuto, false).State);
    }
}
