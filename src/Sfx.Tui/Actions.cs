using System.Text;
using Cellar.Terminal;

namespace Sfx.Tui;

/// <summary>A key with optional Ctrl: a character (<c>r</c>, <c>?</c>, <c>ctrl+p</c>) or a named key (<c>enter</c>).</summary>
public readonly record struct KeyChord(Key Key, char Char, bool Control)
{
    public static KeyChord Of(char c) => new(Key.Char, c, false);

    public static KeyChord Ctrl(char c) => new(Key.Char, char.ToLowerInvariant(c), true);

    public static KeyChord Named(Key key) => new(key, '\0', false);

    public bool Matches(KeyEvent e)
    {
        var control = (e.Modifiers & KeyModifiers.Control) != 0;
        if (Control != control || (e.Modifiers & KeyModifiers.Alt) != 0)
        {
            return false;
        }

        if (Key != Key.Char)
        {
            return e.Key == Key;
        }

        if (e.Rune.Value >= 0x10000)
        {
            return false;
        }

        // Ctrl+letter may arrive in either case.
        var c = (char)e.Rune.Value;
        return control ? char.ToLowerInvariant(c) == Char : e.IsChar && c == Char;
    }

    /// <summary>"ctrl+p", "?", "enter".</summary>
    public override string ToString() => Format("ctrl+", Key.ToString().ToLowerInvariant());

    /// <summary>"^p", "?", "bksp", for tight spaces like the footer.</summary>
    public string Short => Format("^", Key switch
    {
        Key.Backspace => "bksp",
        Key.Escape => "esc",
        _ => Key.ToString().ToLowerInvariant(),
    });

    private string Format(string controlPrefix, string keyName)
    {
        var text = new StringBuilder();
        if (Control)
        {
            text.Append(controlPrefix);
        }
        text.Append(Key == Key.Char ? Char.ToString() : keyName);
        return text.ToString();
    }
}

/// <summary>
/// Something the user can do: by key, by <c>:</c> command, from the palette, or by clicking it in the footer.
/// Every way in comes from this one declaration, so the help, palette and footer can't disagree.
/// </summary>
/// <param name="Run">Gets the text typed after the command name (<c>:ctx prod</c> → "prod"), or null.</param>
/// <param name="Keys">Keys that run it; the first is the one shown in the footer.</param>
/// <param name="Commands">Names accepted after <c>:</c>; the first is the one shown.</param>
/// <param name="Footer">Short label to show in the footer, or null to leave it out.</param>
/// <param name="Risk">Anything but None changes the cluster: hidden in read-only contexts and confirmed before it runs.</param>
/// <param name="Power">Only offered once power actions are unlocked (<c>:power</c>).</param>
public sealed record ShellAction(
    string Title,
    string Group,
    Action<string?> Run,
    IReadOnlyList<KeyChord>? Keys = null,
    IReadOnlyList<string>? Commands = null,
    string? Footer = null,
    ActionRisk Risk = ActionRisk.None,
    bool Power = false)
{
    public string? Command => Commands is { Count: > 0 } ? Commands[0] : null;

    /// <summary>How to invoke it, for help and the palette: "r  :refresh".</summary>
    public string Hint => string.Join("  ", new[]
    {
        Keys is { Count: > 0 } ? string.Join("/", Keys) : null,
        Command is null ? null : ":" + Command,
    }.Where(part => part is not null));
}

public static class ActionLookup
{
    /// <summary>The first action bound to the key; list view actions before global ones so a view can override.</summary>
    public static ShellAction? ForKey(IEnumerable<ShellAction> actions, KeyEvent key) =>
        actions.FirstOrDefault(action => action.Keys?.Any(chord => chord.Matches(key)) == true);

    /// <summary>Splits "ctx prod" into the action named "ctx" and its argument "prod".</summary>
    public static (ShellAction Action, string? Argument)? ForCommand(IEnumerable<ShellAction> actions, string text)
    {
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var action = actions.FirstOrDefault(a => a.Commands?.Contains(parts[0], StringComparer.OrdinalIgnoreCase) == true);
        return action is null ? null : (action, parts.Length > 1 ? parts[1] : null);
    }
}
