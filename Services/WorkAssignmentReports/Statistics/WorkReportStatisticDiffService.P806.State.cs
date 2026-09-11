using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public sealed partial class WorkReportStatisticDiffService
{
    private async Task<P806ConfigState> P806LoadStateAsync(
        IClientSessionHandle? session,
        P806OwnerContext owner,
        CancellationToken ct)
    {
        var ownerId = P806OwnerId(owner);
        var configId = P806DeterministicId(ownerId, "CONFIG");
        var filter = Builders<WorkReportStatisticDiffConfig>.Filter;
        var query =
            filter.Eq(item => item.ConfigId, configId) &
            filter.Eq(item => item.AssignmentId, owner.Assignment.Id) &
            filter.Eq(item => item.DynamicFormTemplateId, owner.Template.Id) &
            filter.Eq(item => item.IsDeleted, false);
        var find = session is null
            ? _ctx.WorkReportStatisticDiffConfigs.Find(query)
            : _ctx.WorkReportStatisticDiffConfigs.Find(session, query);
        var rows = await find
            .SortBy(item => item.VersionNo)
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return new P806ConfigState(
                null,
                configId,
                P806DeterministicId(ownerId, "VERSION:1"),
                null,
                1,
                0,
                StatConfigStatuses.Draft,
                StatConfigCanonicalJson.EmptyConfigHash,
                Array.Empty<string>(),
                P806VirtualEmptyPayload(),
                Array.Empty<WorkReportStatisticDiffConfig>(),
                null,
                null,
                IsVirtual: true);
        }

        P806ValidateRows(owner, rows, configId);
        var current = rows[^1];
        return new P806ConfigState(
            current,
            configId,
            current.Id,
            current.PreviousVersionId,
            current.VersionNo,
            current.Revision,
            current.Status!,
            current.ConfigHash!,
            current.DependencyPins,
            P806DeserializeStoredPayload(current.ConfigJson),
            rows,
            current.LockedAtUtc,
            current.LockedByUserId,
            IsVirtual: false);
    }

    private static void P806ValidateRows(
        P806OwnerContext owner,
        IReadOnlyList<WorkReportStatisticDiffConfig> rows,
        string configId)
    {
        var ownerId = P806OwnerId(owner);
        var draftCount = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var versionNo = index + 1;
            var expectedId = P806DeterministicId(
                ownerId,
                $"VERSION:{versionNo}");
            var previous = index == 0 ? null : rows[index - 1].Id;
            if (!string.Equals(row.Id, expectedId, StringComparison.Ordinal) ||
                !string.Equals(row.ConfigId, configId, StringComparison.Ordinal) ||
                !string.Equals(row.PreviousVersionId, previous, StringComparison.Ordinal) ||
                row.VersionNo != versionNo ||
                row.Revision < 0 ||
                !string.Equals(row.WorkId, owner.Assignment.WorkId, StringComparison.Ordinal) ||
                !string.Equals(row.AssignmentId, owner.Assignment.Id, StringComparison.Ordinal) ||
                !string.Equals(row.DynamicFormTemplateId, owner.Template.Id, StringComparison.Ordinal) ||
                row.Status is not (StatConfigStatuses.Draft or StatConfigStatuses.Locked) ||
                !P806IsCanonicalSha256(row.ConfigHash) ||
                row.DependencyPins is null ||
                !row.DependencyPins.SequenceEqual(
                    row.DependencyPins
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal),
                    StringComparer.Ordinal) ||
                row.IsDeleted || !row.IsActive)
            {
                throw P806Integrity("DIFF_CONFIG_IDENTITY_INVALID");
            }
            var payload = P806DeserializeStoredPayload(row.ConfigJson);
            if (!string.Equals(
                    row.ConfigJson,
                    StatConfigCanonicalJson.Canonicalize(payload),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    row.ConfigHash,
                    P806ComputeConfigHash(ownerId, payload, row.DependencyPins),
                    StringComparison.Ordinal))
            {
                throw P806Integrity("DIFF_CONFIG_HASH_MISMATCH");
            }
            if (row.Status == StatConfigStatuses.Draft)
            {
                draftCount++;
                if (index != rows.Count - 1 ||
                    row.LockedAtUtc is not null ||
                    row.LockedByUserId is not null)
                {
                    throw P806Integrity("DIFF_CONFIG_DRAFT_STATE_INVALID");
                }
            }
            else if (!row.LockedAtUtc.HasValue ||
                     !P806IsCanonicalObjectId(row.LockedByUserId))
            {
                throw P806Integrity("DIFF_CONFIG_LOCK_STATE_INVALID");
            }
        }
        if (draftCount > 1)
            throw P806Integrity("DIFF_CONFIG_ACTIVE_STATE_AMBIGUOUS");
    }

    private static P806ConfigState P806SelectVersion(
        P806ConfigState state,
        WorkReportStatisticDiffConfig row)
        => state with
        {
            Entity = row,
            VersionId = row.Id,
            PreviousVersionId = row.PreviousVersionId,
            VersionNo = row.VersionNo,
            Revision = row.Revision,
            Status = row.Status!,
            ConfigHash = row.ConfigHash!,
            DependencyPins = row.DependencyPins,
            Payload = P806DeserializeStoredPayload(row.ConfigJson),
            LockedAtUtc = row.LockedAtUtc,
            LockedByUserId = row.LockedByUserId,
            IsVirtual = false
        };

    private async Task<P806ConfigState> P806ApplyPutAsync(
        IClientSessionHandle session,
        P806OwnerContext owner,
        P806ConfigState current,
        WorkReportStatisticDiffConfigPayload payload,
        MeResponse me,
        DateTime now,
        CancellationToken ct)
    {
        if (!current.IsVirtual &&
            current.Status == StatConfigStatuses.Locked)
        {
            throw P806StateConflict(current, "DIFF_CONFIG_VERSION_LOCKED");
        }
        var dependencyPins = await P806ResolveDependencyPinsAsync(
            session,
            owner,
            payload,
            me,
            ct);
        return current with
        {
            Revision = checked(current.Revision + 1),
            Status = StatConfigStatuses.Draft,
            ConfigHash = P806ComputeConfigHash(
                P806OwnerId(owner),
                payload,
                dependencyPins),
            DependencyPins = dependencyPins,
            Payload = payload,
            LockedAtUtc = null,
            LockedByUserId = null,
            IsVirtual = false
        };
    }

    private async Task<P806ConfigState> P806ApplyLockAsync(
        IClientSessionHandle session,
        P806OwnerContext owner,
        P806ConfigState current,
        WorkReportStatisticDiffEmptyCommandPayload payload,
        MeResponse me,
        DateTime now,
        CancellationToken ct)
    {
        if (current.IsVirtual)
            throw P806StateConflict(current, "DIFF_CONFIG_DRAFT_NOT_PERSISTED");
        if (current.Status == StatConfigStatuses.Locked)
            throw P806StateConflict(current, "DIFF_CONFIG_VERSION_ALREADY_LOCKED");
        await P806EnsureDependenciesCurrentAsync(
            session,
            owner,
            current,
            me,
            ct);
        return current with
        {
            Revision = checked(current.Revision + 1),
            Status = StatConfigStatuses.Locked,
            LockedAtUtc = now,
            LockedByUserId = me.Id,
            IsVirtual = false
        };
    }

    private async Task<P806ConfigState> P806ApplyNextDraftAsync(
        IClientSessionHandle session,
        P806OwnerContext owner,
        P806ConfigState current,
        WorkReportStatisticDiffEmptyCommandPayload payload,
        MeResponse me,
        DateTime now,
        CancellationToken ct)
    {
        if (current.IsVirtual || current.Status != StatConfigStatuses.Locked)
            throw P806StateConflict(current, "DIFF_CONFIG_VERSION_NOT_LOCKED");
        await P806EnsureDependenciesCurrentAsync(
            session,
            owner,
            current,
            me,
            ct);
        var versionNo = checked(current.VersionNo + 1);
        return current with
        {
            Entity = null,
            VersionId = P806DeterministicId(
                P806OwnerId(owner),
                $"VERSION:{versionNo}"),
            PreviousVersionId = current.VersionId,
            VersionNo = versionNo,
            Revision = 0,
            Status = StatConfigStatuses.Draft,
            LockedAtUtc = null,
            LockedByUserId = null,
            IsVirtual = false
        };
    }

    private async Task P806EnsureDependenciesCurrentAsync(
        IClientSessionHandle session,
        P806OwnerContext owner,
        P806ConfigState current,
        MeResponse me,
        CancellationToken ct)
    {
        IReadOnlyList<string> resolved;
        try
        {
            resolved = await P806ResolveDependencyPinsAsync(
                session,
                owner,
                current.Payload,
                me,
                ct);
        }
        catch (AppException ex) when (
            ex.Code is AppErrorCode.STAT_CONFIG_SCHEMA_INVALID or
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT)
        {
            throw P806StateConflict(current, "DIFF_CONFIG_DEPENDENCY_STALE");
        }
        var hash = P806ComputeConfigHash(
            P806OwnerId(owner),
            current.Payload,
            resolved);
        if (!resolved.SequenceEqual(
                current.DependencyPins,
                StringComparer.Ordinal) ||
            !string.Equals(hash, current.ConfigHash, StringComparison.Ordinal))
        {
            throw P806StateConflict(current, "DIFF_CONFIG_DEPENDENCY_STALE");
        }
    }

    private async Task<P806ConfigState> P806PersistStateAsync(
        IClientSessionHandle session,
        P806OwnerContext owner,
        P806ConfigState current,
        P806ConfigState next,
        string actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        var sameVersion = string.Equals(
            current.VersionId,
            next.VersionId,
            StringComparison.Ordinal);
        var prior = sameVersion ? current.Entity : null;
        var entity = new WorkReportStatisticDiffConfig
        {
            Id = next.VersionId,
            ConfigId = next.ConfigId,
            WorkId = owner.Assignment.WorkId,
            AssignmentId = owner.Assignment.Id,
            DynamicFormTemplateId = owner.Template.Id,
            Name = next.Payload.Name ?? string.Empty,
            ConfigJson = StatConfigCanonicalJson.Canonicalize(next.Payload),
            PreviousVersionId = next.PreviousVersionId,
            VersionNo = next.VersionNo,
            Revision = next.Revision,
            Status = next.Status,
            ConfigHash = next.ConfigHash,
            DependencyPins = next.DependencyPins.ToList(),
            LockedAtUtc = next.LockedAtUtc,
            LockedByUserId = next.LockedByUserId,
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = prior?.CreatedAtUtc ?? now,
            CreatedByUserId = prior?.CreatedByUserId ?? actorUserId,
            UpdatedAtUtc = now,
            UpdatedByUserId = actorUserId
        };
        if (prior is null)
        {
            await _ctx.WorkReportStatisticDiffConfigs.InsertOneAsync(
                session,
                entity,
                cancellationToken: ct);
        }
        else
        {
            var filter = Builders<WorkReportStatisticDiffConfig>.Filter;
            var cas =
                filter.Eq(item => item.Id, current.VersionId) &
                filter.Eq(item => item.ConfigId, current.ConfigId) &
                filter.Eq(item => item.Revision, current.Revision) &
                filter.Eq(item => item.ConfigHash, current.ConfigHash) &
                filter.Eq(item => item.Status, StatConfigStatuses.Draft) &
                filter.Eq(item => item.IsDeleted, false);
            var result = await _ctx.WorkReportStatisticDiffConfigs.ReplaceOneAsync(
                session,
                cas,
                entity,
                new ReplaceOptions { IsUpsert = false },
                ct);
            if (result.MatchedCount != 1)
            {
                throw AppExceptionFactory.Create(
                    AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                    new { reason = "DIFF_CONFIG_CONCURRENT_WRITE" });
            }
        }
        var rows = current.Rows.ToList();
        var existingIndex = rows.FindIndex(row => row.Id == entity.Id);
        if (existingIndex >= 0)
            rows[existingIndex] = entity;
        else
            rows.Add(entity);
        rows.Sort((left, right) => left.VersionNo.CompareTo(right.VersionNo));
        return next with { Entity = entity, Rows = rows };
    }

    private async Task<IReadOnlyList<string>>
        P806ResolveDependencyPinsAsync(
            IClientSessionHandle session,
            P806OwnerContext owner,
            WorkReportStatisticDiffConfigPayload payload,
            MeResponse me,
            CancellationToken ct)
    {
        var template = owner.Template;
        DynamicFormPublishedSchemaSnapshot published;
        try
        {
            published = DynamicFormPublishedSchemaSnapshotBuilder
                .ValidateAgainstTemplate(template);
        }
        catch (InvalidOperationException)
        {
            throw P806Integrity("DIFF_CONFIG_PUBLISHED_SCHEMA_INVALID");
        }
        var trusted = DynamicFormStatisticConfigCommandService
            .GetP804TrustedPersistedView(template);
        if (trusted is null)
        {
            throw P806Schema(
                "$.payload.left.selector",
                "DIFF_TEMPLATE_STAT_CONFIG_REQUIRED");
        }
        List<DynamicFormStatisticFieldConfigDto> fields;
        List<DynamicFormStatisticTableConfigDto> tables;
        try
        {
            fields = JsonSerializer.Deserialize<
                         List<DynamicFormStatisticFieldConfigDto>>(
                         trusted.FieldSectionJson,
                         StatConfigCanonicalJson.StrictJsonOptions) ?? new();
            tables = JsonSerializer.Deserialize<
                         List<DynamicFormStatisticTableConfigDto>>(
                         trusted.TableSectionJson,
                         StatConfigCanonicalJson.StrictJsonOptions) ?? new();
        }
        catch (JsonException)
        {
            throw P806Integrity("DIFF_TEMPLATE_STAT_CONFIG_INVALID");
        }
        P806ValidateScopeOwner(
            payload.Left!.SourceScope!,
            owner.Assignment,
            "$.payload.left.sourceScope");
        P806ValidateScopeOwner(
            payload.Right!.SourceScope!,
            owner.Assignment,
            "$.payload.right.sourceScope");
        P806ValidateSelector(
            payload.Left!.Selector!,
            "$.payload.left.selector",
            fields,
            tables);
        P806ValidateSelector(
            payload.Right!.Selector!,
            "$.payload.right.selector",
            fields,
            tables);
        await P806ValidateSelectedLabelsAsync(
            session,
            me,
            payload,
            fields,
            tables,
            ct);
        var pins = new List<string>
        {
            $"DYNAMIC_FORM_SCHEMA:{template.Id}:{template.VersionNo}:" +
            $"{template.Revision}:{published.Sha256}",
            $"DYNAMIC_FORM_STAT_CONFIG:{trusted.ConfigId}:" +
            $"{trusted.VersionId}:{trusted.VersionNo}:{trusted.Revision}:" +
            trusted.ConfigHash
        };
        pins.AddRange(trusted.DependencyPins);
        return pins
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    private static void P806ValidateSelector(
        WorkReportStatisticDiffSelectorPayload selector,
        string path,
        IReadOnlyList<DynamicFormStatisticFieldConfigDto> fields,
        IReadOnlyList<DynamicFormStatisticTableConfigDto> tables)
    {
        string actualType;
        if (selector.ConceptKind == WorkReportStatisticDiffConfigContract.Field)
        {
            var matches = fields
                .Where(field => field.FieldId == selector.ConceptKey)
                .ToList();
            if (matches.Count != 1 || !matches[0].IsStatistic)
                throw P806Schema($"{path}.conceptKey", "DIFF_FIELD_NOT_FOUND");
            var field = matches[0];
            if (!field.StatisticLabelCodes.Contains(
                    selector.ConceptCode!,
                    StringComparer.Ordinal))
            {
                throw P806Schema(
                    $"{path}.conceptCode",
                    "DIFF_FIELD_CONCEPT_NOT_FOUND");
            }
            actualType = P806MapFieldDataType(
                field.FieldType,
                $"{path}.dataType");
        }
        else
        {
            var key = selector.ConceptKey!;
            var separator = key.IndexOf(':');
            var blockId = key[..separator];
            var localKey = key[(separator + 1)..];
            var tableMatches = tables
                .Where(table => table.BlockId == blockId)
                .ToList();
            if (tableMatches.Count != 1 || tableMatches[0].StatisticsDisabled)
                throw P806Schema($"{path}.conceptKey", "DIFF_TABLE_NOT_FOUND");
            var table = tableMatches[0];
            if (selector.ConceptKind == WorkReportStatisticDiffConfigContract.TableMetric)
            {
                var metrics = table.Metrics
                    .Where(metric => metric.MetricKey == localKey)
                    .ToList();
                if (metrics.Count != 1)
                {
                    throw P806Schema(
                        $"{path}.conceptKey",
                        "DIFF_TABLE_METRIC_NOT_FOUND");
                }
                var labelMatch = table.MetricLabelTargets.Any(target =>
                    target.MetricKey == localKey &&
                    target.StatisticLabelCode == selector.ConceptCode);
                if (!labelMatch)
                {
                    throw P806Schema(
                        $"{path}.conceptCode",
                        "DIFF_TABLE_METRIC_CONCEPT_NOT_FOUND");
                }
                actualType = P806MapTableDataType(
                    metrics[0].DataType,
                    $"{path}.dataType");
            }
            else
            {
                if (!table.AllowedRowLabelCodes.Contains(
                        localKey,
                        StringComparer.Ordinal) ||
                    !string.Equals(
                        localKey,
                        selector.ConceptCode,
                        StringComparison.Ordinal))
                {
                    throw P806Schema(
                        $"{path}.conceptKey",
                        "DIFF_ROW_LABEL_NOT_ALLOWED");
                }
                var snapshots = table.RowLabelSnapshots
                    .Where(snapshot =>
                        snapshot.Code == localKey &&
                        snapshot.IsActive &&
                        snapshot.Usage == LabelUsages.TableTarget)
                    .ToList();
                if (snapshots.Count != 1)
                {
                    throw P806Schema(
                        $"{path}.conceptKey",
                        "DIFF_ROW_LABEL_NOT_ALLOWED");
                }
                actualType = P806MapTableDataType(
                    table.RowLabelDataType,
                    $"{path}.dataType");
            }
        }
        if (!string.Equals(
                actualType,
                selector.DataType,
                StringComparison.Ordinal))
        {
            throw P806Schema(
                $"{path}.dataType",
                "DIFF_SELECTOR_DATA_TYPE_MISMATCH");
        }
    }

    private static string P806MapFieldDataType(string value, string path)
        => value switch
        {
            DynamicFormStatisticFieldTypes.Number => WorkReportStatisticDiffConfigContract.Number,
            DynamicFormStatisticFieldTypes.Date or
                DynamicFormStatisticFieldTypes.FullDate => WorkReportStatisticDiffConfigContract.Date,
            DynamicFormStatisticFieldTypes.Boolean => WorkReportStatisticDiffConfigContract.Boolean,
            DynamicFormStatisticFieldTypes.SingleSelect or
                DynamicFormStatisticFieldTypes.MultiSelect => WorkReportStatisticDiffConfigContract.Choice,
            DynamicFormStatisticFieldTypes.ShortText or
                DynamicFormStatisticFieldTypes.LongText or
                DynamicFormStatisticFieldTypes.StringList or
                DynamicFormStatisticFieldTypes.RichText => WorkReportStatisticDiffConfigContract.Text,
            _ => throw P806Schema(path, "DIFF_SELECTOR_DATA_TYPE_UNSUPPORTED")
        };

    private static string P806MapTableDataType(string value, string path)
        => value switch
        {
            "NUMBER" or "CURRENCY" or "PERCENT" => WorkReportStatisticDiffConfigContract.Number,
            "DATE" or "FULL_DATE" => WorkReportStatisticDiffConfigContract.Date,
            "BOOLEAN" => WorkReportStatisticDiffConfigContract.Boolean,
            "MULTI_SELECT" or "SINGLE_SELECT" => WorkReportStatisticDiffConfigContract.Choice,
            "SHORT_TEXT" or "LONG_TEXT" or "STRING_LIST" or "TEXT" or "STRING" =>
                WorkReportStatisticDiffConfigContract.Text,
            _ => throw P806Schema(path, "DIFF_SELECTOR_DATA_TYPE_UNSUPPORTED")
        };

    internal static string P806ComputeConfigHash(
        string ownerId,
        WorkReportStatisticDiffConfigPayload payload,
        IReadOnlyList<string> dependencyPins)
        => StatConfigCanonicalJson.HashObject(new
        {
            ownerId,
            payload,
            dependencyPins
        });

    private static WorkReportStatisticDiffConfigPayload
        P806DeserializeStoredPayload(string configJson)
    {
        try
        {
            using var document = JsonDocument.Parse(configJson);
            return P806NormalizePayload(
                StatConfigCanonicalJson.DeserializeStrict<
                    WorkReportStatisticDiffConfigPayload>(
                    document.RootElement));
        }
        catch (AppException ex) when (
            ex.Code == AppErrorCode.STAT_CONFIG_SCHEMA_INVALID)
        {
            throw P806Integrity("DIFF_CONFIG_JSON_INVALID");
        }
        catch (JsonException)
        {
            throw P806Integrity("DIFF_CONFIG_JSON_INVALID");
        }
    }

    private static WorkReportStatisticDiffConfigReadback P806ToReadback(
        P806OwnerContext owner,
        P806ConfigState state,
        MeResponse me,
        string? receiptId)
    {
        var canManage = RoleGuard.IsSystemAdmin(me) ||
            string.Equals(
                owner.Assignment.CreatedByUserId,
                me.Id,
                StringComparison.Ordinal);
        var canLock = RoleGuard.IsSystemAdmin(me) ||
            string.Equals(
                owner.Assignment.CreatedByUserId,
                me.Id,
                StringComparison.Ordinal);
        return new WorkReportStatisticDiffConfigReadback(
            P806Identity(owner, state),
            state.Payload,
            new StatConfigPermissionSet(
                CanReadConfig: true,
                CanManageDraft: canManage,
                CanLockVersion: canLock,
                CanViewResult: false,
                CanReadDiagnostics: false),
            WorkReportStatisticDiffConfigContract.RuntimeBlockedUntilP9,
            state.IsVirtual,
            state.PreviousVersionId,
            state.Rows
                .OrderBy(row => row.VersionNo)
                .Select(row => P806ToVersionDto(owner, row))
                .ToList(),
            receiptId);
    }

    private static StatConfigIdentity P806Identity(
        P806OwnerContext owner,
        P806ConfigState state)
        => new(
            StatConfigOwnerKinds.Diff,
            P806OwnerId(owner),
            state.ConfigId,
            state.VersionId,
            state.VersionNo,
            state.Revision,
            state.Status,
            state.ConfigHash,
            state.DependencyPins);

    private static WorkReportStatisticDiffConfigVersionDto
        P806ToVersionDto(
            P806OwnerContext owner,
            WorkReportStatisticDiffConfig row)
    {
        var payload = P806DeserializeStoredPayload(row.ConfigJson);
        return new WorkReportStatisticDiffConfigVersionDto(
            new StatConfigIdentity(
                StatConfigOwnerKinds.Diff,
                P806OwnerId(owner),
                row.ConfigId!,
                row.Id,
                row.VersionNo,
                row.Revision,
                row.Status!,
                row.ConfigHash!,
                row.DependencyPins),
            payload,
            row.PreviousVersionId,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.LockedAtUtc,
            row.LockedByUserId);
    }

    private static bool P806IsCanonicalObjectId(string? value)
        => ObjectId.TryParse(value, out var parsed) &&
           string.Equals(value, parsed.ToString(), StringComparison.Ordinal);

    private static bool P806IsCanonicalSha256(string? value)
        => value is { Length: 64 } &&
           value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record P806ConfigState(
        WorkReportStatisticDiffConfig? Entity,
        string ConfigId,
        string VersionId,
        string? PreviousVersionId,
        int VersionNo,
        long Revision,
        string Status,
        string ConfigHash,
        IReadOnlyList<string> DependencyPins,
        WorkReportStatisticDiffConfigPayload Payload,
        IReadOnlyList<WorkReportStatisticDiffConfig> Rows,
        DateTime? LockedAtUtc,
        string? LockedByUserId,
        bool IsVirtual);
}
