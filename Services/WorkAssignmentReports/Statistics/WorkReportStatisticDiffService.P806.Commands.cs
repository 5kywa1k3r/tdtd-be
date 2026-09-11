using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsConfiguration;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

/// <summary>
/// P8-06 configuration-only command surface. It has no statistic-value,
/// aggregate, result, delta, export, queue or materializer dependency.
/// </summary>
public sealed partial class WorkReportStatisticDiffService
{
    public async Task<WorkReportStatisticDiffConfigReadback>
        GetP8ConfigAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            CancellationToken ct)
    {
        var prepared = await P806PrepareAsync(
            assignmentId,
            dynamicFormTemplateId,
            P806Access.Read,
            ct);
        var owner = await P806LoadOwnerAsync(
            null,
            prepared,
            P806Access.Read,
            ct);
        var state = await P806LoadStateAsync(null, owner, ct);
        return P806ToReadback(
            owner,
            state,
            prepared.Me,
            receiptId: null);
    }

    public async Task<WorkReportStatisticDiffConfigVersionsResult>
        ListP8ConfigVersionsAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            CancellationToken ct)
    {
        var prepared = await P806PrepareAsync(
            assignmentId,
            dynamicFormTemplateId,
            P806Access.Read,
            ct);
        var owner = await P806LoadOwnerAsync(
            null,
            prepared,
            P806Access.Read,
            ct);
        var state = await P806LoadStateAsync(null, owner, ct);
        return new WorkReportStatisticDiffConfigVersionsResult(
            StatConfigOwnerKinds.Diff,
            P806OwnerId(owner),
            state.ConfigId,
            state.Rows
                .OrderBy(row => row.VersionNo)
                .Select(row => P806ToVersionDto(owner, row))
                .ToList());
    }

    public async Task<WorkReportStatisticDiffConfigReadback>
        GetP8ConfigVersionAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            int versionNo,
            CancellationToken ct)
    {
        var prepared = await P806PrepareAsync(
            assignmentId,
            dynamicFormTemplateId,
            P806Access.Read,
            ct);
        var owner = await P806LoadOwnerAsync(
            null,
            prepared,
            P806Access.Read,
            ct);
        var state = await P806LoadStateAsync(null, owner, ct);
        var selected = state.Rows.SingleOrDefault(
            row => row.VersionNo == versionNo);
        if (selected is null)
        {
            throw P806StateConflict(
                state,
                "DIFF_CONFIG_VERSION_NOT_FOUND");
        }
        return P806ToReadback(
            owner,
            P806SelectVersion(state, selected),
            prepared.Me,
            receiptId: null);
    }

    public async Task<WorkReportStatisticDiffConfigReadback>
        PutP8ConfigAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            JsonElement body,
            CancellationToken ct)
    {
        using var isolation =
            StatConfigIsolationGuard.EnterConfigurationMutation(
                StatConfigOwnerKinds.Diff);
        var prepared = await P806PrepareAsync(
            assignmentId,
            dynamicFormTemplateId,
            P806Access.Manage,
            ct);
        var command = NormalizeP806PutCommand(body);
        return await P806ExecuteCommandAsync(
            prepared,
            command,
            P806PutCommandKind,
            P806ApplyPutAsync,
            ct);
    }

    public async Task<WorkReportStatisticDiffConfigReadback>
        LockP8ConfigAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            JsonElement body,
            CancellationToken ct)
    {
        using var isolation =
            StatConfigIsolationGuard.EnterConfigurationMutation(
                StatConfigOwnerKinds.Diff);
        var prepared = await P806PrepareAsync(
            assignmentId,
            dynamicFormTemplateId,
            P806Access.Lock,
            ct);
        var command = NormalizeP806EmptyCommand(
            body,
            P806LockCommandKind);
        return await P806ExecuteCommandAsync(
            prepared,
            command,
            P806LockCommandKind,
            P806ApplyLockAsync,
            ct);
    }

    public async Task<WorkReportStatisticDiffConfigReadback>
        CreateNextP8DraftAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            JsonElement body,
            CancellationToken ct)
    {
        using var isolation =
            StatConfigIsolationGuard.EnterConfigurationMutation(
                StatConfigOwnerKinds.Diff);
        var prepared = await P806PrepareAsync(
            assignmentId,
            dynamicFormTemplateId,
            P806Access.Manage,
            ct);
        var command = NormalizeP806EmptyCommand(
            body,
            P806NextDraftCommandKind);
        return await P806ExecuteCommandAsync(
            prepared,
            command,
            P806NextDraftCommandKind,
            P806ApplyNextDraftAsync,
            ct);
    }

    private async Task<P806PreparedCommand> P806PrepareAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        P806Access access,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        assignmentId = P806NormalizeAssignmentId(assignmentId);
        _ = await P806LoadAuthorizedAssignmentAsync(
            null,
            assignmentId,
            me,
            access,
            ct);
        var templateId = P806NormalizeTemplateId(dynamicFormTemplateId);
        return new P806PreparedCommand(me, assignmentId, templateId, access);
    }

    private async Task<P806OwnerContext> P806LoadOwnerAsync(
        IClientSessionHandle? session,
        P806PreparedCommand prepared,
        P806Access access,
        CancellationToken ct)
    {
        var assignment = await P806LoadAuthorizedAssignmentAsync(
            session,
            prepared.AssignmentId,
            prepared.Me,
            access,
            ct);
        var template = await P806LoadTemplateAsync(
            session,
            assignment,
            prepared.TemplateId,
            ct);
        return new P806OwnerContext(assignment, template);
    }

    private async Task<WorkAssignment> P806LoadAuthorizedAssignmentAsync(
        IClientSessionHandle? session,
        string assignmentId,
        MeResponse me,
        P806Access access,
        CancellationToken ct)
    {
        var filter = P806AssignmentAuthorizationFilter(
            assignmentId,
            me,
            access);
        var assignment = session is null
            ? await _ctx.WorkAssignments.Find(filter).FirstOrDefaultAsync(ct)
            : await _ctx.WorkAssignments.Find(session, filter).FirstOrDefaultAsync(ct);
        if (assignment is not null)
            return assignment;
        throw AppExceptionFactory.Forbidden(
            AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_READ_FORBIDDEN,
            new
            {
                reason = access switch
                {
                    P806Access.Lock => "DIFF_CONFIG_LOCK_FORBIDDEN",
                    P806Access.Manage => "DIFF_CONFIG_MANAGE_FORBIDDEN",
                    _ => "DIFF_CONFIG_ACCESS_DENIED"
                }
            });
    }

    private static FilterDefinition<WorkAssignment>
        P806AssignmentAuthorizationFilter(
            string assignmentId,
            MeResponse me,
            P806Access access)
    {
        var filter = Builders<WorkAssignment>.Filter;
        var result =
            filter.Eq(item => item.Id, assignmentId) &
            filter.Eq(item => item.IsDeleted, false);
        if (RoleGuard.IsSystemAdmin(me))
            return result;
        if (access is P806Access.Manage or P806Access.Lock)
            return result & filter.Eq(item => item.CreatedByUserId, me.Id);
        return result & filter.Or(
            filter.Eq(item => item.CreatedByUserId, me.Id),
            filter.AnyEq(item => item.LeaderWatcherUserIds, me.Id),
            filter.ElemMatch(
                item => item.Assignees,
                assignee => assignee.UserId == me.Id));
    }

    private async Task<DynamicFormTemplate> P806LoadTemplateAsync(
        IClientSessionHandle? session,
        WorkAssignment assignment,
        string templateId,
        CancellationToken ct)
    {
        var filter = Builders<DynamicFormTemplate>.Filter;
        var query =
            filter.Eq(item => item.Id, templateId) &
            filter.Eq(item => item.IsDeleted, false) &
            filter.Eq(item => item.IsActive, true) &
            filter.Eq(item => item.IsPublished, true);
        var template = session is null
            ? await _ctx.DynamicFormTemplates.Find(query).FirstOrDefaultAsync(ct)
            : await _ctx.DynamicFormTemplates.Find(session, query).FirstOrDefaultAsync(ct);
        if (template is null ||
            !string.Equals(
                assignment.DynamicFormTemplateId,
                template.Id,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
                new { reason = "DIFF_CONFIG_TEMPLATE_NOT_FOUND" });
        }
        try
        {
            _ = DynamicFormPublishedSchemaSnapshotBuilder
                .ValidateAgainstTemplate(template);
        }
        catch (InvalidOperationException)
        {
            throw P806Integrity("DIFF_CONFIG_PUBLISHED_SCHEMA_INVALID");
        }
        return template;
    }

    private async Task<WorkReportStatisticDiffConfigReadback>
        P806ExecuteCommandAsync<TPayload>(
            P806PreparedCommand prepared,
            NormalizedStatConfigCommand<TPayload> command,
            string commandKind,
            P806ApplyCommand<TPayload> apply,
            CancellationToken ct)
    {
        var ownerId = P806OwnerId(
            prepared.AssignmentId,
            prepared.TemplateId);
        var receiptId = P806ReceiptId(ownerId, command.CommandId);
        try
        {
            return await _statConfigTransactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    var owner = await P806LoadOwnerAsync(
                        session,
                        prepared,
                        prepared.Access,
                        transactionCt);
                    var replay = await P806LoadReplayAsync(
                        session,
                        receiptId,
                        command.RequestHash,
                        transactionCt);
                    if (replay is not null)
                        return replay;
                    var current = await P806LoadStateAsync(
                        session,
                        owner,
                        transactionCt);
                    P806EnsureCas(current, command);
                    var now = P806UtcNowAtMillisecondPrecision();
                    var next = await apply(
                        session,
                        owner,
                        current,
                        command.Payload,
                        prepared.Me,
                        now,
                        transactionCt);
                    next = await P806PersistStateAsync(
                        session,
                        owner,
                        current,
                        next,
                        prepared.Me.Id,
                        now,
                        transactionCt);
                    var result = P806ToReadback(
                        owner,
                        next,
                        prepared.Me,
                        receiptId);
                    await _ctx.StatConfigCommandReceipts.InsertOneAsync(
                        session,
                        P806CreateReceipt(
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
            ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var receipt = await _ctx.StatConfigCommandReceipts
                .Find(item => item.Id == receiptId)
                .FirstOrDefaultAsync(ct);
            if (receipt is not null)
                return P806RestoreReplay(receipt, command.RequestHash);
            throw P806CasConflict(
                null,
                command,
                "DIFF_CONFIG_CONCURRENT_WRITE");
        }
        catch (AppException ex) when (
            ex.Code == AppErrorCode.STAT_CONFIG_CAS_CONFLICT)
        {
            var receipt = await _ctx.StatConfigCommandReceipts
                .Find(item => item.Id == receiptId)
                .FirstOrDefaultAsync(ct);
            if (receipt is not null)
                return P806RestoreReplay(receipt, command.RequestHash);
            throw;
        }
    }

    private async Task<WorkReportStatisticDiffConfigReadback?>
        P806LoadReplayAsync(
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
            : P806RestoreReplay(receipt, requestHash);
    }

    private static WorkReportStatisticDiffConfigReadback
        P806RestoreReplay(
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
                    reason = "DIFF_COMMAND_REPLAY_CHANGED"
                });
        }
        if (!string.Equals(
                receipt.ResponseHash,
                StatConfigCanonicalJson.HashUtf8(receipt.ResponseJson),
                StringComparison.Ordinal))
            throw P806Integrity("DIFF_RECEIPT_RESPONSE_HASH_MISMATCH");
        try
        {
            return JsonSerializer.Deserialize<
                       WorkReportStatisticDiffConfigReadback>(
                       receipt.ResponseJson,
                       StatConfigCanonicalJson.StrictJsonOptions) ??
                   throw new JsonException();
        }
        catch (JsonException)
        {
            throw P806Integrity("DIFF_RECEIPT_RESPONSE_SCHEMA_INVALID");
        }
    }

    private static StatConfigCommandReceipt P806CreateReceipt<TPayload>(
        string receiptId,
        string ownerId,
        string commandKind,
        NormalizedStatConfigCommand<TPayload> command,
        P806ConfigState state,
        WorkReportStatisticDiffConfigReadback response,
        string actorUserId,
        DateTime now)
    {
        var responseJson = StatConfigCanonicalJson.Canonicalize(response);
        return new StatConfigCommandReceipt
        {
            Id = receiptId,
            OwnerKind = StatConfigOwnerKinds.Diff,
            OwnerId = ownerId,
            CommandKind = commandKind,
            CommandId = command.CommandId,
            RequestHash = command.RequestHash,
            ResponseJson = responseJson,
            ResponseHash = StatConfigCanonicalJson.HashUtf8(responseJson),
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

    private static void P806EnsureCas<TPayload>(
        P806ConfigState current,
        NormalizedStatConfigCommand<TPayload> command)
    {
        if (current.Revision != command.ExpectedRevision ||
            !string.Equals(
                current.ConfigHash,
                command.ExpectedConfigHash,
                StringComparison.Ordinal))
        {
            throw P806CasConflict(
                current,
                command,
                "DIFF_CONFIG_CAS_MISMATCH");
        }
    }

    private static AppException P806CasConflict<TPayload>(
        P806ConfigState? current,
        NormalizedStatConfigCommand<TPayload> command,
        string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                expectedRevision = command.ExpectedRevision,
                expectedConfigHash = command.ExpectedConfigHash,
                actualRevision = current?.Revision,
                actualConfigHash = current?.ConfigHash,
                actualStatus = current?.Status,
                reason
            });

    private static AppException P806StateConflict(
        P806ConfigState current,
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

    private static AppException P806Integrity(string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new { reason });

    private static string P806NormalizeAssignmentId(string? value)
    {
        var normalized = value?.Trim();
        if (!ObjectId.TryParse(normalized, out var parsed) ||
            !string.Equals(normalized, parsed.ToString(), StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_READ_FORBIDDEN,
                new { reason = "DIFF_CONFIG_ACCESS_DENIED" });
        }
        return normalized;
    }

    private static string P806NormalizeTemplateId(string? value)
    {
        var normalized = value?.Trim();
        if (!ObjectId.TryParse(normalized, out var parsed) ||
            !string.Equals(normalized, parsed.ToString(), StringComparison.Ordinal))
        {
            throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
                new { reason = "DIFF_CONFIG_TEMPLATE_NOT_FOUND" });
        }
        return normalized;
    }

    internal static string P806OwnerId(
        string assignmentId,
        string templateId)
        => $"{assignmentId}:{templateId}";

    private static string P806OwnerId(P806OwnerContext owner)
        => P806OwnerId(owner.Assignment.Id, owner.Template.Id);

    internal static string P806ReceiptId(
        string ownerId,
        string commandId)
        => StatConfigCanonicalJson.HashUtf8(
            $"{StatConfigOwnerKinds.Diff}\0{ownerId}\0{commandId}");

    internal static string P806DeterministicId(
        string ownerId,
        string purpose)
        => StatConfigCanonicalJson.HashUtf8(
                $"{StatConfigOwnerKinds.Diff}\0{ownerId}\0{purpose}")
            [..24];

    private static DateTime P806UtcNowAtMillisecondPrecision()
    {
        var now = DateTime.UtcNow;
        return new DateTime(
            now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
    }

    private delegate Task<P806ConfigState> P806ApplyCommand<TPayload>(
        IClientSessionHandle session,
        P806OwnerContext owner,
        P806ConfigState current,
        TPayload payload,
        MeResponse me,
        DateTime now,
        CancellationToken ct);

    private sealed record P806PreparedCommand(
        MeResponse Me,
        string AssignmentId,
        string TemplateId,
        P806Access Access);

    private enum P806Access
    {
        Read,
        Manage,
        Lock
    }

    private sealed record P806OwnerContext(
        WorkAssignment Assignment,
        DynamicFormTemplate Template);
}
