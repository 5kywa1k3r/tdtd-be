using System.Security.Cryptography;
using System.Text;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsRun;

internal static class ActualFixture
{
    internal const string WorkId = "100000000000000000000001";
    internal const string Assignment1 = "200000000000000000000001";
    internal const string Assignment2 = "200000000000000000000002";
    internal const string Report1 = "300000000000000000000001";
    internal const string Report2 = "300000000000000000000002";
    internal const string PeriodId = "400000000000000000000001";
    internal const string TemplateId = "500000000000000000000001";
    internal const string FamilyId = "500000000000000000000002";
    internal const string ExcelTemplateId = "500000000000000000000003";
    internal const string ConfigId = "600000000000000000000001";
    internal const string ConfigVersionId = "600000000000000000000002";
    internal const string Event1 = "700000000000000000000001";
    internal const string PeriodInstanceKey = "MONTH:2026-08";
    internal const string RunId = "800000000000000000000001";
    internal static readonly string GenerationId = Hash('1');
    internal static readonly string GenerationSha256 = Hash('2');
    internal static readonly string MembershipSignature = Hash('3');

    internal static string Hash(char value) => new(value, 64);

    internal static string P9OwnerStableObjectId(
        string generationId,
        string owner,
        params string?[] identity)
    {
        var material = string.Join(
            "\n",
            new[] { "P9_DIRECT_ROW_V1", generationId, owner }
                .Concat(identity.Select(value => value ?? "<null>")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant()[..24];
    }

    internal static ActualSourceMembershipScope Scope()
    {
        var lifecycle = StatRunDirectLifecycleMetricScopeCanonical.Create(
            "DIRECT", "ALL_CANDIDATES", null, Assignment1,
            null, null, null, null, null, null, null,
            "DIRECT_CONFIG", ConfigId, ConfigVersionId, 4, 9,
            Hash('5'), Hash('5'), Hash('e'), Hash('f'));
        return new(WorkId, PeriodInstanceKey, TemplateId,
            MembershipSignature, RunId, GenerationId, GenerationSha256, 17,
            lifecycle);
    }
    internal static ActualDirectProjectionBoundary Boundary()
        => new(
            WorkId,
            PeriodInstanceKey,
            FamilyId,
            TemplateId,
            3,
            Hash('4'),
            RunId,
            GenerationId,
            GenerationSha256,
            "event:approved:3",
            new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc),
            17,
            ConfigId,
            ConfigVersionId,
            4,
            9,
            Hash('5'),
            "p10_chain_fixture",
            "v1.6",
            Hash('6'),
            Hash('7'),
            Hash('8'),
            Hash('9'),
            Hash('a'),
            MembershipSignature);

    internal static ActualSourceOwnerRevision Source(
        string reportId,
        int ordinal = 0,
        bool ownerIncluded = true)
    {
        var assignment = reportId == Report2 ? Assignment2 : Assignment1;
        return new ActualSourceOwnerRevision(
            WorkId: WorkId,
            WorkAssignmentId: assignment,
            PeriodInstanceKey: PeriodInstanceKey,
            DynamicFormTemplateId: TemplateId,
            ReportId: reportId,
            PayloadDocumentId: $"payload-{reportId}",
            PayloadRevision: 1,
            PayloadSha256: reportId == Report2 ? Hash('b') : Hash('a'),
            LifecycleRevision: 3,
            LifecycleSha256: reportId == Report2 ? Hash('d') : Hash('c'),
            LifecycleStatus: "APPROVED",
            IsCurrent: true,
            IsActive: true,
            IsDeleted: false,
            InvalidatedByFlowEventId: null,
            AssignmentIsActive: true,
            PeriodIsActive: true,
            PeriodStatus: "APPROVED",
            PeriodCurrentReportId: reportId,
            PeriodSourceLifecycleReportId: reportId,
            PeriodSourceLifecycleRevision: 3,
            PeriodSourceLifecycleApplied: true,
            ContributionDecision: "INCLUDE",
            OwnerIncluded: ownerIncluded,
            OwnerDecisionCode: ownerIncluded
                ? "OWNER_INCLUDED" : "OWNER_EXCLUDED",
            OwnerOrdinal: ordinal,
            OwnerRunId: RunId,
            OwnerGenerationId: GenerationId,
            OwnerGenerationSha256: GenerationSha256,
            OwnerDirectSourceRevision: 17,
            MappingRevision: null,
            MappingSemanticSha256: null,
            FlowEffectiveStatus: null,
            FlowRuntime: null,
            InLifecycleMetricScope: true);
    }
    internal static ActualFlowRuntimeOwnerRevision Flow(string reportId)
        => new(
            5,
            "510000000000000000000001",
            "510000000000000000000002",
            3,
            "510000000000000000000003",
            Hash('1'),
            "flow-catalog-v1",
            Hash('2'),
            "520000000000000000000001",
            8,
            "ACTIVE",
            4,
            "520000000000000000000002",
            4,
            12,
            "COMPLETED",
            true,
            "520000000000000000000003",
            6,
            "COMPLETED",
            4,
            true,
            reportId,
            3,
            "APPROVED",
            true,
            null,
            null,
            null,
            "step-review",
            "520000000000000000000004",
            2,
            "INCLUDE",
            Hash('3'),
            null);

    internal static async Task<ActualSourceMembershipCapture> CaptureSources(
        IReadOnlyList<ActualSourceOwnerRevision> sources)
        => await new StatisticReconciliationActualSourceMembershipAdapter()
            .CaptureAsync(Scope(), new SourceReader(sources));

    internal static async Task<IncludedContextValue> IncludedContext()
    {
        var source = Source(Report1);
        var membership = await CaptureSources([source]);
        return new IncludedContextValue(Boundary(), source, membership);
    }

    internal static async Task<ActualDirectProjectionCapture> CaptureDirect(
        ActualDirectProjectionBoundary boundary,
        ActualSourceMembershipCapture membership,
        IReadOnlyList<WorkReportFieldStatValue>? fields = null,
        IReadOnlyList<WorkReportTableStatValue>? tables = null,
        IReadOnlyList<WorkReportLabelStatValue>? labels = null)
        => await new StatisticReconciliationActualDirectProjectionAdapter()
            .CaptureAsync(
                boundary,
                membership,
                new ProjectionReader(fields ?? [], tables ?? [], labels ?? []));

    internal static WorkReportFieldStatValue Field(
        ActualDirectProjectionBoundary boundary,
        ActualSourceOwnerRevision source,
        string fieldId,
        string fieldKey,
        string valueKind,
        decimal? numeric = null,
        bool? boolean = null,
        DateTime? date = null,
        string? text = null,
        string? bucketKey = null,
        string periodKey = "2026-08",
        bool assignmentIsActive = true,
        bool reportIsActive = true,
        string? invalidatedByFlowEventId = null)
    {
        var row = new WorkReportFieldStatValue
        {
            WorkId = boundary.WorkId,
            WorkAssignmentId = source.WorkAssignmentId,
            AssignmentIsActive = assignmentIsActive,
            ReportIsActive = reportIsActive,
            InvalidatedByFlowEventId = invalidatedByFlowEventId,
            WorkReportPeriodId = PeriodId,
            WorkAssignmentReportId = source.ReportId,
            DynamicFormTemplateId = boundary.DynamicFormTemplateId,
            FieldId = fieldId,
            FieldKey = fieldKey,
            FieldLabel = fieldKey,
            FieldType = "TEXT",
            StatisticLabelCodes = ["DETAIL"],
            BucketKey = bucketKey,
            BucketLabel = bucketKey,
            SourceKey = "FORM_FIELD",
            ValueKind = valueKind,
            NumericValue = numeric,
            BooleanValue = boolean,
            DateValueUtc = date,
            TextValue = text,
            PeriodKey = periodKey,
            PeriodInstanceKey = boundary.PeriodInstanceKey,
            PeriodKind = "MONTH",
            ReportStatus = 2,
            SourcePayloadRevision = source.PayloadRevision,
            SourcePayloadHash = source.PayloadSha256,
            DirectProjection = Pin(boundary, source)
        };
        row.Id = P9OwnerStableObjectId(
            boundary.GenerationId,
            "FIELD_VALUE",
            row.WorkAssignmentReportId,
            row.PeriodInstanceKey,
            boundary.DynamicFormTemplateId,
            fieldId,
            row.SourceKey,
            bucketKey,
            valueKind.ToUpperInvariant());
        return row;
    }

    internal static WorkReportTableStatValue Table(
        ActualDirectProjectionBoundary boundary,
        ActualSourceOwnerRevision source,
        string blockId,
        string metricKey,
        string rowKey,
        string columnKey,
        string valueKind,
        decimal? numeric = null,
        bool? boolean = null,
        DateTime? date = null,
        string? text = null,
        string? bucketKey = null,
        string periodKey = "2026-08")
    {
        var row = new WorkReportTableStatValue
        {
            WorkId = boundary.WorkId,
            WorkAssignmentId = source.WorkAssignmentId,
            AssignmentIsActive = true,
            ReportIsActive = true,
            WorkReportPeriodId = PeriodId,
            WorkAssignmentReportId = source.ReportId,
            DynamicFormTemplateId = boundary.DynamicFormTemplateId,
            DynamicExcelTemplateId = ExcelTemplateId,
            BlockId = blockId,
            TableMode = "FIXED_GRID",
            MetricKey = metricKey,
            MetricLabelCode = metricKey,
            RowKey = rowKey,
            ColumnKey = columnKey,
            SourceKey = "TABLE_CELL",
            DataType = "TEXT",
            ValueKind = valueKind,
            BucketKey = bucketKey,
            BucketLabel = bucketKey,
            TextValue = text,
            BooleanValue = boolean,
            DateValue = date,
            PeriodKey = periodKey,
            PeriodInstanceKey = boundary.PeriodInstanceKey,
            PeriodKind = "MONTH",
            ReportStatus = 2,
            Value = numeric ?? 0m,
            NumericValue = numeric,
            SourcePayloadRevision = source.PayloadRevision,
            SourcePayloadHash = source.PayloadSha256,
            DirectProjection = Pin(boundary, source)
        };
        row.Id = P9OwnerStableObjectId(
            boundary.GenerationId,
            "TABLE_VALUE",
            row.WorkAssignmentReportId,
            blockId,
            metricKey,
            row.SourceKey,
            bucketKey,
            valueKind.ToUpperInvariant());
        return row;
    }

    internal static WorkReportLabelStatValue Label(
        ActualDirectProjectionBoundary boundary,
        ActualSourceOwnerRevision source,
        string blockId,
        string rowKey,
        int rowIndex,
        string labelCode,
        string periodKey = "2026-08")
    {
        var row = new WorkReportLabelStatValue
        {
            WorkId = boundary.WorkId,
            WorkAssignmentId = source.WorkAssignmentId,
            AssignmentIsActive = true,
            ReportIsActive = true,
            WorkReportPeriodId = PeriodId,
            WorkAssignmentReportId = source.ReportId,
            DynamicFormTemplateId = boundary.DynamicFormTemplateId,
            DynamicExcelTemplateId = ExcelTemplateId,
            BlockId = blockId,
            PeriodKey = periodKey,
            PeriodInstanceKey = boundary.PeriodInstanceKey,
            PeriodKind = "MONTH",
            ReportStatus = 2,
            SheetId = "sheet-a",
            RowKey = rowKey,
            RowIndex = rowIndex,
            LabelCode = labelCode,
            Source = "ROW_LABEL",
            SourcePayloadRevision = source.PayloadRevision,
            SourcePayloadHash = source.PayloadSha256,
            DirectProjection = Pin(boundary, source)
        };
        row.Id = P9OwnerStableObjectId(
            boundary.GenerationId,
            "LABEL_VALUE",
            row.WorkAssignmentReportId,
            row.SourcePayloadRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            source.LifecycleRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            row.WorkReportPeriodId,
            row.PeriodInstanceKey,
            boundary.DynamicFormTemplateId,
            row.DynamicExcelTemplateId,
            row.BlockId,
            row.SheetId,
            row.RowKey,
            row.RowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            row.LabelCode,
            row.Source);
        return row;
    }

    private static WorkReportDirectProjectionPin Pin(
        ActualDirectProjectionBoundary boundary,
        ActualSourceOwnerRevision source)
        => new()
        {
            RunId = boundary.RunId,
            GenerationId = boundary.GenerationId,
            LifecycleEventKey = "event:approved:3",
            SourceReportId = source.ReportId,
            SourcePayloadRevision = source.PayloadRevision,
            SourcePayloadHash = source.PayloadSha256,
            SourceLifecycleRevision = source.LifecycleRevision,
            DirectSourceRevision = boundary.DirectSourceRevision,
            DynamicFormFamilyId = boundary.DynamicFormFamilyId,
            DynamicFormTemplateId = boundary.DynamicFormTemplateId,
            DynamicFormVersionNo = boundary.DynamicFormVersionNo,
            DynamicFormSchemaHash = boundary.DynamicFormSchemaSha256,
            ConfigId = boundary.ConfigId,
            ConfigVersionId = boundary.ConfigVersionId,
            ConfigVersionNo = boundary.ConfigVersionNo,
            ConfigRevision = boundary.ConfigRevision,
            ConfigHash = boundary.ConfigSha256,
            CandidateChainId = boundary.CandidateChainId,
            CatalogVersion = boundary.CatalogVersion,
            CatalogRawSha256 = boundary.CatalogRawSha256,
            CatalogSemanticSha256 = boundary.CatalogSemanticSha256,
            SchemaRawSha256 = boundary.SchemaRawSha256,
            SchemaSemanticSha256 = boundary.SchemaSemanticSha256,
            StageLockSha256 = boundary.StageLockSha256,
            SourceMembershipSignature = boundary.OwnerMembershipSignature,
            ComputedAtUtc = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc)
        };
}

internal sealed record IncludedContextValue(
    ActualDirectProjectionBoundary Boundary,
    ActualSourceOwnerRevision Source,
    ActualSourceMembershipCapture Membership);

internal sealed class SourceReader(IReadOnlyList<ActualSourceOwnerRevision> rows)
    : IStatisticReconciliationActualSourceOwnerReader
{
    internal int CallCount { get; private set; }

    public Task<IReadOnlyList<ActualSourceOwnerRevision>> ReadAsync(
        ActualSourceMembershipScope scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        return Task.FromResult(rows);
    }
}

internal sealed class ProjectionReader(
    IReadOnlyList<WorkReportFieldStatValue> fields,
    IReadOnlyList<WorkReportTableStatValue> tables,
    IReadOnlyList<WorkReportLabelStatValue> labels)
    : IStatisticReconciliationActualDirectProjectionOwnerReader
{
    public Task<IReadOnlyList<WorkReportFieldStatValue>> ReadFieldRowsAsync(
        ActualDirectProjectionBoundary boundary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(fields);
    }

    public Task<IReadOnlyList<WorkReportTableStatValue>> ReadTableMetricRowsAsync(
        ActualDirectProjectionBoundary boundary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(tables);
    }

    public Task<IReadOnlyList<WorkReportLabelStatValue>> ReadRowLabelRowsAsync(
        ActualDirectProjectionBoundary boundary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(labels);
    }
}
