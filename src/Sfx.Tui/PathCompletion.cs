namespace Sfx.Tui;

/// <summary>
/// Tab completion of a path on this machine, for <c>:provision</c> and <c>:upload</c>: folders (with a trailing
/// <c>/</c>, so tab on a lone one goes on into it) and application packages (.sfpkg, .zip). Values keep the form
/// typed, <c>~</c> included; hidden entries only show once a <c>.</c> is typed.
/// </summary>
public static class PathCompletion
{
    private static readonly string[] PackageExtensions = [".sfpkg", ".zip"];

    /// <summary>The completions of <paramref name="typed"/>, relative paths against <paramref name="directory"/> (the current one by default).</summary>
    public static IReadOnlyList<string> Complete(string typed, string? directory = null)
    {
        if (typed == "~")
        {
            return ["~/"];
        }

        var slash = typed.LastIndexOfAny(['/', Path.DirectorySeparatorChar]);
        var folderPart = slash >= 0 ? typed[..(slash + 1)] : "";
        var prefix = typed[(slash + 1)..];
        var folder = folderPart.Length == 0 ? directory ?? Directory.GetCurrentDirectory() : Expand(folderPart, directory);

        try
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists)
            {
                return [];
            }
            return dir.EnumerateFileSystemInfos()
                .Where(entry => entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(entry => !entry.Name.StartsWith('.') || prefix.StartsWith('.'))
                .Where(entry => entry is DirectoryInfo || IsPackage(entry.Name))
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .Select(entry => folderPart + entry.Name + (entry is DirectoryInfo ? "/" : ""))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>The full path of what was typed: <c>~</c> is the home folder, a relative path is against <paramref name="directory"/>.</summary>
    public static string Expand(string path, string? directory = null)
    {
        path = path.Trim();
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            path = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', Path.DirectorySeparatorChar));
        }
        return Path.GetFullPath(path, directory ?? Directory.GetCurrentDirectory());
    }

    public static bool IsPackage(string name) =>
        PackageExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
}
