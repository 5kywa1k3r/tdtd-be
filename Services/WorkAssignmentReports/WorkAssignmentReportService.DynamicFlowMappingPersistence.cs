using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

namespace tdtd_be.Services.WorkAssignmentReports;

public sealed partial class WorkAssignmentReportService
{
    private static DynamicFlowMappingApplyCommand ResolveDynamicFlowMappingApplyCommand(
        WorkAssignmentReport report,
        DynamicFlowMappingRequest request,
        string actorUserId)
    {
        var commandId = ResolveDynamicFlowMappingCommandId(report, request);
        if (request.ExpectedPayloadRevision is not >= 0 ||
            request.ExpectedLifecycleRevision is not >= 0 ||
            !IsLowerSha256(request.ExpectedPayloadHash))
        {
            throw DynamicFlowMappingTargetRevisionConflict(
                report,
                request.ExpectedPayloadRevision ?? -1,
                request.ExpectedLifecycleRevision ?? -1,
                request.ExpectedPayloadHash,
                "DYNAMIC_FLOW_MAPPING_EXPECTED_TARGET_REQUIRED");
        }

        var sourceSignature = request.SourceSignature?.Trim();
        var resultSemanticHash = request.ResultSemanticHash?.Trim();
        if (!IsLowerSha256(sourceSignature) ||
            !IsLowerSha256(resultSemanticHash))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT,
                new
                {
                    reportId = report.Id,
                    sourceSignature,
                    resultSemanticHash,
                    reason = "DYNAMIC_FLOW_MAPPING_PREVIEW_RESULT_BINDING_REQUIRED"
                });
        }

        var previewToken = request.PreviewToken?.Trim();
        if (string.IsNullOrWhiteSpace(previewToken))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_INVALID,
                new
                {
                    reportId = report.Id,
                    reason = "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_REQUIRED"
                });
        }

        request.CommandId = commandId;
        request.ExpectedPayloadHash = request.ExpectedPayloadHash!.Trim();
        request.SourceSignature = sourceSignature;
        request.ResultSemanticHash = resultSemanticHash;
        request.PreviewToken = previewToken;

        var requestHash = ComputeDynamicFlowMappingApplyRequestHash(
            report,
            request,
            actorUserId,
            commandId);
        var previewTokenHash = ComputeDynamicFlowMappingTextSha256(previewToken);
        return new DynamicFlowMappingApplyCommand(
            commandId,
            request.ExpectedPayloadRevision.Value,
            request.ExpectedPayloadHash,
            request.ExpectedLifecycleRevision.Value,
            sourceSignature!,
            resultSemanticHash!,
            requestHash,
            previewTokenHash);
    }

    private static string ResolveDynamicFlowMappingCommandId(
        WorkAssignmentReport report,
        DynamicFlowMappingRequest request)
    {
        var commandId = request.CommandId?.Trim();
        if (string.IsNullOrWhiteSpace(commandId) ||
            !PayloadCommandIdRegex.IsMatch(commandId))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FLOW_MAPPING_COMMAND_ID_REQUIRED,
                new
                {
                    reportId = report.Id,
                    commandId = request.CommandId,
                    minLength = 8,
                    maxLength = 128
                });
        }

        return commandId;
    }

    private static string ComputeDynamicFlowMappingApplyRequestHash(
        WorkAssignmentReport report,
        DynamicFlowMappingRequest request,
        string actorUserId,
        string commandId)
    {
        var originalCommandId = request.CommandId;
        var originalExpectedPayloadHash = request.ExpectedPayloadHash;
        var originalSourceSignature = request.SourceSignature;
        var originalResultSemanticHash = request.ResultSemanticHash;
        var originalPreviewToken = request.PreviewToken;
        try
        {
            request.CommandId = commandId;
            request.ExpectedPayloadHash = request.ExpectedPayloadHash?.Trim();
            request.SourceSignature = request.SourceSignature?.Trim();
            request.ResultSemanticHash = request.ResultSemanticHash?.Trim();
            request.PreviewToken = request.PreviewToken?.Trim();
            return DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(
                JsonSerializer.Serialize(
                    new
                    {
                        operation = "APPLY_DYNAMIC_FLOW_MAPPING",
                        targetReportId = report.Id,
                        actorUserId,
                        request
                    },
                    _jsonOptions));
        }
        finally
        {
            request.CommandId = originalCommandId;
            request.ExpectedPayloadHash = originalExpectedPayloadHash;
            request.SourceSignature = originalSourceSignature;
            request.ResultSemanticHash = originalResultSemanticHash;
            request.PreviewToken = originalPreviewToken;
        }
    }

    private async Task<DynamicFlowMappingApplyReceipt?> LoadDynamicFlowMappingReceiptAsync(
        string targetReportId,
        string commandId,
        CancellationToken ct)
        => (DynamicFlowMappingApplyReceipt?)await _ctx
            .DynamicFlowMappingApplyReceipts
            .Find(receipt =>
                receipt.TargetReportId == targetReportId &&
                receipt.CommandId == commandId)
            .FirstOrDefaultAsync(ct);

    private static void ValidateDynamicFlowMappingReceiptReplayRequest(
        DynamicFlowMappingApplyReceipt receipt,
        string commandId,
        string requestHash,
        string actorUserId)
    {
        if (string.Equals(receipt.CommandId, commandId, StringComparison.Ordinal) &&
            string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal) &&
            string.Equals(receipt.ActorUserId, actorUserId, StringComparison.Ordinal))
        {
            return;
        }

        throw DynamicFlowMappingCommandReplayConflict(receipt, commandId);
    }

    private static void ValidateDynamicFlowMappingReceiptReplay(
        DynamicFlowMappingApplyReceipt receipt,
        DynamicFlowMappingApplyCommand command,
        string actorUserId)
    {
        var exact =
            string.Equals(receipt.CommandId, command.CommandId, StringComparison.Ordinal) &&
            string.Equals(receipt.RequestHash, command.RequestHash, StringComparison.Ordinal) &&
            string.Equals(receipt.ActorUserId, actorUserId, StringComparison.Ordinal) &&
            receipt.ExpectedTargetPayloadRevision == command.ExpectedPayloadRevision &&
            string.Equals(
                receipt.ExpectedTargetPayloadHash,
                command.ExpectedPayloadHash,
                StringComparison.Ordinal) &&
            receipt.ExpectedTargetLifecycleRevision == command.ExpectedLifecycleRevision &&
            string.Equals(
                receipt.SourceSignature,
                command.SourceSignature,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ResultSemanticHash,
                command.ResultSemanticHash,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.PreviewTokenHash,
                command.PreviewTokenHash,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ResultSnapshotHash,
                ComputeDynamicFlowMappingDocumentHash(receipt.ResultSnapshot),
                StringComparison.Ordinal);
        if (exact)
            return;

        throw DynamicFlowMappingCommandReplayConflict(
            receipt,
            command.CommandId);
    }

    private static AppException DynamicFlowMappingCommandReplayConflict(
        DynamicFlowMappingApplyReceipt receipt,
        string commandId)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_MAPPING_COMMAND_REPLAY_MISMATCH,
            new
            {
                reportId = receipt.TargetReportId,
                commandId,
                receiptId = receipt.Id,
                reason = "DYNAMIC_FLOW_MAPPING_COMMAND_RECEIPT_BINDING_MISMATCH"
            });

    private async Task<WorkAssignmentReportResponse>
        MapDynamicFlowMappingReceiptReplayResponseAsync(
            DynamicFlowMappingApplyReceipt receipt,
            string actorUserId,
            CancellationToken ct)
    {
        var report = await _ctx.WorkAssignmentReports
            .Find(item => item.Id == receipt.TargetReportId && !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (report is null)
            throw ReportNotFound(receipt.TargetReportId);
        if (!string.Equals(
                report.DynamicFlowMappingReceiptId,
                receipt.Id,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.DynamicFlowMappingProvenanceId,
                receipt.ProvenanceId,
                StringComparison.Ordinal))
        {
            throw DynamicFlowMappingProvenanceTampered(
                report,
                "DYNAMIC_FLOW_MAPPING_RECEIPT_HEADER_REFERENCE_MISMATCH");
        }

        await EnsureDynamicFlowMappingProvenanceIntegrityAsync(report, ct);
        var period = await _ctx.WorkReportPeriods
            .Find(item => item.Id == report.WorkReportPeriodId && !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        var response = await MapToResponseAsync(
            report,
            period,
            actorUserId,
            ct);
        return BindDynamicFlowMappingReceiptReplayMetadata(
            response,
            receipt);
    }

    /// <summary>
    /// An exact command replay still uses the current report to enforce the
    /// caller's current ACL and payload redaction. Only the immutable
    /// command-result envelope is restored from the receipt. Raw field/table
    /// values and source facts are deliberately not persisted in, or copied
    /// from, the receipt.
    /// </summary>
    internal static WorkAssignmentReportResponse
        BindDynamicFlowMappingReceiptReplayMetadata(
            WorkAssignmentReportResponse response,
            DynamicFlowMappingApplyReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(receipt);

        var applyStateValid =
            response.DynamicFlowMappingApplyState is
                DynamicFlowMappingApplyStates.Committed or
                DynamicFlowMappingApplyStates.Partial or
                DynamicFlowMappingApplyStates.Retrying or
                DynamicFlowMappingApplyStates.Reconciled;
        if (!string.Equals(
                response.Id,
                receipt.TargetReportId,
                StringComparison.Ordinal) ||
            !string.Equals(
                response.DynamicFlowMappingReceiptId,
                receipt.Id,
                StringComparison.Ordinal) ||
            !string.Equals(
                response.DynamicFlowMappingCommandId,
                receipt.CommandId,
                StringComparison.Ordinal) ||
            !string.Equals(
                response.DynamicFlowMappingResultSemanticHash,
                receipt.ResultSemanticHash,
                StringComparison.Ordinal) ||
            !applyStateValid)
        {
            throw DynamicFlowMappingCommandReplayConflict(
                receipt,
                receipt.CommandId);
        }

        response.PayloadRevision = receipt.ResultPayloadRevision;
        response.PayloadHash = receipt.ResultPayloadHash;
        response.LifecycleRevision = receipt.ResultLifecycleRevision;
        response.DynamicFlowMappingReceiptId = receipt.Id;
        response.DynamicFlowMappingCommandId = receipt.CommandId;
        response.DynamicFlowMappingResultSemanticHash =
            receipt.ResultSemanticHash;
        return response;
    }

    private static void EnsureDynamicFlowMappingExpectedTarget(
        WorkAssignmentReport report,
        DynamicFlowMappingApplyCommand command,
        WorkAssignment assignment)
    {
        if (report.PayloadRevision != command.ExpectedPayloadRevision ||
            !string.Equals(
                report.PayloadHash,
                command.ExpectedPayloadHash,
                StringComparison.Ordinal) ||
            report.LifecycleRevision != command.ExpectedLifecycleRevision)
        {
            throw DynamicFlowMappingTargetRevisionConflict(
                report,
                command,
                "DYNAMIC_FLOW_MAPPING_EXPECTED_TARGET_STALE");
        }

        if (!report.IsActive ||
            !report.IsCurrent ||
            report.Status != WorkAssignmentReportStatus.Draft ||
            !assignment.IsActive ||
            !string.Equals(
                assignment.FlowEffectiveStatus,
                DynamicFlowEffectiveStatuses.Effective,
                StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(report.PayloadMutationCommandId))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_MAPPING_LIFECYCLE_CONFLICT,
                new
                {
                    reportId = report.Id,
                    report.Status,
                    report.IsActive,
                    report.IsCurrent,
                    report.LifecycleRevision,
                    assignment.FlowEffectiveStatus,
                    reason = "DYNAMIC_FLOW_MAPPING_TARGET_NOT_WRITABLE"
                });
        }
    }

    private static void EnsureDynamicFlowMappingPreviewParity(
        WorkAssignmentReport report,
        WorkAssignment assignment,
        DynamicFlowMappingApplyCommand command,
        DynamicFlowMappingPreviewResponse projection)
    {
        if (!string.Equals(
                projection.TargetReportId,
                report.Id,
                StringComparison.Ordinal) ||
            !string.Equals(
                projection.TargetAssignmentId,
                assignment.Id,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT,
                new
                {
                    reportId = report.Id,
                    workAssignmentId = assignment.Id,
                    reason = "DYNAMIC_FLOW_MAPPING_PREVIEW_TARGET_CHANGED"
                });
        }

        if (projection.TargetPayloadRevision != command.ExpectedPayloadRevision ||
            !string.Equals(
                projection.TargetPayloadHash,
                command.ExpectedPayloadHash,
                StringComparison.Ordinal) ||
            projection.TargetLifecycleRevision != command.ExpectedLifecycleRevision)
        {
            throw DynamicFlowMappingTargetRevisionConflict(
                report,
                command,
                "DYNAMIC_FLOW_MAPPING_EXPECTED_TARGET_STALE");
        }

        if (!string.Equals(
                projection.SourceSignature,
                command.SourceSignature,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_MAPPING_SOURCE_SIGNATURE_CONFLICT,
                new
                {
                    reportId = report.Id,
                    reason =
                        "DYNAMIC_FLOW_MAPPING_SOURCE_SIGNATURE_CHANGED"
                });
        }

        if (!string.Equals(
                projection.ResultSemanticHash,
                command.ResultSemanticHash,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT,
                new
                {
                    reportId = report.Id,
                    reason = "DYNAMIC_FLOW_MAPPING_PREVIEW_RESULT_CHANGED"
                });
        }
    }

    private static AppException DynamicFlowMappingPreviewTokenFailure(
        WorkAssignmentReport report,
        DynamicFlowMappingSecurityException error)
    {
        var code = error.Reason switch
        {
            DynamicFlowMappingSecurityContract.PreviewTokenKeyUnavailableReason =>
                AppErrorCode.DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_KEY_UNAVAILABLE,
            DynamicFlowMappingSecurityContract.PreviewTokenExpiredReason =>
                AppErrorCode.DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_EXPIRED,
            DynamicFlowMappingSecurityContract.PreviewTokenActorMismatchReason or
                DynamicFlowMappingSecurityContract.PreviewTokenTargetMismatchReason or
                DynamicFlowMappingSecurityContract.PreviewTokenEpochMismatchReason or
                DynamicFlowMappingSecurityContract.PreviewTokenSnapshotMismatchReason or
                DynamicFlowMappingSecurityContract.PreviewTokenNotYetValidReason =>
                AppErrorCode.DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT,
            _ => AppErrorCode.DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_INVALID
        };
        var publicReason = code switch
        {
            AppErrorCode.DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_KEY_UNAVAILABLE =>
                DynamicFlowMappingSecurityContract
                    .PreviewTokenKeyUnavailableReason,
            AppErrorCode.DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_EXPIRED =>
                DynamicFlowMappingSecurityContract
                    .PreviewTokenExpiredReason,
            AppErrorCode.DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT =>
                "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT",
            _ => DynamicFlowMappingSecurityContract
                .PreviewTokenInvalidReason
        };
        return new AppException(
            code,
            new
            {
                reportId = report.Id,
                reason = publicReason
            },
            innerException: error);
    }

    private static AppException DynamicFlowMappingTargetRevisionConflict(
        WorkAssignmentReport report,
        DynamicFlowMappingApplyCommand command,
        string reason)
        => DynamicFlowMappingTargetRevisionConflict(
            report,
            command.ExpectedPayloadRevision,
            command.ExpectedLifecycleRevision,
            command.ExpectedPayloadHash,
            reason);

    private static AppException DynamicFlowMappingTargetRevisionConflict(
        WorkAssignmentReport report,
        int expectedPayloadRevision,
        int expectedLifecycleRevision,
        string? expectedPayloadHash,
        string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_MAPPING_TARGET_REVISION_CONFLICT,
            new
            {
                reportId = report.Id,
                expectedPayloadRevision,
                expectedPayloadHash,
                expectedLifecycleRevision,
                currentPayloadRevision = report.PayloadRevision,
                currentPayloadHash = report.PayloadHash,
                currentLifecycleRevision = report.LifecycleRevision,
                report.Status,
                report.IsActive,
                report.IsCurrent,
                reason
            });

    private static string ComputeDynamicFlowMappingTextSha256(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string ComputeDynamicFlowMappingDocumentHash(
        BsonDocument document)
        => DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(
            document.ToJson(
                new JsonWriterSettings
                {
                    OutputMode = JsonOutputMode.RelaxedExtendedJson
                }));

    private static string ComputeDynamicFlowMappingObjectHash(object value)
        => DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(
            JsonSerializer.Serialize(value, _jsonOptions));

    private static BsonDocument ToDynamicFlowMappingSnapshot(object value)
        => value.ToBsonDocument(value.GetType());

    private static string ComputeDynamicFlowMappingIntentHash(
        DynamicFlowMappingReconcileIntent intent)
        => ComputeDynamicFlowMappingDocumentHash(intent.ToBsonDocument());

    private static string ComputeDynamicFlowMappingOptionalJsonHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ComputeDynamicFlowMappingTextSha256(string.Empty);
        try
        {
            return DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(value);
        }
        catch (DynamicFlowMappingSecurityException)
        {
            return ComputeDynamicFlowMappingTextSha256(value);
        }
    }

    private static string ComputeDynamicFlowMappingAuthorizationHash(
        DynamicFlowPolicyEvaluationResult permissions)
        => ComputeDynamicFlowMappingObjectHash(
            new
            {
                permissions.DenyAllFields,
                permissions.DenyAllTableColumns,
                fields = permissions.Fields
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new
                    {
                        key = item.Key,
                        item.Value.TargetKey,
                        item.Value.FieldId,
                        item.Value.FieldKey,
                        item.Value.Read,
                        item.Value.Write,
                        item.Value.Required,
                        item.Value.Hidden,
                        item.Value.Locked,
                        item.Value.LockedAfterSubmit,
                        item.Value.SourcePolicyId
                    })
                    .ToList(),
                tableColumns = permissions.TableColumns
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new
                    {
                        key = item.Key,
                        item.Value.TargetKey,
                        item.Value.BlockId,
                        item.Value.ColumnKey,
                        item.Value.Read,
                        item.Value.Write,
                        item.Value.Required,
                        item.Value.Hidden,
                        item.Value.Locked,
                        item.Value.LockedAfterSubmit,
                        item.Value.SourcePolicyId
                    })
                    .ToList()
            });

    private static string ComputeDynamicFlowMappingAuthorizationScopeHash(
        WorkAssignment assignment,
        WorkAssignmentReport report,
        DynamicFlowMappingRuntimeContext runtime,
        DynamicFlowPolicyEvaluationResult permissions,
        string actorUserId)
    {
        var actorRole = ResolveDirectDynamicFlowActorRole(
            assignment,
            report,
            actorUserId);
        if (actorRole is null)
        {
            throw DynamicFlowMappingPolicyDenied(
                report,
                assignment,
                "DYNAMIC_FLOW_MAPPING_AUTHORIZATION_SCOPE_UNRESOLVED");
        }

        return ComputeDynamicFlowMappingObjectHash(
            new
            {
                scopeVersion = "P7-MAP-AUTH-1",
                actorUserId,
                actorRole,
                capability = "APPLY_DYNAMIC_FLOW_MAPPING",
                targetReportId = report.Id,
                targetAssignmentId = assignment.Id,
                targetWorkId = assignment.WorkId,
                flowInstanceId = runtime.FlowInstance.Id,
                executionEpoch = runtime.TargetStep.ExecutionEpoch,
                targetStepInstanceId = runtime.TargetStep.Id,
                targetStepId = runtime.TargetStep.FlowStepId,
                targetBranchId = runtime.TargetStep.BranchId,
                targetAttemptNo = runtime.TargetStep.AttemptNo,
                targetFormFamilyId = runtime.TargetStep.FormFamilyId,
                targetFormVersionId = runtime.TargetStep.FormVersionId,
                targetFormVersionNo = runtime.TargetStep.FormVersionNo,
                targetFormSchemaHash = runtime.TargetStep.FormSchemaHash,
                mappingRuleSetHash = runtime.RuleSetHash,
                authorizationSnapshotHash =
                    ComputeDynamicFlowMappingAuthorizationHash(permissions)
            });
    }

    private static DynamicFlowMappingPersistenceBundle
        BuildDynamicFlowMappingPersistenceBundle(
            WorkAssignmentReport report,
            WorkAssignment assignment,
            WorkReportPeriod? period,
            DynamicFlowMappingProjectionResult projectionResult,
            DynamicFlowMappingApplyCommand command,
            DynamicFlowMappingSuccessorPlan successorPlan,
            DynamicFlowMappingPreviewTokenClaims previewClaims,
            WorkReportPayloadWriteResult payloadResult,
            AggregateSourceSnapshot sourceSnapshot,
            DateTime? startedDate,
            DateTime? completedDate,
            bool isHistoricalData,
            DateTime? effectiveDueAtUtc,
            WorkAssignmentReportStatus fromStatus,
            WorkReportPeriodStatus? periodStatus,
            string actorUserId,
            DateTime now)
    {
        var receiptId = ObjectId.GenerateNewId().ToString();
        var provenanceId = successorPlan.SuccessorProvenanceId;
        var eventId = ObjectId.GenerateNewId().ToString();
        var outboxId = ObjectId.GenerateNewId().ToString();
        var runtimePin = BuildDynamicFlowMappingRuntimePin(
            projectionResult.Runtime);
        var sourcePins = projectionResult.Sources
            .OrderBy(source => source.Report.Id, StringComparer.Ordinal)
            .Select(BuildDynamicFlowMappingSourcePin)
            .ToList();
        var ownedTargetRefs = projectionResult.Projection.Changes
            .Where(change => !string.IsNullOrWhiteSpace(change.TargetKey))
            .Select(change =>
                $"{change.TargetKind.Trim().ToUpperInvariant()}:{change.TargetKey.Trim()}")
            .Append("REPORT_PAYLOAD_ROOT")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var authorizationSnapshotHash =
            ComputeDynamicFlowMappingAuthorizationScopeHash(
                assignment,
                report,
                projectionResult.Runtime,
                projectionResult.Permissions,
                actorUserId);
        if (!string.Equals(
                previewClaims.AuthorizationScopeHash,
                authorizationSnapshotHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_MAPPING_AUTHORIZATION_SCOPE_TOKEN_MISMATCH");
        }
        var resultSnapshot = ToDynamicFlowMappingSnapshot(
            new
            {
                schemaVersion = "P7-MAP-RESULT-1",
                receiptId,
                provenanceId,
                targetReportId = report.Id,
                targetAssignmentId = assignment.Id,
                supersedesProvenanceId =
                    successorPlan.PredecessorProvenanceId,
                resultPayloadRevision = payloadResult.PayloadRevision,
                resultPayloadHash = payloadResult.PayloadHash,
                resultLifecycleRevision = report.LifecycleRevision,
                sourceSignatureVersion =
                    DynamicFlowMappingSecurityContract.SourceSignatureVersion,
                sourceSignature = command.SourceSignature,
                resultSemanticHash = command.ResultSemanticHash,
                summarySourceHash =
                    ComputeDynamicFlowMappingOptionalJsonHash(
                        projectionResult.Projection.SummarySourceJson),
                fieldValuesHash =
                    ComputeDynamicFlowMappingOptionalJsonHash(
                        projectionResult.Projection.FieldValuesJson),
                tableValuesHash =
                    ComputeDynamicFlowMappingOptionalJsonHash(
                        projectionResult.Projection.TableValuesJson),
                contributionPolicyHash =
                    ComputeDynamicFlowMappingOptionalJsonHash(
                        projectionResult.Projection
                            .CumulativeContributionPolicyJson),
                projectionResult.Projection.DataOrigin,
                projectionResult.Projection.CumulativeContributionMode,
                sourceReportIds = sourceSnapshot.ReportIds
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList(),
                sourceAssignmentIds = sourceSnapshot.AssignmentIds
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList(),
                ownedTargetRefs
            });
        var resultSnapshotHash =
            ComputeDynamicFlowMappingDocumentHash(resultSnapshot);
        var provenanceSnapshot = ToDynamicFlowMappingSnapshot(
            new
            {
                schemaVersion = "P7-MAP-PROVENANCE-1",
                receiptId,
                provenanceId,
                commandId = command.CommandId,
                requestHash = command.RequestHash,
                targetReportId = report.Id,
                targetAssignmentId = assignment.Id,
                supersedesProvenanceId =
                    successorPlan.PredecessorProvenanceId,
                runtimePin,
                sourcePins,
                sourceSignatureVersion =
                    DynamicFlowMappingSecurityContract.SourceSignatureVersion,
                sourceSignature = command.SourceSignature,
                resultSemanticHash = command.ResultSemanticHash,
                authorizationSnapshotHash,
                resultSnapshotHash,
                resultSnapshot,
                ownedTargetRefs
            });
        var provenanceHash =
            ComputeDynamicFlowMappingDocumentHash(provenanceSnapshot);
        var eventKey = ComputeDynamicFlowMappingObjectHash(
            new
            {
                eventType = successorPlan.EventType,
                targetReportId = report.Id,
                commandId = command.CommandId,
                receiptId,
                resultPayloadRevision = payloadResult.PayloadRevision,
                payloadResult.PayloadHash,
                provenanceHash
            });
        var projectionSnapshot = ToDynamicFlowMappingSnapshot(
            new
            {
                schemaVersion = "P7-MAP-PROJECTION-1",
                targetReportId = report.Id,
                targetAssignmentId = assignment.Id,
                workReportPeriodId = report.WorkReportPeriodId,
                reportStatus = WorkAssignmentReportStatus.Draft.ToString(),
                periodStatus = periodStatus?.ToString(),
                payloadRevision = payloadResult.PayloadRevision,
                payloadHash = payloadResult.PayloadHash,
                lifecycleRevision = report.LifecycleRevision,
                startedDate,
                completedDate,
                isHistoricalData,
                dueAtUtc = effectiveDueAtUtc,
                sourceReportIds = sourceSnapshot.ReportIds
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList(),
                sourceAssignmentIds = sourceSnapshot.AssignmentIds
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList()
            });
        var projectionSnapshotHash =
            ComputeDynamicFlowMappingDocumentHash(projectionSnapshot);
        var auditSnapshot = ToDynamicFlowMappingSnapshot(
            new
            {
                schemaVersion = "P7-MAP-AUDIT-1",
                receiptId,
                provenanceId,
                eventId,
                eventKey,
                targetReportId = report.Id,
                commandId = command.CommandId,
                requestHash = command.RequestHash,
                sourceSignature = command.SourceSignature,
                resultSemanticHash = command.ResultSemanticHash,
                resultPayloadRevision = payloadResult.PayloadRevision,
                resultPayloadHash = payloadResult.PayloadHash,
                provenanceHash,
                authorizationSnapshotHash,
                runtimeRuleSetHash = runtimePin.MappingRuleSetHash,
                sourceFactHashes = sourcePins
                    .Select(pin => pin.SourceFactHash)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList()
            });
        var auditSnapshotHash =
            ComputeDynamicFlowMappingDocumentHash(auditSnapshot);
        var intent = new DynamicFlowMappingReconcileIntent
        {
            ActorUserId = actorUserId,
            ReceiptId = receiptId,
            ProvenanceId = provenanceId,
            EventId = eventId,
            TargetReportId = report.Id,
            TargetAssignmentId = assignment.Id,
            CommandId = command.CommandId,
            TargetPayloadRevision = payloadResult.PayloadRevision,
            TargetPayloadHash = payloadResult.PayloadHash,
            TargetLifecycleRevision = report.LifecycleRevision,
            SourceSignatureVersion =
                DynamicFlowMappingSecurityContract.SourceSignatureVersion,
            SourceSignature = command.SourceSignature,
            ResultSemanticHash = command.ResultSemanticHash,
            MappingRuleSetHash = runtimePin.MappingRuleSetHash,
            ProvenanceHash = provenanceHash,
            RuntimePin = runtimePin,
            ProjectionSnapshot = projectionSnapshot,
            ProjectionSnapshotHash = projectionSnapshotHash,
            AuditSnapshot = auditSnapshot,
            AuditSnapshotHash = auditSnapshotHash,
            ProjectionBusinessKeys = new[]
                {
                    $"report:{report.Id}",
                    $"assignment:{assignment.Id}",
                    $"period:{report.WorkReportPeriodId}"
                }
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList(),
            CommittedAtUtc = now
        };
        var intentHash = ComputeDynamicFlowMappingIntentHash(intent);
        var outboxDedupeKey = ComputeDynamicFlowMappingObjectHash(
            new
            {
                operation = successorPlan.OutboxOperation,
                eventKey,
                intentHash
            });
        var eventPayload = ToDynamicFlowMappingSnapshot(
            new
            {
                schemaVersion = "P7-MAP-EVENT-1",
                receiptId,
                provenanceId,
                outboxId,
                supersedesProvenanceId =
                    successorPlan.PredecessorProvenanceId,
                resultSnapshotHash,
                provenanceHash,
                projectionSnapshotHash,
                auditSnapshotHash,
                intentHash
            });
        var eventPayloadHash =
            ComputeDynamicFlowMappingDocumentHash(eventPayload);
        var writeSetHash = ComputeDynamicFlowMappingObjectHash(
            new
            {
                receiptId,
                provenanceId,
                eventId,
                eventKey,
                eventPayloadHash,
                outboxId,
                outboxDedupeKey,
                intentHash,
                targetReportId = report.Id,
                resultPayloadRevision = payloadResult.PayloadRevision,
                resultPayloadHash = payloadResult.PayloadHash,
                resultLifecycleRevision = report.LifecycleRevision,
                provenanceHash
            });

        var receipt = new DynamicFlowMappingApplyReceipt
        {
            Id = receiptId,
            WorkId = report.WorkId,
            TargetAssignmentId = assignment.Id,
            TargetReportId = report.Id,
            CommandId = command.CommandId,
            RequestHash = command.RequestHash,
            PreviewTokenId = previewClaims.TokenId,
            PreviewTokenHash = command.PreviewTokenHash,
            PreviewIssuedAtUtc = previewClaims.IssuedAtUtc,
            PreviewExpiresAtUtc = previewClaims.ExpiresAtUtc,
            SourceSignatureVersion =
                DynamicFlowMappingSecurityContract.SourceSignatureVersion,
            SourceSignature = command.SourceSignature,
            ResultSemanticHash = command.ResultSemanticHash,
            AuthorizationSnapshotHash = authorizationSnapshotHash,
            ExpectedTargetPayloadRevision = command.ExpectedPayloadRevision,
            ExpectedTargetPayloadHash = command.ExpectedPayloadHash,
            ExpectedTargetLifecycleRevision =
                command.ExpectedLifecycleRevision,
            ExpectedTargetStatus =
                WorkAssignmentReportStatus.Draft.ToString().ToUpperInvariant(),
            ExpectedTargetIsActive = true,
            RuntimePin = runtimePin,
            SourcePins = sourcePins,
            State = DynamicFlowMappingApplyStates.Committed,
            ResultSnapshot = resultSnapshot,
            ResultSnapshotHash = resultSnapshotHash,
            WriteSetHash = writeSetHash,
            ResultPayloadRevision = payloadResult.PayloadRevision,
            ResultPayloadHash = payloadResult.PayloadHash,
            ResultLifecycleRevision = report.LifecycleRevision,
            ProvenanceId = provenanceId,
            ProvenanceHash = provenanceHash,
            EventId = eventId,
            OutboxIntentId = outboxId,
            ActorUserId = actorUserId,
            CreatedAtUtc = now,
            CommittedAtUtc = now,
            UpdatedAtUtc = now
        };
        var provenance = new DynamicFlowMappingProvenanceRecord
        {
            Id = provenanceId,
            ReceiptId = receiptId,
            WorkId = report.WorkId,
            TargetAssignmentId = assignment.Id,
            TargetReportId = report.Id,
            CommandId = command.CommandId,
            TargetPayloadRevision = payloadResult.PayloadRevision,
            TargetPayloadHash = payloadResult.PayloadHash,
            TargetLifecycleRevision = report.LifecycleRevision,
            SourceSignatureVersion =
                DynamicFlowMappingSecurityContract.SourceSignatureVersion,
            SourceSignature = command.SourceSignature,
            ResultSemanticHash = command.ResultSemanticHash,
            MappingRuleSetHash = runtimePin.MappingRuleSetHash,
            RuntimePin = runtimePin,
            SourcePins = sourcePins,
            ResultSnapshot = resultSnapshot,
            ResultSnapshotHash = resultSnapshotHash,
            ProvenanceSnapshot = provenanceSnapshot,
            ProvenanceHash = provenanceHash,
            OwnedTargetRefs = ownedTargetRefs,
            State = DynamicFlowMappingProvenanceStates.Current,
            SupersedesProvenanceId =
                successorPlan.PredecessorProvenanceId,
            CreatedByUserId = actorUserId,
            CreatedAtUtc = now
        };
        var mappingEvent = new DynamicFlowMappingEvent
        {
            Id = eventId,
            EventKey = eventKey,
            EventType = successorPlan.EventType,
            ReceiptId = receiptId,
            ProvenanceId = provenanceId,
            TargetReportId = report.Id,
            TargetAssignmentId = assignment.Id,
            CommandId = command.CommandId,
            CorrelationId = command.CommandId,
            RuntimePin = runtimePin,
            TargetPayloadRevision = payloadResult.PayloadRevision,
            TargetPayloadHash = payloadResult.PayloadHash,
            TargetLifecycleRevision = report.LifecycleRevision,
            SourceSignatureVersion =
                DynamicFlowMappingSecurityContract.SourceSignatureVersion,
            SourceSignature = command.SourceSignature,
            ResultSemanticHash = command.ResultSemanticHash,
            ProvenanceHash = provenanceHash,
            Payload = eventPayload,
            PayloadHash = eventPayloadHash,
            ActorUserId = actorUserId,
            OccurredAtUtc = now
        };
        var outbox = new DynamicFlowMappingOutboxItem
        {
            Id = outboxId,
            ReceiptId = receiptId,
            EventId = eventId,
            ProvenanceId = provenanceId,
            TargetReportId = report.Id,
            CommandId = command.CommandId,
            Operation = successorPlan.OutboxOperation,
            DedupeKey = outboxDedupeKey,
            Intent = intent,
            IntentHash = intentHash,
            State = DynamicFlowMappingOutboxStates.Pending,
            AttemptCount = 0,
            RepairEpoch = 0,
            ProjectorCheckpoints =
                DynamicFlowMappingProjectorContract.BuildPlan(
                    outboxId,
                    intentHash,
                    assignment.Id,
                    report.WorkReportPeriodId),
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var auditLog = new WorkAssignmentReportLog
        {
            Id = ObjectId.GenerateNewId().ToString(),
            WorkId = report.WorkId,
            WorkAssignmentId = assignment.Id,
            WorkReportPeriodId = report.WorkReportPeriodId,
            WorkAssignmentReportId = report.Id,
            Action = successorPlan.IsRerun
                ? "RERUN_DYNAMIC_FLOW_MAPPING"
                : "APPLY_DYNAMIC_FLOW_MAPPING",
            FromStatus = fromStatus.ToString(),
            ToStatus = WorkAssignmentReportStatus.Draft.ToString(),
            ActionByUserId = actorUserId,
            ActionAtUtc = now,
            Reason = successorPlan.IsRerun
                ? DynamicFlowMappingEventTypes.RerunCommitted
                : DynamicFlowMappingEventTypes.ApplyCommitted,
            SnapshotJson = auditSnapshot.ToJson(
                new JsonWriterSettings
                {
                    OutputMode = JsonOutputMode.RelaxedExtendedJson
                }),
            LifecycleEventKey = eventKey,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };

        return new DynamicFlowMappingPersistenceBundle(
            receipt,
            provenance,
            mappingEvent,
            outbox,
            auditLog);
    }

    private static DynamicFlowMappingRuntimePin BuildDynamicFlowMappingRuntimePin(
        DynamicFlowMappingRuntimeContext runtime)
        => new()
        {
            FlowFamilyId = runtime.FlowInstance.FlowTemplateId,
            FlowVersionId = runtime.FlowInstance.FlowTemplateVersionId,
            FlowVersionNo = runtime.FlowInstance.FlowTemplateVersionNo,
            FlowPayloadHash = runtime.FlowInstance.FlowPayloadHash,
            CatalogVersion = runtime.FlowInstance.CatalogVersion,
            CatalogSemanticHash = runtime.FlowInstance.CatalogSemanticHash,
            MappingRuleSetHash = runtime.RuleSetHash,
            EvaluatorVersion =
                DynamicFlowMappingExpressionEvaluator.EvaluatorVersion,
            FunctionRegistryVersion =
                DynamicFlowRegisteredFunctionRegistry.RegistryVersion,
            FunctionRegistryHash =
                DynamicFlowRegisteredFunctionRegistry.RegistryHash,
            FlowInstanceId = runtime.FlowInstance.Id,
            ExecutionEpoch = runtime.TargetStep.ExecutionEpoch,
            StepInstanceId = runtime.TargetStep.Id,
            StepId = runtime.TargetStep.FlowStepId,
            BranchId = runtime.TargetStep.BranchId,
            AttemptNo = runtime.TargetStep.AttemptNo,
            FormFamilyId = runtime.TargetStep.FormFamilyId,
            FormVersionId = runtime.TargetStep.FormVersionId,
            FormVersionNo = runtime.TargetStep.FormVersionNo,
            FormSchemaHash = runtime.TargetStep.FormSchemaHash,
            FormSnapshotHash = runtime.TargetStep.FormSnapshotHash
        };

    private static DynamicFlowMappingSourcePin BuildDynamicFlowMappingSourcePin(
        DynamicFlowMappingSourceReport source)
    {
        var step = source.RuntimeStep ??
                   throw new InvalidOperationException(
                       "DYNAMIC_FLOW_MAPPING_SOURCE_STEP_PIN_REQUIRED");
        var instance = source.RuntimeInstance ??
                       throw new InvalidOperationException(
                           "DYNAMIC_FLOW_MAPPING_SOURCE_INSTANCE_PIN_REQUIRED");
        var sourceFactHash = ComputeDynamicFlowMappingObjectHash(
            new
            {
                sourceReportId = source.Report.Id,
                sourceAssignmentId = source.Report.WorkAssignmentId,
                sourceFlowInstanceId = instance.Id,
                sourceExecutionEpoch = step.ExecutionEpoch,
                sourceStepInstanceId = step.Id,
                sourceStepId = step.FlowStepId,
                sourceBranchId = step.BranchId,
                sourceAttemptNo = step.AttemptNo,
                sourceFormFamilyId = step.FormFamilyId,
                sourceFormVersionId = step.FormVersionId,
                sourceFormVersionNo = step.FormVersionNo,
                sourceFormSchemaHash = step.FormSchemaHash,
                sourcePayloadRevision = source.Report.PayloadRevision,
                sourcePayloadHash = source.Report.PayloadHash,
                sourceLifecycleRevision = source.Report.LifecycleRevision,
                sourceLifecycleStatus =
                    source.Report.Status.ToString().ToUpperInvariant(),
                sourcePeriodInstanceKey =
                    NormalizeReportPeriodInstanceKey(source.Report)
            });
        return new DynamicFlowMappingSourcePin
        {
            SourceReportId = source.Report.Id,
            SourceAssignmentId = source.Report.WorkAssignmentId,
            SourceFlowInstanceId = instance.Id,
            SourceExecutionEpoch = step.ExecutionEpoch,
            SourceStepInstanceId = step.Id,
            SourceStepId = step.FlowStepId,
            SourceBranchId = step.BranchId,
            SourceAttemptNo = step.AttemptNo,
            SourceFormFamilyId = step.FormFamilyId,
            SourceFormVersionId = step.FormVersionId,
            SourceFormVersionNo = step.FormVersionNo,
            SourceFormSchemaHash = step.FormSchemaHash,
            SourcePayloadRevision = source.Report.PayloadRevision,
            SourcePayloadHash = source.Report.PayloadHash ?? string.Empty,
            SourceLifecycleRevision = source.Report.LifecycleRevision,
            SourceLifecycleStatus =
                source.Report.Status.ToString().ToUpperInvariant(),
            SourcePeriodInstanceKey =
                NormalizeReportPeriodInstanceKey(source.Report),
            SourceFactHash = sourceFactHash
        };
    }

    private static WorkAssignmentReport BuildDynamicFlowMappingCommittedReport(
        WorkAssignmentReport report,
        DynamicFlowMappingPersistenceBundle persistence,
        WorkReportPayloadWriteResult payloadResult,
        PayloadMutationCommand payloadCommand,
        DynamicFlowMappingPreviewResponse projection,
        AggregateSourceSnapshot sourceSnapshot,
        string values1DJson,
        string? fieldValuesJson,
        string? tableValuesJson,
        DateTime? startedDate,
        DateTime? completedDate,
        bool isHistoricalData,
        DateTime? effectiveDueAtUtc,
        string actorUserId,
        DateTime now)
    {
        var committed = BsonSerializer.Deserialize<WorkAssignmentReport>(
            report.ToBsonDocument());
        committed.Values1DJson = values1DJson;
        committed.FieldValuesJson = fieldValuesJson;
        committed.TableValuesJson = tableValuesJson;
        committed.SummarySourceJson = projection.SummarySourceJson;
        ApplyPayloadMetadata(committed, payloadResult, now);
        ApplyPayloadCommandCompletionInMemory(
            committed,
            payloadCommand,
            payloadResult);
        committed.DataOrigin =
            WorkReportDataOrigin.Normalize(projection.DataOrigin);
        // P7 persists mapped reports outside the statistics contribution set.
        // An individual mapping rule may carry future INCLUDE semantics, but
        // P8/P9 execution is not active and must not be inferred at apply time.
        committed.CumulativeContributionMode =
            WorkReportCumulativeContributionMode.Exclude;
        committed.CumulativeContributionPolicyJson =
            NormalizeOptionalTextOrNull(
                projection.CumulativeContributionPolicyJson);
        committed.AggregateSourceReportIds = sourceSnapshot.ReportIds;
        committed.AggregateSourceAssignmentIds = sourceSnapshot.AssignmentIds;
        committed.AggregateSourceUpdatedAtUtc =
            sourceSnapshot.IsAggregate ? now : null;
        committed.AggregateSnapshotDirty = false;
        committed.AggregateSnapshotDirtyAtUtc = null;
        committed.AggregateSnapshotRefreshedAtUtc =
            sourceSnapshot.IsAggregate ? now : null;
        committed.AggregateRefreshError = null;
        committed.StartedDate = startedDate;
        committed.CompletedDate = completedDate;
        committed.IsHistoricalData = isHistoricalData;
        committed.DueAtUtc = effectiveDueAtUtc;
        committed.Status = WorkAssignmentReportStatus.Draft;
        committed.DynamicFlowMappingReceiptId =
            persistence.Receipt.Id;
        committed.DynamicFlowMappingProvenanceId =
            persistence.Provenance.Id;
        committed.DynamicFlowMappingProvenanceHash =
            persistence.Provenance.ProvenanceHash;
        committed.DynamicFlowMappingResultPayloadRevision =
            payloadResult.PayloadRevision;
        committed.DynamicFlowMappingResultPayloadHash =
            payloadResult.PayloadHash;
        committed.CreatedByUserId = string.IsNullOrWhiteSpace(
            committed.CreatedByUserId)
            ? actorUserId
            : committed.CreatedByUserId;
        committed.UpdatedAtUtc = now;
        committed.UpdatedByUserId = actorUserId;
        return committed;
    }

    private static FilterDefinition<WorkAssignmentReport>
        BuildDynamicFlowMappingCommitFilter(
            WorkAssignmentReport report,
            DynamicFlowMappingApplyCommand command)
    {
        var fb = Builders<WorkAssignmentReport>.Filter;
        var payloadRevisionFilter = command.ExpectedPayloadRevision == 0
            ? fb.Eq(item => item.PayloadRevision, 0) |
              fb.Exists(item => item.PayloadRevision, false)
            : fb.Eq(
                item => item.PayloadRevision,
                command.ExpectedPayloadRevision);
        return fb.Eq(item => item.Id, report.Id) &
               fb.Eq(item => item.IsDeleted, false) &
               fb.Eq(item => item.AssigneeUserId, report.AssigneeUserId) &
               fb.Eq(item => item.Status, WorkAssignmentReportStatus.Draft) &
               fb.Eq(item => item.IsActive, true) &
               fb.Eq(item => item.IsCurrent, true) &
               payloadRevisionFilter &
               fb.Eq(item => item.PayloadHash, command.ExpectedPayloadHash) &
               fb.Eq(
                   item => item.LifecycleRevision,
                   command.ExpectedLifecycleRevision) &
               fb.Eq(item => item.PayloadMutationCommandId, null) &
               fb.Eq(
                   item => item.DynamicFlowMappingReceiptId,
                   report.DynamicFlowMappingReceiptId) &
               fb.Eq(
                   item => item.DynamicFlowMappingProvenanceId,
                   report.DynamicFlowMappingProvenanceId) &
               fb.Eq(
                   item => item.DynamicFlowMappingProvenanceHash,
                   report.DynamicFlowMappingProvenanceHash) &
               fb.Eq(
                   item => item.DynamicFlowMappingResultPayloadRevision,
                   report.DynamicFlowMappingResultPayloadRevision) &
               fb.Eq(
                   item => item.DynamicFlowMappingResultPayloadHash,
                   report.DynamicFlowMappingResultPayloadHash);
    }

    private static UpdateDefinition<WorkAssignmentReport>
        ApplyDynamicFlowMappingHeaderCommit(
            WorkAssignmentReport committed,
            PayloadMutationCommand payloadCommand,
            WorkReportPayloadWriteResult payloadResult,
            DynamicFlowMappingPersistenceBundle persistence)
    {
        var update = ApplyPayloadCommandCompletion(
            ApplyPayloadHeaderUpdate(
                    Builders<WorkAssignmentReport>.Update,
                    payloadResult,
                    committed.UpdatedAtUtc)
                .Set(item => item.DataOrigin, committed.DataOrigin)
                .Set(
                    item => item.CumulativeContributionMode,
                    committed.CumulativeContributionMode)
                .Set(
                    item => item.CumulativeContributionPolicyJson,
                    committed.CumulativeContributionPolicyJson)
                .Set(
                    item => item.AggregateSourceReportIds,
                    committed.AggregateSourceReportIds)
                .Set(
                    item => item.AggregateSourceAssignmentIds,
                    committed.AggregateSourceAssignmentIds)
                .Set(
                    item => item.AggregateSourceUpdatedAtUtc,
                    committed.AggregateSourceUpdatedAtUtc)
                .Set(
                    item => item.AggregateSnapshotDirty,
                    committed.AggregateSnapshotDirty)
                .Set(
                    item => item.AggregateSnapshotDirtyAtUtc,
                    committed.AggregateSnapshotDirtyAtUtc)
                .Set(
                    item => item.AggregateSnapshotRefreshedAtUtc,
                    committed.AggregateSnapshotRefreshedAtUtc)
                .Set(
                    item => item.AggregateRefreshError,
                    committed.AggregateRefreshError)
                .Set(item => item.W, committed.W)
                .Set(item => item.H, committed.H)
                .Set(item => item.DataRectR0, committed.DataRectR0)
                .Set(item => item.DataRectC0, committed.DataRectC0)
                .Set(item => item.DataRectR1, committed.DataRectR1)
                .Set(item => item.DataRectC1, committed.DataRectC1)
                .Set(item => item.StartedDate, committed.StartedDate)
                .Set(item => item.CompletedDate, committed.CompletedDate)
                .Set(item => item.IsHistoricalData, committed.IsHistoricalData)
                .Set(item => item.DueAtUtc, committed.DueAtUtc)
                .Set(item => item.Status, WorkAssignmentReportStatus.Draft)
                .Set(
                    item => item.CreatedByUserId,
                    committed.CreatedByUserId)
                .Set(item => item.UpdatedAtUtc, committed.UpdatedAtUtc)
                .Set(item => item.UpdatedByUserId, committed.UpdatedByUserId),
            payloadCommand,
            payloadResult);
        return update
            .Set(
                item => item.DynamicFlowMappingReceiptId,
                persistence.Receipt.Id)
            .Set(
                item => item.DynamicFlowMappingProvenanceId,
                persistence.Provenance.Id)
            .Set(
                item => item.DynamicFlowMappingProvenanceHash,
                persistence.Provenance.ProvenanceHash)
            .Set(
                item => item.DynamicFlowMappingResultPayloadRevision,
                payloadResult.PayloadRevision)
            .Set(
                item => item.DynamicFlowMappingResultPayloadHash,
                payloadResult.PayloadHash);
    }

    private static void EnsureDynamicFlowMappingPayloadWriteMatches(
        WorkReportPayloadWriteResult expected,
        WorkReportPayloadWriteResult actual,
        WorkAssignmentReport report)
    {
        if (expected.PayloadRevision == actual.PayloadRevision &&
            string.Equals(
                expected.PayloadHash,
                actual.PayloadHash,
                StringComparison.Ordinal) &&
            expected.PayloadSizeBytes == actual.PayloadSizeBytes &&
            string.Equals(
                expected.PayloadStatus,
                actual.PayloadStatus,
                StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"DYNAMIC_FLOW_MAPPING_PAYLOAD_PREFLIGHT_MISMATCH:{report.Id}");
    }

    private static UpdateDefinition<WorkReportPeriod>
        BuildDynamicFlowMappingPeriodUpdate(
            WorkReportPeriodStatus periodStatus,
            DateTime? startedDate,
            DateTime? completedDate,
            bool isHistoricalData,
            DateTime? effectiveDueAtUtc,
            string actorUserId,
            DateTime now)
        => Builders<WorkReportPeriod>.Update
            .Set(item => item.StartedDate, startedDate)
            .Set(item => item.CompletedDate, completedDate)
            .Set(item => item.IsHistoricalData, isHistoricalData)
            .Set(item => item.DueAtUtc, effectiveDueAtUtc)
            .Set(item => item.Status, periodStatus)
            .Set(
                item => item.IsOverdue,
                WorkReportPeriodStatusHelper.IsOverdue(periodStatus))
            .Set(item => item.LastDraftSavedAtUtc, now)
            .Set(item => item.UpdatedAtUtc, now)
            .Set(item => item.UpdatedByUserId, actorUserId);

    private static void ApplyDynamicFlowMappingPeriodInMemory(
        WorkReportPeriod? period,
        WorkReportPeriodStatus? periodStatus,
        DateTime? startedDate,
        DateTime? completedDate,
        bool isHistoricalData,
        DateTime? effectiveDueAtUtc,
        string actorUserId,
        DateTime now)
    {
        if (period is null || !periodStatus.HasValue)
            return;
        period.StartedDate = startedDate;
        period.CompletedDate = completedDate;
        period.IsHistoricalData = isHistoricalData;
        period.DueAtUtc = effectiveDueAtUtc;
        period.Status = periodStatus.Value;
        period.IsOverdue =
            WorkReportPeriodStatusHelper.IsOverdue(periodStatus.Value);
        period.LastDraftSavedAtUtc = now;
        period.UpdatedAtUtc = now;
        period.UpdatedByUserId = actorUserId;
    }

    private static bool IsMongoDuplicateKey(Exception error)
    {
        if (error is MongoWriteException writeException &&
            writeException.WriteError?.Category ==
            ServerErrorCategory.DuplicateKey)
        {
            return true;
        }
        if (error is MongoCommandException commandException &&
            commandException.Code is 11000 or 11001)
        {
            return true;
        }
        if (error.Message.Contains(
                "E11000 duplicate key",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return error.InnerException is not null &&
               IsMongoDuplicateKey(error.InnerException);
    }

    private async Task<WorkAssignmentReport>
        RevalidateDynamicFlowMappingWriteBoundaryAsync(
            IClientSessionHandle session,
            WorkAssignmentReport preflightReport,
            WorkAssignment preflightAssignment,
            DynamicFlowMappingProjectionResult preflight,
            DynamicFlowMappingApplyCommand command,
            string actorUserId,
            CancellationToken ct)
    {
        var report = await _ctx.WorkAssignmentReports
            .Find(
                session,
                BuildDynamicFlowMappingCommitFilter(
                    preflightReport,
                    command))
            .FirstOrDefaultAsync(ct);
        if (report is null)
        {
            var current = await _ctx.WorkAssignmentReports
                .Find(
                    session,
                    item =>
                        item.Id == preflightReport.Id &&
                        !item.IsDeleted)
                .FirstOrDefaultAsync(ct);
            throw DynamicFlowMappingTargetRevisionConflict(
                current ?? preflightReport,
                command,
                "DYNAMIC_FLOW_MAPPING_TARGET_CAS_PRECONDITION_FAILED");
        }

        var assignment = await _ctx.WorkAssignments
            .Find(
                session,
                item =>
                    item.Id == preflightAssignment.Id &&
                    item.WorkId == preflightAssignment.WorkId &&
                    !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (assignment is null ||
            !assignment.IsActive ||
            !string.Equals(
                report.WorkAssignmentId,
                assignment.Id,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.AssigneeUserId,
                actorUserId,
                StringComparison.Ordinal) ||
            !string.Equals(
                assignment.FlowEffectiveStatus,
                DynamicFlowEffectiveStatuses.Effective,
                StringComparison.Ordinal) ||
            !string.Equals(
                assignment.FlowInstanceId,
                preflight.Runtime.FlowInstance.Id,
                StringComparison.Ordinal) ||
            assignment.FlowExecutionEpoch !=
            preflight.Runtime.TargetStep.ExecutionEpoch ||
            !string.Equals(
                assignment.FlowStepId,
                preflight.Runtime.TargetStep.FlowStepId,
                StringComparison.Ordinal) ||
            !string.Equals(
                assignment.FlowBranchId,
                preflight.Runtime.TargetStep.BranchId,
                StringComparison.Ordinal) ||
            assignment.FlowAttemptNo !=
            preflight.Runtime.TargetStep.AttemptNo)
        {
            throw DynamicFlowMappingIdentityConflict(
                report,
                assignment ?? preflightAssignment,
                "targetAssignment",
                "DYNAMIC_FLOW_MAPPING_TARGET_ASSIGNMENT_CHANGED");
        }

        await EnsureDynamicFlowMappingMutationScopeOpenAsync(
            session,
            assignment,
            actorUserId,
            ct);
        await EnsureDynamicFlowMappingProvenanceIntegrityAsync(
            report,
            ct,
            session,
            DynamicFlowMappingIntegrityMode.AllowHistorical);

        var runtimeInstance = await _ctx.DynamicFlowInstances
            .Find(
                session,
                item =>
                    item.Id == preflight.Runtime.FlowInstance.Id &&
                    item.WorkId == assignment.WorkId &&
                    item.ExecutionEpoch ==
                    preflight.Runtime.TargetStep.ExecutionEpoch)
            .FirstOrDefaultAsync(ct);
        if (runtimeInstance is null ||
            runtimeInstance.State is not DynamicFlowInstanceStates.Active and
                not DynamicFlowInstanceStates.Completed and
                not DynamicFlowInstanceStates.Reconciled ||
            !string.Equals(
                runtimeInstance.FlowTemplateId,
                preflight.Runtime.FlowInstance.FlowTemplateId,
                StringComparison.Ordinal) ||
            !string.Equals(
                runtimeInstance.FlowTemplateVersionId,
                preflight.Runtime.FlowInstance.FlowTemplateVersionId,
                StringComparison.Ordinal) ||
            runtimeInstance.FlowTemplateVersionNo !=
            preflight.Runtime.FlowInstance.FlowTemplateVersionNo ||
            !string.Equals(
                runtimeInstance.FlowPayloadHash,
                preflight.Runtime.FlowInstance.FlowPayloadHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                runtimeInstance.CatalogVersion,
                preflight.Runtime.FlowInstance.CatalogVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                runtimeInstance.CatalogSemanticHash,
                preflight.Runtime.FlowInstance.CatalogSemanticHash,
                StringComparison.Ordinal))
        {
            throw DynamicFlowMappingIdentityConflict(
                report,
                assignment,
                "flowInstanceId",
                "DYNAMIC_FLOW_MAPPING_RUNTIME_INSTANCE_CHANGED");
        }
        if (!_dynamicFlowRuntimeActivationPolicy.CanExecuteP7MappingPin(
                runtimeInstance.CatalogVersion,
                runtimeInstance.CatalogSemanticHash))
        {
            throw DynamicFlowMappingIdentityConflict(
                report,
                assignment,
                "catalogVersion",
                "DYNAMIC_FLOW_MAPPING_RUNTIME_CATALOG_NO_LONGER_ELIGIBLE");
        }

        var flowVersion = await _ctx.DynamicFlowTemplateVersions
            .Find(
                session,
                item =>
                    item.Id == runtimeInstance.FlowTemplateVersionId &&
                    item.TemplateId == runtimeInstance.FlowTemplateId &&
                    item.VersionNo ==
                    runtimeInstance.FlowTemplateVersionNo &&
                    item.Status ==
                    DynamicFlowTemplateVersionStatuses.Locked &&
                    !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (flowVersion is null ||
            !string.Equals(
                flowVersion.PayloadHash,
                runtimeInstance.FlowPayloadHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                flowVersion.CatalogVersion,
                runtimeInstance.CatalogVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                flowVersion.CatalogSemanticHash,
                runtimeInstance.CatalogSemanticHash,
                StringComparison.Ordinal))
        {
            throw DynamicFlowMappingIdentityConflict(
                report,
                assignment,
                "flowVersionId",
                "DYNAMIC_FLOW_MAPPING_LOCKED_FLOW_PIN_CHANGED");
        }

        var targetStep = await _ctx.DynamicFlowStepInstances
            .Find(
                session,
                item =>
                    item.Id == preflight.Runtime.TargetStep.Id &&
                    item.FlowInstanceId == runtimeInstance.Id &&
                    item.ExecutionEpoch == runtimeInstance.ExecutionEpoch &&
                    item.AssignmentId == assignment.Id &&
                    item.ReportId == report.Id)
            .FirstOrDefaultAsync(ct);
        if (targetStep is null ||
            targetStep.IsCanonicalEpoch == false ||
            !string.IsNullOrWhiteSpace(
                targetStep.InvalidatedByFlowEventId) ||
            targetStep.InvalidatedAtUtc.HasValue ||
            !string.IsNullOrWhiteSpace(
                targetStep.SupersededByStepInstanceId) ||
            targetStep.State is not DynamicFlowStepStates.Assigned and
                not DynamicFlowStepStates.InProgress and
                not DynamicFlowStepStates.Returned ||
            !string.Equals(
                targetStep.FlowStepId,
                assignment.FlowStepId,
                StringComparison.Ordinal) ||
            !string.Equals(
                targetStep.BranchId,
                assignment.FlowBranchId,
                StringComparison.Ordinal) ||
            targetStep.AttemptNo != assignment.FlowAttemptNo ||
            !string.Equals(
                targetStep.FormFamilyId,
                report.DynamicFormFamilyId,
                StringComparison.Ordinal) ||
            !string.Equals(
                targetStep.FormVersionId,
                report.DynamicFormTemplateId,
                StringComparison.Ordinal) ||
            targetStep.FormVersionNo != report.DynamicFormVersionNo ||
            !string.Equals(
                targetStep.FormSchemaHash,
                report.DynamicFormSchemaHash,
                StringComparison.Ordinal) ||
            targetStep.ReportLifecycleRevision !=
            report.LifecycleRevision ||
            !string.Equals(
                targetStep.ReportLifecycleStatus,
                report.Status.ToString().ToUpperInvariant(),
                StringComparison.Ordinal) ||
            targetStep.ReportLifecycleIsActive != report.IsActive)
        {
            throw DynamicFlowMappingIdentityConflict(
                report,
                assignment,
                "stepInstanceId",
                "DYNAMIC_FLOW_MAPPING_TARGET_STEP_CHANGED");
        }

        IReadOnlyList<DynamicFlowMappingRuleDto> rules;
        try
        {
            rules = DynamicFlowMappingEngine.ReadRulesFromPayloadJson(
                flowVersion.PayloadJson);
            DynamicFlowMappingEngine.ValidateP7Rules(rules);
        }
        catch (Exception error) when (
            error is DynamicFlowMappingEvaluationException or
                JsonException)
        {
            throw DynamicFlowMappingIdentityConflict(
                report,
                assignment,
                "mappingRules",
                "DYNAMIC_FLOW_MAPPING_LOCKED_RULES_INVALID");
        }
        var ruleSetHash =
            DynamicFlowMappingRuntimeContract.ComputeRuleSetHash(rules);
        if (!string.Equals(
                ruleSetHash,
                preflight.Runtime.RuleSetHash,
                StringComparison.Ordinal))
        {
            throw DynamicFlowMappingIdentityConflict(
                report,
                assignment,
                "mappingRuleSetHash",
                "DYNAMIC_FLOW_MAPPING_RULE_SET_CHANGED");
        }
        var runtime = new DynamicFlowMappingRuntimeContext(
            flowVersion,
            runtimeInstance,
            targetStep,
            rules,
            ruleSetHash);
        DynamicFlowTemplatePayloadDto topology;
        try
        {
            topology = DynamicFlowDefinitionPayloadContract
                .CanonicalizeAndValidate(
                    flowVersion.PayloadJson,
                    new DynamicFlowDefinitionValidationOptions(
                        AllowLegacy: false,
                        AllowServerManagedPins: true,
                        RequireServerManagedPins: true,
                        AllowHistoricalCatalogPins: true))
                .Payload;
        }
        catch (Exception error) when (
            error is AppException or
                InvalidOperationException or
                JsonException)
        {
            throw DynamicFlowMappingIdentityConflict(
                report,
                assignment,
                "flowPayloadHash",
                "DYNAMIC_FLOW_MAPPING_LOCKED_TOPOLOGY_INVALID");
        }

        var actorRole = ResolveDirectDynamicFlowActorRole(
            assignment,
            report,
            actorUserId);
        if (actorRole is null)
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.DYNAMIC_FLOW_MAPPING_POLICY_DENIED,
                new
                {
                    reportId = report.Id,
                    workAssignmentId = assignment.Id,
                    reason =
                        "DYNAMIC_FLOW_MAPPING_WRITE_BOUNDARY_ROLE_UNRESOLVED"
                });
        }
        DynamicFlowPolicyEvaluationResult permissions;
        try
        {
            permissions = _dynamicFlowPolicyEvaluator.Evaluate(
                flowVersion.PayloadJson,
                new DynamicFlowPolicyEvaluationContext
                {
                    StepId = targetStep.FlowStepId,
                    StepCode = targetStep.FlowStepCode,
                    ActorRole = actorRole,
                    IsAfterSubmit = false
                });
        }
        catch (Exception error) when (
            error is AppException or
                InvalidOperationException or
                JsonException)
        {
            throw DynamicFlowMappingPolicyDenied(
                report,
                assignment,
                "DYNAMIC_FLOW_MAPPING_POLICY_NOT_LOADED");
        }
        EnsureDynamicFlowMappingTargetRulesWritable(
            report,
            assignment,
            rules,
            permissions);
        if (!string.Equals(
                ComputeDynamicFlowMappingAuthorizationScopeHash(
                    assignment,
                    report,
                    runtime,
                    permissions,
                    actorUserId),
                ComputeDynamicFlowMappingAuthorizationScopeHash(
                    preflightAssignment,
                    preflightReport,
                    preflight.Runtime,
                    preflight.Permissions,
                    actorUserId),
                StringComparison.Ordinal))
        {
            throw DynamicFlowMappingPolicyDenied(
                report,
                assignment,
                "DYNAMIC_FLOW_MAPPING_AUTHORIZATION_SNAPSHOT_CHANGED");
        }

        var sources = new List<DynamicFlowMappingSourceReport>();
        foreach (var expected in preflight.Sources
                     .OrderBy(
                         item => item.Report.Id,
                         StringComparer.Ordinal))
        {
            var expectedStep = expected.RuntimeStep ??
                               throw DynamicFlowMappingIdentityConflict(
                                   report,
                                   assignment,
                                   "source.stepInstanceId",
                                   "DYNAMIC_FLOW_MAPPING_SOURCE_STEP_PIN_MISSING");
            var sourceReport = await _ctx.WorkAssignmentReports
                .Find(
                    session,
                    item =>
                        item.Id == expected.Report.Id &&
                        item.WorkAssignmentId ==
                        expected.Report.WorkAssignmentId &&
                        item.PayloadRevision ==
                        expected.Report.PayloadRevision &&
                        item.PayloadHash ==
                        expected.Report.PayloadHash &&
                        item.LifecycleRevision ==
                        expected.Report.LifecycleRevision &&
                        item.Status ==
                        WorkAssignmentReportStatus.Approved &&
                        item.IsActive &&
                        item.IsCurrent &&
                        !item.IsDeleted)
                .FirstOrDefaultAsync(ct);
            var sourceAssignment = await _ctx.WorkAssignments
                .Find(
                    session,
                    item =>
                        item.Id == expected.Report.WorkAssignmentId &&
                        item.WorkId == assignment.WorkId &&
                        item.FlowInstanceId == runtimeInstance.Id &&
                        item.FlowExecutionEpoch ==
                        runtimeInstance.ExecutionEpoch &&
                        item.FlowStepId == expectedStep.FlowStepId &&
                        item.FlowBranchId == expectedStep.BranchId &&
                        item.FlowAttemptNo == expectedStep.AttemptNo &&
                        item.IsActive &&
                        !item.IsDeleted)
                .FirstOrDefaultAsync(ct);
            var sourceStep = await _ctx.DynamicFlowStepInstances
                .Find(
                    session,
                    item =>
                        item.Id == expectedStep.Id &&
                        item.FlowInstanceId == runtimeInstance.Id &&
                        item.ExecutionEpoch ==
                        runtimeInstance.ExecutionEpoch &&
                        item.AssignmentId ==
                        expected.Report.WorkAssignmentId &&
                        item.ReportId == expected.Report.Id)
                .FirstOrDefaultAsync(ct);
            if (sourceReport is null ||
                sourceAssignment is null ||
                sourceStep is null ||
                sourceStep.IsCanonicalEpoch == false ||
                !string.IsNullOrWhiteSpace(
                    sourceStep.InvalidatedByFlowEventId) ||
                sourceStep.InvalidatedAtUtc.HasValue ||
                !string.IsNullOrWhiteSpace(
                    sourceStep.SupersededByStepInstanceId) ||
                sourceStep.State is not DynamicFlowStepStates.Approved and
                    not DynamicFlowStepStates.Completed ||
                !string.Equals(
                    sourceStep.FormFamilyId,
                    sourceReport.DynamicFormFamilyId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    sourceStep.FormVersionId,
                    sourceReport.DynamicFormTemplateId,
                    StringComparison.Ordinal) ||
                sourceStep.FormVersionNo !=
                sourceReport.DynamicFormVersionNo ||
                !string.Equals(
                    sourceStep.FormSchemaHash,
                    sourceReport.DynamicFormSchemaHash,
                    StringComparison.Ordinal) ||
                sourceStep.ReportLifecycleRevision !=
                sourceReport.LifecycleRevision ||
                !string.Equals(
                    sourceStep.ReportLifecycleStatus,
                    sourceReport.Status.ToString().ToUpperInvariant(),
                    StringComparison.Ordinal) ||
                sourceStep.ReportLifecycleIsActive !=
                sourceReport.IsActive)
            {
                throw DynamicFlowMappingIdentityConflict(
                    report,
                    assignment,
                    "source.runtimeIdentity",
                    "DYNAMIC_FLOW_MAPPING_SOURCE_CHANGED_AT_WRITE_BOUNDARY");
            }

            try
            {
                DynamicFlowMappingCanonicalSourceContract.Validate(
                    runtimeInstance,
                    targetStep,
                    assignment,
                    sourceStep,
                    sourceAssignment,
                    sourceReport,
                    IsDynamicFlowMappingTopologyAncestor(
                        topology,
                        sourceStep.FlowStepId,
                        targetStep.FlowStepId));
            }
            catch (DynamicFlowMappingSourceContractException error)
            {
                throw DynamicFlowMappingIdentityConflict(
                    report,
                    assignment,
                    error.Field,
                    error.Reason);
            }

            DynamicFlowPolicyEvaluationResult sourcePermissions;
            try
            {
                sourcePermissions = _dynamicFlowPolicyEvaluator.Evaluate(
                    flowVersion.PayloadJson,
                    new DynamicFlowPolicyEvaluationContext
                    {
                        StepId = sourceStep.FlowStepId,
                        StepCode = sourceStep.FlowStepCode,
                        ActorRole = actorRole,
                        IsAfterSubmit = true
                    });
            }
            catch (Exception error) when (
                error is AppException or
                    InvalidOperationException or
                    JsonException)
            {
                throw DynamicFlowMappingPolicyDenied(
                    report,
                    assignment,
                    "DYNAMIC_FLOW_MAPPING_SOURCE_POLICY_LOAD_FAILED");
            }
            EnsureDynamicFlowMappingSourceRulesReadable(
                report,
                assignment,
                rules,
                sourceStep.FlowStepId,
                sourcePermissions);
            sources.Add(
                new DynamicFlowMappingSourceReport(
                    sourceReport,
                    sourceStep.FlowStepId,
                    sourceStep.FlowStepCode,
                    null,
                    null,
                    runtimeInstance,
                    sourceStep,
                    sourceAssignment));
        }

        var sourceSignature =
            DynamicFlowMappingRuntimeContract.ComputeSourceSignature(
                runtime,
                report,
                sources);
        if (sources.Count != preflight.Sources.Count ||
            !string.Equals(
                sourceSignature,
                command.SourceSignature,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_CONFLICT,
                new
                {
                    reportId = report.Id,
                    reason =
                        "DYNAMIC_FLOW_MAPPING_SOURCE_SIGNATURE_CHANGED_AT_WRITE_BOUNDARY"
                });
        }

        EnsureDynamicFlowMappingExpectedTarget(
            report,
            command,
            assignment);
        return report;
    }

    private async Task EnsureDynamicFlowMappingMutationScopeOpenAsync(
        IClientSessionHandle session,
        WorkAssignment assignment,
        string actorUserId,
        CancellationToken ct)
    {
        var work = await _ctx.Works
            .Find(
                session,
                item => item.Id == assignment.WorkId && !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (work is null ||
            work.CompletedAtUtc.HasValue ||
            work.Status == WorkStatus.S3 ||
            IsAssignmentManuallyCompleted(assignment))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_SCOPE_COMPLETED_LOCKED,
                new
                {
                    assignmentId = assignment.Id,
                    assignment.WorkId,
                    actorUserId
                });
        }

        var ancestorIds = ResolveAncestorIds(assignment);
        if (ancestorIds.Count == 0)
            return;
        var hasCompletedAncestor = await _ctx.WorkAssignments
            .Find(
                session,
                item =>
                    ancestorIds.Contains(item.Id) &&
                    item.WorkId == assignment.WorkId &&
                    !item.IsDeleted &&
                    (item.CompletedAtUtc != null ||
                     (item.ProgressStatus ==
                      (int)WorkAssignmentProgressStatus.Completed &&
                      item.CompletedDate != null)))
            .Limit(1)
            .AnyAsync(ct);
        if (!hasCompletedAncestor)
            return;

        throw AppExceptionFactory.Create(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_SCOPE_COMPLETED_LOCKED,
            new
            {
                assignmentId = assignment.Id,
                assignment.WorkId,
                actorUserId
            });
    }

    private async Task EnsureDynamicFlowMappingProvenanceIntegrityAsync(
        WorkAssignmentReport report,
        CancellationToken ct,
        IClientSessionHandle? session = null,
        DynamicFlowMappingIntegrityMode mode =
            DynamicFlowMappingIntegrityMode.RequireCurrent)
    {
        var hasAnyMappingReference =
            !string.IsNullOrWhiteSpace(
                report.DynamicFlowMappingReceiptId) ||
            !string.IsNullOrWhiteSpace(
                report.DynamicFlowMappingProvenanceId) ||
            !string.IsNullOrWhiteSpace(
                report.DynamicFlowMappingProvenanceHash) ||
            report.DynamicFlowMappingResultPayloadRevision.HasValue ||
            !string.IsNullOrWhiteSpace(
                report.DynamicFlowMappingResultPayloadHash);
        if (!hasAnyMappingReference)
            return;
        if (string.IsNullOrWhiteSpace(
                report.DynamicFlowMappingReceiptId) ||
            string.IsNullOrWhiteSpace(
                report.DynamicFlowMappingProvenanceId) ||
            !IsLowerSha256(
                report.DynamicFlowMappingProvenanceHash) ||
            report.DynamicFlowMappingResultPayloadRevision is not > 0 ||
            !IsLowerSha256(
                report.DynamicFlowMappingResultPayloadHash))
        {
            throw DynamicFlowMappingProvenanceTampered(
                report,
                "DYNAMIC_FLOW_MAPPING_HEADER_REFERENCE_INCOMPLETE");
        }

        DynamicFlowMappingApplyReceipt? receipt;
        DynamicFlowMappingProvenanceRecord? provenance;
        DynamicFlowMappingEvent? mappingEvent;
        DynamicFlowMappingOutboxItem? outbox;
        WorkReportPayload? payload;
        if (session is null)
        {
            receipt = await _ctx.DynamicFlowMappingApplyReceipts
                .Find(item =>
                    item.Id ==
                    report.DynamicFlowMappingReceiptId &&
                    item.TargetReportId == report.Id)
                .FirstOrDefaultAsync(ct);
            provenance = await _ctx.DynamicFlowMappingProvenanceRecords
                .Find(item =>
                    item.Id ==
                    report.DynamicFlowMappingProvenanceId &&
                    item.ReceiptId ==
                    report.DynamicFlowMappingReceiptId &&
                    item.TargetReportId == report.Id)
                .FirstOrDefaultAsync(ct);
            mappingEvent = receipt is null
                ? null
                : await _ctx.DynamicFlowMappingEvents
                    .Find(item =>
                        item.Id == receipt.EventId &&
                        item.ReceiptId == receipt.Id)
                    .FirstOrDefaultAsync(ct);
            outbox = receipt is null
                ? null
                : await _ctx.DynamicFlowMappingOutbox
                    .Find(item =>
                        item.Id == receipt.OutboxIntentId &&
                        item.ReceiptId == receipt.Id)
                    .FirstOrDefaultAsync(ct);
            payload = await _ctx.WorkReportPayloads
                .Find(item =>
                    item.ReportId == report.Id &&
                    !item.IsDeleted)
                .FirstOrDefaultAsync(ct);
        }
        else
        {
            receipt = await _ctx.DynamicFlowMappingApplyReceipts
                .Find(
                    session,
                    item =>
                        item.Id ==
                        report.DynamicFlowMappingReceiptId &&
                        item.TargetReportId == report.Id)
                .FirstOrDefaultAsync(ct);
            provenance = await _ctx.DynamicFlowMappingProvenanceRecords
                .Find(
                    session,
                    item =>
                        item.Id ==
                        report.DynamicFlowMappingProvenanceId &&
                        item.ReceiptId ==
                        report.DynamicFlowMappingReceiptId &&
                        item.TargetReportId == report.Id)
                .FirstOrDefaultAsync(ct);
            mappingEvent = receipt is null
                ? null
                : await _ctx.DynamicFlowMappingEvents
                    .Find(
                        session,
                        item =>
                            item.Id == receipt.EventId &&
                            item.ReceiptId == receipt.Id)
                    .FirstOrDefaultAsync(ct);
            outbox = receipt is null
                ? null
                : await _ctx.DynamicFlowMappingOutbox
                    .Find(
                        session,
                        item =>
                            item.Id == receipt.OutboxIntentId &&
                            item.ReceiptId == receipt.Id)
                    .FirstOrDefaultAsync(ct);
            payload = await _ctx.WorkReportPayloads
                .Find(
                    session,
                    item =>
                        item.ReportId == report.Id &&
                        !item.IsDeleted)
                .FirstOrDefaultAsync(ct);
        }

        if (receipt is null ||
            provenance is null ||
            mappingEvent is null ||
            outbox is null ||
            payload is null)
        {
            throw DynamicFlowMappingProvenanceTampered(
                report,
                "DYNAMIC_FLOW_MAPPING_DURABLE_WRITE_SET_MISSING");
        }

        var writeSetHash = ComputeDynamicFlowMappingObjectHash(
            new
            {
                receiptId = receipt.Id,
                provenanceId = provenance.Id,
                eventId = mappingEvent.Id,
                eventKey = mappingEvent.EventKey,
                eventPayloadHash = mappingEvent.PayloadHash,
                outboxId = outbox.Id,
                outboxDedupeKey = outbox.DedupeKey,
                intentHash = outbox.IntentHash,
                targetReportId = report.Id,
                resultPayloadRevision =
                    receipt.ResultPayloadRevision,
                resultPayloadHash = receipt.ResultPayloadHash,
                resultLifecycleRevision =
                    receipt.ResultLifecycleRevision,
                provenanceHash = provenance.ProvenanceHash
            });
        var durableHashesValid =
            string.Equals(
                receipt.ResultSnapshotHash,
                ComputeDynamicFlowMappingDocumentHash(
                    receipt.ResultSnapshot),
                StringComparison.Ordinal) &&
            string.Equals(
                provenance.ResultSnapshotHash,
                ComputeDynamicFlowMappingDocumentHash(
                    provenance.ResultSnapshot),
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ResultSnapshotHash,
                provenance.ResultSnapshotHash,
                StringComparison.Ordinal) &&
            string.Equals(
                provenance.ProvenanceHash,
                ComputeDynamicFlowMappingDocumentHash(
                    provenance.ProvenanceSnapshot),
                StringComparison.Ordinal) &&
            string.Equals(
                mappingEvent.PayloadHash,
                ComputeDynamicFlowMappingDocumentHash(
                    mappingEvent.Payload),
                StringComparison.Ordinal) &&
            string.Equals(
                outbox.IntentHash,
                ComputeDynamicFlowMappingIntentHash(outbox.Intent),
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.WriteSetHash,
                writeSetHash,
                StringComparison.Ordinal);
        var immutablePinsValid =
            string.Equals(
                ComputeDynamicFlowMappingObjectHash(
                    receipt.RuntimePin),
                ComputeDynamicFlowMappingObjectHash(
                    provenance.RuntimePin),
                StringComparison.Ordinal) &&
            string.Equals(
                ComputeDynamicFlowMappingObjectHash(
                    receipt.SourcePins),
                ComputeDynamicFlowMappingObjectHash(
                    provenance.SourcePins),
                StringComparison.Ordinal) &&
            provenance.SourcePins.All(
                pin => string.Equals(
                    pin.SourceFactHash,
                    ComputeDynamicFlowMappingSourceFactHash(pin),
                    StringComparison.Ordinal));
        var referencesValid =
            string.Equals(
                receipt.ProvenanceId,
                provenance.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ProvenanceHash,
                provenance.ProvenanceHash,
                StringComparison.Ordinal) &&
            string.Equals(
                report.DynamicFlowMappingProvenanceHash,
                provenance.ProvenanceHash,
                StringComparison.Ordinal) &&
            string.Equals(
                mappingEvent.ProvenanceId,
                provenance.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                mappingEvent.ProvenanceHash,
                provenance.ProvenanceHash,
                StringComparison.Ordinal) &&
            string.Equals(
                outbox.ProvenanceId,
                provenance.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                outbox.Intent.ProvenanceHash,
                provenance.ProvenanceHash,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.SourceSignature,
                provenance.SourceSignature,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ResultSemanticHash,
                provenance.ResultSemanticHash,
                StringComparison.Ordinal);
        var resultValid =
            receipt.ResultPayloadRevision ==
            provenance.TargetPayloadRevision &&
            receipt.ResultPayloadRevision ==
            report.DynamicFlowMappingResultPayloadRevision &&
            string.Equals(
                receipt.ResultPayloadHash,
                provenance.TargetPayloadHash,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ResultPayloadHash,
                report.DynamicFlowMappingResultPayloadHash,
                StringComparison.Ordinal) &&
            receipt.ResultLifecycleRevision ==
            provenance.TargetLifecycleRevision &&
            report.PayloadRevision >=
            receipt.ResultPayloadRevision &&
            (report.PayloadRevision !=
             receipt.ResultPayloadRevision ||
             string.Equals(
                 report.PayloadHash,
                 receipt.ResultPayloadHash,
                 StringComparison.Ordinal));
        var currentPayloadValid =
            payload.PayloadRevision == report.PayloadRevision &&
            string.Equals(
                payload.PayloadHash,
                report.PayloadHash,
                StringComparison.Ordinal) &&
            string.Equals(
                payload.Status,
                WorkReportPayloadStatus.Ready,
                StringComparison.Ordinal) &&
            IsDynamicFlowMappingSummary(payload.SummarySourceJson);
        if (!durableHashesValid ||
            !immutablePinsValid ||
            !referencesValid ||
            !resultValid ||
            !currentPayloadValid)
        {
            throw DynamicFlowMappingProvenanceTampered(
                report,
                "DYNAMIC_FLOW_MAPPING_PROVENANCE_INTEGRITY_FAILED");
        }
        if (!DynamicFlowMappingLifecycleContract.IsStateAllowed(
                provenance.State,
                mode))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_MAPPING_LIFECYCLE_CONFLICT,
                new
                {
                    reportId = report.Id,
                    receiptId = report.DynamicFlowMappingReceiptId,
                    provenanceId =
                        report.DynamicFlowMappingProvenanceId,
                    provenance.State,
                    integrityMode = mode.ToString(),
                    reason =
                        "DYNAMIC_FLOW_MAPPING_PROVENANCE_NOT_CURRENT"
                });
        }
    }

    private static string ComputeDynamicFlowMappingSourceFactHash(
        DynamicFlowMappingSourcePin pin)
        => ComputeDynamicFlowMappingObjectHash(
            new
            {
                sourceReportId = pin.SourceReportId,
                sourceAssignmentId = pin.SourceAssignmentId,
                sourceFlowInstanceId = pin.SourceFlowInstanceId,
                sourceExecutionEpoch = pin.SourceExecutionEpoch,
                sourceStepInstanceId = pin.SourceStepInstanceId,
                sourceStepId = pin.SourceStepId,
                sourceBranchId = pin.SourceBranchId,
                sourceAttemptNo = pin.SourceAttemptNo,
                sourceFormFamilyId = pin.SourceFormFamilyId,
                sourceFormVersionId = pin.SourceFormVersionId,
                sourceFormVersionNo = pin.SourceFormVersionNo,
                sourceFormSchemaHash = pin.SourceFormSchemaHash,
                sourcePayloadRevision = pin.SourcePayloadRevision,
                sourcePayloadHash = pin.SourcePayloadHash,
                sourceLifecycleRevision = pin.SourceLifecycleRevision,
                sourceLifecycleStatus = pin.SourceLifecycleStatus,
                sourcePeriodInstanceKey =
                    pin.SourcePeriodInstanceKey
            });

    private static AppException DynamicFlowMappingProvenanceTampered(
        WorkAssignmentReport report,
        string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_MAPPING_PROVENANCE_TAMPERED,
            new
            {
                reportId = report.Id,
                receiptId = report.DynamicFlowMappingReceiptId,
                provenanceId = report.DynamicFlowMappingProvenanceId,
                resultPayloadRevision =
                    report.DynamicFlowMappingResultPayloadRevision,
                reason
            });

    private static void
        EnsureDynamicFlowMappingProvenanceOverrideAllowed(
            WorkAssignmentReport report,
            string? requestedSummarySourceJson)
    {
        var mappingOwned =
            !string.IsNullOrWhiteSpace(
                report.DynamicFlowMappingReceiptId) ||
            !string.IsNullOrWhiteSpace(
                report.DynamicFlowMappingProvenanceId) ||
            IsDynamicFlowMappingSummary(report.SummarySourceJson);
        if (!mappingOwned ||
            requestedSummarySourceJson is null ||
            string.Equals(
                NormalizeOptionalTextOrNull(
                    requestedSummarySourceJson),
                NormalizeOptionalTextOrNull(
                    report.SummarySourceJson),
                StringComparison.Ordinal))
        {
            return;
        }

        throw DynamicFlowMappingProvenanceTampered(
            report,
            "DYNAMIC_FLOW_MAPPING_MANUAL_PROVENANCE_OVERRIDE");
    }

    private async Task ReconcileDynamicFlowMappingOutboxAsync(
        string outboxId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outboxId))
            return;

        try
        {
            _ = await _dynamicFlowMappingOutboxReconciler
                .ProcessByIdAsync(outboxId, ct);
        }
        catch (Exception error)
        {
            // The apply transaction already committed the immutable intent.
            // Foreground reconciliation is only a latency optimization; the
            // recurring leased worker owns eventual correctness.
            _log.LogWarning(
                error,
                "Dynamic Flow mapping foreground reconcile deferred to the durable worker. outboxId={OutboxId}",
                outboxId);
        }
    }

    private sealed record DynamicFlowMappingApplyCommand(
        string CommandId,
        int ExpectedPayloadRevision,
        string ExpectedPayloadHash,
        int ExpectedLifecycleRevision,
        string SourceSignature,
        string ResultSemanticHash,
        string RequestHash,
        string PreviewTokenHash);

    private sealed record DynamicFlowMappingPersistenceBundle(
        DynamicFlowMappingApplyReceipt Receipt,
        DynamicFlowMappingProvenanceRecord Provenance,
        DynamicFlowMappingEvent Event,
        DynamicFlowMappingOutboxItem Outbox,
        WorkAssignmentReportLog AuditLog);

    private sealed record DynamicFlowMappingTransactionOutcome(
        DynamicFlowMappingApplyReceipt Receipt,
        bool IsReplay);
}
