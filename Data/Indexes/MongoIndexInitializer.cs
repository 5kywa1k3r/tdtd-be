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
            // USERS (soft delete)
            var users = db.GetCollection<AppUser>(opt.UserCollection);
            await EnsureUsersAsync(users, ct);

            // REFRESH TOKENS (TTL, typically no soft delete)
            var rts = db.GetCollection<RefreshTokenDoc>(opt.RefreshTokenCollection);
            await EnsureRefreshTokensAsync(rts, ct);

            // UNITS (soft delete)
            var units = db.GetCollection<Unit>(opt.UnitCollection);
            await EnsureUnitsAsync(units, ct);

            // UNIT TYPES (soft delete)
            var unitTypes = db.GetCollection<UnitType>(opt.UnitTypeCollection);
            await EnsureUnitTypesAsync(unitTypes, ct);

            // UNIT HISTORIES (usually immutable, no soft delete required)
            var unitHistories = db.GetCollection<UnitVersionHistory>(opt.UnitHistoryCollection);
            await EnsureUnitHistoriesAsync(unitHistories, ct);

            // FILES (soft delete)
            var files = db.GetCollection<FileDoc>(opt.FileDocCollection);
            await EnsureFilesAsync(files, ct);

            // DYNAMIC EXCEL (soft delete)s
            var dx = db.GetCollection<DynamicExcelTemplate>(opt.DynamicExcelTemplateCollection);
            await EnsureDynamicExcelAsync(dx, ct);

            // WORKS (soft delete)
            var works = db.GetCollection<Work>(opt.WorkCollection);
            await EnsureWorksAsync(works, ct);

            // DocRole (soft delete)
            var docRole = db.GetCollection<DocRole>(opt.DocRoleCollection);
            await EnsureDocRolesAsync(docRole, ct);

            // WORK HISTORIES (usually immutable but still BaseEntity has isDeleted => keep)
            var wh = db.GetCollection<WorkHistory>(opt.WorkHistoryCollection);
            await EnsureWorkHistoriesAsync(wh, ct);

            // COUNTERS (no soft delete)
            var counters = db.GetCollection<CounterDoc>(opt.CounterCollection);
            await EnsureCountersAsync(counters, ct);

            // COUNTERS (no soft delete)
            var workAssignment = db.GetCollection<WorkAssignment>(opt.WorkAssignmentCollection);
            await EnsureWorkAssignmentsAsync(workAssignment, ct);

            // WORK ASSIGNMENT REPORTS (soft delete)
            var workAssignmentReports = db.GetCollection<WorkAssignmentReport>(opt.WorkAssignmentReportCollection);
            await EnsureWorkAssignmentReportsAsync(workAssignmentReports, ct);
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
            // NOTE: include isDeleted to speed active search
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_users_unitId_isDeleted",
                key: new BsonDocument { { "unitId", 1 }, { "isDeleted", 1 } }
            ), ct);
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

            // include isDeleted for active children listing
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_units_parentUnitId_isDeleted",
                key: new BsonDocument { { "parentUnitId", 1 }, { "isDeleted", 1 } }
            ), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_units_code_active",
                key: new BsonDocument("code", 1),
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            // optional: prefix search by code (if you query startswith/regex prefix)
            // This index helps range/prefix queries if you use regex anchored "^xxx" or similar.
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_units_code_isDeleted",
                key: new BsonDocument { { "code", 1 }, { "isDeleted", 1 } }
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
        private static async Task EnsureFilesAsync(IMongoCollection<FileDoc> col, CancellationToken ct)
        {
            // partition
            await EnsureBySpecAsync(col, new IndexSpec("ix_files_isDeleted", new BsonDocument("isDeleted", 1)), ct);

            // owner timeline (active)
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_files_owner_createdAt_desc_isDeleted",
                key: new BsonDocument
                {
                    { "createdByUserId", 1 },
                    { "createdAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            // ✅ unique object identity should be unique among active only (soft delete safe)
            await PrecheckDuplicateActiveCompositeAsync(
                col,
                fields: new[] { "bucket", "objectKey" },
                ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_files_bucket_objectKey_active",
                key: new BsonDocument { { "bucket", 1 }, { "objectKey", 1 } },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            // uploadId + owner for lookups (usually active)
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_files_upload_owner_isDeleted",
                key: new BsonDocument
                {
                    { "uploadId", 1 },
                    { "createdByUserId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }
        // ================= DYNAMIC EXCEL =================
        private static async Task EnsureDynamicExcelAsync(IMongoCollection<DynamicExcelTemplate> col, CancellationToken ct)
        {
            await EnsureBySpecAsync(col, new IndexSpec("ix_dynamicExcel_isDeleted", new BsonDocument("isDeleted", 1)), ct);

            // precheck unique among active
            await PrecheckDuplicateActiveAsync(col, field: "code", ct);

            // ✅ unique active code
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_dynamicExcel_code_active",
                key: new BsonDocument("code", 1),
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            // search/sort helpers
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicExcel_createdAt_desc",
                key: new BsonDocument { { "createdAtUtc", -1 } }
            ), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicExcel_createdBy_createdAt_desc",
                key: new BsonDocument { { "createdByUserId", 1 }, { "createdAtUtc", -1 }, { "isDeleted", 1 } }
            ), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicExcel_name_isDeleted",
                key: new BsonDocument { { "name", 1 }, { "isDeleted", 1 } }
            ), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_dynamicExcel_labels_isDeleted",
                key: new BsonDocument { { "labels", 1 }, { "isDeleted", 1 } }
            ), ct);
        }

        // ================= WORKS =================
        private static async Task EnsureWorksAsync(IMongoCollection<Work> col, CancellationToken ct)
        {
            // partition / base soft-delete
            await EnsureBySpecAsync(col,
                new IndexSpec("ix_works_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            // precheck unique among active for autoCode
            await PrecheckDuplicateActiveAsync(col, field: "autoCode", ct);

            // unique active autoCode
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_works_autoCode_active",
                key: new BsonDocument("autoCode", 1),
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            // search helpers
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_works_name_isDeleted",
                key: new BsonDocument
                {
            { "name", 1 },
            { "isDeleted", 1 }
                }
            ), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_works_status_isDeleted",
                key: new BsonDocument
                {
            { "status", 1 },
            { "isDeleted", 1 }
                }
            ), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_works_type_createdAt_desc_isDeleted",
                key: new BsonDocument
                {
            { "type", 1 },
            { "createdAtUtc", -1 },
            { "isDeleted", 1 }
                }
            ), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_works_type_priority_createdAt_desc_isDeleted",
                key: new BsonDocument
                {
            { "type", 1 },
            { "priority", 1 },
            { "createdAtUtc", -1 },
            { "isDeleted", 1 }
                }
            ), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_works_leaderDirective_isDeleted",
                key: new BsonDocument
                {
            { "leaderDirectiveUserId", 1 },
            { "isDeleted", 1 }
                }
            ), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_works_dueDate_isDeleted",
                key: new BsonDocument
                {
            { "dueDate", 1 },
            { "isDeleted", 1 }
                }
            ), ct);

            // optional: code unique active nếu cho phép người dùng nhập code riêng và muốn không trùng
            // await PrecheckDuplicateActiveAsync(col, field: "code", ct);
            // await EnsureBySpecAsync(col, new IndexSpec(
            //     name: "ux_works_code_active",
            //     key: new BsonDocument("code", 1),
            //     unique: true,
            //     partial: new BsonDocument("isDeleted", false)
            // ), ct);
        }

        // ================= DOC ROLES =================
        private static async Task EnsureDocRolesAsync(IMongoCollection<DocRole> col, CancellationToken ct)
        {
            await EnsureBySpecAsync(col,
                new IndexSpec("ix_docRoles_isDeleted", new BsonDocument("isDeleted", 1)),
                ct);

            // precheck unique active theo (docType, docId, userId, role)
            await PrecheckDuplicateActiveByFieldsAsync(
                col,
                fields: new[] { "docType", "docId", "userId", "role" },
                ct: ct);

            // unique active: 1 user - 1 role - 1 doc chỉ có 1 dòng
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_docRoles_docType_docId_userId_role_active",
                key: new BsonDocument
                {
            { "docType", 1 },
            { "docId", 1 },
            { "userId", 1 },
            { "role", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            // query user xem được doc nào
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_docRoles_docType_userId_isDeleted",
                key: new BsonDocument
                {
            { "docType", 1 },
            { "userId", 1 },
            { "isDeleted", 1 }
                }
            ), ct);

            // query 1 doc có những ai
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_docRoles_docType_docId_isDeleted",
                key: new BsonDocument
                {
            { "docType", 1 },
            { "docId", 1 },
            { "isDeleted", 1 }
                }
            ), ct);

            // query role cụ thể trong 1 doc
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_docRoles_docType_docId_role_isDeleted",
                key: new BsonDocument
                {
            { "docType", 1 },
            { "docId", 1 },
            { "role", 1 },
            { "isDeleted", 1 }
                }
            ), ct);
        }

        // ================= WORK HISTORIES =================
        private static async Task EnsureWorkHistoriesAsync(IMongoCollection<WorkHistory> col, CancellationToken ct)
        {
            await EnsureBySpecAsync(col, new IndexSpec("ix_workHist_isDeleted", new BsonDocument("isDeleted", 1)), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workHist_workId_at_desc",
                key: new BsonDocument { { "workId", 1 }, { "atUtc", -1 } }
            ), ct);
        }

        // ================= COUNTERS =================
        private static async Task EnsureCountersAsync(IMongoCollection<CounterDoc> col, CancellationToken ct)
        {
            // unique key
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_counters_key",
                key: new BsonDocument("key", 1),
                unique: true
            ), ct);
        }

        // ================= WORKASSIGNMENT =================
        private static async Task EnsureWorkAssignmentsAsync(IMongoCollection<WorkAssignment> col, CancellationToken ct)
        {
            // partition
            await EnsureBySpecAsync(col, new IndexSpec(
                "ix_workAssignments_isDeleted",
                new BsonDocument("isDeleted", 1)
            ), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_work_createdAt_desc_isDeleted",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "createdAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_work_dynamicExcel_isDeleted",
                key: new BsonDocument
                {
                    { "workId", 1 },
                    { "dynamicExcelId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_assignmentType_isDeleted",
                key: new BsonDocument
                {
                    { "assignmentType", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_aggregationType_isDeleted",
                key: new BsonDocument
                {
                    { "aggregationType", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_assignees_userId_isDeleted",
                key: new BsonDocument
                {
                    { "assignees.userId", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_updatedAt_desc_isDeleted",
                key: new BsonDocument
                {
                    { "updatedAtUtc", -1 },
                    { "isDeleted", 1 }
                }
            ), ct);

            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignments_isActive_isDeleted",
                key: new BsonDocument
                {
                    { "isActive", 1 },
                    { "isDeleted", 1 }
                }
            ), ct);
        }

        // ================= WORK ASSIGNMENT REPORTS =================
        private static async Task EnsureWorkAssignmentReportsAsync(
            IMongoCollection<WorkAssignmentReport> col,
            CancellationToken ct)
        {
            // partition index cho soft delete
            await EnsureBySpecAsync(col, new IndexSpec(
                "ix_workAssignmentReports_isDeleted",
                new BsonDocument("isDeleted", 1)
            ), ct);

            // unique version trong cùng 1 assignment + kỳ, chỉ áp dụng với bản active
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ux_workAssignmentReports_assignment_period_version_active",
                key: new BsonDocument
                {
                { "workAssignmentId", 1 },
                { "periodKey", 1 },
                { "versionNo", 1 }
                },
                unique: true,
                partial: new BsonDocument("isDeleted", false)
            ), ct);

            // query lấy bản current của 1 assignment + kỳ
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_assignment_period_current_isDeleted",
                key: new BsonDocument
                {
                { "workAssignmentId", 1 },
                { "periodKey", 1 },
                { "isCurrent", 1 },
                { "isDeleted", 1 }
                }
            ), ct);

            // query list theo assignment, sort updated mới nhất
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_assignment_isDeleted_updatedAtUtc",
                key: new BsonDocument
                {
                { "workAssignmentId", 1 },
                { "isDeleted", 1 },
                { "updatedAtUtc", -1 }
                }
            ), ct);

            // query list theo work root
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_work_isDeleted_updatedAtUtc",
                key: new BsonDocument
                {
                { "workId", 1 },
                { "isDeleted", 1 },
                { "updatedAtUtc", -1 }
                }
            ), ct);

            // filter theo status
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_status_isDeleted_updatedAtUtc",
                key: new BsonDocument
                {
                { "status", 1 },
                { "isDeleted", 1 },
                { "updatedAtUtc", -1 }
                }
            ), ct);

            // filter/search theo template
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_template_isDeleted_updatedAtUtc",
                key: new BsonDocument
                {
                { "dynamicExcelTemplateId", 1 },
                { "isDeleted", 1 },
                { "updatedAtUtc", -1 }
                }
            ), ct);

            // query theo kỳ nếu cần thống kê / lọc nhanh
            await EnsureBySpecAsync(col, new IndexSpec(
                name: "ix_workAssignmentReports_period_isDeleted_updatedAtUtc",
                key: new BsonDocument
                {
                { "periodKey", 1 },
                { "isDeleted", 1 },
                { "updatedAtUtc", -1 }
                }
            ), ct);
        }

        // ---------- Precheck duplicates (active only - single field) ----------
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

        // ---------- Precheck duplicates (active only - composite fields) ----------
        private static async Task PrecheckDuplicateActiveCompositeAsync<T>(
            IMongoCollection<T> col,
            string[] fields,
            CancellationToken ct)
        {
            if (fields is null || fields.Length == 0)
                throw new ArgumentException("fields required.", nameof(fields));

            var idDoc = new BsonDocument();
            foreach (var f in fields)
                idDoc.Add(f, "$" + f);

            var pipeline = new[]
            {
                new BsonDocument("$match", new BsonDocument
                {
                    { "isDeleted", false }
                }),
                new BsonDocument("$group", new BsonDocument
                {
                    { "_id", idDoc },
                    { "c", new BsonDocument("$sum", 1) }
                }),
                new BsonDocument("$match", new BsonDocument("c", new BsonDocument("$gt", 1))),
                new BsonDocument("$limit", 10)
            };

            var dup = await col.Aggregate<BsonDocument>(pipeline).ToListAsync(ct);
            if (dup.Count > 0)
            {
                var sample = string.Join(", ", dup.Select(x =>
                {
                    var id = x["_id"].AsBsonDocument;
                    var parts = string.Join(";", fields.Select(f => $"{f}={id.GetValue(f, BsonNull.Value)}"));
                    return $"{parts} (count={x["c"]})";
                }));

                throw new InvalidOperationException(
                    $"Duplicate active composite key detected in {col.CollectionNamespace.CollectionName}: {sample}. " +
                    $"Fix data before creating unique active index on ({string.Join(", ", fields)}) with isDeleted=false.");
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

            // 3) create (idempotent-ish)
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

        private static async Task PrecheckDuplicateActiveByFieldsAsync<T>(
            IMongoCollection<T> col,
            string[] fields,
            CancellationToken ct)
        {
            if (fields == null || fields.Length == 0)
                throw new ArgumentException("fields is required", nameof(fields));

            var match = new BsonDocument("$match", new BsonDocument("isDeleted", false));

            var groupId = new BsonDocument();
            foreach (var f in fields)
                groupId.Add(f, $"${f}");

            var group = new BsonDocument("$group", new BsonDocument
            {
                { "_id", groupId },
                { "count", new BsonDocument("$sum", 1) }
            });

            var matchDup = new BsonDocument("$match", new BsonDocument("count", new BsonDocument("$gt", 1)));
            var limit = new BsonDocument("$limit", 1);

            var pipeline = new[] { match, group, matchDup, limit };

            var dup = await col.Aggregate<BsonDocument>(pipeline).FirstOrDefaultAsync(ct);
            if (dup != null)
            {
                var keyText = string.Join(", ", fields);
                throw new InvalidOperationException($"Duplicate active documents detected for unique key: {keyText}");
            }
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