using System.Globalization;
using System.Xml.Linq;
using Microsoft.ServiceFabric.Common;

namespace Sfx.Core;

public sealed record UpgradeDomainRow(string Name, string State);

public sealed record UpgradingNodeRow(string Node, string Phase, IReadOnlyList<string> PendingSafetyChecks, string Duration);

/// <summary>An application or cluster upgrade, in the same shape for both.</summary>
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
    IReadOnlyList<HealthEvaluationRow> Evaluations)
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
        HealthReport.Flatten(upgrade.UnhealthyEvaluations));

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
        HealthReport.Flatten(upgrade.UnhealthyEvaluations));

    private static IReadOnlyList<UpgradeDomainRow> ToDomains(IEnumerable<UpgradeDomainInfo>? domains) =>
        (domains ?? []).Select(d => new UpgradeDomainRow(d.Name ?? "", Text(d.State))).ToList();

    private static IReadOnlyList<UpgradingNodeRow> ToNodes(IEnumerable<NodeUpgradeProgressInfo>? nodes) =>
        (nodes ?? []).Select(n => new UpgradingNodeRow(
            n.NodeName?.ToString() ?? "",
            Text(n.UpgradePhase),
            (n.PendingSafetyChecks ?? []).Select(c => Text(c.SafetyCheck?.Kind)).ToList(),
            n.UpgradeDuration ?? "")).ToList();

    private static TimeSpan? Milliseconds(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ? TimeSpan.FromMilliseconds(ms) : null;

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
}

/// <summary>Appends one line per action taken to a file, so there's a record of what was changed, where and when.</summary>
public sealed class ActionLog(string path)
{
    public string Path => path;

    public void Append(string context, string endpoint, string action, string outcome)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var line = string.Join('\t',
            DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            Environment.UserName,
            context,
            endpoint,
            action,
            outcome.ReplaceLineEndings(" "));
        File.AppendAllText(path, line + Environment.NewLine);
    }
}
