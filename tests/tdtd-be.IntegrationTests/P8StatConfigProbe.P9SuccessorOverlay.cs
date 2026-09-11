using System.Text;
using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string P9SuccessorOverlaySwitch =
        "--p9-successor-overlay";
    private const string P9SuccessorOverlayId = "P9-12-ROLLBACK";
    private const string P9LifecycleOwnerPath =
        "tdtd-be/Services/DynamicFlows/DynamicFlowMappingLifecycleContract.cs";
    private const string P9LifecycleOwnerSha256 =
        "27f16baf0520b6fb15cb2a2f594c5388fc7bbb673386d5c60ca0a73abab89090";
    private const string P7LifecycleOwnerSha256 =
        "a7adc484583b58c324efd81b73de0053e8c8d26af8ef3169ed097a3b5e6403b7";
    private const string P902HandoffPath =
        ".p9-artifacts/handoffs/p9_chain_20260804012144_7185/P9-02.attempt-002.json";
    private const string P902HandoffSha256 =
        "aa5a458fda2dc09d78947ba7e507a7ec4e8ed7204046f9efd3eb759497c5fac9";
    private const string P911HandoffPath =
        ".p9-artifacts/handoffs/p9_chain_20260804012144_7185/P9-11.attempt-002.json";
    private const string P911HandoffSha256 =
        "1401f6b1b6d133d606482db76723a4b3b106388c79254d4b4da78be2a58a62e1";
    private const string P9HistoricalP7LifecyclePath =
        ".p9-artifacts/historical-sha256/a7adc484583b58c324efd81b73de0053e8c8d26af8ef3169ed097a3b5e6403b7.blob";
    private const string P9HistoricalV15LockPath =
        ".p9-artifacts/historical-sha256/9e580106ccee20cd6ff7975dda652833123ef709024bea74087826c34986e42a.blob";
    private const string CurrentCatalogPath =
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json";
    private const string CatalogLockPath =
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json";
    private const string CatalogCurrentV15Sha256 =
        "d5e77af2cf8a4d4d959c8d642b82fa0bccf5b7b3c8369c6122c5875c01e4c535";
    private const string CatalogLockV15Sha256 =
        "9e580106ccee20cd6ff7975dda652833123ef709024bea74087826c34986e42a";
    private const string CatalogLockV16Sha256 =
        "be02e58b97584e7638f591f7a8b37e6cb51bda8ca1ae100ebf1eb4ef9dc8bb6f";
    private const string CatalogV16SemanticSha256 =
        "39cdb98dda168f5901f48a94640fe5d50943c5bd78ed32d5e05e8b719b23d13b";
    private const string SchemaV16SemanticSha256 =
        "da0c80f265845f24aaf282e0a0369272273b1986520dae07171cda85b28b3fed";

    private static readonly string[] P811ExpectedP9SuccessorPaths =
    [
        "docs/AUTOMATION_TEST_PLAN.md",
        "docs/WORK_DONE.md",
        "docs/WORK_PLANNED.md",
        "docs/features/DYNAMIC_FORM_FLOW_TRACE_MATRIX_V1.md",
        "docs/features/DYNAMIC_FORM_FLOW_UI_ROUTE_OWNERSHIP_V1.md",
        "scripts/validate-dynamic-form-flow-capability-catalog.mjs",
        "tdtd-be/Common/Capabilities/DynamicFormFlowCapabilityCatalog.g.cs",
        "tdtd-be/Common/Errors/AppErrorCatalog.cs",
        "tdtd-be/Common/Errors/AppErrorCode.cs",
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json",
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json",
        "tdtd-be/Controllers/AdminOperationsController.cs",
        "tdtd-be/Controllers/WorkAssignmentReportsController.cs",
        "tdtd-be/Program.cs",
        "tdtd-be/Services/Common/JobRunManagementService.cs",
        "tdtd-be/Services/DynamicFlows/DynamicFlowMappingLifecycleContract.cs",
        "tdtd-be/Services/DynamicFlows/DynamicFlowRuntimeEpochCommands.cs",
        "tdtd-be/Services/DynamicFlows/DynamicFlowRuntimeMaterialization.cs",
        "tdtd-be/Services/DynamicFlows/DynamicFlowRuntimeStateProjection.cs",
        "tdtd-be/Services/WorkAssignmentReports/Runtime/WorkReportLifecycleProjectionReconciler.cs",
        "tdtd-be/Services/WorkAssignmentReports/WorkAssignmentReportService.cs",
        "tdtd-be/Services/WorkAssignments/Review/WorkAssignmentReviewService.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/ApiHarnessClient.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/BackendServerLease.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/MongoReplicaSetLease.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P5MaterializationProbe.P601.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P5MaterializationProbe.P7Mapping.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/Program.cs",
        "tdtd-be/tests/tdtd-be.Tests/DynamicFlowMappingLifecycleRerunContractTests.cs",
        "tdtd-be/tests/tdtd-be.Tests/DynamicFlowMappingP708BarrierContractTests.cs",
        "tdtd-be/tests/tdtd-be.Tests/Program.cs",
        "tdtd-be/tools/generate-dynamic-form-flow-capability-catalog.mjs",
        "tdtd-fe/src/api/dynamicFlowTemplateApi.ts",
        "tdtd-fe/src/generated/dynamicFormFlowCapabilityCatalog.generated.ts",
        "tdtd-fe/src/pages/dynamicFlows/DynamicFlowVersionWorkspacePage.tsx",
        "tdtd-fe/src/routes/appRoutes.tsx",
        "tdtd-fe/tests/dynamicFlows/DynamicFlowVersionWorkspacePage.test.tsx"
    ];

    private static bool TryParseP9SuccessorOverlay(
        string[] args,
        out bool enabled,
        out string error)
    {
        enabled = false;
        error = string.Empty;
        var indexes = args.Select((value, index) => (value, index))
            .Where(item => string.Equals(
                item.value,
                P9SuccessorOverlaySwitch,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToArray();
        if (indexes.Length == 0)
            return true;
        if (indexes.Length != 1 || indexes[0] + 1 >= args.Length ||
            !string.Equals(
                args[indexes[0] + 1],
                P9SuccessorOverlayId,
                StringComparison.Ordinal))
        {
            error = $"{P9SuccessorOverlaySwitch} must occur exactly once with value {P9SuccessorOverlayId}.";
            return false;
        }
        enabled = true;
        return true;
    }

    private async Task<P9P7OwnerSuccessorEvidence>
        VerifyP9P7OwnerSuccessorAsync(
            IReadOnlyDictionary<string, string> actual,
            CancellationToken ct)
    {
        HarnessAssert.True(_p9SuccessorOverlayEnabled,
            "P9 P7-owner successor proof was requested without the explicit overlay.");
        HarnessAssert.Equal(P7OwnerSourcePins.Count, actual.Count,
            "P9 successor P7 owner source cardinality drifted");
        foreach (var pair in P7OwnerSourcePins)
        {
            HarnessAssert.True(actual.TryGetValue(pair.Key, out var hash),
                $"P9 successor omitted P7 owner source {pair.Key}");
            var expected = string.Equals(
                pair.Key, P9LifecycleOwnerPath, StringComparison.Ordinal)
                ? P9LifecycleOwnerSha256
                : pair.Value;
            HarnessAssert.Equal(expected, hash,
                $"Unauthorized P9 successor drifted P7 owner source {pair.Key}");
        }

        var archiveBytes = await File.ReadAllBytesAsync(
            P811Path(P9HistoricalP7LifecyclePath), ct);
        HarnessAssert.Equal(P7LifecycleOwnerSha256, Sha256(archiveBytes),
            "Archived P7 lifecycle owner bytes drifted");
        var handoffBytes = await File.ReadAllBytesAsync(
            P811Path(P902HandoffPath), ct);
        HarnessAssert.Equal(P902HandoffSha256, Sha256(handoffBytes),
            "Effective P9-02 successor handoff drifted");
        var handoff = JsonNode.Parse(Encoding.UTF8.GetString(handoffBytes))
                      as JsonObject
                      ?? throw new InvalidOperationException(
                          "Effective P9-02 successor handoff is not an object.");
        HarnessAssert.Equal("P9_HANDOFF_V1",
            handoff["schemaVersion"]?.GetValue<string>(),
            "P9 lifecycle successor handoff schema drifted");
        HarnessAssert.Equal("p9_chain_20260804012144_7185",
            handoff["chainId"]?.GetValue<string>(),
            "P9 lifecycle successor handoff chain drifted");
        HarnessAssert.Equal("P9-02", handoff["promptId"]?.GetValue<string>(),
            "P9 lifecycle successor handoff prompt drifted");
        HarnessAssert.Equal(2, handoff["attempt"]?.GetValue<int>() ?? -1,
            "P9 lifecycle successor handoff attempt drifted");
        HarnessAssert.Equal("PASS", handoff["status"]?.GetValue<string>(),
            "P9 lifecycle successor handoff is not PASS");
        var changed = handoff["filesChanged"] as JsonArray
                      ?? throw new InvalidOperationException(
                          "P9-02 successor handoff lacks filesChanged.");
        RequireP9HandoffPin(changed, P9LifecycleOwnerPath,
            P9LifecycleOwnerSha256);
        RequireP9HandoffPin(changed, P9HistoricalP7LifecyclePath,
            P7LifecycleOwnerSha256);
        return new P9P7OwnerSuccessorEvidence(
            P9SuccessorOverlayId,
            13,
            P9LifecycleOwnerPath,
            P7LifecycleOwnerSha256,
            P9LifecycleOwnerSha256,
            P9HistoricalP7LifecyclePath,
            P902HandoffPath,
            P902HandoffSha256,
            true);
    }

    private static void RequireP9HandoffPin(
        JsonArray changed,
        string path,
        string sha256)
    {
        HarnessAssert.True(changed.OfType<JsonObject>().Any(item =>
                string.Equals(
                    item["path"]?.GetValue<string>()?.Replace('\\', '/'),
                    path,
                    StringComparison.Ordinal) &&
                string.Equals(
                    item["sha256"]?.GetValue<string>(),
                    sha256,
                    StringComparison.Ordinal)),
            $"P9 handoff lacks exact path/SHA pin for {path}");
    }

    private async Task<P811CatalogStateEvidence>
        VerifyP811CatalogStateAsync(CancellationToken ct)
    {
        if (!_p9SuccessorOverlayEnabled)
        {
            return new P811CatalogStateEvidence(
                await VerifyP811PinnedFilesAsync(
                    P811HistoricalCatalogPins, ct),
                null);
        }

        var stablePins = P811HistoricalCatalogPins
            .Where(pair => !string.Equals(
                pair.Key, CatalogLockPath, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.Ordinal);
        var files = (await VerifyP811PinnedFilesAsync(stablePins, ct))
            .ToList();
        var currentBytes = await File.ReadAllBytesAsync(
            P811Path(CurrentCatalogPath), ct);
        HarnessAssert.Equal(CatalogCurrentV15Sha256, Sha256(currentBytes),
            "P9-12 P8 successor overlay requires rollback CURRENT v1.5");
        var lockBytes = await File.ReadAllBytesAsync(
            P811Path(CatalogLockPath), ct);
        HarnessAssert.Equal(CatalogLockV16Sha256, Sha256(lockBytes),
            "P9-12 P8 successor overlay requires append-only LOCK v1.6");
        var archiveBytes = await File.ReadAllBytesAsync(
            P811Path(P9HistoricalV15LockPath), ct);
        HarnessAssert.Equal(CatalogLockV15Sha256, Sha256(archiveBytes),
            "P9-12 archived v1.5 LOCK drifted");

        var oldLock = JsonNode.Parse(Encoding.UTF8.GetString(archiveBytes))
                      as JsonObject
                      ?? throw new InvalidOperationException(
                          "Archived v1.5 LOCK is not an object.");
        var liveLock = JsonNode.Parse(Encoding.UTF8.GetString(lockBytes))
                       as JsonObject
                       ?? throw new InvalidOperationException(
                           "Live v1.6 LOCK is not an object.");
        HarnessAssert.Equal(
            oldLock["lockVersion"]?.GetValue<int>() ?? -1,
            liveLock["lockVersion"]?.GetValue<int>() ?? -2,
            "LOCK schema version changed across P9 append-only publication");
        var oldEntries = oldLock["publishedCatalogs"] as JsonArray
                         ?? throw new InvalidOperationException(
                             "Archived v1.5 LOCK lacks publishedCatalogs.");
        var liveEntries = liveLock["publishedCatalogs"] as JsonArray
                          ?? throw new InvalidOperationException(
                              "Live v1.6 LOCK lacks publishedCatalogs.");
        HarnessAssert.Equal(6, oldEntries.Count,
            "Archived v1.5 LOCK entry count drifted");
        HarnessAssert.Equal(7, liveEntries.Count,
            "Live v1.6 LOCK must add exactly one entry");
        for (var index = 0; index < oldEntries.Count; index++)
        {
            HarnessAssert.True(JsonNode.DeepEquals(
                    oldEntries[index], liveEntries[index]),
                $"Live v1.6 LOCK changed historical entry {index}");
        }
        var v16 = liveEntries[^1] as JsonObject
                  ?? throw new InvalidOperationException(
                      "Live v1.6 LOCK terminal entry is not an object.");
        HarnessAssert.Equal("1.6", v16["catalogVersion"]?.GetValue<string>(),
            "Live LOCK terminal catalogVersion drifted");
        HarnessAssert.Equal("DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.json",
            v16["catalogFile"]?.GetValue<string>(),
            "Live LOCK terminal catalog file drifted");
        HarnessAssert.Equal("DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.schema.json",
            v16["schemaFile"]?.GetValue<string>(),
            "Live LOCK terminal schema file drifted");
        HarnessAssert.Equal(CatalogV16SemanticSha256,
            v16["catalogSha256"]?.GetValue<string>(),
            "Live LOCK terminal catalog semantic pin drifted");
        HarnessAssert.Equal(SchemaV16SemanticSha256,
            v16["schemaSha256"]?.GetValue<string>(),
            "Live LOCK terminal schema semantic pin drifted");

        var p911Bytes = await File.ReadAllBytesAsync(
            P811Path(P911HandoffPath), ct);
        HarnessAssert.Equal(P911HandoffSha256, Sha256(p911Bytes),
            "Effective P9-11 preterminal handoff drifted");
        var p911 = JsonNode.Parse(Encoding.UTF8.GetString(p911Bytes))
                   as JsonObject
                   ?? throw new InvalidOperationException(
                       "Effective P9-11 handoff is not an object.");
        HarnessAssert.Equal("P9-11", p911["promptId"]?.GetValue<string>(),
            "P9 preterminal handoff prompt drifted");
        HarnessAssert.Equal(2, p911["attempt"]?.GetValue<int>() ?? -1,
            "P9 preterminal handoff attempt drifted");
        HarnessAssert.Equal("PASS", p911["status"]?.GetValue<string>(),
            "P9 preterminal handoff is not PASS");
        HarnessAssert.Equal("P9-12", p911["nextPrompt"]?.GetValue<string>(),
            "P9 preterminal handoff nextPrompt drifted");
        HarnessAssert.Equal(
            "9c1acc91c5c1d2683da51c074e08bdac264936bf7921c93cb024c1bbe4c0c396",
            p911["exports"]?["catalogCandidate"]?["stageLock"]?["sha256"]
                ?.GetValue<string>(),
            "P9-11 candidate stage-lock pin drifted");
        var cumulative = p911["exports"]?["cumulative"] as JsonObject
                         ?? throw new InvalidOperationException(
                             "P9-11 handoff lacks cumulative exports.");
        var requirements = cumulative["requirementsVerified"] as JsonArray;
        var groups = cumulative["caseGroupsVerified"] as JsonArray;
        HarnessAssert.Equal(28, requirements?.Count ?? -1,
            "P9-11 cumulative requirement count drifted");
        HarnessAssert.Equal(11, groups?.Count ?? -1,
            "P9-11 cumulative case-group count drifted");
        HarnessAssert.Equal(250, groups?.OfType<JsonObject>().Sum(item =>
            item["expected"]?.GetValue<int>() ?? 0) ?? -1,
            "P9-11 cumulative case count drifted");

        files.Add(new P811FileFingerprint(
            CatalogLockPath, lockBytes.LongLength, CatalogLockV16Sha256));
        files.Add(new P811FileFingerprint(
            P9HistoricalV15LockPath,
            archiveBytes.LongLength,
            CatalogLockV15Sha256));
        files.Add(new P811FileFingerprint(
            P911HandoffPath, p911Bytes.LongLength, P911HandoffSha256));
        return new P811CatalogStateEvidence(
            files.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray(),
            new P811P9SuccessorOverlayEvidence(
                P9SuccessorOverlayId,
                CatalogCurrentV15Sha256,
                CatalogLockV15Sha256,
                CatalogLockV16Sha256,
                6,
                1,
                CatalogV16SemanticSha256,
                SchemaV16SemanticSha256,
                P911HandoffPath,
                P911HandoffSha256,
                80,
                P811ExpectedP9SuccessorPaths.Length,
                true));
    }
}

internal sealed record P9P7OwnerSuccessorEvidence(
    string OverlayId,
    int CanonicalOwnerCount,
    string SuccessorPath,
    string PreviousSha256,
    string CurrentSha256,
    string PreviousArchivePath,
    string AuthorizingHandoffPath,
    string AuthorizingHandoffSha256,
    bool Passed);

internal sealed record P811CatalogStateEvidence(
    IReadOnlyList<P811FileFingerprint> Files,
    P811P9SuccessorOverlayEvidence? Overlay);

internal sealed record P811P9SuccessorOverlayEvidence(
    string OverlayId,
    string RollbackCurrentSha256,
    string HistoricalLockSha256,
    string AppendOnlyLockSha256,
    int HistoricalPrefixEntries,
    int AppendedEntries,
    string CatalogV16SemanticSha256,
    string SchemaV16SemanticSha256,
    string PreterminalHandoffPath,
    string PreterminalHandoffSha256,
    int ExpectedP7VerifierFailures,
    int ExpectedP7VerifierUniquePaths,
    bool Passed);
