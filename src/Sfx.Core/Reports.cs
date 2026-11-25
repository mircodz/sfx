using System.Globalization;
using System.Xml.Linq;
using Microsoft.ServiceFabric.Common;

namespace Sfx.Core;

public sealed record UpgradeDomainRow(string Name, string State);

public sealed record UpgradingNodeRow(string Node, string Phase, IReadOnlyList<string> PendingSafetyChecks, string Duration);

/// <summary>An application or cluster upgrade, in the same shape for both.</summary>
/// <param name="OnFailure">A monitored upgrade's failure action (Rollback or Manual); empty when it doesn't say.</param>
public sealed record UpgradeReport(
    string State,
    string Mode,
    string Target,
    string NextDomain,
    string Started,
    TimeSpan? Duration,
    string FailureReason,
    string HealthCheckPhase,
    string Details,
    IReadOnlyList<UpgradeDomainRow> Domains,
    string CurrentDomain,
    IReadOnlyList<UpgradingNodeRow> CurrentNodes,
    IReadOnlyList<HealthEvaluationRow> Evaluations,
    string OnFailure = "")
{
    public bool InProgress => State is nameof(UpgradeState.RollingForwardInProgress)
        or nameof(UpgradeState.RollingBackInProgress)
        or nameof(UpgradeState.RollingForwardPending);

    public static UpgradeReport From(ApplicationUpgradeProgressInfo upgrade) => new(
        Text(upgrade.UpgradeState),
        Text(upgrade.RollingUpgradeMode),
        upgrade.TargetApplicationTypeVersion ?? "",
        upgrade.NextUpgradeDomain ?? "",
        upgrade.StartTimestampUtc ?? "",
        Milliseconds(upgrade.UpgradeDurationInMilliseconds),
        Text(upgrade.FailureReason),
        Text(upgrade.HealthCheckPhase),
        upgrade.UpgradeStatusDetails ?? "",
        ToDomains(upgrade.UpgradeDomains),
        upgrade.CurrentUpgradeDomainProgress?.DomainName ?? "",
        ToNodes(upgrade.CurrentUpgradeDomainProgress?.NodeUpgradeProgressList),
        HealthReport.Flatten(upgrade.UnhealthyEvaluations),
        Text(upgrade.UpgradeDescription?.MonitoringPolicy?.FailureAction));

    public static UpgradeReport From(ClusterUpgradeProgressObject upgrade) => new(
        Text(upgrade.UpgradeState),
        Text(upgrade.RollingUpgradeMode),
        $"code {upgrade.CodeVersion}, config {upgrade.ConfigVersion}",
        upgrade.NextUpgradeDomain ?? "",
        upgrade.StartTimestampUtc ?? "",
        Milliseconds(upgrade.UpgradeDurationInMilliseconds),
        Text(upgrade.FailureReason),
        Text(upgrade.HealthCheckPhase),
        "",
        ToDomains(upgrade.UpgradeDomains),
        upgrade.CurrentUpgradeDomainProgress?.DomainName ?? "",
        ToNodes(upgrade.CurrentUpgradeDomainProgress?.NodeUpgradeProgressList),
        HealthReport.Flatten(upgrade.UnhealthyEvaluations),
        Text(upgrade.UpgradeDescription?.MonitoringPolicy?.FailureAction));

    private static IReadOnlyList<UpgradeDomainRow> ToDomains(IEnumerable<UpgradeDomainInfo>? domains) =>
        (domains ?? []).Select(d => new UpgradeDomainRow(d.Name ?? "", Text(d.State))).ToList();

    private static IReadOnlyList<UpgradingNodeRow> ToNodes(IEnumerable<NodeUpgradeProgressInfo>? nodes) =>
        (nodes ?? []).Select(n => new UpgradingNodeRow(
            n.NodeName?.ToString() ?? "",
            Text(n.UpgradePhase),
            (n.PendingSafetyChecks ?? []).Select(c => Text(c.SafetyCheck?.Kind)).ToList(),
            n.UpgradeDuration ?? "")).ToList();

    /// <summary>Durations arrive as milliseconds or, despite the field names, as ISO 8601 ("PT0H5M26.812S").</summary>
    internal static TimeSpan? Milliseconds(string? value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        try
        {
            return value is { Length: > 0 } ? System.Xml.XmlConvert.ToTimeSpan(value) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Text<TEnum>(TEnum? value) where TEnum : struct, Enum => value?.ToString() ?? "";
}

public sealed record Setting(string Name, string Value, bool IsEncrypted);

public sealed record SettingsSection(string Name, IReadOnlyList<Setting> Parameters);

public static class ClusterSettings
{
    /// <summary>The <c>FabricSettings</c> of a cluster manifest: every section and its parameters, in manifest order.</summary>
    public static IReadOnlyList<SettingsSection> Parse(string manifest)
    {
        var document = XDocument.Parse(manifest);
        var settings = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "FabricSettings");
        return settings is null
            ? []
            : settings.Elements()
                .Where(e => e.Name.LocalName == "Section")
                .Select(section => new SettingsSection(
                    (string?)section.Attribute("Name") ?? "",
                    section.Elements()
                        .Where(e => e.Name.LocalName == "Parameter")
                        .Select(p => new Setting(
                            (string?)p.Attribute("Name") ?? "",
                            (string?)p.Attribute("Value") ?? "",
                            string.Equals((string?)p.Attribute("IsEncrypted"), "true", StringComparison.OrdinalIgnoreCase)))
                        .ToList()))
                .ToList();
    }

    /// <summary>The config version a cluster manifest is, from its root <c>Version</c>; null when it doesn't say.</summary>
    public static string? ConfigVersion(string manifest) =>
        XDocument.Parse(manifest).Root?.Attribute("Version")?.Value is { Length: > 0 } version ? version : null;
}

/// <summary>One line of the action log.</summary>
public sealed record ActionLogEntry(DateTimeOffset Time, string User, string Context, string Endpoint, string Action, string Outcome)
{
    /// <summary>"ok" when an action ran; "done" when the browser saw the cluster finish what it asked for.</summary>
    public bool Succeeded => Outcome is "ok" or "done";
}

/// <summary>Appends one line per action taken to a file, so there's a record of what was changed, where and when.</summary>
public sealed class ActionLog(string path)
{
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:sszzz";

    public string Path => path;

    public ActionLogEntry Append(string context, string endpoint, string action, string outcome)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var now = DateTimeOffset.Now;
        // The file keeps whole seconds; the entry matches what a later read returns.
        var time = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, now.Offset);
        var entry = new ActionLogEntry(time, Environment.UserName, context, endpoint, action, outcome.ReplaceLineEndings(" "));
        var line = string.Join(
            '\t',
            entry.Time.ToString(TimeFormat, CultureInfo.InvariantCulture),
            entry.User,
            entry.Context,
            entry.Endpoint,
            entry.Action,
            entry.Outcome);
        File.AppendAllText(path, line + Environment.NewLine);
        return entry;
    }

    /// <summary>The last <paramref name="count"/> entries, oldest first; lines that don't parse are skipped.</summary>
    public IReadOnlyList<ActionLogEntry> ReadLast(int count = 1000)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var last = new Queue<ActionLogEntry>(count);
        foreach (var line in File.ReadLines(path))
        {
            if (Parse(line) is not { } entry)
            {
                continue;
            }
            if (last.Count == count)
            {
                last.Dequeue();
            }
            last.Enqueue(entry);
        }
        return last.ToList();
    }

    private static ActionLogEntry? Parse(string line)
    {
        var fields = line.Split('\t');
        return fields.Length == 6
            && DateTimeOffset.TryParseExact(fields[0], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? new ActionLogEntry(time, fields[1], fields[2], fields[3], fields[4], fields[5])
            : null;
    }
}
