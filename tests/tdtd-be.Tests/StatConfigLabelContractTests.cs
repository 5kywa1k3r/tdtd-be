using System.Text.Json;
using MongoDB.Bson;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Labels;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;

internal static class StatConfigLabelContractTests
{
    private const string EmptyHash =
        "74234e98afe7498fb5daf1f36ac2d78acc339464f950703b8c019892f982b90b";

    public static void Run()
    {
        StrictEnvelopeRejectsUnknownDuplicateAndUndefinedJson();
        CanonicalHashSortsObjectsAndNormalizesOnlySetArrays();
        LabelTaxonomyRejectsEveryLegacyFallback();
        FourLabelLayersRejectCrossLayerUsage();
        LockedSnapshotsPinLabelIdentityAndLineage();
        SourceFreezesAuthorizationCasReceiptAndReservation();
        ConfigRoutesStayIsolatedFromResultMaterializers();
        StableErrorsExposeTheFrozenStatuses();
    }

    private static void StrictEnvelopeRejectsUnknownDuplicateAndUndefinedJson()
    {
        using var valid = JsonDocument.Parse(
            "{\"commandId\":\" label-create-1 \",\"expectedRevision\":0," +
            "\"expectedConfigHash\":\"" + EmptyHash.ToUpperInvariant() +
            "\",\"payload\":{}}");
        var envelope = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<LabelTombstonePayload>>(
            valid.RootElement);
        var normalized = StatConfigCanonicalJson.NormalizeCommand(
            envelope,
            StatConfigCommandKinds.TombstoneLabel);

        AssertEqual("label-create-1", normalized.CommandId, "commandId trim");
        AssertEqual(0L, normalized.ExpectedRevision, "expected revision");
        AssertEqual(EmptyHash, normalized.ExpectedConfigHash, "hash lowercase");
        AssertEqual(EmptyHash, StatConfigCanonicalJson.EmptyConfigHash, "empty hash");
        AssertEqual(EmptyHash, LabelConfigRoutes.EmptyConfigHash, "route empty hash");

        using var duplicate = JsonDocument.Parse(
            "{\"commandId\":\"one\",\"commandId\":\"two\"," +
            "\"expectedRevision\":0,\"expectedConfigHash\":\"" +
            EmptyHash + "\",\"payload\":{}}");
        var duplicateError = ExpectError(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            () => StatConfigCanonicalJson.DeserializeStrict<
                StatConfigMutationEnvelope<LabelTombstonePayload>>(
                duplicate.RootElement));
        AssertContains(
            JsonSerializer.Serialize(duplicateError.Details),
            "$.commandId",
            "duplicate property path");

        using var unknown = JsonDocument.Parse(
            "{\"commandId\":\"one\",\"expectedRevision\":0," +
            "\"expectedConfigHash\":\"" + EmptyHash +
            "\",\"payload\":{},\"ownerId\":\"caller-forged\"}");
        ExpectError(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            () => StatConfigCanonicalJson.DeserializeStrict<
                StatConfigMutationEnvelope<LabelTombstonePayload>>(
                unknown.RootElement));

        ExpectError(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            () => StatConfigCanonicalJson.DeserializeStrict<
                StatConfigMutationEnvelope<LabelTombstonePayload>>(
                default));

        ExpectError(
            AppErrorCode.STAT_CONFIG_COMMAND_ID_REQUIRED,
            () => StatConfigCanonicalJson.NormalizeCommand(
                new StatConfigMutationEnvelope<LabelTombstonePayload>(
                    null,
                    0,
                    EmptyHash,
                    new LabelTombstonePayload()),
                StatConfigCommandKinds.TombstoneLabel));
        ExpectError(
            AppErrorCode.STAT_CONFIG_EXPECTED_REVISION_REQUIRED,
            () => StatConfigCanonicalJson.NormalizeCommand(
                new StatConfigMutationEnvelope<LabelTombstonePayload>(
                    "cmd",
                    null,
                    EmptyHash,
                    new LabelTombstonePayload()),
                StatConfigCommandKinds.TombstoneLabel));
        ExpectError(
            AppErrorCode.STAT_CONFIG_EXPECTED_HASH_INVALID,
            () => StatConfigCanonicalJson.NormalizeCommand(
                new StatConfigMutationEnvelope<LabelTombstonePayload>(
                    "cmd",
                    0,
                    "not-a-sha256",
                    new LabelTombstonePayload()),
                StatConfigCommandKinds.TombstoneLabel));
    }

    private static void CanonicalHashSortsObjectsAndNormalizesOnlySetArrays()
    {
        using var first = JsonDocument.Parse(
            "{\"valueOptions\":[{\"code\":\"b\"},{\"code\":\"a\"}]," +
            "\"tagCodes\":[\" Beta \",\"alpha\",\"ALPHA\"],\"a\":1}");
        using var second = JsonDocument.Parse(
            "{\"a\":1,\"tagCodes\":[\"alpha\",\"beta\"]," +
            "\"valueOptions\":[{\"code\":\"b\"},{\"code\":\"a\"}]}");

        var canonical = StatConfigCanonicalJson.CanonicalizeElement(
            first.RootElement);
        AssertEqual(
            "{\"a\":1,\"tagCodes\":[\"alpha\",\"beta\"]," +
            "\"valueOptions\":[{\"code\":\"b\"},{\"code\":\"a\"}]}",
            canonical,
            "canonical json");
        AssertEqual(
            canonical,
            StatConfigCanonicalJson.CanonicalizeElement(second.RootElement),
            "equivalent canonical json");
        AssertEqual(
            StatConfigCanonicalJson.HashUtf8(canonical),
            StatConfigCanonicalJson.HashObject(first.RootElement),
            "canonical hash");
        AssertEqual(
            EmptyHash,
            StatConfigCanonicalJson.HashUtf8("null"),
            "SHA-256 null oracle");
    }

    private static void LabelTaxonomyRejectsEveryLegacyFallback()
    {
        AssertEqual(
            "ops.daily-total",
            LabelTaxonomyContract.NormalizeCode(
                "  OPS.Daily-Total  ",
                "$.payload.code"),
            "normalized code");

        foreach (var value in new[]
                 {
                     LabelDataTypes.Number,
                     LabelDataTypes.ShortText,
                     LabelDataTypes.StringList,
                     LabelDataTypes.LongText,
                     LabelDataTypes.Date,
                     LabelDataTypes.Boolean
                 })
        {
            AssertEqual(
                value,
                LabelTaxonomyContract.NormalizeDataType(
                    value.ToLowerInvariant(),
                    "$.payload.dataType"),
                $"exact datatype {value}");
        }

        AssertEqual(
            LabelDataTypes.LongText,
            LabelTaxonomyContract.NormalizeDataType(
                " long_text ",
                "$.payload.dataType"),
            "LONG_TEXT must not become STRING_LIST");
        ExpectError(
            AppErrorCode.LABEL_DATA_TYPE_INVALID,
            () => LabelTaxonomyContract.NormalizeDataType(
                "TEXT",
                "$.payload.dataType"));
        ExpectError(
            AppErrorCode.LABEL_DATA_TYPE_INVALID,
            () => LabelTaxonomyContract.NormalizeDataType(
                "FULL_DATE",
                "$.payload.dataType"));
        ExpectError(
            AppErrorCode.LABEL_USAGE_INVALID,
            () => LabelTaxonomyContract.NormalizeUsage(
                "TAG",
                "$.payload.usage"));
        ExpectError(
            AppErrorCode.LABEL_VALUE_SOURCE_TYPE_INVALID,
            () => LabelTaxonomyContract.NormalizeValueSourceType(
                "ENUM",
                "$.payload.valueSourceType"));
        ExpectError(
            AppErrorCode.LABEL_SCOPE_TYPE_INVALID,
            () => LabelTaxonomyContract.NormalizeScopeType(
                "ORGANIZATION",
                "$.payload.scopeType"));
        ExpectError(
            AppErrorCode.LABEL_CODE_INVALID,
            () => LabelTaxonomyContract.NormalizeCode(
                "bad/code",
                "$.payload.code"));
    }

    private static void FourLabelLayersRejectCrossLayerUsage()
    {
        var compatible = new[]
        {
            (LabelLayerKinds.ClassificationTag, LabelUsages.Classification),
            (LabelLayerKinds.FieldStatistic, LabelUsages.Statistic),
            (LabelLayerKinds.TableMetric, LabelUsages.TableTarget),
            (LabelLayerKinds.RuntimeRow, LabelUsages.TableTarget)
        };
        foreach (var (layer, usage) in compatible)
        {
            LabelTaxonomyContract.RequireLayerCompatibility(
                layer,
                usage,
                "$.label");
        }

        ExpectError(
            AppErrorCode.LABEL_LAYER_MISMATCH,
            () => LabelTaxonomyContract.RequireLayerCompatibility(
                LabelLayerKinds.ClassificationTag,
                LabelUsages.Statistic,
                "$.statisticLabelCodes[0]"));
        ExpectError(
            AppErrorCode.LABEL_LAYER_MISMATCH,
            () => LabelTaxonomyContract.RequireLayerCompatibility(
                LabelLayerKinds.FieldStatistic,
                LabelUsages.Classification,
                "$.statisticLabelCodes[0]"));
        ExpectError(
            AppErrorCode.LABEL_LAYER_MISMATCH,
            () => LabelTaxonomyContract.RequireLayerCompatibility(
                LabelLayerKinds.TableMetric,
                LabelUsages.Statistic,
                "$.metricLabelTargets[0]"));
        ExpectError(
            AppErrorCode.LABEL_LAYER_MISMATCH,
            () => LabelTaxonomyContract.RequireLayerCompatibility(
                LabelLayerKinds.RuntimeRow,
                LabelUsages.Classification,
                "$.rowLabelCodes[0]"));
    }

    private static void LockedSnapshotsPinLabelIdentityAndLineage()
    {
        const string labelId = "100000000000000000000001";
        const string versionId = "200000000000000000000001";
        var label = new LabelCatalogItem
        {
            Id = labelId,
            ConfigId = labelId,
            VersionId = versionId,
            VersionNo = 3,
            Revision = 3,
            ConfigHash = new string('a', 64),
            Code = "daily.total",
            Name = "Daily total",
            NameLower = "daily total",
            Usage = LabelUsages.Statistic,
            DataType = LabelDataTypes.Number,
            ValueSourceType = LabelValueSourceTypes.None,
            ScopeType = LabelScopeTypes.Unit,
            ScopeId = "300000000000000000000001",
            IsActive = false,
            VersionSnapshots =
            {
                new LabelConfigVersionSnapshot
                {
                    LabelId = labelId,
                    VersionId = versionId,
                    PreviousVersionId = "200000000000000000000000",
                    VersionNo = 3,
                    Revision = 3,
                    Status = StatConfigStatuses.Inactive,
                    ConfigHash = new string('a', 64),
                    Code = "daily.total",
                    Name = "Daily total",
                    Usage = LabelUsages.Statistic,
                    DataType = LabelDataTypes.Number,
                    ValueSourceType = LabelValueSourceTypes.None,
                    ScopeType = LabelScopeTypes.Unit,
                    ScopeId = "300000000000000000000001",
                    IsActive = false,
                    CreatedAtUtc = DateTime.UnixEpoch,
                    CreatedByUserId = "400000000000000000000001"
                }
            }
        };

        var snapshot = LabelTaxonomyContract.CreateLockedSnapshot(label);
        AssertEqual(labelId, snapshot.LabelId, "locked label id");
        AssertEqual(versionId, snapshot.VersionId, "locked version id");
        AssertEqual(3, snapshot.VersionNo, "locked version number");
        AssertEqual(label.ConfigHash, snapshot.ConfigHash, "locked hash");
        AssertEqual(LabelDataTypes.Number, snapshot.DataType, "locked datatype");
        AssertEqual(LabelUsages.Statistic, snapshot.Usage, "locked usage");
        AssertEqual(LabelScopeTypes.Unit, snapshot.ScopeType, "locked scope");
        AssertEqual(false, snapshot.IsActive, "locked active state");

        var bson = label.ToBsonDocument();
        var persisted = bson["versionSnapshots"]
            .AsBsonArray[0]
            .AsBsonDocument;
        AssertEqual(
            labelId,
            persisted["labelId"].AsObjectId.ToString(),
            "embedded snapshot labelId");
        AssertEqual(
            versionId,
            persisted["versionId"].AsObjectId.ToString(),
            "embedded snapshot versionId");
        AssertEqual(
            label.ConfigHash,
            persisted["configHash"].AsString,
            "embedded snapshot hash");
    }

    private static void SourceFreezesAuthorizationCasReceiptAndReservation()
    {
        var service = ReadBackendSource(
            "Services/LabelConfigCommandService.cs");
        var receipt = ReadBackendSource(
            "Models/StatisticsConfiguration/StatConfigCommandReceipt.cs");
        var transaction = ReadBackendSource(
            "Services/StatisticsConfiguration/StatConfigTransactionRunner.cs");
        var create = Slice(
            service,
            "public async Task<LabelConfigResult> CreateAsync",
            "public async Task<LabelConfigResult> UpdateAsync");
        var update = Slice(
            service,
            "public async Task<LabelConfigResult> UpdateAsync",
            "public async Task<LabelConfigResult> TombstoneAsync");
        var tombstone = Slice(
            service,
            "public async Task<LabelConfigResult> TombstoneAsync",
            "private async Task<LabelConfigResult> ExecuteWithDuplicateReplayAsync");
        var reservation = Slice(
            service,
            "private async Task EnsureCodeReservedAsync",
            "private async Task<LabelCatalogItem?> LoadManageableAsync");
        var reservationQuery = Slice(
            reservation,
            "var filter =",
            "var existing =");

        AssertBefore(create, "RequireManager(me);", "NormalizePayloadAsync(", "create ACL");
        AssertBefore(update, "RequireManager(me);", "RequireObjectId(id)", "update ACL");
        AssertBefore(update, "NormalizePayloadAsync(", "LoadManageableAsync(", "requested scope ACL");
        AssertBefore(update, "LoadManageableAsync(", "LoadReplayAsync(", "update replay ACL");
        AssertBefore(tombstone, "RequireManager(me);", "RequireObjectId(id)", "tombstone broad ACL");
        AssertBefore(tombstone, "LoadManageableAsync(", "LoadReplayAsync(", "tombstone replay ACL");

        AssertContains(create, "normalized.ScopeType", "deterministic scope identity");
        AssertContains(create, "normalized.ScopeId ?? \"-\"", "deterministic scope id identity");
        AssertContains(create, "normalized.Code", "deterministic code identity");
        AssertContains(create, "DeterministicObjectId(", "Mongo _id race barrier");
        AssertNotContains(
            reservationQuery,
            "item => item.IsDeleted",
            "reservation query must include tombstones");
        AssertContains(tombstone, "label.IsDeleted = true;", "tombstone persistence");
        AssertContains(tombstone, "ReplaceOneAsync(", "tombstone keeps reserved owner document");
        AssertNotContains(tombstone, "DeleteOneAsync(", "tombstone cannot remove reservation");

        AssertContains(update, "item => item.Revision", "revision CAS filter");
        AssertContains(update, "item => item.ConfigHash", "hash CAS filter");
        AssertBefore(update, "EnsureConfigHashIntegrity(label);", "EnsureCas(label, command);", "hash readback");
        AssertBefore(tombstone, "EnsureConfigHashIntegrity(label);", "EnsureCas(label, command);", "tombstone hash");
        AssertContains(service, "CONFIG_HASH_INTEGRITY", "persisted hash recomputation");

        AssertContains(receipt, "[BsonId]", "receipt uses Mongo unique _id");
        AssertNotContains(
            receipt,
            "[BsonRepresentation(BsonType.ObjectId)]\n    public string Id",
            "receipt id is a SHA-256 string");
        AssertContains(service, "RestoreReplay(receipt, requestHash)", "exact replay");
        AssertContains(service, "STAT_CONFIG_COMMAND_REPLAY_CONFLICT", "changed replay");
        AssertContains(service, "ResponseJson", "exact response receipt");
        AssertContains(service, "ResponseHash", "receipt integrity");
        AssertContains(transaction, "session.StartTransaction(Options)", "transaction start");
        AssertContains(transaction, "ReadConcern.Snapshot", "snapshot read concern");
        AssertContains(transaction, "WriteConcern.WMajority", "majority write concern");
        AssertContains(transaction, "STAT_CONFIG_TRANSACTION_UNSUPPORTED", "no transaction fallback");
    }

    private static void ConfigRoutesStayIsolatedFromResultMaterializers()
    {
        var controller = ReadBackendSource("Controllers/LabelsController.cs");
        var service = ReadBackendSource(
            "Services/LabelConfigCommandService.cs");
        var canonical = ReadBackendSource(
            "Services/StatisticsConfiguration/StatConfigCanonicalJson.cs");
        var rebuild = ReadBackendSource(
            "Services/WorkAssignmentReports/Statistics/WorkReportStatisticRebuildJobService.cs");
        var program = ReadBackendSource("Program.cs");

        AssertContains(controller, "[Authorize]", "explicit controller authorization");
        AssertContains(controller, "[HttpGet(\"{id}/config\")]", "config read route");
        AssertContains(controller, "[HttpPost(\"config\")]", "config create route");
        AssertContains(controller, "[HttpPut(\"{id}/config\")]", "config update route");
        AssertContains(
            controller,
            "[HttpPost(\"{id}/config/tombstone\")]",
            "config tombstone route");
        AssertContains(
            controller,
            "StatConfigCanonicalJson.DeserializeStrict",
            "strict route deserialization");
        foreach (var bypass in new[]
                 {
                     "_svc.CreateAsync(",
                     "_svc.UpdateAsync(",
                     "_svc.DeleteAsync("
                 })
        {
            AssertNotContains(
                controller,
                bypass,
                $"legacy label writer bypass: {bypass}");
        }
        AssertContains(
            service,
            "EnterConfigurationMutation(",
            "active configuration isolation scope");
        AssertContains(
            rebuild,
            "ThrowIfConfigurationMutationActive(",
            "rebuild queue rejects active config mutations");
        AssertContains(
            program,
            "ILabelConfigCommandService",
            "config service registration");
        AssertContains(
            program,
            "IStatConfigTransactionRunner",
            "transaction service registration");
        AssertContains(
            canonical,
            "targetPhase = \"P9\"",
            "P8/P9 isolation barrier");

        foreach (var forbidden in new[]
                 {
                     "IWorkReportStatisticRebuildJobService",
                     "WorkReportStatisticRebuildJob",
                     "EnqueueAsync(",
                     "WorkReportFieldStatValue",
                     "WorkReportFieldStatAggregate",
                     "WorkReportTableStatValue",
                     "WorkReportTableStatAggregate",
                     "WorkReportLabelStatValue",
                     "WorkReportLabelStatAggregate",
                     "WorkAssignmentBasicSummarySnapshot",
                     "WorkAssignmentAdvancedSummaryDayNode",
                     "WorkReportStatisticDiffResult",
                     "Export"
                 })
        {
            AssertNotContains(
                service,
                forbidden,
                $"P8 label config isolation: {forbidden}");
        }
    }

    private static void StableErrorsExposeTheFrozenStatuses()
    {
        foreach (var code in new[]
                 {
                     AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                     AppErrorCode.STAT_CONFIG_COMMAND_REPLAY_CONFLICT,
                     AppErrorCode.LABEL_CONFIG_TOMBSTONED,
                     AppErrorCode.LABEL_DELETE_REFERENCED,
                     AppErrorCode.LABEL_DUPLICATE_CODE
                 })
        {
            AssertEqual(409, AppErrorCatalog.Get(code).HttpStatus, $"{code} status");
        }
        AssertEqual(
            503,
            AppErrorCatalog.Get(
                AppErrorCode.STAT_CONFIG_TRANSACTION_UNSUPPORTED).HttpStatus,
            "transaction unsupported status");
        foreach (var code in new[]
                 {
                     AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
                     AppErrorCode.LABEL_DATA_TYPE_INVALID,
                     AppErrorCode.LABEL_VALUE_SOURCE_TYPE_INVALID,
                     AppErrorCode.LABEL_LAYER_MISMATCH
                 })
        {
            AssertEqual(400, AppErrorCatalog.Get(code).HttpStatus, $"{code} status");
        }
    }

    private static AppException ExpectError(
        AppErrorCode expected,
        Action action)
    {
        try
        {
            action();
        }
        catch (AppException error)
        {
            AssertEqual(expected, error.Code, "stable error code");
            return error;
        }

        throw new InvalidOperationException(
            $"Expected {expected}, but no AppException was thrown.");
    }

    private static string ReadBackendSource(string relativePath)
    {
        var root = FindBackendRoot();
        var path = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Backend source file was not found: {path}");
        return File.ReadAllText(path);
    }

    private static string FindBackendRoot()
    {
        var seeds = new[]
            {
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory
            }
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in seeds)
        {
            for (var directory = new DirectoryInfo(seed);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(
                        Path.Combine(
                            directory.FullName,
                            "tdtd-be.csproj")))
                {
                    return directory.FullName;
                }

                var nested = Path.Combine(
                    directory.FullName,
                    "tdtd-be");
                if (File.Exists(
                        Path.Combine(nested, "tdtd-be.csproj")))
                {
                    return nested;
                }
            }
        }

        throw new InvalidOperationException(
            "Could not locate the tdtd-be source root.");
    }

    private static string Slice(
        string source,
        string start,
        string end)
    {
        var startIndex = source.IndexOf(
            start,
            StringComparison.Ordinal);
        if (startIndex < 0)
            throw new InvalidOperationException(
                $"Source start anchor was not found: {start}");
        var endIndex = source.IndexOf(
            end,
            startIndex + start.Length,
            StringComparison.Ordinal);
        if (endIndex < 0)
            throw new InvalidOperationException(
                $"Source end anchor was not found: {end}");
        return source[startIndex..endIndex];
    }

    private static void AssertBefore(
        string source,
        string first,
        string second,
        string context)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        if (firstIndex < 0 ||
            secondIndex < 0 ||
            firstIndex >= secondIndex)
        {
            throw new InvalidOperationException(
                $"{context}: expected '{first}' before '{second}'.");
        }
    }

    private static void AssertContains(
        string source,
        string expected,
        string context)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{context}: expected '{expected}'.");
    }

    private static void AssertNotContains(
        string source,
        string forbidden,
        string context)
    {
        if (source.Contains(forbidden, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{context}: forbidden '{forbidden}'.");
    }

    private static void AssertEqual<T>(
        T expected,
        T actual,
        string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{context}: expected '{expected}', got '{actual}'.");
        }
    }
}
