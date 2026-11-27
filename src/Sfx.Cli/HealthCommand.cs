using Sfx.Core;
using Sfx.Web;

namespace Sfx.Cli;

/// <summary><c>sfx health &lt;entity&gt;</c>: its state, why it isn't healthy (the evaluation chain), and its health events.</summary>
internal static class HealthCommand
{
    public const string Help = """
        usage: sfx health <entity> [-o text|json]

        The entity's health: why it isn't healthy, as the cluster evaluated it top-down, then its health events.
        <entity>: cluster, node/<name>, an application or service, partition/<id> or replica/<partition id>/<id>.

          -o text|json    how to print it (default: text); json is the browser's health object
        """;

    private const string Indent = "  ";

    public static Func<UiSession, CancellationToken, Task> Prepare(IReadOnlyList<string> args, CliOptions options, CliConsole console)
    {
        var name = Single(args, "health");
        var output = Scripting.OutputOf(options, ["text", "json"], "text");
        Scripting.Only(options, "health");
        var reference = EntityRef.Parse(name);
        if (reference is EntityRef.AppType or EntityRef.Store)
        {
            throw CliFailure.Usage($"{name} has no health; try a cluster, node, application, service, partition or replica.");
        }
        return async (s, ct) =>
        {
            var health = Dto.Health(await ReportAsync(s, await new Resolver(s).ResolveAsync(reference, ct), ct));
            if (output == "json")
            {
                Output.WriteJson(console.Out, health);
            }
            else
            {
                Write(console, health);
            }
        };
    }

    internal static string Single(IReadOnlyList<string> args, string command) => args switch
    {
        [var one] => one,
        [] => throw CliFailure.Usage($"Which entity? e.g. sfx {command} cluster, sfx {command} fabric:/orders."),
        _ => throw CliFailure.Usage($"Unexpected argument '{args[1]}'."),
    };

    private static Task<HealthReport> ReportAsync(UiSession s, Entity entity, CancellationToken ct) => entity switch
    {
        Entity.Cluster => s.Inspector.GetClusterHealthAsync(ct),
        Entity.Node n => s.Inspector.GetNodeHealthAsync(n.NodeName, ct),
        Entity.Application a => s.Inspector.GetApplicationHealthAsync(a.Id, ct),
        Entity.Service v => s.Inspector.GetServiceHealthAsync(v.Id, ct),
        Entity.Partition p => s.Inspector.GetPartitionHealthAsync(new(p.Id), ct),
        Entity.Replica r => s.Inspector.GetReplicaHealthAsync(new(r.Owner.Id), r.Id, ct),
        _ => throw CliFailure.Usage($"{entity.Name} has no health."),
    };

    /// <summary>As the terminal UI shows it: the state, the evaluation chain indented by depth, then the events.</summary>
    internal static void Write(CliConsole console, HealthDto health)
    {
        var o = console.Out;
        o.WriteLine($"State  {Output.Health(health.Health, console.Color)}");
        if (health.Evaluations.Count > 0)
        {
            o.WriteLine();
            o.WriteLine("Why it's unhealthy");
            foreach (var evaluation in health.Evaluations)
            {
                var indent = Indent + new string(' ', evaluation.Depth * 2);
                o.WriteLine($"{indent}{Output.Health(evaluation.Health, console.Color)}  {evaluation.Kind}");
                Paragraph(o, evaluation.Description, indent + Indent);
            }
        }
        o.WriteLine();
        o.WriteLine($"Events ({health.Events.Count})");
        foreach (var e in health.Events)
        {
            o.WriteLine($"{Indent}{Output.Health(e.Health, console.Color)}  {e.Source}  {e.Property}");
            Paragraph(o, e.Description, Indent + Indent);
            var when = e.Modified is { } time ? "updated " + Output.When(time) : "";
            if (when.Length > 0 || e.Expired)
            {
                o.WriteLine(Indent + Indent + when + (e.Expired ? (when.Length > 0 ? "  " : "") + "(expired)" : ""));
            }
        }
    }

    private static void Paragraph(TextWriter o, string text, string indent)
    {
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                o.WriteLine(indent + line.TrimEnd());
            }
        }
    }
}
