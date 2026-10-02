using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Runtime;

public sealed class WorkTemplateAssigneeBindingService : IWorkTemplateAssigneeBindingService
{
    private readonly MongoDbContext _ctx;
    private readonly IDynamicFlowDefinitionTransactionRunner _transactions;

    public WorkTemplateAssigneeBindingService(MongoDbContext ctx, IDynamicFlowDefinitionTransactionRunner transactions)
    {
        _ctx = ctx;
        _transactions = transactions;
    }

    public async Task RebuildForAssignmentAsync(WorkAssignment assignment, string actorUserId, CancellationToken ct = default)
    {
        if (assignment is null)
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_NODE_INVALID, new { field = nameof(assignment) });

        await _transactions.ExecuteAsync(async (session, transactionCt) =>
        {
            var current = await _ctx.WorkAssignments.Find(session, a => a.Id == assignment.Id && !a.IsDeleted
                && a.UpdatedAtUtc == assignment.UpdatedAtUtc).FirstOrDefaultAsync(transactionCt)
                ?? throw new tdtd_be.Services.AggregateMapping.AggregatePreviewException("AGG_BINDING_ASSIGNMENT_STALE");
            var now = DateTime.UtcNow;
            var activeAssigneeIds = (assignment.Assignees ?? new List<UserRef>())
                .Where(x => !string.IsNullOrWhiteSpace(x.UserId))
                .Select(x => x.UserId)
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);

            var existing = await _ctx.WorkTemplateAssignees
                .Find(session, x => x.WorkAssignmentId == assignment.Id && !x.IsDeleted)
                .ToListAsync(transactionCt);
            var selectionChanged = existing.Any(b => b.DynamicFormTemplateId != current.DynamicFormTemplateId
                || b.DynamicFormSchemaHash != current.DynamicFormSchemaHash || b.AssignmentType != current.AssignmentType
                || b.StartDate != current.StartDate || b.DueDate != current.DueDate
                || AggregateCanonical.Hash(b.Schedule) != AggregateCanonical.Hash(current.Schedule));
            await AggregateHostIntegration.RelationshipAsync(_ctx, session, current.WorkId, [current.Id!],
                "binding:" + current.Id + ":" + current.UpdatedAtUtc.Ticks, transactionCt, preserveIdentity: !selectionChanged);

            foreach (var row in existing)
            {
                if (!activeAssigneeIds.Contains(row.AssigneeUserId))
                {
                    var disable = Builders<WorkTemplateAssignee>.Update
                        .Set(x => x.IsActive, false)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId);

                    await _ctx.WorkTemplateAssignees.UpdateOneAsync(
                        session,
                        x => x.Id == row.Id && !x.IsDeleted,
                        disable,
                        cancellationToken: transactionCt);
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
                    .Set(x => x.DynamicFormFamilyId, assignment.DynamicFormFamilyId)
                    .Set(x => x.DynamicFormVersionNo, assignment.DynamicFormVersionNo)
                    .Set(x => x.DynamicFormSchemaHash, assignment.DynamicFormSchemaHash)
                    .Set(x => x.DynamicFormDataSourceRulesJson, assignment.DynamicFormDataSourceRulesJson)
                    .Set(x => x.AutoApproveConditionJson, assignment.AutoApproveConditionJson)
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
                    .Set(x => x.DueDate, assignment.DueDate)
                    .Set(x => x.CompletedDate, assignment.CompletedDate)
                    .Set(x => x.IsActive, assignment.IsActive)
                    .Set(x => x.IsDeleted, false)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actorUserId);

                await _ctx.WorkTemplateAssignees.UpdateOneAsync(
                    session,
                    x => x.WorkAssignmentId == assignment.Id &&
                         x.AssigneeUserId == assignee.UserId &&
                         !x.IsDeleted,
                    update,
                    new UpdateOptions { IsUpsert = true },
                    transactionCt);
            }
            await WorkDirectSourceRevisionFence.IncrementAsync(_ctx, session, current.WorkId, transactionCt);
        }, ct);
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

        await _transactions.ExecuteAsync(async (session, token) =>
        {
            var assignment = await _ctx.WorkAssignments.Find(session, a => a.Id == workAssignmentId && !a.IsDeleted).FirstOrDefaultAsync(token);
            if (assignment == null) return;
            // Inactive assignment keeps approved report identities and frozen evidence (SRC06).
            await AggregateHostIntegration.RelationshipAsync(_ctx, session, assignment.WorkId, [workAssignmentId],
                "binding-disable:" + assignment.Id + ":" + assignment.UpdatedAtUtc.Ticks, token, preserveIdentity: true);
            await _ctx.WorkTemplateAssignees.UpdateManyAsync(session,
                x => x.WorkAssignmentId == workAssignmentId && !x.IsDeleted,
                update,
                cancellationToken: token);
            await WorkDirectSourceRevisionFence.IncrementAsync(_ctx, session, assignment.WorkId, token);
        }, ct);
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
