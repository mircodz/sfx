using System.Globalization;
using Cellar.Primitives;
using Cellar.Text;
using Cellar.Theming;
using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Tui;

/// <summary>Formats entity details as lines for a <see cref="Cellar.Widgets.TextViewer"/>.</summary>
public static class Lines
{
    private const string Indent = "  ";

    /// <summary>Plain text as is; JSON and XML (manifests, configuration) indented and colored.</summary>
    public static IReadOnlyList<StyledText> Text(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [Styles.Muted("(empty)")];
        }

        var (pretty, format) = Inspect.TryPretty(text);
        return Highlighted(pretty, format);
    }

    public static IReadOnlyList<StyledText> Json(object? value) => Highlighted(Inspect.Json(value), Inspect.Format.Json);

    /// <summary>
    /// Pretty-printed JSON or XML, colored a line at a time by Cellar's highlighters. An XML comment can span lines
    /// (manifests have them), so the lines inside one are dimmed here as a whole.
    /// </summary>
    public static IReadOnlyList<StyledText> Highlighted(string text, Inspect.Format format, string indent = "")
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var inComment = false;
        return lines.Select(line => format switch
        {
            Inspect.Format.Json => TextHighlighters.Json(indent + line),
            Inspect.Format.Xml => Xml(indent + line, ref inComment),
            _ => new StyledText(indent + line, Theme.Current.TextStyle),
        }).ToList();
    }

    private static StyledText Xml(string line, ref bool inComment)
    {
        var start = 0;
        var text = StyledText.Empty();
        if (inComment)
        {
            var close = line.IndexOf("-->", StringComparison.Ordinal);
            start = close < 0 ? line.Length : close + 3;
            text.Append(line[..start], new Style(Theme.Current.Muted, Color.Default).Italic);
            inComment = close < 0;
        }

        var rest = line[start..];
        var open = rest.LastIndexOf("<!--", StringComparison.Ordinal);
        inComment |= open >= 0 && rest.IndexOf("-->", open + 4, StringComparison.Ordinal) < 0;
        return rest.Length == 0 ? text : text.Append(TextHighlighters.Xml(rest));
    }

    public static IReadOnlyList<StyledText> Error(Exception error) =>
        [StyledText.Of(error.GetBaseException().Message).Fg(Theme.Current.Error)];

    public static IReadOnlyList<StyledText> Fields(IEnumerable<Field> fields)
    {
        var list = fields.ToList();
        if (list.Count == 0)
        {
            return [Styles.Muted("(nothing to show)")];
        }

        var width = list.Max(field => field.Name.Length) + 2;
        return list
            .SelectMany(field => Value(StyledText.Of(field.Name.PadRight(width)).Fg(Theme.Current.Muted), field.Value, Theme.Current.Foreground))
            .ToList();
    }

    /// <summary>
    /// A name and its value on one line; a value that's JSON or XML with more than one member goes on the lines
    /// below instead, indented and colored, so nested objects and parameter documents can be read.
    /// </summary>
    private static IEnumerable<StyledText> Value(StyledText name, string value, Color color, string indent = Indent)
    {
        var (pretty, format) = Inspect.TryPretty(value);
        if (format == Inspect.Format.None || pretty.Count(c => c == '\n') < 3)
        {
            yield return name.Append(value.Length == 0 ? "-" : value).Fg(color);
            yield break;
        }

        yield return name;
        foreach (var line in Highlighted(pretty, format, indent))
        {
            yield return line;
        }
    }

    /// <summary>Simple aligned columns, for short lists inside a tab.</summary>
    public static IReadOnlyList<StyledText> Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0)
        {
            return [Styles.Muted("(none)")];
        }

        var widths = headers.Select((header, i) => Math.Max(header.Length, rows.Max(row => row[i].Length)) + 2).ToArray();
        string Row(IReadOnlyList<string> cells) => string.Concat(cells.Select((cell, i) => cell.PadRight(widths[i]))).TrimEnd();

        var lines = new List<StyledText> { StyledText.Of(Row(headers)).Bold().Fg(Theme.Current.Accent) };
        lines.AddRange(rows.Select(row => new StyledText(Row(row), Theme.Current.TextStyle)));
        return lines;
    }

    /// <summary>The aggregated state, why it isn't Ok (the evaluation chain, top-down), then every health event.</summary>
    public static IReadOnlyList<StyledText> Health(HealthReport report)
    {
        var lines = new List<StyledText>
        {
            StyledText.Of("State  ").Fg(Theme.Current.Muted).Append(Styles.Health(report.State)),
        };

        if (report.Evaluations.Count > 0)
        {
            lines.Add(StyledText.Empty());
            lines.Add(Heading("Why it's unhealthy"));
            foreach (var evaluation in report.Evaluations)
            {
                var indent = Indent + new string(' ', evaluation.Depth * 2);
                lines.Add(StyledText.Of(indent).Append(Styles.Health(evaluation.State))
                    .Append($"  {evaluation.Kind}").Fg(Theme.Current.Accent));
                lines.AddRange(Paragraph(evaluation.Description, indent + Indent));
            }
        }

        lines.Add(StyledText.Empty());
        lines.Add(Heading($"Events ({report.Events.Count})"));
        foreach (var e in report.Events.OrderByDescending(e => e.State).ThenBy(e => e.Source).ThenBy(e => e.Property))
        {
            lines.Add(StyledText.Of(Indent).Append(Styles.Health(e.State))
                .Append($"  {e.Source}").Fg(Theme.Current.Foreground)
                .Append($"  {e.Property}").Fg(Theme.Current.Accent));
            lines.AddRange(Paragraph(e.Description, Indent + Indent));

            var when = e.LastModifiedUtc is { } time
                ? "updated " + time.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : "";
            if (when.Length > 0 || e.IsExpired)
            {
                lines.Add(Styles.Muted(Indent + Indent + when + (e.IsExpired ? "  (expired)" : "")));
            }
        }
        return lines;
    }

    private static StyledText Heading(string text) => StyledText.Of(text).Bold().Fg(Theme.Current.Accent);

    /// <summary>A labelled value, aligned with the others of its block.</summary>
    private static StyledText Labelled(string label, StyledText value, int width = 12) =>
        StyledText.Of(label.PadRight(width)).Fg(Theme.Current.Muted).Append(value);

    public static IReadOnlyList<StyledText> Upgrade(UpgradeReport upgrade)
    {
        var state = upgrade.State switch
        {
            "RollingForwardCompleted" => Styles.Colored(upgrade.State, Theme.Current.Success),
            "RollingForwardInProgress" or "RollingForwardPending" => Styles.Colored(upgrade.State, Theme.Current.Warning),
            "" => Styles.Muted("-"),
            _ => Styles.Colored(upgrade.State, Theme.Current.Error),
        };

        var lines = new List<StyledText>
        {
            Labelled("State", state),
            Labelled("Target", Styles.Plain(upgrade.Target)),
            Labelled("Mode", Styles.Plain(upgrade.Mode)),
            Labelled("Started", Styles.Plain(upgrade.Started)),
            Labelled("Duration", Styles.Plain(Styles.Duration(upgrade.Duration))),
        };
        if (upgrade.InProgress)
        {
            lines.Add(Labelled("Next UD", Styles.Plain(upgrade.NextDomain)));
            lines.Add(Labelled("Health check", Styles.Plain(upgrade.HealthCheckPhase)));
        }
        if (upgrade.FailureReason is { Length: > 0 } and not "None")
        {
            lines.Add(Labelled("Failure", Styles.Colored(upgrade.FailureReason, Theme.Current.Error)));
        }
        if (upgrade.Details.Length > 0)
        {
            lines.Add(Labelled("Details", Styles.Plain(upgrade.Details)));
        }

        if (upgrade.Domains.Count > 0)
        {
            lines.Add(StyledText.Empty());
            lines.Add(Heading("Upgrade domains"));
            var width = upgrade.Domains.Max(d => d.Name.Length) + 2;
            foreach (var domain in upgrade.Domains)
            {
                var text = domain.State switch
                {
                    "Completed" => Styles.Colored("done", Theme.Current.Success),
                    "InProgress" => Styles.Colored("in progress", Theme.Current.Warning),
                    "Pending" => Styles.Muted("pending"),
                    _ => Styles.Plain(domain.State),
                };
                lines.Add(StyledText.Of(Indent + domain.Name.PadRight(width)).Append(text));
            }
        }

        if (upgrade.CurrentNodes.Count > 0)
        {
            lines.Add(StyledText.Empty());
            lines.Add(Heading($"Upgrading in {upgrade.CurrentDomain}"));
            var width = upgrade.CurrentNodes.Max(n => n.Node.Length) + 2;
            foreach (var node in upgrade.CurrentNodes)
            {
                var line = StyledText.Of(Indent + node.Node.PadRight(width)).Append(node.Phase).Fg(Theme.Current.Warning);
                if (node.PendingSafetyChecks.Count > 0)
                {
                    line.Append("  waiting for " + string.Join(", ", node.PendingSafetyChecks)).Fg(Theme.Current.Muted);
                }
                lines.Add(line);
            }
        }

        if (upgrade.Evaluations.Count > 0)
        {
            lines.Add(StyledText.Empty());
            lines.Add(Heading("Why the health check fails"));
            foreach (var evaluation in upgrade.Evaluations)
            {
                var indent = Indent + new string(' ', evaluation.Depth * 2);
                lines.Add(StyledText.Of(indent).Append(Styles.Health(evaluation.State)).Append($"  {evaluation.Kind}").Fg(Theme.Current.Accent));
                lines.AddRange(Paragraph(evaluation.Description, indent + Indent));
            }
        }
        return lines;
    }

    public static IReadOnlyList<StyledText> NodeLoad(NodeLoadInfo load) =>
        Table(
            ["METRIC", "CAPACITY", "LOAD", "REMAINING", "BUFFERED LEFT", "OVER CAPACITY"],
            (load.NodeLoadMetricInformation ?? [])
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(m => (IReadOnlyList<string>)[
                    m.Name ?? "", m.NodeCapacity ?? "", m.NodeLoad ?? "", m.NodeRemainingCapacity ?? "",
                    m.NodeRemainingBufferedCapacity ?? "", m.IsCapacityViolation == true ? "yes" : "no"])
                .ToList());

    public static IReadOnlyList<StyledText> PartitionLoad(PartitionLoadInformation load)
    {
        IEnumerable<IReadOnlyList<string>> Rows(string role, IEnumerable<LoadMetricReport>? reports) =>
            (reports ?? []).Select(r => (IReadOnlyList<string>)[
                role, r.Name ?? "", r.CurrentValue ?? r.Value ?? "",
                r.LastReportedUtc?.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? ""]);

        return Table(
            ["ROLE", "METRIC", "VALUE", "REPORTED"],
            Rows("primary", load.PrimaryLoadMetricReports)
                .Concat(Rows("secondary", load.SecondaryLoadMetricReports))
                .Concat(Rows("auxiliary", load.AuxiliaryLoadMetricReports))
                .ToList());
    }

    public static IReadOnlyList<StyledText> ClusterLoad(ClusterLoadInfo load)
    {
        static string Time(DateTime? time) => time?.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "-";
        static string Number(double? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";

        var lines = new List<StyledText>
        {
            Labelled("Last balancing", Styles.Plain($"{Time(load.LastBalancingStartTimeUtc)} → {Time(load.LastBalancingEndTimeUtc)}"), 16),
            StyledText.Empty(),
        };
        lines.AddRange(Table(
            ["METRIC", "CAPACITY", "LOAD", "REMAINING", "BALANCED", "DEVIATION", "MIN NODE", "MAX NODE"],
            (load.LoadMetricInformation ?? [])
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(m => (IReadOnlyList<string>)[
                    m.Name ?? "", m.ClusterCapacity ?? "", m.ClusterLoad ?? "", m.ClusterRemainingCapacity ?? "",
                    m.IsBalancedAfter == true ? "yes" : "no",
                    $"{Number(m.DeviationBefore)} → {Number(m.DeviationAfter)}",
                    m.MinNodeLoadValue ?? "", m.MaxNodeLoadValue ?? ""])
                .ToList()));
        return lines;
    }

    public static IReadOnlyList<StyledText> ServiceDescription(Microsoft.ServiceFabric.Common.ServiceDescription description)
    {
        var lines = new List<StyledText>(Fields(Inspect.Fields(
            description,
            "PartitionDescription",
            "ServiceLoadMetrics",
            "ServicePlacementPolicies",
            "CorrelationScheme",
            "ScalingPolicies",
            "InitializationData")));

        lines.Add(StyledText.Empty());
        lines.Add(Heading("Partitioning"));
        lines.AddRange(Indented(Fields(description.PartitionDescription is { } partitioning ? Inspect.Fields(partitioning) : [])));

        lines.Add(StyledText.Empty());
        lines.Add(Heading("Load metrics"));
        lines.AddRange(Indented(Table(
            ["NAME", "WEIGHT", "PRIMARY", "SECONDARY", "AUXILIARY", "DEFAULT", "MAX"],
            (description.ServiceLoadMetrics ?? [])
                .Select(m => (IReadOnlyList<string>)[
                    m.Name ?? "", m.Weight?.ToString() ?? "", m.PrimaryDefaultLoad?.ToString() ?? "", m.SecondaryDefaultLoad?.ToString() ?? "",
                    m.AuxiliaryDefaultLoad?.ToString() ?? "", m.DefaultLoad?.ToString() ?? "", m.MaximumLoad?.ToString() ?? ""])
                .ToList())));

        AddList("Placement policies", description.ServicePlacementPolicies);
        AddList("Correlations", description.CorrelationScheme);
        AddList("Scaling policies", description.ScalingPolicies);
        return lines;

        void AddList(string title, IEnumerable<object>? items)
        {
            lines.Add(StyledText.Empty());
            lines.Add(Heading(title));
            var list = (items ?? []).ToList();
            lines.AddRange(list.Count == 0
                ? [Styles.Muted(Indent + "(none)")]
                : list.Select(item => new StyledText(Indent + Inspect.ToJson(item)?.ToJsonString(), Theme.Current.TextStyle)));
        }
    }

    /// <summary>Every section of the cluster's settings and its parameters; <c>/</c> finds one quickly.</summary>
    public static IReadOnlyList<StyledText> Settings(IReadOnlyList<SettingsSection> sections)
    {
        if (sections.Count == 0)
        {
            return [Styles.Muted("(no FabricSettings in the cluster manifest)")];
        }

        var lines = new List<StyledText>();
        foreach (var section in sections)
        {
            if (lines.Count > 0)
            {
                lines.Add(StyledText.Empty());
            }
            lines.Add(Heading(section.Name));
            var width = section.Parameters.Count == 0 ? 0 : section.Parameters.Max(p => p.Name.Length) + 2;
            foreach (var parameter in section.Parameters)
            {
                var name = StyledText.Of(Indent + parameter.Name.PadRight(width)).Fg(Theme.Current.Foreground);
                if (parameter.IsEncrypted || parameter.Value.Length == 0)
                {
                    lines.Add(name.Append(parameter.IsEncrypted ? Styles.Muted("(encrypted)") : Styles.Plain(parameter.Value)));
                }
                else
                {
                    lines.AddRange(Value(name, parameter.Value, Theme.Current.Foreground, Indent + Indent));
                }
            }
        }
        return lines;
    }

    public static IReadOnlyList<StyledText> EntryPoint(CodePackageEntryPoint? entryPoint)
    {
        if (entryPoint is null)
        {
            return [Styles.Muted("(none)")];
        }

        var lines = new List<StyledText>(Fields(Inspect.Fields(entryPoint, "CodePackageEntryPointStatistics")));
        if (entryPoint.CodePackageEntryPointStatistics is { } statistics)
        {
            lines.Add(StyledText.Empty());
            lines.Add(Heading("Statistics"));
            lines.AddRange(Indented(Fields(Inspect.Fields(statistics))));
        }
        return lines;
    }

    private static IEnumerable<StyledText> Indented(IEnumerable<StyledText> lines) =>
        lines.Select(line => StyledText.Of(Indent).Append(line));

    private static IEnumerable<StyledText> Paragraph(string text, string indent) =>
        text.Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => new StyledText(indent + line.TrimEnd(), Theme.Current.TextStyle));
}
