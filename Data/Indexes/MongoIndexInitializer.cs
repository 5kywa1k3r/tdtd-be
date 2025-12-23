namespace tdtd_be.Data.Indexes
{
    using MongoDB.Bson;
    using MongoDB.Driver;
    using tdtd_be.Models;

    public static class MongoIndexInitializer
    {
        private static async Task DropIndexIfExistsAsync<T>(
            IMongoCollection<T> col,
            string indexName,
            CancellationToken ct)
        {
            using var cursor = await col.Indexes.ListAsync(ct);
            var list = await cursor.ToListAsync(ct);

            var found = list.FirstOrDefault(i => i["name"] == indexName);
            if (found != null)
                await col.Indexes.DropOneAsync(indexName, ct);
        }

        public static async Task EnsureAsync(IMongoDatabase db, CancellationToken ct = default)
        {
            // ================= USERS =================
            var users = db.GetCollection<AppUser>("users");

            await DropIndexIfExistsAsync(users, "ux_users_username", ct);
            await users.Indexes.CreateOneAsync(
                new CreateIndexModel<AppUser>(
                    Builders<AppUser>.IndexKeys.Ascending(x => x.Username),
                    new CreateIndexOptions { Unique = true, Name = "ux_users_username" }
                ),
                cancellationToken: ct
            );

            // ================= REFRESH TOKENS =================
            var rts = db.GetCollection<RefreshTokenDoc>("refresh_tokens");

            // Non-TTL indexes
            await DropIndexIfExistsAsync(rts, "ix_refresh_user", ct);
            await rts.Indexes.CreateOneAsync(
                new CreateIndexModel<RefreshTokenDoc>(
                    Builders<RefreshTokenDoc>.IndexKeys.Ascending(x => x.UserId),
                    new CreateIndexOptions { Name = "ix_refresh_user" }
                ),
                cancellationToken: ct
            );

            // Nếu có index theo TokenHash thì giữ (khuyên dùng)
            // await DropIndexIfExistsAsync(rts, "ix_refresh_tokenHash", ct);
            // await rts.Indexes.CreateOneAsync(
            //     new CreateIndexModel<RefreshTokenDoc>(
            //         Builders<RefreshTokenDoc>.IndexKeys.Ascending(x => x.TokenHash),
            //         new CreateIndexOptions { Name = "ix_refresh_tokenHash" }
            //     ),
            //     cancellationToken: ct
            // );

            // TTL index on expiresAt (TTL index đã đủ để query theo expiresAt, không tạo ix_refresh_expiresAt nữa)
            await EnsureTtlExpiresAtAsync(rts, ct);
        }

        private static async Task EnsureTtlExpiresAtAsync(
            IMongoCollection<RefreshTokenDoc> rts,
            CancellationToken ct)
        {
            const string ttlIndexName = "ttl_refresh_expiresAt";
            const string desiredField = "expiresAt"; // phải khớp với BsonElement("expiresAt")

            using var cursor = await rts.Indexes.ListAsync(ct);
            var existing = await cursor.ToListAsync(ct);

            // Drop mọi index có key chứa desiredField mà KHÔNG phải TTL đúng chuẩn
            foreach (var idx in existing)
            {
                var name = idx["name"].AsString;

                if (!idx.TryGetValue("key", out var keyVal) || !keyVal.IsBsonDocument)
                    continue;

                var keyDoc = keyVal.AsBsonDocument;

                // Chỉ xử lý index 1-field trên expiresAt
                if (keyDoc.ElementCount == 1 && keyDoc.Names.Contains(desiredField))
                {
                    var hasTtl = idx.Contains("expireAfterSeconds");
                    var expireSeconds = hasTtl ? idx["expireAfterSeconds"].ToInt32() : -1;

                    var isDesiredTtl = name == ttlIndexName && hasTtl && expireSeconds == 0;

                    // Nếu không phải TTL mong muốn -> drop (bao gồm ix_refresh_expiresAt hiện tại)
                    if (!isDesiredTtl)
                        await rts.Indexes.DropOneAsync(name, ct);
                }
            }

            // Create TTL index (ExpireAfter=0 => xóa khi tới expiresAt)
            await rts.Indexes.CreateOneAsync(
                new CreateIndexModel<RefreshTokenDoc>(
                    Builders<RefreshTokenDoc>.IndexKeys.Ascending(x => x.ExpiresAt),
                    new CreateIndexOptions { Name = ttlIndexName, ExpireAfter = TimeSpan.Zero }
                ),
                cancellationToken: ct
            );
        }
    }
}
