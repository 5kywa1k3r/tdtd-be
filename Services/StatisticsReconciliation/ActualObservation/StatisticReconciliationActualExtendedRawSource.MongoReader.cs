using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class
    StatisticReconciliationActualMongoExtendedRawSourceCollectReader
    : IStatisticReconciliationActualExtendedRawSourceCollectReader
{
    private const int MaxSourceAssignments = 49_999;
    private const int MaxPayloadBlocks = 10_000;

    private readonly MongoDbContext context;
    private readonly IWorkReportPayloadReader payloadReader;
    private readonly StatisticReconciliationActualExtendedRawSourceTypedCompiler
        typedCompiler;

    public StatisticReconciliationActualMongoExtendedRawSourceCollectReader(
        MongoDbContext context,
        IWorkReportPayloadReader payloadReader,
        StatisticReconciliationActualExtendedRawSourceTypedCompiler
            typedCompiler)
    {
        this.context = context;
        this.payloadReader = payloadReader;
        this.typedCompiler = typedCompiler;
    }

    public async Task<StatisticReconciliationActualExtendedRawSourceCollect>
        CollectAsync(
            StatisticReconciliationActualExtendedRawSourceCommand command,
            CancellationToken cancellationToken)
    {
        try
        {
            var plan = StatisticReconciliationActualSummaryPlanBinding.Normalize(
                command.SummaryPlan);
            var advancedApplicable = plan.IdentityDescriptors.Any(value =>
                value.Family == "ADVANCED");
            var diffApplicable = plan.IdentityDescriptors.Any(value =>
                value.Family == "DIFF");
            var advanced = advancedApplicable
                ? await CollectAdvancedAsync(command, cancellationToken)
                    .ConfigureAwait(false)
                : StatisticReconciliationActualExtendedRawSourceIntegrity
                    .AdvancedNotApplicable(plan.SemanticSha256);
            var diff = diffApplicable
                ? await CollectDiffAsync(command, cancellationToken)
                    .ConfigureAwait(false)
                : StatisticReconciliationActualExtendedRawSourceIntegrity
                    .DiffNotApplicable(plan.SemanticSha256);
            return StatisticReconciliationActualExtendedRawSourceIntegrity.Collect(
                plan.SemanticSha256, advanced, diff);
        }
        catch (StatisticReconciliationActualExtendedRawSourceIncompleteException)
        {
            throw;
        }
        catch (AppException)
        {
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .OwnerReadFailed);
        }
    }

    private async Task<StatisticReconciliationActualExtendedRawSourceEnvelope>
        ReadEnvelopeAsync(
            string family,
            string? side,
            string? grain,
            string? grainKey,
            int sourceOrdinal,
            WorkAssignmentReport report,
            string? directRunId,
            string? directGenerationId,
            string? directGenerationSha256,
            CancellationToken cancellationToken)
    {
        if (report.Status != WorkAssignmentReportStatus.Approved ||
            report.IsDeleted || !report.IsActive || !report.IsCurrent ||
            report.PayloadRevision <= 0 ||
            string.IsNullOrWhiteSpace(report.PayloadHash))
            throw Incomplete(Failure(family, "REPORT_OWNER_INVALID"));
        var documents = await context.WorkReportPayloads
            .Find(value =>
                value.ReportId == report.Id &&
                value.PayloadRevision == report.PayloadRevision &&
                value.Status == WorkReportPayloadStatus.Ready &&
                !value.IsDeleted)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (documents.Count != 1 ||
            !StringComparer.Ordinal.Equals(
                documents[0].PayloadHash, report.PayloadHash))
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .PayloadPreimageRequired,
                StatisticReconciliationActualExtendedRawSourceFields
                    .ExternalPayload);
        var document = documents[0];
        var blocks = await context.WorkReportTableValues
            .Find(value =>
                value.ReportId == report.Id &&
                value.PayloadRevision == report.PayloadRevision &&
                value.Status == WorkReportPayloadStatus.Ready &&
                !value.IsDeleted)
            .SortBy(value => value.BlockOrder)
            .ThenBy(value => value.BlockId)
            .Limit(MaxPayloadBlocks + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (blocks.Count > MaxPayloadBlocks ||
            !blocks.Select(value => value.BlockOrder)
                .SequenceEqual(Enumerable.Range(0, blocks.Count)) ||
            blocks.Select(value => value.BlockId)
                .Distinct(StringComparer.Ordinal).Count() != blocks.Count ||
            blocks.Any(value =>
                string.IsNullOrEmpty(value.BlockId) ||
                value.BlockId != value.BlockId.Trim() ||
                value.PayloadHash !=
                StatisticReconciliationActualJson.RawSha256(value.ValuesJson)))
            throw Incomplete(Failure(family, "TABLE_BLOCK_INTEGRITY_INVALID"));
        var fullHash = WorkReportPayloadHash.Compute(
            document.Values1DJson,
            document.FieldValuesJson,
            document.TableValuesRootJson,
            document.SummarySourceJson,
            blocks.Select(value => new WorkReportPayloadBlockHash(
                value.BlockId,
                value.BlockOrder,
                value.PayloadHash)));
        if (!StringComparer.Ordinal.Equals(fullHash, report.PayloadHash))
            throw Incomplete(Failure(family, "FULL_PAYLOAD_HASH_MISMATCH"));
        var snapshot = await payloadReader.LoadReportPayloadAsync(
                report, cancellationToken)
            .ConfigureAwait(false);
        if (!snapshot.IsExternalPayload || !snapshot.PayloadHashVerified ||
            snapshot.PayloadRevision != report.PayloadRevision ||
            snapshot.PayloadStatus != WorkReportPayloadStatus.Ready ||
            snapshot.PayloadHash != report.PayloadHash)
            throw Incomplete(Failure(family, "PAYLOAD_SNAPSHOT_INVALID"));
        return StatisticReconciliationActualExtendedRawSourceIntegrity.Envelope(
            family,
            side,
            grain,
            grainKey,
            sourceOrdinal,
            report.WorkId,
            report.WorkAssignmentId,
            report.Id,
            document.Id,
            report.PayloadRevision,
            report.PayloadHash!,
            CanonicalPayload(snapshot),
            report.LifecycleRevision,
            LifecycleSha(report),
            directRunId,
            directGenerationId,
            directGenerationSha256);
    }

    private static string CanonicalPayload(WorkReportPayloadSnapshot payload)
    {
        var root = new JsonObject
        {
            ["values1D"] = ParsePayloadPart(
                payload.Values1DJson, "EXTENDED_VALUES1D_JSON"),
            ["fieldValues"] = ParsePayloadPart(
                payload.FieldValuesJson, "EXTENDED_FIELD_VALUES_JSON"),
            ["tableValues"] = ParsePayloadPart(
                payload.TableValuesJson, "EXTENDED_TABLE_VALUES_JSON"),
            ["summarySource"] = ParsePayloadPart(
                payload.SummarySourceJson, "EXTENDED_SUMMARY_SOURCE_JSON")
        };
        using var document = StatisticReconciliationActualJson.ParseStrict(
            root.ToJsonString(), "EXTENDED_COMBINED_PAYLOAD_JSON");
        return StatisticReconciliationActualJson.Canonicalize(
            document.RootElement);
    }

    private static JsonNode? ParsePayloadPart(string? value, string name)
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

    private static string LifecycleSha(WorkAssignmentReport report)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_SOURCE_LIFECYCLE_V1",
            report.WorkId,
            report.WorkAssignmentId,
            report.Id,
            report.DynamicFormTemplateId,
            report.PeriodInstanceKey,
            report.PeriodKey,
            report.Status.ToString().ToUpperInvariant(),
            B(report.IsCurrent),
            B(report.IsActive),
            B(report.IsDeleted),
            I(report.PayloadRevision),
            report.PayloadHash,
            I(report.LifecycleRevision),
            report.LastLifecycleCommandId,
            report.LastLifecycleCommandHash,
            report.LastLifecycleCommandOperation,
            report.LastLifecycleCommandRevision?.ToString(),
            report.LastLifecycleCommandPayloadRevision?.ToString(),
            report.LastLifecycleCommandStatus?.ToString().ToUpperInvariant(),
            report.LastLifecycleCommandIsActive.HasValue
                ? B(report.LastLifecycleCommandIsActive.Value)
                : null,
            report.CumulativeContributionMode,
            report.CumulativeContributionPolicyJson);

    private static string AssignmentManifest(
        IReadOnlyList<WorkAssignment> assignments,
        string domain)
    {
        if (assignments.Count > MaxSourceAssignments)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .OwnerReadFailed);
        var semantics = assignments
            .OrderBy(value => value.Path, StringComparer.Ordinal)
            .ThenBy(value => value.Code, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .Select(AssignmentSha)
            .ToArray();
        return StatisticReconciliationActualCanonical.HashSequence(
            domain, semantics);
    }

    private static string AssignmentSha(WorkAssignment value)
    {
        var units = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_EXTENDED_SOURCE_ASSIGNMENT_UNITS_V1",
            value.Assignees.Select(item => item.UnitId ?? string.Empty)
                .OrderBy(item => item, StringComparer.Ordinal));
        return StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_SOURCE_ASSIGNMENT_V1",
            value.WorkId,
            value.Id,
            value.DynamicFormTemplateId,
            value.AssignmentType,
            B(value.IsActive),
            B(value.IsDeleted),
            value.ParentAssignmentId,
            value.RootAssignmentId,
            value.Path,
            value.Code,
            value.FlowInstanceId,
            value.FlowStepId,
            value.FlowBranchId,
            value.FlowEffectiveStatus,
            value.IsFlowFinalNode.HasValue
                ? B(value.IsFlowFinalNode.Value)
                : null,
            units);
    }

    private static string ScopeSha(NormalizedSummarySourceScope value)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_SOURCE_SCOPE_V1",
            value.Mode,
            value.FlowInstanceId,
            value.FlowStepId,
            value.FlowBranchId,
            value.FlowEffectiveStatus);

    private static StatisticReconciliationActualExtendedRawSourceIncompleteException
        Incomplete(string failure, params string[] fields) => new(failure, fields);

    private static string Failure(string family, string suffix)
        => $"{family}_SOURCE_{suffix}";

    private static string I(long value)
        => StatisticReconciliationActualCanonical.Integer(value);

    private static string B(bool value)
        => StatisticReconciliationActualCanonical.Boolean(value);
}
