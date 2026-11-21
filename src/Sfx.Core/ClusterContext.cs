using Sfx.Core.Auth;

namespace Sfx.Core;

/// <summary>A saved (or ad-hoc) way to reach a cluster: where it is and how to authenticate.</summary>
/// <param name="Auth">Auth method names, applied in order; empty means none.</param>
/// <param name="Options">Options for those auth methods, keyed by option name.</param>
/// <param name="ReadOnly">Hide every action that changes the cluster.</param>
/// <param name="Power">Start with the riskiest actions (recover, remove node state, delete…) unlocked.</param>
/// <param name="Folder">Where a saved context is filed, e.g. <c>prod/europe</c>; null at the top level.</param>
/// <param name="View">The view it opens with, by name; null for none.</param>
public sealed record ClusterContext(
    string Name,
    string Endpoint,
    IReadOnlyList<string> Auth,
    AuthOptions Options,
    bool ReadOnly = false,
    bool Power = false,
    string? Folder = null,
    string? View = null);

public sealed record ClusterSession(ClusterContext Context, Uri Endpoint, ClusterQueries Queries);

/// <summary>Turns a <see cref="ClusterContext"/> into a connected session using the registered auth methods.</summary>
public sealed class ClusterConnector
{
    private readonly Dictionary<string, IClusterAuthProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="providers">Later providers replace earlier ones with the same name.</param>
    public ClusterConnector(IEnumerable<IClusterAuthProvider> providers)
    {
        foreach (var provider in providers)
        {
            _providers[provider.Name] = provider;
        }
    }

    public IReadOnlyCollection<IClusterAuthProvider> Providers => _providers.Values;

    /// <summary>Checks the auth methods exist and every option belongs to one of them, without loading credentials.</summary>
    public IReadOnlyList<IClusterAuthProvider> Resolve(ClusterContext context)
    {
        var names = context.Auth.Count > 0 ? context.Auth : ["none"];
        var selected = names
            .Select(name => _providers.TryGetValue(name, out var provider)
                ? provider
                : throw new AuthConfigurationException($"Unknown auth method '{name}'. Available: {string.Join(", ", _providers.Keys)}."))
            .ToList();

        var known = selected.SelectMany(p => p.Options).Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = context.Options.Keys.FirstOrDefault(key => !known.Contains(key));
        return unknown is null
            ? selected
            : throw new AuthConfigurationException($"--{unknown} isn't an option of auth '{string.Join(",", names)}'.");
    }

    public async Task<ClusterSession> ConnectAsync(ClusterContext context, Func<string, Task> notify, CancellationToken cancellationToken)
    {
        var providers = Resolve(context);
        var endpoint = ClusterEndpoint.Parse(context.Endpoint, providers.Any(p => p.RequiresTls));
        var auth = new AuthContext(context.Options, notify);
        var connection = new ClusterConnection(endpoint, providers.Select(p => p.Create(auth)).ToList());

        var queries = new ClusterQueries(await ClusterClientFactory.CreateAsync(connection, cancellationToken));
        // One real call, so auth and network errors surface here rather than on the first screen.
        await queries.GetNodesAsync(cancellationToken);
        return new ClusterSession(context, endpoint, queries);
    }
}
