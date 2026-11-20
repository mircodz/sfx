using System.Text.RegularExpressions;
using Microsoft.ServiceFabric.Common;

namespace Sfx.Web;

/// <summary>A short sentence saying why an entity isn't healthy, for list rows.</summary>
internal static partial class Reasons
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
