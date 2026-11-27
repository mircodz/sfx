using System.Text.Json.Nodes;
using Sfx.Web;

namespace Sfx.Cli;

/// <summary><c>sfx actions &lt;entity&gt;</c>: the actions this context may run on it, as the browser offers them. Runs nothing.</summary>
internal static class ActionsCommand
{
    public const string Help = """
        usage: sfx actions <entity> [-o table|wide|json]

        The actions available on the entity in this context, and the arguments each takes (key=value). Runs nothing.
        --readonly offers none; the riskiest need --power (or a context saved with it).
        <entity>: cluster, node/<name>, an application or service, partition/<id>, replica/<partition id>/<id>,
        type/<name>@<version> or store:/<path>.

          -o table|wide|json    how to print them (default: table); wide adds each action's warning
          --no-headers          leave out the header line
        """;

    public static Func<UiSession, CancellationToken, Task> Prepare(IReadOnlyList<string> args, CliOptions options, CliConsole console)
    {
        var name = HealthCommand.Single(args, "actions");
        var output = Scripting.OutputOf(options, ["table", "wide", "json"], "table");
        Scripting.Only(options, "actions", "--no-headers");
        var reference = EntityRef.Parse(name);
        return async (s, ct) =>
        {
            var entity = await new Resolver(s).ResolveAsync(reference, ct);
            var actions = await ActionsAsync(s, entity, ct);
            if (output == "json")
            {
                var list = new JsonArray();
                foreach (var action in actions)
                {
                    var node = Output.ToNode(action).AsObject();
                    node["power"] = WebActions.NeedsPower(action.Id);
                    list.Add(node);
                }
                Output.WriteJson(console.Out, list);
                return;
            }
            Output.Table(console, actions, Columns.Where(c => output == "wide" || !c.Wide).ToList(), !options.NoHeaders);
            if (s.ReadOnly)
            {
                console.Error.WriteLine("This context is read-only; it offers no actions.");
            }
            else if (!s.Power && WebActions.HasPowerActions(KindOf(entity)))
            {
                console.Error.WriteLine("The riskiest actions need --power.");
            }
        };
    }

    private static string KindOf(Entity entity) => entity switch
    {
        Entity.Cluster => "cluster",
        Entity.Node => "node",
        Entity.Application => "application",
        Entity.Service => "service",
        Entity.Partition => "partition",
        Entity.Replica => "replica",
        Entity.AppType => "type",
        _ => "store",
    };

    internal static async Task<IReadOnlyList<ActionDto>> ActionsAsync(UiSession s, Entity entity, CancellationToken ct)
    {
        switch (entity)
        {
            case Entity.Cluster:
                var cluster = ApiReads.ClusterAsync(s, ct);
                var upgrade = (await ApiReads.UpgradesAsync(s, ct)).FirstOrDefault(u => u.Kind == "cluster");
                return [.. (await cluster).Actions, .. upgrade?.Actions ?? []];
            case Entity.Node n:
                var node = (await ApiReads.NodesAsync(s, ct)).FirstOrDefault(x => x.Name == n.NodeName)
                    ?? throw CliFailure.NotFound($"There's no node {n.NodeName} on this cluster.");
                return node.Actions;
            case Entity.Application a:
                var app = await ApiReads.ApplicationAsync(s, a.Id, ct);
                return [.. app.Actions, .. app.Upgrade?.Actions ?? []];
            case Entity.Service v:
                return (await ApiReads.ServiceAsync(s, v.ApplicationId, v.Id, ct)).Actions;
            case Entity.Partition p:
                return (await ApiReads.PartitionAsync(s, p.ApplicationId, p.ServiceId, p.Id.ToString(), ct)).Actions;
            case Entity.Replica r:
                var partition = await ApiReads.PartitionAsync(s, r.Owner.ApplicationId, r.Owner.ServiceId, r.Owner.Id.ToString(), ct);
                var replica = partition.Replicas.FirstOrDefault(x => x.Id == r.Id)
                    ?? throw CliFailure.NotFound($"There's no replica {r.Id} in partition {r.Owner.Id}.");
                return replica.Actions;
            case Entity.AppType t:
                var versions = (await ApiReads.TypesAsync(s, ct)).Where(x => x.Name == t.TypeName && (t.Version is null || x.Version == t.Version)).ToList();
                return versions switch
                {
                    [var one] => one.Actions,
                    _ => throw CliFailure.Usage(
                        $"Actions are per version; give one of {string.Join(", ", versions.Select(x => $"type/{x.Name}@{x.Version}"))}."),
                };
            case Entity.StorePath { Path: var path }:
                return await StoreActionsAsync(s, path, ct);
            default:
                return [];
        }
    }

    private static async Task<IReadOnlyList<ActionDto>> StoreActionsAsync(UiSession s, string path, CancellationToken ct)
    {
        var normal = StoreCommand.Slashed(path);
        if (normal.Length == 0)
        {
            throw CliFailure.Usage("The store's root has no actions; give a folder or file in it.");
        }
        var parent = normal.LastIndexOf('/') is var slash and > 0 ? normal[..slash] : "";
        var listing = await ApiReads.StoreAsync(s, parent, ct);
        return listing.Folders.FirstOrDefault(f => StoreCommand.Slashed(f.Path) == normal)?.Actions
            ?? listing.Files.FirstOrDefault(f => StoreCommand.Slashed(f.Path) == normal)?.Actions
            ?? throw CliFailure.NotFound($"There's no {normal} in the image store.");
    }

    /// <summary>What <c>--arg</c> takes: <c>key=&lt;kind&gt;</c>, or the choices.</summary>
    internal static string Arguments(ActionDto action) => string.Join(' ', action.Inputs.Select(i => i.Choices is { Count: > 0 } choices
        ? $"{i.Name}={string.Join('|', choices.Select(c => c.Value))}"
        : $"{i.Name}=<{i.Kind}>"));

    private static readonly Column<ActionDto>[] Columns =
    [
        new("ID", a => a.Id),
        new("TITLE", a => a.Title),
        new("RISK", a => a.Risk),
        new("POWER", a => WebActions.NeedsPower(a.Id) ? "yes" : "no"),
        new("CONFIRM", a => a.ConfirmText),
        new("ARGS", Arguments),
        new("WARNING", a => a.Warning, Wide: true),
    ];
}
