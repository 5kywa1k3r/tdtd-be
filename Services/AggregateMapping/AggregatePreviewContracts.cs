using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

// Internal, server-owned capture. None of these facts are model-bound from HTTP.
internal sealed record AggregateMember(string Id, string Type, AggregateTableSchema? Table = null,
    IReadOnlyList<string>? AllowedChoiceCodes = null, AggregateListSchema? List = null);
internal sealed record AggregateTableSchema(string Identity, string Layout, IReadOnlyList<string> Columns,
    IReadOnlyList<string> Rows, IReadOnlyList<IReadOnlyList<string>> CellTypes)
{
    internal IReadOnlyList<int>? ColumnCoordinates { get; init; }
}
internal sealed record AggregateFunctionTrace(string Method, string? Basis, int InputCount, int PresentCount,
    string? ExactTotal, IReadOnlyList<string> ReportIds);
internal sealed record AggregatePreviewValueDiff(string NodeId, string PortId, JsonElement? Before, JsonElement? After,
    string BeforeState, string AfterState);
internal sealed record AggregateSchema(AggregateFormPinDto Pin, IReadOnlyDictionary<string, AggregateMember> Members);
internal sealed record AggregateSourceHeader(AggregateSourcePinDto Pin, string ParentAssignmentId,
    AggregateFormPinDto Form, string UnitId, string OccurrenceKey, bool WholeReportReadable,
    bool Active, bool Deleted, AggregateDataWindowDeclarationDto? DataWindow)
{
    public bool IsHistoricalData { get; init; }
    public DateTime? CompletedDate { get; init; }
    public DateTime? SubmittedAtUtc { get; init; }
    public DateTime? DueAtUtc { get; init; }
    public string? PeriodKey { get; init; }
    public string? UnitName { get; init; }
    public string? UnitShortName { get; init; }
    public string? UnitSymbol { get; init; }
    public string? ReportTitle { get; init; }
    public DateTime? AssignedAtUtc { get; init; }
    public DateTime? AssignmentStartDate { get; init; }
    public DateTime? AssignmentCompletedDate { get; init; }
    public DateTime? StartedDate { get; init; }
    public DateTime? PeriodStart { get; init; }
    public DateTime? PeriodEnd { get; init; }
    public DateTime? WorkStartDate { get; init; }
    public DateTime? WorkEndDate { get; init; }
}
internal sealed record AggregateSlot(string Key, string BindingId, string ScheduleRevision, string OccurrenceKey,
    string? PeriodId, string? CurrentReportId, AggregateFormPinDto Form, bool Readable,
    DateOnly EffectiveStart, DateOnly? EffectiveEnd, DateOnly OccurrenceDate,
    AggregateDataWindowDeclarationDto? DataWindow);
internal sealed record AggregateSourceListing(IReadOnlyList<AggregateSourceHeader> Headers,
    IReadOnlyList<AggregateSlot> Slots, bool Complete, string MembershipRevision, IReadOnlyList<string> CurrentUnitIds);
internal sealed record AggregateReadContext(AggregatePeriodContextDto Context, AggregateSchema TargetSchema,
    AggregateAuthorityFacts Authority, AggregateExpectedRevisionsDto Revisions, string AuthorizationFingerprint,
    AggregateDataWindowDeclarationDto? DataWindow, IReadOnlyDictionary<string, AggregateValue> PreviousValues)
{
    internal string? SavedResultReadError { get; init; }
}
internal sealed record AggregatePayload(AggregateSourcePinDto Pin, IReadOnlyDictionary<string, AggregateValue> Values);

// Deliberately no write methods. Concrete reader authorizes individual reads or a bounded
// batch capture, then revalidates the captured source set and authority before publication.
internal interface IAggregatePreviewReader
{
    Task<AggregateReadContext> ReadContextAsync(AggregatePeriodContextDto selector, string actor, CancellationToken ct);
    Task<AggregateSchema> ReadSchemaAsync(AggregateFormPinDto pin, AggregateReadContext context, string actor, CancellationToken ct);
    Task<AggregateSourceListing> ListSourcesAsync(AggregateReadContext context, AggregateFormPinDto form, string actor, CancellationToken ct);
    Task<AggregateSourceListing> ListReportSetSourcesAsync(AggregateReadContext context, AggregateFormPinDto form, string actor, CancellationToken ct)
        => ListSourcesAsync(context, form, actor, ct);
    // Only explicitly selected/excluded IDs. Inactive metadata is not a payload or a contributor.
    Task<IReadOnlyList<AggregateSourceHeader>> ReadInactiveSourcesAsync(AggregateReadContext context, AggregateFormPinDto form,
        IReadOnlyList<string> ids, string actor, CancellationToken ct) => Task.FromResult<IReadOnlyList<AggregateSourceHeader>>([]);
    Task<AggregatePayload> ReadPayloadAsync(AggregateReadContext context, AggregateSourceHeader header, AggregateSchema schema, string actor, CancellationToken ct);
    Task<bool> IsCurrentAsync(AggregateReadContext context, IReadOnlyList<AggregateSourcePinDto> pins,
        string membershipRevision, string actor, CancellationToken ct);
}

// Optional bounded read optimization. The ordinary reader contract and the final
// source/config/ACL revalidation remain authoritative; no writes or global cache.
internal interface IAggregateBatchPreviewReader
{
    Task PreparePayloadBatchAsync(AggregateReadContext context, IReadOnlyList<AggregateSourceHeader> headers,
        string actor, CancellationToken ct);
}

internal sealed record AggregateErrorLocation(string NodeId, string? PortId = null);
internal sealed class AggregatePreviewException(string code, string path = "$", Exception? inner = null) : Exception(code, inner)
{
    internal string Code { get; } = code;
    internal string Path { get; } = path;
    internal AggregateErrorLocation? Location { get; init; }
}
internal sealed class AggregateBudget(CancellationToken cancellation, int maxOperations = 500_000, long maxBytes = 16_777_216, TimeSpan? duration = null)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _operations;
    private long _bytes;
    internal void Spend(int operations = 1, long bytes = 0)
    {
        cancellation.ThrowIfCancellationRequested();
        if (operations < 0 || bytes < 0) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED", "$.budget.invalid");
        if (operations > maxOperations - _operations) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED", "$.budget.operations");
        if (bytes > maxBytes - _bytes) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED", "$.budget.bytes");
        _operations += operations; _bytes += bytes;
        if (_clock.Elapsed > (duration ?? TimeSpan.FromSeconds(10)))
            throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED", "$.budget.duration");
    }
}

internal sealed record AggregateTrace(string ReportId, string UnitId, string OccurrenceKey, string MemberId,
    int? Row = null, int? Column = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? RecordId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? FieldId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SourceSlot = null);
internal sealed record AggregateCell(AggregateValue Value, IReadOnlyList<AggregateTrace> Trace);
internal sealed record AggregateTable(AggregateTableSchema Schema, IReadOnlyList<IReadOnlyList<AggregateCell>> Rows,
    IReadOnlyList<string> Units)
{
    public IReadOnlyList<AggregateContentRowNote>? ContentRows { get; init; }
}
internal sealed record AggregateContentReference(string Kind, string Id, string Hash, int RowCount,
    int BlankRows = 0, int MaxContentLength = 0, int MaxUnitLength = 0);
internal interface IAggregateContentSink
{
    Task<AggregateContentReference> CaptureAsync(AggregateReadContext context, AggregateTable table, CancellationToken ct);
}
internal sealed record AggregateContentRowNote(string RowKey, string SourceKey, string ReportId, string UnitId,
    string UnitName, string? ReportTitle, string OccurrenceKey, string MemberId, string? CompletedDate, string? SubmittedAtUtc)
{
    public string? UnitFullName { get; init; }
    public string? UnitShortName { get; init; }
    public string? UnitSymbol { get; init; }
}
internal sealed record AggregateValue(string Type, string State, string? Text = null, bool? Boolean = null,
    AggregateNumber? Number = null, AggregateTable? Table = null, IReadOnlyList<string>? Choices = null)
{
    public JsonElement? StoredWire { get; init; }
    public AggregateContentReference? ContentReference { get; init; }
    public AggregateListValue? List { get; init; }
    public AggregateListReference? ListReference { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? TextFormat { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LengthText { get; init; }
    internal static AggregateValue Blank(string type) => new(type, "BLANK");
    internal static AggregateValue NoResult(string type) => new(type, "NO_RESULT");
    internal static AggregateValue Numeric(AggregateNumber number) => new("NUMBER", "VALUE", Number: number);
}
internal sealed record AggregateObservation(AggregateValue Value, IReadOnlyList<AggregateTrace> Trace)
{
    public AggregateContentRowNote? SourceNote { get; init; }
}
internal sealed record AggregateChannel(string Type, string Shape, IReadOnlyList<AggregateObservation> Items,
    bool TableOrigin = false, AggregateListSchema? ListSchema = null)
{
    internal IReadOnlyList<AggregateTrace> EligibleSources { get; init; } = [];
}
internal sealed record AggregateResolvedSource(AggregateNodeDto Node, AggregateSchema Schema,
    IReadOnlyDictionary<string, AggregateChannel> Outputs, IReadOnlyList<AggregateSourcePinDto> Linked,
    IReadOnlyList<AggregateSourcePinDto> Eligible, IReadOnlyList<AggregateCoverageSlotDto> Coverage,
    IReadOnlyList<AggregateResolvedWindowDto> Windows, IReadOnlyList<AggregateIssueDto> Issues,
    string MembershipRevision, IReadOnlyList<string> UnitIds, bool Complete);

internal static class AggregateDigest
{
    internal static string Of<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
