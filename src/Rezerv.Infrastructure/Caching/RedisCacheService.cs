using System.Text.Json;
using Microsoft.Extensions.Logging;
using Rezerv.Application.Abstractions;
using StackExchange.Redis;

namespace Rezerv.Infrastructure.Caching;

/// <summary>Redis-backed cache. Every Redis failure is logged and degraded to a cache miss / no-op.</summary>
public sealed class RedisCacheService(IConnectionMultiplexer redis, ILogger<RedisCacheService> logger) : ICacheService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IDatabase Db => redis.GetDatabase();

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        try
        {
            var value = await Db.StringGetAsync(key);
            return value.IsNullOrEmpty ? default : JsonSerializer.Deserialize<T>(value.ToString(), Json);
        }
        catch (Exception ex) when (ex is RedisException or JsonException or TimeoutException)
        {
            logger.LogWarning(ex, "Redis GET {Key} failed; treating as miss", key);
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default)
    {
        try
        {
            await Db.StringSetAsync(key, JsonSerializer.Serialize(value, Json), ttl);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Redis SET {Key} failed", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await Db.KeyDeleteAsync(key);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Redis DEL {Key} failed", key);
        }
    }

    public async Task<IAsyncDisposable?> AcquireLockAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        var owner = Guid.NewGuid().ToString("N");
        try
        {
            // SET key owner NX PX ttl
            var taken = await Db.StringSetAsync(key, owner, ttl, When.NotExists);
            return taken ? new RedisLock(Db, key, owner, logger) : null;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Without Redis we can't coordinate; let the caller proceed (DB unique constraints still apply).
            logger.LogWarning(ex, "Redis lock {Key} unavailable; continuing without it", key);
            return NoopLock.Instance;
        }
    }

    private sealed class RedisLock(IDatabase db, string key, string owner, ILogger logger) : IAsyncDisposable
    {
        // Compare-and-delete: only the owner may release, so an expired-then-retaken lock is never freed by the old holder.
        private const string ReleaseScript = """
            if redis.call('GET', KEYS[1]) == ARGV[1] then
                return redis.call('DEL', KEYS[1])
            end
            return 0
            """;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await db.ScriptEvaluateAsync(ReleaseScript, [key], [owner]);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                logger.LogWarning(ex, "Redis lock {Key} release failed; it will expire", key);
            }
        }
    }

    private sealed class NoopLock : IAsyncDisposable
    {
        public static readonly NoopLock Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
