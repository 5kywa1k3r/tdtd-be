using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private static readonly string[] TableMutationWrites =
        [DynamicFormsCollection, ReceiptsCollection];

    private static JsonObject TableMetric(
        string metricKey,
        string dataType,
        IEnumerable<string> aggregateOps)
        => new()
        {
            ["metricKey"] = metricKey,
            ["dataType"] = dataType,
            ["aggregateOps"] = new JsonArray(
                aggregateOps.Select(value => JsonValue.Create(value)).ToArray())
        };

    private static JsonObject TableMetricLabelTarget(
        string metricKey,
        string statisticLabelCode)
        => new()
        {
            ["metricKey"] = metricKey,
            ["statisticLabelCode"] = statisticLabelCode
        };

    private static JsonObject TablePatch(
        string blockId,
        string tableMode,
        bool statisticsDisabled,
        IEnumerable<JsonObject> metrics,
        IEnumerable<JsonObject>? metricLabelTargets = null,
        IEnumerable<string>? allowedRowLabelCodes = null)
        => new()
        {
            ["blockId"] = blockId,
            ["tableMode"] = tableMode,
            ["statisticsDisabled"] = statisticsDisabled,
            ["metrics"] = new JsonArray(
                metrics.Select(metric => metric.DeepClone()).ToArray()),
            ["metricLabelTargets"] = new JsonArray(
                (metricLabelTargets ?? Array.Empty<JsonObject>())
                .Select(target => target.DeepClone()).ToArray()),
            ["allowedRowLabelCodes"] = new JsonArray(
                (allowedRowLabelCodes ?? Array.Empty<string>())
                .Select(code => JsonValue.Create(code)).ToArray())
        };

    private static JsonObject TablePayload(params JsonObject[] tables)
        => new()
        {
            ["tables"] = new JsonArray(
                tables.Select(table => table.DeepClone()).ToArray())
        };

    private async Task<(ApiHarnessResponse Response, P8FieldConfigIdentity Identity)>
        PatchTableConfigAsync(
            P8Actor actor,
            P8TableFixture fixture,
            string commandId,
            JsonObject payload,
            CancellationToken ct,
            long? expectedRevision = null,
            string? expectedConfigHash = null)
    {
        var current = await ReadFieldConfigAsync(
            actor,
            fixture.Form.Id,
            ct,
            requirePersisted: true);
        if (expectedRevision.HasValue)
        {
            HarnessAssert.Equal(expectedRevision.Value, current.Revision,
                "Caller-provided table CAS revision differs from pre-PATCH GET");
        }
        if (expectedConfigHash is not null)
        {
            HarnessAssert.Equal(expectedConfigHash, current.ConfigHash,
                "Caller-provided table CAS hash differs from pre-PATCH GET");
        }

        var beforeOwner = await RequireFormDocumentAsync(fixture.Form.Id, ct);
        var beforeBlocksJson = BsonString(beforeOwner, "blocksJson") ?? "[]";
        var beforeFieldsJson = BsonString(beforeOwner, "fieldsJson") ?? "[]";
        var beforeLegacyJson = BsonString(beforeOwner, "excelBlockJson");
        var beforeSections = beforeOwner.GetValue(
            "statisticConfigSections",
            new BsonDocument()).AsBsonDocument;
        var beforeFieldSectionJson = BsonString(beforeSections, "fieldSectionJson");
        var beforeOwnerRevision = BsonInt(beforeOwner, "revision")
                                  ?? throw new InvalidOperationException(
                                      "Dynamic Form owner revision is absent before table PATCH.");

        var response = await _api.PatchAsync(
            $"api/dynamic-forms/{fixture.Form.Id}/statistics",
            Envelope(
                commandId,
                expectedRevision ?? current.Revision,
                expectedConfigHash ?? current.ConfigHash,
                payload),
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"table statistic PATCH {commandId}");
        var identity = ParseFieldIdentity(response.Json, requireReceipt: true);
        RequireFieldIdentityContract(identity, fixture.Form.Id);
        await RequireDirectFieldIdentityAsync(identity, commandId, actor.Id, ct);

        var read = await ReadFieldConfigAsync(
            actor,
            fixture.Form.Id,
            ct,
            requirePersisted: true);
        HarnessAssert.Equal(
            CanonicalFieldReadback(identity.Raw),
            CanonicalFieldReadback(read.Raw),
            "Table PATCH response and stable GET readback differ");
        HarnessAssert.Equal(current.FieldSectionHash, identity.FieldSectionHash,
            "Table PATCH changed fieldSectionHash");
        HarnessAssert.Equal(
            Canonicalize(current.Fields),
            Canonicalize(identity.Fields),
            "Table PATCH changed typed field config");
        HarnessAssert.Equal(current.Revision + 1, identity.Revision,
            "Table PATCH did not advance config revision exactly once");
        HarnessAssert.Equal(current.VersionNo + 1, identity.VersionNo,
            "Table PATCH did not create exactly one config version");

        var afterOwner = await RequireFormDocumentAsync(fixture.Form.Id, ct);
        HarnessAssert.Equal(beforeFieldsJson, BsonString(afterOwner, "fieldsJson"),
            "Table PATCH changed owner.FieldsJson");
        HarnessAssert.Equal(beforeLegacyJson, BsonString(afterOwner, "excelBlockJson"),
            "Table PATCH changed owner.ExcelBlockJson");
        HarnessAssert.Equal(beforeOwnerRevision + 1, BsonInt(afterOwner, "revision"),
            "Table PATCH did not advance owner revision exactly once");
        var afterSections = afterOwner.GetValue(
            "statisticConfigSections",
            new BsonDocument()).AsBsonDocument;
        var afterFieldSectionJson = BsonString(afterSections, "fieldSectionJson");
        HarnessAssert.True(beforeFieldSectionJson is not null,
            "P8-03 fixture lacks a persisted pre-PATCH field section");
        HarnessAssert.Equal(beforeFieldSectionJson, afterFieldSectionJson,
            "Table PATCH changed persisted fieldSectionJson bytes");

        var afterBlocksJson = BsonString(afterOwner, "blocksJson") ?? "[]";
        HarnessAssert.Equal(
            CanonicalTableStructure(beforeBlocksJson),
            CanonicalTableStructure(afterBlocksJson),
            "Table PATCH changed canonical table structure");
        var targetedBlockIds = RequiredTables(payload).OfType<JsonObject>()
            .Select(table => RequiredString(table, "blockId"))
            .ToHashSet(StringComparer.Ordinal);
        RequireOmittedTableBlocksUnchanged(
            beforeBlocksJson,
            afterBlocksJson,
            targetedBlockIds);
        RequireTableConfigMatchesOwner(identity, afterOwner);

        var currentVersion = identity.Versions.OfType<JsonObject>()
            .Single(version => RequiredInt(version, "versionNo") == identity.VersionNo);
        HarnessAssert.Equal(
            Canonicalize(identity.TableConfig),
            Canonicalize(currentVersion["tableConfig"]
                         ?? throw new InvalidOperationException(
                             "Current version tableConfig is absent.")),
            "Root and current-version tableConfig differ");
        return (response, identity);
    }

    private async Task<ApiHarnessResponse> RequireZeroWriteTableRejectionAsync(
        string probeId,
        Func<Task<ApiHarnessResponse>> request,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string expectedPath,
        string expectedReason,
        CancellationToken ct)
    {
        var before = await CaptureDatabaseSnapshotAsync(ct);
        var response = await request();
        ExpectTableFailure(
            response,
            expectedStatus,
            expectedCode,
            expectedPath,
            expectedReason);
        var after = await CaptureDatabaseSnapshotAsync(ct);
        VerifyCollectionContract(
            probeId,
            BuildDeltas(before, after),
            Array.Empty<string>(),
            Array.Empty<string>());
        return response;
    }

    private static void ExpectTableFailure(
        ApiHarnessResponse response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string expectedPath,
        string expectedReason)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            expectedStatus,
            "expected P8 table rejection");
        var actualCode = ApiHarnessClient.FindStringRecursive(
                             response.Json,
                             "errorCode")
                         ?? ApiHarnessClient.FindStringRecursive(
                             response.Json,
                             "code");
        var actualPath = ApiHarnessClient.FindStringRecursive(response.Json, "path");
        var actualReason = ApiHarnessClient.FindStringRecursive(response.Json, "reason");
        HarnessAssert.Equal(expectedCode, actualCode, "P8 table error code mismatch");
        HarnessAssert.Equal(expectedPath, actualPath, "P8 table error path mismatch");
        HarnessAssert.Equal(expectedReason, actualReason, "P8 table error reason mismatch");
    }

    private static JsonArray RequiredTables(JsonObject payload)
        => payload["tables"] as JsonArray
           ?? throw new InvalidOperationException("P8 table payload lacks tables[].");

    private static JsonArray RequiredTableConfig(P8FieldConfigIdentity identity)
        => identity.TableConfig as JsonArray
           ?? throw new InvalidOperationException("P8 tableConfig is not an array.");

    private static JsonObject RequireTable(
        P8FieldConfigIdentity identity,
        string blockId)
        => RequiredTableConfig(identity).OfType<JsonObject>()
               .SingleOrDefault(table => string.Equals(
                   RequiredString(table, "blockId"),
                   blockId,
                   StringComparison.Ordinal))
           ?? throw new InvalidOperationException(
               $"Table readback {blockId} is missing.");

    private static void RequireTableReadback(
        P8FieldConfigIdentity identity,
        string blockId,
        string expectedMode,
        bool expectedDisabled,
        string expectedSchemaSource,
        IReadOnlyDictionary<string, (string DataType, IReadOnlyList<string> Ops)>
            expectedMetrics,
        IReadOnlyList<(string MetricKey, string Code)>? expectedMetricLabels = null,
        IReadOnlyList<string>? expectedRowLabels = null)
    {
        var table = RequireTable(identity, blockId);
        HarnessAssert.Equal(expectedMode, RequiredString(table, "tableMode"),
            $"Table mode mismatch for {blockId}");
        HarnessAssert.Equal(expectedDisabled, RequiredBool(table, "statisticsDisabled"),
            $"statisticsDisabled mismatch for {blockId}");
        HarnessAssert.Equal(expectedSchemaSource, RequiredString(table, "schemaSource"),
            $"schemaSource mismatch for {blockId}");
        RequireLowerSha256(RequiredString(table, "structureHash"),
            $"{blockId}.structureHash");

        var metrics = table["metrics"] as JsonArray
                      ?? throw new InvalidOperationException(
                          $"Table metrics are absent for {blockId}.");
        HarnessAssert.Equal(expectedMetrics.Count, metrics.Count,
            $"Table metric count mismatch for {blockId}");
        HarnessAssert.True(metrics.OfType<JsonObject>()
                .Select(metric => RequiredString(metric, "metricKey"))
                .SequenceEqual(
                    expectedMetrics.Keys.OrderBy(key => key, StringComparer.Ordinal),
                    StringComparer.Ordinal),
            $"Table metrics are not canonical metricKey order for {blockId}");
        foreach (var metric in metrics.OfType<JsonObject>())
        {
            var metricKey = RequiredString(metric, "metricKey");
            var expected = expectedMetrics[metricKey];
            HarnessAssert.Equal(expected.DataType, RequiredString(metric, "dataType"),
                $"Table metric datatype mismatch for {blockId}/{metricKey}");
            var ops = metric["aggregateOps"] as JsonArray
                      ?? throw new InvalidOperationException(
                          $"aggregateOps absent for {blockId}/{metricKey}.");
            HarnessAssert.True(expected.Ops.SequenceEqual(
                    ops.Select(item => item?.GetValue<string>() ?? string.Empty),
                    StringComparer.Ordinal),
                $"aggregateOps mismatch for {blockId}/{metricKey}");
        }

        var labels = table["metricLabelTargets"] as JsonArray
                     ?? throw new InvalidOperationException(
                         $"metricLabelTargets absent for {blockId}.");
        var expectedLabels = expectedMetricLabels ?? [];
        HarnessAssert.True(expectedLabels
                .OrderBy(item => item.MetricKey, StringComparer.Ordinal)
                .ThenBy(item => item.Code, StringComparer.Ordinal)
                .SequenceEqual(labels.OfType<JsonObject>().Select(item => (
                    RequiredString(item, "metricKey"),
                    RequiredString(item, "statisticLabelCode")))),
            $"Metric label target readback mismatch for {blockId}");

        var rowLabels = table["allowedRowLabelCodes"] as JsonArray
                        ?? throw new InvalidOperationException(
                            $"allowedRowLabelCodes absent for {blockId}.");
        HarnessAssert.True((expectedRowLabels ?? [])
                .OrderBy(code => code, StringComparer.Ordinal)
                .SequenceEqual(
                    rowLabels.Select(item => item?.GetValue<string>() ?? string.Empty),
                    StringComparer.Ordinal),
            $"Allowed row-label readback mismatch for {blockId}");
    }

    private static void RequirePinnedMetricLabel(
        P8FieldConfigIdentity identity,
        string blockId,
        string metricKey,
        P8ConfigIdentity label)
    {
        var table = RequireTable(identity, blockId);
        var target = (table["metricLabelTargets"] as JsonArray)?.OfType<JsonObject>()
            .SingleOrDefault(item =>
                string.Equals(RequiredString(item, "metricKey"), metricKey, StringComparison.Ordinal) &&
                string.Equals(RequiredString(item, "statisticLabelCode"), label.LabelCode,
                    StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Pinned metric label {label.LabelCode} is missing.");
        HarnessAssert.Equal(label.LabelDataType, RequiredString(target, "dataType"),
            "Pinned metric label target datatype mismatch");
        var snapshot = target["labelSnapshot"] as JsonObject
                       ?? throw new InvalidOperationException(
                           "Pinned metric label snapshot is absent.");
        RequirePinnedTableLabelSnapshot(snapshot, label, "TABLE_TARGET");
    }

    private static void RequirePinnedRowLabel(
        P8FieldConfigIdentity identity,
        string blockId,
        P8ConfigIdentity label)
    {
        var table = RequireTable(identity, blockId);
        var snapshots = table["rowLabelSnapshots"] as JsonArray
                        ?? throw new InvalidOperationException(
                            "Pinned row label snapshots are absent.");
        var snapshot = snapshots.OfType<JsonObject>()
            .SingleOrDefault(item => string.Equals(
                RequiredString(item, "code"),
                label.LabelCode,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Pinned row label {label.LabelCode} is missing.");
        RequirePinnedTableLabelSnapshot(snapshot, label, "TABLE_TARGET");
    }

    private static void RequirePinnedTableLabelSnapshot(
        JsonObject snapshot,
        P8ConfigIdentity label,
        string expectedUsage)
    {
        HarnessAssert.Equal(label.LabelId, RequiredString(snapshot, "labelId"),
            "Pinned table labelId mismatch");
        HarnessAssert.Equal(label.LabelCode, RequiredString(snapshot, "code"),
            "Pinned table label code mismatch");
        HarnessAssert.Equal(label.LabelDataType, RequiredString(snapshot, "dataType"),
            "Pinned table label datatype mismatch");
        HarnessAssert.Equal(expectedUsage, RequiredString(snapshot, "usage"),
            "Pinned table label usage mismatch");
        HarnessAssert.Equal(label.LabelScopeType, RequiredString(snapshot, "scopeType"),
            "Pinned table label scope mismatch");
        HarnessAssert.Equal(label.LabelScopeId, OptionalString(snapshot, "scopeId"),
            "Pinned table label scopeId mismatch");
        HarnessAssert.True(RequiredBool(snapshot, "isActive"),
            "Pinned table label must be active");
        HarnessAssert.Equal(label.VersionNo, RequiredInt(snapshot, "versionNo"),
            "Pinned table label versionNo mismatch");
        HarnessAssert.Equal(label.VersionId, RequiredString(snapshot, "versionId"),
            "Pinned table label versionId mismatch");
        HarnessAssert.Equal(label.ConfigHash, RequiredString(snapshot, "configHash"),
            "Pinned table label configHash mismatch");
    }

    private static void RequireTableConfigMatchesOwner(
        P8FieldConfigIdentity identity,
        BsonDocument owner)
    {
        var canonicalBlocks = ParseBlocks(BsonString(owner, "blocksJson"));
        JsonObject? legacy = null;
        var legacyJson = BsonString(owner, "excelBlockJson");
        if (canonicalBlocks.Count == 0 && !string.IsNullOrWhiteSpace(legacyJson))
        {
            legacy = JsonNode.Parse(legacyJson) as JsonObject
                     ?? throw new InvalidOperationException(
                         "Legacy ExcelBlockJson is not an object.");
        }

        var tableConfig = RequiredTableConfig(identity);
        var expectedCount = canonicalBlocks.Count > 0
            ? canonicalBlocks.Count
            : legacy is null ? 0 : 1;
        HarnessAssert.Equal(expectedCount, tableConfig.Count,
            "Typed tableConfig count differs from canonical/adapted blocks");
        foreach (var table in tableConfig.OfType<JsonObject>())
        {
            var blockId = RequiredString(table, "blockId");
            var source = RequiredString(table, "schemaSource");
            var block = source switch
            {
                "BLOCKS_JSON" => canonicalBlocks.OfType<JsonObject>()
                    .Single(item => string.Equals(
                        RequiredString(item, "blockId"),
                        blockId,
                        StringComparison.Ordinal)),
                "LEGACY_EXCEL_BLOCK_ADAPTER" when legacy is not null => legacy,
                _ => throw new InvalidOperationException(
                    $"Unexpected table schema source {source} for {blockId}.")
            };
            HarnessAssert.Equal(
                HashTableStructure(block),
                RequiredString(table, "structureHash"),
                $"Independent table structureHash mismatch for {blockId}");
            HarnessAssert.Equal(
                RequiredString(block, "tableMode").Trim().ToUpperInvariant(),
                RequiredString(table, "tableMode"),
                $"Owner/API tableMode mismatch for {blockId}");
            var disabled = OptionalBool(block, "statisticsDisabled") ?? false;
            HarnessAssert.Equal(disabled, RequiredBool(table, "statisticsDisabled"),
                $"Owner/API statisticsDisabled mismatch for {blockId}");

            var liveMetrics = block["metricRules"] as JsonArray ?? [];
            var apiMetrics = table["metrics"] as JsonArray
                             ?? throw new InvalidOperationException(
                                 $"API metrics absent for {blockId}.");
            HarnessAssert.Equal(liveMetrics.Count, apiMetrics.Count,
                $"Owner/API metric count mismatch for {blockId}");
            foreach (var apiMetric in apiMetrics.OfType<JsonObject>())
            {
                var metricKey = RequiredString(apiMetric, "metricKey");
                var live = liveMetrics.OfType<JsonObject>().Single(rule => string.Equals(
                    RequiredString(rule, "metricKey"),
                    metricKey,
                    StringComparison.Ordinal));
                HarnessAssert.Equal(RequiredString(live, "dataType").Trim().ToUpperInvariant(),
                    RequiredString(apiMetric, "dataType"),
                    $"Owner/API metric datatype mismatch for {blockId}/{metricKey}");
                var liveOps = live["aggregateOps"] as JsonArray ?? [];
                var apiOps = apiMetric["aggregateOps"] as JsonArray
                             ?? throw new InvalidOperationException(
                                 $"API aggregateOps absent for {blockId}/{metricKey}.");
                HarnessAssert.True(liveOps.Select(item => item?.GetValue<string>() ?? string.Empty)
                        .SequenceEqual(
                            apiOps.Select(item => item?.GetValue<string>() ?? string.Empty),
                            StringComparer.Ordinal),
                    $"Owner/API aggregateOps mismatch for {blockId}/{metricKey}");
            }

            var liveTargets = block["metricLabelTargets"] as JsonArray ?? [];
            var apiTargets = table["metricLabelTargets"] as JsonArray
                             ?? throw new InvalidOperationException(
                                 $"API metricLabelTargets absent for {blockId}.");
            HarnessAssert.True(liveTargets.OfType<JsonObject>()
                    .Select(target => (
                        RequiredString(target, "metricKey"),
                        RequiredString(target, "statisticLabelCode")))
                    .OrderBy(item => item.Item1, StringComparer.Ordinal)
                    .ThenBy(item => item.Item2, StringComparer.Ordinal)
                    .SequenceEqual(apiTargets.OfType<JsonObject>().Select(target => (
                        RequiredString(target, "metricKey"),
                        RequiredString(target, "statisticLabelCode")))),
                $"Owner/API metricLabelTargets mismatch for {blockId}");
            var liveRows = block["allowedRowLabelCodes"] as JsonArray ?? [];
            var apiRows = table["allowedRowLabelCodes"] as JsonArray
                          ?? throw new InvalidOperationException(
                              $"API allowedRowLabelCodes absent for {blockId}.");
            HarnessAssert.True(liveRows.Select(item => item?.GetValue<string>() ?? string.Empty)
                    .OrderBy(code => code, StringComparer.Ordinal)
                    .SequenceEqual(apiRows.Select(item => item?.GetValue<string>() ?? string.Empty),
                        StringComparer.Ordinal),
                $"Owner/API allowedRowLabelCodes mismatch for {blockId}");
        }
    }

    private static void RequireOmittedTableBlocksUnchanged(
        string beforeBlocksJson,
        string afterBlocksJson,
        IReadOnlySet<string> targetedBlockIds)
    {
        var before = ParseBlocks(beforeBlocksJson).OfType<JsonObject>()
            .ToDictionary(block => RequiredString(block, "blockId"), StringComparer.Ordinal);
        var after = ParseBlocks(afterBlocksJson).OfType<JsonObject>()
            .ToDictionary(block => RequiredString(block, "blockId"), StringComparer.Ordinal);
        HarnessAssert.True(before.Keys.OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(after.Keys.OrderBy(value => value, StringComparer.Ordinal),
                    StringComparer.Ordinal),
            "Table PATCH added or removed owner blocks");
        foreach (var (blockId, block) in before)
        {
            if (targetedBlockIds.Contains(blockId))
                continue;
            HarnessAssert.Equal(Canonicalize(block), Canonicalize(after[blockId]),
                $"Table PATCH changed omitted sibling block {blockId}");
        }
    }

    private static string CanonicalTableStructure(string blocksJson)
    {
        var blocks = ParseBlocks(blocksJson);
        foreach (var block in blocks.OfType<JsonObject>())
            StripMutableTableMetadata(block);
        return Canonicalize(blocks);
    }

    private static string HashTableStructure(JsonObject block)
    {
        var structure = (JsonObject)block.DeepClone();
        StripMutableTableMetadata(structure);
        return Sha256(Encoding.UTF8.GetBytes(Canonicalize(structure)));
    }

    private static void StripMutableTableMetadata(JsonObject block)
    {
        block.Remove("metricLabelTargets");
        block.Remove("allowedRowLabelCodes");
        block.Remove("statisticsDisabled");
        block.Remove("statisticsDisabledReason");
        if (block["metricRules"] is JsonArray rules)
        {
            foreach (var rule in rules.OfType<JsonObject>())
                rule.Remove("aggregateOps");
        }
    }

    private static JsonArray ParseBlocks(string? blocksJson)
        => JsonNode.Parse(string.IsNullOrWhiteSpace(blocksJson) ? "[]" : blocksJson) as JsonArray
           ?? throw new InvalidOperationException("Dynamic Form blocksJson is not an array.");

    private static bool? OptionalBool(JsonObject value, string property)
        => value[property] is JsonValue scalar && scalar.TryGetValue<bool>(out var result)
            ? result
            : null;
}
