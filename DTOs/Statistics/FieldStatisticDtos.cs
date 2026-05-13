namespace tdtd_be.DTOs.Statistics;

public sealed class FieldStatisticSummaryRequest
{
    public string? WorkId { get; set; }
    public string? ScopeType { get; set; }
    public string? ScopeId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? FieldId { get; set; }
    public string? FieldKey { get; set; }
    public string? FieldType { get; set; }
    public string? BucketKey { get; set; }
    public bool? ShowInTree { get; set; }
    public bool? ShowInDetail { get; set; }
    public string? PeriodKey { get; set; }
    public string? PeriodKeyFrom { get; set; }
    public string? PeriodKeyTo { get; set; }
    public string? PeriodInstanceKey { get; set; }
    public int? ReportStatus { get; set; }
    public int Page { get; set; } = 0;
    public int PageSize { get; set; } = 50;
}

public sealed class FieldStatisticSummaryRow
{
    public string WorkId { get; set; } = default!;
    public string ScopeType { get; set; } = default!;
    public string ScopeId { get; set; } = default!;
    public string? RootAssignmentId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateCode { get; set; }
    public string? DynamicFormTemplateName { get; set; }
    public string FieldId { get; set; } = default!;
    public string FieldKey { get; set; } = default!;
    public string FieldLabel { get; set; } = default!;
    public string FieldType { get; set; } = default!;
    public bool ShowInTree { get; set; }
    public bool ShowInDetail { get; set; }
    public string? BucketKey { get; set; }
    public string? BucketLabel { get; set; }
    public string PeriodKey { get; set; } = default!;
    public string PeriodInstanceKey { get; set; } = default!;
    public string PeriodKind { get; set; } = default!;
    public int ReportStatus { get; set; }
    public long ValueCount { get; set; }
    public long NumericValueCount { get; set; }
    public decimal? Sum { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public decimal? Average { get; set; }
    public long TrueCount { get; set; }
    public long FalseCount { get; set; }
    public DateTime? LatestDateUtc { get; set; }
    public long ReportCount { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class FieldStatisticSummaryResponse
{
    public List<FieldStatisticSummaryRow> Rows { get; set; } = new();
    public long TotalRows { get; set; }
    public long TotalValueCount { get; set; }
    public decimal TotalSum { get; set; }
    public long TotalReportCount { get; set; }
}

public sealed class RebuildFieldStatisticRequest
{
    public string WorkId { get; set; } = default!;
    public string? PeriodInstanceKey { get; set; }
    public string? DynamicFormTemplateId { get; set; }
}

public sealed class RebuildFieldStatisticResponse
{
    public string WorkId { get; set; } = default!;
    public string? PeriodInstanceKey { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public int ReportCount { get; set; }
}

public sealed class FieldTextConcatRequest
{
    public string WorkId { get; set; } = default!;
    public string? ScopeType { get; set; }
    public string? ScopeId { get; set; }
    public string DynamicFormTemplateId { get; set; } = default!;
    public string? FieldId { get; set; }
    public string? FieldKey { get; set; }
    public string? Q { get; set; }
    public string? PeriodKey { get; set; }
    public string? PeriodKeyFrom { get; set; }
    public string? PeriodKeyTo { get; set; }
    public string? PeriodInstanceKey { get; set; }
    public int? ReportStatus { get; set; }
    public int Page { get; set; } = 0;
    public int PageSize { get; set; } = 50;
    public int MaxChars { get; set; } = 10000;
    public int MaxRowChars { get; set; } = 2000;
    public int ScanLimit { get; set; } = 1000;
}

public sealed class FieldTextConcatRow
{
    public string WorkAssignmentReportId { get; set; } = default!;
    public string WorkReportPeriodId { get; set; } = default!;
    public string AssignmentId { get; set; } = default!;
    public string? AssignmentCode { get; set; }
    public string AssignmentName { get; set; } = default!;
    public string? AssigneeUserId { get; set; }
    public string? AssigneeFullName { get; set; }
    public string? AssigneeUsername { get; set; }
    public string? UnitId { get; set; }
    public string? UnitLabel { get; set; }
    public string PeriodKey { get; set; } = default!;
    public string PeriodInstanceKey { get; set; } = default!;
    public string PeriodKind { get; set; } = default!;
    public int ReportStatus { get; set; }
    public string Text { get; set; } = string.Empty;
    public int CharCount { get; set; }
    public bool RowTruncated { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
}

public sealed class FieldTextConcatResponse
{
    public string WorkId { get; set; } = default!;
    public string ScopeType { get; set; } = default!;
    public string? ScopeId { get; set; }
    public string DynamicFormTemplateId { get; set; } = default!;
    public string FieldId { get; set; } = default!;
    public string FieldKey { get; set; } = default!;
    public string FieldLabel { get; set; } = default!;
    public string FieldType { get; set; } = default!;
    public string ConcatenatedText { get; set; } = string.Empty;
    public List<FieldTextConcatRow> Rows { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public long TotalRows { get; set; }
    public int ReturnedRows { get; set; }
    public int TotalChars { get; set; }
    public int MaxChars { get; set; }
    public bool Truncated { get; set; }
    public long MatchingReportCount { get; set; }
    public int ScannedReportCount { get; set; }
    public int ScanLimit { get; set; }
    public bool HasMoreReportsThanScanLimit { get; set; }
}

public sealed class FieldTextConcatExportFile
{
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public string ContentType { get; set; } = "text/csv; charset=utf-8";
    public string FileName { get; set; } = "text-concat.csv";
}
