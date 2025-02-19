﻿namespace YahooQuotesApi;

internal sealed class SerialProducerCache<TKey, TResult> : IDisposable where TKey : notnull
{
    private readonly SemaphoreSlim Semaphore = new(1, 1);
    private readonly List<TKey> Buffer = [];
    private readonly Cache<TKey, TResult> Cache;
    private readonly Func<List<TKey>, CancellationToken, Task<Dictionary<TKey, TResult>>> Produce;

    internal SerialProducerCache(IClock clock, Duration cacheDuration, Func<List<TKey>, CancellationToken, Task<Dictionary<TKey, TResult>>> produce)
    {
        Cache = new Cache<TKey, TResult>(clock, cacheDuration);
        Produce = produce;
    }

    internal async Task<Dictionary<TKey, TResult>> Get(HashSet<TKey> keys, CancellationToken ct)
    {
        if (Cache.TryGetAll(keys, out var results))
            return results;
        
        lock (Buffer)
        {
            Buffer.AddRange(keys);
        }

        await Semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<TKey> items;
            lock (Buffer)
            {
                items = [.. Buffer]; // make copy
                Buffer.Clear();
            }
            if (items.Count != 0)
            {
                results = await Produce(items, ct).ConfigureAwait(false);
                Cache.Add(results);
            }
        }
        finally
        {
            Semaphore.Release();
        }

        return Cache.GetAll(keys);
    }

    public void Dispose() => Semaphore.Dispose();
}
