using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignments.BasicSummary;

public sealed partial class WorkAssignmentBasicSummaryService
{
    public async Task<WorkAssignmentBasicSummaryConfigReadback>
        GetP8ConfigAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            CancellationToken ct)
    {
        // Tạm khóa luồng tổng hợp cũ; giữ nguyên triển khai bên dưới.
        LegacyAggregateRetirement.Reject();
        var me = _me.RequireMe();
        assignmentId = P804NormalizeAssignmentId(assignmentId);
        var assignment = await P804LoadAuthorizedAssignmentAsync(
            null,
            assignmentId,
            me,
            requireManage: false,
            ct);
        var templateId =
            P804NormalizeTemplateId(dynamicFormTemplateId);
        var template = await P804LoadTemplateAsync(
            null,
            templateId,
            ct);
        var state = await P804LoadStateAsync(
            null,
            assignment,
            template,
            ct);
        return P804ToReadback(
            assignment,
            template,
            state,
            me,
            receiptId: null);
    }

    public async Task<WorkAssignmentBasicSummaryConfigVersionsResult>
        ListP8ConfigVersionsAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            CancellationToken ct)
    {
        // Tạm khóa luồng tổng hợp cũ; giữ nguyên triển khai bên dưới.
        LegacyAggregateRetirement.Reject();
        var readback = await GetP8ConfigAsync(
            assignmentId,
            dynamicFormTemplateId,
            ct);
        return new WorkAssignmentBasicSummaryConfigVersionsResult(
            readback.Versions);
    }

    public async Task<WorkAssignmentBasicSummaryConfigReadback>
        GetP8ConfigVersionAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            int versionNo,
            CancellationToken ct)
    {
        // Tạm khóa luồng tổng hợp cũ; giữ nguyên triển khai bên dưới.
        LegacyAggregateRetirement.Reject();
        var me = _me.RequireMe();
        assignmentId = P804NormalizeAssignmentId(assignmentId);
        var assignment = await P804LoadAuthorizedAssignmentAsync(
            null,
            assignmentId,
            me,
            requireManage: false,
            ct);
        var templateId =
            P804NormalizeTemplateId(dynamicFormTemplateId);
        var template = await P804LoadTemplateAsync(
            null,
            templateId,
            ct);
        var current = await P804LoadStateAsync(
            null,
            assignment,
            template,
            ct);
        if (current.IsVirtual && versionNo == 1)
        {
            return P804ToReadback(
                assignment,
                template,
                current,
                me,
                receiptId: null);
        }

        var snapshot = current.Versions
            .SingleOrDefault(item => item.VersionNo == versionNo);
        if (snapshot is null)
        {
            throw AppExceptionFactory.NotFound(
                AppErrorCode.COMMON_NOT_FOUND,
                new
                {
                    versionNo,
                    reason = "BASIC_SUMMARY_CONFIG_VERSION_NOT_FOUND"
                });
        }
        var payload =
            P804DeserializeStoredPayload(snapshot.ConfigJson);
        var expectedHash = P804ComputeConfigHash(
            payload,
            snapshot.DependencyPins);
        if (!string.Equals(
                expectedHash,
                snapshot.ConfigHash,
                StringComparison.Ordinal))
        {
            throw P804Integrity(
                "BASIC_SUMMARY_CONFIG_VERSION_HASH_MISMATCH");
        }
        var selected = new P804ConfigState(
            current.Entity,
            current.ConfigId,
            snapshot.VersionId,
            snapshot.PreviousVersionId,
            snapshot.VersionNo,
            snapshot.Revision,
            snapshot.Status,
            snapshot.ConfigHash,
            snapshot.DependencyPins,
            payload,
            current.Versions,
            IsVirtual: false);
        return P804ToReadback(
            assignment,
            template,
            selected,
            me,
            receiptId: null);
    }

    private async Task<WorkAssignment> P804LoadAuthorizedAssignmentAsync(
        IClientSessionHandle? session,
        string assignmentId,
        MeResponse me,
        bool requireManage,
        CancellationToken ct)
    {
        var filter = P804AssignmentAuthorizationFilter(
            assignmentId,
            me,
            requireManage,
            requireManage ? Array.Empty<string>() : await LoadLeadershipWorkIdsAsync(me.Id, ct));
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
                    ? "BASIC_SUMMARY_CONFIG_MANAGE_FORBIDDEN"
                    : "BASIC_SUMMARY_CONFIG_ACCESS_DENIED"
            });
    }

    // Read permission follows current Work leadership, so removal takes effect
    // without rewriting every descendant assignment. This is never a manage grant.
    private async Task<string[]> LoadLeadershipWorkIdsAsync(string actorUserId, CancellationToken ct)
        => (await _ctx.Works.Find(work => !work.IsDeleted &&
                (work.CreatedByUserId == actorUserId || work.LeaderDirectiveUserId == actorUserId ||
                 work.LeaderWatchUserIds.Contains(actorUserId)))
            .Project(work => work.Id).ToListAsync(ct)).ToArray();

    private static FilterDefinition<WorkAssignment>
        P804AssignmentAuthorizationFilter(
            string assignmentId,
            MeResponse me,
            bool requireManage,
            IReadOnlyCollection<string>? leadershipWorkIds = null)
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
                   filter.In(item => item.WorkId, leadershipWorkIds ?? Array.Empty<string>()),
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

    private async Task<DynamicFormTemplate> P804LoadTemplateAsync(
        IClientSessionHandle? session,
        string templateId,
        CancellationToken ct)
    {
        var filter = Builders<DynamicFormTemplate>.Filter.Where(
            item => item.Id == templateId && !item.IsDeleted);
        var template = session is null
            ? await _ctx.DynamicFormTemplates
                .Find(filter)
                .FirstOrDefaultAsync(ct)
            : await _ctx.DynamicFormTemplates
                .Find(session, filter)
                .FirstOrDefaultAsync(ct);
        return template ??
               throw AppExceptionFactory.NotFound(
                   AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
                   new
                   {
                       reason =
                           "BASIC_SUMMARY_CONFIG_TEMPLATE_NOT_FOUND"
                   });
    }

    private async Task<P804ConfigState> P804LoadStateAsync(
        IClientSessionHandle? session,
        WorkAssignment assignment,
        DynamicFormTemplate template,
        CancellationToken ct)
    {
        var filter = Builders<WorkAssignmentBasicSummaryConfig>.Filter;
        var ownerFilter =
            filter.Eq(item => item.AssignmentId, assignment.Id) &
            filter.Eq(
                item => item.DynamicFormTemplateId,
                template.Id) &
            filter.Eq(item => item.IsActive, true) &
            filter.Eq(item => item.IsDeleted, false);
        var entity = session is null
            ? await _ctx.WorkAssignmentBasicSummaryConfigs
                .Find(ownerFilter)
                .FirstOrDefaultAsync(ct)
            : await _ctx.WorkAssignmentBasicSummaryConfigs
                .Find(session, ownerFilter)
                .FirstOrDefaultAsync(ct);
        var ownerId = P804OwnerId(assignment.Id, template.Id);
        if (entity is null)
        {
            return new P804ConfigState(
                entity,
                entity?.Id ?? P804DeterministicId(
                    ownerId,
                    "CONFIG"),
                P804DeterministicId(ownerId, "VERSION:1"),
                null,
                1,
                0,
                StatConfigStatuses.Draft,
                StatConfigCanonicalJson.EmptyConfigHash,
                P804BuildTemplatePins(template),
                P804VirtualEmptyPayload,
                Array.Empty<
                    WorkAssignmentBasicSummaryConfigVersion>(),
                IsVirtual: true);
        }

        if (!P804IsCanonicalObjectId(entity.Id) ||
            !P804IsCanonicalObjectId(entity.VersionId) ||
            !P804IsOptionalCanonicalObjectId(
                entity.PreviousVersionId) ||
            entity.VersionNo < 1 ||
            entity.Revision < 0 ||
            entity.Status is not (
                StatConfigStatuses.Draft or
                StatConfigStatuses.Locked) ||
            entity.DependencyPins is null ||
            !P804IsCanonicalSha256(entity.ConfigHash))
        {
            throw P804Integrity(
                "BASIC_SUMMARY_CONFIG_IDENTITY_INVALID");
        }

        var payload =
            P804DeserializeTrustedStoredPayload(
                entity.ConfigJson,
                "BASIC_SUMMARY_CONFIG_JSON_INVALID");
        var pins = entity.DependencyPins
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        if (!pins.SequenceEqual(
                entity.DependencyPins,
                StringComparer.Ordinal))
        {
            throw P804Integrity(
                "BASIC_SUMMARY_CONFIG_PINS_NON_CANONICAL");
        }
        var expectedHash = P804ComputeConfigHash(payload, pins);
        if (!string.Equals(
                expectedHash,
                entity.ConfigHash,
                StringComparison.Ordinal))
        {
            throw P804Integrity(
                "BASIC_SUMMARY_CONFIG_HASH_MISMATCH");
        }

        var versions = (entity.Versions ??
                        new List<
                            WorkAssignmentBasicSummaryConfigVersion>())
            .OrderBy(item => item.VersionNo)
            .ToList();
        P804ValidateVersions(entity, versions);
        return new P804ConfigState(
            entity,
            entity.Id,
            entity.VersionId!,
            entity.PreviousVersionId,
            entity.VersionNo,
            entity.Revision,
            entity.Status!,
            entity.ConfigHash!,
            pins,
            payload,
            versions,
            IsVirtual: false);
    }

    private static void P804ValidateVersions(
        WorkAssignmentBasicSummaryConfig entity,
        IReadOnlyList<WorkAssignmentBasicSummaryConfigVersion>
            versions)
    {
        var draftCount = 0;
        for (var index = 0; index < versions.Count; index++)
        {
            var version = versions[index];
            var previous =
                index == 0 ? null : versions[index - 1];
            if (version.VersionNo != index + 1 ||
                version.Revision < 0 ||
                !P804IsCanonicalObjectId(version.VersionId) ||
                !P804IsOptionalCanonicalObjectId(
                    version.PreviousVersionId) ||
                !P804IsCanonicalSha256(version.ConfigHash) ||
                version.Status is not (
                    StatConfigStatuses.Draft or
                    StatConfigStatuses.Locked))
            {
                throw P804Integrity(
                    "BASIC_SUMMARY_CONFIG_VERSION_IDENTITY_INVALID");
            }
            if (previous is null)
            {
                if (version.PreviousVersionId is not null)
                {
                    throw P804Integrity(
                        "BASIC_SUMMARY_CONFIG_LINEAGE_ROOT_INVALID");
                }
            }
            else if (!string.Equals(
                         version.PreviousVersionId,
                         previous.VersionId,
                         StringComparison.Ordinal))
            {
                throw P804Integrity(
                    "BASIC_SUMMARY_CONFIG_LINEAGE_LINK_INVALID");
            }
            if (version.Status == StatConfigStatuses.Draft)
            {
                draftCount++;
                if (index != versions.Count - 1 ||
                    version.VersionNo != entity.VersionNo)
                {
                    throw P804Integrity(
                        "BASIC_SUMMARY_CONFIG_DRAFT_NOT_CURRENT");
                }
            }
            if (version.DependencyPins is null ||
                !version.DependencyPins.SequenceEqual(
                    version.DependencyPins
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal),
                    StringComparer.Ordinal))
            {
                throw P804Integrity(
                    "BASIC_SUMMARY_CONFIG_VERSION_PINS_NON_CANONICAL");
            }
            var payload =
                P804DeserializeTrustedStoredPayload(
                    version.ConfigJson,
                    "BASIC_SUMMARY_CONFIG_VERSION_JSON_INVALID");
            var expectedHash = P804ComputeConfigHash(
                payload,
                version.DependencyPins);
            if (!string.Equals(
                    expectedHash,
                    version.ConfigHash,
                    StringComparison.Ordinal))
            {
                throw P804Integrity(
                    "BASIC_SUMMARY_CONFIG_VERSION_HASH_MISMATCH");
            }
        }
        if (draftCount > 1 ||
            versions.Count == 0 ||
            entity.VersionNo != versions.Count)
        {
            throw P804Integrity(
                "BASIC_SUMMARY_CONFIG_VERSION_SEQUENCE_INVALID");
        }
        var current = versions.SingleOrDefault(
            item => item.VersionNo == entity.VersionNo);
        if (current is null ||
            current.VersionId != entity.VersionId ||
            current.Revision != entity.Revision ||
            current.Status != entity.Status ||
            current.ConfigHash != entity.ConfigHash)
        {
            throw P804Integrity(
                "BASIC_SUMMARY_CONFIG_CURRENT_VERSION_MISMATCH");
        }
    }

    private static WorkAssignmentBasicSummaryConfigPayload
        P804DeserializeTrustedStoredPayload(
            string? configJson,
            string reason)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            throw P804Integrity(reason);
        try
        {
            return P804DeserializeStoredPayload(configJson);
        }
        catch (AppException ex) when (
            ex.Code == AppErrorCode.STAT_CONFIG_SCHEMA_INVALID)
        {
            throw P804Integrity(reason);
        }
    }

    private static bool P804IsCanonicalObjectId(
        string? value)
        => ObjectId.TryParse(value, out var parsed) &&
           string.Equals(
               parsed.ToString(),
               value,
               StringComparison.Ordinal);

    private static bool P804IsOptionalCanonicalObjectId(
        string? value)
        => value is null || P804IsCanonicalObjectId(value);

    private static bool P804IsCanonicalSha256(string? value)
    {
        if (value is not { Length: 64 })
            return false;
        foreach (var character in value)
        {
            if ((character < '0' || character > '9') &&
                (character < 'a' || character > 'f'))
            {
                return false;
            }
        }
        return true;
    }

    private async Task<IReadOnlyList<string>>
        P804ResolveDependencyPinsAsync(
            IClientSessionHandle session,
            DynamicFormTemplate template,
            WorkAssignmentBasicSummaryConfigPayload payload,
            MeResponse me,
            CancellationToken ct)
    {
        var templateContext =
            P804BuildTemplateDependencyContext(template);
        var pins = templateContext.Pins.ToList();
        var targets = payload.Targets ?? throw P804Schema("$.payload.targets", "TARGETS_REQUIRED");
        using var fieldsDocument = P804ParseArray(
            template.FieldsJson,
            "$.template.fields",
            "TEMPLATE_FIELDS_INVALID");
        using var blocksDocument = P804ParseArray(
            template.BlocksJson,
            "$.template.blocks",
            "TEMPLATE_BLOCKS_INVALID");
        var rowLabelCodes = new HashSet<string>(
            targets
                .Where(target =>
                    target.ConceptKind ==
                    WorkAssignmentBasicSummaryConfigContract.RowLabel)
                .Select(target => target.ConceptKey!),
            StringComparer.Ordinal);
        var modernRowLabels =
            templateContext.TableSectionJson is null
                ? null
                : P804ResolveModernRowLabelSnapshots(
                    templateContext.TableSectionJson,
                    rowLabelCodes);

        IReadOnlyDictionary<string, LabelCatalogItem>
            modernLabelOwners =
                new Dictionary<string, LabelCatalogItem>(
                    StringComparer.Ordinal);
        IReadOnlyDictionary<
            string,
            IReadOnlyList<LabelCatalogItem>> legacyLabelsByCode =
                new Dictionary<
                    string,
                    IReadOnlyList<LabelCatalogItem>>(
                    StringComparer.Ordinal);
        if (modernRowLabels is not null &&
            modernRowLabels.Count != 0)
        {
            var labelIds = modernRowLabels.Values
                .Select(item => item.Snapshot.LabelId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var labelFilter =
                Builders<LabelCatalogItem>.Filter.In(
                    label => label.Id,
                    labelIds) &
                Builders<LabelCatalogItem>.Filter.Eq(
                    label => label.IsDeleted,
                    false) &
                DynamicFormStatisticConfigCommandService
                    .GetP804LabelVisibilityFilter(me);
            var owners = await _ctx.Labels
                .Find(session, labelFilter)
                .ToListAsync(ct);
            modernLabelOwners = owners.ToDictionary(
                label => label.Id,
                StringComparer.Ordinal);
        }
        else if (modernRowLabels is null &&
                 rowLabelCodes.Count != 0)
        {
            var labelFilter =
                Builders<LabelCatalogItem>.Filter.In(
                    label => label.Code,
                    rowLabelCodes) &
                Builders<LabelCatalogItem>.Filter.Eq(
                    label => label.IsDeleted,
                    false) &
                DynamicFormStatisticConfigCommandService
                    .GetP804LabelVisibilityFilter(me);
            var labels = await _ctx.Labels
                .Find(session, labelFilter)
                .ToListAsync(ct);
            legacyLabelsByCode = labels
                .GroupBy(label => label.Code, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<LabelCatalogItem>)
                        group.ToList(),
                    StringComparer.Ordinal);
        }
        var selectedLegacyLabels =
            new Dictionary<string, LabelCatalogItem>(
                StringComparer.Ordinal);

        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var actualType = target.ConceptKind switch
            {
                WorkAssignmentBasicSummaryConfigContract.Field =>
                    P804ResolveFieldDataType(
                        fieldsDocument.RootElement,
                        target.ConceptKey!,
                        index),
                WorkAssignmentBasicSummaryConfigContract.TableMetric =>
                    P804ResolveTableMetricDataType(
                        blocksDocument.RootElement,
                        target.ConceptKey!,
                        index),
                WorkAssignmentBasicSummaryConfigContract.RowLabel =>
                    modernRowLabels is null
                        ? P804ResolveLegacyRowLabelDataType(
                            blocksDocument.RootElement,
                            legacyLabelsByCode,
                            selectedLegacyLabels,
                            target.ConceptKey!,
                            index)
                        : P804ResolveModernRowLabelDataType(
                            modernRowLabels,
                            modernLabelOwners,
                            target.ConceptKey!,
                            index),
                _ => throw P804Schema(
                    $"$.payload.targets[{index}].conceptKind",
                    "CONCEPT_KIND_UNSUPPORTED")
            };
            if (!string.Equals(
                    actualType,
                    target.DataType,
                    StringComparison.Ordinal))
            {
                throw P804Schema(
                    $"$.payload.targets[{index}].dataType",
                    "BASIC_SUMMARY_TARGET_DATA_TYPE_MISMATCH");
            }
        }
        if (modernRowLabels is not null)
        {
            foreach (var code in rowLabelCodes
                         .OrderBy(value => value, StringComparer.Ordinal))
            {
                var snapshot = modernRowLabels[code].Snapshot;
                pins.Add(
                    $"LABEL:{snapshot.LabelId}:" +
                    $"{snapshot.VersionId}:{snapshot.VersionNo}:" +
                    snapshot.ConfigHash);
            }
        }
        else
        {
            foreach (var label in selectedLegacyLabels.Values
                         .OrderBy(item => item.Code, StringComparer.Ordinal))
            {
                pins.Add(
                    $"LABEL:{label.Id}:{label.VersionId}:" +
                    $"{label.VersionNo}:{label.ConfigHash}");
            }
        }
        pins.AddRange(await P804ResolveNativeDependencyPinsAsync(
            session, templateContext.NativeConfig, payload.NativeTargets, me, ct));
        return pins
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    private static string P804ResolveFieldDataType(
        JsonElement fields,
        string conceptKey,
        int targetIndex)
    {
        foreach (var field in fields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object)
                continue;
            var id = P804ReadString(field, "id");
            var key = P804ReadString(field, "key");
            if (!string.Equals(
                    id,
                    conceptKey,
                    StringComparison.Ordinal) &&
                !string.Equals(
                    key,
                    conceptKey,
                    StringComparison.Ordinal))
            {
                continue;
            }
            return P804MapDataType(
                P804ReadString(field, "type"),
                $"$.payload.targets[{targetIndex}].conceptKey");
        }
        throw P804Schema(
            $"$.payload.targets[{targetIndex}].conceptKey",
            "BASIC_SUMMARY_TARGET_NOT_FOUND");
    }

    private static string P804ResolveTableMetricDataType(
        JsonElement blocks,
        string conceptKey,
        int targetIndex)
    {
        var separator = conceptKey.IndexOf(':');
        if (separator <= 0 || separator == conceptKey.Length - 1)
        {
            throw P804Schema(
                $"$.payload.targets[{targetIndex}].conceptKey",
                "TABLE_METRIC_CONCEPT_KEY_INVALID");
        }
        var blockId = conceptKey[..separator];
        var metricKey = conceptKey[(separator + 1)..];
        foreach (var block in blocks.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object ||
                !string.Equals(
                    P804ReadString(block, "blockId"),
                    blockId,
                    StringComparison.Ordinal))
            {
                continue;
            }
            if (P804ReadBoolean(block, "statisticsDisabled"))
            {
                throw P804Schema(
                    $"$.payload.targets[{targetIndex}].conceptKey",
                    "TABLE_STATISTICS_DISABLED");
            }
            if (!block.TryGetProperty(
                    "metricRules",
                    out var rules) ||
                rules.ValueKind != JsonValueKind.Array)
            {
                break;
            }
            foreach (var rule in rules.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Object ||
                    !string.Equals(
                        P804ReadString(rule, "metricKey"),
                        metricKey,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                var rawType =
                    P804ReadString(rule, "dataType") ??
                    P804ReadString(rule, "targetDataType") ??
                    P804ReadString(block, "defaultDataType") ??
                    P804ReadString(block, "dataType");
                return P804MapDataType(
                    rawType,
                    $"$.payload.targets[{targetIndex}].conceptKey");
            }
            break;
        }
        throw P804Schema(
            $"$.payload.targets[{targetIndex}].conceptKey",
            "BASIC_SUMMARY_TARGET_NOT_FOUND");
    }

    private static IReadOnlyDictionary<
        string,
        P804ModernRowLabelResolution>
        P804ResolveModernRowLabelSnapshots(
            string tableSectionJson,
            IReadOnlySet<string> requestedCodes)
    {
        List<DynamicFormStatisticTableConfigDto> tables;
        try
        {
            tables = JsonSerializer.Deserialize<
                         List<DynamicFormStatisticTableConfigDto>>(
                         tableSectionJson,
                         StatConfigCanonicalJson.StrictJsonOptions) ??
                     new List<
                         DynamicFormStatisticTableConfigDto>();
        }
        catch (JsonException)
        {
            throw P804Integrity(
                "BASIC_SUMMARY_DYNAMIC_FORM_TABLE_SECTION_INVALID");
        }

        var result =
            new Dictionary<string, P804ModernRowLabelResolution>(
                StringComparer.Ordinal);
        foreach (var table in tables)
        {
            if (table.AllowedRowLabelCodes is null ||
                table.RowLabelSnapshots is null)
            {
                throw P804Integrity(
                    "BASIC_SUMMARY_DYNAMIC_FORM_ROW_LABEL_INVALID");
            }
            foreach (var code in table.AllowedRowLabelCodes)
            {
                if (!requestedCodes.Contains(code))
                    continue;
                var matches = table.RowLabelSnapshots
                    .Where(snapshot =>
                        string.Equals(
                            snapshot.Code,
                            code,
                            StringComparison.Ordinal))
                    .ToList();
                if (matches.Count != 1)
                {
                    throw P804Integrity(
                        "BASIC_SUMMARY_DYNAMIC_FORM_ROW_LABEL_AMBIGUOUS");
                }
                var snapshot = matches[0];
                P804ValidateModernRowLabelSnapshot(snapshot, code);
                var resolution =
                    new P804ModernRowLabelResolution(
                        snapshot,
                        P804MapDataType(
                            table.RowLabelDataType,
                            "$.template.statisticConfig.tableConfig"));
                if (result.TryGetValue(code, out var existing) &&
                    (!Equals(existing.Snapshot, snapshot) ||
                     !string.Equals(
                         existing.DataType,
                         resolution.DataType,
                         StringComparison.Ordinal)))
                {
                    throw P804Integrity(
                        "BASIC_SUMMARY_DYNAMIC_FORM_ROW_LABEL_INCONSISTENT");
                }
                result[code] = resolution;
            }
        }
        return result;
    }

    private static void P804ValidateModernRowLabelSnapshot(
        DynamicFormStatisticLabelSnapshotDto snapshot,
        string expectedCode)
    {
        var canonicalScope =
            snapshot.ScopeType == LabelScopeTypes.Global
                ? snapshot.ScopeId is null
                : snapshot.ScopeType is (
                    LabelScopeTypes.Level or LabelScopeTypes.Unit) &&
                  P804IsCanonicalObjectId(snapshot.ScopeId);
        if (!P804IsCanonicalObjectId(snapshot.LabelId) ||
            !P804IsCanonicalObjectId(snapshot.VersionId) ||
            snapshot.VersionNo < 1 ||
            !P804IsCanonicalSha256(snapshot.ConfigHash) ||
            !string.Equals(
                snapshot.Code,
                expectedCode,
                StringComparison.Ordinal) ||
            !snapshot.IsActive ||
            !string.Equals(
                snapshot.Usage,
                LabelUsages.TableTarget,
                StringComparison.Ordinal) ||
            snapshot.DataType is not (
                LabelDataTypes.Number or
                LabelDataTypes.ShortText or
                LabelDataTypes.StringList or
                LabelDataTypes.LongText or
                LabelDataTypes.Date or
                LabelDataTypes.Boolean) ||
            !canonicalScope)
        {
            throw P804Integrity(
                "BASIC_SUMMARY_DYNAMIC_FORM_ROW_LABEL_IDENTITY_INVALID");
        }
    }

    private static string P804ResolveModernRowLabelDataType(
        IReadOnlyDictionary<
            string,
            P804ModernRowLabelResolution> labels,
        IReadOnlyDictionary<string, LabelCatalogItem> owners,
        string conceptKey,
        int targetIndex)
    {
        if (!labels.TryGetValue(conceptKey, out var resolution) ||
            !owners.TryGetValue(
                resolution.Snapshot.LabelId,
                out var owner))
        {
            throw P804Schema(
                $"$.payload.targets[{targetIndex}].conceptKey",
                "BASIC_SUMMARY_ROW_LABEL_NOT_ALLOWED");
        }
        LabelConfigCommandService
            .ValidateP804TrustedTableTargetSnapshot(
                owner,
                resolution.Snapshot);
        return resolution.DataType;
    }

    private static string P804ResolveLegacyRowLabelDataType(
        JsonElement blocks,
        IReadOnlyDictionary<
            string,
            IReadOnlyList<LabelCatalogItem>> labels,
        IDictionary<string, LabelCatalogItem> selectedLabels,
        string conceptKey,
        int targetIndex)
    {
        var allowed = blocks.EnumerateArray().Any(
            block =>
                block.ValueKind == JsonValueKind.Object &&
                block.TryGetProperty(
                    "allowedRowLabelCodes",
                    out var codes) &&
                codes.ValueKind == JsonValueKind.Array &&
                codes.EnumerateArray().Any(
                    code =>
                        code.ValueKind == JsonValueKind.String &&
                        string.Equals(
                            code.GetString(),
                            conceptKey,
                            StringComparison.Ordinal)));
        if (!allowed ||
            !labels.TryGetValue(conceptKey, out var candidates))
        {
            throw P804Schema(
                $"$.payload.targets[{targetIndex}].conceptKey",
                "BASIC_SUMMARY_ROW_LABEL_NOT_ALLOWED");
        }
        var active = candidates
            .Where(label => label.IsActive && !label.IsDeleted)
            .ToList();
        if (active.Count > 1)
        {
            throw P804Schema(
                $"$.payload.targets[{targetIndex}].conceptKey",
                "BASIC_SUMMARY_ROW_LABEL_AMBIGUOUS");
        }
        if (active.Count != 1 ||
            !string.Equals(
                active[0].Usage,
                LabelUsages.TableTarget,
                StringComparison.Ordinal))
        {
            throw P804Schema(
                $"$.payload.targets[{targetIndex}].conceptKey",
                "BASIC_SUMMARY_ROW_LABEL_NOT_ALLOWED");
        }
        var label = active[0];
        LabelConfigCommandService
            .ValidateP804TrustedActiveTableTarget(label);
        selectedLabels[conceptKey] = label;
        if (label.ValueSourceType != LabelValueSourceTypes.None ||
            label.DataType == LabelDataTypes.StringList)
        {
            return WorkAssignmentBasicSummaryConfigContract.Choice;
        }
        return P804MapDataType(
            label.DataType,
            $"$.payload.targets[{targetIndex}].conceptKey");
    }

    private static string P804MapDataType(
        string? rawType,
        string path)
        => rawType switch
        {
            "number" or "NUMBER" =>
                WorkAssignmentBasicSummaryConfigContract.Number,
            "date" or "fullDate" or "DATE" or "FULL_DATE" =>
                WorkAssignmentBasicSummaryConfigContract.Date,
            "boolean" or "BOOLEAN" =>
                WorkAssignmentBasicSummaryConfigContract.Boolean,
            "singleSelect" or "multiSelect" or "stringList" or
                "SINGLE_SELECT" or "MULTI_SELECT" or "STRING_LIST" =>
                    WorkAssignmentBasicSummaryConfigContract.Choice,
            "shortText" or "longText" or "richText" or
                "SHORT_TEXT" or "LONG_TEXT" or "RICH_TEXT" =>
                    WorkAssignmentBasicSummaryConfigContract.Text,
            _ => throw P804Schema(
                path,
                "BASIC_SUMMARY_TARGET_DATA_TYPE_UNRESOLVED")
        };

    private static WorkAssignmentBasicSummaryConfigReadback
        P804ToReadback(
            WorkAssignment assignment,
            DynamicFormTemplate template,
            P804ConfigState state,
            MeResponse me,
            string? receiptId)
    {
        var canManage =
            RoleGuard.IsSystemAdmin(me) ||
            string.Equals(
                assignment.CreatedByUserId,
                me.Id,
                StringComparison.Ordinal);
        return new WorkAssignmentBasicSummaryConfigReadback(
            new StatConfigIdentity(
                StatConfigOwnerKinds.BasicSummary,
                P804OwnerId(assignment.Id, template.Id),
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
            WorkAssignmentBasicSummaryConfigContract
                .RuntimeEligibilityBlockedUntilP9,
            state.IsVirtual,
            state.PreviousVersionId,
            new WorkAssignmentBasicSummaryMeanContract(
                WorkAssignmentBasicSummaryConfigContract.MeanFormula,
                MetadataOnly: true),
            state.Versions
                .OrderBy(item => item.VersionNo)
                .Select(P804MapVersion)
                .ToList(),
            receiptId);
    }

    private static WorkAssignmentBasicSummaryConfigVersionDto
        P804MapVersion(
            WorkAssignmentBasicSummaryConfigVersion version)
        => new(
            version.VersionId,
            version.PreviousVersionId,
            version.VersionNo,
            version.Revision,
            version.Status,
            version.ConfigHash,
            version.DependencyPins,
            P804DeserializeStoredPayload(version.ConfigJson),
            version.CreatedAtUtc,
            version.LockedAtUtc);

    private static P804TemplateDependencyContext
        P804BuildTemplateDependencyContext(
        DynamicFormTemplate template)
    {
        if (template.NativeTablesVersion is not null)
        {
            var native = P804ReadNativeConfig(template);
            return new P804TemplateDependencyContext(new[]
            {
                $"DYNAMIC_FORM_SCHEMA:{template.Id}:{template.VersionNo}:{template.PublishedSchemaHash}",
                $"DYNAMIC_FORM_STAT_CONFIG:{template.Id}:{native.ConfigId}:{native.VersionId}:" +
                $"{native.VersionNo}:{native.Revision}:{native.ConfigHash}"
            }.OrderBy(value => value, StringComparer.Ordinal).ToList(), native.TableSectionJson, native);
        }
        DynamicFormStatisticConfigCommandService
            .P804TrustedPersistedView? trusted;
        try
        {
            trusted = DynamicFormStatisticConfigCommandService
                .GetP804TrustedPersistedView(template);
        }
        catch (InvalidOperationException)
        {
            throw P804Integrity(
                "BASIC_SUMMARY_DYNAMIC_FORM_PUBLISHED_SCHEMA_INVALID");
        }
        using var fields = P804ParseArray(
            template.FieldsJson,
            "$.template.fields",
            "TEMPLATE_FIELDS_INVALID");
        using var blocks = P804ParseArray(
            template.BlocksJson,
            "$.template.blocks",
            "TEMPLATE_BLOCKS_INVALID");
        var schemaHash = template.IsPublished
            ? template.PublishedSchemaHash!.Trim().ToLowerInvariant()
            : StatConfigCanonicalJson.HashObject(
                new
                {
                    templateId = template.Id,
                    templateVersionNo = template.VersionNo,
                    fields = fields.RootElement,
                    blocks = blocks.RootElement
                });
        var statisticPin = trusted is not null
            ? $"DYNAMIC_FORM_STAT_CONFIG:{template.Id}:" +
              $"{trusted.ConfigId}:{trusted.VersionId}:" +
              $"{trusted.VersionNo}:{trusted.Revision}:" +
              trusted.ConfigHash
            : $"DYNAMIC_FORM_STAT_CONFIG:{template.Id}:LEGACY:" +
              StatConfigCanonicalJson.HashObject(
                  new
                  {
                      fields = fields.RootElement,
                      blocks = blocks.RootElement
                  });
        var pins = new[]
            {
                $"DYNAMIC_FORM_SCHEMA:{template.Id}:" +
                $"{template.VersionNo}:" +
                schemaHash,
                statisticPin
            }
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        return new P804TemplateDependencyContext(pins, trusted?.TableSectionJson, null);
    }

    private static IReadOnlyList<string> P804BuildTemplatePins(
        DynamicFormTemplate template)
        => P804BuildTemplateDependencyContext(template).Pins;

    private sealed record P804TemplateDependencyContext(
        IReadOnlyList<string> Pins,
        string? TableSectionJson,
        DynamicFormStatisticConfigCommandService.NativeStatisticInputView? NativeConfig);

    private sealed record P804ModernRowLabelResolution(
        DynamicFormStatisticLabelSnapshotDto Snapshot,
        string DataType);

    private static JsonDocument P804ParseArray(
        string? json,
        string path,
        string reason)
    {
        try
        {
            var document = JsonDocument.Parse(json ?? "[]");
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                document.Dispose();
                throw P804Schema(path, reason);
            }
            return document;
        }
        catch (AppException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw P804Schema(path, reason);
        }
    }

    private static string? P804ReadString(
        JsonElement owner,
        string propertyName)
        => owner.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static bool P804ReadBoolean(
        JsonElement owner,
        string propertyName)
        => owner.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.True;

    private static string P804NormalizeAssignmentId(string? value)
    {
        var normalized = value?.Trim();
        if (!ObjectId.TryParse(normalized, out var parsed))
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_READ_FORBIDDEN,
                new
                {
                    reason =
                        "BASIC_SUMMARY_CONFIG_ACCESS_DENIED"
                });
        }
        return parsed.ToString();
    }

    private static string P804NormalizeTemplateId(string? value)
    {
        var normalized = value?.Trim();
        if (!ObjectId.TryParse(normalized, out var parsed))
        {
            throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
                new
                {
                    reason =
                        "BASIC_SUMMARY_CONFIG_TEMPLATE_NOT_FOUND"
                });
        }
        return parsed.ToString();
    }

    internal static string P804OwnerId(
        string assignmentId,
        string templateId)
        => $"{assignmentId}:{templateId}";

    private static string P804DeterministicId(
        string ownerId,
        string purpose)
        => StatConfigCanonicalJson.HashUtf8(
                $"{StatConfigOwnerKinds.BasicSummary}\0" +
                $"{ownerId}\0{purpose}")
            [..24];

    private static AppException P804Integrity(string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new { reason });
}
