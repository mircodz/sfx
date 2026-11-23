using System.IO.Compression;
using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Tests;

public class UpgradePlanTests
{
    [Fact]
    public void Lists_the_four_plans_safest_first()
    {
        Assert.Equal(["monitored-rollback", "monitored-manual", "unmonitored-auto", "unmonitored-manual"], UpgradePlan.All.Select(p => p.Id));
        Assert.Equal(
            ["Monitored, roll back on failure", "Monitored, stop on failure", "Unmonitored, automatic", "Unmonitored, manual"],
            UpgradePlan.All.Select(p => p.Title));
        Assert.Equal(["safest", "pauses for you", "no health checks", "resume each domain"], UpgradePlan.All.Select(p => p.Hint));
    }

    [Fact]
    public void Plans_map_to_modes_and_failure_actions()
    {
        Assert.Equal((UpgradeMode.Monitored, FailureAction.Rollback), (UpgradePlan.MonitoredRollback.Mode, UpgradePlan.MonitoredRollback.OnFailure));
        Assert.Equal((UpgradeMode.Monitored, FailureAction.Manual), (UpgradePlan.MonitoredManual.Mode, UpgradePlan.MonitoredManual.OnFailure));
        Assert.Equal((UpgradeMode.UnmonitoredAuto, (FailureAction?)null), (UpgradePlan.UnmonitoredAuto.Mode, UpgradePlan.UnmonitoredAuto.OnFailure));
        Assert.Equal((UpgradeMode.UnmonitoredManual, (FailureAction?)null), (UpgradePlan.UnmonitoredManual.Mode, UpgradePlan.UnmonitoredManual.OnFailure));
        Assert.True(UpgradePlan.MonitoredManual.Monitored);
        Assert.False(UpgradePlan.UnmonitoredManual.Monitored);
    }

    [Fact]
    public void Finds_a_plan_by_id_and_refuses_others()
    {
        Assert.Same(UpgradePlan.UnmonitoredAuto, UpgradePlan.Find("unmonitored-auto"));

        var ex = Assert.Throws<RefusedException>(() => UpgradePlan.Find("yolo"));
        Assert.Equal("Unknown upgrade mode 'yolo'.", ex.Message);
    }

    [Fact]
    public void Recognizes_the_plan_of_a_running_upgrade()
    {
        Assert.Same(UpgradePlan.MonitoredManual, UpgradePlan.Of(UpgradeMode.Monitored, FailureAction.Manual));
        Assert.Same(UpgradePlan.UnmonitoredManual, UpgradePlan.Of(UpgradeMode.UnmonitoredManual, null));
        Assert.Null(UpgradePlan.Of(null, null));
    }

    [Fact]
    public void A_plan_change_sets_mode_and_failure_action()
    {
        var update = UpgradePlan.MonitoredManual.UpdateDescription();

        Assert.Equal(UpgradeMode.Monitored, update.RollingUpgradeMode);
        Assert.Equal(FailureAction.Manual, update.FailureAction);
    }
}

public class ApplicationLifecycleTests
{
    private const string Manifest = """
        <ApplicationManifest xmlns="http://schemas.microsoft.com/2011/01/fabric" ApplicationTypeName="ShopType" ApplicationTypeVersion="2.0.0">
          <Parameters>
            <Parameter Name="Counter_TargetReplicaSetSize" DefaultValue="3" />
            <Parameter Name="Counter_PartitionCount" DefaultValue="1" />
            <Parameter Name="Greeting" DefaultValue="hi" />
          </Parameters>
          <DefaultServices>
            <Service Name="Counter">
              <StatefulService ServiceTypeName="CounterType" TargetReplicaSetSize="[Counter_TargetReplicaSetSize]" MinReplicaSetSize="2">
                <UniformInt64Partition PartitionCount="[Counter_PartitionCount]" LowKey="0" HighKey="1023" />
              </StatefulService>
            </Service>
          </DefaultServices>
        </ApplicationManifest>
        """;

    private static FakeCluster Shop(bool defaultServicesUpgrade = true)
    {
        var defaults1 = new Dictionary<string, string> { ["Counter_TargetReplicaSetSize"] = "3", ["Counter_PartitionCount"] = "1", ["Greeting"] = "hi", ["Old"] = "o" };
        var defaults2 = new Dictionary<string, string> { ["Counter_TargetReplicaSetSize"] = "3", ["Counter_PartitionCount"] = "1", ["Greeting"] = "hi" };
        return new FakeCluster()
            .On("GetApplicationInfoAsync", new ApplicationInfo(
                "shop", new ApplicationName("fabric:/shop"), "ShopType", "1.0.0", ApplicationStatus.Ready,
                new Dictionary<string, string> { ["Greeting"] = "hey", ["Old"] = "x" }))
            .Paged("GetApplicationTypeInfoListByNameAsync",
                new ApplicationTypeInfo("ShopType", "1.0.0", defaults1, ApplicationTypeStatus.Available),
                new ApplicationTypeInfo("ShopType", "2.0.0", defaults2, ApplicationTypeStatus.Available))
            .Paged<ServiceInfo>("GetServiceInfoListAsync", new StatefulServiceInfo("shop~Counter", new ServiceName("fabric:/shop/Counter"), "CounterType"))
            .On("GetApplicationManifestAsync", new ApplicationTypeManifest(Manifest))
            .On("GetClusterManifestAsync", new ClusterManifest($"""
                <ClusterManifest xmlns="http://schemas.microsoft.com/2011/01/fabric">
                  <FabricSettings><Section Name="ClusterManager"><Parameter Name="EnableDefaultServicesUpgrade" Value="{(defaultServicesUpgrade ? "true" : "false")}" /></Section></FabricSettings>
                </ClusterManifest>
                """))
            .OnCall("StartApplicationUpgradeAsync");
    }

    [Fact]
    public async Task An_upgrade_keeps_parameters_the_target_still_has_and_applies_changes()
    {
        var cluster = Shop();
        var request = new ApplicationUpgradeRequest("2.0.0", UpgradePlan.MonitoredRollback, new Dictionary<string, string> { ["Counter_TargetReplicaSetSize"] = "5" });

        await new ApplicationLifecycle(cluster.Client).UpgradeAsync("shop", request, default);

        var args = Assert.Single(cluster.CallsTo("StartApplicationUpgradeAsync"));
        Assert.Equal("shop", args[0]);
        var description = Assert.IsType<ApplicationUpgradeDescription>(args[1]);
        Assert.Equal("fabric:/shop", description.Name);
        Assert.Equal("2.0.0", description.TargetApplicationTypeVersion);
        Assert.Equal(UpgradeMode.Monitored, description.RollingUpgradeMode);
        Assert.Equal(FailureAction.Rollback, description.MonitoringPolicy.FailureAction);
        Assert.Equal(new Dictionary<string, string> { ["Greeting"] = "hey", ["Counter_TargetReplicaSetSize"] = "5" }, description.Parameters);
    }

    [Fact]
    public async Task An_upgrade_refuses_a_version_that_is_not_provisioned()
    {
        var cluster = Shop();

        var ex = await Assert.ThrowsAsync<RefusedException>(() =>
            new ApplicationLifecycle(cluster.Client).UpgradeAsync("shop", new("3.0.0", UpgradePlan.MonitoredRollback), default));

        Assert.Equal("Version 3.0.0 of ShopType isn't provisioned.", ex.Message);
        Assert.False(ex.Conflict);
        Assert.Empty(cluster.CallsTo("StartApplicationUpgradeAsync"));
    }

    [Fact]
    public async Task An_upgrade_refuses_repartitioning_an_existing_default_service()
    {
        var cluster = Shop();
        var request = new ApplicationUpgradeRequest("2.0.0", UpgradePlan.MonitoredRollback, new Dictionary<string, string> { ["Counter_PartitionCount"] = "4" });

        var ex = await Assert.ThrowsAsync<RefusedException>(() => new ApplicationLifecycle(cluster.Client).UpgradeAsync("shop", request, default));

        Assert.StartsWith("Counter_PartitionCount sets how Counter is partitioned (PartitionCount)", ex.Message);
        Assert.True(ex.Conflict);
    }

    [Fact]
    public async Task An_upgrade_says_to_scale_when_the_cluster_cannot_change_default_services()
    {
        var cluster = Shop(defaultServicesUpgrade: false);
        var request = new ApplicationUpgradeRequest("1.0.0", UpgradePlan.UnmonitoredAuto, new Dictionary<string, string> { ["Counter_TargetReplicaSetSize"] = "5" });

        var ex = await Assert.ThrowsAsync<RefusedException>(() => new ApplicationLifecycle(cluster.Client).UpgradeAsync("shop", request, default));

        Assert.EndsWith("(ClusterManager/EnableDefaultServicesUpgrade is off). Scale Counter instead.", ex.Message);
        Assert.True(ex.Conflict);
    }

    [Fact]
    public async Task An_upgrade_uses_the_callers_default_services_setting()
    {
        var cluster = Shop(defaultServicesUpgrade: false);
        var request = new ApplicationUpgradeRequest("1.0.0", UpgradePlan.UnmonitoredAuto, new Dictionary<string, string> { ["Counter_TargetReplicaSetSize"] = "5" });

        await new ApplicationLifecycle(cluster.Client, _ => Task.FromResult(true)).UpgradeAsync("shop", request, default);

        Assert.Single(cluster.CallsTo("StartApplicationUpgradeAsync"));
        Assert.Empty(cluster.CallsTo("GetClusterManifestAsync"));
    }

    [Fact]
    public async Task An_upgrade_refuses_monitoring_settings_on_an_unmonitored_plan()
    {
        var request = new ApplicationUpgradeRequest("2.0.0", UpgradePlan.UnmonitoredAuto, Policy: new UpgradePolicy(HealthCheckWait: TimeSpan.FromSeconds(30)));

        var ex = await Assert.ThrowsAsync<RefusedException>(() => new ApplicationLifecycle(Shop().Client).UpgradeAsync("shop", request, default));

        Assert.Equal("Timeouts and health checks only apply to monitored upgrades.", ex.Message);
    }

    [Fact]
    public void Merging_keeps_what_the_target_defines_and_applies_sets_and_resets()
    {
        var current = new Dictionary<string, string> { ["Greeting"] = "hey", ["OldOnly"] = "x", ["Count"] = "2" };
        var targetDefaults = new Dictionary<string, string> { ["Greeting"] = "hi", ["Count"] = "1", ["NewOne"] = "n" };

        var merged = ApplicationLifecycle.MergeParameters(current, targetDefaults, new Dictionary<string, string> { ["NewOne"] = "y" }, ["Count"], "2.0");

        Assert.Equal(["OldOnly"], merged.Dropped);
        Assert.Equal(new Dictionary<string, string> { ["Greeting"] = "hey", ["NewOne"] = "y" }, merged.Parameters);
    }

    [Fact]
    public void Merging_refuses_unknown_and_contradictory_parameters()
    {
        var defaults = new Dictionary<string, string> { ["A"] = "1" };

        var unknown = Assert.Throws<RefusedException>(() =>
            ApplicationLifecycle.MergeParameters(defaults, defaults, new Dictionary<string, string> { ["B"] = "2" }, [], "2.0"));
        Assert.Equal("Version 2.0 has no parameter 'B'.", unknown.Message);

        var both = Assert.Throws<RefusedException>(() =>
            ApplicationLifecycle.MergeParameters(defaults, defaults, new Dictionary<string, string> { ["A"] = "2" }, ["A"], "2.0"));
        Assert.Equal("'A' can't be both set and reset to its default.", both.Message);
    }

    [Fact]
    public void Describes_an_upgrade_like_the_browser_did()
    {
        var request = new ApplicationUpgradeRequest(
            "2.0.0", UpgradePlan.MonitoredManual, new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" }, ["C"], new UpgradePolicy(ForceRestart: true));

        Assert.Equal(
            "Upgrade fabric:/shop to 2.0.0 (monitored, stop on failure), setting A, B, resetting C, custom policy",
            ApplicationLifecycle.Describe("fabric:/shop", request));
        Assert.Equal("Upgrade fabric:/shop to 1.0 (unmonitored, automatic)", ApplicationLifecycle.Describe("fabric:/shop", new("1.0", UpgradePlan.UnmonitoredAuto)));
    }

    [Fact]
    public void Parses_application_parameters()
    {
        var parameters = ApplicationLifecycle.ParseParameters("a=1; b = x=y\nc=");

        Assert.Equal("1", parameters["a"]);
        Assert.Equal("x=y", parameters["b"]);
        Assert.Equal("", parameters["c"]);
        Assert.Empty(ApplicationLifecycle.ParseParameters("  "));
        Assert.Equal("'oops' isn't name=value.", Assert.Throws<RefusedException>(() => ApplicationLifecycle.ParseParameters("a=1; oops")).Message);
    }

    [Fact]
    public async Task Creating_an_application_checks_its_parameters_against_the_type()
    {
        var cluster = Shop().OnCall("CreateApplicationAsync");
        var lifecycle = new ApplicationLifecycle(cluster.Client);

        var ex = await Assert.ThrowsAsync<RefusedException>(() =>
            lifecycle.CreateApplicationAsync("shop2", "ShopType", "2.0.0", new Dictionary<string, string> { ["Old"] = "x" }, default));
        Assert.Equal("ShopType 2.0.0 has no parameter 'Old'.", ex.Message);

        var missing = await Assert.ThrowsAsync<RefusedException>(() =>
            lifecycle.CreateApplicationAsync("shop2", "ShopType", "9.0", new Dictionary<string, string>(), default));
        Assert.Equal("Version 9.0 of ShopType isn't provisioned.", missing.Message);

        await lifecycle.CreateApplicationAsync("shop2", "ShopType", "2.0.0", new Dictionary<string, string> { ["Greeting"] = "yo" }, default);
        var description = Assert.IsType<ApplicationDescription>(Assert.Single(cluster.CallsTo("CreateApplicationAsync"))[0]);
        Assert.Equal("fabric:/shop2", description.Name.ToString());
        Assert.Equal("2.0.0", description.TypeVersion);
    }

    [Fact]
    public async Task Unprovisioning_refuses_a_version_applications_still_use()
    {
        var cluster = new FakeCluster()
            .On("GetApplicationHealthAsync", new ApplicationHealth(HealthState.Ok))
            .Paged("GetApplicationInfoListAsync",
                new ApplicationInfo("a", new ApplicationName("fabric:/a"), "ShopType", "1.0.0"),
                new ApplicationInfo("b", new ApplicationName("fabric:/b"), "ShopType", "1.0.0"),
                new ApplicationInfo("c", new ApplicationName("fabric:/c"), "ShopType", "2.0.0"))
            .OnCall("UnprovisionApplicationTypeAsync");
        var lifecycle = new ApplicationLifecycle(cluster.Client);

        var ex = await Assert.ThrowsAsync<RefusedException>(() => lifecycle.UnprovisionAsync("ShopType", "1.0.0", default));
        Assert.Equal("ShopType 1.0.0 is still used by fabric:/a, fabric:/b. Upgrade or delete them first.", ex.Message);
        Assert.True(ex.Conflict);

        await Assert.ThrowsAsync<RefusedException>(() => lifecycle.UnprovisionAsync("ShopType", "2.0.0", default));
        await lifecycle.UnprovisionAsync("ShopType", "3.0.0", default);
        var args = Assert.Single(cluster.CallsTo("UnprovisionApplicationTypeAsync"));
        Assert.Equal("ShopType", args[0]);
        Assert.Equal("3.0.0", Assert.IsType<UnprovisionApplicationTypeDescriptionInfo>(args[1]).ApplicationTypeVersion);
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(2, 1)]
    [InlineData(1, 1)]
    public void New_stateful_services_keep_a_write_quorum(int target, int min) =>
        Assert.Equal(min, new NewService("S", "SType", true, target).MinReplicas);

    [Fact]
    public void A_new_service_is_singleton_or_uniform_over_all_keys()
    {
        var stateful = Assert.IsType<StatefulServiceDescription>(
            ApplicationLifecycle.Description("fabric:/shop", new NewService("Counter", "CounterType", true, 3, Partitions: 4)));
        Assert.Equal("fabric:/shop/Counter", stateful.ServiceName.ToString());
        Assert.Equal(3, stateful.TargetReplicaSetSize);
        Assert.Equal(2, stateful.MinReplicaSetSize);
        Assert.True(stateful.HasPersistedState);
        var range = Assert.IsType<UniformInt64RangePartitionSchemeDescription>(stateful.PartitionDescription);
        Assert.Equal(4, range.Count);
        Assert.Equal(long.MinValue.ToString(), range.LowKey);
        Assert.Equal(long.MaxValue.ToString(), range.HighKey);

        var stateless = Assert.IsType<StatelessServiceDescription>(
            ApplicationLifecycle.Description("fabric:/shop", new NewService("fabric:/shop/Web", "WebType", false, -1)));
        Assert.Equal("fabric:/shop/Web", stateless.ServiceName.ToString());
        Assert.Equal(-1, stateless.InstanceCount);
        Assert.IsType<SingletonPartitionSchemeDescription>(stateless.PartitionDescription);
    }

    [Fact]
    public async Task Creating_a_service_checks_its_type()
    {
        var cluster = Shop()
            .On("GetServiceTypeInfoListAsync", new[] { new ServiceTypeInfo(new StatefulServiceTypeDescription(true, "CounterType"), "CounterPkg", "1.0.0") })
            .OnCall("CreateServiceAsync");
        var lifecycle = new ApplicationLifecycle(cluster.Client);

        var missing = await Assert.ThrowsAsync<RefusedException>(() => lifecycle.CreateServiceAsync("shop", new("Web", "WebType", false, 1), default));
        Assert.Equal("ShopType 1.0.0 has no service type WebType.", missing.Message);

        var kind = await Assert.ThrowsAsync<RefusedException>(() => lifecycle.CreateServiceAsync("shop", new("Counter2", "CounterType", false, 1), default));
        Assert.Equal("CounterType is a stateful service type.", kind.Message);

        var count = await Assert.ThrowsAsync<RefusedException>(() => lifecycle.CreateServiceAsync("shop", new("Counter2", "CounterType", true, 0), default));
        Assert.Equal("A stateful service needs at least 1 replica.", count.Message);

        await lifecycle.CreateServiceAsync("shop", new("Counter2", "CounterType", true, 3), default);
        var args = Assert.Single(cluster.CallsTo("CreateServiceAsync"));
        Assert.Equal("shop", args[0]);
        Assert.Equal("fabric:/shop/Counter2", Assert.IsType<StatefulServiceDescription>(args[1]).ServiceName.ToString());
    }

    [Fact]
    public async Task Resuming_the_next_domain_needs_a_waiting_upgrade()
    {
        var cluster = new FakeCluster()
            .On("GetApplicationUpgradeAsync", new ApplicationUpgradeProgressInfo(upgradeState: UpgradeState.RollingForwardPending, nextUpgradeDomain: "UD2"))
            .OnCall("ResumeApplicationUpgradeAsync");

        Assert.Equal("UD2", await new ApplicationLifecycle(cluster.Client).ResumeNextUpgradeDomainAsync("shop", default));
        Assert.Equal("UD2", Assert.IsType<ResumeApplicationUpgradeDescription>(Assert.Single(cluster.CallsTo("ResumeApplicationUpgradeAsync"))[1]).UpgradeDomainName);

        cluster.On("GetApplicationUpgradeAsync", new ApplicationUpgradeProgressInfo(upgradeState: UpgradeState.RollingForwardInProgress, nextUpgradeDomain: "UD3"));
        var ex = await Assert.ThrowsAsync<RefusedException>(() => new ApplicationLifecycle(cluster.Client).ResumeNextUpgradeDomainAsync("shop", default));
        Assert.Equal("The upgrade of fabric:/shop isn't waiting to start an upgrade domain.", ex.Message);
    }

    [Fact]
    public async Task Uploading_refuses_a_version_already_provisioned()
    {
        var folder = LocalPackageTests.PackageFolder("ShopType", "1.0.0");
        try
        {
            var ex = await Assert.ThrowsAsync<RefusedException>(() =>
                new ApplicationLifecycle(Shop().Client).UploadAndProvisionAsync(folder, null, null, default));
            Assert.Equal("ShopType 1.0.0 is already provisioned. Unprovision it first, or give the package a new version.", ex.Message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Uploading_a_package_copies_it_and_provisions_it()
    {
        var folder = LocalPackageTests.PackageFolder("ShopType", "3.0.0");
        var cluster = Shop()
            .On("GetImageStoreConnectionStringAsync", "fabric:ImageStore")
            .OnCall("UploadFileAsync")
            .OnCall("ProvisionApplicationTypeAsync");
        var progress = new List<UploadProgress>();
        try
        {
            var package = await new ApplicationLifecycle(cluster.Client).UploadAndProvisionAsync(folder, null, new SyncProgress(progress), default);

            Assert.Equal(new ApplicationPackage("ShopType", "3.0.0", "ShopType_3.0.0"), package);
            var paths = cluster.CallsTo("UploadFileAsync").Select(a => (string)a[1]!).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(
                ["ShopType_3.0.0/ApplicationManifest.xml", "ShopType_3.0.0/Svc/ServiceManifest.xml", "ShopType_3.0.0/Svc/_.dir", "ShopType_3.0.0/_.dir"],
                paths);
            var provision = Assert.IsType<ProvisionApplicationTypeDescription>(Assert.Single(cluster.CallsTo("ProvisionApplicationTypeAsync"))[0]);
            Assert.Equal("ShopType_3.0.0", provision.ApplicationTypeBuildPath);
            Assert.True(provision.Async);
            Assert.Equal((2, 2), (progress[^1].FilesDone, progress[^1].FilesTotal));
            Assert.Equal(1, progress[^1].Fraction);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>Collects progress as it's reported, unlike <see cref="Progress{T}"/>, which posts it later.</summary>
internal sealed class SyncProgress(List<UploadProgress> reports) : IProgress<UploadProgress>
{
    public void Report(UploadProgress value)
    {
        lock (reports)
        {
            reports.Add(value);
        }
    }
}

public class LocalPackageTests
{
    internal static string Manifest(string name, string version) =>
        $"""<ApplicationManifest xmlns="http://schemas.microsoft.com/2011/01/fabric" ApplicationTypeName="{name}" ApplicationTypeVersion="{version}" />""";

    /// <summary>A package folder with a manifest and one service package, in the test's own scratch folder.</summary>
    internal static string PackageFolder(string name, string version)
    {
        var folder = Directory.CreateTempSubdirectory("sfx-test-package-").FullName;
        File.WriteAllText(Path.Combine(folder, LocalPackage.ManifestFile), Manifest(name, version));
        Directory.CreateDirectory(Path.Combine(folder, "Svc"));
        File.WriteAllText(Path.Combine(folder, "Svc", "ServiceManifest.xml"), "<ServiceManifest />");
        return folder;
    }

    [Fact]
    public void Opens_a_package_folder_in_place()
    {
        var folder = PackageFolder("ShopType", "1.2.0");
        try
        {
            using (var package = LocalPackage.Open(folder + Path.DirectorySeparatorChar))
            {
                Assert.Equal(folder, package.Folder);
                Assert.Equal(("ShopType", "1.2.0"), (package.TypeName, package.TypeVersion));
                Assert.Equal("ShopType_1.2.0", package.StorePath);
            }
            Assert.True(Directory.Exists(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(".sfpkg")]
    [InlineData(".zip")]
    public void Extracts_a_package_file_and_removes_it_when_done(string extension)
    {
        var folder = PackageFolder("ShopType", "1.2.0");
        var file = folder + extension;
        try
        {
            ZipFile.CreateFromDirectory(folder, file);
            string extracted;
            using (var package = LocalPackage.Open(file))
            {
                extracted = package.Folder;
                Assert.NotEqual(folder, extracted);
                Assert.Equal("ShopType", package.TypeName);
                Assert.True(File.Exists(Path.Combine(extracted, "Svc", "ServiceManifest.xml")));
            }
            Assert.False(Directory.Exists(extracted));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
            File.Delete(file);
        }
    }

    [Fact]
    public void Points_at_the_package_when_it_is_zipped_inside_a_folder()
    {
        var folder = PackageFolder("ShopType", "1.2.0");
        var file = folder + ".zip";
        try
        {
            ZipFile.CreateFromDirectory(folder, file, CompressionLevel.Fastest, includeBaseDirectory: true);

            var ex = Assert.Throws<RefusedException>(() => LocalPackage.Open(file));

            Assert.Equal($"{file} has no ApplicationManifest.xml at its root, but {Path.GetFileName(folder)}/ has one: use that folder, or package what's inside it.", ex.Message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
            File.Delete(file);
        }
    }

    [Fact]
    public void Refuses_what_is_not_a_package()
    {
        var folder = Directory.CreateTempSubdirectory("sfx-test-package-").FullName;
        var file = Path.Combine(folder, "notes.zip");
        try
        {
            Assert.Equal(
                $"{folder} has no ApplicationManifest.xml at its root, so it isn't an application package.",
                Assert.Throws<RefusedException>(() => LocalPackage.Open(folder)).Message);

            File.WriteAllText(file, "not a zip");
            Assert.Equal("notes.zip isn't a package folder, an .sfpkg or a .zip file.", Assert.Throws<RefusedException>(() => LocalPackage.Open(file)).Message);

            var missing = Path.Combine(folder, "nothing");
            Assert.Equal($"There's no folder or file {missing}.", Assert.Throws<RefusedException>(() => LocalPackage.Open(missing)).Message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Refuses_a_manifest_without_type_and_version()
    {
        Assert.Contains("isn't an application manifest", Assert.Throws<RefusedException>(() =>
            LocalPackage.ReadManifest("<ApplicationManifest ApplicationTypeName=\"A\" />", "here")).Message);
        Assert.Contains("isn't valid XML", Assert.Throws<RefusedException>(() => LocalPackage.ReadManifest("<oops", "here")).Message);
        Assert.Equal(("A", "1"), LocalPackage.ReadManifest(Manifest("A", "1"), "here"));
    }
}
