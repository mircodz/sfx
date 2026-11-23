using System.Collections.Concurrent;
using System.Globalization;
using Cellar.Layout;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Sfx.Core;
using static Sfx.Tui.Styles;

namespace Sfx.Tui;

/// <summary>A folder or file in the image store, with the package a top-level folder holds.</summary>
/// <param name="Size">In bytes; for a folder, null until it's been added up.</param>
public sealed record StoreEntry(string Path, string Name, bool Folder, long? Size, long? FileCount, DateTime? Modified, ImageStorePackage? Package);

/// <summary>
/// The image store, a folder per page under the breadcrumb: Enter opens a folder, backspace goes up. Top-level
/// folders holding a package say which type and version, and whether it's provisioned. Folder sizes are added up in
/// the background and show from the next refresh.
/// </summary>
public sealed partial class Views
{
    public const string UploadActionId = "store.upload";

    private static readonly TimeSpan StoreCacheFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SizeBudget = TimeSpan.FromSeconds(1.5);

    private ImageStore? _store;
    private readonly ConcurrentDictionary<string, Task<long>> _folderSizes = new();
    private (DateTime At, Task<IReadOnlyList<ImageStorePackage>> Packages)? _packages;
    private (DateTime At, Task<ImageStoreUsage?> Usage)? _usage;

    private ImageStore Store => _store ??= new ImageStore(queries.Client);

    public IResourceView ImageStoreRoot() => ImageStoreFolder("");

    public IResourceView ImageStoreFolder(string path)
    {
        var folder = ImageStore.Normalize(path);
        string? note = null;
        ResourceView<StoreEntry>? view = null;
        view = new ResourceView<StoreEntry>("image store" + (folder.Length > 0 ? " › " + folder : ""), folder.Length == 0 ? "store" : ImageStore.NameOf(folder),
                async ct =>
                {
                    var (entries, usage) = await StoreEntriesAsync(folder, ct);
                    note = usage;
                    return entries;
                },
                e => e.Path)
            {
                OnOpen = e => e.Folder ? ImageStoreFolder(e.Path) : null,
                Empty = folder.Length == 0 ? "the image store is empty; u uploads a folder or package" : "empty folder",
                Note = () => note,
            }
            .HighlightingChanges()
            .Acting(StoreEntryActions(folder), host)
            .Offering(
            [
                new AppAction(UploadActionId, "Upload a folder or package from this machine here", (string? local) =>
                {
                    if (string.IsNullOrWhiteSpace(local))
                    {
                        host.Command("upload ");
                    }
                    else
                    {
                        UploadToStore(local, folder);
                    }
                })
                {
                    Group = "Image store",
                    Keys = [Upload],
                    Commands = ["upload"],
                    ArgumentHint = "<path>",
                    When = () => host.Allows(ActionRisk.Confirm, power: false) && !ImageStore.IsReserved(folder),
                },
            ])
            .Column("NAME", Constraint.Fill(2), e => e.Folder ? Colored(e.Name + "/", Theme.Current.Accent) : Plain(e.Name), e => (e.Folder ? "0" : "1") + e.Name)
            .Column("SIZE", Constraint.Length(9), e => e.Size is null ? Muted(e.Folder ? "…" : "-") : Plain(Bytes(e.Size)), e => e.Size, Justify.Right)
            .Column("FILES", Constraint.Length(6), e => e.FileCount is { } n ? Plain(n.ToString(CultureInfo.InvariantCulture)) : Muted("-"), e => e.FileCount, Justify.Right)
            .Column("PACKAGE", Constraint.Fill(2), e => PackageText(e.Package), e => e.Package?.TypeName)
            .Column("MODIFIED", Constraint.Length(16), e => e.Modified is { } at ? Plain(at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)) : Muted("-"),
                e => e.Modified);
        return view;
    }

    private static StyledText PackageText(ImageStorePackage? package) => package switch
    {
        null => Muted(""),
        { Provisioned: { } type } => StyledText.Of($"{type.Name} {type.Version} ").Fg(Theme.Current.Foreground)
            .Append(type.Status == "Available" ? "provisioned" : type.Status.ToLowerInvariant()).Fg(type.Status == "Available" ? Theme.Current.Success : Theme.Current.Warning),
        { TypeName: { } name } => StyledText.Of($"{name} {package.TypeVersion ?? ""}".TrimEnd() + " ").Fg(Theme.Current.Foreground)
            .Append("not provisioned").Fg(Theme.Current.Warning),
        _ => Colored("package, not provisioned", Theme.Current.Warning),
    };

    /// <summary>A folder's entries, folders first; at the root, with packages and how much of the store is used.</summary>
    private async Task<(IReadOnlyList<StoreEntry> Entries, string? Usage)> StoreEntriesAsync(string folder, CancellationToken cancellationToken)
    {
        var listing = await Store.GetContentAsync(folder, cancellationToken);
        var root = folder.Length == 0;
        var packages = root ? Cached(ref _packages, () => Store.GetPackagesAsync(CancellationToken.None)) : null;
        var usage = root ? Cached(ref _usage, () => Store.GetUsageAsync(CancellationToken.None)) : null;

        // Sizes take a call per folder (more on old clusters): wait a moment for them, then leave the rest for later.
        var sizes = listing.Folders.ToDictionary(f => f.Path, f => _folderSizes.GetOrAdd(f.Path, path => FolderSizeAsync(path)));
        await Task.WhenAny(Task.WhenAll(sizes.Values), Task.Delay(SizeBudget, cancellationToken));

        var byPath = new Dictionary<string, ImageStorePackage>(StringComparer.OrdinalIgnoreCase);
        if (packages is not null)
        {
            try
            {
                foreach (var package in await packages.WaitAsync(SizeBudget, cancellationToken))
                {
                    byPath[ImageStore.Normalize(package.Path)] = package;
                }
            }
            catch (TimeoutException)
            {
            }
        }

        string? used = null;
        if (usage is not null)
        {
            try
            {
                used = UsageText(await usage.WaitAsync(SizeBudget, cancellationToken));
            }
            catch (TimeoutException)
            {
            }
        }

        var entries = listing.Folders
            .Select(f => new StoreEntry(f.Path, f.Name, true,
                sizes[f.Path].IsCompletedSuccessfully ? sizes[f.Path].Result : null, f.FileCount, null,
                byPath.GetValueOrDefault(ImageStore.Normalize(f.Path))))
            .Concat(listing.Files.Select(f => new StoreEntry(f.Path, f.Name, false, f.Size, null, f.Modified, null)))
            .ToList();
        return (entries, used);
    }

    private async Task<long> FolderSizeAsync(string path)
    {
        try
        {
            return await Store.GetFolderSizeAsync(path, CancellationToken.None);
        }
        catch
        {
            // Asked again next time rather than remembered as failed.
            _folderSizes.TryRemove(path, out _);
            throw;
        }
    }

    /// <summary>"1.2 GB used · 40 GB free": what the store's parts use, and what's left on its disk.</summary>
    internal static string? UsageText(ImageStoreUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }
        var parts = new[] { usage.Metadata, usage.Staging, usage.Copy, usage.Register }.Where(u => u?.Bytes is not null).ToList();
        var used = parts.Count == 0 ? null : $"{Bytes(parts.Sum(u => u!.Bytes!.Value))} used";
        var free = usage.DiskAvailable is { } available ? $"{Bytes(available)} free" : null;
        var text = string.Join(" · ", new[] { used, free }.OfType<string>());
        return text.Length == 0 ? null : text;
    }

    private static Task<T> Cached<T>(ref (DateTime At, Task<T> Value)? cache, Func<Task<T>> load)
    {
        if (cache is { } cached && DateTime.UtcNow - cached.At < StoreCacheFor && !cached.Value.IsFaulted)
        {
            return cached.Value;
        }
        var task = load();
        cache = (DateTime.UtcNow, task);
        return task;
    }

    /// <summary>Forgets what was read about the store, after a change.</summary>
    private void StoreChanged(string path)
    {
        _packages = null;
        _usage = null;
        var normalized = ImageStore.Normalize(path);
        foreach (var key in _folderSizes.Keys.Where(key => normalized.StartsWith(ImageStore.Normalize(key), StringComparison.OrdinalIgnoreCase)
            || ImageStore.Normalize(key).StartsWith(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            _folderSizes.TryRemove(key, out _);
        }
    }

    private IReadOnlyList<EntityAction<StoreEntry>> StoreEntryActions(string folder) =>
    [
        new("Provision the package in this folder", ActionRisk.Confirm, e => ProvisionStored(e.Path), [Provision], ["provision-folder"],
            AppliesTo: e => e.Folder && !ImageStore.IsReserved(e.Path) && e.Package?.Provisioned is null),

        new("Delete from the image store", ActionRisk.Destructive,
            e => host.Run(new Operation($"Delete {e.Path} from the image store", e.Name, ActionRisk.Destructive,
                async (ct, _) =>
                {
                    await Store.DeleteAsync(e.Path, ct);
                    StoreChanged(e.Path);
                },
                e.Folder ? "Everything in it is deleted; a provisioned type keeps its own copy." : "The file is deleted.")
            {
                Track = async (_, ct) =>
                {
                    var listing = await Store.GetContentAsync(folder, ct);
                    return Tracking.Gone(listing.Folders.Select(f => f.Path).Concat(listing.Files.Select(f => f.Path))
                        .Any(path => ImageStore.Normalize(path) == ImageStore.Normalize(e.Path)));
                },
            }),
            [Delete], ["delete"], Power: true,
            AppliesTo: e => !ImageStore.IsReserved(e.Path)),
    ];

    /// <summary>
    /// Uploads a folder from this machine into <paramref name="folder"/>, under its own name; a .sfpkg or .zip package
    /// goes in unpacked, as "{type}_{version}".
    /// </summary>
    private void UploadToStore(string local, string folder)
    {
        var full = PathCompletion.Expand(local);
        if (Directory.Exists(full))
        {
            var target = Join(folder, ImageStore.SafeName(Path.GetFileName(Path.TrimEndingDirectorySeparator(full))));
            host.Run(UploadOperation(full, target, () => full, () => { }));
        }
        else if (File.Exists(full) && PathCompletion.IsPackage(full))
        {
            host.Load($"reading {local}", _ => Task.Run(() =>
            {
                using var package = LocalPackage.Open(full);
                return package.StorePath;
            }), storePath =>
            {
                LocalPackage? package = null;
                host.Run(UploadOperation(full, Join(folder, storePath), () => (package = LocalPackage.Open(full)).Folder, () => package?.Dispose()));
            });
        }
        else
        {
            host.Warn($"{local} isn't a folder, an .sfpkg or a .zip");
        }
    }

    internal Operation UploadOperation(string local, string target, Func<string> open, Action close)
    {
        var progress = new OperationProgress();
        return new Operation($"Upload {Path.GetFileName(Path.TrimEndingDirectorySeparator(local))} to the image store", target, ActionRisk.Confirm,
            async (ct, _) =>
            {
                try
                {
                    await Store.UploadFolderAsync(open(), target, Reporting(progress, "uploaded"), ct);
                }
                finally
                {
                    close();
                    StoreChanged(target);
                }
            },
            "Whatever is already there under that name is overwritten.")
        {
            Details = Facts(("From", local), ("To", $"image store › {target}")),
            Progress = progress,
        };
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";
}
