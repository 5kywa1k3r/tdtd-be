using StackExchange.Redis;
using System.Text.Json;
using tdtd_be.Common.Errors;

namespace tdtd_be.Common.Cache
{
    public sealed class RedisDashboardCache
    {
        private readonly IDatabase? _db;
        private readonly IConfiguration _cfg;

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public RedisDashboardCache(IConnectionMultiplexer? mux, IConfiguration cfg)
        {
            _db = mux?.GetDatabase();
            _cfg = cfg;
        }

        private TimeSpan DefaultTtl =>
            TimeSpan.FromMinutes(int.TryParse(_cfg["Redis:DashboardTtlMinutes"], out var m) && m > 0 ? m : 30);

        private TimeSpan LockTtl =>
            TimeSpan.FromSeconds(int.TryParse(_cfg["Redis:DashboardLockSeconds"], out var s) && s > 0 ? s : 15);

        private int WaitRetryCount =>
            int.TryParse(_cfg["Redis:DashboardWaitRetryCount"], out var n) && n > 0 ? n : 20;

        private int WaitDelayMs =>
            int.TryParse(_cfg["Redis:DashboardWaitDelayMs"], out var n) && n > 0 ? n : 150;

        private static string LockKey(string cacheKey) => $"lock:{cacheKey}";

        public async Task<T?> GetAsync<T>(string cacheKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_db is null) return default;

            var val = await _db.StringGetAsync(cacheKey);
            if (val.IsNullOrEmpty) return default;

            return JsonSerializer.Deserialize<T>(val!, JsonOpts);
        }

        public Task SetAsync<T>(string cacheKey, T value, TimeSpan? ttl = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_db is null) return Task.CompletedTask;

            return _db.StringSetAsync(
                cacheKey,
                JsonSerializer.Serialize(value, JsonOpts),
                expiry: ttl ?? DefaultTtl);
        }

        public Task<bool> TryAcquireLockAsync(string cacheKey, string token)
            => _db is null
                ? Task.FromResult(true)
                : _db.StringSetAsync(LockKey(cacheKey), token, LockTtl, When.NotExists);

        public async Task ReleaseLockAsync(string cacheKey, string token)
        {
            if (_db is null) return;

            const string script = @"
if redis.call('get', KEYS[1]) == ARGV[1]
then
    return redis.call('del', KEYS[1])
else
    return 0
end";

            await _db.ScriptEvaluateAsync(
                script,
                new RedisKey[] { LockKey(cacheKey) },
                new RedisValue[] { token });
        }

        public async Task<T> GetOrCreateAsync<T>(
            string cacheKey,
            Func<CancellationToken, Task<T>> factory,
            CancellationToken ct = default,
            bool forceRefresh = false,
            TimeSpan? ttl = null)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
                throw AppExceptionFactory.BadRequest(AppErrorCode.DASHBOARD_CACHE_KEY_REQUIRED, new { field = "cacheKey" });

            if (factory is null)
                throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, new { field = "factory" });

            if (_db is null)
                return await factory(ct);

            var generationKey = $"{cacheKey}:refresh-generation";
            var lifetimeMs = Math.Max(1, (long)(ttl ?? DefaultTtl).TotalMilliseconds);
            if (forceRefresh)
            {
                // Retire the old value and fence already running writers in one Redis operation.
                await _db.ScriptEvaluateAsync(@"
redis.call('set', KEYS[1], ARGV[1], 'PX', ARGV[2])
return redis.call('del', KEYS[2])",
                    new RedisKey[] { generationKey, cacheKey },
                    new RedisValue[] { Guid.NewGuid().ToString("N"), lifetimeMs });
            }
            var generation = await _db.StringGetAsync(generationKey);
            async Task<T> CreateAndStoreAsync()
            {
                var created = await factory(ct);
                ct.ThrowIfCancellationRequested();
                // An earlier computation must not overwrite a later explicit refresh.
                await _db.ScriptEvaluateAsync(@"
local current = redis.call('get', KEYS[1]) or ''
if current == ARGV[1] then
    return redis.call('set', KEYS[2], ARGV[2], 'PX', ARGV[3])
end
return 0",
                    new RedisKey[] { generationKey, cacheKey },
                    new RedisValue[] { generation.IsNull ? "" : generation, JsonSerializer.Serialize(created, JsonOpts), lifetimeMs });
                return created;
            }

            if (!forceRefresh)
            {
                var cached = await GetAsync<T>(cacheKey, ct);
                if (cached is not null)
                    return cached;
            }

            var lockToken = Guid.NewGuid().ToString("N");
            var hasLock = await TryAcquireLockAsync(cacheKey, lockToken);

            if (hasLock)
            {
                try
                {
                    if (!forceRefresh)
                    {
                        var cachedAgain = await GetAsync<T>(cacheKey, ct);
                        if (cachedAgain is not null)
                            return cachedAgain;
                    }

                    return await CreateAndStoreAsync();
                }
                finally
                {
                    await ReleaseLockAsync(cacheKey, lockToken);
                }
            }

            for (var i = 0; i < WaitRetryCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(WaitDelayMs, ct);

                // A refresh must not return the pre-existing value held behind another writer's lock.
                if (forceRefresh)
                {
                    if (!await TryAcquireLockAsync(cacheKey, lockToken)) continue;
                    try
                    {
                        return await CreateAndStoreAsync();
                    }
                    finally { await ReleaseLockAsync(cacheKey, lockToken); }
                }

                var waited = await GetAsync<T>(cacheKey, ct);
                if (waited is not null)
                    return waited;
            }

            return await CreateAndStoreAsync();
        }
    }
}
