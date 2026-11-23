namespace Sfx.Core;

/// <summary>What a <c>[folders.&lt;path&gt;]</c> table sets for the contexts in that folder and its subfolders.</summary>
/// <param name="Path">The normalized folder path, e.g. <c>prod/europe</c>.</param>
/// <param name="Label">Short text shown next to the context name.</param>
/// <param name="Color">One of <see cref="FolderPath.Colors"/>.</param>
/// <param name="ReadOnly">Makes every context in the folder and its subfolders read-only.</param>
public sealed record FolderSettings(string Path, string? Label, string? Color, bool ReadOnly);

/// <summary>A saved context with its folders' rules applied.</summary>
/// <param name="Context">The context with <c>ReadOnly</c> and <c>Power</c> as they apply after the folder rules.</param>
/// <param name="Label">The label of the nearest folder that sets one.</param>
/// <param name="Color">The color of the nearest folder that sets one.</param>
public sealed record ResolvedContext(ClusterContext Context, string? Label, string? Color);

public static class FolderPath
{
    /// <summary>The colors a folder can set.</summary>
    public static IReadOnlyList<string> Colors { get; } = ["success", "warning", "danger", "accent", "neutral"];

    /// <summary>Trims the path, drops empty segments and leading or trailing slashes; null when nothing is left.</summary>
    public static string? Normalize(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var segments = path.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? string.Join('/', segments) : null;
    }

    /// <summary>The folder and every folder it's in, outermost first: <c>prod</c>, <c>prod/europe</c>.</summary>
    public static IEnumerable<string> Ancestors(string path)
    {
        if (Normalize(path) is not { } normalized)
        {
            yield break;
        }

        for (var slash = normalized.IndexOf('/'); slash >= 0; slash = normalized.IndexOf('/', slash + 1))
        {
            yield return normalized[..slash];
        }
        yield return normalized;
    }
}
