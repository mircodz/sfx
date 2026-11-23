using System.Globalization;
using Microsoft.ServiceFabric.Client;
using Microsoft.ServiceFabric.Common;

namespace Sfx.Core;

/// <summary>How a service setting is typed in.</summary>
public enum SettingKind
{
    /// <summary>A whole number, within <see cref="ServiceSetting.Min"/> and <see cref="ServiceSetting.Max"/>.</summary>
    Count,

    /// <summary>A duration in seconds; "30", "30s", "5m", "1h" and "2d" are all taken.</summary>
    Seconds,

    /// <summary>Free text; empty clears it.</summary>
    Text,

    /// <summary>One of <see cref="ServiceSetting.Choices"/>.</summary>
    Choice,

    /// <summary>"true" or "false".</summary>
    Flag,
}

/// <summary>One editable service setting and its current value.</summary>
/// <param name="Key">What <see cref="ServiceSettings.UpdateAsync"/> takes it by, e.g. "quorum-loss-wait".</param>
/// <param name="Value">The current value as text; empty when the cluster's default applies.</param>
public sealed record ServiceSetting(
    string Key, string Title, SettingKind Kind, string Value, IReadOnlyList<string>? Choices = null, int? Min = null, int? Max = null);

/// <summary>A service's editable settings, for stateful or stateless services (they differ).</summary>
public sealed record ServiceSettingSet(bool Stateful, IReadOnlyList<ServiceSetting> Settings);

/// <summary>
/// Which fields of a service update are set: the REST API's ServiceUpdateDescription.Flags. A field sent without its
/// flag is ignored by the cluster.
/// </summary>
[Flags]
internal enum ServiceUpdateFlags
{
    Count = 1,
    ReplicaRestartWaitDuration = 2,
    QuorumLossWaitDuration = 4,
    StandByReplicaKeepDuration = 8,
    MinReplicaSetSize = 16,
    PlacementConstraints = 32,
    DefaultMoveCost = 512,
    ServicePlacementTimeLimit = 2048,
    MinInstanceCount = 4096,
    MinInstancePercentage = 8192,
    InstanceCloseDelayDuration = 16384,
    InstanceRestartWaitDuration = 32768,
    DropSourceReplicaOnMove = 65536,
    ServiceDnsName = 131072,
}

/// <summary>Reads a service's editable settings and changes several of them in one update.</summary>
public sealed class ServiceSettings(IServiceFabricClient client)
{
    private static readonly string[] MoveCosts = Enum.GetNames<MoveCost>();
    private static readonly string[] Booleans = ["true", "false"];

    private static readonly Dictionary<string, ServiceUpdateFlags> Shared = new()
    {
        ["count"] = ServiceUpdateFlags.Count,
        ["placement-constraints"] = ServiceUpdateFlags.PlacementConstraints,
        ["move-cost"] = ServiceUpdateFlags.DefaultMoveCost,
        ["dns-name"] = ServiceUpdateFlags.ServiceDnsName,
    };

    private static readonly Dictionary<string, ServiceUpdateFlags> StatefulFlags = new(Shared)
    {
        ["min-replicas"] = ServiceUpdateFlags.MinReplicaSetSize,
        ["replica-restart-wait"] = ServiceUpdateFlags.ReplicaRestartWaitDuration,
        ["quorum-loss-wait"] = ServiceUpdateFlags.QuorumLossWaitDuration,
        ["standby-keep"] = ServiceUpdateFlags.StandByReplicaKeepDuration,
        ["placement-time-limit"] = ServiceUpdateFlags.ServicePlacementTimeLimit,
        ["drop-source-on-move"] = ServiceUpdateFlags.DropSourceReplicaOnMove,
    };

    private static readonly Dictionary<string, ServiceUpdateFlags> StatelessFlags = new(Shared)
    {
        ["min-instances"] = ServiceUpdateFlags.MinInstanceCount,
        ["min-instance-percent"] = ServiceUpdateFlags.MinInstancePercentage,
        ["instance-close-delay"] = ServiceUpdateFlags.InstanceCloseDelayDuration,
        ["instance-restart-wait"] = ServiceUpdateFlags.InstanceRestartWaitDuration,
    };

    public async Task<ServiceSettingSet> GetAsync(string serviceId, CancellationToken cancellationToken)
    {
        var description = await client.Services.GetServiceDescriptionAsync(serviceId, cancellationToken: cancellationToken);
        return new ServiceSettingSet(description is StatefulServiceDescription, Read(description));
    }

    /// <summary>The settings sfx lets you change, with their values in <paramref name="description"/>.</summary>
    public static IReadOnlyList<ServiceSetting> Read(ServiceDescription description)
    {
        List<ServiceSetting> settings = description switch
        {
            StatefulServiceDescription s =>
            [
                new("count", "Target replicas", SettingKind.Count, Text(s.TargetReplicaSetSize), Min: 1),
                new("min-replicas", "Min replicas", SettingKind.Count, Text(s.MinReplicaSetSize), Min: 1),
                new("replica-restart-wait", "Replica restart wait", SettingKind.Seconds, Text(s.ReplicaRestartWaitDurationSeconds)),
                new("quorum-loss-wait", "Quorum loss wait", SettingKind.Seconds, Text(s.QuorumLossWaitDurationSeconds)),
                new("standby-keep", "Standby replica keep", SettingKind.Seconds, Text(s.StandByReplicaKeepDurationSeconds)),
                new("placement-time-limit", "Placement time limit", SettingKind.Seconds, Text(s.ServicePlacementTimeLimitSeconds)),
                new("drop-source-on-move", "Drop source replica on move", SettingKind.Flag, Text(s.DropSourceReplicaOnMove), Booleans),
            ],
            StatelessServiceDescription s =>
            [
                new("count", "Instances", SettingKind.Count, Text(s.InstanceCount), Min: -1),
                new("min-instances", "Min instances", SettingKind.Count, Text(s.MinInstanceCount), Min: 0),
                new("min-instance-percent", "Min instance percentage", SettingKind.Count, Text(s.MinInstancePercentage), Min: 0, Max: 100),
                new("instance-close-delay", "Instance close delay", SettingKind.Seconds, Text(s.InstanceCloseDelayDurationSeconds)),
                new("instance-restart-wait", "Instance restart wait", SettingKind.Seconds, Text(s.InstanceRestartWaitDurationSeconds)),
            ],
            _ => [],
        };

        settings.Add(new("placement-constraints", "Placement constraints", SettingKind.Text, description.PlacementConstraints ?? ""));
        settings.Add(new("move-cost", "Default move cost", SettingKind.Choice, Text(description.DefaultMoveCost), MoveCosts));
        settings.Add(new("dns-name", "DNS name", SettingKind.Text, description.ServiceDnsName ?? ""));
        return settings;
    }

    /// <summary>
    /// Checks and normalizes a value typed for <paramref name="setting"/>: durations become seconds ("5m" is "300"),
    /// choices take their listed casing. A <see cref="RefusedException"/> says what's expected otherwise.
    /// </summary>
    public static string Parse(ServiceSetting setting, string text)
    {
        text = text.Trim();
        switch (setting.Kind)
        {
            case SettingKind.Count:
                var range = (setting.Min, setting.Max) switch
                {
                    ({ } min, { } max) => $"from {min} to {max}",
                    (-1, null) => "of 1 or more, or -1 for every node",
                    ({ } min, null) => $"of {min} or more",
                    _ => "",
                };
                return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var count)
                    && count >= (setting.Min ?? int.MinValue) && count <= (setting.Max ?? int.MaxValue)
                    // -1 means every node; 0 instances isn't a count the cluster takes.
                    && !(setting.Min == -1 && count == 0)
                    ? count.ToString(CultureInfo.InvariantCulture)
                    : throw new RefusedException($"{setting.Title} must be a whole number {range}".TrimEnd() + ".");
            case SettingKind.Seconds:
                return Seconds(text) is { } seconds
                    ? seconds.ToString(CultureInfo.InvariantCulture)
                    : throw new RefusedException($"{setting.Title} must be a number of seconds, or a number with s, m, h or d (like 30s, 5m, 1h).");
            case SettingKind.Choice or SettingKind.Flag:
                var choices = setting.Choices ?? Booleans;
                return choices.FirstOrDefault(c => c.Equals(text, StringComparison.OrdinalIgnoreCase))
                    ?? throw new RefusedException($"{setting.Title} must be one of: {string.Join(", ", choices)}.");
            default:
                return text;
        }
    }

    /// <summary>"30", "30s", "5m", "1h" or "2d" in seconds; null for anything else.</summary>
    public static long? Seconds(string text)
    {
        text = text.Trim();
        var unit = text.Length > 0 && char.IsLetter(text[^1]) ? char.ToLowerInvariant(text[^1]) : 's';
        var digits = text.Length > 0 && char.IsLetter(text[^1]) ? text[..^1] : text;
        long multiplier = unit switch { 's' => 1, 'm' => 60, 'h' => 3600, 'd' => 86400, _ => 0 };
        return multiplier > 0
            && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            && amount <= long.MaxValue / multiplier
            ? amount * multiplier
            : null;
    }

    /// <summary>
    /// Changes some of a service's settings in one update, by <see cref="ServiceSetting.Key"/>. Values are taken as
    /// typed and checked with <see cref="Parse"/>; ones equal to today's are left out. Returns what was sent, normalized;
    /// empty (and nothing sent) when nothing changes.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> UpdateAsync(
        string serviceId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var current = await GetAsync(serviceId, cancellationToken);
        var changes = Changes(current, values);
        if (changes.Count > 0)
        {
            await client.Services.UpdateServiceAsync(serviceId, BuildUpdate(current.Stateful, changes), cancellationToken: cancellationToken);
        }
        return changes;
    }

    /// <summary>
    /// What <see cref="UpdateAsync"/> would send: the values that differ from today's, checked and normalized, and
    /// checked together (a minimum can't end up above its target).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Changes(ServiceSettingSet current, IReadOnlyDictionary<string, string> values)
    {
        var byKey = current.Settings.ToDictionary(s => s.Key);
        var changes = new Dictionary<string, string>();
        foreach (var (key, text) in values)
        {
            var setting = byKey.GetValueOrDefault(key)
                ?? throw new RefusedException($"A {(current.Stateful ? "stateful" : "stateless")} service has no setting '{key}'.");
            if (text.Trim() == setting.Value)
            {
                continue;
            }
            var value = Parse(setting, text);
            if (value != setting.Value)
            {
                changes[key] = value;
            }
        }

        int? After(string key) =>
            int.TryParse(changes.GetValueOrDefault(key) ?? byKey.GetValueOrDefault(key)?.Value, CultureInfo.InvariantCulture, out var n) ? n : null;
        if (current.Stateful && After("min-replicas") is { } minReplicas && After("count") is { } target && minReplicas > target)
        {
            throw new RefusedException($"Min replicas ({minReplicas}) can't be more than target replicas ({target}).");
        }
        if (!current.Stateful && After("min-instances") is { } minInstances && After("count") is { } instances and not -1 && minInstances > instances)
        {
            throw new RefusedException($"Min instances ({minInstances}) can't be more than instances ({instances}).");
        }
        return changes;
    }

    /// <summary>One update setting exactly the given fields, each with its flag; values as <see cref="Parse"/> returns them.</summary>
    public static ServiceUpdateDescription BuildUpdate(bool stateful, IReadOnlyDictionary<string, string> values)
    {
        var known = stateful ? StatefulFlags : StatelessFlags;
        var flags = (ServiceUpdateFlags)0;
        foreach (var key in values.Keys)
        {
            flags |= known.TryGetValue(key, out var flag)
                ? flag
                : throw new ArgumentException($"A {(stateful ? "stateful" : "stateless")} service has no setting '{key}'.", nameof(values));
        }

        string? Get(string key) => values.TryGetValue(key, out var value) ? value : null;
        int? Int(string key) => Get(key) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : null;
        var moveCost = Get("move-cost") is { } cost ? Enum.Parse<MoveCost>(cost, ignoreCase: true) : (MoveCost?)null;
        var flagText = ((int)flags).ToString(CultureInfo.InvariantCulture);

        if (stateful)
        {
            return new StatefulServiceUpdateDescription(
                flags: flagText,
                placementConstraints: Get("placement-constraints"),
                defaultMoveCost: moveCost,
                serviceDnsName: Get("dns-name"),
                targetReplicaSetSize: Int("count"),
                minReplicaSetSize: Int("min-replicas"),
                replicaRestartWaitDurationSeconds: Get("replica-restart-wait"),
                quorumLossWaitDurationSeconds: Get("quorum-loss-wait"),
                standByReplicaKeepDurationSeconds: Get("standby-keep"),
                servicePlacementTimeLimitSeconds: Get("placement-time-limit"),
                dropSourceReplicaOnMove: Get("drop-source-on-move") is { } drop ? bool.Parse(drop) : null);
        }

        return new StatelessServiceUpdateDescription(
            flags: flagText,
            placementConstraints: Get("placement-constraints"),
            defaultMoveCost: moveCost,
            serviceDnsName: Get("dns-name"),
            instanceCount: Int("count"),
            minInstanceCount: Int("min-instances"),
            minInstancePercentage: Int("min-instance-percent"),
            instanceCloseDelayDurationSeconds: Get("instance-close-delay"),
            instanceRestartWaitDurationSeconds: Get("instance-restart-wait"));
    }

    /// <summary>"Change Quorum loss wait to 300s and Default move cost to High on fabric:/a/b", for confirmations and the action log.</summary>
    public static string Describe(string serviceName, IReadOnlyList<ServiceSetting> settings, IReadOnlyDictionary<string, string> changes)
    {
        var parts = changes
            .Select(kv => (kv.Key, Setting: settings.FirstOrDefault(s => s.Key == kv.Key), kv.Value))
            .Select(c => $"{c.Setting?.Title ?? c.Key} to "
                + (c.Value.Length == 0 ? "nothing" : c.Setting?.Kind == SettingKind.Seconds ? c.Value + "s" : c.Value))
            .ToList();
        var list = parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts[..^1]) + " and " + parts[^1];
        return $"Change {list} on {serviceName}";
    }

    private static string Text<T>(T? value) where T : struct => (object?)value switch
    {
        null => "",
        bool flag => flag ? "true" : "false",
        var other => Convert.ToString(other, CultureInfo.InvariantCulture) ?? "",
    };
}
