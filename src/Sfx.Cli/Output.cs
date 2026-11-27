using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Sfx.Web;

namespace Sfx.Cli;

/// <summary>Where a scripting command writes. <paramref name="Color"/>: whether to colour health words.</summary>
/// <param name="Interactive">Whether stdin and stdout are a terminal, so signing in may prompt.</param>
public sealed record CliConsole(TextWriter Out, TextWriter Error, bool Color, bool Interactive = false)
{
    public static CliConsole Standard() =>
        new(
            Console.Out,
            Console.Error,
            UseColor(!Console.IsOutputRedirected, Environment.GetEnvironmentVariable("NO_COLOR")),
            !Console.IsInputRedirected && !Console.IsOutputRedirected);

    /// <summary>Only on a terminal, and not when <c>NO_COLOR</c> is set to anything.</summary>
    public static bool UseColor(bool terminal, string? noColor) => terminal && string.IsNullOrEmpty(noColor);
}

/// <summary>A column of a table: its header, its cell, and whether only <c>-o wide</c> shows it.</summary>
internal sealed record Column<T>(string Header, Func<T, string?> Cell, bool Wide = false, bool Health = false, bool Right = false);

internal static class Output
{
    private const string Gap = "   ";

    /// <summary>The web server's JSON (camelCase, its escaping), indented, without the browser's action signatures.</summary>
    public static readonly JsonSerializerOptions Json = new(ApiReads.Json)
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { DropSignature } },
    };

    // A signature only means something to the server that made it, for the browser to send back.
    private static void DropSignature(JsonTypeInfo info)
    {
        if (info.Type == typeof(ActionDto) && info.Properties.FirstOrDefault(p => p.Name == "signature") is { } signature)
        {
            info.Properties.Remove(signature);
        }
    }

    public static void WriteJson<T>(TextWriter writer, T value) => writer.WriteLine(JsonSerializer.Serialize(value, Json));

    public static void WriteJson(TextWriter writer, JsonNode node) => writer.WriteLine(node.ToJsonString(Json));

    public static JsonNode ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Json)!;

    /// <summary>One line, as the web server writes its errors.</summary>
    public static string JsonError(string message) => JsonSerializer.Serialize(new { error = message }, ApiReads.Json);

    /// <summary>Aligned columns, a header line unless <paramref name="headers"/> is false, no trailing spaces.</summary>
    public static void Table<T>(CliConsole console, IReadOnlyList<T> rows, IReadOnlyList<Column<T>> columns, bool headers = true, string indent = "")
    {
        var cells = rows.Select(r => columns.Select(c => OneLine(c.Cell(r))).ToArray()).ToList();
        var widths = columns
            .Select((c, i) => Math.Max(headers ? c.Header.Length : 0, cells.Count == 0 ? 0 : cells.Max(row => row[i].Length)))
            .ToArray();
        if (headers)
        {
            console.Out.WriteLine(indent + Row(columns.Select(c => c.Header).ToArray(), columns, widths, null));
        }
        foreach (var row in cells)
        {
            console.Out.WriteLine(indent + Row(row, columns, widths, console.Color ? columns : null));
        }
    }

    private static string Row<T>(string[] cells, IReadOnlyList<Column<T>> columns, int[] widths, IReadOnlyList<Column<T>>? colored)
    {
        var line = new System.Text.StringBuilder();
        for (var i = 0; i < cells.Length; i++)
        {
            var last = i == cells.Length - 1;
            var pad = new string(' ', widths[i] - cells[i].Length);
            var text = colored?[i].Health is true ? Health(cells[i], true) : cells[i];
            line.Append(columns[i].Right ? pad + text : last ? text : text + pad);
            if (!last)
            {
                line.Append(Gap);
            }
        }
        return line.ToString().TrimEnd();
    }

    /// <summary>Label and value pairs, the values lined up; empty values are left out.</summary>
    public static void Fields(CliConsole console, IEnumerable<(string Label, string? Value)> fields, string indent = "")
    {
        var shown = fields.Where(f => !string.IsNullOrEmpty(f.Value)).ToList();
        var width = shown.Count == 0 ? 0 : shown.Max(f => f.Label.Length) + 1;
        foreach (var (label, value) in shown)
        {
            var text = label == "Health" ? Health(value!, console.Color) : value!;
            var lines = text.ReplaceLineEndings("\n").Split('\n');
            console.Out.WriteLine($"{indent}{(label + ":").PadRight(width)}  {lines[0]}");
            foreach (var more in lines.Skip(1))
            {
                console.Out.WriteLine($"{indent}{new string(' ', width)}  {more}");
            }
        }
    }

    public static void Section(CliConsole console, string title)
    {
        console.Out.WriteLine();
        console.Out.WriteLine(title);
    }

    /// <summary>A health word in its colour: green, yellow or red.</summary>
    public static string Health(string health, bool color) => !color ? health : health switch
    {
        "Healthy" => $"\e[32m{health}\e[0m",
        "Warning" => $"\e[33m{health}\e[0m",
        "Error" => $"\e[31m{health}\e[0m",
        _ => health,
    };

    public static string OneLine(string? text) =>
        string.IsNullOrEmpty(text) ? "" : string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    /// <summary>"512 B", "3.4 KB", "1.2 GB": bytes in 1024s, one decimal below 10; "-" when unknown.</summary>
    public static string Bytes(long? bytes)
    {
        if (bytes is not { } value)
        {
            return "-";
        }
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value} B"
            : size < 10 ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{size:0.0} {units[unit]}")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{size:0} {units[unit]}");
    }

    /// <summary>"3d4h", "5h2m", "7m"; "-" when unknown.</summary>
    public static string Duration(TimeSpan? value) => value switch
    {
        null => "-",
        { TotalDays: >= 1 } t => $"{(int)t.TotalDays}d{t.Hours}h",
        { TotalHours: >= 1 } t => $"{(int)t.TotalHours}h{t.Minutes}m",
        { TotalMinutes: >= 1 } t => $"{(int)t.TotalMinutes}m",
        { } t => $"{(int)t.TotalSeconds}s",
    };

    /// <summary>UTC, to the second: "2026-11-27 10:04:05Z"; empty when unknown.</summary>
    public static string When(DateTime? time) => time is not { } t || t.Year <= 1
        ? ""
        : (t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t).ToString("yyyy-MM-dd HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
