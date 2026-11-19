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
}
