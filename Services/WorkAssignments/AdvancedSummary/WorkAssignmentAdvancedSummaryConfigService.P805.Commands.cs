using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Models.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

/// <summary>
/// P8-05 configuration-only command path. It deliberately has no report,
/// preview, hierarchy, snapshot, materializer or background-job operation.
/// </summary>
public sealed partial class WorkAssignmentAdvancedSummaryConfigService
{
    public async Task<WorkAssignmentAdvancedSummaryConfigReadback>
        PutP8ConfigAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            string sectionId,
            JsonElement body,
            CancellationToken ct)
    {
        using var isolation =
            StatConfigIsolationGuard.EnterConfigurationMutation(
                StatConfigOwnerKinds.AdvancedSummary);
        var prepared = await P805AuthorizeBeforeBodyAsync(
            assignmentId,
            dynamicFormTemplateId,
            sectionId,
            ct);
        var command = NormalizeP805PutCommand(
            body,
            prepared.SectionId);
        return await P805ExecuteCommandAsync(
            prepared,
            command,
            P805PutCommandKind,
            P805ApplyPutAsync,
            ct);
    }

    public async Task<WorkAssignmentAdvancedSummaryConfigReadback>
        LockP8ConfigAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            string sectionId,
            JsonElement body,
            CancellationToken ct)
    {
        using var isolation =
            StatConfigIsolationGuard.EnterConfigurationMutation(
                StatConfigOwnerKinds.AdvancedSummary);
        var prepared = await P805AuthorizeBeforeBodyAsync(
            assignmentId,
            dynamicFormTemplateId,
            sectionId,
            ct);
        var command = NormalizeP805EmptyCommand(
            body,
            P805LockCommandKind);
        return await P805ExecuteCommandAsync(
            prepared,
            command,
            P805LockCommandKind,
            P805ApplyLockAsync,
            ct);
    }

    public async Task<WorkAssignmentAdvancedSummaryConfigReadback>
        CreateNextP8DraftAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            string sectionId,
            JsonElement body,
            CancellationToken ct)
    {
        using var isolation =
            StatConfigIsolationGuard.EnterConfigurationMutation(
                StatConfigOwnerKinds.AdvancedSummary);
        var prepared = await P805AuthorizeBeforeBodyAsync(
            assignmentId,
            dynamicFormTemplateId,
            sectionId,
            ct);
        var command = NormalizeP805EmptyCommand(
            body,
            P805NextDraftCommandKind);
        return await P805ExecuteCommandAsync(
            prepared,
            command,
            P805NextDraftCommandKind,
            P805ApplyNextDraftAsync,
            ct);
    }

    public async Task<WorkAssignmentAdvancedSummaryConfigReadback>
        ArchiveP8ConfigAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            string sectionId,
            JsonElement body,
            CancellationToken ct)
    {
        using var isolation =
            StatConfigIsolationGuard.EnterConfigurationMutation(
                StatConfigOwnerKinds.AdvancedSummary);
        var prepared = await P805AuthorizeBeforeBodyAsync(
            assignmentId,
            dynamicFormTemplateId,
            sectionId,
            ct);
        var command = NormalizeP805EmptyCommand(
            body,
            P805ArchiveCommandKind);
        return await P805ExecuteCommandAsync(
            prepared,
            command,
            P805ArchiveCommandKind,
            P805ApplyArchiveAsync,
            ct);
    }

    private async Task<P805PreparedCommand>
        P805AuthorizeBeforeBodyAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            string sectionId,
            CancellationToken ct)
    {
        var me = _me.RequireMe();
        assignmentId = P805NormalizeAssignmentId(assignmentId);
        _ = await P805LoadAuthorizedAssignmentAsync(
            null,
            assignmentId,
            me,
            requireManage: true,
            ct);
        var templateId =
            P805NormalizeTemplateId(dynamicFormTemplateId);
        sectionId = P805RequiredIdentity(
            sectionId,
            "$.route.sectionId",
            "SECTION_ID_REQUIRED");
        return new P805PreparedCommand(
            me,
            assignmentId,
            templateId,
            sectionId);
    }

    private async Task<WorkAssignmentAdvancedSummaryConfigReadback>
        P805ExecuteCommandAsync<TPayload>(
            P805PreparedCommand prepared,
            NormalizedStatConfigCommand<TPayload> command,
            string commandKind,
            P805ApplyCommand<TPayload> apply,
            CancellationToken ct)
    {
        var ownerId = P805OwnerId(
            prepared.AssignmentId,
            prepared.TemplateId,
            prepared.SectionId);
        var receiptId =
            P805ReceiptId(ownerId, command.CommandId);
        try
        {
            return await _statConfigTransactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    _ = await P805LoadAuthorizedAssignmentAsync(
                        session,
                        prepared.AssignmentId,
                        prepared.Me,
                        requireManage: true,
                        transactionCt);
                    var replay = await P805LoadReplayAsync(
                        session,
                        receiptId,
                        command.RequestHash,
                        transactionCt);
                    if (replay is not null)
                        return replay;

                    var owner = await P805LoadOwnerContextAsync(
                        session,
                        prepared.AssignmentId,
                        prepared.TemplateId,
                        prepared.SectionId,
                        prepared.Me,
                        requireManage: true,
                        transactionCt);
                    var current = await P805LoadStateAsync(
                        session,
                        owner,
                        transactionCt);
                    P805EnsureCas(current, command);
                    var now =
                        P805UtcNowAtMillisecondPrecision();
                    var next = await apply(
                        session,
                        owner,
                        current,
                        command.Payload,
                        prepared.Me,
                        command.CommandId,
                        now,
                        transactionCt);
                    var result = P805ToReadback(
                        owner,
                        next,
                        prepared.Me,
                        receiptId);
                    await _ctx.StatConfigCommandReceipts
                        .InsertOneAsync(
                            session,
                            P805CreateReceipt(
                                receiptId,
                                ownerId,
                                commandKind,
                                command,
                                next,
                                result,
                                prepared.Me.Id,
                                now),
                            cancellationToken: transactionCt);
                    return result;
                },
                ct);
        }
        catch (MongoWriteException ex) when (
            ex.WriteError?.Category ==
            ServerErrorCategory.DuplicateKey)
        {
            var receipt = await _ctx.StatConfigCommandReceipts
                .Find(item => item.Id == receiptId)
                .FirstOrDefaultAsync(ct);
            if (receipt is not null)
            {
                return P805RestoreReplay(
                    receipt,
                    command.RequestHash);
            }
            throw P805CasConflict(
                null,
                command,
                "ADVANCED_SUMMARY_CONFIG_CONCURRENT_WRITE");
        }
        catch (AppException ex) when (
            ex.Code == AppErrorCode.STAT_CONFIG_CAS_CONFLICT)
        {
            var receipt = await _ctx.StatConfigCommandReceipts
                .Find(item => item.Id == receiptId)
                .FirstOrDefaultAsync(ct);
            if (receipt is not null)
            {
                return P805RestoreReplay(
                    receipt,
                    command.RequestHash);
            }
            throw;
        }
    }

    private async Task<
        WorkAssignmentAdvancedSummaryConfigReadback?>
        P805LoadReplayAsync(
            IClientSessionHandle session,
            string receiptId,
            string requestHash,
            CancellationToken ct)
    {
        var receipt = await _ctx.StatConfigCommandReceipts
            .Find(session, item => item.Id == receiptId)
            .FirstOrDefaultAsync(ct);
        return receipt is null
            ? null
            : P805RestoreReplay(receipt, requestHash);
    }

    private static WorkAssignmentAdvancedSummaryConfigReadback
        P805RestoreReplay(
            StatConfigCommandReceipt receipt,
            string requestHash)
    {
        if (!string.Equals(
                receipt.RequestHash,
                requestHash,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_COMMAND_REPLAY_CONFLICT,
                new
                {
                    ownerKind = receipt.OwnerKind,
                    ownerId = receipt.OwnerId,
                    commandId = receipt.CommandId,
                    reason =
                        "ADVANCED_SUMMARY_COMMAND_REPLAY_CHANGED"
                });
        }
        if (!string.Equals(
                receipt.ResponseHash,
                StatConfigCanonicalJson.HashUtf8(
                    receipt.ResponseJson),
                StringComparison.Ordinal))
        {
            throw P805Integrity(
                "ADVANCED_SUMMARY_RECEIPT_RESPONSE_HASH_MISMATCH");
        }
        try
        {
            return JsonSerializer.Deserialize<
                       WorkAssignmentAdvancedSummaryConfigReadback>(
                       receipt.ResponseJson,
                       StatConfigCanonicalJson.StrictJsonOptions) ??
                   throw new JsonException();
        }
        catch (JsonException)
        {
            throw P805Integrity(
                "ADVANCED_SUMMARY_RECEIPT_RESPONSE_SCHEMA_INVALID");
        }
    }

    private static StatConfigCommandReceipt
        P805CreateReceipt<TPayload>(
            string receiptId,
            string ownerId,
            string commandKind,
            NormalizedStatConfigCommand<TPayload> command,
            P805ConfigState state,
            WorkAssignmentAdvancedSummaryConfigReadback response,
            string actorUserId,
            DateTime now)
    {
        var responseJson =
            StatConfigCanonicalJson.Canonicalize(response);
        return new StatConfigCommandReceipt
        {
            Id = receiptId,
            OwnerKind = StatConfigOwnerKinds.AdvancedSummary,
            OwnerId = ownerId,
            CommandKind = commandKind,
            CommandId = command.CommandId,
            RequestHash = command.RequestHash,
            ResponseJson = responseJson,
            ResponseHash =
                StatConfigCanonicalJson.HashUtf8(responseJson),
            ResultConfigId = state.ConfigId,
            ResultVersionId = state.VersionId,
            ResultVersionNo = state.VersionNo,
            ResultRevision = state.Revision,
            ResultStatus = state.Status,
            ResultConfigHash = state.ConfigHash,
            ActorUserId = actorUserId,
            CreatedAtUtc = now
        };
    }

    private static void P805EnsureCas<TPayload>(
        P805ConfigState current,
        NormalizedStatConfigCommand<TPayload> command)
    {
        if (current.Revision != command.ExpectedRevision ||
            !string.Equals(
                current.ConfigHash,
                command.ExpectedConfigHash,
                StringComparison.Ordinal))
        {
            throw P805CasConflict(
                current,
                command,
                "ADVANCED_SUMMARY_CONFIG_CAS_MISMATCH");
        }
    }

    private static AppException P805CasConflict<TPayload>(
        P805ConfigState? current,
        NormalizedStatConfigCommand<TPayload> command,
        string reason)
        => P805CasConflict(
            current,
            command.ExpectedRevision,
            command.ExpectedConfigHash,
            reason);

    private static AppException P805CasConflict(
        P805ConfigState? current,
        long expectedRevision,
        string expectedConfigHash,
        string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                expectedRevision,
                expectedConfigHash,
                actualRevision = current?.Revision,
                actualConfigHash = current?.ConfigHash,
                reason
            });

    private static AppException P805StateConflict(
        P805ConfigState current,
        string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                actualRevision = current.Revision,
                actualConfigHash = current.ConfigHash,
                actualStatus = current.Status,
                reason
            });

    internal static string P805ReceiptId(
        string ownerId,
        string commandId)
        => StatConfigCanonicalJson.HashUtf8(
            $"{StatConfigOwnerKinds.AdvancedSummary}\0" +
            $"{ownerId}\0{commandId}");

    private static DateTime P805UtcNowAtMillisecondPrecision()
    {
        var now = DateTime.UtcNow;
        return new DateTime(
            now.Ticks -
            now.Ticks % TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
    }

    private delegate Task<P805ConfigState>
        P805ApplyCommand<TPayload>(
            IClientSessionHandle session,
            P805OwnerContext owner,
            P805ConfigState current,
            TPayload payload,
            MeResponse me,
            string commandId,
            DateTime now,
            CancellationToken ct);

    private sealed record P805PreparedCommand(
        MeResponse Me,
        string AssignmentId,
        string TemplateId,
        string SectionId);
}
