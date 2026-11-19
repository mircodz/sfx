using System.Globalization;

namespace Sfx.Tui;

/// <summary>Auto-refresh intervals as typed: <c>30</c> (seconds), <c>2s</c>, <c>1m</c>, <c>1h</c>, or <c>off</c>/<c>0</c>.</summary>
public static class RefreshInterval
{
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(1);

    /// <summary>Zero means off. Intervals under <see cref="Minimum"/> are refused: they'd only load the cluster.</summary>
    public static bool TryParse(string text, out TimeSpan interval)
    {
        interval = TimeSpan.Zero;
        text = text.Trim().ToLowerInvariant();
        if (text is "off" or "0")
        {
            return true;
        }

        var (number, unit) = text.Length > 0 && char.IsLetter(text[^1]) ? (text[..^1], text[^1]) : (text, 's');
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        interval = unit switch
        {
            's' => TimeSpan.FromSeconds(value),
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            _ => TimeSpan.MinValue,
        };
        return interval == TimeSpan.Zero || interval >= Minimum;
    }

    /// <summary>"5s", "2m", "1h"; "off" for zero.</summary>
    public static string Format(TimeSpan interval) => interval switch
    {
        { Ticks: <= 0 } => "off",
        { TotalSeconds: < 60 } t => $"{(int)t.TotalSeconds}s",
        { TotalMinutes: < 60 } t when t.Seconds == 0 => $"{(int)t.TotalMinutes}m",
        { TotalMinutes: >= 60 } t when t.Minutes == 0 && t.Seconds == 0 => $"{(int)t.TotalHours}h",
        var t => $"{(int)t.TotalSeconds}s",
    };
}
