using System.Text;
using Cellar.Primitives;
using Cellar.Text;
using Cellar.Theming;
using Sfx.Core;

namespace Sfx.Tui;

/// <summary>Colors pretty-printed JSON and XML a line at a time: names, values, and the punctuation between them.</summary>
public static class Highlight
{
    public static IReadOnlyList<StyledText> Lines(string text, Inspect.Format format, string indent = "")
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var inComment = false;
        return lines.Select(line => format switch
        {
            Inspect.Format.Json => Json(indent + line),
            Inspect.Format.Xml => Xml(indent + line, ref inComment),
            _ => new StyledText(indent + line, Theme.Current.TextStyle),
        }).ToList();
    }

    public static StyledText Json(string line)
    {
        var theme = Theme.Current;
        var text = StyledText.Empty();
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (c == '"')
            {
                var end = StringEnd(line, i);
                var next = line.AsSpan(end).TrimStart();
                text.Append(line[i..end]).Fg(next.StartsWith(":") ? theme.Accent : theme.Success);
                i = end;
            }
            else if (char.IsDigit(c) || c == '-' || line.AsSpan(i).StartsWith("true") || line.AsSpan(i).StartsWith("false") || line.AsSpan(i).StartsWith("null"))
            {
                var end = i;
                while (end < line.Length && line[end] is not (',' or ' ' or ']' or '}'))
                {
                    end++;
                }
                text.Append(line[i..end]).Fg(theme.Warning);
                i = end;
            }
            else
            {
                var end = i;
                while (end < line.Length && line[end] != '"' && !char.IsDigit(line[end]) && line[end] is not ('-' or 't' or 'f' or 'n'))
                {
                    end++;
                }
                end = Math.Max(end, i + 1);
                Append(text, line[i..end], char.IsWhiteSpace(line[i]) ? theme.Foreground : theme.Muted);
                i = end;
            }
        }
        return text;
    }

    /// <summary><paramref name="inComment"/> carries a comment that spans lines over to the next one.</summary>
    public static StyledText Xml(string line, ref bool inComment)
    {
        var theme = Theme.Current;
        var text = StyledText.Empty();
        var i = 0;
        while (i < line.Length)
        {
            if (inComment || line.AsSpan(i).StartsWith("<!--"))
            {
                var close = line.IndexOf("-->", i, StringComparison.Ordinal);
                var end = close < 0 ? line.Length : close + 3;
                text.Append(line[i..end]).Fg(theme.Muted);
                inComment = close < 0;
                i = end;
            }
            else if (line[i] == '<')
            {
                i = Tag(line, i, text);
            }
            else
            {
                var end = line.IndexOf('<', i);
                end = end < 0 ? line.Length : end;
                Append(text, line[i..end], theme.Foreground);
                i = end;
            }
        }
        return text;
    }

    /// <summary>One tag from its '<': the name, then attribute names and values, up to its '>'.</summary>
    private static int Tag(string line, int i, StyledText text)
    {
        var theme = Theme.Current;
        var nameEnd = i + 1;
        while (nameEnd < line.Length && line[nameEnd] is not (' ' or '>' or '/') || nameEnd == i + 1 && line[nameEnd] is '/' or '?' or '!')
        {
            nameEnd++;
        }
        text.Append(line[i..nameEnd]).Fg(theme.Accent);
        i = nameEnd;

        while (i < line.Length && line[i] != '>')
        {
            if (line[i] == '"' || line[i] == '\'')
            {
                var close = line.IndexOf(line[i], i + 1);
                var end = close < 0 ? line.Length : close + 1;
                text.Append(line[i..end]).Fg(theme.Success);
                i = end;
            }
            else if (char.IsLetter(line[i]) || line[i] is ':' or '_')
            {
                var end = i;
                while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] is ':' or '_' or '-' or '.'))
                {
                    end++;
                }
                text.Append(line[i..end]).Fg(theme.Info);
                i = end;
            }
            else
            {
                Append(text, line[i].ToString(), line[i] == ' ' ? theme.Foreground : theme.Accent);
                i++;
            }
        }
        if (i < line.Length)
        {
            text.Append(">").Fg(theme.Accent);
            i++;
        }
        return i;
    }

    private static int StringEnd(string line, int start)
    {
        var i = start + 1;
        while (i < line.Length)
        {
            if (line[i] == '\\')
            {
                i += 2;
                continue;
            }
            if (line[i] == '"')
            {
                return i + 1;
            }
            i++;
        }
        return line.Length;
    }

    private static void Append(StyledText text, string part, Color color) => text.Append(part).Fg(color);
}
