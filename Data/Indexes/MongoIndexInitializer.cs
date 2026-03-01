namespace tdtd_be.Data.Indexes
{
    using MongoDB.Bson;
    using MongoDB.Driver;
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using tdtd_be.Data.Infrastructure;
    using tdtd_be.Models;

    public static class MongoIndexInitializer
    {
        public static async Task EnsureAsync(IMongoDatabase db, MongoOptions opt, CancellationToken ct = default)
        {
            // USERS
            var users = db.GetCollection<AppUser>(opt.UserCollection);
            await EnsureUsersAsync(users, ct);

            // REFRESH TOKENS
            var rts = db.GetCollection<RefreshTokenDoc>(opt.RefreshTokenCollection);
            await EnsureRefreshTokensAsync(rts, ct);

            // UNITS
            var units = db.GetCollection<Unit>(opt.UnitCollection);
            await EnsureUnitsAsync(units, ct);

            // UNIT TYPES
            var unitTypes = db.GetCollection<UnitType>(opt.UnitTypeCollection);
            await EnsureUnitTypesAsync(unitTypes, ct);

            // UNIT HISTORIES
            var unitHistories = db.GetCollection<UnitVersionHistory>(opt.UnitHistoryCollection);
            await EnsureUnitHistoriesAsync(unitHistories, ct);

            // files
            var files = db.GetCollection<FileDoc>(opt.FileDocCollection);
            await EnsureFilesAsync(files, ct);
        }

        // ================= USERS =================
        private static async Task EnsureUsersAsync(IMongoCollection<AppUser> col, CancellationToken ct)
        {
            // precheck unique among active
            await PrecheckDuplicateActiveAsync(col, field: "username", ct);

            // partition index
            await EnsureBySpecAsync(col, new IndexSpec("ix_users_isDeleted", new BsonDocument("isDeleted", 1)), ct);

            // partial unique username (active)
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_users_username_active",
                key: new BsonDocument("username", 1),
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            // common query
            await EnsureBySpecAsync(col, new IndexSpec("ix_users_unitId", new BsonDocument("unitId", 1)), ct);
        }

        // ================= REFRESH TOKENS =================
        private static async Task EnsureRefreshTokensAsync(IMongoCollection<RefreshTokenDoc> col, CancellationToken ct)
        {
            await EnsureBySpecAsync(col, new IndexSpec("ix_refresh_userId", new BsonDocument("userId", 1)), ct);

            // TTL expiresAt
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ttl_refresh_expiresAt",
                key: new BsonDocument("expiresAt", 1),
                expireAfterSeconds: 0
            ), ct);
        }

        // ================= UNITS =================
        private static async Task EnsureUnitsAsync(IMongoCollection<Unit> col, CancellationToken ct)
        {
            await PrecheckDuplicateActiveAsync(col, field: "code", ct);

            await EnsureBySpecAsync(col, new IndexSpec("ix_units_isDeleted", new BsonDocument("isDeleted", 1)), ct);

            await EnsureBySpecAsync(col, new IndexSpec("ix_units_parentUnitId", new BsonDocument("parentUnitId", 1)), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_units_code_active",
                key: new BsonDocument("code", 1),
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);
        }

        // ================= UNIT TYPES =================
        private static async Task EnsureUnitTypesAsync(IMongoCollection<UnitType> col, CancellationToken ct)
        {
            await PrecheckDuplicateActiveAsync(col, field: "code", ct);

            await EnsureBySpecAsync(col, new IndexSpec("ix_unitTypes_isDeleted", new BsonDocument("isDeleted", 1)), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_unitTypes_code_active",
                key: new BsonDocument("code", 1),
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);
        }

        // ================= UNIT HISTORIES =================
        private static async Task EnsureUnitHistoriesAsync(IMongoCollection<UnitVersionHistory> col, CancellationToken ct)
        {
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_unitHist_unitId_versionDesc",
                key: new BsonDocument { { "unitId", 1 }, { "version", -1 } }
            ), ct);
        }

        // ================= FILES =================
        private static async Task EnsureFilesAsync(IMongoCollection<FileDoc> col, CancellationToken ct = default)
        {
            var idx = new List<CreateIndexModel<FileDoc>>
            {
                new(
                    Builders<FileDoc>.IndexKeys.Ascending(x => x.CreatedByUserId).Descending(x => x.CreatedAtUtc),
                    new CreateIndexOptions { Name = "ix_files_owner_createdAt" }),

                new(
                    Builders<FileDoc>.IndexKeys.Ascending(x => x.Bucket).Ascending(x => x.ObjectKey),
                    new CreateIndexOptions { Name = "ux_files_bucket_objectKey", Unique = true }),

                new(
                    Builders<FileDoc>.IndexKeys.Ascending(x => x.UploadId).Ascending(x => x.CreatedByUserId),
                    new CreateIndexOptions { Name = "ix_files_upload_owner" }),

                new(
                    Builders<FileDoc>.IndexKeys.Ascending(x => x.IsDeleted),
                    new CreateIndexOptions { Name = "ix_files_isDeleted" })
            };

            await col.Indexes.CreateManyAsync(idx, ct);
        }

        // ---------- Precheck duplicates (active only) ----------
        private static async Task PrecheckDuplicateActiveAsync<T>(
            IMongoCollection<T> col,
            string field,
            CancellationToken ct)
        {
            var pipeline = new[]
            {
                new BsonDocument("$match", new BsonDocument
                {
                    { "isDeleted", false },
                    { field, new BsonDocument("$exists", true) }
                }),
                new BsonDocument("$group", new BsonDocument
                {
                    { "_id", "$" + field },
                    { "c", new BsonDocument("$sum", 1) }
                }),
                new BsonDocument("$match", new BsonDocument("c", new BsonDocument("$gt", 1))),
                new BsonDocument("$limit", 10)
            };

            var dup = await col.Aggregate<BsonDocument>(pipeline).ToListAsync(ct);
            if (dup.Count > 0)
            {
                var sample = string.Join(", ", dup.Select(x => $"{field}={x["_id"]} (count={x["c"]})"));
                throw new InvalidOperationException(
                    $"Duplicate active key detected in {col.CollectionNamespace.CollectionName}: {sample}. " +
                    $"Fix data before creating unique index on {field} with isDeleted=false.");
            }
        }

        // ---------- Ensure by spec (Migrate style) ----------
        private static async Task EnsureBySpecAsync<T>(
            IMongoCollection<T> col,
            IndexSpec desired,
            CancellationToken ct)
        {
            // 1) drop conflicts by same key pattern but different name (normalize naming)
            await DropConflictsByKeyAsync(col, desired, ct);

            // 2) if same name exists but different spec => drop it
            var docs = await ListIndexDocsAsync(col, ct);
            var current = docs.FirstOrDefault(d => d["name"].AsString == desired.Name);
            if (current != null && !IsSameSpec(current, desired))
                await col.Indexes.DropOneAsync(desired.Name, ct);

            // 3) create (idempotent-ish; if exists same spec Mongo will just keep)
            await CreateIndexByCommandAsync(col, desired, ct);
        }

        private static async Task DropConflictsByKeyAsync<T>(
            IMongoCollection<T> col,
            IndexSpec desired,
            CancellationToken ct)
        {
            var docs = await ListIndexDocsAsync(col, ct);

            foreach (var d in docs)
            {
                var name = d["name"].AsString;
                if (name == "_id_") continue;
                if (name == desired.Name) continue;

                if (!d.TryGetValue("key", out var k) || !k.IsBsonDocument) continue;
                var keyDoc = k.AsBsonDocument;

                if (BsonEqualsKey(keyDoc, desired.Key))
                    await col.Indexes.DropOneAsync(name, ct);
            }
        }

        private static async Task<BsonDocument[]> ListIndexDocsAsync<T>(IMongoCollection<T> col, CancellationToken ct)
        {
            using var cursor = await col.Indexes.ListAsync(ct);
            var list = await cursor.ToListAsync(ct);
            return list.ToArray();
        }

        private static bool IsSameSpec(BsonDocument current, IndexSpec desired)
        {
            if (!current.TryGetValue("key", out var k) || !k.IsBsonDocument) return false;
            if (!BsonEqualsKey(k.AsBsonDocument, desired.Key)) return false;

            var curUnique = current.TryGetValue("unique", out var u) && u.IsBoolean && u.AsBoolean;
            if (curUnique != desired.Unique) return false;

            var curPartial = current.TryGetValue("partialFilterExpression", out var p) && p.IsBsonDocument
                ? p.AsBsonDocument
                : null;

            if (desired.Partial == null && curPartial != null) return false;
            if (desired.Partial != null && curPartial == null) return false;
            if (desired.Partial != null && !desired.Partial.Equals(curPartial)) return false;

            var curExpire = current.TryGetValue("expireAfterSeconds", out var e) ? (int?)e.ToInt32() : null;
            if (desired.ExpireAfterSeconds == null && curExpire != null) return false;
            if (desired.ExpireAfterSeconds != null && curExpire == null) return false;
            if (desired.ExpireAfterSeconds != null && curExpire != desired.ExpireAfterSeconds) return false;

            return true;
        }

        private static bool BsonEqualsKey(BsonDocument a, BsonDocument b)
        {
            if (a.ElementCount != b.ElementCount) return false;
            var ae = a.Elements.ToArray();
            var be = b.Elements.ToArray();
            for (int i = 0; i < ae.Length; i++)
            {
                if (ae[i].Name != be[i].Name) return false;
                if (!ae[i].Value.Equals(be[i].Value)) return false;
            }
            return true;
        }

        private static async Task CreateIndexByCommandAsync<T>(IMongoCollection<T> col, IndexSpec desired, CancellationToken ct)
        {
            var idx = new BsonDocument
            {
                { "name", desired.Name },
                { "key", desired.Key }
            };

            if (desired.Unique) idx.Add("unique", true);
            if (desired.Partial != null) idx.Add("partialFilterExpression", desired.Partial);
            if (desired.ExpireAfterSeconds != null) idx.Add("expireAfterSeconds", desired.ExpireAfterSeconds.Value);

            var cmd = new BsonDocument
            {
                { "createIndexes", col.CollectionNamespace.CollectionName },
                { "indexes", new BsonArray { idx } }
            };

            await col.Database.RunCommandAsync<BsonDocument>(cmd, cancellationToken: ct);
        }

        private sealed class IndexSpec
        {
            public IndexSpec(string name, BsonDocument key, bool unique = false, BsonDocument? partial = null, int? expireAfterSeconds = null)
            {
                Name = name;
                Key = key;
                Unique = unique;
                Partial = partial;
                ExpireAfterSeconds = expireAfterSeconds;
            }

            public string Name { get; }
            public BsonDocument Key { get; }
            public bool Unique { get; }
            public BsonDocument? Partial { get; }
            public int? ExpireAfterSeconds { get; }
        }
    }
}