using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.WorkAssignments;

public sealed partial class WorkAssignmentService
{
    public async Task<AssignmentFormPickerResult> QueryAssignmentFormsAsync(
        string workId,
        AssignmentFormPickerQueryRequest request,
        CancellationToken ct)
    {
        var actorId = _me.RequireMe().Id;
        if (request is null || !ObjectId.TryParse(workId, out _) ||
            (!string.IsNullOrWhiteSpace(request.ParentAssignmentId) &&
             !ObjectId.TryParse(request.ParentAssignmentId.Trim(), out _)))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED);

        var actor = await _ctx.Users
            .Find(x => x.Id == actorId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.Unauthorized();
        var work = await _lookup.LoadWorkAsync(workId, ct);
        var parentId = request.ParentAssignmentId?.Trim();
        var parent = await _lookup.LoadParentAsync(parentId, work.Id, ct);
        WorkAssignmentCreateScopeGuard.EnsureCanCreateWithinScope(work, parent, actor.Id, Array.Empty<string>());
        if (parent is not null && !WorkAssignmentCurrentAuthority.CanLeadChild(actor))
            throw AppExceptionFactory.Forbidden(AppErrorCode.WORK_ASSIGNMENT_BRANCH_CREATE_FORBIDDEN);
        await EnsureCreateScopeOpenAsync(work, parent, actor.Id, ct);
        if (!string.IsNullOrWhiteSpace(parent?.FlowInstanceId) ||
            await _ctx.DynamicFlowInstances
                .Find(x => x.WorkId == work.Id && !x.IsDeleted)
                .AnyAsync(ct))
            throw AppExceptionFactory.Create(AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE);

        // The exact parent version is the only exception to owner/admin bind.
        // ResolveForChildAsync checks actor, Work, pins and published integrity.
        string? inheritedId = null;
        if (parent is not null && !string.IsNullOrWhiteSpace(parent.DynamicFormTemplateId))
        {
            try
            {
                await _templateResolver.ResolveForChildAsync(
                    parent.DynamicFormTemplateId, actor.Id, work.Id, parent.Id, ct);
                inheritedId = parent.DynamicFormTemplateId;
            }
            catch (AppException)
            {
                // A stale or invalid parent pin cannot become a picker grant.
            }
        }

        var page = Math.Max(0, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 20);
        var offset = (long)page * pageSize;
        if (offset > 100_000)
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED);

        var fb = Builders<DynamicFormTemplate>.Filter;
        var scopeFilter = DynamicFormBindingAccessPolicy.BuildMayBindSearchFilter(actor, requirePublished: true);
        if (inheritedId is not null)
            scopeFilter |= fb.Eq(x => x.Id, inheritedId) &
                           fb.Eq(x => x.IsDeleted, false) &
                           fb.Eq(x => x.IsActive, true) &
                           fb.Eq(x => x.IsPublished, true);

        var searchFilter = scopeFilter;
        var q = request.Q?.Trim();
        if (!string.IsNullOrEmpty(q))
        {
            if (q.Length > 100)
                throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED);
            var literal = new BsonRegularExpression(Regex.Escape(q), "i");
            searchFilter &= fb.Regex(x => x.Code, literal) | fb.Regex(x => x.Name, literal);
        }

        var total = await _ctx.DynamicFormTemplates.CountDocumentsAsync(searchFilter, cancellationToken: ct);
        var docs = await _ctx.DynamicFormTemplates.Find(searchFilter)
            .SortByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip((int)offset)
            .Limit(pageSize)
            .ToListAsync(ct);

        async Task<bool> CanBindAsync(DynamicFormTemplate form)
        {
            try
            {
                if (parent is null)
                    await _templateResolver.ResolveAsync(form.Id, actor.Id, ct);
                else
                    await _templateResolver.ResolveForChildAsync(form.Id, actor.Id, work.Id, parent.Id, ct);
                return true;
            }
            catch (AppException)
            {
                return false;
            }
        }

        var rows = new List<AssignmentFormPickerRow>(docs.Count);
        foreach (var form in docs)
            rows.Add(new AssignmentFormPickerRow(
                form.Id, form.Code, form.Name, Math.Max(1, form.VersionNo),
                form.Id == inheritedId, await CanBindAsync(form)));

        var selectedId = request.SelectedId?.Trim();
        var selectedValid = false;
        if (!string.IsNullOrWhiteSpace(selectedId) && ObjectId.TryParse(selectedId, out _))
        {
            var selected = docs.FirstOrDefault(x => x.Id == selectedId)
                ?? await _ctx.DynamicFormTemplates.Find(scopeFilter & fb.Eq(x => x.Id, selectedId))
                    .FirstOrDefaultAsync(ct);
            if (selected is not null)
                selectedValid = await CanBindAsync(selected);
        }

        return new AssignmentFormPickerResult(rows, total, page, pageSize, selectedValid);
    }
}
