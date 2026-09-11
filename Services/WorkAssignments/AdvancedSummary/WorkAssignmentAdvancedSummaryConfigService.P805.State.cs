using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public sealed partial class WorkAssignmentAdvancedSummaryConfigService
{
    public async Task<WorkAssignmentAdvancedSummaryConfigReadback>
        GetP8ConfigAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            string sectionId,
            CancellationToken ct)
    {
        var me = _me.RequireMe();
        var owner = await P805LoadOwnerContextAsync(
            null,
            assignmentId,
            dynamicFormTemplateId,
            sectionId,
            me,
            requireManage: false,
            ct);
        var state = await P805LoadStateAsync(null, owner, ct);
        return P805ToReadback(owner, state, me, null);
    }

    public async Task<WorkAssignmentAdvancedSummaryConfigVersionsResult>
        ListP8ConfigVersionsAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            string sectionId,
            CancellationToken ct)
    {
        var me = _me.RequireMe();
        var owner = await P805LoadOwnerContextAsync(
            null,
            assignmentId,
            dynamicFormTemplateId,
            sectionId,
            me,
            requireManage: false,
            ct);
        var state = await P805LoadStateAsync(null, owner, ct);
        return new WorkAssignmentAdvancedSummaryConfigVersionsResult(
            StatConfigOwnerKinds.AdvancedSummary,
            P805OwnerId(owner),
            state.ConfigId,
            state.Versions
                .OrderBy(item => item.VersionNo)
                .Select(item => P805MapVersion(owner, item))
                .ToList());
    }

    public async Task<WorkAssignmentAdvancedSummaryConfigVersionDto>
        GetP8ConfigVersionAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            string sectionId,
            int versionNo,
            CancellationToken ct)
    {
        var me = _me.RequireMe();
        var owner = await P805LoadOwnerContextAsync(
            null,
            assignmentId,
            dynamicFormTemplateId,
            sectionId,
            me,
            requireManage: false,
            ct);
        var state = await P805LoadStateAsync(null, owner, ct);
        var row = state.Versions.SingleOrDefault(
            item => item.VersionNo == versionNo);
        if (row is null)
        {
            throw AppExceptionFactory.NotFound(
                AppErrorCode.COMMON_NOT_FOUND,
                new
                {
                    versionNo,
                    reason =
                        "ADVANCED_SUMMARY_CONFIG_VERSION_NOT_FOUND"
                });
        }
        return P805MapVersion(owner, row);
    }

    private async Task<P805OwnerContext> P805LoadOwnerContextAsync(
        IClientSessionHandle? session,
        string assignmentId,
        string dynamicFormTemplateId,
        string sectionId,
        MeResponse me,
        bool requireManage,
        CancellationToken ct)
    {
        assignmentId = P805NormalizeAssignmentId(assignmentId);
        var assignment = await P805LoadAuthorizedAssignmentAsync(
            session,
            assignmentId,
            me,
            requireManage,
            ct);

        var templateId = P805NormalizeTemplateId(dynamicFormTemplateId);
        var templateFilter =
            Builders<DynamicFormTemplate>.Filter.Eq(
                item => item.Id,
                templateId) &
            Builders<DynamicFormTemplate>.Filter.Eq(
                item => item.IsDeleted,
                false);
        var template = session is null
            ? await _ctx.DynamicFormTemplates
                .Find(templateFilter)
                .FirstOrDefaultAsync(ct)
            : await _ctx.DynamicFormTemplates
                .Find(session, templateFilter)
                .FirstOrDefaultAsync(ct);
        if (template is null ||
            !string.Equals(
                assignment.DynamicFormTemplateId,
                template.Id,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
                new
                {
                    reason =
                        "ADVANCED_SUMMARY_CONFIG_TEMPLATE_NOT_FOUND"
                });
        }
        if (!template.IsPublished ||
            !template.IsActive)
        {
            throw P805Integrity(
                "ADVANCED_SUMMARY_TEMPLATE_MUST_BE_PUBLISHED");
        }

        DynamicFormPublishedSchemaSnapshot published;
        DynamicFormSectionSnapshot section;
        try
        {
            published =
                DynamicFormPublishedSchemaSnapshotBuilder
                    .ValidateAgainstTemplate(template);
            section =
                DynamicFormSectionSnapshotBuilder
                    .GetRequiredSection(template, sectionId);
        }
        catch (InvalidOperationException)
        {
            throw P805Integrity(
                "ADVANCED_SUMMARY_PUBLISHED_SCHEMA_INVALID");
        }
        return new P805OwnerContext(
            assignment,
            template,
            section,
            published);
    }

    private async Task<WorkAssignment>
        P805LoadAuthorizedAssignmentAsync(
            IClientSessionHandle? session,
            string assignmentId,
            MeResponse me,
            bool requireManage,
            CancellationToken ct)
    {
        var filter = P805AssignmentAuthorizationFilter(
            assignmentId,
            me,
            requireManage);
        var assignment = session is null
            ? await _ctx.WorkAssignments
                .Find(filter)
                .FirstOrDefaultAsync(ct)
            : await _ctx.WorkAssignments
                .Find(session, filter)
                .FirstOrDefaultAsync(ct);
        if (assignment is not null)
            return assignment;

        throw AppExceptionFactory.Forbidden(
            AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_READ_FORBIDDEN,
            new
            {
                reason = requireManage
                    ? "ADVANCED_SUMMARY_CONFIG_MANAGE_FORBIDDEN"
                    : "ADVANCED_SUMMARY_CONFIG_ACCESS_DENIED"
            });
    }

    private static FilterDefinition<WorkAssignment>
        P805AssignmentAuthorizationFilter(
            string assignmentId,
            MeResponse me,
            bool requireManage)
    {
        var filter = Builders<WorkAssignment>.Filter;
        var result =
            filter.Eq(item => item.Id, assignmentId) &
            filter.Eq(item => item.IsDeleted, false);
        if (RoleGuard.IsSystemAdmin(me))
            return result;
        if (requireManage)
        {
            return result &
                   filter.Eq(
                       item => item.CreatedByUserId,
                       me.Id);
        }
        return result &
               filter.Or(
                   filter.Eq(
                       item => item.CreatedByUserId,
                       me.Id),
                   filter.AnyEq(
                       item => item.LeaderWatcherUserIds,
                       me.Id),
                   filter.ElemMatch(
                       item => item.Assignees,
                       assignee => assignee.UserId == me.Id));
    }

    private async Task<P805ConfigState> P805LoadStateAsync(
        IClientSessionHandle? session,
        P805OwnerContext owner,
        CancellationToken ct)
    {
        var filter = Builders<
            WorkAssignmentAdvancedSummaryConfig>.Filter;
        var ownerFilter =
            filter.Eq(
                item => item.AssignmentId,
                owner.Assignment.Id) &
            filter.Eq(
                item => item.DynamicFormTemplateId,
                owner.Template.Id) &
            filter.Eq(
                item => item.SectionId,
                owner.Section.SectionId) &
            filter.Eq(item => item.IsDeleted, false);
        var query = session is null
            ? _ctx.WorkAssignmentAdvancedSummaryConfigs
                .Find(ownerFilter)
            : _ctx.WorkAssignmentAdvancedSummaryConfigs
                .Find(session, ownerFilter);
        var rows = await query
            .SortBy(item => item.VersionNo)
            .ToListAsync(ct);
        var ownerId = P805OwnerId(owner);
        var configId = P805DeterministicId(ownerId, "CONFIG");
        if (rows.Count == 0)
        {
            return new P805ConfigState(
                null,
                configId,
                P805DeterministicId(ownerId, "VERSION:1"),
                null,
                1,
                0,
                StatConfigStatuses.Draft,
                StatConfigCanonicalJson.EmptyConfigHash,
                P805BuildBasePins(owner),
                P805VirtualEmptyPayload(owner.Section.SectionId),
                Array.Empty<
                    WorkAssignmentAdvancedSummaryConfig>(),
                null,
                IsVirtual: true);
        }

        P805ValidateRows(owner, rows, configId);
        var current =
            rows.SingleOrDefault(
                item =>
                    item.Status ==
                    StatConfigStatuses.Draft) ??
            rows.SingleOrDefault(
                item =>
                    item.Status ==
                    StatConfigStatuses.Locked) ??
            rows[^1];
        var payload = P805DeserializeTrustedStoredPayload(
            current.ConfigJson,
            owner.Section.SectionId,
            "ADVANCED_SUMMARY_CONFIG_JSON_INVALID");
        return new P805ConfigState(
            current,
            configId,
            current.Id,
            current.PreviousVersionId,
            current.VersionNo,
            current.Revision,
            current.Status,
            current.ConfigHash,
            current.DependencyPins,
            payload,
            rows,
            P805ReadValidationReceipt(current, payload),
            IsVirtual: false);
    }

    private static void P805ValidateRows(
        P805OwnerContext owner,
        IReadOnlyList<WorkAssignmentAdvancedSummaryConfig> rows,
        string expectedConfigId)
    {
        var draftCount = 0;
        var lockedCount = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var expectedVersionNo = index + 1;
            var expectedVersionId = P805DeterministicId(
                P805OwnerId(owner),
                $"VERSION:{expectedVersionNo}");
            var previous = index == 0 ? null : rows[index - 1];
            if (!string.Equals(
                    row.ConfigId,
                    expectedConfigId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    row.Id,
                    expectedVersionId,
                    StringComparison.Ordinal) ||
                row.VersionNo != expectedVersionNo ||
                row.Revision < 0 ||
                !string.Equals(
                    row.AssignmentId,
                    owner.Assignment.Id,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    row.WorkId,
                    owner.Assignment.WorkId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    row.DynamicFormTemplateId,
                    owner.Template.Id,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    row.SectionId,
                    owner.Section.SectionId,
                    StringComparison.Ordinal) ||
                row.Status is not (
                    StatConfigStatuses.Draft or
                    StatConfigStatuses.Locked or
                    StatConfigStatuses.Archived) ||
                !P805IsCanonicalSha256(row.ConfigHash) ||
                row.DependencyPins is null ||
                !row.DependencyPins.SequenceEqual(
                    row.DependencyPins
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal),
                    StringComparer.Ordinal))
            {
                throw P805Integrity(
                    "ADVANCED_SUMMARY_CONFIG_IDENTITY_INVALID");
            }

            var expectedPrevious = previous?.Id;
            if (!string.Equals(
                    row.PreviousVersionId,
                    expectedPrevious,
                    StringComparison.Ordinal))
            {
                throw P805Integrity(
                    "ADVANCED_SUMMARY_CONFIG_LINEAGE_INVALID");
            }
            var payload = P805DeserializeTrustedStoredPayload(
                row.ConfigJson,
                owner.Section.SectionId,
                "ADVANCED_SUMMARY_CONFIG_VERSION_JSON_INVALID");
            if (!string.Equals(
                    P805ComputeConfigHash(
                        payload,
                        row.DependencyPins),
                    row.ConfigHash,
                    StringComparison.Ordinal))
            {
                throw P805Integrity(
                    "ADVANCED_SUMMARY_CONFIG_HASH_MISMATCH");
            }

            if (row.Status == StatConfigStatuses.Draft)
            {
                draftCount++;
                if (index != rows.Count - 1 ||
                    row.LockedAtUtc is not null ||
                    row.LockedByUserId is not null ||
                    row.LockTokenId is not null ||
                    row.ArchivedAtUtc is not null ||
                    row.ArchivedByUserId is not null ||
                    row.ValidationReceiptJson is not null ||
                    row.ValidationReceiptHash is not null)
                {
                    throw P805Integrity(
                        "ADVANCED_SUMMARY_DRAFT_STATE_INVALID");
                }
            }
            else
            {
                if (!row.LockedAtUtc.HasValue ||
                    !P805IsCanonicalObjectId(
                        row.LockedByUserId) ||
                    !P805IsCanonicalObjectId(
                        row.LockTokenId) ||
                    string.IsNullOrWhiteSpace(
                        row.ValidationReceiptJson) ||
                    !P805IsCanonicalSha256(
                        row.ValidationReceiptHash))
                {
                    throw P805Integrity(
                        "ADVANCED_SUMMARY_LOCK_STATE_INVALID");
                }
                _ = P805ReadValidationReceipt(row, payload);
                if (row.Status == StatConfigStatuses.Locked)
                {
                    lockedCount++;
                    if (row.ArchivedAtUtc is not null ||
                        row.ArchivedByUserId is not null)
                    {
                        throw P805Integrity(
                            "ADVANCED_SUMMARY_LOCK_STATE_INVALID");
                    }
                }
                else if (!row.ArchivedAtUtc.HasValue ||
                         !P805IsCanonicalObjectId(
                             row.ArchivedByUserId))
                {
                    throw P805Integrity(
                        "ADVANCED_SUMMARY_ARCHIVE_STATE_INVALID");
                }
            }
        }
        if (draftCount > 1 || lockedCount > 1)
        {
            throw P805Integrity(
                "ADVANCED_SUMMARY_CONFIG_ACTIVE_STATE_AMBIGUOUS");
        }
        if (draftCount == 1 &&
            lockedCount == 1 &&
            rows[^1].Status != StatConfigStatuses.Draft)
        {
            throw P805Integrity(
                "ADVANCED_SUMMARY_CONFIG_ACTIVE_STATE_INVALID");
        }
    }

    private static WorkAssignmentAdvancedSummaryConfigPayload
        P805DeserializeTrustedStoredPayload(
            string? configJson,
            string sectionId,
            string reason)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            throw P805Integrity(reason);
        try
        {
            return P805DeserializeStoredPayload(configJson, sectionId);
        }
        catch (AppException ex) when (
            ex.Code == AppErrorCode.STAT_CONFIG_SCHEMA_INVALID)
        {
            throw P805Integrity(reason);
        }
    }

    private static WorkAssignmentAdvancedSummaryConfigReadback
        P805ToReadback(
            P805OwnerContext owner,
            P805ConfigState state,
            MeResponse me,
            string? receiptId)
    {
        var canManage =
            RoleGuard.IsSystemAdmin(me) ||
            string.Equals(
                owner.Assignment.CreatedByUserId,
                me.Id,
                StringComparison.Ordinal);
        return new WorkAssignmentAdvancedSummaryConfigReadback(
            new StatConfigIdentity(
                StatConfigOwnerKinds.AdvancedSummary,
                P805OwnerId(owner),
                state.ConfigId,
                state.VersionId,
                state.VersionNo,
                state.Revision,
                state.Status,
                state.ConfigHash,
                state.DependencyPins),
            state.Payload,
            new StatConfigPermissionSet(
                CanReadConfig: true,
                CanManageDraft: canManage,
                CanLockVersion: canManage,
                CanViewResult: false,
                CanReadDiagnostics: false),
            WorkAssignmentAdvancedSummaryConfigContract
                .RuntimeBlockedUntilP9,
            state.IsVirtual,
            state.PreviousVersionId,
            state.Versions
                .OrderBy(item => item.VersionNo)
                .Select(item => P805MapVersion(owner, item))
                .ToList(),
            state.ValidationReceipt,
            receiptId);
    }

    private static WorkAssignmentAdvancedSummaryConfigVersionDto
        P805MapVersion(
            P805OwnerContext owner,
            WorkAssignmentAdvancedSummaryConfig row)
    {
        var payload = P805DeserializeTrustedStoredPayload(
            row.ConfigJson,
            owner.Section.SectionId,
            "ADVANCED_SUMMARY_CONFIG_VERSION_JSON_INVALID");
        return new WorkAssignmentAdvancedSummaryConfigVersionDto(
            new StatConfigIdentity(
                StatConfigOwnerKinds.AdvancedSummary,
                P805OwnerId(owner),
                row.ConfigId,
                row.Id,
                row.VersionNo,
                row.Revision,
                row.Status,
                row.ConfigHash,
                row.DependencyPins),
            payload,
            row.PreviousVersionId,
            P805ReadValidationReceipt(row, payload),
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.LockedAtUtc,
            row.LockedByUserId,
            row.ArchivedAtUtc,
            row.ArchivedByUserId);
    }

    private static string P805NormalizeAssignmentId(string? value)
    {
        var normalized = value?.Trim();
        if (!ObjectId.TryParse(normalized, out var parsed) ||
            !string.Equals(
                normalized,
                parsed.ToString(),
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_READ_FORBIDDEN,
                new
                {
                    reason =
                        "ADVANCED_SUMMARY_CONFIG_ACCESS_DENIED"
                });
        }
        return normalized;
    }

    private static string P805NormalizeTemplateId(string? value)
    {
        var normalized = value?.Trim();
        if (!ObjectId.TryParse(normalized, out var parsed) ||
            !string.Equals(
                normalized,
                parsed.ToString(),
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
                new
                {
                    reason =
                        "ADVANCED_SUMMARY_CONFIG_TEMPLATE_NOT_FOUND"
                });
        }
        return normalized;
    }

    private static string P805OwnerId(P805OwnerContext owner)
        => P805OwnerId(
            owner.Assignment.Id,
            owner.Template.Id,
            owner.Section.SectionId);

    internal static string P805OwnerId(
        string assignmentId,
        string templateId,
        string sectionId)
        => $"{assignmentId}:{templateId}:{sectionId}";

    private static string P805DeterministicId(
        string ownerId,
        string purpose)
        => StatConfigCanonicalJson.HashUtf8(
                $"{StatConfigOwnerKinds.AdvancedSummary}\0" +
                $"{ownerId}\0{purpose}")
            [..24];

    private static bool P805IsCanonicalObjectId(string? value)
        => ObjectId.TryParse(value, out var parsed) &&
           string.Equals(
               value,
               parsed.ToString(),
               StringComparison.Ordinal);

    private static bool P805IsCanonicalSha256(string? value)
    {
        if (value is not { Length: 64 })
            return false;
        return value.All(
            character =>
                character is >= '0' and <= '9' or
                >= 'a' and <= 'f');
    }

    private static AppException P805Integrity(string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new { reason });

    private static AppException BuildP805LegacyMutationBlocked()
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                reason =
                    "ADVANCED_SUMMARY_LEGACY_MUTATION_BLOCKED_USE_CAS_CONFIG_ROUTE"
            });

    private sealed record P805OwnerContext(
        WorkAssignment Assignment,
        DynamicFormTemplate Template,
        DynamicFormSectionSnapshot Section,
        DynamicFormPublishedSchemaSnapshot Published);

    private sealed record P805ConfigState(
        WorkAssignmentAdvancedSummaryConfig? Entity,
        string ConfigId,
        string VersionId,
        string? PreviousVersionId,
        int VersionNo,
        long Revision,
        string Status,
        string ConfigHash,
        IReadOnlyList<string> DependencyPins,
        WorkAssignmentAdvancedSummaryConfigPayload Payload,
        IReadOnlyList<WorkAssignmentAdvancedSummaryConfig> Versions,
        WorkAssignmentAdvancedSummaryValidationReceipt?
            ValidationReceipt,
        bool IsVirtual);
}
