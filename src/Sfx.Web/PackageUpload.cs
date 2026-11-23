namespace Sfx.Web;

/// <summary>Where the files of an uploaded package go, from the names the browser gives them.</summary>
internal static class PackageUpload
{
    /// <summary>
    /// A file's path inside the package from the name the browser sent (a folder upload sends each file's relative path,
    /// e.g. "MyAppPkg/Code/app.dll"), with '/' between parts. Refuses absolute paths, "..", and names that aren't files.
    /// </summary>
    public static string RelativePath(string? fileName)
    {
        var name = (fileName ?? "").Replace('\\', '/').Trim();
        if (name.Length == 0)
        {
            throw new ActionException("Every file in the upload needs a name.");
        }
        if (name.StartsWith('/') || Path.IsPathRooted(name) || (name.Length >= 2 && name[1] == ':'))
        {
            throw new ActionException($"'{name}' isn't a path inside the package: it's absolute.");
        }
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToList();
        if (parts.Any(p => p == ".."))
        {
            throw new ActionException($"'{name}' isn't a path inside the package: it has '..' in it.");
        }
        if (parts.Count == 0 || parts.Any(p => p.Trim().Length == 0 || p.IndexOfAny(Invalid) >= 0 || p.Contains(':')))
        {
            throw new ActionException($"'{name}' isn't a file name sfx can save.");
        }
        return string.Join('/', parts);
    }

    private static readonly char[] Invalid = ['\0', '<', '>', '"', '|', '?', '*', .. Enumerable.Range(1, 31).Select(c => (char)c)];

    /// <summary>
    /// The folder every file is in, when they share one (a folder upload names the chosen folder first): it's the
    /// package's root. Empty when the files are at the root already, or don't share a folder.
    /// </summary>
    public static string CommonFolder(IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0 || relativePaths.Any(p => !p.Contains('/')))
        {
            return "";
        }
        var first = relativePaths[0][..relativePaths[0].IndexOf('/')];
        return relativePaths.All(p => p.StartsWith(first + "/", StringComparison.Ordinal)) ? first : "";
    }

    /// <summary>Whether a lone file is a packaged application to extract, rather than a one-file folder.</summary>
    public static bool IsArchive(string name) =>
        name.EndsWith(".sfpkg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>"12 of 40 files, 18 of 52 MB", in the unit that suits the total.</summary>
    public static string Progress(Sfx.Core.UploadProgress p)
    {
        var (unit, size) = p.BytesTotal switch
        {
            >= 1L << 30 => ("GB", 1L << 30),
            >= 1L << 20 => ("MB", 1L << 20),
            _ => ("KB", 1L << 10),
        };
        static string Amount(long bytes, long size) =>
            (bytes / (double)size).ToString(bytes >= size * 10 || bytes == 0 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);
        return $"{p.FilesDone} of {p.FilesTotal} files, {Amount(p.BytesDone, size)} of {Amount(p.BytesTotal, size)} {unit}";
    }
}
