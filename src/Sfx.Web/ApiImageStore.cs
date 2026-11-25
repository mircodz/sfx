using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Sfx.Core;

namespace Sfx.Web;

/// <param name="Kind">Native, FileShare, AzureBlob or Unknown: where the cluster keeps packages.</param>
/// <param name="Usage">The store's disk and what uses it; only at the root, and null when the cluster can't say.</param>
public sealed record StoreDto(string Path, string Kind, StoreUsageDto? Usage, IReadOnlyList<StoreFolderDto> Folders, IReadOnlyList<StoreFileDto> Files);

/// <summary>Space used for one purpose; null fields when the cluster doesn't say.</summary>
public sealed record StoreUseDto(long? Bytes, long? Files);

public sealed record StoreUsageDto(
    long? DiskCapacity,
    long? DiskAvailable,
    StoreUseDto? Metadata,
    StoreUseDto? Staging,
    StoreUseDto? Copy,
    StoreUseDto? Register);

/// <param name="Path">Its path in the image store, as the cluster gives it (Windows clusters separate with '\'); send it back as ?path=.</param>
/// <param name="FileCount">How many files it holds, all the way down; null when the cluster doesn't say.</param>
/// <param name="Size">In bytes, if asked for (GET /api/store/size) within the last minute; null otherwise.</param>
/// <param name="Package">Set for a top-level folder holding an application package (an ApplicationManifest.xml).</param>
/// <param name="Reserved">One of the cluster's own folders (Store, WindowsFabricStore) or in one: never deleted.</param>
/// <param name="Actions">delete-store-content and provision-package, where they apply.</param>
public sealed record StoreFolderDto(
    string Path,
    string Name,
    long? FileCount,
    long? Size,
    StorePackageDto? Package,
    bool Reserved,
    IReadOnlyList<ActionDto> Actions);

/// <summary>
/// Guessed from the folder's name, since the image store can't be read back: the type when a provisioned type's name is
/// in it, and the version when that type's provisioned version is in it too.
/// </summary>
/// <param name="Provisioned">Whether that type version is provisioned, in any state.</param>
/// <param name="Status">The provisioned type version's status (Available, Provisioning, …); null when it isn't provisioned.</param>
public sealed record StorePackageDto(string? TypeName, string? TypeVersion, bool Provisioned, string? Status);

/// <param name="Version">The image store's version number of the file; null when it doesn't say.</param>
public sealed record StoreFileDto(string Path, string Name, long? Size, string? Version, DateTime? Modified, IReadOnlyList<ActionDto> Actions);

public sealed record StoreSizeDto(long? Size);

/// <param name="OperationId">The operation in GET /api/operations that follows the upload (and provisioning).</param>
public sealed record UploadResultDto(string OperationId, string Message);

internal static class ApiImageStore
{
    public const long MaxUpload = 4L * 1024 * 1024 * 1024;
    public const string UploadActionId = "upload-package";

    private static readonly TimeSpan UploadTimeout = TimeSpan.FromHours(6);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/store", async (UiSession s, string? path, CancellationToken ct) =>
        {
            var folder = ImageStore.Normalize(path);
            var root = folder.Length == 0;
            var listing = s.ImageStore.GetContentAsync(folder, ct);
            var kind = KindAsync(s, ct);
            var usage = root ? s.ImageStore.GetUsageAsync(ct) : Task.FromResult<ImageStoreUsage?>(null);
            var packages = root ? PackagesAsync(
                s,
                ct) : Task.FromResult<IReadOnlyDictionary<string, ImageStorePackage>>(new Dictionary<string, ImageStorePackage>());
            var l = await listing;
            return Listing(s, l, await kind, await usage, await packages);
        });

        api.MapGet("/store/size", async (UiSession s, string? path, CancellationToken ct) => new StoreSizeDto(await s.GetFolderSizeAsync(path ?? "", ct)));

        api.MapPost("/store/upload", (UiSession s, HttpContext context) => UploadAsync(s, context));
    }

    internal static StoreDto Listing(
        UiSession s,
        ImageStoreListing listing,
        string kind,
        ImageStoreUsage? usage,
        IReadOnlyDictionary<string, ImageStorePackage> packages)
    {
        static StoreUseDto? Use(ImageStoreUse? u) => u is null ? null : new StoreUseDto(u.Bytes, u.Files);
        return new StoreDto(
            listing.Path,
            kind,
            usage is null
                ? null
                : new StoreUsageDto(usage.DiskCapacity, usage.DiskAvailable, Use(usage.Metadata), Use(usage.Staging), Use(usage.Copy), Use(usage.Register)),
            listing.Folders.Select(f =>
            {
                var package = packages.GetValueOrDefault(f.Path);
                var target = Target(f.Path, f.Name, "folder", provision: package is { Provisioned: null });
                return new StoreFolderDto(
                    f.Path,
                    f.Name,
                    f.FileCount,
                    f.Size ?? s.KnownFolderSize(f.Path),
                    package is null ? null : new StorePackageDto(
                        package.TypeName,
                        package.TypeVersion,
                        package.Provisioned is not null,
                        package.Provisioned?.Status),
                    ImageStore.IsReserved(f.Path),
                    WebActions.For(s, "store", target));
            }).ToList(),
            listing.Files.Select(f => new StoreFileDto(
                f.Path,
                f.Name,
                f.Size,
                f.Version.Length > 0 ? f.Version : null,
                f.Modified,
                WebActions.For(s, "store", Target(f.Path, f.Name, "file", provision: false)))).ToList());
    }

    private static Dictionary<string, string> Target(string path, string name, string entry, bool provision) => new()
    {
        ["path"] = path,
        ["name"] = name,
        ["entry"] = entry,
        ["provision"] = provision ? "true" : "false",
    };

    private static async Task<string> KindAsync(UiSession s, CancellationToken ct)
    {
        try
        {
            return (await s.ImageStore.GetKindAsync(ct)).ToString();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return nameof(ImageStoreKind.Unknown);
        }
    }

    /// <summary>None when they can't be told, so the listing still shows.</summary>
    private static async Task<IReadOnlyDictionary<string, ImageStorePackage>> PackagesAsync(UiSession s, CancellationToken ct)
    {
        try
        {
            return (await s.GetStorePackagesAsync(ct)).ToDictionary(p => p.Path);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new Dictionary<string, ImageStorePackage>();
        }
    }

    /// <summary>
    /// Takes multipart/form-data: one .sfpkg or .zip, or a folder's files each named by its path. Saves to a temporary
    /// folder as it arrives, checks it's a package, then uploads (and provisions) it in the background as an operation.
    /// </summary>
    internal static async Task<UploadResultDto> UploadAsync(UiSession s, HttpContext context)
    {
        var ct = context.RequestAborted;
        if (s.ReadOnly)
        {
            throw new ActionException("This context is read-only.", StatusCodes.Status403Forbidden);
        }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxUpload;
        }
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var media)
            || !media.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(media.Boundary).Value is not { Length: > 0 } boundary)
        {
            throw new ActionException("Send the package as multipart/form-data.", StatusCodes.Status415UnsupportedMediaType);
        }

        var temp = Directory.CreateTempSubdirectory("sfx-upload-").FullName;
        LocalPackage? package = null;
        try
        {
            var files = Path.Combine(temp, "files");
            Directory.CreateDirectory(files);
            var (saved, fields) = await ReceiveAsync(new MultipartReader(boundary, context.Request.Body) { BodyLengthLimit = MaxUpload }, files, ct);
            package = Open(files, saved);

            var provision = !(fields.GetValueOrDefault("provision")?.Trim().ToLowerInvariant() is "false" or "0" or "no" or "off");
            var storePath = StorePath(fields.GetValueOrDefault("storePath"), package.StorePath);
            if (provision)
            {
                var existing = await s.Inspector.GetApplicationTypeVersionsAsync(package.TypeName, ct);
                if (existing.FirstOrDefault(t => t.Version == package.TypeVersion) is { } type)
                {
                    throw new ActionException(
                        $"{package.TypeName} {package.TypeVersion} is already {(type.Status == Microsoft.ServiceFabric.Common.ApplicationTypeStatus.Provisioning ? "being provisioned" : "provisioned")}. " +
                        "Unprovision it first, or give the package a new version.",
                        StatusCodes.Status409Conflict);
                }
            }

            var title = $"Upload {package.TypeName} {package.TypeVersion} to {storePath}" + (provision ? " and provision it" : "");
            var id = s.Begin(UploadActionId, title, "Starting the upload.");
            var logError = s.TryLog(title, "ok");
            var (uploading, folder) = (package, temp);
            package = null;
            temp = "";
            _ = Task.Run(() => RunUploadAsync(s, id, title, uploading, folder, storePath, provision));
            return new UploadResultDto(id, logError is null ? title : $"{title}. Started, but the action log couldn't be written ({logError})");
        }
        finally
        {
            package?.Dispose();
            if (temp.Length > 0)
            {
                TryDelete(temp);
            }
        }
    }

    /// <summary>Saves each file part under <paramref name="files"/> as it streams in; also returns the plain fields.</summary>
    internal static async Task<(IReadOnlyList<string> Files, IReadOnlyDictionary<string, string> Fields)> ReceiveAsync(
        MultipartReader reader,
        string files,
        CancellationToken ct)
    {
        var saved = new List<string>();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(files) + Path.DirectorySeparatorChar;
        while (await reader.ReadNextSectionAsync(ct) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
            {
                continue;
            }
            var fileName = disposition.FileNameStar.HasValue ? disposition.FileNameStar.Value : HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
            if (fileName is null && !disposition.FileName.HasValue)
            {
                if (HeaderUtilities.RemoveQuotes(disposition.Name).Value is { Length: > 0 } name)
                {
                    using var text = new StreamReader(section.Body);
                    var buffer = new char[4096];
                    var read = await text.ReadBlockAsync(buffer, ct);
                    fields[name] = new string(buffer, 0, read);
                }
                continue;
            }
            // An empty file input sends a part with no file name.
            if (string.IsNullOrEmpty(fileName))
            {
                continue;
            }

            var relative = PackageUpload.RelativePath(fileName);
            var destination = Path.GetFullPath(Path.Combine(root, relative));
            if (!destination.StartsWith(root, StringComparison.Ordinal))
            {
                throw new ActionException($"'{fileName}' isn't a path inside the package.");
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await section.Body.CopyToAsync(file, ct);
            }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination) || saved.Any(p => relative.StartsWith(
                p + "/",
                StringComparison.Ordinal)))
            {
                throw new ActionException($"'{relative}' is in the upload twice.");
            }
            saved.Add(relative);
        }
        return (saved, fields);
    }

    /// <summary>A lone .sfpkg or .zip, else the folder the files share (or all of them).</summary>
    internal static LocalPackage Open(string files, IReadOnlyList<string> saved)
    {
        if (saved.Count == 0)
        {
            throw new ActionException("Choose a package to upload: a package folder, or an .sfpkg or .zip file.");
        }
        string local, shown;
        if (saved.Count == 1 && PackageUpload.IsArchive(saved[0]))
        {
            local = Path.Combine(files, saved[0]);
            shown = Path.GetFileName(saved[0]);
        }
        else
        {
            var common = PackageUpload.CommonFolder(saved);
            local = common.Length == 0 ? files : Path.Combine(files, common);
            shown = common.Length == 0 ? "The upload" : common;
        }
        try
        {
            return LocalPackage.Open(local);
        }
        // Core names the folder it was given, which here is a temporary one: name what was uploaded instead.
        catch (RefusedException ex)
        {
            throw new ActionException(ex.Message.Replace(local, shown).Replace(files, "the upload"));
        }
    }

    /// <summary>As given, or the package's own "{type}_{version}".</summary>
    internal static string StorePath(string? given, string fallback)
    {
        if (string.IsNullOrWhiteSpace(given))
        {
            return fallback;
        }
        var path = ImageStore.Normalize(given).Replace('\\', '/');
        if (path.Length == 0 || path.Split('/').Any(part => part.Trim() is "" or "." or ".."))
        {
            throw new ActionException($"'{given.Trim()}' isn't a path in the image store: it can't have empty, '.' or '..' parts.");
        }
        if (ImageStore.IsReserved(path))
        {
            throw new ActionException($"{path.Split('/')[0]} is reserved for the cluster's own copies; upload somewhere else.");
        }
        return path;
    }

    private static async Task RunUploadAsync(UiSession s, string id, string title, LocalPackage package, string temp, string storePath, bool provision)
    {
        var (name, version) = (package.TypeName, package.TypeVersion);
        using var timeout = new CancellationTokenSource(UploadTimeout);
        try
        {
            await s.ImageStore.UploadFolderAsync(
                package.Folder,
                storePath,
                new Reporter(p => s.Progress(id, "Uploading " + PackageUpload.Progress(p), p.Fraction)),
                timeout.Token);
        }
        catch (Exception ex)
        {
            s.Finish(id, title, SettleChecks.Failed, Failure("The upload failed", ex));
            return;
        }
        finally
        {
            package.Dispose();
            TryDelete(temp);
        }

        if (!provision)
        {
            s.Finish(id, title, SettleChecks.Done, $"Uploaded to {storePath}.");
            return;
        }
        s.Progress(id, $"Provisioning {name} {version}.", null);
        try
        {
            await s.Lifecycle.ProvisionAsync(storePath, timeout.Token);
        }
        catch (Exception ex)
        {
            s.Finish(id, title, SettleChecks.Failed, Failure($"Uploaded to {storePath}, but provisioning failed", ex));
            return;
        }
        var started = DateTime.UtcNow;
        await s.WatchAsync(id, title, async ct => SettleChecks.TypeProvisioned(
            (await s.Queries.GetApplicationTypesAsync(ct)).FirstOrDefault(t => t.Name == name && t.Version == version),
            name,
            version,
            DateTime.UtcNow - started));
    }

    private static string Failure(string what, Exception ex) => ex switch
    {
        RefusedException refused => refused.Message,
        OperationCanceledException => $"{what}: it took longer than {UploadTimeout.TotalHours:0} hours.",
        _ => $"{what}: {ex.GetBaseException().Message.TrimEnd('.')}.",
    };

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in the temporary folder, which the OS clears.
        }
    }

    /// <summary>Reports on the uploading thread, never going backwards (files upload in parallel).</summary>
    private sealed class Reporter(Action<UploadProgress> report) : IProgress<UploadProgress>
    {
        private readonly Lock _lock = new();
        private UploadProgress? _last;

        public void Report(UploadProgress value)
        {
            lock (_lock)
            {
                var next = _last is { } last
                    ? value with { FilesDone = Math.Max(value.FilesDone, last.FilesDone), BytesDone = Math.Max(value.BytesDone, last.BytesDone) }
                    : value;
                if (next == _last)
                {
                    return;
                }
                _last = next;
                report(next);
            }
        }
    }
}
