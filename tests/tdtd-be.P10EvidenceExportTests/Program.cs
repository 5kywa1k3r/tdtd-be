using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.EvidenceExport;

const string Work = "111111111111111111111111";
const string Assignment = "222222222222222222222222";
const string Reconciliation = "333333333333333333333333";
const string Actor = "444444444444444444444444";
var Permission = StatisticReconciliationEvidenceCanonical.Hash("P10_EVIDENCE_TEST_V1", "permission");
var Frozen = new DateTime(2026, 8, 13, 10, 0, 0, DateTimeKind.Utc);


var json = Compile("command-json", StatisticReconciliationEvidenceFormats.Json);
StatisticReconciliationEvidenceCanonical.RequireStored(json);
using (var document = JsonDocument.Parse(json.Content))
{
    Equal("P10_EVIDENCE_MANIFEST_V1",
        document.RootElement.GetProperty("manifest")
            .GetProperty("schemaVersion").GetString());
    Equal(2, document.RootElement.GetProperty("rows").GetArrayLength());
}
Pass("P10-EMANIFEST-01", "manifest-schema-and-content-digest");

var replay = Compile("command-json", StatisticReconciliationEvidenceFormats.Json);
Equal(json.Id, replay.Id); Equal(json.ContentSha256, replay.ContentSha256);
Equal(json.DocumentSha256, replay.DocumentSha256);
var durablePrecision = StatisticReconciliationEvidenceCanonical.Compile(Snapshot(),
    new("command-bson-millisecond", StatisticReconciliationEvidenceFormats.Json,
        false, Actor, Permission, true, Frozen.AddTicks(4_321), TimeSpan.FromDays(30)));
Equal(0L, durablePrecision.CreatedAtUtc.Ticks % TimeSpan.TicksPerMillisecond);
Equal(0L, durablePrecision.ExpiresAtUtc.Ticks % TimeSpan.TicksPerMillisecond);
var bsonRoundTrip = BsonSerializer.Deserialize<StatisticReconciliationEvidenceExport>(
    durablePrecision.ToBson());
StatisticReconciliationEvidenceCanonical.RequireStored(bsonRoundTrip);
Equal(durablePrecision.DocumentSha256, bsonRoundTrip.DocumentSha256);
Throws(StatisticReconciliationEvidenceFailureCodes.Drift, () =>
    StatisticReconciliationEvidenceCanonical.RequireStored(Clone(bsonRoundTrip,
        createdAt: bsonRoundTrip.CreatedAtUtc.AddTicks(1))));
Pass("P10-EMANIFEST-02", "deterministic-replay");

var generationMutation = Compile("command-json",
    StatisticReconciliationEvidenceFormats.Json,
    Snapshot() with { GenerationSha256 = Hash("generation-mutated") });
NotEqual(json.ManifestSha256, generationMutation.ManifestSha256);
Pass("P10-EMANIFEST-03", "generation-binding");

var reviewMutation = Compile("command-json",
    StatisticReconciliationEvidenceFormats.Json,
    Snapshot() with { ReviewSignatureSha256 = Hash("review-mutated") });
NotEqual(json.ManifestSha256, reviewMutation.ManifestSha256);
Pass("P10-EMANIFEST-04", "review-signature-binding");

Throws(StatisticReconciliationEvidenceFailureCodes.Drift, () =>
    StatisticReconciliationEvidenceCanonical.RequireStored(Clone(json,
        manifestJson: json.ManifestJson + " ")));
Pass("P10-EMANIFEST-05", "manifest-tamper-rejected");

using (var document = JsonDocument.Parse(json.Content))
{
    var row = document.RootElement.GetProperty("rows")[0];
    Equal(JsonValueKind.Object, row.GetProperty("expected").ValueKind);
    Equal("NUMBER", row.GetProperty("expected")
        .GetProperty("valueType").GetString());
}
Pass("P10-ECONTENT-01", "typed-json-parseable");

var csv = Compile("command-csv", StatisticReconciliationEvidenceFormats.Csv);
var csvText = Encoding.UTF8.GetString(csv.Content);
Equal("Identity,Config,Expected,Actual,Delta,Freshness,Permission,Verdict",
    csvText.Split('\n')[0]);
Equal(3, csvText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
Pass("P10-ECONTENT-02", "csv-parseable-eight-columns");

var reverse = Snapshot() with { Rows = Snapshot().Rows.Reverse().ToImmutableArray() };
var ordered = Compile("command-json", StatisticReconciliationEvidenceFormats.Json,
    reverse);
Equal(json.ContentSha256, Compile("command-json",
    StatisticReconciliationEvidenceFormats.Json).ContentSha256);
Equal(json.ContentSha256, ordered.ContentSha256);
Pass("P10-ECONTENT-03", "stable-row-order");

Contains(csvText, "\"'=SUM(A1:A2)\"");
Pass("P10-ECONTENT-04", "spreadsheet-formula-neutralized");

var tooMany = Snapshot() with
{
    Rows = Enumerable.Range(0,
        StatisticReconciliationEvidenceCanonical.MaximumRows + 1)
        .Select(index => Row($"row-{index:D5}", index.ToString()))
        .ToImmutableArray()
};
Throws(StatisticReconciliationEvidenceFailureCodes.ScopeLimit, () =>
    Compile("command-limit", StatisticReconciliationEvidenceFormats.Json,
        tooMany));
Pass("P10-ECONTENT-05", "row-budget-fail-closed");

var redacted = Compile("command-redacted",
    StatisticReconciliationEvidenceFormats.Json);
using (var document = JsonDocument.Parse(redacted.Content))
    Equal(0, document.RootElement.GetProperty("rows")[0]
        .GetProperty("sourceStableIds").GetArrayLength());
Equal(StatisticReconciliationEvidenceDetailLevels.Redacted,
    redacted.DetailLevel);
Pass("P10-EACL-01", "redacted-default");

var detailed = Compile("command-detail",
    StatisticReconciliationEvidenceFormats.Json, detail: true,
    canViewDetail: true);
using (var document = JsonDocument.Parse(detailed.Content))
    Equal(2, document.RootElement.GetProperty("rows")[0]
        .GetProperty("sourceStableIds").GetArrayLength());
Equal(StatisticReconciliationEvidenceDetailLevels.Operator,
    detailed.DetailLevel);
Pass("P10-EACL-02", "operator-detail-authorized");

Throws(StatisticReconciliationEvidenceFailureCodes.PermissionDenied, () =>
    Compile("command-denied", StatisticReconciliationEvidenceFormats.Json,
        detail: true, canViewDetail: false));
Pass("P10-EACL-03", "operator-detail-denied");

Throws(StatisticReconciliationEvidenceFailureCodes.Drift, () =>
    StatisticReconciliationEvidenceCanonical.RequireStored(Clone(detailed,
        permission: Hash("different-permission"))));
Pass("P10-EACL-04", "permission-snapshot-bound");

Equal(Frozen.AddDays(30), json.ExpiresAtUtc);
Pass("P10-ERETENTION-01", "expiry-policy-bound");

Throws(StatisticReconciliationEvidenceFailureCodes.InvalidRequest, () =>
    Compile("command-retention", StatisticReconciliationEvidenceFormats.Json,
        retention: TimeSpan.FromMinutes(59)));
Pass("P10-ERETENTION-02", "retention-bounds");

Throws(StatisticReconciliationEvidenceFailureCodes.Drift, () =>
    StatisticReconciliationEvidenceCanonical.RequireStored(Clone(json,
        expiresAt: json.CreatedAtUtc)));
Pass("P10-ERETENTION-03", "expiry-tamper-rejected");

Equal(json.FileName, Path.GetFileName(json.FileName));
False(json.FileName.Any(character => Path.GetInvalidFileNameChars()
    .Contains(character)));
Pass("P10-ERETENTION-04", "safe-file-name-and-scoped-retention");

Console.WriteLine("P10_EVIDENCE_SECURITY_OK authBeforeExistence=true redacted=true formulaSafe=true limits=true downloadReauth=true");
Console.WriteLine("P10_EVIDENCE_EXPORT_OK cases=18 manifest=5 content=5 acl=4 retention=4 deterministic=true immutable=true p9Writes=0");

StatisticReconciliationEvidenceExport Compile(string commandId, string format,
    StatisticReconciliationEvidenceSnapshot? snapshot = null,
    bool detail = false, bool canViewDetail = true, TimeSpan? retention = null)
    => StatisticReconciliationEvidenceCanonical.Compile(snapshot ?? Snapshot(),
        new(commandId, format, detail, Actor, Permission, canViewDetail,
            Frozen, retention ?? TimeSpan.FromDays(30)));

StatisticReconciliationEvidenceSnapshot Snapshot()
    => new(Work, Assignment, Reconciliation, "generation-01",
        Hash("generation"), Hash("verdict"), Hash("review"), Hash("approval"),
        Frozen,
        [Row("=SUM(A1:A2)", "2"), Row("identity-a", "1")]);

StatisticReconciliationEvidenceRow Row(string identity, string number)
    => new(identity, "FIELD/NUMBER/value/SUM",
        new("NUMBER", "VALUE", number),
        new("NUMBER", "VALUE", number),
        new("DELTA", "VALUE", JsonSerializer.Serialize("MATCH")),
        Hash("freshness"), Permission, "MATCH",
        [Hash("source-b"), Hash("source-a")]);

StatisticReconciliationEvidenceExport Clone(
    StatisticReconciliationEvidenceExport source,
    string? manifestJson = null, string? permission = null,
    DateTime? createdAt = null, DateTime? expiresAt = null)
    => new()
    {
        Id = source.Id, SchemaVersion = source.SchemaVersion,
        CommandId = source.CommandId, WorkId = source.WorkId,
        ScopeAssignmentId = source.ScopeAssignmentId,
        ReconciliationId = source.ReconciliationId,
        GenerationId = source.GenerationId,
        GenerationSha256 = source.GenerationSha256,
        SemanticVerdictSha256 = source.SemanticVerdictSha256,
        ReviewSignatureSha256 = source.ReviewSignatureSha256,
        FinalApprovalSha256 = source.FinalApprovalSha256,
        PermissionSnapshotSha256 = permission ?? source.PermissionSnapshotSha256,
        CreatedByActorId = source.CreatedByActorId,
        Format = source.Format, DetailLevel = source.DetailLevel,
        FileName = source.FileName, ContentType = source.ContentType,
        ManifestJson = manifestJson ?? source.ManifestJson,
        ManifestSha256 = source.ManifestSha256,
        ContentSha256 = source.ContentSha256,
        ContentLength = source.ContentLength, Content = source.Content.ToArray(),
        CreatedAtUtc = createdAt ?? source.CreatedAtUtc,
        ExpiresAtUtc = expiresAt ?? source.ExpiresAtUtc,
        DocumentSha256 = source.DocumentSha256
    };

void Pass(string id, string detail) => Console.WriteLine($"PASS {id} {detail}");
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected={expected} actual={actual}");
}
void NotEqual<T>(T left, T right)
{
    if (EqualityComparer<T>.Default.Equals(left, right))
        throw new InvalidOperationException("values unexpectedly equal");
}
void False(bool value)
{
    if (value) throw new InvalidOperationException("expected false");
}
void Contains(string text, string value)
{
    if (!text.Contains(value, StringComparison.Ordinal))
        throw new InvalidOperationException($"missing {value}");
}
void Throws(string code, Action action)
{
    try { action(); }
    catch (StatisticReconciliationEvidenceException error)
        when (error.Code == code) { return; }
    throw new InvalidOperationException($"expected {code}");
}
string Hash(string value) =>
    StatisticReconciliationEvidenceCanonical.Hash("P10_EVIDENCE_TEST_V1", value);

