using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.Common
{
    public interface IDocRoleService
    {
        Task UpsertWorkRootRolesAsync(Work work, CancellationToken ct);
        Task UpsertWorkAssignmentRolesAsync(WorkAssignment assignment, CancellationToken ct);

        Task RebuildWorkParticipantRolesFromAssignmentsAsync(
            string workId,
            string byUserId,
            CancellationToken ct);

        Task<bool> HasAnyRoleAsync(DocType docType, string docId, string userId, CancellationToken ct);
        Task<bool> HasRoleAsync(DocType docType, string docId, string userId, DocRoleType role, CancellationToken ct);
        Task<List<string>> GetAccessibleDocIdsAsync(DocType docType, string userId, CancellationToken ct);
        Task DeleteDocRolesAsync(DocType docType, string docId, string byUserId, CancellationToken ct);
    }

    public sealed class DocRoleService : IDocRoleService
    {
        private readonly MongoDbContext _ctx;

        public DocRoleService(MongoDbContext ctx)
        {
            _ctx = ctx;
        }

        public async Task UpsertWorkRootRolesAsync(Work work, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var byUserId = work.UpdatedByUserId ?? work.CreatedByUserId ?? "system";

            var desired = new List<DocRole>();

            if (!string.IsNullOrWhiteSpace(work.CreatedByUserId))
            {
                desired.Add(new DocRole
                {
                    DocType = DocType.WORK,
                    DocId = work.Id,
                    UserId = work.CreatedByUserId,
                    Role = DocRoleType.OWNER,
                    User = work.Owner,
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = byUserId,
                    UpdatedByUserId = byUserId
                });
            }

            if (!string.IsNullOrWhiteSpace(work.LeaderDirectiveUserId))
            {
                desired.Add(new DocRole
                {
                    DocType = DocType.WORK,
                    DocId = work.Id,
                    UserId = work.LeaderDirectiveUserId,
                    Role = DocRoleType.LEADER_DIRECTIVE,
                    User = work.LeaderDirective,
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = byUserId,
                    UpdatedByUserId = byUserId
                });
            }

            var watchIds = (work.LeaderWatchUserIds ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var watcherId in watchIds)
            {
                var watcherRef = (work.LeaderWatch ?? new List<UserRef>())
                    .FirstOrDefault(x => x.UserId == watcherId);

                desired.Add(new DocRole
                {
                    DocType = DocType.WORK,
                    DocId = work.Id,
                    UserId = watcherId,
                    Role = DocRoleType.LEADER_WATCH,
                    User = watcherRef,
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = byUserId,
                    UpdatedByUserId = byUserId
                });
            }

            await ReplaceRolesByExactRoleSetAsync(
                docType: DocType.WORK,
                docId: work.Id,
                replaceRoles: new[]
                {
                    DocRoleType.OWNER,
                    DocRoleType.LEADER_DIRECTIVE,
                    DocRoleType.LEADER_WATCH
                },
                desired: desired,
                byUserId: byUserId,
                ct: ct);
        }

        public async Task UpsertWorkAssignmentRolesAsync(WorkAssignment assignment, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var byUserId = assignment.UpdatedByUserId ?? assignment.CreatedByUserId ?? "system";

            var desired = new List<DocRole>();

            if (!string.IsNullOrWhiteSpace(assignment.CreatedByUserId))
            {
                desired.Add(new DocRole
                {
                    DocType = DocType.WORK_ASSIGNMENT,
                    DocId = assignment.Id!,
                    UserId = assignment.CreatedByUserId,
                    Role = DocRoleType.ASSIGNER,
                    User = null,
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = byUserId,
                    UpdatedByUserId = byUserId
                });
            }

            foreach (var assignee in (assignment.Assignees ?? new List<UserRef>())
                         .Where(x => !string.IsNullOrWhiteSpace(x.UserId))
                         .GroupBy(x => x.UserId, StringComparer.Ordinal)
                         .Select(g => g.First()))
            {
                desired.Add(new DocRole
                {
                    DocType = DocType.WORK_ASSIGNMENT,
                    DocId = assignment.Id!,
                    UserId = assignee.UserId,
                    Role = DocRoleType.ASSIGNEE,
                    User = ToUserRef(assignee),
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = byUserId,
                    UpdatedByUserId = byUserId
                });
            }

            foreach (var watcher in (assignment.LeaderWatchers ?? new List<UserRef>())
                         .Where(x => !string.IsNullOrWhiteSpace(x.UserId))
                         .GroupBy(x => x.UserId, StringComparer.Ordinal)
                         .Select(g => g.First()))
            {
                desired.Add(new DocRole
                {
                    DocType = DocType.WORK_ASSIGNMENT,
                    DocId = assignment.Id!,
                    UserId = watcher.UserId,
                    Role = DocRoleType.ASSIGNMENT_LEADER_WATCH,
                    User = watcher,
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = byUserId,
                    UpdatedByUserId = byUserId
                });
            }

            await ReplaceRolesByExactRoleSetAsync(
                docType: DocType.WORK_ASSIGNMENT,
                docId: assignment.Id!,
                replaceRoles: new[]
                {
                    DocRoleType.ASSIGNER,
                    DocRoleType.ASSIGNEE,
                    DocRoleType.ASSIGNMENT_LEADER_WATCH
                },
                desired: desired,
                byUserId: byUserId,
                ct: ct);
        }

        public async Task RebuildWorkParticipantRolesFromAssignmentsAsync(
            string workId,
            string byUserId,
            CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            var assignments = await _ctx.WorkAssignments
                .Find(x => x.WorkId == workId && !x.IsDeleted && x.IsActive)
                .Project(x => new
                {
                    x.Id,
                    x.Assignees
                })
                .ToListAsync(ct);

            var desired = assignments
                .SelectMany(x => x.Assignees ?? new List<UserRef>())
                .Where(x => !string.IsNullOrWhiteSpace(x.UserId))
                .GroupBy(x => x.UserId, StringComparer.Ordinal)
                .Select(g => g.First())
                .Select(a => new DocRole
                {
                    DocType = DocType.WORK,
                    DocId = workId,
                    UserId = a.UserId,
                    Role = DocRoleType.WORK_PARTICIPANT,
                    User = ToUserRef(a),
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = byUserId,
                    UpdatedByUserId = byUserId
                })
                .ToList();

            await ReplaceRolesByExactRoleSetAsync(
                docType: DocType.WORK,
                docId: workId,
                replaceRoles: new[]
                {
                    DocRoleType.WORK_PARTICIPANT
                },
                desired: desired,
                byUserId: byUserId,
                ct: ct);
        }

        public async Task<bool> HasAnyRoleAsync(DocType docType, string docId, string userId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(docId) || string.IsNullOrWhiteSpace(userId))
                return false;

            return await _ctx.DocRoles
                .Find(x =>
                    x.DocType == docType &&
                    x.DocId == docId &&
                    x.UserId == userId &&
                    !x.IsDeleted)
                .AnyAsync(ct);
        }

        public async Task<bool> HasRoleAsync(DocType docType, string docId, string userId, DocRoleType role, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(docId) || string.IsNullOrWhiteSpace(userId))
                return false;

            return await _ctx.DocRoles
                .Find(x =>
                    x.DocType == docType &&
                    x.DocId == docId &&
                    x.UserId == userId &&
                    x.Role == role &&
                    !x.IsDeleted)
                .AnyAsync(ct);
        }

        public async Task<List<string>> GetAccessibleDocIdsAsync(DocType docType, string userId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return new List<string>();

            var ids = await _ctx.DocRoles
                .Find(x =>
                    x.DocType == docType &&
                    x.UserId == userId &&
                    !x.IsDeleted)
                .Project(x => x.DocId)
                .ToListAsync(ct);

            return ids
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        public async Task DeleteDocRolesAsync(DocType docType, string docId, string byUserId, CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            var filter = Builders<DocRole>.Filter.And(
                Builders<DocRole>.Filter.Eq(x => x.DocType, docType),
                Builders<DocRole>.Filter.Eq(x => x.DocId, docId),
                Builders<DocRole>.Filter.Eq(x => x.IsDeleted, false)
            );

            var update = Builders<DocRole>.Update
                .Set(x => x.IsDeleted, true)
                .Set(x => x.DeletedAtUtc, now)
                .Set(x => x.DeletedByUserId, byUserId)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, byUserId);

            await _ctx.DocRoles.UpdateManyAsync(filter, update, cancellationToken: ct);
        }

        private async Task ReplaceRolesByExactRoleSetAsync(
            DocType docType,
            string docId,
            IEnumerable<DocRoleType> replaceRoles,
            List<DocRole> desired,
            string byUserId,
            CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var roles = replaceRoles.Distinct().ToList();

            var deleteFilter = Builders<DocRole>.Filter.And(
                Builders<DocRole>.Filter.Eq(x => x.DocType, docType),
                Builders<DocRole>.Filter.Eq(x => x.DocId, docId),
                Builders<DocRole>.Filter.In(x => x.Role, roles),
                Builders<DocRole>.Filter.Eq(x => x.IsDeleted, false)
            );

            var deleteUpdate = Builders<DocRole>.Update
                .Set(x => x.IsDeleted, true)
                .Set(x => x.DeletedAtUtc, now)
                .Set(x => x.DeletedByUserId, byUserId)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, byUserId);

            await _ctx.DocRoles.UpdateManyAsync(deleteFilter, deleteUpdate, cancellationToken: ct);

            desired = desired
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.DocId) &&
                    !string.IsNullOrWhiteSpace(x.UserId))
                .GroupBy(x => new { x.DocType, x.DocId, x.UserId, x.Role })
                .Select(g => g.First())
                .ToList();

            if (desired.Count == 0)
                return;

            await _ctx.DocRoles.InsertManyAsync(desired, cancellationToken: ct);
        }

        private static UserRef ToUserRef(UserRef x) => new()
        {
            UserId = x.UserId,
            Username = x.Username,
            FullName = x.FullName,
            UnitId = x.UnitId,
            UnitSymbol = x.UnitSymbol,
            UnitShortName = x.UnitShortName,
            UnitName = x.UnitName,
            PositionCode = x.PositionCode,
            PositionName = x.PositionName
        };
    }
}