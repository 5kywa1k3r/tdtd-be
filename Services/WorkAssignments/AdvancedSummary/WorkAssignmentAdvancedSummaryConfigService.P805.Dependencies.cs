using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public sealed partial class WorkAssignmentAdvancedSummaryConfigService
{
    private static IReadOnlyList<string> P805BuildBasePins(
        P805OwnerContext owner)
        => new[]
            {
                $"DYNAMIC_FORM_SCHEMA:{owner.Template.Id}:" +
                $"{owner.Template.VersionNo}:" +
                owner.Published.Sha256.ToLowerInvariant(),
                $"DYNAMIC_FORM_SECTION:{owner.Template.Id}:" +
                $"{owner.Section.SectionId}:" +
                owner.Section.ContentHash.ToLowerInvariant()
            }
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

    private async Task<IReadOnlyList<string>>
        P805ResolveDependencyPinsAsync(
            IClientSessionHandle session,
            P805OwnerContext owner,
            WorkAssignmentAdvancedSummaryConfigPayload payload,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ct.ThrowIfCancellationRequested();
        using var document = P805ParseSectionFields(
            owner.Section.FieldsJson);
        var fields =
            new Dictionary<string, JsonElement>(
                StringComparer.Ordinal);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw P805Integrity(
                    "ADVANCED_SUMMARY_SECTION_FIELD_INVALID");
            }
            var fieldId = P805ReadRequiredFieldString(
                element,
                "id",
                "ADVANCED_SUMMARY_SECTION_FIELD_ID_INVALID");
            if (!fields.TryAdd(fieldId, element.Clone()))
            {
                throw P805Integrity(
                    "ADVANCED_SUMMARY_SECTION_FIELD_DUPLICATE");
            }
        }

        var pins = P805BuildBasePins(owner).ToList();
        var targets = payload.Sections![0].Targets!;
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            if (!fields.TryGetValue(target.FieldId!, out var field))
            {
                throw P805Schema(
                    $"$.payload.sections[0].targets[{index}].fieldId",
                    "ADVANCED_SUMMARY_TARGET_FIELD_NOT_FOUND");
            }
            var rawType = P805ReadRequiredFieldString(
                field,
                "type",
                "ADVANCED_SUMMARY_SECTION_FIELD_TYPE_INVALID");
            var actualType = P805MapFieldDataType(
                rawType,
                index);
            if (!string.Equals(
                    actualType,
                    target.DataType,
                    StringComparison.Ordinal))
            {
                throw P805Schema(
                    $"$.payload.sections[0].targets[{index}].dataType",
                    "ADVANCED_SUMMARY_TARGET_DATA_TYPE_MISMATCH");
            }

            pins.Add(
                $"DYNAMIC_FORM_FIELD:{owner.Template.Id}:" +
                $"{owner.Section.SectionId}:{target.FieldId}:" +
                $"{actualType}:" +
                StatConfigCanonicalJson.HashUtf8(
                    StatConfigCanonicalJson.CanonicalizeElement(
                        field)));
        }

        pins.AddRange(await P805NativeDependencyPinsAsync(session, owner, payload, ct));
        IReadOnlyList<string> result = pins
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        return result;
    }

    private async Task P805EnsureDependenciesCurrentAsync(
        IClientSessionHandle session,
        P805OwnerContext owner,
        WorkAssignmentAdvancedSummaryConfigPayload payload,
        IReadOnlyList<string> expectedPins,
        CancellationToken ct)
    {
        IReadOnlyList<string> actualPins;
        try
        {
            actualPins = await P805ResolveDependencyPinsAsync(
                session,
                owner,
                payload,
                ct);
        }
        catch (AppException ex) when (
            ex.Code == AppErrorCode.STAT_CONFIG_SCHEMA_INVALID ||
            ex.Code ==
            AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND ||
            ex.Code ==
            AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID)
        {
            throw P805Integrity(
                "ADVANCED_SUMMARY_CONFIG_DEPENDENCY_STALE");
        }
        if (!actualPins.SequenceEqual(
                expectedPins,
                StringComparer.Ordinal))
        {
            throw P805Integrity(
                "ADVANCED_SUMMARY_CONFIG_DEPENDENCY_STALE");
        }
    }

    private static JsonDocument P805ParseSectionFields(string? json)
    {
        try
        {
            var document = JsonDocument.Parse(json ?? "[]");
            if (document.RootElement.ValueKind !=
                JsonValueKind.Array)
            {
                document.Dispose();
                throw P805Integrity(
                    "ADVANCED_SUMMARY_SECTION_FIELDS_INVALID");
            }
            return document;
        }
        catch (AppException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw P805Integrity(
                "ADVANCED_SUMMARY_SECTION_FIELDS_INVALID");
        }
    }

    private static string P805ReadRequiredFieldString(
        JsonElement owner,
        string propertyName,
        string reason)
    {
        if (!owner.TryGetProperty(
                propertyName,
                out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw P805Integrity(reason);
        }
        var result = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(result) ||
            result.Length > 256 ||
            result.Any(char.IsControl))
        {
            throw P805Integrity(reason);
        }
        return result;
    }

    private static string P805MapFieldDataType(
        string rawType,
        int targetIndex)
        => rawType switch
        {
            "number" or "NUMBER" => "NUMBER",
            "date" or "fullDate" or
                "DATE" or "FULL_DATE" => "DATE",
            "boolean" or "BOOLEAN" => "BOOLEAN",
            "singleSelect" or "multiSelect" or
                "stringList" or "SINGLE_SELECT" or
                "MULTI_SELECT" or "STRING_LIST" => "CHOICE",
            "shortText" or "longText" or "richText" or
                "SHORT_TEXT" or "LONG_TEXT" or
                "RICH_TEXT" => "TEXT",
            _ => throw P805Schema(
                $"$.payload.sections[0].targets[{targetIndex}].fieldId",
                "ADVANCED_SUMMARY_TARGET_DATA_TYPE_UNRESOLVED")
        };

    private static WorkAssignmentAdvancedSummaryValidationReceipt
        P805BuildValidationReceipt(
            string ownerId,
            string configId,
            string versionId,
            int versionNo,
            long revision,
            string configHash,
            IReadOnlyList<string> dependencyPins,
            WorkAssignmentAdvancedSummaryConfigPayload payload)
    {
        var section = payload.Sections![0];
        var targetCount = section.Targets!.Count + (section.NativeTargets?.Count ?? 0);
        var targetLimit = section.IsCumulative!.Value
            ? WorkAssignmentAdvancedSummaryConfigContract
                .MaxCumulativeTargets
            : WorkAssignmentAdvancedSummaryConfigContract
                .MaxNonCumulativeTargets;
        var body = new P805ValidationReceiptBody(
            WorkAssignmentAdvancedSummaryConfigContract
                .ValidationContractVersion,
            WorkAssignmentAdvancedSummaryConfigContract.ValidationMode,
            ownerId,
            configId,
            versionId,
            versionNo,
            revision,
            configHash,
            dependencyPins,
            section.SectionId!,
            payload.SourceScope!.Mode!,
            section.IsCumulative.Value,
            targetCount,
            targetLimit,
            payload.HierarchyGrains!,
            payload.HierarchyGrains!.Count,
            WorkAssignmentAdvancedSummaryConfigContract
                .MaxHierarchyDepth,
            P805CanonicalPayloadBytes(payload),
            WorkAssignmentAdvancedSummaryConfigContract
                .MaxCanonicalPayloadBytes,
            WorkAssignmentAdvancedSummaryConfigContract
                .RuntimeBlockedUntilP9,
            PreviewRead: false,
            PreviewWrite: false,
            HierarchyRead: false,
            HierarchyWrite: false);
        return new WorkAssignmentAdvancedSummaryValidationReceipt(
            P805ValidationReceiptId(
                ownerId, versionId, revision, configHash),
            body.ContractVersion,
            body.ValidationMode,
            body.OwnerId,
            body.ConfigId,
            body.VersionId,
            body.VersionNo,
            body.Revision,
            body.ConfigHash,
            body.DependencyPins,
            body.SectionId,
            body.SourceScopeMode,
            body.IsCumulative,
            body.TargetCount,
            body.TargetLimit,
            body.HierarchyGrains,
            body.HierarchyDepth,
            body.MaxHierarchyDepth,
            body.CanonicalPayloadBytes,
            body.MaxCanonicalPayloadBytes,
            body.RuntimeEligibility,
            body.PreviewRead,
            body.PreviewWrite,
            body.HierarchyRead,
            body.HierarchyWrite);
    }

    internal static string P805ValidationReceiptId(
        string ownerId,
        string versionId,
        long revision,
        string configHash)
        => StatConfigCanonicalJson.HashUtf8(
            $"ADVANCED_SUMMARY_VALIDATION\0{ownerId}\0" +
            $"{versionId}\0{revision}\0{configHash}");

    private static WorkAssignmentAdvancedSummaryValidationReceipt?
        P805ReadValidationReceipt(
            WorkAssignmentAdvancedSummaryConfig row,
            WorkAssignmentAdvancedSummaryConfigPayload payload)
    {
        if (row.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Draft)
            return null;
        WorkAssignmentAdvancedSummaryValidationReceipt receipt;
        try
        {
            using var document = JsonDocument.Parse(
                row.ValidationReceiptJson!);
            receipt = StatConfigCanonicalJson.DeserializeStrict<
                WorkAssignmentAdvancedSummaryValidationReceipt>(
                document.RootElement);
        }
        catch (AppException)
        {
            throw P805Integrity(
                "ADVANCED_SUMMARY_VALIDATION_RECEIPT_INVALID");
        }
        catch (JsonException)
        {
            throw P805Integrity(
                "ADVANCED_SUMMARY_VALIDATION_RECEIPT_INVALID");
        }

        var canonical =
            StatConfigCanonicalJson.Canonicalize(receipt);
        if (!string.Equals(
                StatConfigCanonicalJson.HashUtf8(canonical),
                row.ValidationReceiptHash,
                StringComparison.Ordinal))
        {
            throw P805Integrity(
                "ADVANCED_SUMMARY_VALIDATION_RECEIPT_HASH_MISMATCH");
        }
        var lockedRevision =
            row.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Archived
                ? row.Revision - 1
                : row.Revision;
        if (lockedRevision < 1)
        {
            throw P805Integrity(
                "ADVANCED_SUMMARY_VALIDATION_RECEIPT_REVISION_INVALID");
        }
        var expected = P805BuildValidationReceipt(
            P805OwnerId(
                row.AssignmentId,
                row.DynamicFormTemplateId,
                row.SectionId),
            row.ConfigId,
            row.Id,
            row.VersionNo,
            lockedRevision,
            row.ConfigHash,
            row.DependencyPins,
            payload);
        if (!string.Equals(
                canonical,
                StatConfigCanonicalJson.Canonicalize(expected),
                StringComparison.Ordinal))
        {
            throw P805Integrity(
                "ADVANCED_SUMMARY_VALIDATION_RECEIPT_CONTENT_MISMATCH");
        }
        return receipt;
    }

    private sealed record P805ValidationReceiptBody(
        string ContractVersion,
        string ValidationMode,
        string OwnerId,
        string ConfigId,
        string VersionId,
        int VersionNo,
        long Revision,
        string ConfigHash,
        IReadOnlyList<string> DependencyPins,
        string SectionId,
        string SourceScopeMode,
        bool IsCumulative,
        int TargetCount,
        int TargetLimit,
        IReadOnlyList<string> HierarchyGrains,
        int HierarchyDepth,
        int MaxHierarchyDepth,
        int CanonicalPayloadBytes,
        int MaxCanonicalPayloadBytes,
        string RuntimeEligibility,
        bool PreviewRead,
        bool PreviewWrite,
        bool HierarchyRead,
        bool HierarchyWrite);
}
