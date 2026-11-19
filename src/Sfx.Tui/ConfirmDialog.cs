using Cellar.Primitives;
using Cellar.Rendering;
using Cellar.Terminal;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;

namespace Sfx.Tui;

/// <summary>
/// Asks before an operation runs: y/Enter for a plain one, typing the target's name for a destructive one.
/// Option keys toggle the operation's switches (e.g. <c>i</c> to ignore placement constraints).
/// </summary>
internal sealed class ConfirmDialog : Widget
{
    private readonly Operation _operation;
    private readonly Action<IReadOnlySet<char>> _confirm;
    private readonly Action _cancel;
    private readonly HashSet<char> _enabled = [];
    private readonly Input _input = new() { HasFocus = true };
    private readonly Label _body = new(StyledText.Empty()) { Wrap = true };
    private readonly Label _hint = new(StyledText.Empty());
    private readonly Panel _panel;
    private bool _mismatch;

    public ConfirmDialog(Operation operation, Action<IReadOnlySet<char>> confirm, Action cancel)
    {
        _operation = operation;
        _confirm = confirm;
        _cancel = cancel;

        var layout = new Cellar.Widgets.Stack(Cellar.Layout.Direction.Vertical)
            .Add(_body, Cellar.Layout.Constraint.Fill());
        if (Typed)
        {
            layout.Add(new Panel(_input), Cellar.Layout.Constraint.Length(3));
        }
        layout.Add(_hint, Cellar.Layout.Constraint.Length(1));

        _panel = new Panel(new Padding(layout, new Thickness(1, 0)), Typed ? " confirm · destructive " : " confirm ")
        {
            BorderStyle = BorderStyle.Rounded,
            Background = Theme.Current.Background,
            BorderLineStyle = new Style(Typed ? Theme.Current.Error : Theme.Current.Warning, Theme.Current.Background),
        };
        Update();
    }

    /// <summary>Rows the dialog needs at <paramref name="width"/> columns, borders included.</summary>
    public int Height(int width) =>
        TextRenderer.Wrap(BodyText(), Math.Max(10, width - 4)).Count + (Typed ? 3 : 0) + 1 + 2;

    public override bool IsFocusable => true;

    private bool Typed => _operation.Risk == ActionRisk.Destructive;

    public override bool OnEvent(InputEvent e)
    {
        if (e is not KeyEvent key)
        {
            return false;
        }

        switch (key.Key)
        {
            case Key.Escape:
                _cancel();
                return true;
            case Key.Enter:
                Submit();
                return true;
        }

        if (!Typed && key.IsChar && key.Modifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            var c = (char)key.Rune.Value;
            if (c is 'y' or 'Y')
            {
                Submit();
            }
            else if (c is 'n' or 'N')
            {
                _cancel();
            }
            else if (_operation.Options?.Any(o => o.Key == c) == true && !_enabled.Remove(c))
            {
                _enabled.Add(c);
            }
            Update();
            return true;
        }

        if (Typed)
        {
            _input.OnEvent(key);
            _mismatch = false;
            Update();
        }
        return true;
    }

    public override void Render(Surface surface, Rect area) => _panel.Render(surface, area);

    private void Submit()
    {
        if (Typed && _input.Text.Trim() != _operation.Target)
        {
            _mismatch = true;
            Update();
            return;
        }
        _confirm(_enabled);
    }

    private StyledText BodyText()
    {
        var text = StyledText.Of(_operation.Title).Bold().Fg(Theme.Current.Foreground);
        if (_operation.Warning is { } warning)
        {
            text.Append("\n\n" + warning).Fg(Theme.Current.Warning);
        }
        foreach (var option in _operation.Options ?? [])
        {
            var on = _enabled.Contains(option.Key);
            text.Append($"\n\n[{option.Key}] ").Fg(Theme.Current.Accent)
                .Append($"{option.Label}: ").Fg(Theme.Current.Muted)
                .Append(on ? "on" : "off").Fg(on ? Theme.Current.Warning : Theme.Current.Muted);
        }
        if (Typed)
        {
            text.Append("\n\nType ").Fg(Theme.Current.Muted)
                .Append(_operation.Target).Bold().Fg(Theme.Current.Accent)
                .Append(" to confirm.").Fg(Theme.Current.Muted);
        }
        return text;
    }

    private void Update()
    {
        _body.Content = BodyText();
        _hint.Content = _mismatch
            ? StyledText.Of("that doesn't match; esc cancels").Fg(Theme.Current.Error)
            : Typed
                ? new StyledText("enter run · esc cancel", Theme.Current.MutedStyle)
                : new StyledText("y/enter run · n/esc cancel", Theme.Current.MutedStyle);
    }
}
