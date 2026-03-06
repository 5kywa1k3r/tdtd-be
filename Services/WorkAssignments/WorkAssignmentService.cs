using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignments.Domain;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments.Lookups;

namespace tdtd_be.Services.WorkAssignments;

public interface IWorkAssignmentService
{
    Task<List<WorkAssignmentResponse>> GetByWorkIdAsync(string workId, CancellationToken ct = default);
    Task<WorkAssignmentResponse?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<List<WorkAssignmentResponse>> GetChildrenAsync(string parentAssignmentId, CancellationToken ct = default);
    Task<List<WorkAssignmentResponse>> GetByDynamicExcelAsync(string workId, string dynamicExcelId, CancellationToken ct = default);
    Task<List<WorkAssignmentResponse>> GetChildrenByDynamicExcelAsync(string parentAssignmentId, string dynamicExcelId, CancellationToken ct = default);
    Task<WorkAssignmentResponse> CreateAsync(string workId, SaveWorkAssignmentRequest req, string actorUserId, CancellationToken ct = default);
    Task<WorkAssignmentResponse> UpdateAsync(string id, SaveWorkAssignmentRequest req, string actorUserId, CancellationToken ct = default);
    Task<bool> SoftDeleteAsync(string id, string actorUserId, CancellationToken ct = default);
}

public sealed class WorkAssignmentService : IWorkAssignmentService
{
    private readonly MongoDbContext _ctx;
    private readonly IDocRoleService _docRole;
    private readonly IWorkAssignmentLookupService _lookup;
    private readonly IDynamicExcelLookupService _dynamicExcelLookup;
    private readonly IWorkAssignmentDataGuardService _dataGuard;
    private readonly IWorkAssignmentTreeService _tree;

    public WorkAssignmentService(
        MongoDbContext ctx,
        IDocRoleService docRole,
        IWorkAssignmentLookupService lookup,
        IDynamicExcelLookupService dynamicExcelLookup,
        IWorkAssignmentDataGuardService dataGuard,
        IWorkAssignmentTreeService tree)
    {
        _ctx = ctx;
        _docRole = docRole;
        _lookup = lookup;
        _dynamicExcelLookup = dynamicExcelLookup;
        _dataGuard = dataGuard;
        _tree = tree;
    }

    public async Task<List<WorkAssignmentResponse>> GetByWorkIdAsync(string workId, CancellationToken ct = default)
    {
        var items = await _ctx.WorkAssignments
            .Find(x => x.WorkId == workId && !x.IsDeleted)
            .SortBy(x => x.RootAssignmentId)
            .ThenBy(x => x.Path)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(ct);

        return await BuildResponseListAsync(items, ct);
    }

    public async Task<WorkAssignmentResponse?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var entity = await _ctx.WorkAssignments
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null) return null;

        var hasData = await _dataGuard.HasAssignmentDataAsync(entity.Id!, ct);
        return WorkAssignmentResponseMapper.ToResponse(entity, hasData);
    }

    public async Task<List<WorkAssignmentResponse>> GetChildrenAsync(string parentAssignmentId, CancellationToken ct = default)
    {
        await _lookup.EnsureParentExistsAsync(parentAssignmentId, ct);

        var items = await _ctx.WorkAssignments
            .Find(x => x.ParentAssignmentId == parentAssignmentId && !x.IsDeleted)
            .SortBy(x => x.Path)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(ct);

        return await BuildResponseListAsync(items, ct);
    }

    public async Task<List<WorkAssignmentResponse>> GetByDynamicExcelAsync(
        string workId,
        string dynamicExcelId,
        CancellationToken ct = default)
    {
        var items = await _ctx.WorkAssignments
            .Find(x => x.WorkId == workId && x.DynamicExcelId == dynamicExcelId && !x.IsDeleted)
            .SortBy(x => x.RootAssignmentId)
            .ThenBy(x => x.Path)
            .ToListAsync(ct);

        return await BuildResponseListAsync(items, ct);
    }

    public async Task<List<WorkAssignmentResponse>> GetChildrenByDynamicExcelAsync(
        string parentAssignmentId,
        string dynamicExcelId,
        CancellationToken ct = default)
    {
        await _lookup.EnsureParentExistsAsync(parentAssignmentId, ct);

        var items = await _ctx.WorkAssignments
            .Find(x => x.ParentAssignmentId == parentAssignmentId && x.DynamicExcelId == dynamicExcelId && !x.IsDeleted)
            .SortBy(x => x.Path)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(ct);

        return await BuildResponseListAsync(items, ct);
    }

    public async Task<WorkAssignmentResponse> CreateAsync(
        string workId,
        SaveWorkAssignmentRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        var work = await _lookup.LoadWorkAsync(workId, ct);

        var normalizedReq = WorkAssignmentScheduleHelper.NormalizeRequest(req);
        WorkAssignmentScheduleHelper.ValidateRequest(normalizedReq, work);

        var parent = await _lookup.LoadParentAsync(normalizedReq.ParentAssignmentId, workId, ct);
        var dynamicExcel = await _dynamicExcelLookup.LoadAsync(normalizedReq.DynamicExcelId, ct);

        var assignees = await WorkAssignmentUserHelper.BuildAssigneesAsync(_ctx, normalizedReq.AssigneeUserIds, ct);
        var leaderWatchers = await WorkAssignmentUserHelper.BuildLeaderWatchersAsync(_ctx, normalizedReq.LeaderWatcherUserIds, assignees, ct);

        await _tree.EnsureNoDuplicateAssignmentAsync(
            workId,
            parent?.Id,
            normalizedReq.DynamicExcelId,
            assignees.Select(x => x.UserId).ToList(),
            null,
            ct);

        var code = await _tree.GenerateAssignmentCodeAsync(workId, ct);
        var now = DateTime.UtcNow;

        var entity = new WorkAssignment
        {
            WorkId = workId,
            ParentAssignmentId = parent?.Id,
            RootAssignmentId = string.Empty,
            Level = parent is null ? 0 : parent.Level + 1,
            Code = code,
            Path = parent is null ? $"/{code}" : $"{parent.Path}/{code}",

            DynamicExcelId = normalizedReq.DynamicExcelId,
            DynamicExcelCode = dynamicExcel.Code,
            DynamicExcelName = dynamicExcel.Name,

            WorkType = work.Type.ToString(),
            AssignmentType = normalizedReq.AssignmentType,
            AggregationType = normalizedReq.AggregationType,
            Schedule = WorkAssignmentScheduleHelper.MapSchedule(normalizedReq.Schedule, normalizedReq.AssignmentType),

            Assignees = assignees,
            LeaderWatcherUserIds = leaderWatchers.Select(x => x.UserId).Distinct(StringComparer.Ordinal).ToList(),
            LeaderWatchers = leaderWatchers,

            Description = normalizedReq.Description,
            IsActive = normalizedReq.IsActive ?? true,

            IsDeleted = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId
        };

        await _ctx.WorkAssignments.InsertOneAsync(entity, cancellationToken: ct);

        if (parent is null)
        {
            entity.RootAssignmentId = entity.Id!;
            await _ctx.WorkAssignments.UpdateOneAsync(
                x => x.Id == entity.Id && !x.IsDeleted,
                Builders<WorkAssignment>.Update.Set(x => x.RootAssignmentId, entity.Id),
                cancellationToken: ct);
        }
        else
        {
            entity.RootAssignmentId = parent.RootAssignmentId!;
        }

        await _docRole.UpsertWorkAssignmentRolesAsync(entity, ct);
        await _docRole.RebuildWorkParticipantRolesFromAssignmentsAsync(workId, actorUserId, ct);

        return WorkAssignmentResponseMapper.ToResponse(entity, false);
    }

    public async Task<WorkAssignmentResponse> UpdateAsync(
        string id,
        SaveWorkAssignmentRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        var entity = await _lookup.LoadAssignmentAsync(id, ct);
        var work = await _lookup.LoadWorkAsync(entity.WorkId, ct);

        var normalizedReq = WorkAssignmentScheduleHelper.NormalizeRequest(req);
        WorkAssignmentScheduleHelper.ValidateRequest(normalizedReq, work);

        var hasData = await _dataGuard.HasAssignmentDataAsync(entity.Id!, ct);

        if (hasData &&
            !string.Equals(entity.DynamicExcelId, normalizedReq.DynamicExcelId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Assignment đã phát sinh dữ liệu, không được đổi biểu mẫu.");
        }

        if (!string.Equals(normalizedReq.ParentAssignmentId, entity.ParentAssignmentId, StringComparison.Ordinal))
        {
            var hasChildren = await _ctx.WorkAssignments
                .Find(x => x.ParentAssignmentId == entity.Id && !x.IsDeleted)
                .AnyAsync(ct);

            if (hasChildren)
                throw new InvalidOperationException("Không được đổi nhánh cha khi assignment đã có node con.");
        }

        var parent = await _lookup.LoadParentAsync(normalizedReq.ParentAssignmentId, entity.WorkId, ct);

        var dynamicExcel = string.Equals(entity.DynamicExcelId, normalizedReq.DynamicExcelId, StringComparison.Ordinal)
            ? (entity.DynamicExcelCode, entity.DynamicExcelName)
            : await _dynamicExcelLookup.LoadAsync(normalizedReq.DynamicExcelId, ct);

        var assignees = await WorkAssignmentUserHelper.BuildAssigneesAsync(_ctx, normalizedReq.AssigneeUserIds, ct);
        var leaderWatchers = await WorkAssignmentUserHelper.BuildLeaderWatchersAsync(_ctx, normalizedReq.LeaderWatcherUserIds, assignees, ct);

        await _tree.EnsureNoDuplicateAssignmentAsync(
            entity.WorkId,
            parent?.Id,
            normalizedReq.DynamicExcelId,
            assignees.Select(x => x.UserId).ToList(),
            entity.Id,
            ct);

        var oldPath = entity.Path;
        var oldRootAssignmentId = entity.RootAssignmentId;

        entity.ParentAssignmentId = parent?.Id;
        entity.RootAssignmentId = parent?.RootAssignmentId ?? entity.Id!;
        entity.Level = parent is null ? 0 : parent.Level + 1;
        entity.Path = parent is null ? $"/{entity.Code}" : $"{parent.Path}/{entity.Code}";

        entity.DynamicExcelId = normalizedReq.DynamicExcelId;
        entity.DynamicExcelCode = dynamicExcel.Item1;
        entity.DynamicExcelName = dynamicExcel.Item2;

        entity.AssignmentType = normalizedReq.AssignmentType;
        entity.AggregationType = normalizedReq.AggregationType;
        entity.Schedule = WorkAssignmentScheduleHelper.MapSchedule(normalizedReq.Schedule, normalizedReq.AssignmentType);

        entity.Assignees = assignees;
        entity.LeaderWatcherUserIds = leaderWatchers.Select(x => x.UserId).Distinct(StringComparer.Ordinal).ToList();
        entity.LeaderWatchers = leaderWatchers;

        entity.Description = normalizedReq.Description;
        entity.IsActive = normalizedReq.IsActive ?? entity.IsActive;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        entity.UpdatedByUserId = actorUserId;

        await _ctx.WorkAssignments.ReplaceOneAsync(
            x => x.Id == entity.Id && !x.IsDeleted,
            entity,
            cancellationToken: ct);

        if (!string.Equals(oldPath, entity.Path, StringComparison.Ordinal) ||
            !string.Equals(oldRootAssignmentId, entity.RootAssignmentId, StringComparison.Ordinal))
        {
            await _tree.RebuildDescendantPathAsync(entity, oldPath, oldRootAssignmentId, ct);
        }

        await _docRole.UpsertWorkAssignmentRolesAsync(entity, ct);
        await _docRole.RebuildWorkParticipantRolesFromAssignmentsAsync(entity.WorkId, actorUserId, ct);

        return WorkAssignmentResponseMapper.ToResponse(entity, hasData);
    }

    public async Task<bool> SoftDeleteAsync(string id, string actorUserId, CancellationToken ct = default)
    {
        var entity = await _ctx.WorkAssignments
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return false;

        var hasChildren = await _ctx.WorkAssignments
            .Find(x => x.ParentAssignmentId == id && !x.IsDeleted)
            .AnyAsync(ct);

        if (hasChildren)
            throw new InvalidOperationException("Không được xóa assignment khi vẫn còn node con hoạt động.");

        var now = DateTime.UtcNow;

        var update = Builders<WorkAssignment>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.IsActive, false)
            .Set(x => x.DeletedAtUtc, now)
            .Set(x => x.DeletedByUserId, actorUserId)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);

        var rs = await _ctx.WorkAssignments.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (rs.ModifiedCount > 0)
        {
            await _docRole.DeleteDocRolesAsync(DocType.WORK_ASSIGNMENT, id, actorUserId, ct);
            await _docRole.RebuildWorkParticipantRolesFromAssignmentsAsync(entity.WorkId, actorUserId, ct);
        }

        return rs.ModifiedCount > 0;
    }

    private async Task<List<WorkAssignmentResponse>> BuildResponseListAsync(
        List<WorkAssignment> items,
        CancellationToken ct)
    {
        var ids = items
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .Select(x => x.Id!)
            .ToList();

        var idsHavingData = await _dataGuard.GetAssignmentIdsHavingDataAsync(ids, ct);

        return items
            .Select(x => WorkAssignmentResponseMapper.ToResponse(x, idsHavingData.Contains(x.Id!)))
            .ToList();
    }
}