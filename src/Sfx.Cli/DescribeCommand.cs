using System.Globalization;
using Sfx.Web;

namespace Sfx.Cli;

/// <summary><c>sfx describe &lt;entity&gt;</c>: what the entity's page in the browser shows.</summary>
internal static class DescribeCommand
{
    public const string Help = """
        usage: sfx describe <entity> [-o text|json]

        What the entity's page shows: its details, load, and what it contains.
        <entity>: cluster, node/<name>, an application or service, partition/<id>, replica/<partition id>/<id> or type/<name>[@<version>].

          -o text|json    how to print it (default: text); json is what the browser gets for the page
        """;

    public static Func<UiSession, CancellationToken, Task> Prepare(IReadOnlyList<string> args, CliOptions options, CliConsole console)
    {
        var name = HealthCommand.Single(args, "describe");
        var output = Scripting.OutputOf(options, ["text", "json"], "text");
        Scripting.Only(options, "describe");
        var reference = EntityRef.Parse(name);
        if (reference is EntityRef.Store)
        {
            throw CliFailure.Usage("For the image store, use sfx store ls.");
        }
        var json = output == "json";
        return async (s, ct) =>
        {
            switch (await new Resolver(s).ResolveAsync(reference, ct))
            {
                case Entity.Cluster:
                    var cluster = await ApiReads.ClusterAsync(s, ct);
                    Print(console, json, cluster, () => Cluster(console, cluster));
                    break;
                case Entity.Node n:
                    var node = await ApiReads.NodeAsync(s, n.NodeName, ct);
                    Print(console, json, node, () => Node(console, node));
                    break;
                case Entity.Application a:
                    var app = await ApiReads.ApplicationAsync(s, a.Id, ct);
                    Print(console, json, app, () => Application(console, app));
                    break;
                case Entity.Service v:
                    var service = await ApiReads.ServiceAsync(s, v.ApplicationId, v.Id, ct);
                    Print(console, json, service, () => Service(console, service));
                    break;
                case Entity.Partition p:
                    var partition = await ApiReads.PartitionAsync(s, p.ApplicationId, p.ServiceId, p.Id.ToString(), ct);
                    Print(console, json, partition, () => Partition(console, partition));
                    break;
                case Entity.Replica r:
                    var owner = await ApiReads.PartitionAsync(s, r.Owner.ApplicationId, r.Owner.ServiceId, r.Owner.Id.ToString(), ct);
                    var replica = owner.Replicas.FirstOrDefault(x => x.Id == r.Id)
                        ?? throw CliFailure.NotFound($"There's no replica {r.Id} in partition {r.Owner.Id}.");
                    Print(console, json, replica, () => Replica(console, owner, replica));
                    break;
                case Entity.AppType t:
                    var versions = (await ApiReads.TypesAsync(s, ct))
                        .Where(x => x.Name == t.TypeName && (t.Version is null || x.Version == t.Version))
                        .ToList();
                    if (versions is [var one])
                    {
                        Print(console, json, one, () => Type(console, one));
                    }
                    else
                    {
                        Print(console, json, versions, () => versions.ForEach(v => Type(console, v, separate: v != versions[0])));
                    }
                    break;
            }
        };
    }

    private static void Print<T>(CliConsole console, bool json, T value, Action text)
    {
        if (json)
        {
            Output.WriteJson(console.Out, value);
        }
        else
        {
            text();
        }
    }

    private static void Cluster(CliConsole console, ClusterDto c)
    {
        Output.Fields(console, [("Name", c.Name), ("Endpoint", c.Endpoint), ("Version", c.CodeVersion), ("Health", c.Health.Health)]);
        Why(console, c.Health, "cluster");
    }

    private static void Node(CliConsole console, NodeDetailDto d)
    {
        var n = d.Node;
        Output.Fields(
            console,
            [
                ("Name", n.Name),
                ("Status", n.Status),
                ("Health", n.Health),
                ("Type", n.NodeType),
                ("Address", n.Address),
                ("Upgrade domain", n.UpgradeDomain),
                ("Fault domain", n.FaultDomain),
                ("Seed", n.IsSeed ? "yes" : "no"),
                ("Version", n.CodeVersion),
                ("Uptime", n.UptimeSeconds is { } up ? Output.Duration(TimeSpan.FromSeconds(up)) : null),
            ]);
        Why(console, d.Health, $"node/{n.Name}");
        Load(console, d.Load);
        Output.Section(console, $"Replicas ({d.Replicas.Count})");
        Output.Table(console, d.Replicas, DeployedColumns, indent: "  ");
    }

    private static void Application(CliConsole console, ApplicationDetailDto d)
    {
        var a = d.Application;
        Output.Fields(
            console,
            [
                ("Name", a.Name),
                ("Type", a.TypeName),
                ("Version", a.TypeVersion),
                ("Status", a.Status),
                ("Health", a.Health),
                ("Upgrade", d.Upgrade is { } u ? $"{u.State}, {u.From} -> {u.Target}" : null),
            ]);
        Why(console, d.Health, a.Name);
        if (d.Parameters.Count > 0)
        {
            Output.Section(console, $"Parameters ({d.Parameters.Count})");
            Output.Table(console, d.Parameters, ParameterColumns, indent: "  ");
        }
        Output.Section(console, $"Services ({d.Services.Count})");
        Output.Table(console, d.Services, GetCommand.ServiceColumns.Where(c => !c.Wide).ToList(), indent: "  ");
    }

    private static void Service(CliConsole console, ServiceDetailDto d)
    {
        var v = d.Service;
        IEnumerable<(string, string?)> fields =
        [
            ("Name", v.Name),
            ("Type", v.TypeName),
            ("Kind", v.Kind),
            ("Status", v.Status),
            ("Health", v.Health),
        ];
        Output.Fields(console, fields.Concat(d.Description.Select(f => (f.Name, (string?)f.Value))));
        Why(console, d.Health, v.Name);
        Output.Section(console, $"Partitions ({d.Partitions.Count})");
        Output.Table(console, d.Partitions, GetCommand.PartitionColumns.Where(c => !c.Wide || c.Header == "KEY").ToList(), indent: "  ");
    }

    private static void Partition(CliConsole console, PartitionDetailDto d)
    {
        var p = d.Partition;
        Output.Fields(
            console,
            [
                ("Id", p.Id),
                ("Service", d.Service.Name),
                ("Kind", p.Kind),
                ("Partitioning", p.Scheme),
                ("Key", GetCommand.Key(p)),
                ("Replicas", p.ReplicaSet),
                ("Status", p.Status),
                ("Health", p.Health),
            ]);
        Why(console, d.Health, $"partition/{p.Id}");
        Load(console, d.Load);
        Output.Section(console, $"Replicas ({d.Replicas.Count})");
        Output.Table(console, d.Replicas, ReplicaColumns, indent: "  ");
    }

    private static void Replica(CliConsole console, PartitionDetailDto owner, ReplicaDto r)
    {
        Output.Fields(
            console,
            [
                ("Id", r.Id),
                ("Partition", owner.Partition.Id),
                ("Service", owner.Service.Name),
                ("Node", r.NodeName),
                ("Role", r.Role),
                ("Status", r.Status),
                ("Health", r.Health),
                ("Address", r.Address),
            ]);
        if (r.Health is not ("Healthy" or "Unknown"))
        {
            console.Out.WriteLine();
            console.Out.WriteLine($"See sfx health replica/{owner.Partition.Id}/{r.Id} for why.");
        }
    }

    private static void Type(CliConsole console, ApplicationTypeDto t, bool separate = false)
    {
        if (separate)
        {
            console.Out.WriteLine();
        }
        Output.Fields(
            console,
            [
                ("Name", t.Name),
                ("Version", t.Version),
                ("Status", t.Status),
                ("Details", t.StatusDetails),
                ("Applications", t.Applications.Count == 0 ? "none" : string.Join(", ", t.Applications)),
            ]);
        if (t.DefaultParameters.Count > 0)
        {
            Output.Section(console, $"Default parameters ({t.DefaultParameters.Count})");
            Output.Table(console, t.DefaultParameters.ToList(), DefaultColumns, indent: "  ");
        }
    }

    /// <summary>The first level of why it's unhealthy (or its own unhealthy events), and where to see the rest.</summary>
    private static void Why(CliConsole console, HealthDto health, string name)
    {
        var reasons = health.Evaluations
            .Where(e => e.Depth == 0)
            .Select(e => (e.Health, What: $"{e.Kind}: {Output.OneLine(e.Description)}"))
            .ToList();
        if (reasons.Count == 0)
        {
            reasons = health.Events
                .Where(e => e.Health is "Warning" or "Error")
                .Select(e => (e.Health, What: $"{e.Source} {e.Property}: {Output.OneLine(e.Description)}"))
                .ToList();
        }
        if (reasons.Count == 0)
        {
            return;
        }
        Output.Section(console, "Why it's unhealthy");
        foreach (var (state, what) in reasons)
        {
            console.Out.WriteLine($"  {Output.Health(state, console.Color)}  {what}");
        }
        console.Out.WriteLine($"  See sfx health {name} for more.");
    }

    private static void Load(CliConsole console, IReadOnlyList<MetricDto> load)
    {
        if (load.Count > 0)
        {
            Output.Section(console, $"Load ({load.Count})");
            Output.Table(console, load, MetricColumns, indent: "  ");
        }
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static readonly Column<MetricDto>[] MetricColumns =
    [
        new("METRIC", m => m.Name),
        new("LOAD", m => Number(m.Value), Right: true),
        new("CAPACITY", m => m.Capacity is { } c ? Number(c) : "-", Right: true),
    ];

    private static readonly Column<DeployedReplicaDto>[] DeployedColumns =
    [
        new("APPLICATION", r => r.Application),
        new("SERVICE", r => r.Service),
        new("PARTITION", r => r.PartitionId),
        new("REPLICA", r => r.Id),
        new("ROLE", r => r.Role),
        new("STATUS", r => r.Status),
    ];

    private static readonly Column<ReplicaDto>[] ReplicaColumns =
    [
        new("ID", r => r.Id),
        new("NODE", r => r.NodeName),
        new("ROLE", r => r.Role),
        new("STATUS", r => r.Status),
        new("HEALTH", r => r.Health, Health: true),
        new("ADDRESS", r => r.Address),
    ];

    private static readonly Column<ParameterDto>[] ParameterColumns =
    [
        new("NAME", p => p.Name),
        new("VALUE", p => p.Value),
        new("DEFAULT", p => p.Overridden ? p.Default ?? "" : "(default)"),
    ];

    private static readonly Column<KeyValuePair<string, string>>[] DefaultColumns =
    [
        new("NAME", p => p.Key),
        new("DEFAULT", p => p.Value),
    ];
}
