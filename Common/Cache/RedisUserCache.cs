using StackExchange.Redis;
using System.Text.Json;
using tdtd_be.DTOs.Auth;

namespace tdtd_be.Common.Cache
{
    public sealed class RedisUserCache
    {
        private readonly IDatabase _db;
        private readonly IConfiguration _cfg;

        public RedisUserCache(IConnectionMultiplexer mux, IConfiguration cfg)
        {
            _db = mux.GetDatabase();
            _cfg = cfg;
        }

        private TimeSpan MeTtl => TimeSpan.FromMinutes(int.TryParse(_cfg["Redis:MeTtlMinutes"], out var m) ? m : 720);

        private static string MeKey(string userId) => $"me:{userId}";
        private static string TvKey(string userId) => $"tv:{userId}";

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public async Task<MeResponse?> GetMeAsync(string userId)
        {
            var val = await _db.StringGetAsync(MeKey(userId));
            if (val.IsNullOrEmpty) return null;
            return JsonSerializer.Deserialize<MeResponse>(val!, JsonOpts);
        }

        public Task SetMeAsync(MeResponse me)
            => _db.StringSetAsync(MeKey(me.Id), JsonSerializer.Serialize(me, JsonOpts), expiry: MeTtl);

        public Task DeleteMeAsync(string userId) => _db.KeyDeleteAsync(MeKey(userId));

        public async Task<long> GetTokenVersionAsync(string userId)
        {
            var val = await _db.StringGetAsync(TvKey(userId));
            if (val.IsNullOrEmpty) return 0;
            return (long)val!;
        }

        public Task EnsureTokenVersionAsync(string userId)
            => _db.StringSetAsync(TvKey(userId), 0, when: When.NotExists);

        public Task<long> BumpTokenVersionAsync(string userId)
            => _db.StringIncrementAsync(TvKey(userId));
    }
}
