using Cellar.Primitives;
using Cellar.Text;
using Cellar.Theming;
using Microsoft.ServiceFabric.Common;

namespace Sfx.Tui;

internal static class Styles
{
    public static StyledText Plain(string text) => text.Length == 0 ? Muted("-") : StyledText.Of(text);

    public static StyledText Muted(string text) => new(text, Theme.Current.MutedStyle);

    public static StyledText Colored(string text, Color color) => StyledText.Of(text).Fg(color);

    public static StyledText Health(HealthState health) => health switch
    {
        HealthState.Ok => Colored("Ok", Theme.Current.Success),
        HealthState.Warning => Colored("Warning", Theme.Current.Warning),
        HealthState.Error => Colored("Error", Theme.Current.Error),
        _ => Muted("-"),
    };

    public static StyledText Status(string status) => status switch
    {
        "" => Muted("-"),
        "Up" or "Ready" or "Active" => Colored(status, Theme.Current.Success),
        "Down" or "Failed" or "Dropped" or "Invalid" or "InQuorumLoss" => Colored(status, Theme.Current.Error),
        _ => Colored(status, Theme.Current.Warning),
    };

    public static string Duration(TimeSpan? value) => value switch
    {
        null => "-",
        { TotalDays: >= 1 } t => $"{(int)t.TotalDays}d{t.Hours}h",
        { TotalHours: >= 1 } t => $"{(int)t.TotalHours}h{t.Minutes}m",
        { } t => $"{(int)t.TotalMinutes}m",
    };

    /// <summary>How long ago: "45s", "4m", "2h5m", "3d1h".</summary>
    public static string Age(DateTime? utc, DateTime? now = null)
    {
        if (utc is not { } at)
        {
            return "-";
        }

        var age = (now ?? DateTime.UtcNow) - at;
        return age < TimeSpan.Zero ? "0s" : age.TotalMinutes < 1 ? $"{(int)age.TotalSeconds}s" : Duration(age);
    }

    /// <summary>"■■□□□ 2/5", in <paramref name="color"/>; "-" when there's nothing to count.</summary>
    public static StyledText Progress(int done, int total, Color color)
    {
        if (total <= 0)
        {
            return Muted("-");
        }

        var filled = Math.Clamp(done, 0, total);
        return StyledText.Of(new string('■', filled)).Fg(color)
            .Append(new string('□', total - filled)).Fg(Theme.Current.Muted)
            .Append($" {done}/{total}").Fg(Theme.Current.Foreground);
    }

    /// <summary>An upgrade state in words, colored by whether it needs attention.</summary>
    public static StyledText UpgradeState(string state) => state switch
    {
        "RollingForwardInProgress" => Colored("rolling forward", Theme.Current.Warning),
        "RollingBackInProgress" => Colored("rolling back", Theme.Current.Error),
        "RollingForwardPending" => Colored("waiting to resume", Theme.Current.Warning),
        "RollingForwardCompleted" => Colored("done", Theme.Current.Success),
        "RollingBackCompleted" => Colored("rolled back", Theme.Current.Error),
        "Failed" => Colored("failed", Theme.Current.Error),
        "" => Muted("-"),
        _ => Plain(state),
    };

    public static Color UpgradeColor(string state) => state switch
    {
        "RollingForwardCompleted" => Theme.Current.Success,
        "RollingForwardInProgress" or "RollingForwardPending" => Theme.Current.Warning,
        _ => Theme.Current.Error,
    };
}
