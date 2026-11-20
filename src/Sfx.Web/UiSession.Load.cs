using Sfx.Core;

namespace Sfx.Web;

public sealed partial class UiSession
{
    public RepairQueries Repairs { get; } = new(session.Queries);

    private readonly Cached<LoadDto> _load = new(TimeSpan.FromSeconds(15), ct => ApiLoad.LoadAsync(session.Queries, ct));

    /// <summary>Cluster and node load; one walk of the nodes serves every tab for 15 seconds.</summary>
    public Task<LoadDto> GetLoadAsync(CancellationToken cancellationToken) => _load.GetAsync(cancellationToken);
}
