using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Controllers;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.SummaryTokens;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignments.SummaryTokens;

internal static class WorkSummaryTokenP805ContractTests
{
    private const string EmptyHash =
        "e3b0c44298fc1c149afbf4c8996fb924" +
        "27ae41e4649b934ca495991b7852b855";

    public static void Run()
    {
        StrictGrantEnvelopeRejectsCallerOwnedQuotaFields();
        StrictCompensationEnvelopeRejectsCallerOwnedLinkage();
        PublicQuotaAndMutationResponsesExposeExactPoolState();
        ConsumeContractRequiresCallerSessionAndDurableIdentity();
        LedgerKindsAndBsonShapePreserveLegacyEntries();
        QuotaMathAndCommandKindsAreFrozen();
        StrictMutationRoutesArePublished();
    }

    private static void StrictGrantEnvelopeRejectsCallerOwnedQuotaFields()
    {
        using var valid = JsonDocument.Parse(
            "{\"commandId\":\"grant-1\",\"expectedRevision\":0," +
            "\"expectedConfigHash\":\"" + EmptyHash + "\"," +
            "\"payload\":{\"units\":2,\"reason\":\"capacity\"}}");
        var envelope = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<
                WorkSummaryTokenGrantP8Payload>>(valid.RootElement);
        var normalized = StatConfigCanonicalJson.NormalizeCommand(
            envelope,
            StatConfigCommandKinds.GrantWorkSummaryTokenQuota);
        AssertEqual("grant-1", normalized.CommandId, "grant command id");
        AssertEqual(2, normalized.Payload.Units, "grant units");

        foreach (var forged in new[]
                 {
                     "\"ownerUnitId\":\"100000000000000000000001\"," ,
                     "\"tokenKind\":\"ADVANCED_SUMMARY_CONFIG_LOCK\"," ,
                     "\"periodMonthKey\":\"2026-08\"," ,
                     "\"monthlyQuota\":999," ,
                     "\"baseMonthlyQuota\":999,"
                 })
        {
            using var document = JsonDocument.Parse(
                "{\"commandId\":\"grant-1\",\"expectedRevision\":0," +
                "\"expectedConfigHash\":\"" + EmptyHash + "\"," +
                "\"payload\":{" + forged +
                "\"units\":2,\"reason\":\"capacity\"}}");
            ExpectSchema(
                () => StatConfigCanonicalJson.DeserializeStrict<
                    StatConfigMutationEnvelope<
                        WorkSummaryTokenGrantP8Payload>>(
                        document.RootElement),
                $"forged grant field {forged}");
        }

        AssertPropertyNames(
            typeof(WorkSummaryTokenGrantP8Payload),
            ["Units", "Reason"]);
    }

    private static void StrictCompensationEnvelopeRejectsCallerOwnedLinkage()
    {
        using var valid = JsonDocument.Parse(
            "{\"commandId\":\"compensate-1\"," +
            "\"expectedRevision\":4," +
            "\"expectedConfigHash\":\"" + new string('a', 64) + "\"," +
            "\"payload\":{\"reason\":\"owner write aborted\"}}");
        var envelope = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<
                WorkSummaryTokenCompensationP8Payload>>(
                valid.RootElement);
        var normalized = StatConfigCanonicalJson.NormalizeCommand(
            envelope,
            StatConfigCommandKinds
                .CompensateAdvancedSummaryConfigLockToken);
        AssertEqual(
            "compensate-1",
            normalized.CommandId,
            "compensation command id");

        foreach (var field in new[]
                 {
                     "compensatedLedgerId",
                     "ownerUnitId",
                     "units",
                     "configId",
                     "tokenKind"
                 })
        {
            using var forged = JsonDocument.Parse(
                "{\"commandId\":\"compensate-1\"," +
                "\"expectedRevision\":4," +
                "\"expectedConfigHash\":\"" + new string('a', 64) +
                "\",\"payload\":{\"reason\":\"repair\",\"" +
                field + "\":\"forged\"}}");
            ExpectSchema(
                () => StatConfigCanonicalJson.DeserializeStrict<
                    StatConfigMutationEnvelope<
                        WorkSummaryTokenCompensationP8Payload>>(
                        forged.RootElement),
                $"forged compensation field {field}");
        }

        AssertPropertyNames(
            typeof(WorkSummaryTokenCompensationP8Payload),
            ["Reason"]);
    }

    private static void PublicQuotaAndMutationResponsesExposeExactPoolState()
    {
        AssertPropertyNames(
            typeof(WorkSummaryTokenQuotaResponse),
            [
                "PoolId",
                "Revision",
                "PoolHash",
                "OwnerUnitId",
                "TokenKind",
                "PeriodMonthKey",
                "BaseMonthlyQuota",
                "GrantedUnits",
                "UsedUnits",
                "MonthlyQuota",
                "RemainingUnits"
            ]);
        AssertPropertyNames(
            typeof(WorkSummaryTokenGrantResponse),
            [
                "LedgerId",
                "CommandReceiptId",
                "OwnerUnitId",
                "IssuerUserId",
                "TokenKind",
                "PeriodMonthKey",
                "Units",
                "PoolRevision",
                "PoolHash",
                "Quota",
                "CreatedAtUtc"
            ]);
        AssertPropertyNames(
            typeof(WorkSummaryTokenCompensationResponse),
            [
                "CompensationLedgerId",
                "CommandReceiptId",
                "CompensatedLedgerId",
                "OwnerUnitId",
                "TokenKind",
                "PeriodMonthKey",
                "Units",
                "PoolRevision",
                "PoolHash",
                "Quota",
                "CreatedAtUtc"
            ]);
    }

    private static void ConsumeContractRequiresCallerSessionAndDurableIdentity()
    {
        var method = typeof(IWorkSummaryTokenService).GetMethod(
            nameof(IWorkSummaryTokenService
                .ConsumeAdvancedConfigLockP8Async))
            ?? throw new InvalidOperationException(
                "P8 consume contract is missing.");
        AssertEqual(
            typeof(Task<WorkSummaryTokenConsumeResult>),
            method.ReturnType,
            "consume return type");
        AssertSequenceEqual(
            new[]
            {
                typeof(IClientSessionHandle),
                typeof(WorkAssignmentAdvancedSummaryConfig),
                typeof(string),
                typeof(bool),
                typeof(string),
                typeof(string),
                typeof(CancellationToken)
            },
            method.GetParameters().Select(x => x.ParameterType),
            "consume parameter types");
        AssertSequenceEqual(
            new[]
            {
                "session",
                "config",
                "ownerUnitId",
                "isFree",
                "requestTokenId",
                "actorUserId",
                "ct"
            },
            method.GetParameters().Select(x => x.Name!),
            "consume parameter names");
        AssertPropertyNames(
            typeof(WorkSummaryTokenConsumeResult),
            [
                "LedgerId",
                "CommandReceiptId",
                "PoolId",
                "OwnerUnitId",
                "TokenKind",
                "PeriodMonthKey",
                "Units",
                "BaseMonthlyQuota",
                "GrantedUnits",
                "MonthlyQuota",
                "UsedBefore",
                "UsedAfter",
                "PoolRevision",
                "PoolHash",
                "IsFree"
            ]);
    }

    private static void LedgerKindsAndBsonShapePreserveLegacyEntries()
    {
        AssertEqual("ENTRY", WorkSummaryTokenLedgerRecordKinds.Entry, "entry kind");
        AssertEqual("POOL", WorkSummaryTokenLedgerRecordKinds.Pool, "pool kind");
        AssertEqual("COMPENSATE", WorkSummaryTokenDirections.Compensate, "compensate direction");

        var pool = new WorkSummaryTokenLedger
        {
            Id = "100000000000000000000001",
            RecordKind = WorkSummaryTokenLedgerRecordKinds.Pool,
            OwnerUnitId = "200000000000000000000001",
            ActorUserId = "300000000000000000000001",
            PoolHash = new string('b', 64)
        };
        var document = pool.ToBsonDocument();
        AssertEqual("POOL", document["recordKind"].AsString, "stored pool kind");
        AssertEqual(new string('b', 64), document["poolHash"].AsString, "stored pool hash");
        if (!document.Contains("configHash") || !document["configHash"].IsBsonNull)
            throw new InvalidOperationException(
                "POOL configHash must remain separate from poolHash.");

        var legacyDocument = new BsonDocument
        {
            { "_id", MongoDB.Bson.ObjectId.Parse(
                "400000000000000000000001") },
            { "actorUserId", MongoDB.Bson.ObjectId.Parse(
                "500000000000000000000001") },
            { "tokenKind", WorkSummaryTokenKinds.AdvancedSummaryConfigLock },
            { "direction", WorkSummaryTokenDirections.Consume },
            { "units", 1 },
            { "periodMonthKey", "2026-08" },
            { "outcome", WorkSummaryTokenOutcomes.Success },
            { "isDeleted", false }
        };
        var legacy = BsonSerializer.Deserialize<
            WorkSummaryTokenLedger>(legacyDocument);
        AssertEqual(
            WorkSummaryTokenLedgerRecordKinds.Entry,
            legacy.RecordKind,
            "legacy row defaults to entry");
    }

    private static void QuotaMathAndCommandKindsAreFrozen()
    {
        AssertEqual(0, WorkSummaryTokenService.CalculateBaseQuotaFromActiveUsers(-1), "negative active users");
        AssertEqual(17, WorkSummaryTokenService.CalculateBaseQuotaFromActiveUsers(17), "active user quota");
        AssertEqual(21, WorkSummaryTokenService.CalculateAllowance(17, 4), "grant allowance");
        AssertEqual(false, WorkSummaryTokenService.WouldExceedQuota(20, 1, 21), "quota N");
        AssertEqual(true, WorkSummaryTokenService.WouldExceedQuota(21, 1, 21), "quota N plus one");
        AssertEqual(false, WorkSummaryTokenService.WouldExceedQuota(21, 0, 21), "free lock");

        AssertEqual(
            "GRANT_WORK_SUMMARY_TOKEN_QUOTA",
            StatConfigCommandKinds.GrantWorkSummaryTokenQuota,
            "grant command kind");
        AssertEqual(
            "CONSUME_ADVANCED_SUMMARY_CONFIG_LOCK_TOKEN",
            StatConfigCommandKinds.ConsumeAdvancedSummaryConfigLockToken,
            "consume command kind");
        AssertEqual(
            "COMPENSATE_ADVANCED_SUMMARY_CONFIG_LOCK_TOKEN",
            StatConfigCommandKinds
                .CompensateAdvancedSummaryConfigLockToken,
            "compensation command kind");
    }

    private static void StrictMutationRoutesArePublished()
    {
        AssertHttpPostTemplate(
            nameof(WorkSummaryTokensController.GrantP8),
            "pools/{ownerUnitId}/grants");
        AssertHttpPostTemplate(
            nameof(WorkSummaryTokensController.CompensateP8),
            "entries/{ledgerId}/compensations");
    }

    private static void AssertHttpPostTemplate(
        string methodName,
        string expected)
    {
        var method = typeof(WorkSummaryTokensController).GetMethod(methodName)
            ?? throw new InvalidOperationException(
                $"Controller method {methodName} is missing.");
        var route = method.GetCustomAttribute<HttpPostAttribute>()
            ?? throw new InvalidOperationException(
                $"Controller method {methodName} is not POST.");
        AssertEqual(expected, route.Template, $"route {methodName}");
    }

    private static void ExpectSchema(Action action, string context)
    {
        try
        {
            action();
        }
        catch (AppException error)
        {
            AssertEqual(AppErrorCode.STAT_CONFIG_SCHEMA_INVALID, error.Code, context);
            return;
        }

        throw new InvalidOperationException(
            $"{context}: expected schema error.");
    }

    private static void AssertPropertyNames(Type type, string[] expected)
        => AssertSequenceEqual(
            expected.OrderBy(x => x, StringComparer.Ordinal),
            type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(x => x.Name)
                .OrderBy(x => x, StringComparer.Ordinal),
            $"public shape {type.Name}");

    private static void AssertSequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string context)
    {
        var expectedArray = expected.ToArray();
        var actualArray = actual.ToArray();
        if (!expectedArray.SequenceEqual(actualArray))
        {
            throw new InvalidOperationException(
                $"{context}: expected [{string.Join(",", expectedArray)}], " +
                $"actual [{string.Join(",", actualArray)}].");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{context}: expected {expected}, actual {actual}.");
        }
    }
}
