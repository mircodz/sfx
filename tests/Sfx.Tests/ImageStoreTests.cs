using Microsoft.ServiceFabric.Client.Exceptions;
using Microsoft.ServiceFabric.Common;
using Microsoft.ServiceFabric.Common.Exceptions;
using Sfx.Core;
using StoreFile = Microsoft.ServiceFabric.Common.FileInfo;

namespace Sfx.Tests;

public class ImageStoreTests
{
    private static ApplicationTypeRow Type(string name, string version, string status = "Available") =>
        new(name, version, status, new Dictionary<string, string>());

    [Theory]
    [InlineData("fabric:ImageStore", ImageStoreKind.Native)]
    [InlineData(" FABRIC:IMAGESTORE ", ImageStoreKind.Native)]
    [InlineData(@"file:C:\SfDevCluster\Data\ImageStoreShare", ImageStoreKind.FileShare)]
    [InlineData("xstore:DefaultEndpointsProtocol=https;AccountName=x;Container=y", ImageStoreKind.AzureBlob)]
    [InlineData("something", ImageStoreKind.Unknown)]
    public void Recognizes_the_kind_of_store(string connection, ImageStoreKind kind) => Assert.Equal(kind, ImageStore.KindOf(connection));

    [Theory]
    [InlineData("Shop Type_1.0", "Shop_Type_1.0")]
    [InlineData("a/b\\c:d", "a_b_c_d")]
    [InlineData("Ok-1.2_3", "Ok-1.2_3")]
    public void Makes_names_safe_for_the_store(string name, string safe) => Assert.Equal(safe, ImageStore.SafeName(name));

    [Theory]
    [InlineData(null, "", "")]
    [InlineData(" /Shop/Code/ ", "Shop/Code", "Code")]
    [InlineData(@"Store\ShopType\x.xml", @"Store\ShopType\x.xml", "x.xml")]
    public void Normalizes_paths(string? path, string normal, string name)
    {
        Assert.Equal(normal, ImageStore.Normalize(path));
        Assert.Equal(name, ImageStore.NameOf(path));
    }

    [Theory]
    [InlineData("Store", true)]
    [InlineData(@"store\ShopType", true)]
    [InlineData("WindowsFabricStore/x", true)]
    [InlineData("Storefront", false)]
    [InlineData("Shop", false)]
    public void Knows_the_clusters_own_folders(string path, bool reserved) => Assert.Equal(reserved, ImageStore.IsReserved(path));

    [Fact]
    public void Reads_versions_from_provisioned_manifest_names()
    {
        Assert.Equal(
            ["1.10.0", "1.9.0"],
            ImageStore.ManifestVersions(["ApplicationManifest.1.9.0.xml", "ApplicationManifest.1.10.0.xml", "ServiceManifest.xml", "ApplicationManifest.xml"]));
    }

    [Theory]
    [InlineData("ShopType_1.0.0", "ShopType", "1.0.0", true)]
    [InlineData("Shop.1.0.0", "ShopType", "1.0.0", true)]
    [InlineData("ShopType", "ShopType", null, false)]
    [InlineData("ShopType_1.0.0.1", "ShopType", null, false)]
    [InlineData("Other", null, null, false)]
    [InlineData("ShopFrontType_2.0", "ShopFrontType", "2.0", true)]
    public void Matches_package_folders_to_provisioned_types(string folder, string? type, string? version, bool provisioned)
    {
        IReadOnlyList<ApplicationTypeRow> types = [Type("ShopType", "1.0.0"), Type("ShopType", "2.0"), Type("ShopFrontType", "2.0")];

        var package = ImageStore.MatchPackage(folder, types);

        Assert.Equal(folder, package.Name);
        Assert.Equal(type, package.TypeName);
        Assert.Equal(version, package.TypeVersion);
        Assert.Equal(provisioned, package.Provisioned is not null);
    }

    [Theory]
    [InlineData("App_1.0", "1.0", true)]
    [InlineData("App_1.0.1", "1.0", false)]
    [InlineData("App_11.0", "1.0", false)]
    [InlineData("App-v1.0-beta", "1.0", true)]
    [InlineData("App", "", false)]
    public void Finds_a_version_on_its_own(string text, string version, bool found) => Assert.Equal(found, ImageStore.ContainsVersion(text, version));

    [Fact]
    public async Task Lists_folders_then_files_sorted_with_what_the_store_says()
    {
        var modified = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var cluster = new FakeCluster().On("GetImageStoreContentAsync", new ImageStoreContent(
            [new StoreFile("2048", new FileVersion("7"), modified, @"Shop\ApplicationManifest.xml")],
            [new FolderInfo(@"Shop\Web", "4"), new FolderInfo(@"Shop\Api", null)]));

        var listing = await new ImageStore(cluster.Client).GetContentAsync("/Shop/", default);

        Assert.Equal("Shop", listing.Path);
        Assert.Equal([new ImageStoreFolder(@"Shop\Api", "Api", null), new ImageStoreFolder(@"Shop\Web", "Web", 4)], listing.Folders);
        Assert.Equal(new ImageStoreFile(@"Shop\ApplicationManifest.xml", "ApplicationManifest.xml", 2048, "7", modified), Assert.Single(listing.Files));
        Assert.Equal("Shop", Assert.Single(cluster.CallsTo("GetImageStoreContentAsync"))[0]);
    }

    [Fact]
    public async Task A_path_that_is_not_there_is_not_found()
    {
        var cluster = new FakeCluster().On("GetImageStoreContentAsync", _ => null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => new ImageStore(cluster.Client).GetContentAsync("Nope", default));

        Assert.Equal("There's nothing at Nope in the image store.", ex.Message);
    }

    [Fact]
    public async Task A_store_that_is_not_native_is_explained()
    {
        var cluster = new FakeCluster()
            .Throws("GetImageStoreRootContentAsync", new ServiceFabricException("not supported", FabricErrorCodes.E_INVALIDARG, false))
            .On("GetImageStoreConnectionStringAsync", "xstore:AccountName=x");

        var ex = await Assert.ThrowsAsync<RefusedException>(() => new ImageStore(cluster.Client).GetContentAsync(null, default));

        Assert.StartsWith("This cluster keeps packages in Azure blob storage, which can't be browsed through the cluster", ex.Message);
        Assert.True(ex.Conflict);
    }

    [Fact]
    public async Task Asks_folder_sizes_and_walks_the_folder_when_the_cluster_cannot_say()
    {
        var cluster = new FakeCluster()
            .On("GetImageStoreFolderSizeAsync", new FolderSizeInfo("Shop", "4096"))
            .On("GetImageStoreRootFolderSizeAsync", _ => throw new ServiceFabricRequestException("404"))
            .On("GetImageStoreRootContentAsync", new ImageStoreContent([new StoreFile("10", null, null, "a.txt")], [new FolderInfo("Shop", "1")]))
            .On("GetImageStoreContentAsync", new ImageStoreContent([new StoreFile("5", null, null, @"Shop\b.txt")], []));
        var store = new ImageStore(cluster.Client);

        Assert.Equal(4096, await store.GetFolderSizeAsync("Shop", default));
        Assert.Equal(15, await store.GetFolderSizeAsync(null, default));
    }

    [Fact]
    public async Task Deleting_refuses_the_root_the_clusters_folders_and_a_package_being_provisioned()
    {
        var cluster = new FakeCluster()
            .Paged("GetApplicationTypeInfoListAsync", new ApplicationTypeInfo("ShopType", "2.0.0", status: ApplicationTypeStatus.Provisioning))
            .OnCall("DeleteImageStoreContentAsync");
        var store = new ImageStore(cluster.Client);

        Assert.Equal("Choose what to delete: the image store's root can't be deleted.",
            (await Assert.ThrowsAsync<RefusedException>(() => store.DeleteAsync(" / ", default))).Message);
        Assert.StartsWith("Store is the cluster's own copy",
            (await Assert.ThrowsAsync<RefusedException>(() => store.DeleteAsync(@"Store\ShopType", default))).Message);
        Assert.Equal("ShopType 2.0.0 is still being provisioned from ShopType_2.0.0; wait for it to finish.",
            (await Assert.ThrowsAsync<RefusedException>(() => store.DeleteAsync("ShopType_2.0.0/Code", default))).Message);

        await store.DeleteAsync("ShopType_1.0.0", default);
        Assert.Equal("ShopType_1.0.0", Assert.Single(cluster.CallsTo("DeleteImageStoreContentAsync"))[0]);
    }

    [Fact]
    public async Task Finds_packages_and_whether_they_are_provisioned()
    {
        var cluster = new FakeCluster()
            .Paged("GetApplicationTypeInfoListAsync", new ApplicationTypeInfo("ShopType", "1.0.0", status: ApplicationTypeStatus.Available))
            .On("GetImageStoreRootContentAsync", new ImageStoreContent([], [new FolderInfo("Store"), new FolderInfo("ShopType_1.0.0"), new FolderInfo("ShopType_2.0.0"), new FolderInfo("Logs")]))
            .On("GetImageStoreContentAsync", args => (string)args[0]! switch
            {
                "Logs" => new ImageStoreContent([new StoreFile("1", null, null, @"Logs\x.log")], []),
                var folder => new ImageStoreContent([new StoreFile("1", null, null, folder + @"\ApplicationManifest.xml")], []),
            });

        var packages = await new ImageStore(cluster.Client).GetPackagesAsync(default);

        Assert.Equal(["ShopType_1.0.0", "ShopType_2.0.0"], packages.Select(p => p.Path));
        Assert.Equal("1.0.0", packages[0].Provisioned?.Version);
        Assert.Equal(("ShopType", null), (packages[1].TypeName, packages[1].TypeVersion));
        Assert.DoesNotContain(cluster.CallsTo("GetImageStoreContentAsync"), a => (string)a[0]! == "Store");
    }

    [Fact]
    public async Task Lists_the_provisioned_copies_in_the_store_folder()
    {
        var cluster = new FakeCluster().On("GetImageStoreContentAsync", args => (string)args[0]! switch
        {
            "Store" => new ImageStoreContent([], [new FolderInfo(@"Store\ShopType")]),
            _ => new ImageStoreContent([new StoreFile("1", null, null, @"Store\ShopType\ApplicationManifest.1.0.0.xml")], []),
        });

        var copy = Assert.Single(await new ImageStore(cluster.Client).GetTypeCopiesAsync(default));

        Assert.Equal((@"Store\ShopType", "ShopType"), (copy.Path, copy.TypeName));
        Assert.Equal(["1.0.0"], copy.Versions);
    }

    [Fact]
    public async Task Uploads_large_files_in_chunks_and_commits_them()
    {
        var folder = Directory.CreateTempSubdirectory("sfx-test-upload-").FullName;
        var big = new byte[9 * 1024 * 1024 + 10];
        new Random(1).NextBytes(big);
        File.WriteAllBytes(Path.Combine(folder, "big.bin"), big);
        var cluster = new FakeCluster()
            .On("GetImageStoreConnectionStringAsync", "fabric:ImageStore")
            .OnCall("UploadFileAsync")
            .OnCall("UploadFileChunkAsync")
            .OnCall("CommitImageStoreUploadSessionAsync");
        var progress = new List<UploadProgress>();
        try
        {
            await new ImageStore(cluster.Client).UploadFolderAsync(folder, "Big", new SyncProgress(progress), default);

            var chunks = cluster.CallsTo("UploadFileChunkAsync");
            Assert.Equal(2, chunks.Count);
            Assert.All(chunks, c => Assert.Equal("Big/big.bin", c[1]));
            Assert.Equal((0L, 8L * 1024 * 1024 - 1), ((long)chunks[0][3]!, (long)chunks[0][4]!));
            Assert.Equal((8L * 1024 * 1024, (long)big.Length - 1), ((long)chunks[1][3]!, (long)chunks[1][4]!));
            Assert.Equal(chunks[0][2], Assert.Single(cluster.CallsTo("CommitImageStoreUploadSessionAsync"))[0]);
            Assert.Equal(["Big/_.dir"], cluster.CallsTo("UploadFileAsync").Select(a => (string)a[1]!));
            Assert.Equal(new UploadProgress(1, 1, big.Length, big.Length), progress[^1]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_failed_chunked_upload_drops_its_session()
    {
        var folder = Directory.CreateTempSubdirectory("sfx-test-upload-").FullName;
        File.WriteAllBytes(Path.Combine(folder, "big.bin"), new byte[9 * 1024 * 1024]);
        var cluster = new FakeCluster()
            .On("GetImageStoreConnectionStringAsync", "fabric:ImageStore")
            .Throws("UploadFileChunkAsync", new ServiceFabricException("disk full", FabricErrorCodes.E_FAIL, false))
            .OnCall("DeleteImageStoreUploadSessionAsync");
        try
        {
            await Assert.ThrowsAsync<ServiceFabricException>(() => new ImageStore(cluster.Client).UploadFolderAsync(folder, "Big", null, default));

            Assert.Single(cluster.CallsTo("DeleteImageStoreUploadSessionAsync"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("", "Choose where in the image store to upload to.")]
    [InlineData("a/../b", "'a/../b' isn't a path in the image store: it can't have empty, '.' or '..' parts.")]
    [InlineData("Store/x", "Store is reserved for the cluster's own copies; upload somewhere else.")]
    public async Task Refuses_upload_paths_it_should_not_write(string path, string message)
    {
        var ex = await Assert.ThrowsAsync<RefusedException>(() => new ImageStore(new FakeCluster().Client).UploadFolderAsync(".", path, null, default));

        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public async Task Reads_disk_usage_or_nothing_when_the_cluster_cannot_say()
    {
        var cluster = new FakeCluster().On("GetImageStoreInfoAsync", new ImageStoreInfo(
            new DiskInfo("1000", "400"), new UsageInfo("10", "2"), null, null, new UsageInfo("300", "30")));

        var usage = await new ImageStore(cluster.Client).GetUsageAsync(default);

        Assert.Equal(1000, usage!.DiskCapacity);
        Assert.Equal(400, usage.DiskAvailable);
        Assert.Equal(new ImageStoreUse(10, 2), usage.Metadata);
        Assert.Equal(new ImageStoreUse(300, 30), usage.Register);

        cluster.Throws("GetImageStoreInfoAsync", new ServiceFabricRequestException("404"));
        Assert.Null(await new ImageStore(cluster.Client).GetUsageAsync(default));
    }
}
