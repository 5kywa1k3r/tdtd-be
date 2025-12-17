//Caching/RedisAppCache.cs
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
namespace tdtd_be.Caching
{
    public sealed class RedisAppCache : IAppCache
    {
        private readonly IDistributedCache _cache;
        private static readonly JsonSerializerOptions JsonOpt = new(JsonSerializerDefaults.Web);

        public RedisAppCache(IDistributedCache cache) => _cache = cache;

        public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            var json = await _cache.GetStringAsync(key, ct);
            return json is null ? default : JsonSerializer.Deserialize<T>(json, JsonOpt);
        }

        public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default)
        {
            var json = JsonSerializer.Serialize(value, JsonOpt);
            var opt = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl };
            return _cache.SetStringAsync(key, json, opt, ct);
        }

        public Task RemoveAsync(string key, CancellationToken ct = default)
            => _cache.RemoveAsync(key, ct);
    }
}
