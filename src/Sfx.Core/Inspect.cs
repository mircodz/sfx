using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ServiceFabric.Common;

namespace Sfx.Core;

/// <summary>A named value of an entity, for an overview.</summary>
public sealed record Field(string Name, string Value);

public sealed record HealthEventRow(
    string Source,
    string Property,
    HealthState State,
    string Description,
    DateTime? LastModifiedUtc,
    bool IsExpired);

/// <summary>One step of the chain explaining an unhealthy state; <see cref="Depth"/> nests it under the step above.</summary>
public sealed record HealthEvaluationRow(int Depth, string Kind, HealthState State, string Description);

/// <param name="Reason">One line on why it isn't healthy (see <see cref="HealthReasons.From"/>); null when nothing says.</param>
public sealed record HealthReport(
    HealthState State, IReadOnlyList<HealthEventRow> Events, IReadOnlyList<HealthEvaluationRow> Evaluations, string? Reason = null)
{
    public static HealthReport From(EntityHealth health) => new(
        health.AggregatedHealthState ?? HealthState.Unknown,
        (health.HealthEvents ?? [])
            .Select(e => new HealthEventRow(
                e.SourceId ?? "",
                e.Property ?? "",
                e.HealthState ?? HealthState.Unknown,
                e.Description ?? "",
                e.LastModifiedUtcTimestamp,
                e.IsExpired ?? false))
            .ToList(),
        Flatten(health.UnhealthyEvaluations),
        health.AggregatedHealthState is HealthState.Ok ? null : HealthReasons.From(health));

    /// <summary>Flattens nested unhealthy evaluations, depth first, so the cause reads top-down.</summary>
    public static IReadOnlyList<HealthEvaluationRow> Flatten(IEnumerable<HealthEvaluationWrapper>? evaluations)
    {
        var rows = new List<HealthEvaluationRow>();
        Flatten(evaluations, 0, rows);
        return rows;
    }

    private static void Flatten(IEnumerable<HealthEvaluationWrapper>? evaluations, int depth, List<HealthEvaluationRow> rows)
    {
        foreach (var evaluation in (evaluations ?? []).Select(w => w.HealthEvaluation).OfType<HealthEvaluation>())
        {
            rows.Add(new HealthEvaluationRow(
                depth,
                evaluation.Kind?.ToString() ?? "",
                evaluation.AggregatedHealthState ?? HealthState.Unknown,
                evaluation.Description ?? ""));

            // Each evaluation kind is its own type, but those that nest all call it this.
            var nested = evaluation.GetType().GetProperty("UnhealthyEvaluations")?.GetValue(evaluation);
            Flatten(nested as IEnumerable<HealthEvaluationWrapper>, depth + 1, rows);
        }
    }
}

/// <summary>Turns client models into JSON or fields by reflection, so every property shows without a mapping per type.</summary>
public static class Inspect
{
    private const int MaxDepth = 32;

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Json(object? value) => ToJson(value)?.ToJsonString(Indented) ?? "null";

    /// <summary>Re-indents a JSON document, such as the cluster configuration the cluster returns as one string.</summary>
    public static string PrettyJson(string json) => JsonNode.Parse(json)?.ToJsonString(Indented) ?? json;

    public enum Format { None, Json, Xml }

    /// <summary>
    /// Re-indents <paramref name="text"/> when it's a JSON object or array, or an XML document or fragment, and says
    /// which; anything else, or anything that doesn't parse, comes back unchanged with <see cref="Format.None"/>.
    /// </summary>
    public static (string Text, Format Format) TryPretty(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length < 2)
        {
            return (text ?? "", Format.None);
        }

        try
        {
            if (trimmed[0] is '{' or '[' && JsonNode.Parse(trimmed) is { } node)
            {
                return (node.ToJsonString(Indented), Format.Json);
            }

            if (trimmed[0] == '<')
            {
                return (PrettyXml(trimmed), Format.Xml);
            }
        }
        catch (Exception ex) when (ex is JsonException or System.Xml.XmlException)
        {
        }
        return (text!, Format.None);
    }

    /// <summary>Indents XML two spaces per level, keeping the declaration as written, comments and attribute order.</summary>
    private static string PrettyXml(string xml)
    {
        // Written to a string, the declaration would claim UTF-16; keep the original one instead.
        var declaration = "";
        if (xml.StartsWith("<?xml", StringComparison.Ordinal) && xml.IndexOf("?>", StringComparison.Ordinal) is var end and > 0)
        {
            declaration = xml[..(end + 2)] + "\n";
            xml = xml[(end + 2)..];
        }

        using var reader = System.Xml.XmlReader.Create(new StringReader(xml),
            new System.Xml.XmlReaderSettings { ConformanceLevel = System.Xml.ConformanceLevel.Auto, IgnoreWhitespace = true, DtdProcessing = System.Xml.DtdProcessing.Ignore });
        var output = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = System.Xml.XmlWriter.Create(output, new System.Xml.XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = true,
            ConformanceLevel = System.Xml.ConformanceLevel.Auto,
            NewLineChars = "\n",
        }))
        {
            writer.WriteNode(reader, defattr: false);
        }
        return declaration + output.ToString();
    }

    /// <summary>The top-level properties of <paramref name="value"/>; nested objects are shown as compact JSON.</summary>
    public static IReadOnlyList<Field> Fields(object value, params string[] exclude) =>
        Properties(value)
            .Where(p => !exclude.Contains(p.Name))
            .Select(p => (p.Name, Node: ToJson(p.Value, 1)))
            .Where(p => p.Node is not null)
            .Select(p => new Field(p.Name, p.Node is JsonValue scalar ? scalar.ToString() : p.Node!.ToJsonString(Compact)))
            .ToList();

    public static JsonNode? ToJson(object? value, int depth = 0)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return JsonValue.Create(text);
            case bool flag:
                return JsonValue.Create(flag);
            case Enum member:
                return JsonValue.Create(member.ToString());
            case DateTime time:
                return JsonValue.Create(time.ToString("o", CultureInfo.InvariantCulture));
            case DateTimeOffset time:
                return JsonValue.Create(time.ToString("o", CultureInfo.InvariantCulture));
            case TimeSpan span:
                return JsonValue.Create(span.ToString("c", CultureInfo.InvariantCulture));
            case Guid id:
                return JsonValue.Create(id.ToString());
            case byte[] bytes:
                return JsonValue.Create(Convert.ToBase64String(bytes));
            // Identifier wrappers whose only useful form is their text.
            case NodeName or FabricName or PartitionId or ReplicaId:
                return JsonValue.Create(value.ToString());
            case IFormattable number when value.GetType().IsPrimitive || value is decimal:
                return JsonNode.Parse(number.ToString(null, CultureInfo.InvariantCulture));
        }

        if (depth >= MaxDepth)
        {
            return JsonValue.Create("…");
        }

        if (value is IDictionary dictionary)
        {
            var map = new JsonObject();
            foreach (DictionaryEntry entry in dictionary)
            {
                map[entry.Key.ToString() ?? ""] = ToJson(entry.Value, depth + 1);
            }
            return map;
        }

        if (value is IEnumerable items)
        {
            var array = new JsonArray();
            foreach (var item in items)
            {
                array.Add(ToJson(item, depth + 1));
            }
            return array;
        }

        var obj = new JsonObject();
        foreach (var (name, property) in Properties(value))
        {
            if (ToJson(property, depth + 1) is { } node)
            {
                obj[name] = node;
            }
        }
        return obj;
    }

    private static IEnumerable<(string Name, object? Value)> Properties(object value)
    {
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead)
            {
                continue;
            }

            object? result;
            try
            {
                result = property.GetValue(value);
            }
            catch (TargetInvocationException)
            {
                continue;
            }
            yield return (property.Name, result);
        }
    }
}
