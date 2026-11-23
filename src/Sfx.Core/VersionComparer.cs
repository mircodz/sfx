namespace Sfx.Core;

/// <summary>Orders versions like "1.10.0" after "1.9.0", falling back to text for anything else.</summary>
public sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var xIsVersion = Version.TryParse(x, out var a);
        var yIsVersion = Version.TryParse(y, out var b);
        return (xIsVersion, yIsVersion) switch
        {
            (true, true) => a!.CompareTo(b),
            // A mixed pair always orders the same way, so sorting a mixed list stays consistent.
            (true, false) => 1,
            (false, true) => -1,
            _ => string.CompareOrdinal(x, y),
        };
    }
}
