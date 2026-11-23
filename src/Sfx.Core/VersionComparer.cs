namespace Sfx.Core;

/// <summary>Orders versions like "1.10.0" after "1.9.0", falling back to text for anything else.</summary>
public sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y) =>
        Version.TryParse(x, out var a) && Version.TryParse(y, out var b) ? a.CompareTo(b) : string.CompareOrdinal(x, y);
}
