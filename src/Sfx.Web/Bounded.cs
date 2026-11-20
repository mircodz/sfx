namespace Sfx.Web;

internal static class Bounded
{
    public const int Parallelism = 8;

    /// <summary>Runs <paramref name="run"/> on every item, at most <see cref="Parallelism"/> at a time, keeping their order.</summary>
    public static async Task<IReadOnlyList<TOut>> SelectAsync<TIn, TOut>(IEnumerable<TIn> items, Func<TIn, Task<TOut>> run)
    {
        using var gate = new SemaphoreSlim(Parallelism);
        return await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync();
            try
            {
                return await run(item);
            }
            finally
            {
                gate.Release();
            }
        }));
    }
}
