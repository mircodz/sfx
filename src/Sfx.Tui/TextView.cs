using Cellar.Primitives;
using Cellar.Rendering;
using Cellar.Terminal;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;

namespace Sfx.Tui;

/// <summary>
/// Read-only lines of styled text: scrolls both ways (arrows, PgUp/PgDn, Home/End, the wheel) and highlights the
/// matches of a search, which <see cref="NextMatch"/> steps through.
/// </summary>
public sealed class TextView : Widget
{
    private const int WheelStep = 3;

    private IReadOnlyList<StyledText> _lines = [];
    private string[] _plain = [];
    private List<(int Line, int Column)> _matches = [];
    private int _match = -1;
    private int _top;
    private int _left;
    private Rect _area;

    public override bool IsFocusable => true;

    public string Search { get; private set; } = "";

    public int MatchCount => _matches.Count;

    /// <summary>1-based index of the current match, or 0 when there is none.</summary>
    public int CurrentMatch => _match + 1;

    public string Text => string.Join('\n', _plain);

    public void SetLines(IReadOnlyList<StyledText> lines)
    {
        _lines = lines;
        _plain = lines.Select(line => line.PlainText).ToArray();
        FindMatches(keepPosition: true);
        _top = Math.Clamp(_top, 0, Math.Max(0, _lines.Count - 1));
    }

    /// <summary>Highlights <paramref name="query"/> (case-insensitive) and scrolls to its first match below the top line.</summary>
    public void Find(string query)
    {
        Search = query;
        FindMatches(keepPosition: false);
    }

    /// <summary>Moves to the next (<paramref name="direction"/> 1) or previous (-1) match, wrapping around.</summary>
    public void NextMatch(int direction)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        _match = ((_match < 0 ? 0 : _match + direction) % _matches.Count + _matches.Count) % _matches.Count;
        ScrollToMatch();
    }

    public override void Render(Surface surface, Rect area)
    {
        _area = area;
        if (area.IsEmpty)
        {
            return;
        }

        var overflowing = _lines.Count > area.Height;
        var text = overflowing && area.Width > 1 ? new Rect(area.X, area.Y, area.Width - 1, area.Height) : area;
        var highlight = new Style(Theme.Current.Background, Theme.Current.Warning);
        var current = new Style(Theme.Current.Background, Theme.Current.Accent);

        surface.SetClip(text);
        for (var row = 0; row < text.Height && _top + row < _lines.Count; row++)
        {
            var index = _top + row;
            var y = text.Y + row;
            // Drawn from left of the clip so the line scrolls horizontally.
            TextRenderer.DrawLine(surface, text.X - _left, y, text.Width + _left, _lines[index], Justify.Left);

            if (Search.Length == 0)
            {
                continue;
            }

            for (var m = 0; m < _matches.Count; m++)
            {
                if (_matches[m].Line != index)
                {
                    continue;
                }

                var style = m == _match ? current : highlight;
                for (var c = 0; c < Search.Length; c++)
                {
                    var x = text.X - _left + _matches[m].Column + c;
                    if (x >= text.X && x < text.Right)
                    {
                        surface.Set(x, y, surface.Get(x, y).WithStyle(style));
                    }
                }
            }
        }
        surface.ResetClip();

        if (overflowing && area.Width > 1)
        {
            Scrollbar.Default.Render(surface, new Rect(area.Right - 1, area.Y, 1, area.Height), _top, area.Height, _lines.Count);
        }
    }

    public override bool OnEvent(InputEvent e)
    {
        switch (e)
        {
            case MouseEvent { Kind: MouseEventKind.Wheel } wheel when _area.Contains(wheel.X, wheel.Y):
                ScrollBy(wheel.Button == MouseButton.WheelUp ? -WheelStep : WheelStep);
                return true;
            case KeyEvent { Modifiers: KeyModifiers.None or KeyModifiers.Shift } key:
                return OnKey(key.Key);
            default:
                return false;
        }
    }

    private bool OnKey(Key key)
    {
        var page = Math.Max(1, _area.Height - 1);
        switch (key)
        {
            case Key.Up: ScrollBy(-1); return true;
            case Key.Down: ScrollBy(1); return true;
            case Key.PageUp: ScrollBy(-page); return true;
            case Key.PageDown: ScrollBy(page); return true;
            case Key.Home: _top = 0; _left = 0; return true;
            case Key.End: _top = MaxTop; return true;
            case Key.Left: _left = Math.Max(0, _left - 8); return true;
            case Key.Right: _left += 8; return true;
            default: return false;
        }
    }

    private int MaxTop => Math.Max(0, _lines.Count - Math.Max(1, _area.Height));

    private void ScrollBy(int delta) => _top = Math.Clamp(_top + delta, 0, MaxTop);

    private void FindMatches(bool keepPosition)
    {
        var previous = _match >= 0 && _match < _matches.Count ? _matches[_match] : default;
        _matches = [];
        if (Search.Length > 0)
        {
            for (var line = 0; line < _plain.Length; line++)
            {
                for (var column = _plain[line].IndexOf(Search, StringComparison.OrdinalIgnoreCase);
                     column >= 0;
                     column = _plain[line].IndexOf(Search, column + Search.Length, StringComparison.OrdinalIgnoreCase))
                {
                    _matches.Add((line, column));
                }
            }
        }

        if (_matches.Count == 0)
        {
            _match = -1;
            return;
        }

        if (keepPosition && _match >= 0)
        {
            var same = _matches.IndexOf(previous);
            _match = same >= 0 ? same : Math.Min(_match, _matches.Count - 1);
            return;
        }

        _match = Math.Max(0, _matches.FindIndex(m => m.Line >= _top));
        ScrollToMatch();
    }

    private void ScrollToMatch()
    {
        var (line, column) = _matches[_match];
        var height = Math.Max(1, _area.Height);
        if (line < _top || line >= _top + height)
        {
            _top = Math.Clamp(line - height / 3, 0, MaxTop);
        }

        var width = Math.Max(1, _area.Width - 1);
        if (column < _left || column + Search.Length > _left + width)
        {
            _left = Math.Max(0, column - width / 3);
        }
    }
}
