using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private static void RequireBasicPayloadContract(
        P8BasicConfigIdentity identity,
        JsonObject expected)
    {
        var actualSource = identity.Payload["sourceScope"] as JsonObject
                           ?? throw new InvalidOperationException(
                               "Basic payload lacks sourceScope.");
        var expectedSource = expected["sourceScope"] as JsonObject
                             ?? throw new InvalidOperationException(
                                 "Expected Basic payload lacks sourceScope.");
        HarnessAssert.Equal(
            CanonicalBasicNullableObject(
                expectedSource,
                "flowInstanceId",
                "flowStepId",
                "flowBranchId",
                "flowEffectiveStatus"),
            CanonicalBasicNullableObject(actualSource),
            "Basic sourceScope readback mismatch");

        var actualPeriod = identity.Payload["periodRule"] as JsonObject
                           ?? throw new InvalidOperationException(
                               "Basic payload lacks periodRule.");
        var expectedPeriod = expected["periodRule"] as JsonObject
                             ?? throw new InvalidOperationException(
                                 "Expected Basic payload lacks periodRule.");
        HarnessAssert.Equal(
            CanonicalBasicNullableObject(
                expectedPeriod,
                "periodKey",
                "periodKeyFrom",
                "periodKeyTo"),
            CanonicalBasicNullableObject(actualPeriod),
            "Basic periodRule readback mismatch");

        var actualGroups = identity.Payload["groupingHints"] as JsonArray
                           ?? throw new InvalidOperationException(
                               "Basic payload lacks groupingHints.");
        var expectedGroups = expected["groupingHints"] as JsonArray
                             ?? throw new InvalidOperationException(
                                 "Expected Basic payload lacks groupingHints.");
        var actualGroupValues = actualGroups
            .Select(item => item?.GetValue<string>() ?? string.Empty)
            .ToArray();
        var expectedGroupValues = expectedGroups
            .Select(item => item?.GetValue<string>() ?? string.Empty)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        HarnessAssert.True(
            actualGroupValues.SequenceEqual(expectedGroupValues, StringComparer.Ordinal),
            "Basic groupingHints were not canonicalized as a distinct ordinal set");

        var actualDetail = identity.Payload["detailHints"] as JsonObject
                           ?? throw new InvalidOperationException(
                               "Basic payload lacks detailHints.");
        var expectedDetail = expected["detailHints"] as JsonObject
                             ?? throw new InvalidOperationException(
                                 "Expected Basic payload lacks detailHints.");
        HarnessAssert.Equal(
            Canonicalize(expectedDetail),
            Canonicalize(actualDetail),
            "Basic detailHints readback mismatch");

        var actualTargets = identity.Payload["targets"] as JsonArray
                            ?? throw new InvalidOperationException(
                                "Basic payload lacks targets.");
        var expectedTargets = expected["targets"] as JsonArray
                              ?? throw new InvalidOperationException(
                                  "Expected Basic payload lacks targets.");
        var actualMap = BasicTargetMap(actualTargets);
        var expectedMap = BasicTargetMap(expectedTargets);
        HarnessAssert.Equal(expectedMap.Count, actualMap.Count,
            "Basic target count mismatch");
        foreach (var pair in expectedMap)
        {
            HarnessAssert.True(actualMap.TryGetValue(pair.Key, out var actual),
                $"Basic target {pair.Key} is absent from readback");
            HarnessAssert.Equal(pair.Value.DataType, actual.DataType,
                $"Basic target {pair.Key} datatype mismatch");
            HarnessAssert.Equal(pair.Value.Operation, actual.Operation,
                $"Basic target {pair.Key} operation mismatch");
        }
    }

    private static string CanonicalBasicNullableObject(
        JsonObject source,
        params string[] nullableProperties)
    {
        var normalized = (JsonObject)source.DeepClone();
        foreach (var property in nullableProperties)
        {
            if (!normalized.ContainsKey(property))
                normalized[property] = null;
        }
        return Canonicalize(normalized);
    }

    private static Dictionary<string, (string DataType, string Operation)>
        BasicTargetMap(JsonArray targets)
    {
        var result = new Dictionary<string, (string, string)>(
            StringComparer.Ordinal);
        foreach (var item in targets)
        {
            var target = item as JsonObject
                         ?? throw new InvalidOperationException(
                             "Basic target readback contains a non-object.");
            var key = $"{RequiredString(target, "conceptKind")}:" +
                      RequiredString(target, "conceptKey");
            HarnessAssert.True(result.TryAdd(
                    key,
                    (RequiredString(target, "dataType"),
                        RequiredString(target, "operation"))),
                $"Basic target {key} is duplicated");
        }
        return result;
    }

    private void RequireP804ModernStatisticDependencyPins(
        P8BasicConfigIdentity identity)
    {
        var expectedStatisticPin =
            $"DYNAMIC_FORM_STAT_CONFIG:" +
            $"{_basicModernStatisticConfig.OwnerId}:" +
            $"{_basicModernStatisticConfig.ConfigId}:" +
            $"{_basicModernStatisticConfig.VersionId}:" +
            $"{_basicModernStatisticConfig.VersionNo}:" +
            $"{_basicModernStatisticConfig.Revision}:" +
            _basicModernStatisticConfig.ConfigHash;
        var statisticPins = identity.DependencyPins
            .Where(pin => pin.StartsWith(
                "DYNAMIC_FORM_STAT_CONFIG:",
                StringComparison.Ordinal))
            .ToArray();
        HarnessAssert.Equal(1, statisticPins.Length,
            "Basic dependency pins do not contain exactly one Dynamic Form statistic identity");
        HarnessAssert.Equal(expectedStatisticPin, statisticPins[0],
            "Basic dependency pin differs from validated P8-03 statistic identity");

        var expectedFrozenLabelPin = BasicLabelPin(
            _basicRowLabelSnapshot);
        var labelPins = identity.DependencyPins
            .Where(pin => pin.StartsWith(
                "LABEL:",
                StringComparison.Ordinal))
            .ToArray();
        HarnessAssert.Equal(1, labelPins.Length,
            "Basic dependency pins do not contain exactly one ROW_LABEL identity");
        HarnessAssert.Equal(expectedFrozenLabelPin, labelPins[0],
            "Basic ROW_LABEL pin did not use the frozen P8-03 snapshot");
        HarnessAssert.True(
            !identity.DependencyPins.Contains(
                BasicLabelPin(_basicRowLabelLive),
                StringComparer.Ordinal),
            "Basic ROW_LABEL pin followed the drifted live label version");
        HarnessAssert.True(
            !identity.DependencyPins.Contains(
                BasicLabelPin(_basicRowLabelDuplicate),
                StringComparer.Ordinal),
            "Basic ROW_LABEL pin selected the cross-scope live duplicate");
        HarnessAssert.Equal(
            _basicRowLabelSnapshot.LabelId,
            _basicRowLabelLive.LabelId,
            "P8-04 fixture live drift did not preserve label owner identity");
        HarnessAssert.True(
            !string.Equals(
                _basicRowLabelSnapshot.VersionId,
                _basicRowLabelLive.VersionId,
                StringComparison.Ordinal),
            "P8-04 fixture did not advance the live label version after snapshot");
        HarnessAssert.True(
            identity.DependencyPins.Count(pin => pin.StartsWith(
                "DYNAMIC_FORM_SCHEMA:",
                StringComparison.Ordinal)) == 1,
            "Basic dependency pins do not contain exactly one Dynamic Form schema identity");
    }

    private static string BasicLabelPin(P8ConfigIdentity label)
        => $"LABEL:{label.LabelId}:{label.VersionId}:" +
           $"{label.VersionNo}:{label.ConfigHash}";

    private static JsonObject[] BasicFieldTargets(
        string dataType,
        params string[] operations)
    {
        var keys = BasicTargetKeys[dataType];
        HarnessAssert.Equal(operations.Length, keys.Length,
            $"Basic fixture key count mismatch for {dataType}");
        return operations.Select((operation, index) => BasicTarget(
                "FIELD",
                keys[index],
                dataType,
                operation))
            .ToArray();
    }

    private async Task<JsonArray> ListBasicVersionsAsync(
        P8Actor actor,
        P8BasicFixture fixture,
        CancellationToken ct)
    {
        var response = await _api.GetAsync(
            $"{BasicConfigRoute(fixture)}/versions",
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P8 Basic versions GET {fixture.Key}");
        var root = ApiHarnessClient.RequiredObject(
            response.Json,
            "P8 Basic versions response");
        return root["items"] as JsonArray
               ?? throw new InvalidOperationException(
                   "P8 Basic versions response lacks items.");
    }

    private async Task<P8BasicConfigIdentity> ReadBasicVersionAsync(
        P8Actor actor,
        P8BasicFixture fixture,
        int versionNo,
        CancellationToken ct)
    {
        var response = await _api.GetAsync(
            $"{BasicConfigRoute(fixture)}/versions/{versionNo}",
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P8 Basic version {versionNo} GET {fixture.Key}");
        var identity = ParseBasicIdentity(response.Json, requireReceipt: false);
        RequireBasicIdentityContract(identity, fixture);
        return identity;
    }

    private async Task<long> CountBasicReceiptsAsync(
        string ownerId,
        string commandId,
        CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(ReceiptsCollection)
            .CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Eq("ownerId", ownerId) &
                Builders<BsonDocument>.Filter.Eq("commandId", commandId),
                cancellationToken: ct);

    private static JsonObject BasicSummaryRequest(
        P8BasicFixture fixture,
        bool forceRefresh = false)
        => new()
        {
            ["scopeAssignmentId"] = fixture.Assignment.Id,
            ["dynamicFormTemplateId"] = fixture.DynamicFormTemplateId,
            ["periodScopeMode"] = "ALL_PERIODS",
            ["sourceScopeMode"] = "DIRECT_CHILDREN_OR_SELF",
            ["forceRefresh"] = forceRefresh,
            ["includeSourceRows"] = true,
            ["maxTextChars"] = 12000
        };

    private static void RequireVersionIdentity(
        JsonObject version,
        P8BasicConfigIdentity expected)
    {
        HarnessAssert.Equal(expected.VersionId,
            RequiredString(version, "versionId"),
            "Basic versionId list/detail mismatch");
        HarnessAssert.Equal(expected.VersionNo,
            RequiredInt(version, "versionNo"),
            "Basic versionNo list/detail mismatch");
        HarnessAssert.Equal(expected.Revision,
            RequiredLong(version, "revision"),
            "Basic version revision mismatch");
        HarnessAssert.Equal(expected.Status,
            RequiredString(version, "status"),
            "Basic version status mismatch");
        HarnessAssert.Equal(expected.ConfigHash,
            RequiredString(version, "configHash"),
            "Basic version configHash mismatch");
    }
}
