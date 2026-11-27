using Sfx.Web;

namespace Sfx.Cli;

/// <summary><c>sfx store ls|du</c>: the image store, as the browser's store page shows it.</summary>
internal static class StoreCommand
{
    public const string Help = """
        usage: sfx store ls [<path>] [-l] [-o json]
               sfx store du [<path>] [-o json]

        ls lists a folder of the image store (default: its root); folders end in /.
        du gives the size of everything in it, largest first, and the total.
        <path>: store:/<path> or just the path.

          -l              ls: sizes, file counts, when modified, and which application package a folder holds
          -o json         the browser's listing; for du, with each folder's size filled in, largest first
          --no-headers    leave out the header line
        """;

    private const int Parallel = 4;

    public static Func<UiSession, CancellationToken, Task> Prepare(IReadOnlyList<string> args, CliOptions options, CliConsole console)
    {
        var (verb, path) = args switch
        {
            [var v] => (v, ""),
            [var v, var p] => (v, Path(p)),
            [] => throw CliFailure.Usage("sfx store ls or sfx store du?"),
            _ => throw CliFailure.Usage($"Unexpected argument '{args[2]}'."),
        };
        var json = Scripting.OutputOf(options, ["text", "json"], "text") == "json";
        switch (verb)
        {
            case "ls":
                Scripting.Only(options, "store ls", "-l", "--no-headers");
                return (s, ct) => ListAsync(s, path, json, options, console, ct);
            case "du":
                Scripting.Only(options, "store du", "--no-headers");
                return (s, ct) => UsageAsync(s, path, json, options, console, ct);
            default:
                throw CliFailure.Usage($"No store command '{verb}'; use ls or du.");
        }
    }

    private static string Path(string typed) =>
        Slashed(typed.StartsWith("store:", StringComparison.Ordinal) ? typed["store:".Length..] : typed);

    /// <summary>Without leading or trailing separators, and with '/' for '\', to compare paths however the cluster writes them.</summary>
    internal static string Slashed(string path) => Sfx.Core.ImageStore.Normalize(path).Replace('\\', '/');

    private static async Task ListAsync(UiSession s, string path, bool json, CliOptions options, CliConsole console, CancellationToken ct)
    {
        var listing = await ApiReads.StoreAsync(s, path, ct);
        listing = listing with
        {
            Folders = GetCommand.Sorted(listing.Folders, f => f.Name),
            Files = GetCommand.Sorted(listing.Files, f => f.Name),
        };
        if (json)
        {
            Output.WriteJson(console.Out, listing);
            return;
        }
        var entries = listing.Folders.Select(f => Entry.Of(f)).Concat(listing.Files.Select(Entry.Of)).ToList();
        if (!options.Long)
        {
            entries.ForEach(e => console.Out.WriteLine(e.Name));
            return;
        }
        Output.Table(console, entries, LongColumns, !options.NoHeaders);
    }

    private static async Task UsageAsync(UiSession s, string path, bool json, CliOptions options, CliConsole console, CancellationToken ct)
    {
        var listing = await ApiReads.StoreAsync(s, path, ct);
        var folders = new StoreFolderDto[listing.Folders.Count];
        using var gate = new SemaphoreSlim(Parallel);
        await Task.WhenAll(listing.Folders.Select(async (folder, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                folders[i] = folder with { Size = (await ApiReads.StoreSizeAsync(s, folder.Path, ct)).Size };
            }
            finally
            {
                gate.Release();
            }
        }));
        var bySize = Comparer<long?>.Create((a, b) => (b ?? -1).CompareTo(a ?? -1));
        if (json)
        {
            Output.WriteJson(
                console.Out,
                listing with
                {
                    Folders = folders.OrderBy(f => f.Size, bySize).ToList(),
                    Files = listing.Files.OrderBy(f => f.Size, bySize).ToList(),
                });
            return;
        }
        var entries = folders.Select(Entry.Of).Concat(listing.Files.Select(Entry.Of)).OrderBy(e => e.Size, bySize).ToList();
        var total = entries.Sum(e => e.Size ?? 0);
        var unknown = entries.Any(e => e.Size is null);
        entries.Add(new Entry("total", total, null, null, null));
        Output.Table(console, entries, UsageColumns, !options.NoHeaders);
        if (unknown)
        {
            console.Error.WriteLine("The cluster didn't say how big some are (?); the total leaves them out.");
        }
    }

    private sealed record Entry(string Name, long? Size, long? Files, DateTime? Modified, string? Package)
    {
        public static Entry Of(StoreFolderDto f) => new(f.Name + "/", f.Size, f.FileCount, null, PackageOf(f.Package));

        public static Entry Of(StoreFileDto f) => new(f.Name, f.Size, null, f.Modified, null);

        private static string? PackageOf(StorePackageDto? p) => p switch
        {
            null => null,
            { TypeName: null } => "package",
            { TypeVersion: null } => p.TypeName,
            { Provisioned: true } => $"{p.TypeName}@{p.TypeVersion} ({p.Status ?? "provisioned"})",
            _ => $"{p.TypeName}@{p.TypeVersion} (not provisioned)",
        };
    }

    private static readonly Column<Entry>[] LongColumns =
    [
        new("SIZE", e => Output.Bytes(e.Size), Right: true),
        new("FILES", e => e.Files?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", Right: true),
        new("MODIFIED", e => Output.When(e.Modified)),
        new("NAME", e => e.Name),
        new("PACKAGE", e => e.Package),
    ];

    private static readonly Column<Entry>[] UsageColumns =
    [
        new("SIZE", e => e.Size is null ? "?" : Output.Bytes(e.Size), Right: true),
        new("NAME", e => e.Name),
    ];
}
