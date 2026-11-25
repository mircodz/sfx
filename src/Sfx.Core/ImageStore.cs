using System.Globalization;
using System.Text;
using Microsoft.ServiceFabric.Client;
using Microsoft.ServiceFabric.Client.Exceptions;
using Microsoft.ServiceFabric.Common;
using Microsoft.ServiceFabric.Common.Exceptions;
using FileInfo = System.IO.FileInfo;

namespace Sfx.Core;

/// <summary>Where the cluster keeps application packages, from its ImageStoreConnectionString.</summary>
public enum ImageStoreKind
{
    /// <summary>"fabric:ImageStore": the cluster's own store, which sfx can browse, upload to and delete from.</summary>
    Native,

    /// <summary>"file:…": a folder or share, which uploads copy into directly when this machine can reach it.</summary>
    FileShare,

    /// <summary>"xstore:…": Azure blob storage, uploaded to through the cluster.</summary>
    AzureBlob,

    Unknown,
}

/// <param name="Path">Its path in the image store, as the cluster gives it (Windows clusters separate with '\').</param>
/// <param name="FileCount">How many files it holds, all the way down; null when the cluster doesn't say.</param>
/// <param name="Size">Its size in bytes, when asked for; null otherwise.</param>
public sealed record ImageStoreFolder(string Path, string Name, long? FileCount, long? Size = null);

/// <param name="Version">The image store's version number of the file; empty when it doesn't say.</param>
public sealed record ImageStoreFile(string Path, string Name, long? Size, string Version, DateTime? Modified);

/// <summary>What one image store folder holds, folders first, each sorted by name.</summary>
/// <param name="Path">The folder listed; empty for the root.</param>
public sealed record ImageStoreListing(string Path, IReadOnlyList<ImageStoreFolder> Folders, IReadOnlyList<ImageStoreFile> Files);

/// <summary>Space used for one purpose; null fields when the cluster doesn't say.</summary>
public sealed record ImageStoreUse(long? Bytes, long? Files);

/// <summary>The image store's disk and what uses it (metadata, staging, copies, registered packages).</summary>
public sealed record ImageStoreUsage(
    long? DiskCapacity,
    long? DiskAvailable,
    ImageStoreUse? Metadata,
    ImageStoreUse? Staging,
    ImageStoreUse? Copy,
    ImageStoreUse? Register);

/// <summary>How far an upload is; reported after each file and each chunk of a large one.</summary>
public sealed record UploadProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal)
{
    public double Fraction => BytesTotal > 0 ? (double)BytesDone / BytesTotal : FilesTotal > 0 ? (double)FilesDone / FilesTotal : 1;
}

/// <summary>
/// A top-level image store folder holding an application package (an ApplicationManifest.xml at its root), with the
/// type and version its name suggests. The image store can't be read back, so these come from the folder name:
/// "{type}_{version}" as sfx uploads, but also "Voting.1.0.0" or "VotingType" and the like.
/// </summary>
/// <param name="TypeName">The provisioned type the name matches; null when none does.</param>
/// <param name="TypeVersion">The provisioned version of it the name contains; null when it names none.</param>
/// <param name="Provisioned">The provisioned type version both match, with its status; null when they don't.</param>
public sealed record ImageStorePackage(string Path, string Name, string? TypeName, string? TypeVersion, ApplicationTypeRow? Provisioned);

/// <summary>
/// The cluster's own copy of a provisioned application type, in the reserved Store folder: provisioning writes an
/// ApplicationManifest.{version}.xml there per version.
/// </summary>
public sealed record ImageStoreTypeCopy(string Path, string TypeName, IReadOnlyList<string> Versions);

/// <summary>
/// The image store: browsing it, folder sizes, deleting, and uploading a local folder (as provisioning does). Only
/// the native image store can be browsed; uploads also work to a file share this machine reaches.
/// </summary>
public sealed class ImageStore(IServiceFabricClient client)
{
    /// <summary>
    /// The cluster's own folders: Store holds the copies of provisioned application types, WindowsFabricStore those
    /// of provisioned Service Fabric versions. sfx doesn't upload to or delete from them.
    /// </summary>
    public static IReadOnlyList<string> ReservedFolders { get; } = ["Store", "WindowsFabricStore"];

    private const int ChunkSize = 8 * 1024 * 1024;
    private const int Parallelism = 4;
    private const string DirectoryMarker = "_.dir";

    private Task<string>? _connectionString;

    /// <summary>The ImageStoreConnectionString from the cluster manifest; read once per instance.</summary>
    public Task<string> GetConnectionStringAsync(CancellationToken cancellationToken)
    {
        var read = _connectionString ??= client.Cluster.GetImageStoreConnectionStringAsync(cancellationToken: CancellationToken.None);
        if (read.IsFaulted || read.IsCanceled)
        {
            _connectionString = null;
        }
        return read.WaitAsync(cancellationToken);
    }

    public async Task<ImageStoreKind> GetKindAsync(CancellationToken cancellationToken) => KindOf(await GetConnectionStringAsync(cancellationToken));

    public static ImageStoreKind KindOf(string connectionString) => connectionString.Trim() switch
    {
        var s when s.Equals("fabric:ImageStore", StringComparison.OrdinalIgnoreCase) => ImageStoreKind.Native,
        var s when s.StartsWith("file:", StringComparison.OrdinalIgnoreCase) => ImageStoreKind.FileShare,
        var s when s.StartsWith("xstore:", StringComparison.OrdinalIgnoreCase) => ImageStoreKind.AzureBlob,
        _ => ImageStoreKind.Unknown,
    };

    // Browsing

    /// <summary>What a folder holds; the root when <paramref name="path"/> is null or empty.</summary>
    /// <param name="folderSizes">Also ask each folder's size (a call per folder).</param>
    public async Task<ImageStoreListing> GetContentAsync(string? path, CancellationToken cancellationToken, bool folderSizes = false)
    {
        var folder = Normalize(path);
        var content = await BrowseAsync(folder, cancellationToken);
        var folders = (content?.StoreFolders ?? [])
            .Select(f => new ImageStoreFolder(f.StoreRelativePath ?? "", NameOf(f.StoreRelativePath), Long(f.FileCount)))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var files = (content?.StoreFiles ?? [])
            .Select(f => new ImageStoreFile(
                f.StoreRelativePath ?? "",
                NameOf(f.StoreRelativePath),
                Long(f.FileSize),
                f.FileVersion?.VersionNumber ?? "",
                f.ModifiedDate))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folderSizes)
        {
            folders = (await Bounded.SelectAsync(folders, async f => f with { Size = await GetFolderSizeAsync(f.Path, cancellationToken) })).ToList();
        }
        return new ImageStoreListing(folder, folders, files);
    }

    private async Task<ImageStoreContent?> BrowseAsync(string folder, CancellationToken cancellationToken)
    {
        ImageStoreContent? content;
        try
        {
            content = folder.Length == 0
                ? await client.ImageStore.GetImageStoreRootContentAsync(cancellationToken: cancellationToken)
                : await client.ImageStore.GetImageStoreContentAsync(folder, cancellationToken: cancellationToken);
        }
        catch (ServiceFabricException ex) when (ClusterQueries.IsGone(ex))
        {
            throw new NotFoundException($"There's nothing at {folder} in the image store.", ex);
        }
        catch (Exception ex) when (ex is ServiceFabricException or ServiceFabricRequestException && !cancellationToken.IsCancellationRequested)
        {
            await RequireNativeAsync(ex, cancellationToken);
            throw;
        }
        // The cluster answers a path that isn't there with nothing at all.
        return content is null && folder.Length > 0 ? throw new NotFoundException($"There's nothing at {folder} in the image store.") : content;
    }

    /// <summary>A failure browsing a store that isn't the native one is explained as that.</summary>
    private async Task RequireNativeAsync(Exception failure, CancellationToken cancellationToken)
    {
        ImageStoreKind kind;
        try
        {
            kind = await GetKindAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return;
        }
        if (kind is not ImageStoreKind.Native)
        {
            throw new RefusedException(
                $"This cluster keeps packages in {Describe(kind)}, which can't be browsed through the cluster; only the native image store (fabric:ImageStore) can.",
                conflict: true,
                failure);
        }
    }

    /// <summary>
    /// A folder's size in bytes, all the way down; the whole store's when <paramref name="path"/> is null or empty.
    /// Clusters before Service Fabric 6.5 can't say, so there the folder is walked and its files added up.
    /// </summary>
    public async Task<long> GetFolderSizeAsync(string? path, CancellationToken cancellationToken)
    {
        var folder = Normalize(path);
        try
        {
            var size = folder.Length == 0
                ? await client.ImageStore.GetImageStoreRootFolderSizeAsync(cancellationToken: cancellationToken)
                : await client.ImageStore.GetImageStoreFolderSizeAsync(folder, cancellationToken: cancellationToken);
            if (Long(size?.FolderSize) is { } bytes)
            {
                return bytes;
            }
        }
        catch (ServiceFabricException ex) when (ClusterQueries.IsGone(ex))
        {
            throw new NotFoundException($"There's nothing at {folder} in the image store.", ex);
        }
        catch (Exception ex) when (ex is ServiceFabricException or ServiceFabricRequestException && !cancellationToken.IsCancellationRequested)
        {
        }
        return await WalkSizeAsync(folder, cancellationToken);
    }

    private async Task<long> WalkSizeAsync(string folder, CancellationToken cancellationToken)
    {
        var content = await GetContentAsync(folder, cancellationToken);
        var below = await Bounded.SelectAsync(content.Folders, f => WalkSizeAsync(f.Path, cancellationToken));
        return content.Files.Sum(f => f.Size ?? 0) + below.Sum();
    }

    /// <summary>The image store's disk and what uses it; null when the cluster can't say (before Service Fabric 7.1).</summary>
    public async Task<ImageStoreUsage?> GetUsageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await client.ImageStore.GetImageStoreInfoAsync(cancellationToken: cancellationToken);
            static ImageStoreUse? Use(UsageInfo? u) => u is null ? null : new ImageStoreUse(Long(u.UsedSpace), Long(u.FileCount));
            return info is null
                ? null
                : new ImageStoreUsage(
                    Long(info.DiskInfo?.Capacity),
                    Long(info.DiskInfo?.AvailableSpace),
                    Use(info.UsedByMetadata),
                    Use(info.UsedByStaging),
                    Use(info.UsedByCopy),
                    Use(info.UsedByRegister));
        }
        catch (Exception ex) when (ex is ServiceFabricException or ServiceFabricRequestException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // What's in it

    /// <summary>
    /// The top-level folders that hold an application package, each matched to a provisioned type version by its name
    /// where it can be. A folder per package is listed, so this is a call per top-level folder.
    /// </summary>
    public async Task<IReadOnlyList<ImageStorePackage>> GetPackagesAsync(CancellationToken cancellationToken)
    {
        var types = new ClusterQueries(client).GetApplicationTypesAsync(cancellationToken);
        var root = await GetContentAsync(null, cancellationToken);
        var candidates = root.Folders.Where(f => !IsReserved(f.Path)).ToList();
        var packages = await Bounded.SelectAsync(candidates, async f =>
        {
            try
            {
                var content = await GetContentAsync(f.Path, cancellationToken);
                return content.Files.Any(file => file.Name.Equals(LocalPackage.ManifestFile, StringComparison.OrdinalIgnoreCase)) ? f : null;
            }
            // Deleted since the root was listed.
            catch (NotFoundException)
            {
                return null;
            }
        });
        var provisioned = await types;
        return packages.OfType<ImageStoreFolder>().Select(f => MatchPackage(f.Path, provisioned)).ToList();
    }

    /// <summary>The provisioned types' copies in the reserved Store folder, with the versions each holds.</summary>
    public async Task<IReadOnlyList<ImageStoreTypeCopy>> GetTypeCopiesAsync(CancellationToken cancellationToken)
    {
        ImageStoreListing store;
        try
        {
            store = await GetContentAsync(ReservedFolders[0], cancellationToken);
        }
        catch (NotFoundException)
        {
            return [];
        }
        var copies = await Bounded.SelectAsync(store.Folders, async f =>
        {
            try
            {
                var content = await GetContentAsync(f.Path, cancellationToken);
                return new ImageStoreTypeCopy(f.Path, f.Name, ManifestVersions(content.Files.Select(file => file.Name)));
            }
            catch (NotFoundException)
            {
                return null;
            }
        });
        return copies.OfType<ImageStoreTypeCopy>().ToList();
    }

    /// <summary>The versions "ApplicationManifest.{version}.xml" files name, newest first.</summary>
    public static IReadOnlyList<string> ManifestVersions(IEnumerable<string> fileNames) => fileNames
        .Where(n => n.Length > "ApplicationManifest..xml".Length
            && n.StartsWith("ApplicationManifest.", StringComparison.OrdinalIgnoreCase)
            && n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        .Select(n => n["ApplicationManifest.".Length..^".xml".Length])
        .Distinct()
        .OrderDescending(VersionComparer.Instance)
        .ToList();

    /// <summary>
    /// Matches a package folder to the provisioned type its name suggests: one whose name (or the name without a
    /// "Type" ending) it contains, preferring one whose version it also contains; the longest match wins.
    /// </summary>
    public static ImageStorePackage MatchPackage(string path, IReadOnlyList<ApplicationTypeRow> types)
    {
        var name = NameOf(path);
        var best = types
            .Select(t => (Type: t, Name: NameMatch(name, t.Name), Version: ContainsVersion(name, t.Version)))
            .Where(m => m.Name > 0)
            .OrderByDescending(m => m.Version)
            .ThenByDescending(m => m.Name)
            .ThenByDescending(m => m.Type.Version, VersionComparer.Instance)
            .FirstOrDefault();
        return best.Type is null
            ? new ImageStorePackage(path, name, null, null, null)
            : best.Version
                ? new ImageStorePackage(path, name, best.Type.Name, best.Type.Version, best.Type)
                : new ImageStorePackage(path, name, best.Type.Name, null, null);
    }

    /// <summary>How long a stretch of <paramref name="folder"/> names the type; 0 when it doesn't.</summary>
    private static int NameMatch(string folder, string typeName)
    {
        if (folder.Contains(typeName, StringComparison.OrdinalIgnoreCase))
        {
            return typeName.Length;
        }
        var bare = typeName.EndsWith("Type", StringComparison.OrdinalIgnoreCase) ? typeName[..^4] : "";
        return bare.Length >= 3 && folder.Contains(bare, StringComparison.OrdinalIgnoreCase) ? bare.Length : 0;
    }

    /// <summary>Whether <paramref name="version"/> appears in <paramref name="text"/> on its own, not as part of a longer version.</summary>
    internal static bool ContainsVersion(string text, string version)
    {
        if (version.Length == 0)
        {
            return false;
        }
        for (var at = text.IndexOf(
            version,
            StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(version, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + version.Length;
            var before = at == 0 || !(char.IsDigit(text[at - 1]) || (text[at - 1] == '.' && at > 1 && char.IsDigit(text[at - 2])));
            var after = end == text.Length || !(char.IsDigit(text[end]) || (text[end] == '.' && end + 1 < text.Length && char.IsDigit(text[end + 1])));
            if (before && after)
            {
                return true;
            }
        }
        return false;
    }

    // Changing it

    /// <summary>
    /// Deletes a file or folder. Refuses the root, the cluster's reserved folders, and a package an application type
    /// is still being provisioned from.
    /// </summary>
    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var target = Normalize(path);
        if (target.Length == 0)
        {
            throw new RefusedException("Choose what to delete: the image store's root can't be deleted.");
        }
        if (IsReserved(target))
        {
            throw new RefusedException(
                $"{TopOf(target)} is the cluster's own copy of what's provisioned; deleting from it breaks provisioning. Unprovision instead.",
                conflict: true);
        }

        var types = await new ClusterQueries(client).GetApplicationTypesAsync(cancellationToken);
        if (MatchPackage(TopOf(target), types).Provisioned is { Status: nameof(ApplicationTypeStatus.Provisioning) } provisioning)
        {
            throw new RefusedException(
                $"{provisioning.Name} {provisioning.Version} is still being provisioned from {TopOf(target)}; wait for it to finish.",
                conflict: true);
        }
        try
        {
            await client.ImageStore.DeleteImageStoreContentAsync(target, cancellationToken: cancellationToken);
        }
        catch (ServiceFabricException ex) when (ClusterQueries.IsGone(ex))
        {
            throw new NotFoundException($"There's nothing at {target} in the image store.", ex);
        }
    }

    /// <summary>
    /// Uploads a local folder, everything in it, to <paramref name="storePath"/>, reporting progress as files and
    /// chunks of large files arrive. Overwrites what's there. Large files go in chunks; each request is tried twice.
    /// </summary>
    public async Task UploadFolderAsync(string localFolder, string storePath, IProgress<UploadProgress>? progress, CancellationToken cancellationToken)
    {
        var target = RequireUploadPath(storePath);
        var root = Path.GetFullPath(localFolder);
        if (!Directory.Exists(root))
        {
            throw new RefusedException($"There's no folder {root}.");
        }

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
        var folders = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root).ToList();
        var tracker = new Tracker(files.Count, files.Sum(f => f.Length), progress);
        tracker.Report();

        var connection = await GetConnectionStringAsync(cancellationToken);
        if (KindOf(connection) == ImageStoreKind.FileShare)
        {
            await CopyToShareAsync(connection, root, target, files, tracker, cancellationToken);
            return;
        }

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (file, ct) => await UploadFileAsync(file, StorePathOf(root, target, file.FullName), tracker, ct));

        // A marker in every folder tells the image store the folder is complete; the cluster checks for them when provisioning.
        await Parallel.ForEachAsync(
            folders,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (folder, ct) => await TwiceAsync(
                () => client.ImageStore.UploadFileAsync([], StorePathOf(root, target, folder) + "/" + DirectoryMarker, cancellationToken: ct),
                ct));
    }

    private async Task UploadFileAsync(FileInfo file, string path, Tracker tracker, CancellationToken cancellationToken)
    {
        var length = file.Length;
        if (length <= ChunkSize)
        {
            var bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
            await TwiceAsync(() => client.ImageStore.UploadFileAsync(bytes, path, cancellationToken: cancellationToken), cancellationToken);
            tracker.Add(length, fileDone: true);
            return;
        }

        var session = Guid.NewGuid();
        try
        {
            await using var stream = file.OpenRead();
            var buffer = new byte[ChunkSize];
            for (long start = 0; start < length; start += ChunkSize)
            {
                var size = (int)Math.Min(ChunkSize, length - start);
                await stream.ReadExactlyAsync(buffer.AsMemory(0, size), cancellationToken);
                var chunk = size == ChunkSize ? buffer : buffer[..size];
                var from = start;
                await TwiceAsync(
                    () => client.ImageStore.UploadFileChunkAsync(chunk, path, session, from, from + size - 1, length, cancellationToken: cancellationToken),
                    cancellationToken);
                tracker.Add(size, fileDone: false);
            }
            await TwiceAsync(() => client.ImageStore.CommitImageStoreUploadSessionAsync(session, cancellationToken: cancellationToken), cancellationToken);
            tracker.Add(0, fileDone: true);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await client.ImageStore.DeleteImageStoreUploadSessionAsync(session, cancellationToken: CancellationToken.None);
            }
            catch (Exception)
            {
                // The session expires on its own; the upload's own failure is what matters.
            }
            throw;
        }
    }

    /// <summary>A file share image store is written to directly, as the Service Fabric client does, when this machine reaches it.</summary>
    private static async Task CopyToShareAsync(
        string connection,
        string root,
        string target,
        IReadOnlyList<FileInfo> files,
        Tracker tracker,
        CancellationToken cancellationToken)
    {
        string share;
        try
        {
            share = new Uri(connection.Trim()).LocalPath;
        }
        catch (UriFormatException)
        {
            share = connection.Trim()["file:".Length..];
        }
        if (!Directory.Exists(share))
        {
            throw new RefusedException($"This cluster keeps packages in a file share ({share}) that this machine can't reach.", conflict: true);
        }
        foreach (var file in files)
        {
            var destination = Path.Combine(share, target, Path.GetRelativePath(root, file.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var source = file.OpenRead())
            await using (var copy = File.Create(destination))
            {
                await source.CopyToAsync(copy, cancellationToken);
            }
            tracker.Add(file.Length, fileDone: true);
        }
    }

    /// <summary>Tries a request again once when the connection, not the cluster, failed it.</summary>
    private static async Task TwiceAsync(Func<Task> request, CancellationToken cancellationToken)
    {
        try
        {
            await request();
        }
        catch (Exception ex) when (ex is HttpRequestException or ServiceFabricRequestException && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            await request();
        }
    }

    private sealed class Tracker(int files, long bytes, IProgress<UploadProgress>? progress)
    {
        private int _files;
        private long _bytes;

        public void Add(long bytesDone, bool fileDone)
        {
            var doneBytes = Interlocked.Add(ref _bytes, bytesDone);
            var doneFiles = fileDone ? Interlocked.Increment(ref _files) : Volatile.Read(ref _files);
            progress?.Report(new UploadProgress(doneFiles, files, doneBytes, bytes));
        }

        public void Report() => progress?.Report(new UploadProgress(0, files, 0, bytes));
    }

    // Paths

    /// <summary>Whether a path is in one of the cluster's <see cref="ReservedFolders"/>.</summary>
    public static bool IsReserved(string path) =>
        ReservedFolders.Contains(TopOf(Normalize(path)), StringComparer.OrdinalIgnoreCase);

    /// <summary>A path without surrounding spaces or separators; empty for the root.</summary>
    public static string Normalize(string? path) => (path ?? "").Trim().Trim('/', '\\');

    /// <summary>The last part of a path, whichever separator it uses.</summary>
    public static string NameOf(string? path)
    {
        var normal = Normalize(path);
        return normal[(normal.LastIndexOfAny(['/', '\\']) + 1)..];
    }

    private static string TopOf(string path)
    {
        var at = path.IndexOfAny(['/', '\\']);
        return at < 0 ? path : path[..at];
    }

    /// <summary>A name safe as an image store folder: letters, digits, '.', '-' and '_', anything else made '_'.</summary>
    public static string SafeName(string name)
    {
        var safe = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
        {
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }
        return safe.ToString();
    }

    private static string RequireUploadPath(string storePath)
    {
        var target = Normalize(storePath).Replace('\\', '/');
        if (target.Length == 0)
        {
            throw new RefusedException("Choose where in the image store to upload to.");
        }
        if (target.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new RefusedException($"'{storePath}' isn't a path in the image store: it can't have empty, '.' or '..' parts.");
        }
        if (IsReserved(target))
        {
            throw new RefusedException($"{TopOf(target)} is reserved for the cluster's own copies; upload somewhere else.");
        }
        return target;
    }

    private static string StorePathOf(string root, string target, string local)
    {
        var relative = Path.GetRelativePath(root, local);
        return relative == "." ? target : target + "/" + relative.Replace('\\', '/');
    }

    private static string Describe(ImageStoreKind kind) => kind switch
    {
        ImageStoreKind.FileShare => "a file share",
        ImageStoreKind.AzureBlob => "Azure blob storage",
        _ => "a store sfx doesn't know",
    };

    private static long? Long(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
