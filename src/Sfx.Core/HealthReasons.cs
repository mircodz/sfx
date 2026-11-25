using System.Text.RegularExpressions;
using Microsoft.ServiceFabric.Client;
using Microsoft.ServiceFabric.Common;

namespace Sfx.Core;

/// <summary>A short sentence saying why an entity isn't healthy, for list rows and the unhealthy list.</summary>
public static partial class HealthReasons
{
    public const int MaxLength = 160;

    /// <summary>
    /// The first unhealthy evaluation's deepest cause (the event behind it, when there is one), or without evaluations
    /// the first event that isn't Ok; null when neither says anything.
    /// </summary>
    public static string? From(EntityHealth health)
    {
        if (FirstLeaf(health.UnhealthyEvaluations) is { } leaf)
        {
            return Clean(leaf is EventHealthEvaluation { UnhealthyEvent.Description: var text } && !string.IsNullOrWhiteSpace(text) ? text : leaf.Description);
        }
        return Clean((health.HealthEvents ?? []).FirstOrDefault(e => e.HealthState != HealthState.Ok)?.Description);
    }

    /// <summary>Collapses whitespace and cuts to <see cref="MaxLength"/> characters, ending in "…" when cut.</summary>
    public static string? Clean(string? text)
    {
        var clean = Whitespace().Replace(text ?? "", " ").Trim();
        if (clean.Length == 0)
        {
            return null;
        }
        return clean.Length <= MaxLength ? clean : clean[..(MaxLength - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// Asks the cluster why one entity isn't healthy: its own evaluations and unhealthy events only, without its
    /// children's states, which on a large application or service would be most of the answer.
    /// </summary>
    /// <param name="kind">"node", "application", "service" or "partition".</param>
    /// <param name="id">The node's name, or the application's, service's or partition's id.</param>
    public static async Task<string?> FetchAsync(IServiceFabricClient client, string kind, string id, CancellationToken cancellationToken)
    {
        const int none = HealthFilter.None, unhealthy = HealthFilter.WarningOrError;
        EntityHealth health = kind switch
        {
            "node" => await client.Nodes.GetNodeHealthAsync(new NodeName(id), eventsHealthStateFilter: unhealthy, cancellationToken: cancellationToken),
            "application" => await client.Applications.GetApplicationHealthAsync(
                id,
                eventsHealthStateFilter: unhealthy,
                deployedApplicationsHealthStateFilter: none,
                servicesHealthStateFilter: none,
                excludeHealthStatistics: true,
                cancellationToken: cancellationToken),
            "service" => await client.Services.GetServiceHealthAsync(
                id,
                eventsHealthStateFilter: unhealthy,
                partitionsHealthStateFilter: none,
                excludeHealthStatistics: true,
                cancellationToken: cancellationToken),
            "partition" => await client.Partitions.GetPartitionHealthAsync(
                new PartitionId(Guid.Parse(id)),
                eventsHealthStateFilter: unhealthy,
                replicasHealthStateFilter: none,
                excludeHealthStatistics: true,
                cancellationToken: cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        return From(health);
    }

    private static HealthEvaluation? FirstLeaf(IEnumerable<HealthEvaluationWrapper>? evaluations)
    {
        var first = (evaluations ?? []).Select(w => w.HealthEvaluation).OfType<HealthEvaluation>().FirstOrDefault();
        if (first is null)
        {
            return null;
        }

        // Each evaluation kind is its own type, but those that nest all call it this.
        var nested = first.GetType().GetProperty("UnhealthyEvaluations")?.GetValue(first) as IEnumerable<HealthEvaluationWrapper>;
        return FirstLeaf(nested) ?? first;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
