using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Runtime;

public sealed class WorkTemplateAssigneeBindingService : IWorkTemplateAssigneeBindingService
{
    private readonly MongoDbContext _ctx;

    public WorkTemplateAssigneeBindingService(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task RebuildForAssignmentAsync(WorkAssignment assignment, string actorUserId, CancellationToken ct = default)
    {
        if (assignment is null)
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_NODE_INVALID, new { field = nameof(assignment) });

        var now = DateTime.UtcNow;
        var activeAssigneeIds = (assignment.Assignees ?? new List<UserRef>())
            .Where(x => !string.IsNullOrWhiteSpace(x.UserId))
            .Select(x => x.UserId)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var existing = await _ctx.WorkTemplateAssignees
            .Find(x => x.WorkAssignmentId == assignment.Id && !x.IsDeleted)
            .ToListAsync(ct);

        foreach (var row in existing)
        {
            if (!activeAssigneeIds.Contains(row.AssigneeUserId))
            {
                var disable = Builders<WorkTemplateAssignee>.Update
                    .Set(x => x.IsActive, false)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId);

                await _ctx.WorkTemplateAssignees.UpdateOneAsync(
                    x => x.Id == row.Id && !x.IsDeleted,
                    disable,
                    cancellationToken: ct);
            }
        }

        foreach (var assignee in assignment.Assignees ?? new List<UserRef>())
        {
            if (string.IsNullOrWhiteSpace(assignee.UserId))
                continue;

            var update = Builders<WorkTemplateAssignee>.Update
                .SetOnInsert(x => x.CreatedAtUtc, now)
                .SetOnInsert(x => x.CreatedByUserId, actorUserId)
                .Set(x => x.WorkId, assignment.WorkId)
                .Set(x => x.WorkAssignmentId, assignment.Id)
                .Set(x => x.DynamicExcelId, assignment.DynamicExcelId)
                .Set(x => x.DynamicExcelCode, assignment.DynamicExcelCode)
                .Set(x => x.DynamicExcelName, assignment.DynamicExcelName)
                .Set(x => x.DynamicFormTemplateId, assignment.DynamicFormTemplateId)
                .Set(x => x.DynamicFormTemplateCode, assignment.DynamicFormTemplateCode)
                .Set(x => x.DynamicFormTemplateName, assignment.DynamicFormTemplateName)
                .Set(x => x.DynamicFormDataSourceRulesJson, assignment.DynamicFormDataSourceRulesJson)
                .Set(x => x.AssigneeUserId, assignee.UserId)
                .Set(x => x.AssigneeUsername, assignee.Username ?? string.Empty)
                .Set(x => x.AssigneeFullName, assignee.FullName ?? string.Empty)
                .Set(x => x.AssigneeUnitId, assignee.UnitId)
                .Set(x => x.AssigneeUnitSymbol, assignee.UnitSymbol)
                .Set(x => x.AssigneeUnitShortName, assignee.UnitShortName)
                .Set(x => x.AssigneeUnitName, assignee.UnitName)
                .Set(x => x.AssignmentType, assignment.AssignmentType)
                .Set(x => x.AggregationType, assignment.AggregationType)
                .Set(x => x.Schedule, assignment.Schedule)
                .Set(x => x.StartDate, assignment.StartDate)
                .Set(x => x.CompletedDate, assignment.CompletedDate)
                .Set(x => x.AllowUserCreatedReports, true)
                .Set(x => x.IsActive, assignment.IsActive)
                .Set(x => x.IsDeleted, false)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId);

            await _ctx.WorkTemplateAssignees.UpdateOneAsync(
                x => x.WorkAssignmentId == assignment.Id &&
                     x.AssigneeUserId == assignee.UserId &&
                     !x.IsDeleted,
                update,
                new UpdateOptions { IsUpsert = true },
                ct);
        }
    }

    public async Task DisableByAssignmentAsync(string workAssignmentId, string actorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workAssignmentId))
            return;

        var now = DateTime.UtcNow;

        var update = Builders<WorkTemplateAssignee>.Update
            .Set(x => x.IsActive, false)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);

        await _ctx.WorkTemplateAssignees.UpdateManyAsync(
            x => x.WorkAssignmentId == workAssignmentId && !x.IsDeleted,
            update,
            cancellationToken: ct);
    }

    public async Task<List<WorkTemplateAssignee>> GetActiveByWorkAndAssigneeAsync(
        string workId,
        string assigneeUserId,
        CancellationToken ct = default)
    {
        return await _ctx.WorkTemplateAssignees
            .Find(x => x.WorkId == workId &&
                       x.AssigneeUserId == assigneeUserId &&
                       x.IsActive &&
                       !x.IsDeleted)
            .SortByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<List<WorkTemplateAssignee>> GetActiveByWorkTemplateAndAssigneeAsync(
        string workId,
        string dynamicExcelId,
        string assigneeUserId,
        CancellationToken ct = default)
    {
        return await _ctx.WorkTemplateAssignees
            .Find(x => x.WorkId == workId &&
                       x.DynamicExcelId == dynamicExcelId &&
                       x.AssigneeUserId == assigneeUserId &&
                       x.IsActive &&
                       !x.IsDeleted)
            .SortByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(ct);
    }
}
