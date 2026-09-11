using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed partial class StatisticReconciliationRunService
{
    internal static StatisticReconciliationRecheckFinalizeReceipt
        BuildRecheckFinalizeReceipt(
            StatisticReconciliationRecheckMarker marker,
            string successorVerdictGenerationId,
            string successorVerdictGenerationSha256,
            string terminalStatus,
            long reviewSupersessionExpectedStateRevision,
            DateTime promotedAtUtc,
            string? remediationSemanticSha256 = null,
            StatisticReconciliationTrustedP8ConfigurationIdentity?
                currentP8Configuration = null)
    {
        var normalizedCurrentP8 = currentP8Configuration is null
            ? null
            : Recheck
                .StatisticReconciliationTrustedP8ConfigurationIdentityCanonical
                .Normalize(currentP8Configuration);
        var receipt = new StatisticReconciliationRecheckFinalizeReceipt
        {
            SchemaVersion = normalizedCurrentP8 is null
                ? StatisticReconciliationRecheckFinalizeReceipt
                    .CurrentSchemaVersion
                : StatisticReconciliationRecheckFinalizeReceipt
                    .CurrentP8SchemaVersion,
            MarkerId = marker.MarkerId,
            BeginCommandId = marker.BeginCommandId,
            BeginRequestHash = marker.BeginRequestHash,
            BaseActualGenerationId = marker.BaseCurrentGenerationId,
            BaseActualGenerationSha256 = marker.BaseCurrentGenerationHash,
            BaseVerdictGenerationId = marker.BaseVerdictGenerationId,
            BaseVerdictGenerationSha256 = marker.BaseVerdictGenerationHash,
            SuccessorActualGenerationId = marker.SuccessorGenerationId!,
            SuccessorActualGenerationSha256 = marker.SuccessorGenerationHash!,
            SuccessorVerdictGenerationId = successorVerdictGenerationId,
            SuccessorVerdictGenerationSha256 =
                successorVerdictGenerationSha256,
            TerminalStatus = terminalStatus,
            ReviewSupersessionCommandId =
                marker.ReviewSupersessionCommandId,
            ReviewSupersessionExpectedStateRevision =
                reviewSupersessionExpectedStateRevision,
            CaptureBindingSha256 = marker.CaptureBinding.BindingSha256,
            RemediationBinding = marker.RemediationBinding,
            RemediationSemanticSha256 = remediationSemanticSha256,
            CurrentP8Configuration = normalizedCurrentP8,
            PromotedAtUtc = promotedAtUtc
        };
        receipt.ReceiptSha256 = RecheckFinalizeReceiptHash(receipt);
        return receipt;
    }

    internal static string RecheckFinalizeReceiptHash(
        StatisticReconciliationRecheckFinalizeReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (StringComparer.Ordinal.Equals(
                receipt.SchemaVersion,
                StatisticReconciliationRecheckFinalizeReceipt
                    .CurrentSchemaVersion))
        {
            return StatisticReconciliationCanonicalJson.HashObject(new
            {
                schema = StatisticReconciliationRecheckFinalizeReceipt
                    .CurrentSchemaVersion,
                receipt.MarkerId,
                receipt.BeginCommandId,
                receipt.BeginRequestHash,
                receipt.BaseActualGenerationId,
                receipt.BaseActualGenerationSha256,
                receipt.BaseVerdictGenerationId,
                receipt.BaseVerdictGenerationSha256,
                receipt.SuccessorActualGenerationId,
                receipt.SuccessorActualGenerationSha256,
                receipt.SuccessorVerdictGenerationId,
                receipt.SuccessorVerdictGenerationSha256,
                receipt.TerminalStatus,
                receipt.ReviewSupersessionCommandId,
                receipt.ReviewSupersessionExpectedStateRevision,
                receipt.CaptureBindingSha256,
                remediationBindingSha256 =
                    receipt.RemediationBinding?.BindingSha256,
                receipt.RemediationSemanticSha256,
                promotedAtUtc = FormatUtc(receipt.PromotedAtUtc)
            });
        }

        return StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = StatisticReconciliationRecheckFinalizeReceipt
                .CurrentP8SchemaVersion,
            receipt.MarkerId,
            receipt.BeginCommandId,
            receipt.BeginRequestHash,
            receipt.BaseActualGenerationId,
            receipt.BaseActualGenerationSha256,
            receipt.BaseVerdictGenerationId,
            receipt.BaseVerdictGenerationSha256,
            receipt.SuccessorActualGenerationId,
            receipt.SuccessorActualGenerationSha256,
            receipt.SuccessorVerdictGenerationId,
            receipt.SuccessorVerdictGenerationSha256,
            receipt.TerminalStatus,
            receipt.ReviewSupersessionCommandId,
            receipt.ReviewSupersessionExpectedStateRevision,
            receipt.CaptureBindingSha256,
            remediationBindingSha256 =
                receipt.RemediationBinding?.BindingSha256,
            receipt.RemediationSemanticSha256,
            currentP8ConfigurationSha256 =
                receipt.CurrentP8Configuration?.SemanticSha256,
            promotedAtUtc = FormatUtc(receipt.PromotedAtUtc)
        });
    }

    internal static bool HasValidRecheckFinalizeReceipt(
        StatisticReconciliationRun run)
    {
        var receipt = run.CurrentRecheckFinalizeReceipt;
        if (receipt is null)
            return run.CurrentGenerationRecheckCaptureBinding is null;
        return ValidReceiptSchema(receipt) &&
               StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                   receipt.MarkerId) &&
               !string.IsNullOrWhiteSpace(receipt.BeginCommandId) &&
               StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                   receipt.BeginRequestHash) &&
               StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                   receipt.BaseActualGenerationId) &&
               StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                   receipt.BaseActualGenerationSha256) &&
               StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                   receipt.BaseVerdictGenerationId) &&
               StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                   receipt.BaseVerdictGenerationSha256) &&
               StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                   receipt.SuccessorActualGenerationId) &&
               StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                   receipt.SuccessorActualGenerationSha256) &&
               StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                   receipt.SuccessorVerdictGenerationId) &&
               StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                   receipt.SuccessorVerdictGenerationSha256) &&
               Recheck.StatisticReconciliationRecheckTerminalStatuses.All
                   .Contains(receipt.TerminalStatus) &&
               StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                   receipt.ReviewSupersessionCommandId) &&
               receipt.ReviewSupersessionExpectedStateRevision >= 1 &&
               StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                   receipt.CaptureBindingSha256) &&
               ValidReceiptRemediation(receipt) &&
               receipt.PromotedAtUtc != default &&
               receipt.PromotedAtUtc.Kind == DateTimeKind.Utc &&
               StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                   receipt.ReceiptSha256) &&
               receipt.ReceiptSha256 == RecheckFinalizeReceiptHash(receipt) &&
               run.CurrentGenerationId ==
                   receipt.SuccessorActualGenerationId &&
               run.CurrentGenerationHash ==
                   receipt.SuccessorActualGenerationSha256 &&
               (run.Recheck is not null ||
                run.Status == receipt.TerminalStatus) &&
               run.CurrentGenerationRecheckCaptureBinding?.BindingSha256 ==
                   receipt.CaptureBindingSha256 &&
               run.CurrentGenerationRecheckCaptureBinding?
                   .RemediationEvidenceSha256 ==
                   receipt.RemediationBinding?.EvidenceSha256;
    }

    private static bool ValidReceiptSchema(
        StatisticReconciliationRecheckFinalizeReceipt receipt)
    {
        if (StringComparer.Ordinal.Equals(
                receipt.SchemaVersion,
                StatisticReconciliationRecheckFinalizeReceipt
                    .CurrentSchemaVersion))
            return receipt.CurrentP8Configuration is null;
        return StringComparer.Ordinal.Equals(
                   receipt.SchemaVersion,
                   StatisticReconciliationRecheckFinalizeReceipt
                       .CurrentP8SchemaVersion) &&
               Recheck
                   .StatisticReconciliationTrustedP8ConfigurationIdentityCanonical
                   .IsValid(receipt.CurrentP8Configuration);
    }

    private static bool ValidReceiptRemediation(
        StatisticReconciliationRecheckFinalizeReceipt receipt)
    {
        if (receipt.RemediationBinding is null)
            return receipt.RemediationSemanticSha256 is null;
        if (!StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                receipt.RemediationSemanticSha256))
            return false;
        try
        {
            Recheck.StatisticReconciliationTrustedRemediationEvidenceCanonical
                .RequireValid(receipt.RemediationBinding);
            return receipt.RemediationBinding.EvidenceSha256 is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
