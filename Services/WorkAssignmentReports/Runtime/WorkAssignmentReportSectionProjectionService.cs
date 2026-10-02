using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.WorkAssignmentReports.Runtime;

public interface IWorkAssignmentReportSectionProjectionService
{
    Task<IReadOnlyList<WorkAssignmentReportSection>> EnsureCurrentAndVerifyAsync(
        WorkAssignmentReport report,
        IReadOnlyCollection<WorkAssignmentReportSection> observedSections,
        string actorUserId,
        DateTime projectedAtUtc,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkAssignmentReportSection>> ProjectCurrentAndVerifyAsync(
        WorkAssignmentReport report,
        string actorUserId,
        DateTime projectedAtUtc,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkAssignmentReportSection>> ProjectAndVerifyAsync(
        WorkAssignmentReport report,
        string? fieldValuesJson,
        string? tableValuesJson,
        string actorUserId,
        DateTime projectedAtUtc,
        CancellationToken ct = default,
        IClientSessionHandle? session = null);
}

/// <summary>
/// Canonical writer for report-section read models. A successful return guarantees that the
/// active projection is an exact, current projection of the report's immutable published form.
/// </summary>
public sealed class WorkAssignmentReportSectionProjectionService
    : IWorkAssignmentReportSectionProjectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly MongoDbContext _ctx;
    private readonly IWorkReportPayloadReader _payloadReader;

    public WorkAssignmentReportSectionProjectionService(
        MongoDbContext ctx,
        IWorkReportPayloadReader payloadReader)
    {
        _ctx = ctx;
        _payloadReader = payloadReader;
    }

    public async Task<IReadOnlyList<WorkAssignmentReportSection>> EnsureCurrentAndVerifyAsync(
        WorkAssignmentReport report,
        IReadOnlyCollection<WorkAssignmentReportSection> observedSections,
        string actorUserId,
        DateTime projectedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(observedSections);

        var contract = await LoadPublishedTemplateContractAsync(report, ct, session: null);
        if (contract is null)
            return Order(observedSections);

        var payloadSnapshot = await LoadCurrentPayloadAsync(report, ct);
        var payload = ParseProjectionPayload(
            report,
            payloadSnapshot.FieldValuesJson,
            payloadSnapshot.TableValuesJson);
        if (IsCompleteProjection(report, contract.Sections, observedSections, payload, contract.EnumOptions))
            return Order(observedSections);

        return await ProjectAndVerifyCoreAsync(
            report,
            contract,
            payload,
            actorUserId,
            projectedAtUtc,
            ct,
            session: null);
    }

    public async Task<IReadOnlyList<WorkAssignmentReportSection>> ProjectCurrentAndVerifyAsync(
        WorkAssignmentReport report,
        string actorUserId,
        DateTime projectedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var contract = await LoadPublishedTemplateContractAsync(report, ct, session: null);
        if (contract is null)
            return Array.Empty<WorkAssignmentReportSection>();

        var payloadSnapshot = await LoadCurrentPayloadAsync(report, ct);
        var payload = ParseProjectionPayload(
            report,
            payloadSnapshot.FieldValuesJson,
            payloadSnapshot.TableValuesJson);
        return await ProjectAndVerifyCoreAsync(
            report,
            contract,
            payload,
            actorUserId,
            projectedAtUtc,
            ct,
            session: null);
    }

    public async Task<IReadOnlyList<WorkAssignmentReportSection>> ProjectAndVerifyAsync(
        WorkAssignmentReport report,
        string? fieldValuesJson,
        string? tableValuesJson,
        string actorUserId,
        DateTime projectedAtUtc,
        CancellationToken ct = default,
        IClientSessionHandle? session = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        var contract = await LoadPublishedTemplateContractAsync(report, ct, session);
        if (contract is null)
            return Array.Empty<WorkAssignmentReportSection>();

        return await ProjectAndVerifyCoreAsync(
            report,
            contract,
            ParseProjectionPayload(report, fieldValuesJson, tableValuesJson),
            actorUserId,
            projectedAtUtc,
            ct,
            session);
    }

    internal static bool IsProjectionCurrent(
        WorkAssignmentReport report,
        WorkAssignmentReportSection section)
        => (report.PayloadRevision <= 0 || WorkReportPayloadConsistency.IsReadyForStatisticProjection(report)) &&
           string.Equals(section.WorkAssignmentReportId, report.Id, StringComparison.Ordinal) &&
           string.Equals(section.WorkId, report.WorkId, StringComparison.Ordinal) &&
           string.Equals(section.WorkAssignmentId, report.WorkAssignmentId, StringComparison.Ordinal) &&
           string.Equals(section.WorkReportPeriodId, report.WorkReportPeriodId, StringComparison.Ordinal) &&
           section.SourcePayloadRevision == report.PayloadRevision &&
           section.SourceLifecycleRevision == report.LifecycleRevision &&
           string.Equals(Normalize(section.SourcePayloadHash), Normalize(report.PayloadHash), StringComparison.Ordinal) &&
           string.Equals(
               section.DynamicFlowMappingReceiptId,
               report.DynamicFlowMappingReceiptId,
               StringComparison.Ordinal) &&
           string.Equals(
               section.DynamicFlowMappingProvenanceId,
               report.DynamicFlowMappingProvenanceId,
               StringComparison.Ordinal) &&
           string.Equals(
               section.DynamicFlowMappingProvenanceHash,
               report.DynamicFlowMappingProvenanceHash,
               StringComparison.Ordinal) &&
           section.DynamicFlowMappingResultPayloadRevision ==
           report.DynamicFlowMappingResultPayloadRevision &&
           string.Equals(
               section.DynamicFlowMappingResultPayloadHash,
               report.DynamicFlowMappingResultPayloadHash,
               StringComparison.Ordinal) &&
           (report.PayloadUpdatedAtUtc is null ||
            SameMongoMillisecond(section.SourcePayloadUpdatedAtUtc, report.PayloadUpdatedAtUtc)) &&
           SameMongoMillisecond(section.SourceReportUpdatedAtUtc, report.UpdatedAtUtc) &&
           section.Status == report.Status &&
           string.Equals(section.DynamicFormTemplateId, report.DynamicFormTemplateId, StringComparison.Ordinal) &&
           string.Equals(section.DynamicFormTemplateCode, report.DynamicFormTemplateCode, StringComparison.Ordinal) &&
           string.Equals(section.DynamicFormTemplateName, report.DynamicFormTemplateName, StringComparison.Ordinal) &&
           string.Equals(section.DynamicFormFamilyId, report.DynamicFormFamilyId, StringComparison.Ordinal) &&
           section.DynamicFormVersionNo == report.DynamicFormVersionNo &&
           string.Equals(
               Normalize(section.DynamicFormSchemaHash),
               Normalize(report.DynamicFormSchemaHash),
               StringComparison.OrdinalIgnoreCase);

    internal static bool IsCompleteProjection(
        WorkAssignmentReport report,
        IReadOnlyCollection<DynamicFormSectionSnapshot> expectedSections,
        IReadOnlyCollection<WorkAssignmentReportSection> projectedSections,
        string? canonicalFieldValuesJson,
        string? canonicalTableValuesJson,
        IReadOnlyDictionary<string, RuntimeEnumOptionSet>? enumOptions = null)
        => IsCompleteProjection(
            report,
            expectedSections,
            projectedSections,
            ParseProjectionPayload(report, canonicalFieldValuesJson, canonicalTableValuesJson), enumOptions);

    private static bool IsCompleteProjection(
        WorkAssignmentReport report,
        IReadOnlyCollection<DynamicFormSectionSnapshot> expectedSections,
        IReadOnlyCollection<WorkAssignmentReportSection> projectedSections,
        ProjectionPayload canonicalPayload,
        IReadOnlyDictionary<string, RuntimeEnumOptionSet>? enumOptions)
    {
        ValidateNativeProjection(report, expectedSections, canonicalPayload.TableRoot, enumOptions);
        if (projectedSections.Count != expectedSections.Count)
            return false;

        var expectedIds = expectedSections
            .Select(x => x.SectionId)
            .ToHashSet(StringComparer.Ordinal);
        if (expectedIds.Count != expectedSections.Count)
            return false;
        var expectedById = expectedSections.ToDictionary(x => x.SectionId, StringComparer.Ordinal);

        var projectedById = projectedSections
            .GroupBy(x => x.SectionId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        if (projectedById.Count != expectedById.Count || projectedById.Any(x => x.Value.Length != 1))
            return false;

        foreach (var (sectionId, expected) in expectedById)
        {
            if (!projectedById.TryGetValue(sectionId, out var matches))
                return false;

            var actual = matches[0];
            var expectedFieldValues = BuildSectionFieldValues(canonicalPayload.FieldValues, expected.FieldIds);
            var expectedTableBlocks = BuildSectionTableBlocks(canonicalPayload.TableBlocksById, expected.BlockIds);
            var expectedNative = BuildSectionNativeValues(canonicalPayload.TableRoot, expected.NativeTableIds);
            var expectedPayloadHash = ComputeSectionPayloadHash(expectedFieldValues, expectedTableBlocks, expectedNative);
            var expectedHasData = JsonObjectHasData(expectedFieldValues) ||
                                  expectedTableBlocks.OfType<JsonObject>().Any(TableBlockHasEnteredData) || NativeHasData(expectedNative);
            if (!IsProjectionCurrent(report, actual) ||
                actual.IsDeleted ||
                actual.SectionTitle != expected.Title ||
                actual.SectionOrder != expected.Order ||
                actual.FieldCount != expected.FieldIds.Length ||
                actual.BlockCount != expected.BlockIds.Length ||
                actual.NativeTableCount != expected.NativeTableIds.Length ||
                actual.HasData != expectedHasData ||
                !string.Equals(actual.PayloadHash, expectedPayloadHash, StringComparison.Ordinal) ||
                !MatchesProjectedSectionPayload(
                    report,
                    expected.SchemaVersion,
                    canonicalPayload.TableRoot,
                    expectedFieldValues,
                    expectedTableBlocks,
                    expectedNative,
                    actual))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<IReadOnlyList<WorkAssignmentReportSection>> ProjectAndVerifyCoreAsync(
        WorkAssignmentReport report,
        PublishedTemplateContract contract,
        ProjectionPayload payload,
        string actorUserId,
        DateTime projectedAtUtc,
        CancellationToken ct,
        IClientSessionHandle? session)
    {
        if (string.IsNullOrWhiteSpace(report.Id))
            throw new ArgumentException("A persisted report id is required for section projection.", nameof(report));

        var actor = Normalize(actorUserId) ?? Normalize(report.UpdatedByUserId) ?? Normalize(report.CreatedByUserId)
            ?? throw new ArgumentException("An actor user id is required for section projection.", nameof(actorUserId));
        var now = NormalizeUtc(projectedAtUtc);
        var sectionFilter = Builders<WorkAssignmentReportSection>.Filter.Where(
            x => x.WorkAssignmentReportId == report.Id && !x.IsDeleted);
        var existingSections = session is null
            ? await _ctx.WorkAssignmentReportSections
                .Find(sectionFilter)
                .ToListAsync(ct)
            : await _ctx.WorkAssignmentReportSections
                .Find(session, sectionFilter)
                .ToListAsync(ct);
        var existingBySectionId = existingSections
            .GroupBy(x => x.SectionId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        var fieldValues = payload.FieldValues;
        var tableRoot = payload.TableRoot;
        var tableBlocksById = payload.TableBlocksById;
        ValidateNativeProjection(report, contract.Sections, tableRoot, contract.EnumOptions);

        foreach (var templateSection in contract.Sections)
        {
            var sectionValues = BuildSectionFieldValues(fieldValues, templateSection.FieldIds);
            var sectionBlocks = BuildSectionTableBlocks(tableBlocksById, templateSection.BlockIds);
            var sectionNative = BuildSectionNativeValues(tableRoot, templateSection.NativeTableIds);
            var hasData = JsonObjectHasData(sectionValues) ||
                          sectionBlocks.OfType<JsonObject>().Any(TableBlockHasEnteredData) || NativeHasData(sectionNative);
            var payloadHash = ComputeSectionPayloadHash(sectionValues, sectionBlocks, sectionNative);
            existingBySectionId.TryGetValue(templateSection.SectionId, out var existing);
            var changed = existing is null ||
                          !string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal);
            DateTime? lastUpdatedAtUtc = hasData
                ? (changed ? now : existing?.LastUpdatedAtUtc ?? now)
                : null;
            var lastUpdatedByUserId = hasData
                ? (changed ? actor : existing?.LastUpdatedByUserId ?? actor)
                : null;

            var projection = new WorkAssignmentReportSection
            {
                Id = existing?.Id ?? ObjectId.GenerateNewId().ToString(),
                WorkAssignmentReportId = report.Id,
                WorkId = report.WorkId,
                WorkAssignmentId = report.WorkAssignmentId,
                WorkReportPeriodId = report.WorkReportPeriodId,
                DynamicFormTemplateId = report.DynamicFormTemplateId,
                DynamicFormTemplateCode = report.DynamicFormTemplateCode,
                DynamicFormTemplateName = report.DynamicFormTemplateName,
                DynamicFormFamilyId = report.DynamicFormFamilyId,
                DynamicFormVersionNo = report.DynamicFormVersionNo,
                DynamicFormSchemaHash = report.DynamicFormSchemaHash,
                SectionId = templateSection.SectionId,
                SectionTitle = templateSection.Title,
                SectionOrder = templateSection.Order,
                Status = report.Status,
                FieldValuesJson = BuildSectionFieldValuesJson(report, templateSection.SchemaVersion, sectionValues, now),
                TableValuesJson = BuildSectionTableValuesJson(report, tableRoot, sectionBlocks, now, sectionNative),
                FieldCount = templateSection.FieldIds.Length,
                BlockCount = templateSection.BlockIds.Length,
                NativeTableCount = templateSection.NativeTableIds.Length,
                HasData = hasData,
                LastUpdatedAtUtc = lastUpdatedAtUtc,
                LastUpdatedByUserId = lastUpdatedByUserId,
                SourcePayloadUpdatedAtUtc = report.PayloadUpdatedAtUtc ?? now,
                SourcePayloadRevision = report.PayloadRevision,
                SourcePayloadHash = report.PayloadHash,
                SourceLifecycleRevision = report.LifecycleRevision,
                DynamicFlowMappingReceiptId =
                    report.DynamicFlowMappingReceiptId,
                DynamicFlowMappingProvenanceId =
                    report.DynamicFlowMappingProvenanceId,
                DynamicFlowMappingProvenanceHash =
                    report.DynamicFlowMappingProvenanceHash,
                DynamicFlowMappingResultPayloadRevision =
                    report.DynamicFlowMappingResultPayloadRevision,
                DynamicFlowMappingResultPayloadHash =
                    report.DynamicFlowMappingResultPayloadHash,
                SourceReportUpdatedAtUtc = report.UpdatedAtUtc,
                PayloadHash = payloadHash,
                CreatedAtUtc = existing?.CreatedAtUtc ?? now,
                UpdatedAtUtc = now,
                CreatedByUserId = existing?.CreatedByUserId ?? actor,
                UpdatedByUserId = actor,
                IsDeleted = false
            };

            await WriteMonotonicAsync(projection, existing, ct, session);
        }

        await RetireUnexpectedSectionsAsync(
            report.Id,
            contract.SectionIds,
            actor,
            now,
            ct,
            session);

        var projected = session is null
            ? await _ctx.WorkAssignmentReportSections
                .Find(sectionFilter)
                .SortBy(x => x.SectionOrder)
                .ThenBy(x => x.SectionId)
                .ToListAsync(ct)
            : await _ctx.WorkAssignmentReportSections
                .Find(session, sectionFilter)
                .SortBy(x => x.SectionOrder)
                .ThenBy(x => x.SectionId)
                .ToListAsync(ct);
        if (!IsCompleteProjection(report, contract.Sections, projected, payload, contract.EnumOptions))
            throw IncompleteProjection(report, contract.SectionIds, projected);

        return projected;
    }

    private async Task<WorkReportPayloadSnapshot> LoadCurrentPayloadAsync(
        WorkAssignmentReport report,
        CancellationToken ct)
    {
        var payload = await _payloadReader.LoadReportPayloadAsync(report, ct);
        if (report.PayloadRevision > 0)
            WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, payload);
        return payload;
    }

    private async Task<PublishedTemplateContract?> LoadPublishedTemplateContractAsync(
        WorkAssignmentReport report,
        CancellationToken ct,
        IClientSessionHandle? session)
    {
        var templateId = Normalize(report.DynamicFormTemplateId);
        if (templateId is null)
            return null;

        var filter = Builders<DynamicFormTemplate>.Filter.Where(
            x => x.Id == templateId && x.IsPublished && !x.IsDeleted);
        var template = (session is null
            ? await _ctx.DynamicFormTemplates
                .Find(filter)
                .FirstOrDefaultAsync(ct)
            : await _ctx.DynamicFormTemplates
                .Find(session, filter)
                .FirstOrDefaultAsync(ct))
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
                new { dynamicFormTemplateId = templateId, reason = "PUBLISHED_DYNAMIC_FORM_TEMPLATE_NOT_FOUND" });

        EnsurePublishedRuntimeBinding(report, template);
        var sections = DynamicFormSectionSnapshotBuilder.Build(template).Sections;
        // Only server-validated published List fields can supply catalog identities.
        // Catalog deactivation blocks new authoring, not an existing report binding.
        var catalogIds = (DynamicFormNativeTableDefinition.ReadStored(template.NativeTablesVersion, template.TablesJson) ?? [])
            .Where(table => table.Presentation?.Kind == "LIST")
            .SelectMany(table => table.TypeConfig!.Rules!)
            .Select(rule => rule.Spec?.ValueSource)
            .Where(source => source?.SourceType == "ENUM_CATALOG" && !string.IsNullOrWhiteSpace(source.CatalogId))
            .Select(source => source!.CatalogId!).Distinct(StringComparer.Ordinal).ToArray();
        var enumOptions = new Dictionary<string, RuntimeEnumOptionSet>(StringComparer.Ordinal);
        if (catalogIds.Length > 0)
        {
            var catalogsFilter = Builders<LabelEnumCatalog>.Filter.Where(catalog => catalogIds.Contains(catalog.Id) && !catalog.IsDeleted);
            var catalogs = session is null
                ? await _ctx.LabelEnumCatalogs.Find(catalogsFilter).ToListAsync(ct)
                : await _ctx.LabelEnumCatalogs.Find(session, catalogsFilter).ToListAsync(ct);
            foreach (var catalog in catalogs)
                enumOptions[catalog.Id] = new RuntimeEnumOptionSet(catalog.Id,
                    catalog.Options.Where(option => option.IsActive).Select(option => option.Code).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        return new PublishedTemplateContract(
            sections,
            sections.Select(x => x.SectionId).ToHashSet(StringComparer.Ordinal), enumOptions);
    }

    private static void EnsurePublishedRuntimeBinding(
        WorkAssignmentReport report,
        DynamicFormTemplate template)
    {
        var familyId = Normalize(template.FamilyId) ?? template.Id;
        DynamicFormPublishedSchemaSnapshot snapshot;
        try
        {
            snapshot = DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(template);
        }
        catch (InvalidOperationException ex)
        {
            throw InvalidBinding(report, "DYNAMIC_FORM_RUNTIME_PUBLISHED_SCHEMA_INVALID", ex);
        }

        if (report.DynamicFormVersionNo != template.VersionNo)
            throw InvalidBinding(report, "DYNAMIC_FORM_RUNTIME_VERSION_MISMATCH");
        if (!string.Equals(Normalize(report.DynamicFormFamilyId), familyId, StringComparison.Ordinal))
            throw InvalidBinding(report, "DYNAMIC_FORM_RUNTIME_FAMILY_MISMATCH");
        if (!string.IsNullOrWhiteSpace(report.DynamicFormTemplateCode) &&
            !string.Equals(Normalize(report.DynamicFormTemplateCode), Normalize(template.Code), StringComparison.Ordinal))
        {
            throw InvalidBinding(report, "DYNAMIC_FORM_RUNTIME_CODE_MISMATCH");
        }
        if (!string.Equals(
                Normalize(report.DynamicFormSchemaHash),
                snapshot.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidBinding(report, "DYNAMIC_FORM_RUNTIME_SCHEMA_HASH_MISMATCH");
        }
    }

    private async Task WriteMonotonicAsync(
        WorkAssignmentReportSection projection,
        WorkAssignmentReportSection? observed,
        CancellationToken ct,
        IClientSessionHandle? session)
    {
        var fb = Builders<WorkAssignmentReportSection>.Filter;
        var sourceOrderFilter =
            fb.Exists(x => x.SourcePayloadRevision, false) |
            fb.Lt(x => x.SourcePayloadRevision, projection.SourcePayloadRevision) |
            (fb.Eq(x => x.SourcePayloadRevision, projection.SourcePayloadRevision) &
             (fb.Exists(x => x.SourceLifecycleRevision, false) |
              fb.Lte(x => x.SourceLifecycleRevision, projection.SourceLifecycleRevision)));

        if (observed is not null)
        {
            var filter = fb.Eq(x => x.Id, observed.Id) &
                         fb.Eq(x => x.IsDeleted, false) &
                         sourceOrderFilter;
            if (session is null)
            {
                await _ctx.WorkAssignmentReportSections.ReplaceOneAsync(
                    filter,
                    projection,
                    new ReplaceOptions { IsUpsert = false },
                    ct);
            }
            else
            {
                await _ctx.WorkAssignmentReportSections.ReplaceOneAsync(
                    session,
                    filter,
                    projection,
                    new ReplaceOptions { IsUpsert = false },
                    ct);
            }
            return;
        }

        try
        {
            if (session is null)
            {
                await _ctx.WorkAssignmentReportSections.InsertOneAsync(
                    projection,
                    cancellationToken: ct);
            }
            else
            {
                await _ctx.WorkAssignmentReportSections.InsertOneAsync(
                    session,
                    projection,
                    cancellationToken: ct);
            }
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var filter = fb.Eq(x => x.WorkAssignmentReportId, projection.WorkAssignmentReportId) &
                         fb.Eq(x => x.SectionId, projection.SectionId) &
                         fb.Eq(x => x.IsDeleted, false) &
                         sourceOrderFilter;
            if (session is null)
            {
                await _ctx.WorkAssignmentReportSections.ReplaceOneAsync(
                    filter,
                    projection,
                    new ReplaceOptions { IsUpsert = false },
                    ct);
            }
            else
            {
                await _ctx.WorkAssignmentReportSections.ReplaceOneAsync(
                    session,
                    filter,
                    projection,
                    new ReplaceOptions { IsUpsert = false },
                    ct);
            }
        }
    }

    private async Task RetireUnexpectedSectionsAsync(
        string reportId,
        IReadOnlyCollection<string> expectedSectionIds,
        string actorUserId,
        DateTime now,
        CancellationToken ct,
        IClientSessionHandle? session)
    {
        var fb = Builders<WorkAssignmentReportSection>.Filter;
        var filter = fb.Eq(x => x.WorkAssignmentReportId, reportId) & fb.Eq(x => x.IsDeleted, false);
        if (expectedSectionIds.Count > 0)
            filter &= fb.Nin(x => x.SectionId, expectedSectionIds);

        var update = Builders<WorkAssignmentReportSection>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.DeletedAtUtc, now)
            .Set(x => x.DeletedByUserId, actorUserId)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);
        if (session is null)
        {
            await _ctx.WorkAssignmentReportSections.UpdateManyAsync(
                filter,
                update,
                cancellationToken: ct);
        }
        else
        {
            await _ctx.WorkAssignmentReportSections.UpdateManyAsync(
                session,
                filter,
                update,
                cancellationToken: ct);
        }
    }

    private static IReadOnlyList<WorkAssignmentReportSection> Order(
        IEnumerable<WorkAssignmentReportSection> sections)
        => sections.OrderBy(x => x.SectionOrder).ThenBy(x => x.SectionId, StringComparer.Ordinal).ToArray();

    private static AppException InvalidBinding(
        WorkAssignmentReport report,
        string reason,
        Exception? innerException = null)
        => new(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
            new
            {
                reportId = report.Id,
                dynamicFormTemplateId = report.DynamicFormTemplateId,
                dynamicFormVersionNo = report.DynamicFormVersionNo,
                dynamicFormFamilyId = report.DynamicFormFamilyId,
                dynamicFormSchemaHash = report.DynamicFormSchemaHash,
                reason
            },
            innerException: innerException);

    private static AppException IncompleteProjection(
        WorkAssignmentReport report,
        IReadOnlyCollection<string> expectedSectionIds,
        IReadOnlyCollection<WorkAssignmentReportSection> projected)
        => AppExceptionFactory.Create(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_PAYLOAD_REVISION_CONFLICT,
            new
            {
                reportId = report.Id,
                currentPayloadRevision = report.PayloadRevision,
                currentPayloadHash = report.PayloadHash,
                currentLifecycleRevision = report.LifecycleRevision,
                currentStatus = report.Status,
                expectedSectionCount = expectedSectionIds.Count,
                expectedSectionIds = expectedSectionIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                projectedSectionCount = projected.Count,
                projectedSectionIds = projected.Select(x => x.SectionId).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                reason = "DYNAMIC_FORM_SECTION_PROJECTION_INCOMPLETE"
            });

    internal static void ValidateProjectionPayloadShape(
        WorkAssignmentReport report,
        string? fieldValuesJson,
        string? tableValuesJson)
        => _ = ParseProjectionPayload(report, fieldValuesJson, tableValuesJson);

    private static ProjectionPayload ParseProjectionPayload(
        WorkAssignmentReport report,
        string? fieldValuesJson,
        string? tableValuesJson)
    {
        var fieldRoot = ParseJsonObjectOrThrow(report, fieldValuesJson, "fieldValuesJson");
        JsonObject fieldValues;
        if (fieldRoot is null)
        {
            fieldValues = new JsonObject();
        }
        else if (!fieldRoot.TryGetPropertyValue("values", out var valuesNode))
        {
            fieldValues = fieldRoot;
        }
        else if (valuesNode is JsonObject values)
        {
            fieldValues = values;
        }
        else
        {
            throw InvalidProjectionPayload(
                report,
                "fieldValuesJson",
                "DYNAMIC_FORM_RUNTIME_FIELD_VALUES_OBJECT_REQUIRED");
        }

        var tableRoot = ParseJsonObjectOrThrow(report, tableValuesJson, "tableValuesJson");
        if (tableRoot?.ContainsKey("nativeTables") == true && Encoding.UTF8.GetByteCount(tableValuesJson!) > DynamicFormNativeTableValues.MaximumBytes)
            throw InvalidProjectionPayload(report, "nativeTables", "NATIVE_VALUES_BYTE_LIMIT");
        var tableBlocksById = BuildTableBlockMap(report, tableRoot);
        return new ProjectionPayload(fieldValues, tableRoot, tableBlocksById);
    }

    private static JsonObject? ParseJsonObjectOrThrow(
        WorkAssignmentReport report,
        string? json,
        string scope)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            if (JsonNode.Parse(json) is JsonObject parsed)
                return parsed;

            throw InvalidProjectionPayload(
                report,
                scope,
                "DYNAMIC_FORM_SECTION_PROJECTION_JSON_OBJECT_REQUIRED");
        }
        catch (JsonException ex)
        {
            throw InvalidProjectionPayload(
                report,
                scope,
                "DYNAMIC_FORM_SECTION_PROJECTION_JSON_INVALID",
                ex);
        }
    }

    private static Dictionary<string, JsonObject> BuildTableBlockMap(
        WorkAssignmentReport report,
        JsonObject? tableRoot)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (tableRoot is null)
            return result;

        if (!tableRoot.TryGetPropertyValue("blocks", out var blocksNode) || blocksNode is not JsonArray blocks)
        {
            throw InvalidProjectionPayload(
                report,
                "tableValuesJson",
                "DYNAMIC_FORM_TABLE_BLOCKS_ARRAY_REQUIRED");
        }

        for (var index = 0; index < blocks.Count; index++)
        {
            if (blocks[index] is not JsonObject block)
            {
                throw InvalidProjectionPayload(
                    report,
                    "tableValuesJson",
                    "DYNAMIC_FORM_TABLE_BLOCK_OBJECT_REQUIRED",
                    value: new { index });
            }

            var blockId = NormalizeBlockId(ReadString(block, "blockId") ?? ReadString(block, "id"));
            if (string.IsNullOrWhiteSpace(blockId))
            {
                throw InvalidProjectionPayload(
                    report,
                    "tableValuesJson",
                    "DYNAMIC_FORM_TABLE_BLOCK_ID_REQUIRED",
                    value: new { index });
            }
            if (!result.TryAdd(blockId, block))
            {
                throw InvalidProjectionPayload(
                    report,
                    "tableValuesJson",
                    "DYNAMIC_FORM_TABLE_BLOCK_DUPLICATE",
                    value: new { index, blockId });
            }
        }

        return result;
    }

    private static AppException InvalidProjectionPayload(
        WorkAssignmentReport report,
        string scope,
        string reason,
        Exception? innerException = null,
        object? value = null)
        => new(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
            new
            {
                reportId = report.Id,
                workAssignmentId = report.WorkAssignmentId,
                workReportPeriodId = report.WorkReportPeriodId,
                dynamicFormTemplateId = report.DynamicFormTemplateId,
                scope,
                reason,
                value
            },
            innerException: innerException);

    private static JsonObject BuildSectionFieldValues(
        JsonObject fieldValues,
        IReadOnlyCollection<string> fieldIds)
    {
        var result = new JsonObject();
        foreach (var fieldId in fieldIds)
        {
            if (fieldValues.TryGetPropertyValue(fieldId, out var value))
                result[fieldId] = Clone(value);
        }

        return result;
    }

    private static JsonArray BuildSectionTableBlocks(
        IReadOnlyDictionary<string, JsonObject> tableBlocksById,
        IReadOnlyCollection<string> blockIds)
    {
        var result = new JsonArray();
        foreach (var blockId in blockIds)
        {
            if (tableBlocksById.TryGetValue(NormalizeBlockId(blockId), out var block))
                result.Add(Clone(block));
        }

        return result;
    }

    private static void ValidateNativeProjection(WorkAssignmentReport report,
        IReadOnlyCollection<DynamicFormSectionSnapshot> sections, JsonObject? root,
        IReadOnlyDictionary<string, RuntimeEnumOptionSet>? enumOptions)
    {
        if (root is null || !root.ContainsKey("nativeTables")) return; // Unentered report: preserve absence.
        if (!sections.Any(section => section.NativeTablesJson is not null))
            throw InvalidProjectionPayload(report, "nativeTables", "NATIVE_VALUES_WITHOUT_DEFINITION");
        if (Encoding.UTF8.GetByteCount(root.ToJsonString(JsonOptions)) > DynamicFormNativeTableValues.MaximumBytes)
            throw InvalidProjectionPayload(report, "nativeTables", "NATIVE_VALUES_BYTE_LIMIT");
        var definitions = sections.SelectMany(section => DynamicFormNativeTableDefinition.ReadStored(
            section.NativeTablesJson is null ? null : 1, section.NativeTablesJson) ?? []).ToArray();
        using var document = JsonDocument.Parse(root["nativeTables"]?.ToJsonString(JsonOptions) ?? "null");
        DynamicFormNativeTableValues.ValidateEnvelope(definitions, report.DynamicFormSchemaHash, document.RootElement, submitting: false, enumOptions);
    }

    private static JsonObject? BuildSectionNativeValues(JsonObject? root, IReadOnlyCollection<string> tableIds)
    {
        if (tableIds.Count == 0 || root?["nativeTables"] is not JsonObject envelope) return null;
        var ids = tableIds.ToHashSet(StringComparer.Ordinal);
        var result = new JsonObject { ["version"] = Clone(envelope["version"]), ["schemaHash"] = Clone(envelope["schemaHash"]) };
        result["tables"] = new JsonArray(((JsonArray)envelope["tables"]!).OfType<JsonObject>()
            .Where(table => ids.Contains(ReadString(table, "tableId")!)).Select(Clone).ToArray());
        return result;
    }

    private static bool NativeHasData(JsonObject? envelope)
        => envelope?["tables"] is JsonArray tables && tables.OfType<JsonObject>().Any(table =>
            table["contentRef"]?["rowCount"]?.GetValue<int>() > 0 || (table["rows"] ?? table["records"]) is JsonArray rows && rows.OfType<JsonObject>().Any(row =>
                row["cells"] is JsonObject cells && cells.Select(pair => pair.Value).OfType<JsonObject>().Any(cell =>
                    ReadString(cell, "state") == "error" || ReadString(cell, "state") == "value" && JsonNodeHasData(cell["value"]))));

    private static bool MatchesProjectedSectionPayload(
        WorkAssignmentReport report,
        int schemaVersion,
        JsonObject? canonicalTableRoot,
        JsonObject expectedFieldValues,
        JsonArray expectedTableBlocks,
        JsonObject? expectedNative,
        WorkAssignmentReportSection actual)
    {
        try
        {
            var actualFieldRoot = ParseJsonObjectOrThrow(report, actual.FieldValuesJson, "section.fieldValuesJson");
            if (actualFieldRoot is null ||
                actualFieldRoot["values"] is not JsonObject ||
                !RemoveValidProjectionTimestamp(actualFieldRoot))
            {
                return false;
            }

            var expectedFieldRoot = BuildSectionFieldValuesRoot(report, schemaVersion, expectedFieldValues);
            if (!JsonNode.DeepEquals(actualFieldRoot, expectedFieldRoot))
                return false;

            var expectedTableRoot = BuildSectionTableValuesRoot(report, canonicalTableRoot, expectedTableBlocks, expectedNative);
            if (expectedTableRoot is null)
                return string.IsNullOrWhiteSpace(actual.TableValuesJson);

            var actualTableRoot = ParseJsonObjectOrThrow(report, actual.TableValuesJson, "section.tableValuesJson");
            if (actualTableRoot is null || !RemoveValidProjectionTimestamp(actualTableRoot))
                return false;

            // Validate object/ID/duplicate shape independently before semantic equality so malformed
            // persisted projections never pass through the read fast-path or durable outbox ACK.
            _ = BuildTableBlockMap(report, actualTableRoot);
            return JsonNode.DeepEquals(actualTableRoot, expectedTableRoot);
        }
        catch (AppException)
        {
            return false;
        }
    }

    private static bool RemoveValidProjectionTimestamp(JsonObject root)
    {
        if (!root.TryGetPropertyValue("updatedAtUtc", out var node) ||
            node is not JsonValue value ||
            !value.TryGetValue<string>(out var text) ||
            !DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _))
        {
            return false;
        }

        return root.Remove("updatedAtUtc");
    }

    private static JsonObject BuildSectionFieldValuesRoot(
        WorkAssignmentReport report,
        int schemaVersion,
        JsonObject values)
        => new()
        {
            ["dynamicFormTemplateId"] = report.DynamicFormTemplateId,
            ["dynamicFormTemplateCode"] = report.DynamicFormTemplateCode,
            ["dynamicFormTemplateName"] = report.DynamicFormTemplateName,
            ["schemaVersion"] = schemaVersion,
            ["values"] = Clone(values)
        };

    private static JsonObject? BuildSectionTableValuesRoot(
        WorkAssignmentReport report,
        JsonObject? tableRoot,
        JsonArray blocks,
        JsonObject? native = null)
    {
        if (blocks.Count == 0 && native is null)
            return null;

        var root = tableRoot?.ContainsKey("nativeTables") == true
            ? new JsonObject(tableRoot.Where(pair => pair.Key is not ("nativeTables" or "blocks"))
                .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Clone(pair.Value))))
            : Clone(tableRoot) as JsonObject ?? new JsonObject();
        root.Remove("nativeTables"); // Never copy the whole report's tables into one section.
        if (native is not null) root["nativeTables"] = Clone(native);
        root.Remove("updatedAtUtc");
        root["dynamicFormTemplateId"] = report.DynamicFormTemplateId;
        root["dynamicFormTemplateCode"] = report.DynamicFormTemplateCode;
        root["dynamicFormTemplateName"] = report.DynamicFormTemplateName;
        root["blocks"] = Clone(blocks);
        return root;
    }

    private static string BuildSectionFieldValuesJson(
        WorkAssignmentReport report,
        int schemaVersion,
        JsonObject values,
        DateTime updatedAtUtc)
    {
        var root = BuildSectionFieldValuesRoot(report, schemaVersion, values);
        root["updatedAtUtc"] = updatedAtUtc.ToString("O", CultureInfo.InvariantCulture);
        return root.ToJsonString(JsonOptions);
    }

    private static string? BuildSectionTableValuesJson(
        WorkAssignmentReport report,
        JsonObject? tableRoot,
        JsonArray blocks,
        DateTime updatedAtUtc,
        JsonObject? native = null)
    {
        var root = BuildSectionTableValuesRoot(report, tableRoot, blocks, native);
        if (root is null)
            return null;

        root["updatedAtUtc"] = updatedAtUtc.ToString("O", CultureInfo.InvariantCulture);
        return root.ToJsonString(JsonOptions);
    }

    internal static string ComputeSectionPayloadHash(JsonObject sectionValues, JsonArray sectionBlocks, JsonObject? native = null)
    {
        var root = new JsonObject
        {
            ["fieldValues"] = Clone(sectionValues),
            ["tableBlocks"] = Clone(sectionBlocks)
        };
        if (native is not null) root["nativeTables"] = Clone(native);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(root.ToJsonString(JsonOptions)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool JsonObjectHasData(JsonObject obj)
        => obj.Any(item => JsonNodeHasData(item.Value));

    private static bool TableBlockHasEnteredData(JsonObject block)
        => block.TryGetPropertyValue("values1D", out var values) && JsonNodeHasData(values);

    private static bool JsonNodeHasData(JsonNode? node)
    {
        if (node is null)
            return false;
        if (node is JsonArray array)
            return array.Any(JsonNodeHasData);
        if (node is JsonObject obj)
            return obj.Any(item => JsonNodeHasData(item.Value));
        if (node is not JsonValue value)
            return false;
        if (value.TryGetValue<string>(out var text))
            return !string.IsNullOrWhiteSpace(text);
        if (value.TryGetValue<bool>(out _))
            return true;
        if (value.TryGetValue<double>(out var number))
            return !double.IsNaN(number);
        return true;
    }

    private static JsonNode? Clone(JsonNode? node)
        => node is null ? null : JsonNode.Parse(node.ToJsonString(JsonOptions));

    private static string? ReadString(JsonObject obj, string name)
        => obj.TryGetPropertyValue(name, out var value) && value is JsonValue jsonValue &&
           jsonValue.TryGetValue<string>(out var text)
            ? Normalize(text)
            : null;

    private static string NormalizeBlockId(string? value)
        => Normalize(value)?.ToLowerInvariant() ?? string.Empty;

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static bool SameMongoMillisecond(DateTime? left, DateTime? right)
    {
        if (!left.HasValue || !right.HasValue)
            return left.HasValue == right.HasValue;

        return ToMongoMillisecond(left.Value) == ToMongoMillisecond(right.Value);
    }

    private static long ToMongoMillisecond(DateTime value)
        => new DateTimeOffset(NormalizeUtc(value)).ToUnixTimeMilliseconds();

    private sealed record PublishedTemplateContract(
        IReadOnlyList<DynamicFormSectionSnapshot> Sections,
        IReadOnlySet<string> SectionIds,
        IReadOnlyDictionary<string, RuntimeEnumOptionSet> EnumOptions);

    private sealed record ProjectionPayload(
        JsonObject FieldValues,
        JsonObject? TableRoot,
        IReadOnlyDictionary<string, JsonObject> TableBlocksById);
}
