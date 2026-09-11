using System.Globalization;
using MongoDB.Bson;
using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation.Recheck;

internal static class StatisticReconciliationRecheckCanonical
{
    internal static string BeginRequestHash(
        string reconciliationId,
        string actorUserId,
        StatisticReconciliationBeginRecheckCommand command,
        string baseTerminalStatus,
        string baseCurrentGenerationId,
        string baseCurrentGenerationHash,
        string baseVerdictGenerationId,
        string baseVerdictGenerationHash)
        => Hash(
            "P10_SAME_RUN_RECHECK_BEGIN_REQUEST_V1",
            Required(reconciliationId, "reconciliationId", 128),
            Required(actorUserId, "actorUserId", 128),
            Required(command.CommandId, "commandId", 128),
            command.ExpectedStateRevision.ToString(CultureInfo.InvariantCulture),
            Sha(command.ExpectedStateHash, "expectedStateHash"),
            RequireTerminalStatus(baseTerminalStatus),
            Sha(baseCurrentGenerationId, "baseCurrentGenerationId"),
            Sha(baseCurrentGenerationHash, "baseCurrentGenerationHash"),
            Sha(baseVerdictGenerationId, "baseVerdictGenerationId"),
            Sha(baseVerdictGenerationHash, "baseVerdictGenerationHash"));

    internal static StatisticReconciliationRecheckMarker NewMarker(
        string reconciliationId,
        string actorUserId,
        StatisticReconciliationBeginRecheckCommand command,
        string baseTerminalStatus,
        string baseCurrentGenerationId,
        string baseCurrentGenerationHash,
        string baseVerdictGenerationId,
        string baseVerdictGenerationHash,
        StatisticReconciliationRecheckCaptureBinding captureBinding,
        DateTime begunAtUtc,
        StatisticReconciliationRecheckRemediationBinding?
            remediationBinding = null)
    {
        StatisticReconciliationRecheckCaptureBindingCanonical.RequireValid(
            captureBinding);
        if (remediationBinding is not null)
            StatisticReconciliationTrustedRemediationEvidenceCanonical
                .RequireValid(remediationBinding);
        if (captureBinding.RemediationEvidenceSha256 !=
            remediationBinding?.EvidenceSha256)
            Throw(StatisticReconciliationRecheckFailureCodes.Invalid,
                "remediationCaptureBinding");
        actorUserId = Required(actorUserId, "actorUserId", 128);
        if (!ObjectId.TryParse(actorUserId, out _))
            Throw(StatisticReconciliationRecheckFailureCodes.Invalid,
                "actorUserId");
        begunAtUtc = Utc(begunAtUtc, "begunAtUtc");
        var requestHash = BeginRequestHash(reconciliationId, actorUserId,
            command, baseTerminalStatus, baseCurrentGenerationId,
            baseCurrentGenerationHash, baseVerdictGenerationId,
            baseVerdictGenerationHash);
        var marker = new StatisticReconciliationRecheckMarker
        {
            MarkerId = Hash(
                "P10_SAME_RUN_RECHECK_MARKER_ID_V1",
                reconciliationId,
                actorUserId,
                command.CommandId,
                requestHash),
            BeginActorUserId = actorUserId,
            BeginCommandId = command.CommandId.Trim(),
            BeginRequestHash = requestHash,
            BeginExpectedStateRevision = command.ExpectedStateRevision,
            BeginExpectedStateHash = command.ExpectedStateHash,
            BaseTerminalStatus = baseTerminalStatus,
            BaseCurrentGenerationId = baseCurrentGenerationId,
            BaseCurrentGenerationHash = baseCurrentGenerationHash,
            BaseVerdictGenerationId = baseVerdictGenerationId,
            BaseVerdictGenerationHash = baseVerdictGenerationHash,
            CaptureBinding = captureBinding,
            RemediationBinding = remediationBinding,
            Phase = StatisticReconciliationRecheckPhases.ReadyToClaim,
            ReviewSupersessionCommandId = Hash(
                "P10_RECHECK_REVIEW_SUPERSESSION_COMMAND_ID_V1",
                reconciliationId,
                baseVerdictGenerationId,
                requestHash),
            BegunAtUtc = begunAtUtc
        };
        marker.MarkerStateHash = MarkerHash(marker);
        RequireValidMarker(marker);
        return marker;
    }

    internal static void RefreshMarkerHash(
        StatisticReconciliationRecheckMarker marker)
        => marker.MarkerStateHash = MarkerHash(marker);

    internal static void RequireValidMarker(
        StatisticReconciliationRecheckMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        StatisticReconciliationRecheckCaptureBindingCanonical.RequireValid(
            marker.CaptureBinding);
        if (marker.RemediationBinding is not null)
            StatisticReconciliationTrustedRemediationEvidenceCanonical
                .RequireValid(marker.RemediationBinding);
        if (marker.SchemaVersion !=
                StatisticReconciliationRecheckMarker.CurrentSchemaVersion ||
            !IsSha(marker.MarkerId) ||
            !ObjectId.TryParse(marker.BeginActorUserId, out _) ||
            Required(marker.BeginCommandId, "beginCommandId", 128) !=
            marker.BeginCommandId ||
            !IsSha(marker.BeginRequestHash) ||
            marker.BeginExpectedStateRevision < 1 ||
            !IsSha(marker.BeginExpectedStateHash) ||
            !StatisticReconciliationRecheckTerminalStatuses.All.Contains(
                marker.BaseTerminalStatus) ||
            !IsSha(marker.BaseCurrentGenerationId) ||
            !IsSha(marker.BaseCurrentGenerationHash) ||
            !IsSha(marker.BaseVerdictGenerationId) ||
            !IsSha(marker.BaseVerdictGenerationHash) ||
            marker.CaptureBinding.RemediationEvidenceSha256 !=
                marker.RemediationBinding?.EvidenceSha256 ||
            !StatisticReconciliationRecheckPhases.All.Contains(marker.Phase) ||
            !IsSha(marker.ReviewSupersessionCommandId) ||
            marker.BegunAtUtc == default ||
            marker.BegunAtUtc.Kind != DateTimeKind.Utc)
        {
            Throw(StatisticReconciliationRecheckFailureCodes.Invalid,
                "markerHeader");
        }

        var successorAbsent = marker.SuccessorGenerationId is null &&
                              marker.SuccessorGenerationHash is null;
        var successorComplete = IsSha(marker.SuccessorGenerationId) &&
                                IsSha(marker.SuccessorGenerationHash);
        var verdictAbsent = marker.SuccessorVerdictGenerationId is null &&
                            marker.SuccessorVerdictGenerationHash is null;
        var verdictComplete = IsSha(marker.SuccessorVerdictGenerationId) &&
                              IsSha(marker.SuccessorVerdictGenerationHash);
        var shapeValid = marker.Phase switch
        {
            StatisticReconciliationRecheckPhases.ReadyToClaim or
            StatisticReconciliationRecheckPhases.CaptureRunning =>
                successorAbsent && verdictAbsent,
            StatisticReconciliationRecheckPhases.PendingPublished =>
                successorComplete && verdictAbsent,
            StatisticReconciliationRecheckPhases.ReviewSupersessionPending =>
                successorComplete && verdictComplete,
            _ => false
        };
        if (!shapeValid ||
            (successorComplete &&
             (Same(marker.SuccessorGenerationId,
                  marker.BaseCurrentGenerationId) ||
              Same(marker.SuccessorGenerationHash,
                  marker.BaseCurrentGenerationHash))) ||
            !Same(marker.MarkerStateHash, MarkerHash(marker)))
        {
            Throw(StatisticReconciliationRecheckFailureCodes.Invalid,
                "markerState");
        }
    }

    internal static StatisticReconciliationRecheckMarker Clone(
        StatisticReconciliationRecheckMarker marker)
        => new()
        {
            SchemaVersion = marker.SchemaVersion,
            MarkerId = marker.MarkerId,
            BeginActorUserId = marker.BeginActorUserId,
            BeginCommandId = marker.BeginCommandId,
            BeginRequestHash = marker.BeginRequestHash,
            BeginExpectedStateRevision = marker.BeginExpectedStateRevision,
            BeginExpectedStateHash = marker.BeginExpectedStateHash,
            BaseTerminalStatus = marker.BaseTerminalStatus,
            BaseCurrentGenerationId = marker.BaseCurrentGenerationId,
            BaseCurrentGenerationHash = marker.BaseCurrentGenerationHash,
            BaseVerdictGenerationId = marker.BaseVerdictGenerationId,
            BaseVerdictGenerationHash = marker.BaseVerdictGenerationHash,
            CaptureBinding = marker.CaptureBinding,
            RemediationBinding = marker.RemediationBinding,
            Phase = marker.Phase,
            SuccessorGenerationId = marker.SuccessorGenerationId,
            SuccessorGenerationHash = marker.SuccessorGenerationHash,
            SuccessorVerdictGenerationId =
                marker.SuccessorVerdictGenerationId,
            SuccessorVerdictGenerationHash =
                marker.SuccessorVerdictGenerationHash,
            ReviewSupersessionCommandId =
                marker.ReviewSupersessionCommandId,
            BegunAtUtc = marker.BegunAtUtc,
            MarkerStateHash = marker.MarkerStateHash
        };

    internal static string MarkerHash(
        StatisticReconciliationRecheckMarker marker)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_SAME_RUN_RECHECK_MARKER_STATE_V1",
            marker.SchemaVersion,
            marker.MarkerId,
            marker.BeginActorUserId,
            marker.BeginCommandId,
            marker.BeginRequestHash,
            marker.BeginExpectedStateRevision,
            marker.BeginExpectedStateHash,
            marker.BaseTerminalStatus,
            marker.BaseCurrentGenerationId,
            marker.BaseCurrentGenerationHash,
            marker.BaseVerdictGenerationId,
            marker.BaseVerdictGenerationHash,
            marker.CaptureBinding.BindingSha256,
            remediationBindingSha256 =
                marker.RemediationBinding?.BindingSha256,
            marker.Phase,
            marker.SuccessorGenerationId,
            marker.SuccessorGenerationHash,
            marker.SuccessorVerdictGenerationId,
            marker.SuccessorVerdictGenerationHash,
            marker.ReviewSupersessionCommandId,
            begunAtUtc = marker.BegunAtUtc.ToString("O",
                CultureInfo.InvariantCulture)
        });

    internal static string Hash(params string?[] values)
        => StatisticReconciliationCanonicalJson.HashText(
            string.Join("\n", values.Select(value => value ?? "~")));

    internal static string Sha(string? value, string field)
    {
        if (!IsSha(value))
            Throw(StatisticReconciliationRecheckFailureCodes.Invalid, field);
        return value!;
    }

    internal static bool IsSha(string? value)
        => StatisticReconciliationCanonicalJson.IsCanonicalSha256(value);

    internal static string Required(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength ||
            value != value.Trim() || value.Any(char.IsControl))
            Throw(StatisticReconciliationRecheckFailureCodes.Invalid, field);
        return value;
    }

    internal static string RequireTerminalStatus(string? value)
    {
        if (!StatisticReconciliationRecheckTerminalStatuses.All.Contains(
                value ?? string.Empty))
            Throw(StatisticReconciliationRecheckFailureCodes.Invalid,
                "terminalStatus");
        return value!;
    }

    internal static DateTime Utc(DateTime value, string field)
    {
        if (value == default || value.Kind != DateTimeKind.Utc)
            Throw(StatisticReconciliationRecheckFailureCodes.Invalid, field);
        return value;
    }

    internal static bool Same(string? left, string? right)
        => StringComparer.Ordinal.Equals(left, right);

    internal static void Throw(string code, string detail)
        => throw new StatisticReconciliationRecheckException(code, detail);
}
