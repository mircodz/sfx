using Microsoft.ServiceFabric.Common;
using Microsoft.ServiceFabric.Common.Exceptions;
using Sfx.Core;
using Sfx.Web;

namespace Sfx.Cli;

/// <summary>An entity as typed, before the cluster is asked about it.</summary>
internal abstract record EntityRef
{
    public sealed record Cluster : EntityRef;

    public sealed record Node(string Name) : EntityRef;

    /// <summary>An application or service, as <c>fabric:/…</c>; <paramref name="Short"/> is what was typed when that was left out.</summary>
    public sealed record Named(string Name, string? Short) : EntityRef;

    public sealed record Partition(Guid Id) : EntityRef;

    public sealed record Replica(Guid PartitionId, string Id) : EntityRef;

    public sealed record AppType(string Name, string? Version) : EntityRef;

    public sealed record Store(string Path) : EntityRef;

    /// <param name="bare">What a name without a prefix is: an application or service, or (where only nodes make sense) a node.</param>
    public static EntityRef Parse(string text, Bare bare = Bare.Named)
    {
        var t = text.Trim();
        if (t.Length == 0)
        {
            throw CliFailure.Usage("An empty name.");
        }
        if (t == "cluster")
        {
            return new Cluster();
        }
        if (Prefixed(t, "node/") is { } node)
        {
            return new Node(node.Length > 0 ? node : throw CliFailure.Usage("node/ needs a node name, e.g. node/_Node_0."));
        }
        if (Prefixed(t, "partition/") is { } partition)
        {
            return new Partition(ParseGuid(partition));
        }
        if (Prefixed(t, "replica/") is { } replica)
        {
            var parts = replica.Split('/');
            return parts is [var p, { Length: > 0 } r]
                ? new Replica(ParseGuid(p), r)
                : throw CliFailure.Usage($"'{text}' isn't a replica; write replica/<partition id>/<replica id>.");
        }
        if (Prefixed(t, "type/") is { } type)
        {
            var at = type.LastIndexOf('@');
            var (name, version) = at > 0 ? (type[..at], type[(at + 1)..]) : (type, null);
            return name.Length > 0 && version is not { Length: 0 }
                ? new AppType(name, version)
                : throw CliFailure.Usage($"'{text}' isn't an application type; write type/<name> or type/<name>@<version>.");
        }
        if (t.StartsWith("store:", StringComparison.Ordinal))
        {
            return new Store(ImageStore.Normalize(t["store:".Length..]));
        }
        if (t.StartsWith("fabric:/", StringComparison.Ordinal))
        {
            return t.Length > "fabric:/".Length ? new Named(t.TrimEnd('/'), null) : throw CliFailure.Usage("fabric:/ needs a name.");
        }
        if (Guid.TryParse(t, out var id))
        {
            return new Partition(id);
        }
        return bare switch
        {
            Bare.Node => new Node(t),
            Bare.Type => Parse("type/" + t),
            Bare.Store => new Store(ImageStore.Normalize(t)),
            _ => new Named("fabric:/" + t.Trim('/'), t.Trim('/')),
        };
    }

    public enum Bare { Named, Node, Type, Store }

    private static string? Prefixed(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text[prefix.Length..] : null;

    private static Guid ParseGuid(string text) =>
        Guid.TryParse(text, out var id) ? id : throw CliFailure.Usage($"'{text}' isn't a partition id.");
}

/// <summary>An entity the cluster has; <see cref="Name"/> is how to write it back to sfx.</summary>
internal abstract record Entity
{
    public abstract string Name { get; }

    public sealed record Cluster : Entity
    {
        public override string Name => "cluster";
    }

    public sealed record Node(string NodeName) : Entity
    {
        public override string Name => $"node/{NodeName}";
    }

    public sealed record Application(string Id, string AppName) : Entity
    {
        public override string Name => AppName;
    }

    public sealed record Service(string ApplicationId, string Id, string ServiceName) : Entity
    {
        public override string Name => ServiceName;
    }

    public sealed record Partition(string ApplicationId, string ServiceId, string ServiceName, Guid Id) : Entity
    {
        public override string Name => $"partition/{Id}";
    }

    public sealed record Replica(Partition Owner, string Id) : Entity
    {
        public override string Name => $"replica/{Owner.Id}/{Id}";
    }

    public sealed record AppType(string TypeName, string? Version) : Entity
    {
        public override string Name => Version is null ? $"type/{TypeName}" : $"type/{TypeName}@{Version}";
    }

    public sealed record StorePath(string Path) : Entity
    {
        public override string Name => $"store:/{Path.Replace('\\', '/')}";
    }
}

/// <summary>Finds what a name refers to on the cluster. Not there: exit 1; more than one match: a usage error listing them.</summary>
internal sealed class Resolver(UiSession s)
{
    public async Task<Entity> ResolveAsync(EntityRef reference, CancellationToken ct) => reference switch
    {
        EntityRef.Cluster => new Entity.Cluster(),
        EntityRef.Node n => new Entity.Node(await NodeAsync(n.Name, ct)),
        EntityRef.Named n => await NamedAsync(n, ct),
        EntityRef.Partition p => await PartitionAsync(p.Id, ct),
        EntityRef.Replica r => new Entity.Replica(await PartitionAsync(r.PartitionId, ct), r.Id),
        EntityRef.AppType t => await TypeAsync(t, ct),
        EntityRef.Store p => new Entity.StorePath(p.Path),
        _ => throw new ArgumentOutOfRangeException(nameof(reference)),
    };

    private async Task<string> NodeAsync(string name, CancellationToken ct)
    {
        var nodes = await s.Queries.GetNodesAsync(ct);
        if (nodes.FirstOrDefault(n => n.Name == name) is { } exact)
        {
            return exact.Name;
        }
        var near = nodes.Where(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return near switch
        {
            [var one] => one.Name,
            [] => throw CliFailure.NotFound($"There's no node {name} on this cluster."),
            _ => throw Ambiguous(name, near.Select(n => $"node/{n.Name}")),
        };
    }

    private async Task<Entity> NamedAsync(EntityRef.Named named, CancellationToken ct)
    {
        var id = ClusterInspector.IdOf(named.Name);
        if (await ProbeAsync(() => s.Queries.GetApplicationAsync(id, ct)) is { } app)
        {
            return new Entity.Application(app.Id, app.Name);
        }
        if (await ServiceAsync(named.Name, id, ct) is { } service)
        {
            return service;
        }
        if (named.Short is not { } typed)
        {
            throw CliFailure.NotFound($"There's no application or service {named.Name}.");
        }

        // A short name may leave out the application, or be cased differently.
        var index = await s.GetSearchIndexAsync(ct);
        var candidates = index.Where(h => h.Kind is "application" or "service").ToList();
        var matches = candidates.Where(h => h.Title.Equals(named.Name, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } cased
            ? cased
            : candidates.Where(h => h.Title.EndsWith("/" + typed, StringComparison.OrdinalIgnoreCase)).ToList();
        switch (matches)
        {
            case [{ Service: { } svc, Application: { } owner }]:
                return new Entity.Service(owner.Id, svc.Id, svc.Name);
            case [{ Application: { } one }]:
                return new Entity.Application(one.Id, one.Name);
            case []:
                var node = index.Any(h => h.Kind == "node" && h.Title == typed) ? $" For the node, write node/{typed}." : "";
                throw CliFailure.NotFound($"There's no application or service {named.Name}.{node}");
            default:
                throw Ambiguous(typed, matches.Select(h => h.Title));
        }
    }

    private async Task<Entity.Service?> ServiceAsync(string name, string serviceId, CancellationToken ct)
    {
        var owner = await ProbeAsync(() => s.Queries.Client.Services.GetApplicationNameInfoAsync(serviceId, cancellationToken: ct));
        var appId = owner?.Id is { Length: > 0 } known ? known
            : name.StartsWith(ClusterQueries.SystemApplicationName + "/", StringComparison.Ordinal) ? ClusterQueries.SystemApplicationId
            : null;
        if (appId is null)
        {
            return null;
        }
        var row = await ProbeAsync(() => s.Queries.GetServiceAsync(appId, serviceId, ct));
        return row is null ? null : new Entity.Service(appId, row.Id, row.Name);
    }

    private async Task<Entity.Partition> PartitionAsync(Guid id, CancellationToken ct)
    {
        var hit = await s.Overview.FindPartitionAsync(id, ct);
        return hit is { Service: { } service }
            ? new Entity.Partition(hit.Application?.Id ?? ClusterQueries.SystemApplicationId, service.Id, service.Name, id)
            : throw CliFailure.NotFound($"There's no partition {id} on this cluster.");
    }

    private async Task<Entity.AppType> TypeAsync(EntityRef.AppType type, CancellationToken ct)
    {
        var types = await s.Queries.GetApplicationTypesAsync(ct);
        var names = types.Select(t => t.Name).Distinct().ToList();
        var name = names.FirstOrDefault(n => n == type.Name)
            ?? names.Where(n => n.Equals(type.Name, StringComparison.OrdinalIgnoreCase)).ToList() switch
            {
                [var one] => one,
                [] => throw CliFailure.NotFound($"There's no application type {type.Name} on this cluster."),
                var several => throw Ambiguous(type.Name, several.Select(n => $"type/{n}")),
            };
        if (type.Version is { } version && !types.Any(t => t.Name == name && t.Version == version))
        {
            throw CliFailure.NotFound($"{name} has no version {version} provisioned.");
        }
        return new Entity.AppType(name, type.Version);
    }

    /// <summary>Null when the cluster says there's no such thing, or that the name can't be one.</summary>
    private static async Task<T?> ProbeAsync<T>(Func<Task<T?>> query) where T : class
    {
        try
        {
            return await query();
        }
        catch (ServiceFabricException ex) when (ClusterQueries.IsGone(ex) || ex.ErrorCode.ToString().Contains("INVALID", StringComparison.Ordinal))
        {
            return null;
        }
    }

    private static CliFailure Ambiguous(string typed, IEnumerable<string> matches) =>
        CliFailure.Usage($"'{typed}' could be any of {string.Join(", ", matches.Order(StringComparer.OrdinalIgnoreCase))}; give the full name.");
}
