using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

internal static class WorkAssignmentReportSectionProjectionContractTests
{
    private const string CanonicalFieldValuesJson =
        "{\"values\":{\"title\":\"Quarterly\",\"owner\":\"reviewer\",\"amount\":12}}";
    private const string CanonicalTableValuesJson =
        "{\"blocks\":[{\"blockId\":\"items\",\"values1D\":[1]}]}";

    public static void Run()
    {
        CompletenessRequiresExactPublishedSectionTopologyAndCurrentSourceState();
        BsonMillisecondRoundTripPreservesCurrentProjection();
        ProjectionPayloadShapeFailsClosed();
        DurableReconcilerUsesCanonicalCreateAndVerifyWriter();
    }

    private static void CompletenessRequiresExactPublishedSectionTopologyAndCurrentSourceState()
    {
        var report = Report();
        var expected = new[]
        {
            Snapshot("overview", 0, ["title"], []),
            Snapshot("details", 1, ["owner", "amount"], ["items"])
        };
        var complete = new[]
        {
            Projection(report, expected[0]),
            Projection(report, expected[1])
        };

        True(
            IsComplete(report, expected, complete),
            "exact current projection");
        False(
            IsComplete(report, expected, complete.Take(1).ToArray()),
            "missing published section");
        False(
            IsComplete(
                report,
                expected,
                complete.Concat([Projection(report, Snapshot("unexpected", 2, [], []))]).ToArray()),
            "unexpected active section");

        var stalePayload = Projection(report, expected[0]);
        stalePayload.SourcePayloadRevision--;
        FalseCurrent(report, expected[0], stalePayload, "payload revision");

        var staleLifecycle = Projection(report, expected[0]);
        staleLifecycle.SourceLifecycleRevision--;
        FalseCurrent(report, expected[0], staleLifecycle, "lifecycle revision");

        var staleHash = Projection(report, expected[0]);
        staleHash.SourcePayloadHash = "stale";
        FalseCurrent(report, expected[0], staleHash, "payload hash");

        var pendingReport = Report();
        pendingReport.PayloadStatus = WorkReportPayloadStatus.Pending;
        FalseCurrent(pendingReport, expected[0], Projection(pendingReport, expected[0]), "payload readiness");

        var staleStatus = Projection(report, expected[0]);
        staleStatus.Status = WorkAssignmentReportStatus.Submitted;
        FalseCurrent(report, expected[0], staleStatus, "status");

        var staleShape = Projection(report, expected[0]);
        staleShape.FieldCount++;
        FalseCurrent(report, expected[0], staleShape, "published field count");

        var deleted = Projection(report, expected[0]);
        deleted.IsDeleted = true;
        FalseCurrent(report, expected[0], deleted, "deleted row");

        var corruptBodyWithCurrentMarkers = Projection(report, expected[0]);
        corruptBodyWithCurrentMarkers.FieldValuesJson =
            "{\"dynamicFormTemplateId\":\"100000000000000000000005\",\"dynamicFormTemplateCode\":\"P3_FORM\",\"dynamicFormTemplateName\":\"P3 form\",\"schemaVersion\":2,\"values\":{\"title\":\"CORRUPT\"},\"updatedAtUtc\":\"1970-01-01T00:00:00.0000000Z\"}";
        FalseCurrent(report, expected[0], corruptBodyWithCurrentMarkers, "current markers with corrupt field body");

        var corruptHashWithCurrentMarkers = Projection(report, expected[0]);
        corruptHashWithCurrentMarkers.PayloadHash = "corrupt-section-payload-hash";
        FalseCurrent(report, expected[0], corruptHashWithCurrentMarkers, "current markers with corrupt section hash");

        var corruptTableWithCurrentMarkers = Projection(report, expected[1]);
        corruptTableWithCurrentMarkers.TableValuesJson = "{\"blocks\":[],\"updatedAtUtc\":\"1970-01-01T00:00:00.0000000Z\"}";
        FalseCurrent(report, expected[1], corruptTableWithCurrentMarkers, "current markers with corrupt table block set");
    }

    private static void BsonMillisecondRoundTripPreservesCurrentProjection()
    {
        var report = Report();
        var subMillisecond = new DateTime(
                2026,
                7,
                30,
                12,
                34,
                56,
                789,
                DateTimeKind.Utc)
            .AddTicks(4_321);
        report.PayloadUpdatedAtUtc = subMillisecond;
        report.UpdatedAtUtc = subMillisecond.AddTicks(1_234);
        var expected = Snapshot("overview", 0, ["title"], []);
        var projected = Projection(report, expected);
        var bsonRoundTripped = BsonSerializer.Deserialize<WorkAssignmentReportSection>(
            projected.ToBsonDocument());

        True(
            IsComplete(report, [expected], [bsonRoundTripped]),
            "MongoDB millisecond DateTime round-trip");

        bsonRoundTripped.SourceReportUpdatedAtUtc =
            bsonRoundTripped.SourceReportUpdatedAtUtc!.Value.AddMilliseconds(1);
        FalseCurrent(
            report,
            expected,
            bsonRoundTripped,
            "true millisecond timestamp drift");
    }

    private static void DurableReconcilerUsesCanonicalCreateAndVerifyWriter()
    {
        var projection = ReadBackendSource(
            "Services/WorkAssignmentReports/Runtime/WorkAssignmentReportSectionProjectionService.cs");
        var reconciler = ReadBackendSource(
            "Services/WorkAssignmentReports/Runtime/WorkReportLifecycleProjectionReconciler.cs");
        var reports = ReadBackendSource("Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var program = ReadBackendSource("Program.cs");

        Contains(projection, "x.Id == templateId && x.IsPublished && !x.IsDeleted", "published exact template lookup");
        Contains(projection, "InsertOneAsync", "missing-section creation path");
        Contains(projection, "RetireUnexpectedSectionsAsync", "unexpected-section repair path");
        Contains(projection, "IsCompleteProjection(report, contract.Sections, projected, payload, contract.EnumOptions)", "post-write exact payload verification with bound catalog options");
        Contains(projection, "MatchesProjectedSectionPayload", "exact projected field/table payload verifier");
        Contains(projection, "JsonNode.DeepEquals", "structural payload equality");
        Contains(projection, "actual.PayloadHash, expectedPayloadHash", "recomputed section payload hash verification");
        Before(
            projection,
            "var payloadSnapshot = await LoadCurrentPayloadAsync(report, ct);",
            "if (IsCompleteProjection(report, contract.Sections, observedSections, payload, contract.EnumOptions))",
            "canonical payload must load before the read fast-path");
        Contains(projection, "DYNAMIC_FORM_SECTION_PROJECTION_INCOMPLETE", "fail-closed verification reason");
        Contains(reconciler, "_sectionProjection.ProjectCurrentAndVerifyAsync", "durable retry canonical projection");
        Before(
            reconciler,
            "_sectionProjection.ProjectCurrentAndVerifyAsync",
            "CompletePendingEntriesAsync(claim",
            "complete section verification must finish before outbox acknowledgement");
        Contains(reports, "_sectionProjection.ProjectAndVerifyAsync", "foreground payload projection");
        Contains(reports, "_sectionProjection.EnsureCurrentAndVerifyAsync", "read repair projection");
        Contains(
            program,
            "IWorkAssignmentReportSectionProjectionService, WorkAssignmentReportSectionProjectionService",
            "projection service dependency injection");
    }

    private static void ProjectionPayloadShapeFailsClosed()
    {
        var report = Report();
        WorkAssignmentReportSectionProjectionService.ValidateProjectionPayloadShape(
            report,
            "{\"values\":{\"title\":\"ok\"}}",
            "{\"blocks\":[{\"blockId\":\"items\"}]}");
        WorkAssignmentReportSectionProjectionService.ValidateProjectionPayloadShape(
            report,
            "{\"title\":\"direct envelope is supported\"}",
            null);

        PayloadShapeFailure(
            report,
            "{",
            null,
            "DYNAMIC_FORM_SECTION_PROJECTION_JSON_INVALID");
        PayloadShapeFailure(
            report,
            "[]",
            null,
            "DYNAMIC_FORM_SECTION_PROJECTION_JSON_OBJECT_REQUIRED");
        PayloadShapeFailure(
            report,
            "{\"values\":[]}",
            null,
            "DYNAMIC_FORM_RUNTIME_FIELD_VALUES_OBJECT_REQUIRED");
        PayloadShapeFailure(
            report,
            null,
            "{}",
            "DYNAMIC_FORM_TABLE_BLOCKS_ARRAY_REQUIRED");
        PayloadShapeFailure(
            report,
            null,
            "{\"blocks\":{}}",
            "DYNAMIC_FORM_TABLE_BLOCKS_ARRAY_REQUIRED");
        PayloadShapeFailure(
            report,
            null,
            "{\"blocks\":[1]}",
            "DYNAMIC_FORM_TABLE_BLOCK_OBJECT_REQUIRED");
        PayloadShapeFailure(
            report,
            null,
            "{\"blocks\":[{}]}",
            "DYNAMIC_FORM_TABLE_BLOCK_ID_REQUIRED");
        PayloadShapeFailure(
            report,
            null,
            "{\"blocks\":[{\"blockId\":\"A\"},{\"blockId\":\"a\"}]}",
            "DYNAMIC_FORM_TABLE_BLOCK_DUPLICATE");
    }

    private static void PayloadShapeFailure(
        WorkAssignmentReport report,
        string? fieldValuesJson,
        string? tableValuesJson,
        string expectedReason)
    {
        try
        {
            WorkAssignmentReportSectionProjectionService.ValidateProjectionPayloadShape(
                report,
                fieldValuesJson,
                tableValuesJson);
        }
        catch (AppException ex)
        {
            if (ex.Code != AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID)
            {
                throw new InvalidOperationException(
                    $"payload shape code: expected '{AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID}', got '{ex.Code}'.");
            }

            var details = JsonSerializer.Serialize(ex.Details);
            if (!details.Contains(expectedReason, StringComparison.Ordinal))
                throw new InvalidOperationException($"payload shape reason: expected '{expectedReason}' in '{details}'.");
            return;
        }

        throw new InvalidOperationException($"payload shape: expected '{expectedReason}' failure.");
    }

    private static void FalseCurrent(
        WorkAssignmentReport report,
        DynamicFormSectionSnapshot expected,
        WorkAssignmentReportSection actual,
        string context)
        => False(
            IsComplete(report, [expected], [actual]),
            context);

    private static bool IsComplete(
        WorkAssignmentReport report,
        IReadOnlyCollection<DynamicFormSectionSnapshot> expected,
        IReadOnlyCollection<WorkAssignmentReportSection> projected)
        => WorkAssignmentReportSectionProjectionService.IsCompleteProjection(
            report,
            expected,
            projected,
            CanonicalFieldValuesJson,
            CanonicalTableValuesJson);

    private static WorkAssignmentReport Report()
        => new()
        {
            Id = "100000000000000000000001",
            WorkId = "100000000000000000000002",
            WorkAssignmentId = "100000000000000000000003",
            WorkReportPeriodId = "100000000000000000000004",
            DynamicFormTemplateId = "100000000000000000000005",
            DynamicFormTemplateCode = "P3_FORM",
            DynamicFormTemplateName = "P3 form",
            DynamicFormFamilyId = "100000000000000000000006",
            DynamicFormVersionNo = 4,
            DynamicFormSchemaHash = "schema-hash",
            PayloadRevision = 9,
            PayloadHash = "payload-hash",
            PayloadStatus = WorkReportPayloadStatus.Ready,
            PayloadUpdatedAtUtc = DateTime.UnixEpoch,
            LifecycleRevision = 3,
            Status = WorkAssignmentReportStatus.Approved,
            UpdatedAtUtc = DateTime.UnixEpoch
        };

    private static DynamicFormSectionSnapshot Snapshot(
        string sectionId,
        int order,
        string[] fieldIds,
        string[] blockIds)
        => new(
            "100000000000000000000005",
            "P3_FORM",
            "P3 form",
            sectionId,
            sectionId,
            null,
            [],
            order,
            2,
            fieldIds,
            blockIds,
            "[]",
            "[]",
            $"hash-{sectionId}");

    private static WorkAssignmentReportSection Projection(
        WorkAssignmentReport report,
        DynamicFormSectionSnapshot expected)
    {
        var canonicalFields = (JsonNode.Parse(CanonicalFieldValuesJson) as JsonObject)?["values"] as JsonObject
                              ?? throw new InvalidOperationException("Canonical test field values are invalid.");
        var sectionValues = new JsonObject();
        foreach (var fieldId in expected.FieldIds)
        {
            if (canonicalFields.TryGetPropertyValue(fieldId, out var value))
                sectionValues[fieldId] = value?.DeepClone();
        }

        var canonicalBlocks = (JsonNode.Parse(CanonicalTableValuesJson) as JsonObject)?["blocks"] as JsonArray
                              ?? throw new InvalidOperationException("Canonical test table values are invalid.");
        var sectionBlocks = new JsonArray();
        foreach (var blockId in expected.BlockIds)
        {
            var block = canonicalBlocks
                .OfType<JsonObject>()
                .SingleOrDefault(x => string.Equals(x["blockId"]?.GetValue<string>(), blockId, StringComparison.Ordinal));
            if (block is not null)
                sectionBlocks.Add(block.DeepClone());
        }

        var fieldRoot = new JsonObject
        {
            ["dynamicFormTemplateId"] = report.DynamicFormTemplateId,
            ["dynamicFormTemplateCode"] = report.DynamicFormTemplateCode,
            ["dynamicFormTemplateName"] = report.DynamicFormTemplateName,
            ["schemaVersion"] = expected.SchemaVersion,
            ["values"] = sectionValues.DeepClone(),
            ["updatedAtUtc"] = DateTime.UnixEpoch.ToString("O")
        };
        string? tableValuesJson = null;
        if (sectionBlocks.Count > 0)
        {
            tableValuesJson = new JsonObject
            {
                ["dynamicFormTemplateId"] = report.DynamicFormTemplateId,
                ["dynamicFormTemplateCode"] = report.DynamicFormTemplateCode,
                ["dynamicFormTemplateName"] = report.DynamicFormTemplateName,
                ["blocks"] = sectionBlocks.DeepClone(),
                ["updatedAtUtc"] = DateTime.UnixEpoch.ToString("O")
            }.ToJsonString();
        }

        return new WorkAssignmentReportSection
        {
            Id = $"20000000000000000000000{expected.Order + 1}",
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
            SectionId = expected.SectionId,
            SectionTitle = expected.Title,
            SectionOrder = expected.Order,
            FieldValuesJson = fieldRoot.ToJsonString(),
            TableValuesJson = tableValuesJson,
            FieldCount = expected.FieldIds.Length,
            BlockCount = expected.BlockIds.Length,
            HasData = sectionValues.Count > 0 || sectionBlocks.Count > 0,
            SourcePayloadRevision = report.PayloadRevision,
            SourcePayloadHash = report.PayloadHash,
            SourcePayloadUpdatedAtUtc = report.PayloadUpdatedAtUtc,
            SourceLifecycleRevision = report.LifecycleRevision,
            SourceReportUpdatedAtUtc = report.UpdatedAtUtc,
            PayloadHash = WorkAssignmentReportSectionProjectionService.ComputeSectionPayloadHash(sectionValues, sectionBlocks),
            Status = report.Status,
            IsDeleted = false
        };
    }

    private static string ReadBackendSource(string relativePath)
    {
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var directory = new DirectoryInfo(seed); directory is not null; directory = directory.Parent)
            {
                var direct = Path.Combine(directory.FullName, "tdtd-be.csproj");
                if (File.Exists(direct))
                    return File.ReadAllText(Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));

                var nestedRoot = Path.Combine(directory.FullName, "tdtd-be");
                if (File.Exists(Path.Combine(nestedRoot, "tdtd-be.csproj")))
                    return File.ReadAllText(Path.Combine(nestedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            }
        }

        throw new InvalidOperationException("Could not locate the tdtd-be source root.");
    }

    private static void Contains(string source, string expected, string context)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context}: expected '{expected}'.");
    }

    private static void Before(string source, string first, string second, string context)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        if (firstIndex < 0 || secondIndex < 0 || firstIndex >= secondIndex)
            throw new InvalidOperationException($"{context}: expected '{first}' before '{second}'.");
    }

    private static void True(bool value, string context)
    {
        if (!value)
            throw new InvalidOperationException($"{context}: expected true.");
    }

    private static void False(bool value, string context)
    {
        if (value)
            throw new InvalidOperationException($"{context}: expected false.");
    }
}
