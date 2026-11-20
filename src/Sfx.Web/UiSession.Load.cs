using Sfx.Core;

namespace Sfx.Web;

public sealed partial class UiSession
{
    public RepairQueries Repairs { get; } = new(session.Queries);

    private Cached<LoadDto>? _load;
    private Cached<LoadDto>? _nodeLoad;
    private Cached<NodeCapacities>? _capacities;
    private Cached<IReadOnlyDictionary<string, string>>? _nodeNames;

    /// <summary>Cluster load per metric, from one call, shared by every tab for 15 seconds.</summary>
    public Task<LoadDto> GetLoadAsync(CancellationToken cancellationToken) =>
        LazyInitializer.EnsureInitialized(ref _load, () => new Cached<LoadDto>(
                TimeSpan.FromSeconds(15), ct => ApiLoad.LoadAsync(this, includeNodes: false, ct)))
            .GetAsync(cancellationToken);

    /// <summary>Cluster load with each node's; one walk of the nodes serves every tab for a minute.</summary>
    public Task<LoadDto> GetNodeLoadAsync(CancellationToken cancellationToken) =>
        LazyInitializer.EnsureInitialized(ref _nodeLoad, () => new Cached<LoadDto>(
                TimeSpan.FromSeconds(60), ct => ApiLoad.LoadAsync(this, includeNodes: true, ct)))
            .GetAsync(cancellationToken);

    /// <summary>
    /// The capacities the cluster manifest declares, which tell a capacity of 0 (nothing may be placed) from an unset one.
    /// Asked every 5 minutes; when the manifest can't be read, no capacity of 0 counts as a limit.
    /// </summary>
    public Task<NodeCapacities> GetDeclaredCapacitiesAsync(CancellationToken cancellationToken) =>
        LazyInitializer.EnsureInitialized(ref _capacities, () => new Cached<NodeCapacities>(TimeSpan.FromMinutes(5), async ct =>
            {
                try
                {
                    return NodeCapacities.Parse(await Inspector.GetClusterManifestAsync(ct));
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    return NodeCapacities.None;
                }
            }))
            .GetAsync(cancellationToken);

    /// <summary>
    /// Node names by id, for what the cluster reports by id only. A node keeps its id, so the list is asked at most every
    /// 5 minutes, and sooner when an id isn't in it (a node added since). When it can't be had, ids aren't resolved.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, string>> GetNodeNamesAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0)
        {
            return new Dictionary<string, string>();
        }
        var cache = LazyInitializer.EnsureInitialized(ref _nodeNames, () => new Cached<IReadOnlyDictionary<string, string>>(
            TimeSpan.FromMinutes(5), async ct => ApiLoad.NamesById(await Queries.GetNodesAsync(ct))));
        try
        {
            var names = await cache.GetAsync(cancellationToken);
            if (wanted.Any(id => !names.ContainsKey(id)))
            {
                cache.Invalidate();
            }
            return names;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new Dictionary<string, string>();
        }
    }
}
