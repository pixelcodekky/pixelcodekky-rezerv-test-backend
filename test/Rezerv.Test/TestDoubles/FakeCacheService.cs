using System.Collections.Concurrent;
using Rezerv.Application.Abstractions;

namespace Rezerv.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="ICacheService"/> with Redis lock semantics (SET NX, released only by its owner).
/// <see cref="Unavailable"/> simulates Redis being down, which RedisCacheService degrades to misses and a no-op lock.
/// </summary>
public sealed class FakeCacheService : ICacheService
{
    private readonly ConcurrentDictionary<string, object?> _values = new();
    private readonly ConcurrentDictionary<string, string> _locks = new();

    public bool Unavailable { get; set; }

    public int LockAttempts;

    public bool Contains(string key) => _values.ContainsKey(key);

    public bool IsLocked(string key) => _locks.ContainsKey(key);

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) =>
        Task.FromResult(!Unavailable && _values.TryGetValue(key, out var v) && v is T t ? t : default);

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default)
    {
        if (!Unavailable)
            _values[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable?> AcquireLockAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        Interlocked.Increment(ref LockAttempts);
        if (Unavailable)
            return Task.FromResult<IAsyncDisposable?>(new Lease(null, key, ""));

        var owner = Guid.NewGuid().ToString("N");
        return Task.FromResult<IAsyncDisposable?>(_locks.TryAdd(key, owner) ? new Lease(this, key, owner) : null);
    }

    private sealed class Lease(FakeCacheService? cache, string key, string owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            // Compare-and-delete.
            cache?._locks.TryRemove(new KeyValuePair<string, string>(key, owner));
            return ValueTask.CompletedTask;
        }
    }
}
