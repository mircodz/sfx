using System.IO.Compression;
using Microsoft.ServiceFabric.Common;
using Sfx.Core;
using Sfx.Core.Auth;

namespace Sfx.Tests;

/// <summary>
/// Overview, image store, cluster upgrade state and the application lifecycle against a real cluster: reading needs
/// SFX_TEST_CLUSTER; deploying a tiny guest-executable application through its life also needs SFX_TEST_MUTATE=1.
/// </summary>
public class LifecycleIntegrationTests
{
    private const string AppName = "fabric:/SfxLifecycleTest";
    private const string TypeName = "SfxLifecycleTestType";
    private const string ServiceTypeName = "SfxLifecycleServiceType";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private static async Task<ClusterQueries> ConnectAsync()
    {
        var connection = new ClusterConnection(ClusterEndpoint.Parse(ClusterFactAttribute.Endpoint!, secure: false), [NoAuth.Instance]);
        return new ClusterQueries(await ClusterClientFactory.CreateAsync(connection));
    }

    [ClusterFact]
    public async Task Summarizes_searches_and_finds_partitions()
    {
        var queries = await ConnectAsync();
        var overview = new ClusterOverview(queries);
        var ct = CancellationToken.None;

        var summary = await overview.GetSummaryAsync(ct);
        Assert.NotEmpty(summary.CodeVersion);
        Assert.Contains(summary.Counts, c => c.Kind == "Node" && c.Healthy + c.Warning + c.Error > 0);

        var index = await overview.GetSearchIndexAsync(ct);
        Assert.Contains(index, hit => hit.Kind == "node" && hit.Node is not null);
        var naming = Assert.Single(ClusterOverview.Search(index, "fabric:/System/NamingService"), hit => hit.Kind == "service");
        Assert.Equal(ClusterQueries.SystemApplicationId, naming.Application!.Id);

        var partition = (await queries.GetPartitionsAsync(naming.Service!.Id, ct))[0];
        var found = await overview.FindPartitionAsync(Guid.Parse(partition.Id.ToString()), ct);
        Assert.Equal(naming.Service.Id, found?.Service?.Id);
        Assert.Null(await overview.FindPartitionAsync(Guid.NewGuid(), ct));

        Assert.All(await overview.GetUnhealthyAsync(ct), e => Assert.NotEqual(HealthState.Ok, e.Health));
    }

    [ClusterFact]
    public async Task Reads_types_upgrade_options_and_the_image_store()
    {
        var queries = await ConnectAsync();
        var ct = CancellationToken.None;

        Assert.All(await queries.GetApplicationTypesAsync(ct), t => Assert.False(string.IsNullOrEmpty(t.Version)));

        var options = await new ClusterUpgrades(queries.Client).GetOptionsAsync(ct);
        Assert.NotEmpty(options.CodeVersion);
        Assert.Equal(options.AzureManaged, options.Note is not null);

        var store = new ImageStore(queries.Client);
        if (await store.GetKindAsync(ct) == ImageStoreKind.Native)
        {
            var root = await store.GetContentAsync(null, ct, folderSizes: true);
            Assert.All(root.Folders, f => Assert.True(f.Size >= 0));
            Assert.NotNull(await store.GetPackagesAsync(ct));
        }
    }

    [ClusterFact]
    public async Task Provisions_creates_reconfigures_and_upgrades_an_application()
    {
        if (Environment.GetEnvironmentVariable("SFX_TEST_MUTATE") != "1")
        {
            return;
        }

        var queries = await ConnectAsync();
        var client = queries.Client;
        var lifecycle = new ApplicationLifecycle(client);
        var store = new ImageStore(client);
        var ops = new ClusterOperations(client);
        var ct = CancellationToken.None;
        var root = Directory.CreateTempSubdirectory("sfx-lifecycle-");

        await CleanUpAsync(queries, lifecycle, ops, store);
        try
        {
            var progress = new List<UploadProgress>();
            var first = await lifecycle.UploadAndProvisionAsync(WritePackage(root.FullName, "1.0.0"), null, new SyncProgress(progress), ct);
            Assert.Equal($"{TypeName}_1.0.0", first.StorePath);
            Assert.Equal(1, progress[^1].Fraction);

            // The second version comes as an .sfpkg.
            var folder = WritePackage(root.FullName, "2.0.0");
            ZipFile.CreateFromDirectory(folder, folder + ".sfpkg");
            await lifecycle.UploadAndProvisionAsync(folder + ".sfpkg", null, null, ct);
            await WaitAsync(async () => (await queries.GetApplicationTypesAsync(ct)).Count(t => t.Name == TypeName && t.Status == "Available") == 2);
            Assert.Contains(await store.GetPackagesAsync(ct), p => p.Provisioned is { Name: TypeName, Version: "1.0.0" });

            var serviceType = Assert.Single(await queries.GetServiceTypesAsync(TypeName, "1.0.0", ct));
            Assert.Equal((ServiceTypeName, false), (serviceType.Name, serviceType.Stateful));

            await lifecycle.CreateApplicationAsync(AppName, TypeName, "1.0.0", new Dictionary<string, string>(), ct);
            var app = await WaitForAppAsync(queries, a => a.Status == "Ready");
            await Assert.ThrowsAsync<RefusedException>(() => lifecycle.UnprovisionAsync(TypeName, "1.0.0", ct));

            await lifecycle.CreateServiceAsync(app.Id, new NewService("Svc", ServiceTypeName, false, 1), ct);
            var service = Assert.Single(await queries.GetServicesAsync(app.Id, ct));
            Assert.Equal(AppName + "/Svc", service.Name);

            var settings = new ServiceSettings(client);
            await settings.UpdateAsync(service.Id, new Dictionary<string, string> { ["instance-close-delay"] = "10s", ["move-cost"] = "high" }, ct);
            var edited = (await settings.GetAsync(service.Id, ct)).Settings.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal(("10", "High"), (edited["instance-close-delay"], edited["move-cost"]));

            await lifecycle.UpgradeAsync(app.Id, new("1.0.0", UpgradePlan.UnmonitoredAuto, new Dictionary<string, string> { ["Greeting"] = "hello" }), ct);
            await WaitAsync(async () => (await lifecycle.GetParametersAsync(app.Id, ct)).GetValueOrDefault("Greeting") == "hello"
                && (await WaitForAppAsync(queries, _ => true)).Status == "Ready");

            await lifecycle.UpgradeAsync(app.Id, new("2.0.0", UpgradePlan.UnmonitoredAuto), ct);
            var upgraded = await WaitForAppAsync(queries, a => a.TypeVersion == "2.0.0" && a.Status == "Ready");
            Assert.Equal("2.0.0", upgraded.TypeVersion);
            Assert.Equal("hello", (await lifecycle.GetParametersAsync(app.Id, ct))["Greeting"]);
        }
        finally
        {
            await CleanUpAsync(queries, lifecycle, ops, store);
            root.Delete(recursive: true);
        }
    }

    private static async Task CleanUpAsync(ClusterQueries queries, ApplicationLifecycle lifecycle, ClusterOperations ops, ImageStore store)
    {
        var ct = CancellationToken.None;
        if ((await queries.GetApplicationsAsync(ct)).FirstOrDefault(a => a.Name == AppName) is { } app)
        {
            await ops.DeleteApplicationAsync(app.Id, force: true, ct);
            await WaitAsync(async () => (await queries.GetApplicationsAsync(ct)).All(a => a.Name != AppName));
        }
        foreach (var type in (await queries.GetApplicationTypesAsync(ct)).Where(t => t.Name == TypeName))
        {
            await lifecycle.UnprovisionAsync(type.Name, type.Version, ct);
        }
        await WaitAsync(async () => (await queries.GetApplicationTypesAsync(ct)).All(t => t.Name != TypeName));
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            try
            {
                await store.DeleteAsync($"{TypeName}_{version}", ct);
            }
            catch (Exception ex) when (ex is NotFoundException or Microsoft.ServiceFabric.Common.Exceptions.ServiceFabricException)
            {
            }
        }
    }

    private static async Task<ApplicationRow> WaitForAppAsync(ClusterQueries queries, Func<ApplicationRow, bool> done)
    {
        ApplicationRow? app = null;
        await WaitAsync(async () =>
            (app = (await queries.GetApplicationsAsync(CancellationToken.None)).FirstOrDefault(a => a.Name == AppName)) is { } found && done(found));
        return app!;
    }

    private static async Task WaitAsync(Func<Task<bool>> done)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await done())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the cluster");
            await Task.Delay(1000);
        }
    }

    /// <summary>A stateless guest executable that just stays up; only the application version differs.</summary>
    private static string WritePackage(string root, string version)
    {
        var folder = Path.Combine(root, $"SfxLifecycleTest.{version}");
        var code = Directory.CreateDirectory(Path.Combine(folder, "Pkg", "Code")).FullName;
        File.WriteAllText(Path.Combine(code, "run.cmd"), "@ping -n 100000 127.0.0.1 > nul\r\n");
        File.WriteAllText(Path.Combine(folder, "Pkg", "ServiceManifest.xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <ServiceManifest Name="Pkg" Version="1.0.0" xmlns="http://schemas.microsoft.com/2011/01/fabric">
              <ServiceTypes>
                <StatelessServiceType ServiceTypeName="{ServiceTypeName}" UseImplicitHost="true" />
              </ServiceTypes>
              <CodePackage Name="Code" Version="1.0.0">
                <EntryPoint>
                  <ExeHost>
                    <Program>run.cmd</Program>
                    <WorkingFolder>CodePackage</WorkingFolder>
                  </ExeHost>
                </EntryPoint>
              </CodePackage>
            </ServiceManifest>
            """);
        File.WriteAllText(Path.Combine(folder, "ApplicationManifest.xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <ApplicationManifest ApplicationTypeName="{TypeName}" ApplicationTypeVersion="{version}" xmlns="http://schemas.microsoft.com/2011/01/fabric">
              <Parameters>
                <Parameter Name="Greeting" DefaultValue="hi" />
              </Parameters>
              <ServiceManifestImport>
                <ServiceManifestRef ServiceManifestName="Pkg" ServiceManifestVersion="1.0.0" />
              </ServiceManifestImport>
            </ApplicationManifest>
            """);
        return folder;
    }
}
