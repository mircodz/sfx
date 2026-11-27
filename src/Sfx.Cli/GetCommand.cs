using System.Globalization;
using Sfx.Core;
using Sfx.Web;

namespace Sfx.Cli;

/// <summary><c>sfx get &lt;kind&gt; [&lt;name&gt;]</c>: a list as the browser shows it, or the one named.</summary>
internal static class GetCommand
{
    public const string Help = """
        usage: sfx get <kind> [<name>] [options]

        Lists one kind of entity, or the one named.

          nodes [<node>]
          apps [<app>]
          services <app> | <service>
          partitions <service> | partition/<id>
          replicas [<app> | node/<node> | <service> | partition/<id> | replica/<partition id>/<id>]
          types [<type>[@<version>]]
          upgrades [cluster | <app>]
          repairs [<id>]
          operations                 what was changed through sfx: the action log

          -o table|wide|json|name    how to print them (default: table); name prints what to pass back to sfx
          --filter <text>            only rows containing the text, case aside
          --health ok|warning|error  only rows in these health states; several comma-separated
          --no-headers               leave out the header line
        """;

    private static readonly string[] Outputs = ["table", "wide", "json", "name"];

    public static Func<UiSession, CancellationToken, Task> Prepare(IReadOnlyList<string> args, CliOptions options, CliConsole console)
    {
        var (kind, name) = args switch
        {
            [var k] => (Kind(k), null),
            [var k, var n] => (Kind(k), n),
            [] => throw CliFailure.Usage($"Which kind? One of {string.Join(", ", Kinds)}."),
            _ => throw CliFailure.Usage($"Unexpected argument '{args[2]}'."),
        };
        var output = Scripting.OutputOf(options, Outputs, "table");
        Scripting.Only(options, "get", "--filter", "--health", "--no-headers");
        var print = new Printer(console, options, output);

        return kind switch
        {
            "nodes" => (s, ct) => NodesAsync(s, name, print, ct),
            "apps" => (s, ct) => ApplicationsAsync(s, name, print, ct),
            "services" => Needs(name, "an application", "sfx get services fabric:/orders", n => (s, ct) => ServicesAsync(s, n, print, ct)),
            "partitions" => Needs(name, "a service", "sfx get partitions fabric:/orders/Counter", n => (s, ct) => PartitionsAsync(s, n, print, ct)),
            "replicas" => (s, ct) => ReplicasAsync(s, name, print, ct),
            "types" => NoHealth(options, kind, (s, ct) => TypesAsync(s, name, print, ct)),
            "upgrades" => NoHealth(options, kind, (s, ct) => UpgradesAsync(s, name, print, ct)),
            "repairs" => NoHealth(options, kind, (s, ct) => RepairsAsync(s, name, print, ct)),
            _ => Operations(name, options, output, print),
        };
    }

    public static readonly IReadOnlyList<string> Kinds = ["nodes", "apps", "services", "partitions", "replicas", "types", "upgrades", "repairs", "operations"];

    public static string Kind(string typed) => typed.ToLowerInvariant() switch
    {
        "node" or "nodes" => "nodes",
        "app" or "apps" or "application" or "applications" => "apps",
        "service" or "services" or "svc" => "services",
        "partition" or "partitions" => "partitions",
        "replica" or "replicas" => "replicas",
        "type" or "types" or "apptype" or "apptypes" or "applicationtype" or "applicationtypes" => "types",
        "upgrade" or "upgrades" => "upgrades",
        "repair" or "repairs" => "repairs",
        "operation" or "operations" or "activity" => "operations",
        _ => throw CliFailure.Usage($"No kind '{typed}'. Kinds: {string.Join(", ", Kinds)}."),
    };

    private static Func<UiSession, CancellationToken, Task> Needs(
        string? name,
        string what,
        string example,
        Func<string, Func<UiSession, CancellationToken, Task>> run) =>
        name is null ? throw CliFailure.Usage($"These are listed per {what[(what.IndexOf(' ') + 1)..]}: give {what}, e.g. {example}.") : run(name);

    private static Func<UiSession, CancellationToken, Task> NoHealth(CliOptions options, string kind, Func<UiSession, CancellationToken, Task> run) =>
        options.Health is null ? run : throw CliFailure.Usage($"--health doesn't apply to {kind}.");

    private static Func<UiSession, CancellationToken, Task> Operations(string? name, CliOptions options, string output, Printer print)
    {
        if (name is not null)
        {
            throw CliFailure.Usage("operations takes no name; use --filter.");
        }
        if (output == "name")
        {
            throw CliFailure.Usage("-o name doesn't apply to operations.");
        }
        return NoHealth(options, "operations", (s, _) =>
        {
            print.List(ApiReads.Activity(s), ActivityColumns, a => "", null);
            return Task.CompletedTask;
        });
    }

    private static async Task NodesAsync(UiSession s, string? name, Printer print, CancellationToken ct)
    {
        var nodes = Sorted(await ApiReads.NodesAsync(s, ct), n => n.Name);
        if (name is null)
        {
            print.List(nodes, NodeColumns, n => $"node/{n.Name}", n => n.Health);
            return;
        }
        var wanted = EntityRef.Parse(name, EntityRef.Bare.Node) as EntityRef.Node ?? throw CliFailure.Usage($"'{name}' isn't a node.");
        var node = nodes.FirstOrDefault(n => n.Name == wanted.Name)
            ?? One(nodes.Where(n => n.Name.Equals(wanted.Name, StringComparison.OrdinalIgnoreCase)), wanted.Name, n => $"node/{n.Name}")
            ?? throw CliFailure.NotFound($"There's no node {wanted.Name} on this cluster.");
        print.One(node, NodeColumns, n => $"node/{n.Name}", n => n.Health);
    }

    private static async Task ApplicationsAsync(UiSession s, string? name, Printer print, CancellationToken ct)
    {
        var apps = (await ApiReads.ApplicationsAsync(s, ct))
            .OrderBy(a => a.Id != ClusterQueries.SystemApplicationId)
            .ThenBy(a => a.Name, NaturalComparer.Instance)
            .ToList();
        if (name is null)
        {
            print.List(apps, ApplicationColumns, a => a.Name, a => a.Health);
            return;
        }
        var wanted = EntityRef.Parse(name) as EntityRef.Named ?? throw CliFailure.Usage($"'{name}' isn't an application.");
        var app = Match(apps, a => a.Name, wanted) ?? throw CliFailure.NotFound($"There's no application {wanted.Name}.");
        print.One(app, ApplicationColumns, a => a.Name, a => a.Health);
    }

    private static async Task ServicesAsync(UiSession s, string name, Printer print, CancellationToken ct)
    {
        switch (await new Resolver(s).ResolveAsync(EntityRef.Parse(name), ct))
        {
            case Entity.Application app:
                print.List(Sorted(await ApiReads.ServicesAsync(s, app.Id, ct), v => v.Name), ServiceColumns, v => v.Name, v => v.Health);
                break;
            case Entity.Service service:
                var one = (await ApiReads.ServicesAsync(s, service.ApplicationId, ct)).FirstOrDefault(v => v.Id == service.Id)
                    ?? throw CliFailure.NotFound($"There's no service {service.ServiceName}.");
                print.One(one, ServiceColumns, v => v.Name, v => v.Health);
                break;
            case var other:
                throw CliFailure.Usage($"Services are listed per application, and {other.Name} isn't one.");
        }
    }

    private static async Task PartitionsAsync(UiSession s, string name, Printer print, CancellationToken ct)
    {
        switch (await new Resolver(s).ResolveAsync(EntityRef.Parse(name), ct))
        {
            case Entity.Service service:
                print.List(ByKey(await ApiReads.PartitionsAsync(s, service.Id, ct)), PartitionColumns, p => $"partition/{p.Id}", p => p.Health);
                break;
            case Entity.Partition partition:
                var partitions = await ApiReads.PartitionsAsync(s, partition.ServiceId, ct);
                var one = partitions.FirstOrDefault(p => p.Id == partition.Id.ToString())
                    ?? throw CliFailure.NotFound($"There's no partition {partition.Id} in {partition.ServiceName}.");
                print.One(one, PartitionColumns, p => $"partition/{p.Id}", p => p.Health);
                break;
            case var other:
                throw CliFailure.Usage($"Partitions are listed per service, and {other.Name} isn't one.");
        }
    }

    private static async Task ReplicasAsync(UiSession s, string? name, Printer print, CancellationToken ct)
    {
        static string NameOf(ClusterReplicaDto r) => $"replica/{r.PartitionId}/{r.ReplicaId}";
        if (name is null)
        {
            print.List(await s.GetClusterReplicasAsync(ct), ReplicaColumns, NameOf, r => r.Health);
            return;
        }
        switch (await new Resolver(s).ResolveAsync(EntityRef.Parse(name), ct))
        {
            case Entity.Application app:
                print.List((await s.GetClusterReplicasAsync(ct)).Where(r => r.ApplicationId == app.Id).ToList(), ReplicaColumns, NameOf, r => r.Health);
                break;
            case Entity.Node node:
                print.List((await s.GetClusterReplicasAsync(ct)).Where(r => r.Node == node.NodeName).ToList(), ReplicaColumns, NameOf, r => r.Health);
                break;
            case Entity.Service service:
                print.List(await s.GetServiceReplicasAsync(service.ApplicationId, service.Id, ct), ReplicaColumns, NameOf, r => r.Health);
                break;
            case Entity.Partition partition:
                var inPartition = (await s.GetServiceReplicasAsync(partition.ApplicationId, partition.ServiceId, ct))
                    .Where(r => r.PartitionId == partition.Id.ToString())
                    .ToList();
                print.List(inPartition, ReplicaColumns, NameOf, r => r.Health);
                break;
            case Entity.Replica replica:
                var p = replica.Owner;
                var one = (await s.GetServiceReplicasAsync(p.ApplicationId, p.ServiceId, ct))
                    .FirstOrDefault(r => r.PartitionId == p.Id.ToString() && r.ReplicaId == replica.Id)
                    ?? throw CliFailure.NotFound($"There's no replica {replica.Id} in partition {p.Id}.");
                print.One(one, ReplicaColumns, NameOf, r => r.Health);
                break;
            case var other:
                throw CliFailure.Usage($"{other.Name} has no replicas to list.");
        }
    }

    private static async Task TypesAsync(UiSession s, string? name, Printer print, CancellationToken ct)
    {
        static string NameOf(ApplicationTypeDto t) => $"type/{t.Name}@{t.Version}";
        var types = (await ApiReads.TypesAsync(s, ct))
            .OrderBy(t => t.Name, NaturalComparer.Instance)
            .ThenByDescending(t => t.Version, NaturalComparer.Instance)
            .ToList();
        if (name is null)
        {
            print.List(types, TypeColumns, NameOf, null);
            return;
        }
        var wanted = EntityRef.Parse(name, EntityRef.Bare.Type) as EntityRef.AppType ?? throw CliFailure.Usage($"'{name}' isn't an application type.");
        var versions = types.Where(t => t.Name == wanted.Name).ToList() is { Count: > 0 } exact
            ? exact
            : types.Where(t => t.Name.Equals(wanted.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (versions.Select(t => t.Name).Distinct().Count() > 1)
        {
            var names = string.Join(", ", versions.Select(t => $"type/{t.Name}").Distinct());
            throw CliFailure.Usage($"'{wanted.Name}' could be any of {names}; give the exact name.");
        }
        if (versions.Count == 0)
        {
            throw CliFailure.NotFound($"There's no application type {wanted.Name} on this cluster.");
        }
        if (wanted.Version is null)
        {
            print.List(versions, TypeColumns, NameOf, null);
            return;
        }
        var version = versions.FirstOrDefault(t => t.Version == wanted.Version)
            ?? throw CliFailure.NotFound($"{versions[0].Name} has no version {wanted.Version} provisioned.");
        print.One(version, TypeColumns, NameOf, null);
    }

    private static async Task UpgradesAsync(UiSession s, string? name, Printer print, CancellationToken ct)
    {
        static string NameOf(UpgradeDto u) => u.Kind == "cluster" ? "cluster" : u.Name;
        var upgrades = await ApiReads.UpgradesAsync(s, ct);
        if (name is null)
        {
            print.List(upgrades, UpgradeColumns, NameOf, null);
            return;
        }
        var wanted = EntityRef.Parse(name);
        var upgrade = wanted switch
        {
            EntityRef.Cluster => upgrades.FirstOrDefault(u => u.Kind == "cluster"),
            EntityRef.Named n => Match(upgrades.Where(u => u.Kind != "cluster").ToList(), NameOf, n),
            _ => throw CliFailure.Usage($"Upgrades are of the cluster or an application, and '{name}' is neither."),
        };
        print.One(upgrade ?? throw CliFailure.NotFound($"There's no upgrade of {name} on record."), UpgradeColumns, NameOf, null);
    }

    private static async Task RepairsAsync(UiSession s, string? id, Printer print, CancellationToken ct)
    {
        var repairs = await ApiReads.RepairsAsync(s, ct);
        if (repairs.Unavailable is { } why)
        {
            print.Note(why);
        }
        if (id is null)
        {
            print.List(repairs.Repairs, RepairColumns, r => r.Id, null, kept => repairs with { Repairs = kept });
            return;
        }
        var repair = repairs.Repairs.FirstOrDefault(r => r.Id == id) ?? throw CliFailure.NotFound($"There's no repair task {id}.");
        print.One(repair, RepairColumns, r => r.Id, null);
    }

    /// <summary>In the browser's order: by name, as people read numbers.</summary>
    internal static List<T> Sorted<T>(IEnumerable<T> items, Func<T, string> name) => items.OrderBy(name, NaturalComparer.Instance).ToList();

    /// <summary>Ranged partitions in key order, as the browser shows them; named ones by name.</summary>
    private static IReadOnlyList<PartitionDto> ByKey(IReadOnlyList<PartitionDto> partitions)
    {
        static long? Key(PartitionDto p) => long.TryParse(p.LowKey, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var key) ? key : null;
        if (partitions.All(p => Key(p) is not null))
        {
            return partitions.OrderBy(Key).ToList();
        }
        return partitions.All(p => p.Name is not null) ? Sorted(partitions, p => p.Name!) : partitions;
    }

    private static T? One<T>(IEnumerable<T> matches, string typed, Func<T, string> name) where T : class => matches.ToList() switch
    {
        [] => null,
        [var one] => one,
        var several => throw CliFailure.Usage(
            $"'{typed}' could be any of {string.Join(", ", several.Select(name).Order(StringComparer.OrdinalIgnoreCase))}; give the full name."),
    };

    /// <summary>The exact name; for a short one, else the name cased differently, else the one whose last parts it is.</summary>
    private static T? Match<T>(IReadOnlyList<T> items, Func<T, string> name, EntityRef.Named wanted) where T : class
    {
        if (items.FirstOrDefault(i => name(i) == wanted.Name) is { } exact)
        {
            return exact;
        }
        if (wanted.Short is not { } typed)
        {
            return null;
        }
        var cased = items.Where(i => name(i).Equals(wanted.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        return One(cased.Count > 0 ? cased : items.Where(i => name(i).EndsWith("/" + typed, StringComparison.OrdinalIgnoreCase)), typed, name);
    }

    internal static readonly Column<NodeDto>[] NodeColumns =
    [
        new("NAME", n => n.Name),
        new("STATUS", n => n.Status),
        new("HEALTH", n => n.Health, Health: true),
        new("TYPE", n => n.NodeType),
        new("ADDRESS", n => n.Address),
        new("UD", n => n.UpgradeDomain),
        new("FD", n => n.FaultDomain),
        new("SEED", n => n.IsSeed ? "yes" : "no", Wide: true),
        new("VERSION", n => n.CodeVersion, Wide: true),
        new("UPTIME", n => Output.Duration(n.UptimeSeconds is { } up ? TimeSpan.FromSeconds(up) : null), Wide: true, Right: true),
        new("REASON", n => n.Reason, Wide: true),
    ];

    internal static readonly Column<ApplicationDto>[] ApplicationColumns =
    [
        new("NAME", a => a.Name),
        new("TYPE", a => a.TypeName),
        new("VERSION", a => a.TypeVersion),
        new("STATUS", a => a.Status),
        new("HEALTH", a => a.Health, Health: true),
        new("ID", a => a.Id, Wide: true),
        new("REASON", a => a.Reason, Wide: true),
    ];

    internal static readonly Column<ServiceDto>[] ServiceColumns =
    [
        new("NAME", v => v.Name),
        new("TYPE", v => v.TypeName),
        new("KIND", v => v.Kind),
        new("STATUS", v => v.Status),
        new("HEALTH", v => v.Health, Health: true),
        new("ID", v => v.Id, Wide: true),
        new("REASON", v => v.Reason, Wide: true),
    ];

    internal static readonly Column<PartitionDto>[] PartitionColumns =
    [
        new("ID", p => p.Id),
        new("KIND", p => p.Kind),
        new("PARTITIONING", p => p.Scheme),
        new("REPLICAS", p => p.ReplicaSet),
        new("STATUS", p => p.Status),
        new("HEALTH", p => p.Health, Health: true),
        new("KEY", Key, Wide: true),
        new("REASON", p => p.Reason, Wide: true),
    ];

    internal static string Key(PartitionDto p) => p.Name ?? (p.LowKey is null ? "" : $"{p.LowKey}..{p.HighKey}");

    internal static readonly Column<ClusterReplicaDto>[] ReplicaColumns =
    [
        new("SERVICE", r => r.Service),
        new("PARTITION", r => r.PartitionId),
        new("REPLICA", r => r.ReplicaId),
        new("NODE", r => r.Node),
        new("ROLE", r => r.Role),
        new("STATUS", r => r.Status),
        new("HEALTH", r => r.Health, Health: true),
        new("APPLICATION", r => r.Application, Wide: true),
        new("KIND", r => r.ServiceKind, Wide: true),
        new("PARTITION STATUS", r => r.PartitionStatus, Wide: true),
        new("PARTITION HEALTH", r => r.PartitionHealth, Wide: true, Health: true),
    ];

    internal static readonly Column<ApplicationTypeDto>[] TypeColumns =
    [
        new("NAME", t => t.Name),
        new("VERSION", t => t.Version),
        new("STATUS", t => t.Status),
        new("APPS", t => t.Applications.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), Right: true),
        new("USED BY", t => string.Join(", ", t.Applications), Wide: true),
        new("DETAILS", t => t.StatusDetails, Wide: true),
    ];

    internal static readonly Column<UpgradeDto>[] UpgradeColumns =
    [
        new("NAME", u => u.Name),
        new("KIND", u => u.Kind),
        new("VERSION", u => u.From.Length > 0 ? $"{u.From} -> {u.Target}" : u.Target),
        new("MODE", u => u.Mode),
        new("STATE", u => u.State),
        new("STARTED", u => Output.When(u.Started)),
        new("DOMAIN", u => u.CurrentDomain, Wide: true),
        new("NEXT", u => u.NextDomain, Wide: true),
        new("PHASE", u => u.HealthCheckPhase, Wide: true),
        new("FAILURE", u => u.FailureReason, Wide: true),
    ];

    internal static readonly Column<RepairDto>[] RepairColumns =
    [
        new("ID", r => r.Id),
        new("STATE", r => r.State),
        new("ACTION", r => r.Action),
        new("NODES", r => string.Join(",", r.Nodes)),
        new("RESULT", r => r.Result),
        new("CREATED", r => Output.When(r.Created)),
        new("EXECUTOR", r => r.Executor, Wide: true),
        new("COMPLETED", r => Output.When(r.Completed), Wide: true),
        new("DESCRIPTION", r => r.Description, Wide: true),
    ];

    internal static readonly Column<ActivityDto>[] ActivityColumns =
    [
        new("TIME", a => Output.When(a.Time.UtcDateTime)),
        new("RESULT", a => a.Succeeded ? "ok" : "failed"),
        new("CONTEXT", a => a.Context),
        new("ACTION", a => a.Action),
        new("USER", a => a.User, Wide: true),
        new("OUTCOME", a => a.Outcome, Wide: true),
    ];

    /// <summary>Prints rows as <c>-o</c> asks, after <c>--filter</c> and <c>--health</c>.</summary>
    private sealed class Printer(CliConsole console, CliOptions options, string output)
    {
        /// <param name="json">The browser's shape for the rows kept, when it isn't the list itself.</param>
        public void List<T>(
            IReadOnlyList<T> rows,
            IReadOnlyList<Column<T>> columns,
            Func<T, string> name,
            Func<T, string>? health,
            Func<IReadOnlyList<T>, object>? json = null)
        {
            var kept = rows.Where(r => Keep(r, columns, health)).ToList();
            switch (output)
            {
                case "json" when json is not null:
                    Output.WriteJson(console.Out, json(kept));
                    break;
                case "json":
                    Output.WriteJson<IReadOnlyList<T>>(console.Out, kept);
                    break;
                case "name":
                    kept.ForEach(r => console.Out.WriteLine(name(r)));
                    break;
                default:
                    Output.Table(console, kept, columns.Where(c => output == "wide" || !c.Wide).ToList(), !options.NoHeaders);
                    break;
            }
        }

        /// <summary>One entity: an object in JSON, a single row otherwise; nothing when the filters leave it out.</summary>
        public void One<T>(T row, IReadOnlyList<Column<T>> columns, Func<T, string> name, Func<T, string>? health)
        {
            if (output != "json")
            {
                List([row], columns, name, health);
            }
            else if (Keep(row, columns, health))
            {
                Output.WriteJson(console.Out, row);
            }
        }

        public void Note(string text) => console.Error.WriteLine(text);

        private bool Keep<T>(T row, IReadOnlyList<Column<T>> columns, Func<T, string>? health) =>
            (options.Health is null || (health is not null && options.Health.Contains(health(row))))
            && (options.Filter is not { } text || columns.Any(c => Output.OneLine(c.Cell(row)).Contains(text, StringComparison.OrdinalIgnoreCase)));
    }
}
