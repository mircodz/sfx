using System.Globalization;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace Sfx.Core;

/// <summary>
/// Edits TOML as text, touching only the lines of the keys and tables that change, so comments, blank lines and the
/// layout of everything else survive. An edit returns null when the text isn't laid out in a way it can edit.
/// </summary>
internal static class TomlText
{
    /// <summary>Sets a key of the <c>[table]</c> at <paramref name="table"/>, or of the root when empty; null removes it.</summary>
    public static string? SetKey(string text, IReadOnlyList<string> table, string key, object? value)
    {
        if (Layout.Of(text) is not { } layout)
        {
            return null;
        }

        var index = table.Count == 0 ? -1 : layout.Find(table);
        if (table.Count > 0 && index < 0)
        {
            return null;
        }

        var items = index < 0 ? layout.Document.KeyValues : layout.Tables[index].Items;
        if (items.FirstOrDefault(item => KeyPath(item.Key).SequenceEqual([key])) is { } existing)
        {
            if (value is null)
            {
                return layout.Remove(layout.LineOf(existing.Span.Start.Offset), layout.LineOf(existing.Span.End.Offset) + 1);
            }
            var start = existing.Value!.Span.Start.Offset;
            var end = existing.Value.Span.End.Offset + 1;
            return Same(text[start..end], value) ? text : text[..start] + Value(value) + text[end..];
        }

        if (value is null)
        {
            return text;
        }
        var line = Line(key, value);
        return index >= 0 ? layout.Insert(layout.ContentEnd(index) + 1, line, blankBefore: false, blankAfter: false)
            : layout.RootEnd >= 0 ? layout.Insert(layout.RootEnd + 1, line, blankBefore: false, blankAfter: false)
            : layout.Tables.Count > 0 ? layout.Insert(layout.Lead(0), line, blankBefore: false, blankAfter: true)
            : layout.Insert(layout.Count, line, blankBefore: false, blankAfter: false);
    }

    /// <summary>Adds <c>[path]</c> after the last table under the same top-level key, or at the end.</summary>
    public static string? AddTable(string text, IReadOnlyList<string> path, TomlTable values)
    {
        if (Layout.Of(text) is not { } layout || layout.Find(path) >= 0)
        {
            return null;
        }

        var block = string.Join("\n", [$"[{Header(path)}]", .. values.Select(entry => Line(entry.Key, entry.Value))]);
        var sibling = layout.Tables.FindLastIndex(table => KeyPath(table.Name) is [var top, ..] && top == path[0]);
        return sibling >= 0
            ? layout.Insert(layout.TailEnd(sibling), block, blankBefore: true, blankAfter: true)
            : layout.Insert(layout.Count, block, blankBefore: true, blankAfter: false);
    }

    /// <summary>Removes <c>[path]</c> with its keys, the comments right above it, and the blank lines after it.</summary>
    public static string? RemoveTable(string text, IReadOnlyList<string> path)
    {
        if (Layout.Of(text) is not { } layout || layout.Find(path) is var index && index < 0)
        {
            return null;
        }

        var start = layout.Lead(index);
        var end = layout.TailEnd(index);
        if (end >= layout.Count)
        {
            while (start - 1 > layout.Floor(index) && layout.IsBlank(start - 1))
            {
                start--;
            }
        }
        return layout.Remove(start, end);
    }

    public static string? RenameTable(string text, IReadOnlyList<string> path, IReadOnlyList<string> newPath)
    {
        if (Layout.Of(text) is not { } layout || layout.Find(path) is var index && index < 0)
        {
            return null;
        }

        var name = layout.Tables[index].Name!.Span;
        return text[..name.Start.Offset] + Header(newPath) + text[(name.End.Offset + 1)..];
    }

    /// <summary>The model of <paramref name="text"/>, or null if it isn't valid TOML.</summary>
    public static TomlTable? Parse(string text)
    {
        if (SyntaxParser.Parse(text).HasErrors)
        {
            return null;
        }
        try
        {
            return TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
        }
        catch (TomlException)
        {
            return null;
        }
    }

    /// <summary>A value as text that's equal for equal values, whatever their key order; empty tables don't count.</summary>
    public static string Canonical(object? value) => value switch
    {
        TomlTable table => "{" + string.Join(
            ",",
            table.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => (entry.Key, Text: Canonical(entry.Value)))
                .Where(entry => entry.Text != "{}")
                .Select(entry => JsonSerializer.Serialize(entry.Key) + ":" + entry.Text)) + "}",
        TomlArray array => "[" + string.Join(",", array.Select(Canonical)) + "]",
        TomlTableArray tables => "[" + string.Join(",", tables.Select(Canonical)) + "]",
        string text => JsonSerializer.Serialize(text),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => value.GetType().Name + ":" + formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? "null",
    };

    private static bool Same(string valueText, object value) =>
        Parse("v = " + valueText) is { } parsed && Canonical(parsed["v"]) == Canonical(value);

    private static string Line(string key, object value) => TomlSerializer.Serialize(new TomlTable { [key] = value }).Trim();

    private static string Value(object value) => Line("v", value)["v = ".Length..];

    private static string Header(IReadOnlyList<string> path) =>
        string.Join('.', path.Select(key => key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? key : Value(key)));

    private static IReadOnlyList<string> KeyPath(KeySyntax? key) =>
        key is null ? [] : [Name(key.Key), .. key.DotKeys.Select(dotted => Name(dotted.Key))];

    private static string Name(BareKeyOrStringValueSyntax? key) => key switch
    {
        BareKeySyntax bare => bare.Key?.Text ?? "",
        StringValueSyntax text => text.Value ?? "",
        _ => "",
    };

    /// <summary>
    /// The lines of a file and which belong to which table. A table owns the comments right above its header, and
    /// the comments right after its keys unless they run into the next table's header; then the blank lines after.
    /// </summary>
    private sealed class Layout
    {
        private readonly string _text;
        private readonly string _newLine;
        private readonly List<int> _starts = [];

        private Layout(string text, DocumentSyntax document)
        {
            _text = text;
            _newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            Document = document;
            Tables = [.. document.Tables];
            for (var i = 0; i < text.Length; i++)
            {
                if (i == 0 || text[i - 1] == '\n')
                {
                    _starts.Add(i);
                }
            }
            RootEnd = document.KeyValues.LastOrDefault() is { } last ? LineOf(last.Span.End.Offset) : -1;
        }

        public static Layout? Of(string text) => SyntaxParser.Parse(text) is { HasErrors: false } document ? new Layout(text, document) : null;

        public DocumentSyntax Document { get; }

        public List<TableSyntaxBase> Tables { get; }

        public int Count => _starts.Count;

        /// <summary>The last line of the root's keys, or -1.</summary>
        public int RootEnd { get; }

        public int Find(IReadOnlyList<string> path) => Tables.FindIndex(table => table is TableSyntax && KeyPath(table.Name).SequenceEqual(path));

        public int LineOf(int offset)
        {
            var index = _starts.BinarySearch(offset);
            return index >= 0 ? index : ~index - 1;
        }

        public int ContentEnd(int table) => LineOf(Tables[table].Span.End.Offset);

        /// <summary>The last line that isn't the table's: the root's last key, or the previous table's.</summary>
        public int Floor(int table) => table == 0 ? RootEnd : ContentEnd(table - 1);

        public int Lead(int table)
        {
            var line = LineOf(Tables[table].Span.Start.Offset);
            while (line - 1 > Floor(table) && IsComment(line - 1))
            {
                line--;
            }
            return line;
        }

        /// <summary>The first line after the table's.</summary>
        public int TailEnd(int table)
        {
            var next = table + 1 < Tables.Count ? Lead(table + 1) : Count;
            var line = ContentEnd(table) + 1;
            while (line < next && IsComment(line))
            {
                line++;
            }
            while (line < next && IsBlank(line))
            {
                line++;
            }
            return line;
        }

        public bool IsBlank(int line) => line < Count && Text(line).Length == 0;

        public string Remove(int start, int end) => _text[..Start(start)] + _text[Start(end)..];

        /// <summary>Inserts lines before <paramref name="line"/>, with a blank line around them if asked and not there yet.</summary>
        public string Insert(int line, string lines, bool blankBefore, bool blankAfter)
        {
            var offset = Start(line);
            var before = offset == _text.Length && _text.Length > 0 && !_text.EndsWith('\n') ? _newLine : "";
            if (blankBefore && line > 0 && !IsBlank(line - 1))
            {
                before += _newLine;
            }
            var after = blankAfter && line < Count && !IsBlank(line) ? _newLine : "";
            return _text[..offset] + before + lines.ReplaceLineEndings(_newLine) + _newLine + after + _text[offset..];
        }

        private bool IsComment(int line) => line < Count && Text(line).StartsWith('#');

        private string Text(int line) => _text[Start(line)..Start(line + 1)].Trim();

        private int Start(int line) => line < Count ? _starts[line] : _text.Length;
    }
}
