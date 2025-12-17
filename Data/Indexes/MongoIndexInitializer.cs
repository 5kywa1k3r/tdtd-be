//Data/Indexes/MongoIndexInitializer.cs
namespace tdtd_be.Data.Indexes
{
    using MongoDB.Driver;
    using tdtd_be.Models;

    public static class MongoIndexInitializer
    {
        public static async Task EnsureAsync(IMongoDatabase db, CancellationToken ct = default)
        {
            var users = db.GetCollection<AppUser>("users");
            await users.Indexes.CreateOneAsync(
                new CreateIndexModel<AppUser>(
                    Builders<AppUser>.IndexKeys.Ascending(x => x.Username),
                    new CreateIndexOptions
                    {
                        Unique = true,
                        Name = "ux_users_username"
                    }),
                cancellationToken: ct);


            var rts = db.GetCollection<RefreshTokenDoc>("refresh_tokens");
            await rts.Indexes.CreateManyAsync(new[]
            {
            new CreateIndexModel<RefreshTokenDoc>(
                Builders<RefreshTokenDoc>.IndexKeys.Ascending(x => x.UserId),
                new CreateIndexOptions { Name = "ix_refresh_user" }),

            new CreateIndexModel<RefreshTokenDoc>(
                Builders<RefreshTokenDoc>.IndexKeys.Ascending(x => x.ExpiresAt),
                new CreateIndexOptions { Name = "ix_refresh_expiresAt" })
        }, ct);
            var indexes = await rts.Indexes.ListAsync(cancellationToken: ct);
            var existing = await indexes.ToListAsync(ct);

            var expiresIndex = existing.FirstOrDefault(i =>
                i["key"].AsBsonDocument.Contains("ExpiresAt"));

            if (expiresIndex is not null)
            {
                var hasTtl = expiresIndex.Contains("expireAfterSeconds");

                if (!hasTtl)
                {
                    var name = expiresIndex["name"].AsString;
                    await rts.Indexes.DropOneAsync(name, ct);
                }
            }

            await rts.Indexes.CreateOneAsync(
                new CreateIndexModel<RefreshTokenDoc>(
                    Builders<RefreshTokenDoc>.IndexKeys.Ascending(x => x.ExpiresAt),
                    new CreateIndexOptions
                    {
                        ExpireAfter = TimeSpan.Zero,
                        Name = "ttl_refresh_expiresAt"
                    }),
                cancellationToken: ct);

            // TTL index: tự xoá token hết hạn
            await rts.Indexes.CreateOneAsync(
                new CreateIndexModel<RefreshTokenDoc>(
                    Builders<RefreshTokenDoc>.IndexKeys.Ascending(x => x.ExpiresAt),
                    new CreateIndexOptions
                    {
                        ExpireAfter = TimeSpan.Zero,
                        Name = "ttl_refresh_expiresAt"
                    }),
                cancellationToken: ct);

        }
    }
}
