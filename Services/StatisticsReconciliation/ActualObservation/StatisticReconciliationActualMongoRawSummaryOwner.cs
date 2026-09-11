using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

/// <summary>
/// Resolves raw actual payloads from the exact P9 membership decision tuple.
/// The expected projection is never queried here.  A second full read after
/// compilation closes in-place payload/report drift during the proof window.
/// </summary>
internal sealed class StatisticReconciliationActualMongoRawSummaryOwner(
    MongoDbContext context,
    IWorkReportPayloadReader payloadReader,
    IStatisticReconciliationActualRawSummaryCompiler compiler)
    : IStatisticReconciliationActualRawSummaryOwner
{
    internal const string ProofSchemaVersion =
        "P10_ACTUAL_RAW_SUMMARY_PROOF_V1";

    public async Task<StatisticReconciliationActualRawSummaryProof> ResolveAsync(
        StatisticReconciliationActualSummaryPlanBinding exactPlan,
        ActualSourceMembershipCapture actualSources,
        CancellationToken cancellationToken,
        bool projectDynamicFormDirectFields = false)
    {
        ArgumentNullException.ThrowIfNull(exactPlan);
        ArgumentNullException.ThrowIfNull(actualSources);
        var plan = StatisticReconciliationActualSummaryPlanBinding.Normalize(
            exactPlan);
        RequireSourceCapture(actualSources);

        var first = await CollectAsync(actualSources,
                projectDynamicFormDirectFields, cancellationToken)
            .ConfigureAwait(false);
        var firstCollect = CollectSha(first);
        var atoms = compiler.Compile(plan, first);
        var atomManifest = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_RAW_SUMMARY_ATOM_MANIFEST_V1",
            atoms.Select(value => value.AtomSemanticSha256));

        var second = await CollectAsync(actualSources,
                projectDynamicFormDirectFields, cancellationToken)
            .ConfigureAwait(false);
        var secondCollect = CollectSha(second);
        if (firstCollect != secondCollect || !first.SequenceEqual(second))
            throw Fail("RAW_DOUBLE_COLLECT_DRIFT");
        var doubleCollect = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_RAW_SUMMARY_DOUBLE_COLLECT_V1",
            actualSources.CaptureSemanticSha256,
            firstCollect,
            secondCollect);
        var sourceManifest = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_RAW_SUMMARY_SOURCE_MANIFEST_V1",
            first.Select(value => value.EnvelopeSemanticSha256));
        var membership = actualSources.MembershipSemanticSha256!;
        var proof = StatisticReconciliationActualCanonical.Hash(
            ProofSchemaVersion,
            plan.SemanticSha256,
            actualSources.CaptureSemanticSha256,
            membership,
            sourceManifest,
            StatisticReconciliationActualCanonical.Integer(first.Length),
            atomManifest,
            StatisticReconciliationActualCanonical.Integer(atoms.Length),
            doubleCollect);
        return new StatisticReconciliationActualRawSummaryProof(
            ProofSchemaVersion,
            plan.SemanticSha256,
            actualSources.CaptureSemanticSha256,
            membership,
            first,
            sourceManifest,
            atoms,
            atomManifest,
            firstCollect,
            secondCollect,
            doubleCollect,
            proof);
    }

    private async Task<ImmutableArray<
        StatisticReconciliationActualRawPayloadEnvelope>> CollectAsync(
            ActualSourceMembershipCapture source,
            bool projectDynamicFormDirectFields,
            CancellationToken cancellationToken)
    {
        var builder = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualRawPayloadEnvelope>(
            source.IncludedSources.Length);
        foreach (var decision in source.IncludedSources
                     .OrderBy(value => value.SourceStableIdentitySha256,
                         StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!decision.Included)
                throw Fail("RAW_INCLUDED_DECISION_REQUIRED");
            var owner = decision.ObservedOwner;
            _ = RequireActualStableIdentitySha256(
                decision.SourceStableIdentitySha256,
                owner.WorkId,
                owner.WorkAssignmentId,
                owner.ReportId);

            var reports = await context.WorkAssignmentReports
                .Find(report =>
                    report.Id == owner.ReportId &&
                    report.WorkId == owner.WorkId &&
                    report.WorkAssignmentId == owner.WorkAssignmentId &&
                    report.PeriodInstanceKey == owner.PeriodInstanceKey &&
                    report.DynamicFormTemplateId == owner.DynamicFormTemplateId &&
                    report.PayloadRevision == owner.PayloadRevision &&
                    report.LifecycleRevision == owner.LifecycleRevision &&
                    !report.IsDeleted)
                .Limit(2)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (reports.Count != 1)
                throw Fail("RAW_EXACT_REPORT_REQUIRED");
            var report = reports[0];
            if (!StringComparer.Ordinal.Equals(
                    report.PayloadHash, owner.PayloadSha256) ||
                !StringComparer.Ordinal.Equals(
                    report.Status.ToString().ToUpperInvariant(),
                    owner.LifecycleStatus))
                throw Fail("RAW_REPORT_OWNER_BINDING_MISMATCH");

            var payloadDocuments = await context.WorkReportPayloads
                .Find(payload =>
                    payload.Id == owner.PayloadDocumentId &&
                    payload.ReportId == owner.ReportId &&
                    payload.PayloadRevision == owner.PayloadRevision &&
                    payload.Status == WorkReportPayloadStatus.Ready &&
                    !payload.IsDeleted)
                .Limit(2)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (payloadDocuments.Count != 1 ||
                !StringComparer.Ordinal.Equals(
                    payloadDocuments[0].PayloadHash, owner.PayloadSha256))
                throw Fail("RAW_EXACT_PAYLOAD_DOCUMENT_REQUIRED");

            var payloadDocument = payloadDocuments[0];
            var blocks = await context.WorkReportTableValues
                .Find(block =>
                    block.ReportId == owner.ReportId &&
                    block.PayloadRevision == owner.PayloadRevision &&
                    block.Status == WorkReportPayloadStatus.Ready &&
                    !block.IsDeleted)
                .SortBy(block => block.BlockOrder)
                .ThenBy(block => block.BlockId)
                .Limit(10_001)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (blocks.Count > 10_000 ||
                !blocks.Select(block => block.BlockOrder)
                    .SequenceEqual(Enumerable.Range(0, blocks.Count)) ||
                blocks.Select(block => block.BlockId)
                    .Distinct(StringComparer.Ordinal).Count() != blocks.Count ||
                blocks.Any(block =>
                    string.IsNullOrEmpty(block.BlockId) ||
                    !StringComparer.Ordinal.Equals(
                        block.BlockId, block.BlockId.Trim()) ||
                    !StringComparer.Ordinal.Equals(
                        block.PayloadHash, RawSha256(block.ValuesJson))))
                throw Fail("RAW_TABLE_BLOCK_INTEGRITY_INVALID");
            var fullPayloadHash = WorkReportPayloadHash.Compute(
                payloadDocument.Values1DJson,
                payloadDocument.FieldValuesJson,
                payloadDocument.TableValuesRootJson,
                payloadDocument.SummarySourceJson,
                blocks.Select(block => new WorkReportPayloadBlockHash(
                    block.BlockId,
                    block.BlockOrder,
                    block.PayloadHash)));
            if (!StringComparer.Ordinal.Equals(
                    fullPayloadHash, owner.PayloadSha256))
                throw Fail("RAW_FULL_PAYLOAD_HASH_MISMATCH");

            var snapshot = await payloadReader.LoadReportPayloadAsync(
                    report, cancellationToken)
                .ConfigureAwait(false);
            if (!snapshot.IsExternalPayload || !snapshot.PayloadHashVerified ||
                snapshot.PayloadRevision != owner.PayloadRevision ||
                snapshot.PayloadStatus != WorkReportPayloadStatus.Ready ||
                !StringComparer.Ordinal.Equals(
                    snapshot.PayloadHash, owner.PayloadSha256))
                throw Fail("RAW_FULL_PAYLOAD_INTEGRITY_INVALID");
            var canonical = CanonicalPayload(
                snapshot.Values1DJson,
                snapshot.FieldValuesJson,
                snapshot.TableValuesJson,
                snapshot.SummarySourceJson,
                projectDynamicFormDirectFields);
            var canonicalSha =
                StatisticReconciliationActualJson.RawSha256(canonical);
            var semantic = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_RAW_PAYLOAD_ENVELOPE_V1",
                decision.SourceStableIdentitySha256,
                owner.WorkId,
                owner.WorkAssignmentId,
                owner.ReportId,
                owner.PayloadDocumentId,
                StatisticReconciliationActualCanonical.Integer(
                    owner.PayloadRevision),
                owner.PayloadSha256,
                StatisticReconciliationActualCanonical.Integer(
                    owner.LifecycleRevision),
                owner.LifecycleSha256,
                decision.OwnerStateSemanticSha256,
                decision.DecisionSemanticSha256,
                canonicalSha);
            builder.Add(new StatisticReconciliationActualRawPayloadEnvelope(
                decision.SourceStableIdentitySha256,
                owner.WorkId,
                owner.WorkAssignmentId,
                owner.ReportId,
                owner.PayloadDocumentId,
                owner.PayloadRevision,
                owner.PayloadSha256,
                canonicalSha,
                canonical,
                semantic));
        }
        var result = builder.ToImmutable();
        if (result.Select(value => value.SourceStableIdentitySha256)
                .Distinct(StringComparer.Ordinal).Count() != result.Length ||
            result.Select(value => value.PayloadDocumentId)
                .Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw Fail("RAW_SOURCE_DUPLICATE");
        return result;
    }

    internal static string CanonicalPayload(
        string? values1DJson,
        string? fieldValuesJson,
        string? tableValuesJson,
        string? summarySourceJson,
        bool projectDynamicFormDirectFields)
    {
        var fieldValues = Parse(fieldValuesJson, "RAW_FIELD_VALUES_JSON");
        var root = new JsonObject
        {
            ["values1D"] = Parse(values1DJson, "RAW_VALUES1D_JSON"),
            ["fieldValues"] = fieldValues,
            ["tableValues"] = Parse(
                tableValuesJson, "RAW_TABLE_VALUES_JSON"),
            ["summarySource"] = Parse(
                summarySourceJson, "RAW_SUMMARY_SOURCE_JSON")
        };
        if (projectDynamicFormDirectFields)
            root["directFieldValues"] =
                ProjectDynamicFormDirectFields(fieldValues);
        using var document = StatisticReconciliationActualJson.ParseStrict(
            root.ToJsonString(), "RAW_COMBINED_PAYLOAD_JSON");
        return StatisticReconciliationActualJson.Canonicalize(
            document.RootElement);
    }

    private static JsonObject ProjectDynamicFormDirectFields(
        JsonNode? fieldValues)
    {
        if (fieldValues is null)
            return new JsonObject();
        if (fieldValues is not JsonObject rawFieldValues)
            throw Fail("RAW_DIRECT_FIELD_VALUES_OBJECT_REQUIRED");
        if (rawFieldValues["values"] is null)
            return (JsonObject)rawFieldValues.DeepClone();
        if (rawFieldValues["values"] is JsonObject values)
            return (JsonObject)values.DeepClone();
        throw Fail("RAW_DIRECT_FIELD_VALUES_ENVELOPE_INVALID");
    }

    private static JsonNode? Parse(string? value, string name)
    {
        if (value is null)
            return null;
        using var document = StatisticReconciliationActualJson.ParseStrict(
            value, name);
        var canonical = StatisticReconciliationActualJson.Canonicalize(
            document.RootElement);
        return JsonNode.Parse(canonical, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 128
        });
    }

    private static void RequireSourceCapture(
        ActualSourceMembershipCapture source)
    {
        if (source.Decisions.IsDefault || source.IncludedSources.IsDefault ||
            source.IncludedSources.Any(value => !value.Included) ||
            source.IncludedSources.Length !=
            source.Decisions.Count(value => value.Included) ||
            source.IncludedSources.Length >
            StatisticReconciliationActualSourceMembershipAdapter
                .MaxSourceCandidates)
            throw Fail("RAW_SOURCE_CAPTURE_INVALID");
        var included = source.Decisions.Where(value => value.Included)
            .OrderBy(value => value.ReportId, StringComparer.Ordinal)
            .ThenBy(value => value.ObservedOwner.OwnerOrdinal)
            .ToImmutableArray();
        if (!included.SequenceEqual(source.IncludedSources) ||
            source.MembershipSemanticSha256 is null ||
            StatisticReconciliationActualCanonical.Sha256(
                source.MembershipSemanticSha256,
                "RAW_MEMBERSHIP_SEMANTIC_SHA256") !=
            StatisticReconciliationActualSourceMembershipAdapter
                .IncludedMembershipSemanticSha256(included))
            throw Fail("RAW_SOURCE_MEMBERSHIP_INTEGRITY_INVALID");
    }

    private static string CollectSha(
        IEnumerable<StatisticReconciliationActualRawPayloadEnvelope> values)
        => StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_RAW_SUMMARY_COLLECT_V1",
            values.Select(value => value.EnvelopeSemanticSha256));

    internal static string RequireActualStableIdentitySha256(
        string? stored,
        string workId,
        string workAssignmentId,
        string reportId)
    {
        var expected = StatisticReconciliationActualSourceMembershipAdapter
            .ActualStableIdentitySha256(workId, workAssignmentId, reportId);
        if (StatisticReconciliationActualCanonical.Sha256(
                stored, "RAW_STABLE_IDENTITY_SHA256") != expected)
            throw Fail("RAW_STABLE_IDENTITY_MISMATCH");
        return expected;
    }

    private static string RawSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"ACTUAL_RAW_SUMMARY_OWNER:{reason}");
}
