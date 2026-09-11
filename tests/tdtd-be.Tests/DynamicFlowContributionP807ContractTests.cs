using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowContributionP807ContractTests
{
    public static void Run()
    {
        ExcludeIsTheDefaultAndIncludeRequiresWarning();
        HistoricalLockRequestHashRemainsReplayCompatible();
        PolicyHashIsDeterministicAndVersionBound();
        IncludeRequiresExactLockedExcludeOrigin();
        EmptyStatisticProfileNormalizesWithoutHistoricalHashDrift();
        MixedContributionIndexesNeverCompoundParallelArrays();
        SameNameIndexMigrationIsRestartIdempotent();
    }

    private static void ExcludeIsTheDefaultAndIncludeRequiresWarning()
    {
        var excluded = DynamicFlowContributionPolicyContract.ResolveLockSelection(null, null);
        AssertEqual(DynamicFlowContributionPolicyContract.Exclude, excluded.Policy, "default policy");
        AssertEqual<string?>(null, excluded.Warning, "default warning");

        var warningError = AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_CONTRIBUTION_WARNING_REQUIRED,
            () => DynamicFlowContributionPolicyContract.ResolveLockSelection("INCLUDE", false));
        AssertContains(warningError.Details?.ToString() ?? string.Empty, "acknowledgeContributionWarning", "warning path");

        var included = DynamicFlowContributionPolicyContract.ResolveLockSelection(" include ", true);
        AssertEqual(DynamicFlowContributionPolicyContract.Include, included.Policy, "include policy");
        AssertEqual(DynamicFlowContributionPolicyContract.IncludeWarning, included.Warning, "include warning");

        _ = AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_CONTRIBUTION_POLICY_INVALID,
            () => DynamicFlowContributionPolicyContract.ResolveLockSelection("SOURCE_ONLY", true));
    }

    private static void HistoricalLockRequestHashRemainsReplayCompatible()
    {
        const string familyId = "000000000000000000000001";
        const string versionId = "000000000000000000000002";
        var expectedPayloadHash = new string('a', 64);
        var omitted = new LockDynamicFlowTemplateVersionRequest
        {
            ExpectedFamilyRevision = 7,
            ExpectedDraftRevision = 3,
            ExpectedPayloadHash = expectedPayloadHash
        };
        var omittedSelection = DynamicFlowContributionPolicyContract.ResolveLockSelection(
            omitted.ContributionPolicy,
            omitted.AcknowledgeContributionWarning);
        var legacyHash = DynamicFlowTemplateService.BuildLockRequestHash(
            familyId,
            versionId,
            expectedPayloadHash,
            omitted,
            omittedSelection);
        AssertEqual(
            "4016de1f7d5a6703196e1494ff229cf12801d984c47a48475958cfca74492384",
            legacyHash,
            "omitted P8 fields must retain exact pre-P8 LOCK request hash");

        var explicitExclude = new LockDynamicFlowTemplateVersionRequest
        {
            ExpectedFamilyRevision = omitted.ExpectedFamilyRevision,
            ExpectedDraftRevision = omitted.ExpectedDraftRevision,
            ExpectedPayloadHash = expectedPayloadHash,
            ContributionPolicy = "EXCLUDE"
        };
        var explicitExcludeHash = DynamicFlowTemplateService.BuildLockRequestHash(
            familyId,
            versionId,
            expectedPayloadHash,
            explicitExclude,
            DynamicFlowContributionPolicyContract.ResolveLockSelection("EXCLUDE", null));
        AssertFalse(
            string.Equals(legacyHash, explicitExcludeHash, StringComparison.Ordinal),
            "explicit contribution policy must use expanded P8 request hash");

        var explicitAcknowledgement = new LockDynamicFlowTemplateVersionRequest
        {
            ExpectedFamilyRevision = omitted.ExpectedFamilyRevision,
            ExpectedDraftRevision = omitted.ExpectedDraftRevision,
            ExpectedPayloadHash = expectedPayloadHash,
            AcknowledgeContributionWarning = false
        };
        var explicitAcknowledgementHash = DynamicFlowTemplateService.BuildLockRequestHash(
            familyId,
            versionId,
            expectedPayloadHash,
            explicitAcknowledgement,
            DynamicFlowContributionPolicyContract.ResolveLockSelection(null, false));
        AssertFalse(
            string.Equals(legacyHash, explicitAcknowledgementHash, StringComparison.Ordinal),
            "explicit warning acknowledgement must use expanded P8 request hash");
    }

    private static void PolicyHashIsDeterministicAndVersionBound()
    {
        var excluded = Version("000000000000000000000011", versionNo: 1);
        DynamicFlowContributionPolicyContract.ApplyLockedPolicy(
            excluded,
            DynamicFlowContributionPolicyContract.ResolveLockSelection(null, null));
        var firstHash = excluded.ContributionPolicyHash;
        DynamicFlowContributionPolicyContract.ValidateLockedPolicy(excluded);
        AssertEqual(
            firstHash,
            DynamicFlowContributionPolicyContract.ComputePolicyHash(
                excluded,
                DynamicFlowContributionPolicyContract.Exclude),
            "deterministic policy hash");

        var anotherVersion = Version("000000000000000000000012", versionNo: 2);
        DynamicFlowContributionPolicyContract.ApplyLockedPolicy(
            anotherVersion,
            DynamicFlowContributionPolicyContract.ResolveLockSelection(null, null));
        AssertFalse(
            string.Equals(firstHash, anotherVersion.ContributionPolicyHash, StringComparison.Ordinal),
            "policy hash must bind version identity");

        excluded.ContributionWarning = DynamicFlowContributionPolicyContract.IncludeWarning;
        AssertThrows<InvalidOperationException>(
            () => DynamicFlowContributionPolicyContract.ValidateLockedPolicy(excluded));

        var historical = Version("000000000000000000000013", versionNo: 3);
        DynamicFlowContributionPolicyContract.ValidateLockedPolicy(historical);
    }

    private static void IncludeRequiresExactLockedExcludeOrigin()
    {
        var origin = Version("000000000000000000000021", versionNo: 1);
        origin.Status = DynamicFlowTemplateVersionStatuses.Locked;
        DynamicFlowContributionPolicyContract.ApplyLockedPolicy(
            origin,
            DynamicFlowContributionPolicyContract.ResolveLockSelection("EXCLUDE", null));

        var draft = Version("000000000000000000000022", versionNo: 2);
        draft.OriginFamilyId = draft.TemplateId;
        draft.OriginVersionId = origin.Id;
        DynamicFlowContributionPolicyContract.EnsureIncludeOrigin(draft, origin);

        var edited = Version("000000000000000000000023", versionNo: 2);
        edited.OriginFamilyId = edited.TemplateId;
        edited.OriginVersionId = origin.Id;
        edited.PayloadHash = new string('b', 64);
        _ = AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_CONTRIBUTION_BASELINE_CONFLICT,
            () => DynamicFlowContributionPolicyContract.EnsureIncludeOrigin(edited, origin));

        var initialInclude = Version("000000000000000000000024", versionNo: 1);
        _ = AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_CONTRIBUTION_BASELINE_CONFLICT,
            () => DynamicFlowContributionPolicyContract.EnsureIncludeOrigin(initialInclude, null));
    }

    private static void EmptyStatisticProfileNormalizesWithoutHistoricalHashDrift()
    {
        var authoringPayload = MinimalPayload();
        authoringPayload.StatisticProfile["diffMode"] = JsonSerializer.SerializeToElement("NONE");
        DynamicFlowTemplateService.NormalizeEmptyStatisticProfileForAuthoring(authoringPayload);

        var normalized = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            authoringPayload,
            null);
        AssertEqual(0, normalized.Payload.StatisticProfile.Count, "NONE profile normalization");
        AssertContains(normalized.CanonicalJson, "\"statisticProfile\":{}", "normalized profile bytes");

        var historicalPayload = MinimalPayload();
        historicalPayload.StatisticProfile["diffMode"] = JsonSerializer.SerializeToElement("NONE");
        var preserved = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            historicalPayload,
            null);
        AssertEqual(1, preserved.Payload.StatisticProfile.Count, "historical profile preservation");
        AssertContains(preserved.CanonicalJson, "\"diffMode\":\"NONE\"", "historical profile bytes");
    }

    private static void MixedContributionIndexesNeverCompoundParallelArrays()
    {
        var source = ReadBackendSource("Data/Indexes/MongoIndexInitializer.cs");
        const string legacyName = "ix_workReportStatisticRebuildJobs_contribution_source_v2";
        const string flowName = "ix_workReportStatisticRebuildJobs_flow_contribution_source_v2";
        const string nonFlowName = "ix_workReportStatisticRebuildJobs_nonflow_contribution_source_v2";

        AssertEqual(1, CountOccurrences(source, $"name: \"{legacyName}\""), "legacy index migration cardinality");
        AssertEqual(1, CountOccurrences(source, $"name: \"{flowName}\""), "flow index cardinality");
        AssertEqual(1, CountOccurrences(source, $"name: \"{nonFlowName}\""), "non-flow index cardinality");

        var migration = ReadIndexSpec(source, legacyName);
        AssertContains(migration, "MigrateExactOwnedIndexAsync", "legacy exact migration");
        AssertContains(migration, "flowContributionSources.sourceReportId", "legacy flow key");
        AssertContains(migration, "nonFlowContributionSources.sourceReportId", "legacy non-flow key");
        AssertContains(migration, "P9_CONTRIBUTION_V2", "legacy operation version");

        var flow = ReadIndexSpec(source, flowName);
        AssertContains(flow, "EnsureFailClosedBySpecAsync", "flow fail-closed ensure");
        AssertContains(flow, "flowContributionSources.sourceReportId", "flow source key");
        AssertEqual(2, CountOccurrences(flow, "flowContributionOperationVersion"), "flow version key and partial");
        AssertFalse(flow.Contains("nonFlowContributionSources.sourceReportId", StringComparison.Ordinal),
            "flow index must not compound the non-flow source array");
        AssertContains(flow, "P9_CONTRIBUTION_V2", "flow operation version");

        var nonFlow = ReadIndexSpec(source, nonFlowName);
        AssertContains(nonFlow, "EnsureFailClosedBySpecAsync", "non-flow fail-closed ensure");
        AssertContains(nonFlow, "nonFlowContributionSources.sourceReportId", "non-flow source key");
        AssertEqual(2, CountOccurrences(nonFlow, "flowContributionOperationVersion"), "non-flow version key and partial");
        AssertFalse(nonFlow.Contains("flowContributionSources.sourceReportId", StringComparison.Ordinal),
            "non-flow index must not compound the flow source array");
        AssertContains(nonFlow, "P9_CONTRIBUTION_V2", "non-flow operation version");

        AssertFalse(source.IndexOf($"name: \"{flowName}\"", StringComparison.Ordinal) <
                    source.IndexOf($"name: \"{legacyName}\"", StringComparison.Ordinal),
            "legacy parallel-array index must migrate before replacement indexes are ensured");
    }

    private static void SameNameIndexMigrationIsRestartIdempotent()
    {
        var source = ReadBackendSource("Data/Indexes/MongoIndexInitializer.cs");
        AssertContains(
            source,
            "IsSameSpec(current, acceptedReplacement)",
            "same-name migrated replacement exact-spec acceptance");
        AssertContains(
            source,
            "legacyGenerationIndex,\n                generationIndex,\n                ct);",
            "review index passes exact legacy and replacement specs");
        AssertFalse(
            source.IndexOf(
                "IsSameSpec(current, acceptedReplacement)",
                StringComparison.Ordinal) >
            source.IndexOf(
                "if (!IsSameSpec(current, expectedLegacy))",
                StringComparison.Ordinal),
            "accepted replacement must be recognized before legacy mismatch rejection");
    }
    private static string ReadIndexSpec(string source, string name)
    {
        var nameOffset = source.IndexOf($"name: \"{name}\"", StringComparison.Ordinal);
        if (nameOffset < 0)
            throw new InvalidOperationException($"Mongo index source was not found: {name}");
        var callOffset = source.LastIndexOf("await MongoIndexEnsureHelper.", nameOffset, StringComparison.Ordinal);
        var endOffset = source.IndexOf("), ct);", nameOffset, StringComparison.Ordinal);
        if (callOffset < 0 || endOffset < 0)
            throw new InvalidOperationException($"Mongo index source was incomplete: {name}");
        return source[callOffset..(endOffset + "), ct);".Length)];
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        for (var offset = 0; ;)
        {
            offset = source.IndexOf(value, offset, StringComparison.Ordinal);
            if (offset < 0)
                return count;
            count++;
            offset += value.Length;
        }
    }

    private static string ReadBackendSource(string relativePath)
    {
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var directory = new DirectoryInfo(seed); directory is not null; directory = directory.Parent)
            {
                var root = File.Exists(Path.Combine(directory.FullName, "tdtd-be.csproj"))
                    ? directory.FullName
                    : Path.Combine(directory.FullName, "tdtd-be");
                if (!File.Exists(Path.Combine(root, "tdtd-be.csproj")))
                    continue;
                var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path))
                    return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            }
        }
        throw new InvalidOperationException($"Backend source was not found: {relativePath}");
    }

    private static DynamicFlowTemplateVersion Version(string id, int versionNo)
        => new()
        {
            Id = id,
            TemplateId = "000000000000000000000001",
            RootDynamicFormTemplateId = "000000000000000000000002",
            VersionNo = versionNo,
            Status = DynamicFlowTemplateVersionStatuses.Draft,
            SchemaVersion = DynamicFlowDefinitionSchema.CurrentVersion,
            AdapterVersion = DynamicFlowDefinitionSchema.CurrentAdapterVersion,
            CatalogVersion = "1.4",
            CatalogSemanticHash = new string('c', 64),
            PayloadJson = "{\"p7\":\"baseline\"}",
            PayloadHash = new string('a', 64),
            IsDeleted = false
        };

    private static DynamicFlowTemplatePayloadDto MinimalPayload()
        => new()
        {
            SchemaVersion = DynamicFlowDefinitionSchema.CurrentVersion,
            ArchetypeId = "FLOW-T01",
            EntryStepId = "step-1",
            RootDynamicFormTemplateId = "000000000000000000000002",
            FormNodes = new List<DynamicFlowFormNodeDto>
            {
                new()
                {
                    FormNodeId = "form-node-1",
                    Role = "ROOT",
                    DynamicFormTemplateId = "000000000000000000000002"
                }
            },
            Nodes = new List<DynamicFlowTopologyNodeDto>
            {
                new()
                {
                    NodeId = "step-1",
                    NodeCode = "STEP_1",
                    NodeKind = DynamicFlowNodeKinds.FormStep,
                    FormNodeId = "form-node-1",
                    DeclaredRoles = new List<string> { "OWNER" }
                }
            }
        };

    private static AppException AssertThrows(AppErrorCode code, Action action)
    {
        try
        {
            action();
        }
        catch (AppException error) when (error.Code == code)
        {
            return error;
        }
        catch (AppException error)
        {
            throw new InvalidOperationException($"Expected {code}, got {error.Code}.", error);
        }
        throw new InvalidOperationException($"Expected {code}, but no exception was thrown.");
    }

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}, but no exception was thrown.");
    }

    private static void AssertContains(string actual, string expected, string context)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context}: expected '{expected}' in '{actual}'.");
    }

    private static void AssertEqual<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected '{expected}', got '{actual}'.");
    }

    private static void AssertFalse(bool value, string context)
    {
        if (value)
            throw new InvalidOperationException(context);
    }
}
