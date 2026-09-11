using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Services.StatisticsRun;

internal enum StatRunFoundationStage { Claim, Projection, Heartbeat, Completion, Retry, Cancellation }

public enum StatRunFoundationFailureDisposition
{
    Retry,
    Terminal
}

public sealed record StatRunFoundationFailure(
    string DiagnosticCode,
    StatRunFoundationFailureDisposition Disposition);

/// <summary>Internal operational evidence only. Never logs exception text, arbitrary details or a lease token.</summary>
internal static class StatRunFoundationDiagnostics
{
    private static readonly HashSet<string> KnownValidationCodes = new(StringComparer.Ordinal)
    {
        "FOUNDATION_JOB_MISSING", "FOUNDATION_JOB_IDENTITY_INVALID",
        "FOUNDATION_SOURCE_NOT_EFFECTIVE", "FOUNDATION_SOURCE_PIN_STALE",
        "FOUNDATION_LIFECYCLE_ENTRY_INVALID", "FOUNDATION_DIRECT_PROJECTION_NOT_PUBLISHED",
        "STALE_WORKER_FENCE", "SOURCE_PIN_STALE",
        "P9_DIRECT_SOURCE_REPORT_ID_INVALID", "P9_DIRECT_LIFECYCLE_EVENT_KEY_INVALID",
        "P9_DIRECT_ACTOR_USER_ID_INVALID", "P9_DIRECT_SOURCE_WORK_ID_INVALID",
        "P9_DIRECT_SOURCE_REPORT_MISSING", "P9_DIRECT_LIFECYCLE_EVENT_MISSING",
        "P9_DIRECT_LIFECYCLE_SOURCE_CONFLICT", "P9_DIRECT_SOURCE_ASSIGNMENT_MISSING",
        "P9_DIRECT_SOURCE_WORK_BINDING_INVALID", "P9_DIRECT_SOURCE_PERIOD_MISSING",
        "P9_DIRECT_SOURCE_WORK_MISSING", "P9_DIRECT_SOURCE_REVISION_INITIALIZATION_LOST",
        "P9_DIRECT_ACTOR_SCOPE_INVALID", "P9_DIRECT_APPROVAL_ACTOR_SCOPE_INVALID",
        "P9_DIRECT_ACTOR_MISSING", "P9_DIRECT_ACTOR_UNIT_INVALID",
        "P9_DIRECT_TENANT_SCOPE_INVALID", "P9_DIRECT_ASSIGNEE_UNIT_INVALID",
        "P9_DIRECT_ASSIGNEE_SCOPE_INVALID", "P9_DIRECT_CONFIG_OWNER_INVALID",
        "P9_DIRECT_CONFIG_OWNER_MISSING", "P9_DIRECT_FORM_SOURCE_PIN_INVALID",
        "P9_DIRECT_LOCKED_CONFIG_MISSING", "P9_DIRECT_LOCKED_CONFIG_INVALID",
        "P9_DIRECT_PERIOD_PIN_INVALID", "P9_DIRECT_PERIOD_SOURCE_BINDING_INVALID",
        "P9_DIRECT_MEMBER_PERIOD_MISSING", "P9_DIRECT_MEMBER_FORM_PIN_INVALID",
        "P9_DIRECT_MEMBER_APPROVAL_EVENT_AMBIGUOUS", "P9_DIRECT_MEMBER_APPROVAL_ACTOR_STALE",
        "P9_DIRECT_MEMBER_APPROVAL_ACTOR_MISSING", "P9_DIRECT_MEMBER_APPROVAL_TENANT_INVALID",
        "P9_DIRECT_MEMBER_ASSIGNEE_MISSING", "P9_DIRECT_MEMBER_ASSIGNEE_TENANT_INVALID",
        "P9_DIRECT_MEMBER_APPROVAL_ACTOR_INVALID", "P9_DIRECT_MEMBER_APPROVAL_UNIT_INVALID",
        "P9_DIRECT_MEMBER_ISSUER_UNIT_INVALID", "P9_DIRECT_MEMBER_TARGET_UNIT_INVALID",
        "P9_DIRECT_MEMBER_ASSIGNEE_UNIT_INVALID",
        "P9_DIRECT_FLOW_CONTRIBUTION_POLICY_INVALID", "P9_DIRECT_CONTRIBUTION_POLICY_INVALID",
        "P9_DIRECT_FLOW_MAPPING_LINEAGE_STALE",
        "P9_DIRECT_RUN_CLAIM_BUSY", "P9_DIRECT_RUN_IDENTITY_COLLISION",
        "P9_DIRECT_RUN_HEADER_CONFLICT", "P9_DIRECT_RUN_RECEIPT_MISSING",
        "P9_DIRECT_FOUNDATION_REFRESH_PIN_INVALID", "P9_DIRECT_FOUNDATION_REFRESH_SOURCE_PIN_STALE",
        "P9_DIRECT_FOUNDATION_REFRESH_RESOLVED_PIN_STALE",
        "P9_DIRECT_FOUNDATION_REFRESH_LIFECYCLE_LINK_INVALID",
        "P9_DIRECT_FOUNDATION_REFRESH_REVERSAL_INVALID",
        "P9_DIRECT_CANDIDATE_PIN_STALE", "P9_DIRECT_CONFIG_PIN_STALE",
        "P9_DIRECT_SOURCE_REVISION_STALE", "P9_DIRECT_MEMBERSHIP_STALE",
        "P9_DIRECT_STAGED_PIN_MISMATCH", "P9_DIRECT_STAGED_ROW_ID_MISSING",
        "P9_DIRECT_STAGED_ROW_NULL", "P9_DIRECT_PUBLICATION_SCOPE_KEY_INVALID",
        "P9_DIRECT_MEMBERSHIP_SIGNATURE_INVALID", "P9_DIRECT_STALE_PUBLICATION_FENCE",
        "P9_DIRECT_STALE_SOURCE_REVISION_FENCE", "P9_DIRECT_PUBLICATION_REVISION_INVALID",
        "P9_DIRECT_PUBLISHED_JOB_INVALID", "P9_DIRECT_CURRENT_PUBLICATION_AMBIGUOUS",
        "P9_DIRECT_CURRENT_PUBLICATION_INVALID", "P9_DIRECT_CURRENT_PUBLICATION_FAMILY_INVALID",
        "P9_DIRECT_CURRENT_PUBLICATION_DIGEST_MISMATCH",
        "P9_DIRECT_CURRENT_PUBLICATION_GENERATION_HASH_MISMATCH",
        "P9_DIRECT_CURRENT_PUBLICATION_LEDGER_SHAPE_INVALID",
        "P9_DIRECT_LIFECYCLE_LINK_STATE_CONFLICT", "P9_DIRECT_LIFECYCLE_LINK_TERMINAL_CONFLICT",
        "P9_DIRECT_LIFECYCLE_LINK_LOST", "P9_DIRECT_LIFECYCLE_LINK_REPORT_MISSING",
        "P9_DIRECT_LIFECYCLE_LINK_EVENT_AMBIGUOUS", "P9_DIRECT_LIFECYCLE_LINK_EVENT_INVALID",
        "P9_DIRECT_LIFECYCLE_TIME_PIN_INVALID", "P9_DIRECT_STALE_RUN_TERMINALIZATION_LOST",
        "P9_DIRECT_REVERSAL_PRIOR_PUBLICATION_MISSING", "P9_DIRECT_REVERSAL_PRIOR_PUBLICATION_STALE",
        "P9_DIRECT_CONTRIBUTION_TARGET_AMBIGUOUS", "P9_DIRECT_FLOW_MAPPING_LINEAGE_MISSING",
        "P9_DIRECT_FLOW_MAPPING_RECEIPT_MISSING", "P9_DIRECT_FLOW_MAPPING_PROVENANCE_MISSING",
        "P9_DIRECT_FLOW_CONTRIBUTION_REPLAY_MISMATCH", "P9_DIRECT_PERSISTED_FLOW_CONTRIBUTION_INVALID",
        "P9_DIRECT_PERSISTED_REVERSAL_AUDIT_INVALID", "P9_DIRECT_COMPLETED_PUBLICATION_AMBIGUOUS",
        "P9_DIRECT_COMPLETED_PUBLICATION_INVALID", "P9_DIRECT_COMPLETED_PUBLICATION_PIN_MISMATCH",
        "P9_DIRECT_COMPLETED_RUN_INVALID", "P9_DIRECT_COMPLETED_DIGEST_SET_INVALID",
        "P9_DIRECT_COMPLETED_DIGEST_REPLAY_MISMATCH", "P9_DIRECT_COMPLETED_GENERATION_HASH_MISMATCH",
        "P9_DIRECT_CONTRIBUTION_MODE_INVALID",
        WorkReportDirectGenerationValidationException.RowConflictCode,
        WorkReportDirectGenerationValidationException.SourceDriftCode,
        WorkReportDirectGenerationValidationException.PinConflictCode,
        WorkReportDirectGenerationValidationException.PolicyDriftCode,
        WorkReportDirectGenerationValidationException.PayloadNotReadyCode
    };

    // These validation-shaped outcomes can become valid after a competing owner
    // releases its fence or finishes the lifecycle link. Every other allowlisted
    // validation outcome is deterministic for the claimed immutable input.
    private static readonly HashSet<string> RetryableValidationCodes = new(StringComparer.Ordinal)
    {
        "STALE_WORKER_FENCE",
        "P9_DIRECT_RUN_CLAIM_BUSY",
        "P9_DIRECT_STALE_PUBLICATION_FENCE",
        "P9_DIRECT_LIFECYCLE_LINK_LOST",
        "P9_DIRECT_STALE_RUN_TERMINALIZATION_LOST"
    };

    private static readonly HashSet<string> RetryableFoundationCodes = new(StringComparer.Ordinal)
    {
        StatRunFoundationWorker.ProjectionFailure,
        StatRunFoundationWorker.HeartbeatFailure,
        StatRunFoundationWorker.CancellationFailure
    };

    // Only the sealed producer-owned details type can contribute a reason. Never inspect
    // arbitrary Details through reflection, serialization, interfaces, or user-defined getters.
    private static readonly HashSet<string> KnownJobConflictReasons = new(StringComparer.Ordinal)
    {
        "CLAIM_CONTENTION", "STALE_WORKER_FENCE", "JOB_DEADLINE_INVALID", "JOB_DEADLINE_EXPIRED",
        "DIRECT_PROJECTION_RECEIPT_INVALID", "FOUNDATION_DIAGNOSTIC_INVALID", "FOUNDATION_CAPABILITY_NOT_OWNED",
        "JOB_ACTIVATION_PIN_MISMATCH", "JOB_NOT_AVAILABLE", "IMMUTABLE_HEADER_INTEGRITY_INVALID",
        "RECEIPT_INTEGRITY_INVALID", "STATE_INTEGRITY_INVALID", "RESET_RECEIPT_HISTORY_INTEGRITY_INVALID"
    };

    private static readonly HashSet<string> RetryableJobConflictReasons = new(StringComparer.Ordinal)
    {
        "CLAIM_CONTENTION", "STALE_WORKER_FENCE", "JOB_DEADLINE_EXPIRED", "JOB_NOT_AVAILABLE"
    };

    internal static (string Category, string Code) Describe(Exception error)
    {
        if (error is AppException app)
        {
            if (app.Code ==
                AppErrorCode.WORK_ASSIGNMENT_REPORT_PAYLOAD_NOT_READY)
            {
                return (
                    "VALIDATION",
                    WorkReportDirectGenerationValidationException
                        .PayloadNotReadyCode);
            }
            if (app.Code == AppErrorCode.STAT_RUN_JOB_CONFLICT &&
                app.Details is StatRunJobConflictDetails conflict && KnownJobConflictReasons.Contains(conflict.Reason))
                return ("DOMAIN", conflict.Reason);
            return ("DOMAIN", System.Enum.IsDefined(app.Code) ? app.Code.ToString() : "UNKNOWN_DOMAIN_ERROR");
        }
        if (error.GetType() ==
            typeof(WorkReportDirectGenerationValidationException))
        {
            var validation =
                (WorkReportDirectGenerationValidationException)error;
            return (
                "VALIDATION",
                KnownValidationCodes.Contains(validation.DiagnosticCode)
                    ? validation.DiagnosticCode
                    : "UNLISTED_VALIDATION_ERROR");
        }
        // Exact type avoids invoking a user-defined Message override.
        if (error.GetType() == typeof(InvalidOperationException))
            return ("VALIDATION", KnownValidationCodes.Contains(error.Message)
                ? error.Message : "UNLISTED_VALIDATION_ERROR");
        return error switch
        {
            OperationCanceledException => ("CANCELLATION", "OPERATION_CANCELLED"),
            TimeoutException => ("TIMEOUT", "DEPENDENCY_TIMEOUT"),
            MongoException => ("DATABASE", "DATABASE_OPERATION_FAILED"),
            _ => ("UNEXPECTED", "UNEXPECTED_WORKER_FAILURE")
        };
    }

    internal static StatRunFoundationFailure ClassifyProjectionFailure(Exception error)
    {
        var (_, code) = Describe(error);
        var trustedValidation =
            error.GetType() == typeof(InvalidOperationException) ||
            error.GetType() ==
                typeof(WorkReportDirectGenerationValidationException) ||
            error is AppException
            {
                Code: AppErrorCode.WORK_ASSIGNMENT_REPORT_PAYLOAD_NOT_READY
            };
        if (trustedValidation && KnownValidationCodes.Contains(code))
        {
            return new StatRunFoundationFailure(
                code,
                RetryableValidationCodes.Contains(code)
                    ? StatRunFoundationFailureDisposition.Retry
                    : StatRunFoundationFailureDisposition.Terminal);
        }
        if (error is AppException app &&
            app.Code == AppErrorCode.STAT_RUN_JOB_CONFLICT &&
            app.Details is StatRunJobConflictDetails conflict &&
            KnownJobConflictReasons.Contains(conflict.Reason))
        {
            return new StatRunFoundationFailure(
                conflict.Reason,
                RetryableJobConflictReasons.Contains(conflict.Reason)
                    ? StatRunFoundationFailureDisposition.Retry
                    : StatRunFoundationFailureDisposition.Terminal);
        }

        // Dependency, cancellation and unknown failures keep the bounded retry
        // policy. Their raw type/message is never persisted as a diagnostic.
        return new StatRunFoundationFailure(
            StatRunFoundationWorker.ProjectionFailure,
            StatRunFoundationFailureDisposition.Retry);
    }

    internal static bool IsAcceptedFailure(StatRunFoundationFailure? failure)
    {
        if (failure is null || string.IsNullOrWhiteSpace(failure.DiagnosticCode))
            return false;
        if (RetryableFoundationCodes.Contains(failure.DiagnosticCode))
            return failure.Disposition == StatRunFoundationFailureDisposition.Retry;
        if (KnownValidationCodes.Contains(failure.DiagnosticCode))
        {
            return failure.Disposition == (RetryableValidationCodes.Contains(failure.DiagnosticCode)
                ? StatRunFoundationFailureDisposition.Retry
                : StatRunFoundationFailureDisposition.Terminal);
        }
        if (!KnownJobConflictReasons.Contains(failure.DiagnosticCode))
            return false;
        return failure.Disposition == (RetryableJobConflictReasons.Contains(failure.DiagnosticCode)
            ? StatRunFoundationFailureDisposition.Retry
            : StatRunFoundationFailureDisposition.Terminal);
    }

    internal static void LogFailure(ILogger logger, StatRunFoundationStage stage,
        Exception error, StatRunWorkerLeaseResponse? lease, bool secondary = false)
    {
        var (category, code) = Describe(error);
        var job = lease?.Job;
        var jobId = ObjectId.TryParse(job?.JobId, out var parsed) ? parsed.ToString() : "UNAVAILABLE";
        logger.LogWarning(new EventId(9104, "P9FoundationFailure"),
            "P9 Foundation failure stage={Stage} category={Category} code={Code} jobId={JobId} stateRevision={StateRevision} retryCount={RetryCount} secondary={Secondary}",
            stage, category, code, jobId, job?.StateRevision, job?.RetryCount, secondary);
    }
}
