using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Terminal;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Sfx.Core;

namespace Sfx.Tui;

/// <summary>
/// Asked at startup when there are saved contexts but none is active (and no endpoint or <c>-c</c> was given): the
/// contexts page, on its own; Enter opens one (and makes it the active one), <c>q</c> or Esc quits.
/// </summary>
internal static class ContextPicker
{
    public static async Task<ClusterContext?> PickAsync(ITerminal terminal, ContextStore store)
    {
        using var app = new App(terminal);
        ClusterContext? picked = null;
        var view = Views.Contexts(store, () => "", context =>
        {
            picked = context;
            app.Quit();
        });
        (await view.LoadAsync(CancellationToken.None))();
        view.Widget.HasFocus = true;
        SelectFirst(view, terminal.Size);

        var header = new Label(StyledText.Of(" sfx ").Bold().Fg(Theme.Current.Accent)
            .Append("no active context: which cluster?").Fg(Theme.Current.Foreground));
        var footer = new Label(Styles.Muted(" enter opens it and makes it the active one · ↑/↓ or j/k choose · q quits"));
        app.Root = new Stack(Direction.Vertical)
            .Add(header, Constraint.Length(1))
            .Add(view.Widget, Constraint.Fill())
            .Add(footer, Constraint.Length(1));
        app.OnUnhandledEvent = e => Handle(app, view, e);
        await app.RunAsync(CancellationToken.None);
        return picked;
    }

    /// <summary>Puts the cursor on the first context rather than on its folder's heading.</summary>
    internal static void SelectFirst(ResourceView<ResolvedContext> view, Size size)
    {
        view.Widget.Render(new Cellar.Rendering.Surface(size.Width, size.Height), new Rect(0, 0, size.Width, size.Height));
        for (var i = 0; i < 3 && view.Selected is null; i++)
        {
            view.Widget.OnEvent(new KeyEvent(Key.Down, default, KeyModifiers.None));
        }
    }

    /// <summary>Keys the table doesn't take itself: Enter opens, j/k move, q, Esc and ctrl+c quit.</summary>
    internal static bool Handle(App app, IResourceView view, InputEvent e)
    {
        if (e is not KeyEvent key)
        {
            return false;
        }

        KeyEvent? forward = key switch
        {
            { Key: Key.Enter } => key,
            { Key: Key.Char, Rune.Value: 'j' } => new KeyEvent(Key.Down, default, KeyModifiers.None),
            { Key: Key.Char, Rune.Value: 'k' } => new KeyEvent(Key.Up, default, KeyModifiers.None),
            { Key: Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown } => key,
            _ => null,
        };
        if (forward is not null)
        {
            return view.Widget.OnEvent(forward);
        }

        if (key is { Key: Key.Escape } or { Key: Key.Char, Rune.Value: 'q' } || (key.Modifiers.HasFlag(KeyModifiers.Control) && key.Rune.Value is 'c' or 'C'))
        {
            app.Quit();
            return true;
        }
        return false;
    }
}
