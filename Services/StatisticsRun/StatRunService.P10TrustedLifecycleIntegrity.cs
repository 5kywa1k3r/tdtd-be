using MongoDB.Bson;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsRun;

public sealed partial class StatRunService
{
    /// <summary>
    /// Exposes the authoritative lifecycle header/state/receipt algorithms to
    /// the trusted P10 remediation owner. P10 accepts only the published P9
    /// prompt. The operations surface also serves sealed candidate-era
    /// lifecycle prompts, so its broader version-aware gate is not reused here.
    /// </summary>
    internal static void RequireP10TrustedLifecycleOperationIntegrity(
        WorkReportStatisticRebuildJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var expectedHeader = BuildLifecycleOperationsHeaderHash(job);

        if (job.RunKind !=
                WorkReportStatisticRebuildJobRunKinds
                    .LifecycleDirectProjection ||
            job.CapabilityId != StatRunCapabilities.DirectFieldTableLabel ||
            job.RouteId != StatRunRouteRegistry.LifecycleDirectProjector ||
            job.CandidateChainId !=
                StatRunCapabilityActivation.RequiredChainId ||
            job.CandidatePromptId !=
                StatRunCapabilityActivation.PublishedPromptId ||
            job.CatalogVersion !=
                StatRunCapabilityActivation.RequiredCatalogVersion ||
            job.CatalogRawSha256 !=
                StatRunCapabilityActivation.PublishedCatalogRawSha256 ||
            job.CatalogSemanticSha256 !=
                StatRunCapabilityActivation.PublishedCatalogSemanticSha256 ||
            job.SchemaRawSha256 !=
                StatRunCapabilityActivation.PublishedSchemaRawSha256 ||
            job.SchemaSemanticSha256 !=
                StatRunCapabilityActivation.PublishedSchemaSemanticSha256 ||
            job.StageLockSha256 !=
                StatRunCapabilityActivation.PublishedSealStageLockRawSha256 ||
            job.ScopeType != "WORK_PERIOD_TEMPLATE" ||
            job.ScopeKind != WorkReportStatisticRebuildJobScopeKinds.Bounded ||
            !ObjectId.TryParse(job.Id, out _) ||
            !ObjectId.TryParse(job.SourceReportId, out _) ||
            !ObjectId.TryParse(job.ActorUserId, out _) ||
            !ObjectId.TryParse(job.RequestedByUserId, out _) ||
            job.ActorUserId != job.RequestedByUserId ||
            !ObjectId.TryParse(job.TenantUnitId, out _) ||
            !ObjectId.TryParse(job.WorkId, out _) ||
            !ObjectId.TryParse(job.WorkAssignmentId, out _) ||
            !ObjectId.TryParse(job.DynamicFormFamilyId, out _) ||
            !ObjectId.TryParse(job.DynamicFormTemplateId, out _) ||
            !ObjectId.TryParse(job.ConfigId, out _) ||
            !ObjectId.TryParse(job.ConfigVersionId, out _) ||
            !StatRunCanonicalJson.IsCanonicalSha256(
                job.SourceLifecycleEventKey) ||
            job.SourcePayloadRevision is not > 0 ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.SourcePayloadHash) ||
            job.SourceLifecycleRevision is not > 0 ||
            !StatRunCanonicalJson.IsCanonicalSha256(
                job.SourceMembershipSignature) ||
            job.DirectSourceRevision is not > 0 ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.PublicationScopeKey) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.GenerationId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.GenerationHash) ||
            !StatRunCanonicalJson.IsCanonicalSha256(
                job.DynamicFormSchemaHash) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.ConfigHash) ||
            !HasCanonicalLifecycleProjectionIdentity(job) ||
            job.ScopeId != job.WorkId ||
            job.RequestHash != expectedHeader ||
            job.ImmutableHeaderHash != expectedHeader ||
            !HasOperationsStateIntegrity(job) ||
            !HasOperationReceiptIntegrity(job) ||
            job.Status != WorkReportStatisticRebuildJobStatuses.Completed ||
            job.IsActive ||
            job.NextRetryAtUtc is not null ||
            job.LeaseUntilUtc is not null ||
            job.ClaimToken is not null ||
            job.LeaseOwnerId is not null ||
            !job.IsCurrentPublication ||
            job.DirectPublicationRevision is not > 0 ||
            job.ReceiptResponseHash is not null ||
            job.ReceiptAcceptedAtUtc is not { Kind: DateTimeKind.Utc } ||
            job.PublishedAtUtc is not { Kind: DateTimeKind.Utc } ||
            job.CompletedAtUtc is not { Kind: DateTimeKind.Utc } ||
            job.ComputedAtUtc is not { Kind: DateTimeKind.Utc } ||
            job.FreshnessState !=
                WorkReportStatisticRebuildJobFreshnessStates.Fresh)
        {
            throw new InvalidOperationException(
                "P10_AUTHORIZED_P9_OPERATION_INTEGRITY_INVALID");
        }
    }

    internal static bool IsFoundationRefreshLifecycleProjection(
        WorkReportStatisticRebuildJob job)
        => string.Equals(
            job.DirectProjectionIdentityVersion,
            StatRunDirectProjectionService.FoundationRefreshIdentityVersion,
            StringComparison.Ordinal);

    private static bool HasCanonicalLifecycleProjectionIdentity(
        WorkReportStatisticRebuildJob job)
    {
        if (!ObjectId.TryParse(job.SourceReportId, out var sourceReportId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(
                job.SourceLifecycleEventKey))
        {
            return false;
        }

        if (job.DirectProjectionIdentityVersion is null &&
            job.DirectProjectionIdentityKey is null)
        {
            var sourceEventIdentity =
                $"{sourceReportId}\n{job.SourceLifecycleEventKey}";
            return string.Equals(
                       job.Id,
                       StatRunCanonicalJson.HashText(
                           $"P9_LFC_RUN\n{sourceEventIdentity}")[..24],
                       StringComparison.Ordinal) &&
                   string.Equals(
                       job.DedupeKey,
                       $"p9-lfc:{sourceReportId}:{job.SourceLifecycleEventKey}",
                       StringComparison.Ordinal) &&
                   string.Equals(
                       job.ReceiptId,
                       StatRunCanonicalJson.HashText(
                           $"P9_LIFECYCLE_DIRECT_RECEIPT_V2\n{sourceEventIdentity}"),
                       StringComparison.Ordinal);
        }

        if (!IsFoundationRefreshLifecycleProjection(job) ||
            !StatRunCanonicalJson.IsCanonicalSha256(
                job.DirectProjectionIdentityKey) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.PublicationScopeKey) ||
            !StatRunCanonicalJson.IsCanonicalSha256(
                job.SourceMembershipSignature))
        {
            return false;
        }

        try
        {
            var expected = StatRunDirectProjectionService
                .BuildFoundationRefreshCanonicalIdentity(
                    sourceReportId.ToString(),
                    job.SourceLifecycleEventKey!,
                    job.PublicationScopeKey!,
                    job.SourceMembershipSignature!);
            var expectedGeneration = StatRunDirectProjectionService
                .BuildFoundationRefreshGenerationId(
                    expected.Key,
                    job.CandidateChainId!,
                    job.CandidatePromptId!,
                    sourceReportId.ToString(),
                    job.SourceLifecycleEventKey!,
                    job.PublicationScopeKey!,
                    job.SourceMembershipSignature!);
            return string.Equals(
                       job.DirectProjectionIdentityVersion,
                       expected.Version,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       job.DirectProjectionIdentityKey,
                       expected.Key,
                       StringComparison.Ordinal) &&
                   string.Equals(job.Id, expected.RunId,
                       StringComparison.Ordinal) &&
                   string.Equals(job.DedupeKey, expected.DedupeKey,
                       StringComparison.Ordinal) &&
                   string.Equals(job.ReceiptId, expected.ReceiptId,
                       StringComparison.Ordinal) &&
                   string.Equals(job.GenerationId, expectedGeneration,
                       StringComparison.Ordinal) &&
                   job.CommandId is null &&
                   job.ReversalAudit is null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
    // Focused contract tests build a structurally valid lifecycle receipt
    // through the same production canonical functions; no parallel hash
    // implementation is permitted in the test fixture.
    internal static void RefreshP10TrustedLifecycleOperationIntegrity(
        WorkReportStatisticRebuildJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        job.RequestHash = BuildLifecycleOperationsHeaderHash(job);
        job.ImmutableHeaderHash = job.RequestHash;
        job.StateHash = BuildLifecycleOperationsStateHash(job);
    }
}