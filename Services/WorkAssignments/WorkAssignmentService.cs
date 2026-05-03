
using MongoDB.Driver;
using Microsoft.Extensions.Logging;
using System.Linq;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Users;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.DTOs.WorkAssignments.Review;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services;
using tdtd_be.Services.WorkAssignments.Domain;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments.Lookups;
using tdtd_be.Services.WorkAssignments.Queue;
using tdtd_be.Services.WorkAssignments.Runtime;

namespace tdtd_be.Services.WorkAssignments;

public sealed class WorkAssignmentService : IWorkAssignmentService
{
    private readonly MongoDbContext _ctx;
    private readonly IDocRoleService _docRole;
    private readonly IWorkAssignmentLookupService _lookup;
    private readonly IWorkAssignmentTemplateResolver _templateResolver;
    private readonly IWorkAssignmentDataGuardService _dataGuard;
    private readonly IWorkAssignmentTreeService _tree;
    private readonly IWorkTemplateAssigneeBindingService _binding;
    private readonly IWorkAssignmentQueueService _queueService;
    private readonly IWorkAssignmentMaterializeJobService _materializeJob;
    private readonly IWorkAssignmentStatusRepairService _statusRepair;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
    private readonly IDocRoleReadModelFreshnessService _docRoleReadModelFreshness;
    private readonly IUnitSelectionService _unitSelection;
    private readonly MeAccessor _me;
    private readonly ILogger<WorkAssignmentService> _log;

    public WorkAssignmentService(
        MongoDbContext ctx,
        IDocRoleService docRole,
        IWorkAssignmentLookupService lookup,
        IWorkAssignmentTemplateResolver templateResolver,
        IWorkAssignmentDataGuardService dataGuard,
        IWorkAssignmentTreeService tree,
        IWorkTemplateAssigneeBindingService binding,
        IWorkAssignmentQueueService queueService,
        IWorkAssignmentMaterializeJobService materializeJob,
        IWorkAssignmentStatusRepairService statusRepair,
        IDocRoleReadModelProjectionService docRoleReadModelProjection,
        IDocRoleReadModelFreshnessService docRoleReadModelFreshness,
        IUnitSelectionService unitSelection,
        MeAccessor me,
        ILogger<WorkAssignmentService> log)
    {
        _ctx = ctx;
        _docRole = docRole;
        _lookup = lookup;
        _templateResolver = templateResolver;
        _dataGuard = dataGuard;
        _tree = tree;
        _binding = binding;
        _queueService = queueService;
        _materializeJob = materializeJob;
        _statusRepair = statusRepair;
        _docRoleReadModelProjection = docRoleReadModelProjection;
        _docRoleReadModelFreshness = docRoleReadModelFreshness;
        _unitSelection = unitSelection;
        _me = me;
        _log = log;
    }

    public async Task<List<WorkAssignmentListResponse>> GetByWorkIdAsync(
        string workId,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (!await EnsureAssignmentListDocRolesForUserWorkAsync(workId, actorUserId, ct))
            return new List<WorkAssignmentListResponse>();

        var fb = Builders<AssignmentListDocRole>.Filter;
        var filter = fb.Eq(x => x.WorkId, workId)
                     & fb.Eq(x => x.UserId, actorUserId)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.AnyEq(x => x.Roles, DocRoleType.ASSIGNER);

        var items = await _ctx.AssignmentListDocRoles
            .Find(filter)
            .SortByDescending(x => x.IsActive)
            .ThenByDescending(x => x.AssignmentUpdatedAtUtc)
            .ThenBy(x => x.Path)
            .ToListAsync(ct);

        return items
            .Select(ToListResponse)
            .ToList();
    }

    public async Task<List<WorkAssignmentListResponse>> GetMyReportAssignmentsAsync(
        string workId,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (!await EnsureAssignmentListDocRolesForUserWorkAsync(workId, actorUserId, ct))
            return new List<WorkAssignmentListResponse>();

        var fb = Builders<AssignmentListDocRole>.Filter;
        var filter = fb.Eq(x => x.WorkId, workId)
                     & fb.Eq(x => x.UserId, actorUserId)
                     & fb.Eq(x => x.IsActive, true)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.AnyEq(x => x.Roles, DocRoleType.ASSIGNEE);

        var items = await _ctx.AssignmentListDocRoles
            .Find(filter)
            .SortByDescending(x => x.HasOverduePeriod)
            .ThenByDescending(x => x.LatestDueAtUtc)
            .ThenByDescending(x => x.AssignmentUpdatedAtUtc)
            .ThenBy(x => x.Path)
            .ToListAsync(ct);

        return items
            .Select(ToListResponse)
            .ToList();
    }

    public async Task<List<WorkAssignmentListResponse>> GetMyReviewParentAssignmentsAsync(
        string workId,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        if (!await EnsureAssignmentListDocRolesForUserWorkAsync(workId, actorUserId, ct))
            return new List<WorkAssignmentListResponse>();

        var fb = Builders<AssignmentListDocRole>.Filter;
        var candidateFilter = fb.Eq(x => x.WorkId, workId)
                              & fb.Eq(x => x.UserId, actorUserId)
                              & fb.Eq(x => x.IsActive, true)
                              & fb.Eq(x => x.IsDeleted, false)
                              & fb.AnyEq(x => x.Roles, DocRoleType.ASSIGNER);

        var candidates = await _ctx.AssignmentListDocRoles
            .Find(candidateFilter)
            .SortBy(x => x.Path)
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return new List<WorkAssignmentListResponse>();

        var candidateIds = candidates
            .Select(x => x.AssignmentId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var childFilter = fb.Eq(x => x.WorkId, workId)
                          & fb.Eq(x => x.UserId, actorUserId)
                          & fb.Eq(x => x.IsActive, true)
                          & fb.Eq(x => x.IsDeleted, false)
                          & fb.In(x => x.ParentAssignmentId, candidateIds);

        var parentIds = await _ctx.AssignmentListDocRoles
            .Find(childFilter)
            .Project(x => x.ParentAssignmentId!)
            .ToListAsync(ct);

        var parentIdSet = parentIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        return candidates
            .Where(x => parentIdSet.Contains(x.Id))
            .Select(ToListResponse)
            .ToList();
    }

    public async Task<List<WorkAssignmentListResponse>> GetMyParentCandidatesAsync(
        string workId,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        var work = await _lookup.LoadWorkAsync(workId, ct);

        if (!await EnsureAssignmentListDocRolesForUserWorkAsync(workId, actorUserId, ct))
            return new List<WorkAssignmentListResponse>();

        var fb = Builders<AssignmentListDocRole>.Filter;
        var filter = fb.Eq(x => x.WorkId, workId)
                     & fb.Eq(x => x.UserId, actorUserId)
                     & fb.Eq(x => x.IsActive, true)
                     & fb.Eq(x => x.IsDeleted, false);

        if (work.CreatedByUserId == actorUserId)
        {
            filter &= fb.AnyEq(x => x.Roles, DocRoleType.ASSIGNER)
                      & (fb.Eq(x => x.ParentAssignmentId, null) |
                         fb.Eq(x => x.AssignmentCreatedByUserId, actorUserId));
        }
        else
        {
            filter &= fb.AnyEq(x => x.Roles, DocRoleType.ASSIGNEE);
        }

        var items = await _ctx.AssignmentListDocRoles
            .Find(filter)
            .SortByDescending(x => x.AssignmentUpdatedAtUtc)
            .ThenBy(x => x.Path)
            .ToListAsync(ct);

        return items
            .Select(ToListResponse)
            .ToList();
    }

    public async Task<WorkAssignmentResponse?> GetByIdAsync(
        string id,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        var entity = await _ctx.WorkAssignments
            .Find(x =>
                x.Id == id &&
                x.CreatedByUserId == actorUserId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return null;

        var hasData = await _dataGuard.HasAssignmentDataAsync(entity.Id!, ct);
        await _docRoleReadModelFreshness.EnsureAssignmentFreshAsync(entity, actorUserId, ct);
        return ToDetailResponse(entity, hasData);
    }

    public async Task<List<WorkAssignmentListResponse>> GetChildrenAsync(
        string parentAssignmentId,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        var parent = await _ctx.WorkAssignments
            .Find(x =>
                x.Id == parentAssignmentId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (parent is null)
            throw new InvalidOperationException("Không tìm thấy assignment cha.");

        EnsureCanAccessBranch(parent, actorUserId);

        await EnsureAssignmentListDocRolesForUserWorkAsync(parent.WorkId, actorUserId, ct);

        var fb = Builders<AssignmentListDocRole>.Filter;
        var filter = fb.Eq(x => x.WorkId, parent.WorkId)
                     & fb.Eq(x => x.UserId, actorUserId)
                     & fb.Eq(x => x.ParentAssignmentId, parent.Id)
                     & fb.Eq(x => x.IsDeleted, false);

        var items = await _ctx.AssignmentListDocRoles
            .Find(filter)
            .SortByDescending(x => x.IsActive)
            .ThenByDescending(x => x.AssignmentUpdatedAtUtc)
            .ThenBy(x => x.Path)
            .ToListAsync(ct);

        return items
            .Select(ToListResponse)
            .ToList();
    }

    public async Task<WorkAssignmentResponse> CreateAsync(
        string workId,
        SaveWorkAssignmentRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        var work = await _lookup.LoadWorkAsync(workId, ct);

        var normalizedReq = WorkAssignmentScheduleHelper.NormalizeRequest(req);

        // Unit assignment is the only place where virtual units change meaning:
        // selected VU ids are expanded to real descendant units, then mapped to mu_* accounts.
        var unitManagerUserIds = await _unitSelection.ResolveUnitManagerUserIdsAsync(normalizedReq.AssigneeUnitIds, ct);
        if (unitManagerUserIds.Count > 0)
        {
            normalizedReq.AssigneeUserIds = normalizedReq.AssigneeUserIds
                .Concat(unitManagerUserIds)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        WorkAssignmentScheduleHelper.ValidateRequest(normalizedReq, work);

        WorkAssignment? parent = null;
        var isRootCreate = string.IsNullOrWhiteSpace(normalizedReq.ParentAssignmentId);

        if (isRootCreate)
        {
            if (!string.Equals(work.CreatedByUserId, actorUserId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Chỉ chủ sở hữu công việc mới được tạo assignment gốc.");
        }
        else
        {
            parent = await _ctx.WorkAssignments
                .Find(x =>
                    x.Id == normalizedReq.ParentAssignmentId &&
                    x.WorkId == workId &&
                    !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Không tìm thấy assignment cha.");

            EnsureCanAccessBranch(parent, actorUserId);
        }

        var template = await _templateResolver.ResolveAsync(
            normalizedReq.DynamicFormTemplateId,
            normalizedReq.DynamicExcelId,
            ct);

        var assignees = await WorkAssignmentUserHelper.BuildAssigneesAsync(_ctx, normalizedReq.AssigneeUserIds, ct);
        var leaderWatchers = await WorkAssignmentUserHelper.BuildLeaderWatchersAsync(
            _ctx,
            normalizedReq.LeaderWatcherUserIds ?? new List<string>(),
            assignees,
            ct);

        EnsureNoSelfAssignment(assignees.Select(x => x.UserId), actorUserId);

        var willBeActive = normalizedReq.IsActive ?? true;

        await EnsureNoActiveTemplateReuseConflictAsync(
            workId,
            template.DynamicFormTemplateId,
            template.DynamicExcelId,
            assignees.Select(x => x.UserId).ToList(),
            onlyWhenActive: willBeActive,
            ct: ct);

        var code = await _tree.GenerateAssignmentCodeAsync(workId, ct);
        var now = DateTime.UtcNow;

        var entity = new WorkAssignment
        {
            WorkId = workId,
            ParentAssignmentId = parent?.Id,
            RootAssignmentId = string.Empty,
            Level = parent is null ? 0 : parent.Level + 1,
            Code = code,
            Path = string.Empty,

            DynamicExcelId = template.DynamicExcelId,
            DynamicExcelCode = template.DynamicExcelCode,
            DynamicExcelName = template.DynamicExcelName,
            DynamicFormTemplateId = template.DynamicFormTemplateId,
            DynamicFormTemplateCode = template.DynamicFormTemplateCode,
            DynamicFormTemplateName = template.DynamicFormTemplateName,

            EvaluationTemplateId = string.IsNullOrWhiteSpace(work.EvaluationTemplateId) ? null : work.EvaluationTemplateId,
            EvaluationTemplateCode = string.IsNullOrWhiteSpace(work.EvaluationTemplateCode) ? null : work.EvaluationTemplateCode,
            EvaluationTemplateLabel = string.IsNullOrWhiteSpace(work.EvaluationTemplateLabel) ? null : work.EvaluationTemplateLabel,

            WorkType = work.Type.ToString(),
            AssignmentType = normalizedReq.AssignmentType,
            AggregationType = normalizedReq.AggregationType,
            Schedule = WorkAssignmentScheduleHelper.MapSchedule(normalizedReq.Schedule, normalizedReq.AssignmentType),

            Assignees = assignees,
            LeaderWatcherUserIds = leaderWatchers
                .Select(x => x.UserId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            LeaderWatchers = leaderWatchers,

            Description = normalizedReq.Description,
            IsActive = willBeActive,
            AllowUserCreatedReports = normalizedReq.AllowUserCreatedReports ?? true,

            ProgressStatus = 0,
            ProgressStatusUpdatedAtUtc = null,
            LatestPeriodKey = null,
            LatestDueAtUtc = null,
            HasAnyDuePeriod = false,
            HasOverduePeriod = false,

            HasManualEvaluations = false,
            EvaluatedAssignmentCount = 0,
            WorstEvaluationCode = null,
            WorstEvaluationLabel = null,

            IsDeleted = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            DueAtUtc = NormalizeDueDateUtc(normalizedReq.DueAtUtc),
        };

        await _ctx.WorkAssignments.InsertOneAsync(entity, cancellationToken: ct);

        if (parent is null)
        {
            entity.RootAssignmentId = entity.Id!;
            entity.Path = $"/{entity.Id}";

            await _ctx.WorkAssignments.UpdateOneAsync(
                x => x.Id == entity.Id && !x.IsDeleted,
                Builders<WorkAssignment>.Update
                    .Set(x => x.RootAssignmentId, entity.Id)
                    .Set(x => x.Path, entity.Path)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId),
                cancellationToken: ct);
        }
        else
        {
            entity.RootAssignmentId = !string.IsNullOrWhiteSpace(parent.RootAssignmentId)
                ? parent.RootAssignmentId!
                : parent.Id!;

            entity.Path = $"{parent.Path}/{entity.Id}";

            await _ctx.WorkAssignments.UpdateOneAsync(
                x => x.Id == entity.Id && !x.IsDeleted,
                Builders<WorkAssignment>.Update
                    .Set(x => x.RootAssignmentId, entity.RootAssignmentId)
                    .Set(x => x.Path, entity.Path)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId),
                cancellationToken: ct);
        }

        await _docRole.UpsertWorkAssignmentRolesAsync(entity, ct);
        await _docRole.RebuildWorkParticipantRolesFromAssignmentsAsync(workId, actorUserId, ct);

        await _binding.RebuildForAssignmentAsync(entity, actorUserId, ct);

        if (entity.IsActive)
        {
            await _materializeJob.EnqueueOrTouchAsync(entity, actorUserId, ct);
        }

        await _statusRepair.RebuildWorkTreeAsync(entity.WorkId, ct);
        await RebuildManualEvaluationTreeAsync(entity.WorkId, actorUserId, ct);

        return ToDetailResponse(entity, hasData: false);
    }

    public async Task<bool> DeactivateAsync(
        string id,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        var entity = await _ctx.WorkAssignments
            .Find(x =>
                x.Id == id &&
                x.CreatedByUserId == actorUserId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return false;

        if (!entity.IsActive)
            return true;

        var now = DateTime.UtcNow;

        var rs = await _ctx.WorkAssignments.UpdateOneAsync(
            x => x.Id == id &&
                 x.CreatedByUserId == actorUserId &&
                 !x.IsDeleted &&
                 x.IsActive,
            Builders<WorkAssignment>.Update
                .Set(x => x.IsActive, false)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        if (rs.ModifiedCount > 0)
        {
            await _docRoleReadModelProjection.RebuildAssignmentAsync(id, actorUserId, ct);
            await _binding.DisableByAssignmentAsync(id, actorUserId, ct);
            await _queueService.DisableByAssignmentAsync(id, actorUserId, ct);
            await _materializeJob.DisableByAssignmentIdAsync(id, actorUserId, ct);

            var periodIds = await _ctx.WorkReportPeriods
                .Find(x => x.WorkAssignmentId == id && !x.IsDeleted)
                .Project(x => x.Id)
                .ToListAsync(ct);

            await _ctx.WorkReportPeriods.UpdateManyAsync(
                x => x.WorkAssignmentId == id && !x.IsDeleted,
                Builders<WorkReportPeriod>.Update
                    .Set(x => x.IsActive, false)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId),
                cancellationToken: ct);

            foreach (var periodId in periodIds.Where(x => !string.IsNullOrWhiteSpace(x)))
                await _docRoleReadModelProjection.RebuildReportPeriodAsync(periodId, actorUserId, ct);

            await _statusRepair.RebuildWorkTreeAsync(entity.WorkId, ct);
            await RebuildManualEvaluationTreeAsync(entity.WorkId, actorUserId, ct);
        }

        return true;
    }

    public async Task<bool> ActivateAsync(
        string id,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        var entity = await _ctx.WorkAssignments
            .Find(x =>
                x.Id == id &&
                x.CreatedByUserId == actorUserId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return false;

        if (entity.IsActive)
            return true;

        var assigneeUserIds = (entity.Assignees ?? Enumerable.Empty<UserRef>())
            .Select(x => x.UserId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await EnsureNoActiveTemplateReuseConflictAsync(
            entity.WorkId,
            entity.DynamicFormTemplateId,
            entity.DynamicExcelId,
            assigneeUserIds,
            onlyWhenActive: true,
            ct: ct);

        var now = DateTime.UtcNow;

        var rs = await _ctx.WorkAssignments.UpdateOneAsync(
            x => x.Id == id &&
                 x.CreatedByUserId == actorUserId &&
                 !x.IsDeleted &&
                 !x.IsActive,
            Builders<WorkAssignment>.Update
                .Set(x => x.IsActive, true)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        if (rs.ModifiedCount > 0)
        {
            entity.IsActive = true;
            entity.UpdatedAtUtc = now;
            entity.UpdatedByUserId = actorUserId;

            await _docRole.UpsertWorkAssignmentRolesAsync(entity, ct);
            await _docRole.RebuildWorkParticipantRolesFromAssignmentsAsync(entity.WorkId, actorUserId, ct);
            await _binding.RebuildForAssignmentAsync(entity, actorUserId, ct);

            await _materializeJob.EnqueueOrTouchAsync(entity, actorUserId, ct);
            await _statusRepair.RebuildWorkTreeAsync(entity.WorkId, ct);
            await RebuildManualEvaluationTreeAsync(entity.WorkId, actorUserId, ct);
        }

        return true;
    }

    private void EnsureCanAccessBranch(WorkAssignment parent, string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            throw new UnauthorizedAccessException("Không xác định được người dùng.");

        var isOwner = string.Equals(parent.CreatedByUserId, actorUserId, StringComparison.Ordinal);
        var isDirectAssignee = parent.Assignees != null &&
                               parent.Assignees.Any(a => string.Equals(a.UserId, actorUserId, StringComparison.Ordinal));

        if (!isOwner && !isDirectAssignee)
            throw new UnauthorizedAccessException("Bạn không được tạo assignment dưới nhánh này.");
    }

    private async Task EnsureNoActiveTemplateReuseConflictAsync(
        string workId,
        string? dynamicFormTemplateId,
        string dynamicExcelId,
        List<string> assigneeUserIds,
        bool onlyWhenActive,
        CancellationToken ct)
    {
        if (!onlyWhenActive)
            return;

        var normalizedAssigneeIds = (assigneeUserIds ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalizedAssigneeIds.Count == 0)
            return;

        var f = Builders<WorkTemplateAssignee>.Filter;
        var filter = f.Eq(x => x.WorkId, workId)
                     & f.Eq(x => x.IsActive, true)
                     & f.Eq(x => x.IsDeleted, false)
                     & f.In(x => x.AssigneeUserId, normalizedAssigneeIds);

        filter &= !string.IsNullOrWhiteSpace(dynamicFormTemplateId)
            ? f.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId)
            : f.Eq(x => x.DynamicExcelId, dynamicExcelId);

        var bindings = await _ctx.WorkTemplateAssignees
            .Find(filter)
            .ToListAsync(ct);

        if (bindings.Count == 0)
            return;

        var conflictedAssignees = bindings
            .Select(x => string.IsNullOrWhiteSpace(x.AssigneeFullName)
                ? x.AssigneeUserId
                : $"{x.AssigneeFullName} ({x.AssigneeUserId})")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        throw new InvalidOperationException(
            $"Template đang có bản active khác trong cùng work cho assignee: {string.Join(", ", conflictedAssignees)}.");
    }

    private static void EnsureNoSelfAssignment(
        IEnumerable<string>? assigneeUserIds,
        string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            return;

        var normalized = (assigneeUserIds ?? Enumerable.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalized.Contains(actorUserId, StringComparer.Ordinal))
            throw new InvalidOperationException("Không được tự giao việc cho chính mình.");
    }

    private static void EnsureActor(string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            throw new UnauthorizedAccessException("Không xác định được người dùng thực hiện.");
    }

    private static DateTime? NormalizeDueDateUtc(DateTime? value)
    {
        if (!value.HasValue)
            return null;

        var utc = value.Value.Kind == DateTimeKind.Utc
            ? value.Value
            : value.Value.ToUniversalTime();

        return new DateTime(
            utc.Year,
            utc.Month,
            utc.Day,
            23,
            59,
            59,
            999,
            DateTimeKind.Utc);
    }

    private async Task<bool> EnsureAssignmentListDocRolesForUserWorkAsync(
        string workId,
        string actorUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workId) || string.IsNullOrWhiteSpace(actorUserId))
            return false;

        var hasProjectedRows = await _ctx.AssignmentListDocRoles
            .Find(x =>
                x.WorkId == workId &&
                x.UserId == actorUserId &&
                !x.IsDeleted)
            .AnyAsync(ct);

        if (hasProjectedRows)
            return true;

        _log.LogWarning(
            "Assignment list projection missing. workId={workId} actorUserId={actorUserId}. Returning current projection only; run internal DocRole repair/backfill if source data exists.",
            workId,
            actorUserId);

        return false;
    }


    private async Task RebuildManualEvaluationTreeAsync(string workId, string byUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workId))
            return;

        var assignments = await _ctx.WorkAssignments
            .Find(x => x.WorkId == workId && x.IsActive && !x.IsDeleted)
            .SortByDescending(x => x.Level)
            .ThenBy(x => x.Path)
            .ToListAsync(ct);

        if (assignments.Count == 0)
        {
            await RebuildWorkManualEvaluationAggregateAsync(workId, byUserId, ct);
            return;
        }

        var templateOrderMap = await BuildTemplateOrderMapAsync(assignments, ct);

        foreach (var assignment in assignments)
        {
            var children = assignments
                .Where(x => string.Equals(x.ParentAssignmentId, assignment.Id, StringComparison.Ordinal))
                .ToList();

            var aggregate = BuildAssignmentManualAggregate(assignment, children, templateOrderMap);

            await _ctx.WorkAssignments.UpdateOneAsync(
                x => x.Id == assignment.Id && !x.IsDeleted,
                Builders<WorkAssignment>.Update
                    .Set(x => x.HasManualEvaluations, aggregate.HasManualEvaluations)
                    .Set(x => x.EvaluatedAssignmentCount, aggregate.EvaluatedAssignmentCount)
                    .Set(x => x.WorstEvaluationCode, aggregate.WorstEvaluationCode)
                    .Set(x => x.WorstEvaluationLabel, aggregate.WorstEvaluationLabel),
                cancellationToken: ct);

            assignment.HasManualEvaluations = aggregate.HasManualEvaluations;
            assignment.EvaluatedAssignmentCount = aggregate.EvaluatedAssignmentCount;
            assignment.WorstEvaluationCode = aggregate.WorstEvaluationCode;
            assignment.WorstEvaluationLabel = aggregate.WorstEvaluationLabel;

            await _docRoleReadModelProjection.RebuildAssignmentAsync(assignment.Id, byUserId, ct);
        }

        await RebuildWorkManualEvaluationAggregateAsync(workId, byUserId, ct, assignments);
    }

    private async Task<Dictionary<string, Dictionary<string, int>>> BuildTemplateOrderMapAsync(
        List<WorkAssignment> assignments,
        CancellationToken ct)
    {
        var templateIds = assignments
            .Select(x => x.EvaluationTemplateId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (templateIds.Count == 0)
            return new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        var templates = await _ctx.EvaluationTemplates
            .Find(x => templateIds.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(ct);

        return templates.ToDictionary(
            t => t.Id,
            t => (t.Items ?? new List<EvaluationTemplateItem>())
                .Where(i => !string.IsNullOrWhiteSpace(i.Code))
                .ToDictionary(
                    i => i.Code,
                    i => i.Order,
                    StringComparer.OrdinalIgnoreCase),
            StringComparer.Ordinal);
    }

    private ManualEvaluationAggregate BuildAssignmentManualAggregate(
        WorkAssignment assignment,
        List<WorkAssignment> children,
        Dictionary<string, Dictionary<string, int>> templateOrderMap)
    {
        if (children.Count == 0)
        {
            var hasOwn = !string.IsNullOrWhiteSpace(assignment.EvaluationCode);

            return new ManualEvaluationAggregate
            {
                HasManualEvaluations = hasOwn,
                EvaluatedAssignmentCount = hasOwn ? 1 : 0,
                WorstEvaluationCode = hasOwn ? assignment.EvaluationCode : null,
                WorstEvaluationLabel = hasOwn ? assignment.EvaluationLabel : null
            };
        }

        var evaluatedCount = children.Sum(x => x.EvaluatedAssignmentCount);
        var hasManual = evaluatedCount > 0 || children.Any(x => x.HasManualEvaluations);

        var options = children
            .Where(x => !string.IsNullOrWhiteSpace(x.WorstEvaluationCode))
            .Select(x => new ManualEvaluationChoice(
                x.WorstEvaluationCode!,
                x.WorstEvaluationLabel,
                ResolveEvaluationOrder(x.EvaluationTemplateId, x.WorstEvaluationCode!, templateOrderMap)))
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var worst = options.FirstOrDefault();

        return new ManualEvaluationAggregate
        {
            HasManualEvaluations = hasManual,
            EvaluatedAssignmentCount = evaluatedCount,
            WorstEvaluationCode = worst?.Code,
            WorstEvaluationLabel = worst?.Label
        };
    }

    private async Task RebuildWorkManualEvaluationAggregateAsync(
        string workId,
        string byUserId,
        CancellationToken ct,
        List<WorkAssignment>? preloadedAssignments = null)
    {
        var work = await _ctx.Works
            .Find(x => x.Id == workId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (work is null)
            return;

        var assignments = preloadedAssignments ?? await _ctx.WorkAssignments
            .Find(x => x.WorkId == workId && x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        var roots = assignments
            .Where(x => string.IsNullOrWhiteSpace(x.ParentAssignmentId))
            .ToList();

        var evaluatedCount = roots.Sum(x => x.EvaluatedAssignmentCount);
        var hasManual = evaluatedCount > 0 || roots.Any(x => x.HasManualEvaluations);

        Dictionary<string, int> orderMap = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(work.EvaluationTemplateId))
        {
            var template = await _ctx.EvaluationTemplates
                .Find(x => x.Id == work.EvaluationTemplateId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);

            if (template is not null)
            {
                orderMap = (template.Items ?? new List<EvaluationTemplateItem>())
                    .Where(i => !string.IsNullOrWhiteSpace(i.Code))
                    .ToDictionary(i => i.Code, i => i.Order, StringComparer.OrdinalIgnoreCase);
            }
        }

        var worst = roots
            .Where(x => !string.IsNullOrWhiteSpace(x.WorstEvaluationCode))
            .Select(x => new ManualEvaluationChoice(
                x.WorstEvaluationCode!,
                x.WorstEvaluationLabel,
                orderMap.TryGetValue(x.WorstEvaluationCode!, out var order) ? order : int.MaxValue))
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        await _ctx.Works.UpdateOneAsync(
            x => x.Id == workId && !x.IsDeleted,
            Builders<Work>.Update
                .Set(x => x.HasManualEvaluations, hasManual)
                .Set(x => x.EvaluatedAssignmentCount, evaluatedCount)
                .Set(x => x.WorstEvaluationCode, worst?.Code)
                .Set(x => x.WorstEvaluationLabel, worst?.Label),
            cancellationToken: ct);

        await _docRoleReadModelProjection.RebuildWorkAsync(workId, byUserId, ct);
    }

    private static int ResolveEvaluationOrder(
        string? templateId,
        string code,
        Dictionary<string, Dictionary<string, int>> templateOrderMap)
    {
        if (!string.IsNullOrWhiteSpace(templateId) &&
            templateOrderMap.TryGetValue(templateId, out var orderMap) &&
            orderMap.TryGetValue(code, out var order))
            return order;

        return int.MaxValue;
    }

    private static WorkAssignmentListResponse ToListResponse(WorkAssignment entity)
    {
        var detail = WorkAssignmentResponseMapper.ToResponse(entity, hasData: false);

        return new WorkAssignmentListResponse
        {
            Id = detail.Id,
            WorkId = detail.WorkId,

            DynamicExcelId = detail.DynamicExcelId,
            DynamicExcelCode = detail.DynamicExcelCode,
            DynamicExcelName = detail.DynamicExcelName,
            DynamicFormTemplateId = detail.DynamicFormTemplateId,
            DynamicFormTemplateCode = detail.DynamicFormTemplateCode,
            DynamicFormTemplateName = detail.DynamicFormTemplateName,

            AssignmentType = detail.AssignmentType,
            AggregationType = detail.AggregationType,

            Assignees = detail.Assignees ?? new(),
            LeaderWatchers = detail.LeaderWatchers ?? new(),

            Description = detail.Description,
            IsActive = detail.IsActive,
            AllowUserCreatedReports = detail.AllowUserCreatedReports,

            ProgressStatus = entity.ProgressStatus,
            ProgressStatusUpdatedAtUtc = entity.ProgressStatusUpdatedAtUtc,
            LatestPeriodKey = entity.LatestPeriodKey,
            LatestDueAtUtc = entity.LatestDueAtUtc,
            HasAnyDuePeriod = entity.HasAnyDuePeriod,
            HasOverduePeriod = entity.HasOverduePeriod,

            CreatedAtUtc = detail.CreatedAtUtc,
            UpdatedAtUtc = detail.UpdatedAtUtc,

            ParentAssignmentId = detail.ParentAssignmentId,
            RootAssignmentId = detail.RootAssignmentId,
            Level = detail.Level,
            Code = detail.Code,
            Path = detail.Path,
            EvaluationTemplateId = entity.EvaluationTemplateId,
            EvaluationTemplateCode = entity.EvaluationTemplateCode,
            EvaluationTemplateLabel = entity.EvaluationTemplateLabel,
            EvaluationCode = entity.EvaluationCode,
            EvaluationLabel = entity.EvaluationLabel,

            HasManualEvaluations = entity.HasManualEvaluations,
            EvaluatedAssignmentCount = entity.EvaluatedAssignmentCount,
            WorstEvaluationCode = entity.WorstEvaluationCode,
            WorstEvaluationLabel = entity.WorstEvaluationLabel,

            WorstPeriodStatus = entity.WorstPeriodStatus,
            WorstOverdueReasonCode = entity.WorstOverdueReasonCode,
            WorstOverdueReasonLabel = entity.WorstOverdueReasonLabel,
            DueAtUtc = entity.DueAtUtc,
        };
    }

    private static WorkAssignmentListResponse ToListResponse(AssignmentListDocRole entity)
    {
        return new WorkAssignmentListResponse
        {
            Id = entity.AssignmentId,
            WorkId = entity.WorkId,

            DynamicExcelId = entity.DynamicExcelId,
            DynamicExcelCode = entity.DynamicExcelCode,
            DynamicExcelName = entity.DynamicExcelName,
            DynamicFormTemplateId = entity.DynamicFormTemplateId,
            DynamicFormTemplateCode = entity.DynamicFormTemplateCode,
            DynamicFormTemplateName = entity.DynamicFormTemplateName,

            AssignmentType = entity.AssignmentType,
            AggregationType = entity.AggregationType,

            Assignees = (entity.Assignees ?? new List<UserRef>()).Select(ToUserRefDto).ToList(),
            LeaderWatchers = (entity.LeaderWatchers ?? new List<UserRef>()).Select(ToUserRefDto).ToList(),

            Description = entity.Description,
            IsActive = entity.IsActive,
            AllowUserCreatedReports = entity.AllowUserCreatedReports,

            ProgressStatus = entity.ProgressStatus,
            ProgressStatusUpdatedAtUtc = entity.ProgressStatusUpdatedAtUtc,
            LatestPeriodKey = entity.LatestPeriodKey,
            LatestDueAtUtc = entity.LatestDueAtUtc,
            HasAnyDuePeriod = entity.HasAnyDuePeriod,
            HasOverduePeriod = entity.HasOverduePeriod,

            CreatedAtUtc = entity.AssignmentCreatedAtUtc,
            UpdatedAtUtc = entity.AssignmentUpdatedAtUtc,

            ParentAssignmentId = entity.ParentAssignmentId,
            RootAssignmentId = entity.RootAssignmentId ?? string.Empty,
            Level = entity.Level,
            Code = entity.Code,
            Path = entity.Path,
            EvaluationTemplateId = entity.EvaluationTemplateId,
            EvaluationTemplateCode = entity.EvaluationTemplateCode,
            EvaluationTemplateLabel = entity.EvaluationTemplateLabel,
            EvaluationCode = entity.EvaluationCode,
            EvaluationLabel = entity.EvaluationLabel,

            HasManualEvaluations = entity.HasManualEvaluations,
            EvaluatedAssignmentCount = entity.EvaluatedAssignmentCount,
            WorstEvaluationCode = entity.WorstEvaluationCode,
            WorstEvaluationLabel = entity.WorstEvaluationLabel,

            WorstPeriodStatus = entity.WorstPeriodStatus,
            WorstOverdueReasonCode = entity.WorstOverdueReasonCode,
            WorstOverdueReasonLabel = entity.WorstOverdueReasonLabel,
            DueAtUtc = entity.DueAtUtc,
        };
    }

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

    private static WorkAssignmentResponse ToDetailResponse(WorkAssignment entity, bool hasData)
    {
        var detail = WorkAssignmentResponseMapper.ToResponse(entity, hasData);

        detail.ProgressStatus = entity.ProgressStatus;
        detail.ProgressStatusUpdatedAtUtc = entity.ProgressStatusUpdatedAtUtc;
        detail.LatestPeriodKey = entity.LatestPeriodKey;
        detail.LatestDueAtUtc = entity.LatestDueAtUtc;
        detail.HasAnyDuePeriod = entity.HasAnyDuePeriod;
        detail.HasOverduePeriod = entity.HasOverduePeriod;
        detail.EvaluationTemplateId = entity.EvaluationTemplateId;
        detail.EvaluationTemplateCode = entity.EvaluationTemplateCode;
        detail.EvaluationTemplateLabel = entity.EvaluationTemplateLabel;
        detail.EvaluationCode = entity.EvaluationCode;
        detail.EvaluationLabel = entity.EvaluationLabel;
        detail.EvaluationNote = entity.EvaluationNote;
        detail.EvaluatedAtUtc = entity.EvaluatedAtUtc;
        detail.EvaluatedByUserId = entity.EvaluatedByUserId;

        detail.HasManualEvaluations = entity.HasManualEvaluations;
        detail.EvaluatedAssignmentCount = entity.EvaluatedAssignmentCount;
        detail.WorstEvaluationCode = entity.WorstEvaluationCode;
        detail.WorstEvaluationLabel = entity.WorstEvaluationLabel;

        detail.WorstPeriodStatus = entity.WorstPeriodStatus;
        detail.WorstOverdueReasonCode = entity.WorstOverdueReasonCode;
        detail.WorstOverdueReasonLabel = entity.WorstOverdueReasonLabel;
        detail.DueAtUtc = entity.DueAtUtc;
        return detail;
    }

    private sealed class ManualEvaluationAggregate
    {
        public bool HasManualEvaluations { get; set; }
        public int EvaluatedAssignmentCount { get; set; }
        public string? WorstEvaluationCode { get; set; }
        public string? WorstEvaluationLabel { get; set; }
    }

    private sealed record ManualEvaluationChoice(string Code, string? Label, int Order);
}
