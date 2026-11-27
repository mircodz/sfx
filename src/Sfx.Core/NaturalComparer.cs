namespace Sfx.Core;

/// <summary>
/// Orders names as people read them, case aside: "_Node_2" before "_Node_10", "1.9.0" before "1.10.0". The browser's
/// <c>localeCompare</c> with <c>numeric</c>, near enough.
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            int order;
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var a = Number(x, ref i);
                var b = Number(y, ref j);
                order = a.Length != b.Length ? a.Length.CompareTo(b.Length) : a.SequenceCompareTo(b);
            }
            else
            {
                order = char.ToLowerInvariant(x[i++]).CompareTo(char.ToLowerInvariant(y[j++]));
            }
            if (order != 0)
            {
                return order;
            }
        }
        var rest = (x.Length - i).CompareTo(y.Length - j);
        // Equal but for case or leading zeros: still a fixed order.
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }

    /// <summary>The digits from <paramref name="at"/> on, without leading zeros; moves past them.</summary>
    private static ReadOnlySpan<char> Number(string text, ref int at)
    {
        var start = at;
        while (at < text.Length && char.IsAsciiDigit(text[at]))
        {
            at++;
        }
        return text.AsSpan(start, at - start).TrimStart('0');
    }
}
