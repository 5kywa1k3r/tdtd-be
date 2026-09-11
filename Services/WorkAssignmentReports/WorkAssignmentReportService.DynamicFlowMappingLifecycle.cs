using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.Services.WorkAssignmentReports;

public sealed partial class WorkAssignmentReportService
{
    private Task<DynamicFlowMappingLifecycleBinding?>
        ValidateDynamicFlowMappingLifecycleBoundaryAsync(
            WorkAssignmentReport report,
            CancellationToken ct,
            DynamicFlowMappingIntegrityMode mode =
                DynamicFlowMappingIntegrityMode.RequireCurrent,
            IClientSessionHandle? session = null)
        => DynamicFlowMappingLifecycleContract.ValidateAsync(
            _ctx,
            report,
            mode,
            ct,
            session);

    private async Task<DynamicFlowMappingLifecycleBinding?>
        ValidateDynamicFlowMappingLifecycleMutationBoundaryAsync(
            WorkAssignmentReport report,
            string operation,
            CancellationToken ct,
            DynamicFlowMappingIntegrityMode mode =
                DynamicFlowMappingIntegrityMode.RequireCurrent,
            IClientSessionHandle? session = null)
    {
        var canLifecycleMapping =
            ResolveDynamicFlowMappingActivation()
                .CanLifecycleMapping;
        var binding =
            await DynamicFlowMappingLifecycleContract.ValidateAsync(
                _ctx,
                report,
                mode,
                ct,
                session,
                canLifecycleMapping: canLifecycleMapping,
                lifecycleOperation: operation);
        await DynamicFlowMappingLifecycleContract
            .EnsureSourceMutationPhaseAsync(
                _ctx,
                report.Id,
                canLifecycleMapping,
                ct);
        return binding;
    }

    private async Task<DynamicFlowMappingSuccessorPlan>
        BuildDynamicFlowMappingSuccessorAsync(
            WorkAssignmentReport report,
            CancellationToken ct)
    {
        var predecessor =
            await ValidateDynamicFlowMappingLifecycleBoundaryAsync(
                report,
                ct,
                DynamicFlowMappingIntegrityMode.AllowHistorical);
        if (predecessor is not null)
        {
            DynamicFlowMappingLifecycleContract.EnsureP7LifecyclePhase(
                ResolveDynamicFlowMappingActivation()
                    .CanLifecycleMapping,
                "APPLY_DYNAMIC_FLOW_MAPPING_RERUN",
                DynamicFlowMappingLifecycleContract
                    .RerunBlockedReason,
                reportId: report.Id);
        }

        return DynamicFlowMappingLifecycleContract.BuildSuccessorPlan(
            predecessor,
            ObjectId.GenerateNewId().ToString());
    }

    private async Task CommitDynamicFlowMappingSuccessorAsync(
        IClientSessionHandle session,
        DynamicFlowMappingSuccessorPlan plan,
        DateTime committedAtUtc,
        CancellationToken ct)
    {
        if (!plan.IsRerun)
            return;

        var predecessorCommit =
            await _ctx.DynamicFlowMappingProvenanceRecords.UpdateOneAsync(
                session,
                item =>
                    item.Id == plan.PredecessorProvenanceId &&
                    item.ReceiptId == plan.PredecessorReceiptId &&
                    new[]
                    {
                        DynamicFlowMappingProvenanceStates.Current,
                        DynamicFlowMappingProvenanceStates.Invalidated
                    }.Contains(item.State) &&
                    item.SupersededByProvenanceId == null,
                Builders<DynamicFlowMappingProvenanceRecord>.Update
                    .Set(
                        item => item.State,
                        DynamicFlowMappingProvenanceStates.Superseded)
                    .Set(
                        item => item.SupersededByProvenanceId,
                        plan.SuccessorProvenanceId)
                    .Set(
                        item => item.InvalidationReason,
                        DynamicFlowMappingLifecycleContract
                            .SupersededByRerunReason)
                    .Set(item => item.InvalidatedAtUtc, committedAtUtc),
                cancellationToken: ct);
        if (predecessorCommit.ModifiedCount == 1)
            return;

        throw AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_MAPPING_TARGET_REVISION_CONFLICT,
            new
            {
                receiptId = plan.PredecessorReceiptId,
                provenanceId = plan.PredecessorProvenanceId,
                successorProvenanceId =
                    plan.SuccessorProvenanceId,
                reason =
                    "DYNAMIC_FLOW_MAPPING_SUCCESSOR_CAS_LOST"
            });
    }
}
