namespace Rezerv.Application.Abstractions;

/// <summary>
/// Distributed cache (Redis). Implementations degrade gracefully: when the cache is unavailable,
/// reads return default and writes are no-ops, so MySQL stays the source of truth.
/// </summary>
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);

    /// <summary>Takes a short-lived distributed lock; dispose to release. Returns null when someone else holds it.</summary>
    Task<IAsyncDisposable?> AcquireLockAsync(string key, TimeSpan ttl, CancellationToken ct = default);
}
