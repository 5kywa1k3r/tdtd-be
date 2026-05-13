using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Logging;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Operations;
using tdtd_be.DTOs.Users;
using tdtd_be.DTOs.Works;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Services.Common;

namespace tdtd_be.Services.Works
{
    public class WorkServices
    {
        public interface IWorkService
        {
            Task<WorkResponse> CreateAsync(WorkCreateRequest req, CancellationToken ct);
            Task<WorkResponse> GetByIdAsync(string id, CancellationToken ct);
            Task<PagedResult<WorkListRow>> SearchAsync(WorkSearchRequest req, CancellationToken ct);
            Task<WorkResponse> UpdateAsync(string id, WorkUpdateRequest req, CancellationToken ct);
            Task DeleteAsync(string id, CancellationToken ct);
        }

        public sealed class WorkService : IWorkService
        {
            private readonly MongoDbContext _ctx;
            private readonly MeAccessor _me;
            private readonly IWorkCodeGenerator _codeGen;
            private readonly IWorkHistoryService _history;
            private readonly IDocRoleService _docRole;
            private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
            private readonly IDocRoleReadModelFreshnessService _docRoleReadModelFreshness;
            private readonly IUserActionLogService _userActionLog;
            private readonly IWorkPermissionService _permission;
            private readonly ILogger<WorkService> _log;

            public WorkService(
                MongoDbContext ctx,
                MeAccessor me,
                IWorkCodeGenerator codeGen,
                IWorkHistoryService history,
                IDocRoleService docRole,
                IDocRoleReadModelProjectionService docRoleReadModelProjection,
                IDocRoleReadModelFreshnessService docRoleReadModelFreshness,
                IUserActionLogService userActionLog,
                IWorkPermissionService permission,
                ILogger<WorkService> log)
            {
                _ctx = ctx;
                _me = me;
                _codeGen = codeGen;
                _history = history;
                _docRole = docRole;
                _docRoleReadModelProjection = docRoleReadModelProjection;
                _docRoleReadModelFreshness = docRoleReadModelFreshness;
                _userActionLog = userActionLog;
                _permission = permission;
                _log = log;
            }

            // ===============================
            // Snapshot helpers
            // ===============================

            private async Task RebuildRootSnapshotAsync(Work doc, CancellationToken ct)
            {
                var ids = new List<string>();

                if (!string.IsNullOrWhiteSpace(doc.CreatedByUserId))
                    ids.Add(doc.CreatedByUserId);

                if (!string.IsNullOrWhiteSpace(doc.LeaderDirectiveUserId))
                    ids.Add(doc.LeaderDirectiveUserId);

                if (doc.LeaderWatchUserIds is { Count: > 0 })
                    ids.AddRange(doc.LeaderWatchUserIds);

                var map = await UserRefSnapshotHelper.LoadUserRefMapAsync(_ctx, ids, ct);

                doc.Owner =
                    !string.IsNullOrWhiteSpace(doc.CreatedByUserId) &&
                    map.TryGetValue(doc.CreatedByUserId, out var owner)
                        ? owner
                        : (!string.IsNullOrWhiteSpace(doc.CreatedByUserId)
                            ? UserRefSnapshotHelper.NewEmptyUserRef(doc.CreatedByUserId)
                            : null);

                doc.LeaderDirective =
                    !string.IsNullOrWhiteSpace(doc.LeaderDirectiveUserId) &&
                    map.TryGetValue(doc.LeaderDirectiveUserId, out var leader)
                        ? leader
                        : (!string.IsNullOrWhiteSpace(doc.LeaderDirectiveUserId)
                            ? UserRefSnapshotHelper.NewEmptyUserRef(doc.LeaderDirectiveUserId)
                            : null);

                doc.LeaderWatch = (doc.LeaderWatchUserIds ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .Select(id => map.TryGetValue(id, out var r) ? r : UserRefSnapshotHelper.NewEmptyUserRef(id))
                    .ToList();
            }

            private async Task<bool> NeedsBackfillSnapshotAsync(Work doc, CancellationToken ct)
            {
                if (doc.Owner == null || doc.LeaderDirective == null || doc.LeaderWatch == null)
                    return true;

                var activeRoleCount = await _ctx.DocRoles
                    .Find(x =>
                        x.DocType == DocType.WORK &&
                        x.DocId == doc.Id &&
                        !x.IsDeleted)
                    .CountDocumentsAsync(ct);

                var expectedRoleCount =
                    (string.IsNullOrWhiteSpace(doc.CreatedByUserId) ? 0 : 1) +
                    (string.IsNullOrWhiteSpace(doc.LeaderDirectiveUserId) ? 0 : 1) +
                    (doc.LeaderWatchUserIds?
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .Count() ?? 0);

                return activeRoleCount != expectedRoleCount;
            }

            private async Task BackfillRootSnapshotAndRolesAsync(Work doc, string byUserId, CancellationToken ct)
            {
                await RebuildRootSnapshotAsync(doc, ct);

                doc.UpdatedAtUtc = DateTime.UtcNow;
                doc.UpdatedByUserId = byUserId;

                await _ctx.Works.ReplaceOneAsync(x => x.Id == doc.Id, doc, cancellationToken: ct);
                await _docRole.UpsertWorkRootRolesAsync(doc, ct);
            }


            private async Task<EvaluationTemplate?> ResolveEvaluationTemplateAsync(string? evaluationTemplateId, CancellationToken ct)
            {
                if (string.IsNullOrWhiteSpace(evaluationTemplateId))
                    return null;

                return await _ctx.EvaluationTemplates
                    .Find(x => x.Id == evaluationTemplateId.Trim() && !x.IsDeleted && x.IsActive)
                    .FirstOrDefaultAsync(ct)
                    ?? throw AppExceptionFactory.BadRequest(
                        AppErrorCode.WORK_EVALUATION_TEMPLATE_NOT_FOUND,
                        new { evaluationTemplateId });
            }

            // ===============================
            // Service methods
            // ===============================

            public async Task<WorkResponse> CreateAsync(WorkCreateRequest req, CancellationToken ct)
            {
                var me = _me.RequireMe();
                _permission.EnsureCanCreateRoot(me);

                if (string.IsNullOrWhiteSpace(req.Name))
                    throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_NAME_REQUIRED);


                var now = DateTime.UtcNow;
                var year = now.Year;
                var autoCode = await _codeGen.GenerateAsync(me.Username, year, ct);

                var leaderWatchUserIds = (req.LeaderWatchUserIds ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var evaluationTemplate = await ResolveEvaluationTemplateAsync(req.EvaluationTemplateId, ct);

                var doc = new Work
                {
                    AutoCode = autoCode,
                    Code = string.IsNullOrWhiteSpace(req.Code) ? null : req.Code.Trim(),
                    Name = req.Name.Trim(),
                    Description = req.Description,
                    Note = req.Note,
                    Status = WorkStatus.S1,
                    Type = req.Type,

                    LeaderDirectiveUserId = string.IsNullOrWhiteSpace(req.LeaderDirectiveUserId) ? null : req.LeaderDirectiveUserId.Trim(),
                    LeaderWatchUserIds = leaderWatchUserIds,

                    EvaluationTemplateId = evaluationTemplate?.Id,
                    EvaluationTemplateCode = evaluationTemplate?.RepresentativeCode,
                    EvaluationTemplateLabel = evaluationTemplate?.RepresentativeLabel,

                    StartDate = req.StartDate,
                    EndDate = req.EndDate,
                    DueDate = req.DueDate,
                    Priority = req.Priority ?? WorkPriority.MEDIUM,
                    AttachmentCount = 0,

                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = me.Id,
                    UpdatedByUserId = me.Id
                };

                await RebuildRootSnapshotAsync(doc, ct);

                await _ctx.Works.InsertOneAsync(doc, cancellationToken: ct);
                await _docRole.UpsertWorkRootRolesAsync(doc, ct);

                await _history.AppendAsync(
                    workId: doc.Id,
                    byUserId: me.Id,
                    type: WorkHistoryType.CREATED,
                    data: new Dictionary<string, object>
                    {
                        { "autoCode", doc.AutoCode },
                        { "priority", (int)doc.Priority },
                        { "type", (int)doc.Type }
                    },
                    ct: ct);

                await _userActionLog.RecordAsync(new UserActionLogSeed
                {
                    Action = UserActionLogActions.WorkCreated,
                    Scope = "work",
                    ActorUserId = me.Id,
                    WorkId = doc.Id,
                    Summary = $"Created work {doc.AutoCode}",
                    Data = new Dictionary<string, string>
                    {
                        { "autoCode", doc.AutoCode },
                        { "type", doc.Type.ToString() },
                        { "priority", doc.Priority.ToString() }
                    },
                    OccurredAtUtc = now
                }, CancellationToken.None);

                return ToResponse(doc);
            }

            public async Task<WorkResponse> GetByIdAsync(string id, CancellationToken ct)
            {
                var me = _me.RequireMe();

                var doc = await _ctx.Works
                    .Find(x => x.Id == id && !x.IsDeleted)
                    .FirstOrDefaultAsync(ct);

                if (doc is null)
                    throw AppExceptionFactory.NotFound(AppErrorCode.WORK_NOT_FOUND, new { workId = id });

                await _permission.EnsureCanReadAsync(id, me.Id, ct);

                if (await NeedsBackfillSnapshotAsync(doc, ct))
                    await BackfillRootSnapshotAndRolesAsync(doc, me.Id, ct);

                await _docRoleReadModelFreshness.EnsureWorkFreshAsync(doc, me.Id, ct);

                return ToResponse(doc);
            }

            public async Task<PagedResult<WorkListRow>> SearchAsync(WorkSearchRequest req, CancellationToken ct)
            {
                var me = _me.RequireMe();

                var page = req.Page < 0 ? 0 : req.Page;
                var pageSize = req.PageSize <= 0 ? 10 : Math.Min(req.PageSize, 200);

                if (!await EnsureWorkListDocRolesForUserAsync(me.Id, ct))
                    return new PagedResult<WorkListRow>(new List<WorkListRow>(), 0, page, pageSize);

                var fb = Builders<WorkListDocRole>.Filter;
                var f = fb.Eq(x => x.IsDeleted, false) &
                        fb.Eq(x => x.DocType, DocType.WORK) &
                        fb.Eq(x => x.UserId, me.Id) &
                        fb.Eq(x => x.Type, req.Type);

                if (!string.IsNullOrWhiteSpace(req.Q))
                {
                    var q = req.Q.Trim();
                    var qRegex = new BsonRegularExpression(q, "i");

                    f &= fb.Or(
                        fb.Regex(x => x.AutoCode, qRegex),
                        fb.Regex(x => x.Name, qRegex),
                        fb.Regex(x => x.Code, qRegex)
                    );
                }

                if (req.Status != null)
                    f &= fb.Eq(x => x.Status, req.Status.Value);

                if (req.Priority != null)
                    f &= fb.Eq(x => x.Priority, req.Priority.Value);

                if (!string.IsNullOrWhiteSpace(req.LeaderDirectiveUserId))
                    f &= fb.Eq(x => x.LeaderDirectiveUserId, req.LeaderDirectiveUserId);

                var sort = BuildDocRoleSort(req.SortField, req.SortDirection);

                var total = await _ctx.WorkListDocRoles.CountDocumentsAsync(f, cancellationToken: ct);

                var rows = await _ctx.WorkListDocRoles.Find(f)
                    .Sort(sort)
                    .Skip(page * pageSize)
                    .Limit(pageSize)
                    .Project(x => new WorkListRow(
                        x.WorkId,
                        x.AutoCode,
                        x.Code,
                        x.Name,
                        x.Status,
                        x.Priority,
                        x.Type,
                        x.WorkCreatedByUserId,
                        x.OwnerName,
                        x.LeaderDirectiveUserId,
                        x.LeaderWatchCount,
                        x.EvaluationTemplateId,
                        x.EvaluationTemplateCode,
                        x.EvaluationTemplateLabel,
                        x.HasManualEvaluations,
                        x.EvaluatedAssignmentCount,
                        x.WorstEvaluationCode,
                        x.WorstEvaluationLabel,
                        x.DueDate,
                        x.WorkCreatedAtUtc
                    ))
                    .ToListAsync(ct);

                return new PagedResult<WorkListRow>(rows, total, page, pageSize);
            }

            private async Task<bool> EnsureWorkListDocRolesForUserAsync(string userId, CancellationToken ct)
            {
                if (string.IsNullOrWhiteSpace(userId))
                    return false;

                var hasProjectedRows = await _ctx.WorkListDocRoles
                    .Find(x =>
                        x.UserId == userId &&
                        x.DocType == DocType.WORK &&
                        !x.IsDeleted)
                    .AnyAsync(ct);

                if (hasProjectedRows)
                    return true;

                _log.LogWarning(
                    "Work list projection missing. userId={userId}. Returning current projection only; run internal DocRole repair/backfill if source data exists.",
                    userId);

                return false;
            }

            public async Task<WorkResponse> UpdateAsync(string id, WorkUpdateRequest req, CancellationToken ct)
            {
                var me = _me.RequireMe();

                var doc = await _ctx.Works
                    .Find(x => x.Id == id && !x.IsDeleted)
                    .FirstOrDefaultAsync(ct);

                if (doc is null)
                    throw AppExceptionFactory.NotFound(AppErrorCode.WORK_NOT_FOUND, new { workId = id });

                await _permission.EnsureCanUpdateRootAsync(id, me.Id, ct);

                if (!string.IsNullOrWhiteSpace(req.Name))
                    doc.Name = req.Name.Trim();

                if (req.Description != null)
                    doc.Description = req.Description;

                if (req.Note != null)
                    doc.Note = req.Note;

                if (req.Code != null)
                    doc.Code = string.IsNullOrWhiteSpace(req.Code) ? null : req.Code.Trim();

                var needRebuildRoot = false;

                if (req.EvaluationTemplateId != null)
                {
                    var evaluationTemplate = await ResolveEvaluationTemplateAsync(req.EvaluationTemplateId, ct);
                    doc.EvaluationTemplateId = evaluationTemplate?.Id;
                    doc.EvaluationTemplateCode = evaluationTemplate?.RepresentativeCode;
                    doc.EvaluationTemplateLabel = evaluationTemplate?.RepresentativeLabel;
                }

                if (req.LeaderDirectiveUserId != null)
                {
                    doc.LeaderDirectiveUserId = string.IsNullOrWhiteSpace(req.LeaderDirectiveUserId)
                        ? null
                        : req.LeaderDirectiveUserId.Trim();
                    needRebuildRoot = true;
                }

                if (req.LeaderWatchUserIds != null)
                {
                    doc.LeaderWatchUserIds = req.LeaderWatchUserIds
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    needRebuildRoot = true;
                }

                doc.StartDate = req.StartDate;
                doc.EndDate = req.EndDate;
                doc.DueDate = req.DueDate;

                if (req.Priority != null)
                    doc.Priority = req.Priority.Value;

                doc.UpdatedAtUtc = DateTime.UtcNow;
                doc.UpdatedByUserId = me.Id;

                if (needRebuildRoot || await NeedsBackfillSnapshotAsync(doc, ct))
                    await RebuildRootSnapshotAsync(doc, ct);

                await _ctx.Works.ReplaceOneAsync(x => x.Id == id, doc, cancellationToken: ct);
                await _docRole.UpsertWorkRootRolesAsync(doc, ct);

                await _history.AppendAsync(
                    workId: doc.Id,
                    byUserId: me.Id,
                    type: WorkHistoryType.UPDATED,
                    data: new Dictionary<string, object?>
                    {
                        { "priority", (int)doc.Priority },
                        { "leaderDirectiveUserId", doc.LeaderDirectiveUserId },
                        { "leaderWatchCount", doc.LeaderWatchUserIds.Count }
                    },
                    ct: ct);

                return ToResponse(doc);
            }

            public async Task DeleteAsync(string id, CancellationToken ct)
            {
                var me = _me.RequireMe();

                var doc = await _ctx.Works
                    .Find(x => x.Id == id && !x.IsDeleted)
                    .FirstOrDefaultAsync(ct);

                if (doc is null)
                    throw AppExceptionFactory.NotFound(AppErrorCode.WORK_NOT_FOUND, new { workId = id });

                await _permission.EnsureCanDeleteRootAsync(id, me.Id, ct);

                var now = DateTime.UtcNow;

                var workUpdate = Builders<Work>.Update
                    .Set(x => x.IsDeleted, true)
                    .Set(x => x.DeletedAtUtc, now)
                    .Set(x => x.DeletedByUserId, me.Id)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, me.Id);

                await _ctx.Works.UpdateOneAsync(
                    filter: Builders<Work>.Filter.Where(x => x.Id == id && !x.IsDeleted),
                    update: workUpdate,
                    cancellationToken: ct);

                var fileFilter = Builders<FileDoc>.Filter.And(
                    Builders<FileDoc>.Filter.Eq(x => x.SourceId, id),
                    Builders<FileDoc>.Filter.Eq(x => x.IsDeleted, false)
                );

                var fileUpdate = Builders<FileDoc>.Update
                    .Set(x => x.IsDeleted, true)
                    .Set(x => x.DeletedAtUtc, now)
                    .Set(x => x.DeletedByUserId, me.Id)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, me.Id);

                await _ctx.Files.UpdateManyAsync(fileFilter, fileUpdate, cancellationToken: ct);
                await _docRole.DeleteDocRolesAsync(DocType.WORK, id, me.Id, ct);

                await _history.AppendAsync(
                    workId: id,
                    byUserId: me.Id,
                    type: WorkHistoryType.DELETED,
                    data: null,
                    ct: ct);
            }

            // ===============================
            // Mappers / sort
            // ===============================

            private static SortDefinition<Work> BuildSort(string? sortField, string? sortDirection)
            {
                var desc = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);
                sortField = string.IsNullOrWhiteSpace(sortField) ? "createdAtUtc" : sortField;

                var sb = Builders<Work>.Sort;

                return sortField switch
                {
                    "createdAtUtc" => desc ? sb.Descending(x => x.CreatedAtUtc) : sb.Ascending(x => x.CreatedAtUtc),
                    "dueDate" => desc ? sb.Descending(x => x.DueDate) : sb.Ascending(x => x.DueDate),
                    "autoCode" => desc ? sb.Descending(x => x.AutoCode) : sb.Ascending(x => x.AutoCode),
                    "name" => desc ? sb.Descending(x => x.Name) : sb.Ascending(x => x.Name),
                    "priority" => desc ? sb.Descending(x => x.Priority) : sb.Ascending(x => x.Priority),
                    _ => desc ? sb.Descending(x => x.CreatedAtUtc) : sb.Ascending(x => x.CreatedAtUtc),
                };
            }

            private static SortDefinition<WorkListDocRole> BuildDocRoleSort(string? sortField, string? sortDirection)
            {
                var desc = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);
                sortField = string.IsNullOrWhiteSpace(sortField) ? "createdAtUtc" : sortField;

                var sb = Builders<WorkListDocRole>.Sort;

                return sortField switch
                {
                    "createdAtUtc" => desc ? sb.Descending(x => x.WorkCreatedAtUtc) : sb.Ascending(x => x.WorkCreatedAtUtc),
                    "dueDate" => desc ? sb.Descending(x => x.DueDate) : sb.Ascending(x => x.DueDate),
                    "autoCode" => desc ? sb.Descending(x => x.AutoCode) : sb.Ascending(x => x.AutoCode),
                    "name" => desc ? sb.Descending(x => x.Name) : sb.Ascending(x => x.Name),
                    "priority" => desc ? sb.Descending(x => x.Priority) : sb.Ascending(x => x.Priority),
                    _ => desc ? sb.Descending(x => x.WorkCreatedAtUtc) : sb.Ascending(x => x.WorkCreatedAtUtc),
                };
            }

            private static WorkResponse ToResponse(Work x) => new(
                x.Id,
                x.AutoCode,
                x.Code,
                x.Name,
                x.Description,
                x.Note,
                x.Status,
                x.CreatedByUserId,
                x.LeaderDirectiveUserId,
                x.LeaderWatchUserIds,
                x.EvaluationTemplateId,
                x.EvaluationTemplateCode,
                x.EvaluationTemplateLabel,
                x.HasManualEvaluations,
                x.EvaluatedAssignmentCount,
                x.WorstEvaluationCode,
                x.WorstEvaluationLabel,
                x.StartDate,
                x.EndDate,
                x.DueDate,
                x.Priority,
                x.Type,
                x.IsDeleted,
                x.CreatedAtUtc,
                x.UpdatedAtUtc,
                x.Owner != null ? ToUserRefDto(x.Owner) : null,
                x.LeaderDirective != null ? ToUserRefDto(x.LeaderDirective) : null,
                (x.LeaderWatch ?? new List<UserRef>()).Select(ToUserRefDto).ToList()
            );

            private static UserRefDTO ToUserRefDto(UserRef x) => new(
                userId: x.UserId,
                username: x.Username,
                fullName: x.FullName,
                unitId: x.UnitId,
                unitSymbol: x.UnitSymbol,
                unitShortName: x.UnitShortName,
                unitName: x.UnitName,
                positionCode: x.PositionCode,
                positionName: x.PositionName
            );
        }
    }
}
