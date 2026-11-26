using Sfx.Core;

namespace Sfx.Web;

public sealed partial class UiSession
{
    private static readonly TimeSpan FolderSizeLifetime = TimeSpan.FromSeconds(60);

    public ImageStore ImageStore => _imageStore ??= new(Queries.Client);

    private ImageStore? _imageStore;

    public ServiceSettings ServiceSettings => _serviceSettings ??= new(Queries.Client);

    private ServiceSettings? _serviceSettings;

    private Cached<IReadOnlyList<ImageStorePackage>>? _storePackages;

    /// <summary>Top-level folders holding a package; cached because it costs a call per folder.</summary>
    internal Task<IReadOnlyList<ImageStorePackage>> GetStorePackagesAsync(CancellationToken cancellationToken) =>
        LazyInitializer.EnsureInitialized(ref _storePackages, () => new Cached<IReadOnlyList<ImageStorePackage>>(
            TimeSpan.FromSeconds(30),
            ct => ImageStore.GetPackagesAsync(ct)))
            .GetAsync(cancellationToken);

    private readonly Lock _sizesLock = new();
    private readonly Dictionary<string, (DateTime At, Task<long?> Load)> _folderSizes = [];

    /// <summary>Null when the cluster can't say; a missing folder is a <see cref="NotFoundException"/> and isn't kept.</summary>
    internal Task<long?> GetFolderSizeAsync(string path, CancellationToken cancellationToken)
    {
        var key = ImageStore.Normalize(path);
        var now = DateTime.UtcNow;
        Task<long?> load;
        lock (_sizesLock)
        {
            foreach (var stale in _folderSizes.Where(kv => kv.Value.Load.IsCompleted && now - kv.Value.At >= FolderSizeLifetime).Select(kv => kv.Key).ToList())
            {
                _folderSizes.Remove(stale);
            }
            if (!_folderSizes.TryGetValue(key, out var entry) || entry.Load.IsFaulted || entry.Load.IsCanceled)
            {
                entry = (now, Task.Run(async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    try
                    {
                        return (long?)await ImageStore.GetFolderSizeAsync(key, timeout.Token);
                    }
                    catch (Exception ex) when (ex is not NotFoundException && !timeout.IsCancellationRequested)
                    {
                        return null;
                    }
                }));
                _folderSizes[key] = entry;
            }
            load = entry.Load;
        }
        return load.WaitAsync(cancellationToken);
    }

    /// <summary>A size loaded within the last minute, without asking the cluster; null otherwise.</summary>
    internal long? KnownFolderSize(string path)
    {
        lock (_sizesLock)
        {
            return _folderSizes.TryGetValue(ImageStore.Normalize(path), out var entry)
                && entry.Load.IsCompletedSuccessfully
                && DateTime.UtcNow - entry.At < FolderSizeLifetime
                    ? entry.Load.Result
                    : null;
        }
    }

    /// <summary>Called after a change so packages and sizes aren't served from before it.</summary>
    private void ForgetStore()
    {
        _storePackages?.Invalidate();
        lock (_sizesLock)
        {
            _folderSizes.Clear();
        }
    }

    private Task<bool?>? _imageStoreBrowsable;

    /// <summary>
    /// False only when listing the root is refused because it isn't the native store; any other failure counts as yes
    /// and is asked again next time.
    /// </summary>
    internal async Task<bool> ImageStoreBrowsableAsync(CancellationToken cancellationToken)
    {
        var check = _imageStoreBrowsable ??= CheckImageStoreAsync();
        if (await check.WaitAsync(cancellationToken) is { } known)
        {
            return known;
        }
        _imageStoreBrowsable = null;
        return true;
    }

    private async Task<bool?> CheckImageStoreAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await ImageStore.GetContentAsync(null, timeout.Token);
            return true;
        }
        catch (RefusedException)
        {
            return false;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private StaleCache<IReadOnlyList<SearchHit>>? _searchIndex;

    /// <summary>Every node, application and service to search, refreshed in the background.</summary>
    internal Task<IReadOnlyList<SearchHit>> GetSearchIndexAsync(CancellationToken cancellationToken) =>
        LazyInitializer.EnsureInitialized(ref _searchIndex, () => new StaleCache<IReadOnlyList<SearchHit>>(
            TimeSpan.FromSeconds(30),
            ct => Overview.GetSearchIndexAsync(ct),
            TimeSpan.FromMinutes(2)))
            .GetAsync(cancellationToken);
}
