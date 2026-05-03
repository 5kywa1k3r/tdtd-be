using StackExchange.Redis;
using System.Text.Json;

namespace tdtd_be.Common.Cache
{
    public sealed class RedisDashboardCache
    {
        private readonly IDatabase _db;
        private readonly IConfiguration _cfg;

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public RedisDashboardCache(IConnectionMultiplexer mux, IConfiguration cfg)
        {
            _db = mux.GetDatabase();
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

            var val = await _db.StringGetAsync(cacheKey);
            if (val.IsNullOrEmpty) return default;

            return JsonSerializer.Deserialize<T>(val!, JsonOpts);
        }

        public Task SetAsync<T>(string cacheKey, T value, TimeSpan? ttl = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            return _db.StringSetAsync(
                cacheKey,
                JsonSerializer.Serialize(value, JsonOpts),
                expiry: ttl ?? DefaultTtl);
        }

        public Task<bool> TryAcquireLockAsync(string cacheKey, string token)
            => _db.StringSetAsync(LockKey(cacheKey), token, LockTtl, When.NotExists);

        public async Task ReleaseLockAsync(string cacheKey, string token)
        {
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
                throw new ArgumentException("cacheKey không được trống.", nameof(cacheKey));

            ArgumentNullException.ThrowIfNull(factory);

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

                    var created = await factory(ct);
                    await SetAsync(cacheKey, created, ttl, ct);
                    return created;
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

                var waited = await GetAsync<T>(cacheKey, ct);
                if (waited is not null)
                    return waited;
            }

            return await factory(ct);
        }
    }
}
