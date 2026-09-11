using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

internal static class FoundationRefreshIdentityCases
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("foundation V2 identity replays same pins and splits config or membership", CanonicalIdentity),
        ("foundation V2 generation binds identity, source, config and membership", CanonicalGeneration),
        ("foundation V2 header ignores request envelope and binds logical pins", CanonicalHeader),
        ("foundation V2 supersedes config A within the same current family", CurrentPublicationSupersession),
        ("foundation refresh binds all supported outer authorization scopes", FoundationScopeBinding),
        ("foundation refresh keeps outer and inner activation bindings separate", FoundationBindingSeparation),
        ("foundation terminal validation terminalizes the claimed inner run", FoundationTerminalizationPolicy),
        ("legacy lifecycle jobs retain the unversioned identity shape", LegacyIdentityShape)
    ];

    private static Task CanonicalIdentity()
    {
        const string reportId = "111111111111111111111111";
        var eventKey = Hash('a');
        var scopeA = Hash('b');
        var scopeB = Hash('c');
        var membersA = Hash('d');
        var membersB = Hash('e');

        var first = StatRunDirectProjectionService.BuildFoundationRefreshCanonicalIdentity(
            reportId, eventKey, scopeA, membersA);
        var replay = StatRunDirectProjectionService.BuildFoundationRefreshCanonicalIdentity(
            reportId, eventKey, scopeA, membersA);
        var configRefresh = StatRunDirectProjectionService.BuildFoundationRefreshCanonicalIdentity(
            reportId, eventKey, scopeB, membersA);
        var membershipRefresh = StatRunDirectProjectionService.BuildFoundationRefreshCanonicalIdentity(
            reportId, eventKey, scopeA, membersB);

        Equal(first, replay, "same logical pins must converge on one V2 identity");
        NotEqual(first.Key, configRefresh.Key, "config/publication scope A to B must create a new identity");
        NotEqual(first.RunId, configRefresh.RunId, "config/publication scope A to B must create a new run");
        NotEqual(first.DedupeKey, configRefresh.DedupeKey, "config/publication scope A to B must create a new dedupe key");
        NotEqual(first.ReceiptId, configRefresh.ReceiptId, "config/publication scope A to B must create a new receipt");
        NotEqual(first.Key, membershipRefresh.Key, "membership change must create a new identity");
        Equal(WorkReportDirectProjectionIdentityVersions.FoundationRefreshV2, first.Version,
            "model and projector must share the V2 identity version");
        Check(IsLowerHex(first.Key, 64), "identity key must be lowercase SHA-256");
        Check(IsLowerHex(first.RunId, 24), "run id must be a stable ObjectId-shaped value");
        Check(IsLowerHex(first.ReceiptId, 64), "receipt id must be lowercase SHA-256");
        Check(first.DedupeKey == $"p9-fdn-refresh-v2:{first.Key}", "dedupe key formula");
        return Task.CompletedTask;
    }

    private static Task CanonicalGeneration()
    {
        const string reportId = "111111111111111111111111";
        const string chainId = "p9_chain_20260804012144_7185";
        const string promptId = StatRunCapabilityActivation.LifecycleRequiredPromptId;
        var eventKey = Hash('a');
        var scopeA = Hash('b');
        var scopeB = Hash('c');
        var membersA = Hash('d');
        var membersB = Hash('e');
        var identityA = StatRunDirectProjectionService.BuildFoundationRefreshCanonicalIdentity(
            reportId, eventKey, scopeA, membersA);
        var identityB = StatRunDirectProjectionService.BuildFoundationRefreshCanonicalIdentity(
            reportId, eventKey, scopeB, membersA);

        var first = StatRunDirectProjectionService.BuildFoundationRefreshGenerationId(
            identityA.Key, chainId, promptId, reportId, eventKey, scopeA, membersA);
        var replay = StatRunDirectProjectionService.BuildFoundationRefreshGenerationId(
            identityA.Key, chainId, promptId, reportId, eventKey, scopeA, membersA);
        var configRefresh = StatRunDirectProjectionService.BuildFoundationRefreshGenerationId(
            identityB.Key, chainId, promptId, reportId, eventKey, scopeB, membersA);
        var membershipRefresh = StatRunDirectProjectionService.BuildFoundationRefreshGenerationId(
            StatRunDirectProjectionService.BuildFoundationRefreshCanonicalIdentity(
                reportId, eventKey, scopeA, membersB).Key,
            chainId, promptId, reportId, eventKey, scopeA, membersB);

        Equal(first, replay, "same V2 pins must converge on one generation");
        NotEqual(first, configRefresh, "config/publication scope A to B must create a new generation");
        NotEqual(first, membershipRefresh, "membership change must create a new generation");
        Check(IsLowerHex(first, 64), "generation id must be lowercase SHA-256");
        return Task.CompletedTask;
    }

    private static Task CanonicalHeader()
    {
        var identity = StatRunDirectProjectionService.BuildFoundationRefreshCanonicalIdentity(
            "111111111111111111111111", Hash('a'), Hash('b'), Hash('d'));
        var job = BuildJob(identity);
        var expected = StatRunDirectProjectionService.ComputeFoundationRefreshImmutableHeaderHash(job);

        job.CommandId = "a client command that must not enter the V2 header";
        job.RequestHash = Hash('f');
        job.ImmutableHeaderHash = Hash('0');
        job.ReceiptResponseHash = Hash('1');
        job.ReceiptAcceptedAtUtc = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        job.RequestedByUserId = "aaaaaaaaaaaaaaaaaaaaaaaa";
        job.CreatedAtUtc = new DateTime(2031, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        job.UpdatedAtUtc = new DateTime(2032, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Equal(expected,
            StatRunDirectProjectionService.ComputeFoundationRefreshImmutableHeaderHash(job),
            "request envelope and persistence timestamps must not perturb a logical replay");

        job.ConfigRevision++;
        NotEqual(expected,
            StatRunDirectProjectionService.ComputeFoundationRefreshImmutableHeaderHash(job),
            "a logical config pin must be bound by the immutable header");
        job.ConfigRevision--;
        job.DirectProjectionIdentityKey = Hash('9');
        NotEqual(expected,
            StatRunDirectProjectionService.ComputeFoundationRefreshImmutableHeaderHash(job),
            "the canonical V2 identity must be bound by the immutable header");
        return Task.CompletedTask;
    }

    private static Task CurrentPublicationSupersession()
    {
        var identityA = StatRunDirectProjectionService.BuildFoundationRefreshCanonicalIdentity(
            "111111111111111111111111", Hash('a'), Hash('b'), Hash('d'));
        var identityB = StatRunDirectProjectionService.BuildFoundationRefreshCanonicalIdentity(
            "111111111111111111111111", Hash('a'), Hash('c'), Hash('d'));
        var configA = BuildJob(identityA);
        var configB = BuildJob(identityB);
        configB.WorkAssignmentId = "bbbbbbbbbbbbbbbbbbbbbbbb";
        configB.PublicationScopeKey = Hash('c');
        configB.ConfigId = "cccccccccccccccccccccccc";
        configB.ConfigVersionId = "dddddddddddddddddddddddd";
        configB.ConfigVersionNo = 3;
        configB.ConfigRevision = 14;
        configB.ConfigHash = Hash('5');

        var render = new RenderArgs<WorkReportStatisticRebuildJob>(
            BsonSerializer.LookupSerializer<WorkReportStatisticRebuildJob>(),
            BsonSerializer.SerializerRegistry);
        var familyA = StatRunDirectProjectionService.BuildCurrentPublicationFamilyFilter(
            Builders<WorkReportStatisticRebuildJob>.Filter, configA).Render(render);
        var familyB = StatRunDirectProjectionService.BuildCurrentPublicationFamilyFilter(
            Builders<WorkReportStatisticRebuildJob>.Filter, configB).Render(render);
        Equal(familyA, familyB,
            "config A and B with different trigger assignments must share one publication family");
        Check(!familyA.Contains("publicationScopeKey") &&
              !familyA.Contains("workAssignmentId") &&
              !familyA.Contains("configId") &&
              !familyA.Contains("configVersionId"),
            "current family must cross config and trigger-assignment identities");
        foreach (var field in new[]
                 {
                     "workId", "periodInstanceKey", "periodKind",
                     "dynamicFormFamilyId", "dynamicFormTemplateId",
                     "dynamicFormVersionNo", "dynamicFormSchemaHash",
                     "candidateChainId", "candidatePromptId", "isCurrentPublication"
                 })
        {
            Check(familyA.Contains(field), $"current family must bind {field}");
        }

        var update = StatRunDirectProjectionService
            .BuildCurrentPublicationSupersessionUpdate(
                "222222222222222222222222",
                new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc))
            .Render(render);
        var set = update["$set"].AsBsonDocument;
        Check(!set["isCurrentPublication"].AsBoolean,
            "config A must stop being current when B publishes");
        Equal(WorkReportStatisticRebuildJobFreshnessStates.Stale,
            set["freshnessState"].AsString,
            "config A must become stale when B publishes");
        Equal("LIFECYCLE_SUPERSEDED", set["staleReason"].AsString,
            "config A must carry the canonical supersession reason");

        configB.DynamicFormTemplateId = "eeeeeeeeeeeeeeeeeeeeeeee";
        var otherTemplate =
            StatRunDirectProjectionService.BuildCurrentPublicationFamilyFilter(
                Builders<WorkReportStatisticRebuildJob>.Filter, configB)
                .Render(render);
        NotEqual(familyA, otherTemplate,
            "another template must retain an independent current publication");
        configB.DynamicFormTemplateId = configA.DynamicFormTemplateId;
        configB.PeriodInstanceKey = "2026-10";
        var otherPeriod =
            StatRunDirectProjectionService.BuildCurrentPublicationFamilyFilter(
                Builders<WorkReportStatisticRebuildJob>.Filter, configB)
                .Render(render);
        NotEqual(familyA, otherPeriod,
            "another period must retain an independent current publication");
        return Task.CompletedTask;
    }

    private static Task FoundationScopeBinding()
    {
        const string workId = "888888888888888888888888";
        const string assignmentId = "999999999999999999999999";
        const string rootAssignmentId = "aaaaaaaaaaaaaaaaaaaaaaaa";
        Check(StatRunDirectProjectionService.FoundationScopeMatches(
                "WORK", workId, workId, assignmentId, rootAssignmentId),
            "WORK scope must bind ScopeId to WorkId");
        Check(StatRunDirectProjectionService.FoundationScopeMatches(
                "ASSIGNMENT", assignmentId, workId, assignmentId, rootAssignmentId),
            "ASSIGNMENT scope must bind the authoritative source assignment");
        Check(StatRunDirectProjectionService.FoundationScopeMatches(
                "ROOT", rootAssignmentId, workId, assignmentId, rootAssignmentId),
            "ROOT scope must bind the authoritative root assignment");
        Check(!StatRunDirectProjectionService.FoundationScopeMatches(
                "ASSIGNMENT", rootAssignmentId, workId, assignmentId, rootAssignmentId),
            "a mismatched ASSIGNMENT scope must fail closed");
        Check(!StatRunDirectProjectionService.FoundationScopeMatches(
                "UNKNOWN", workId, workId, assignmentId, rootAssignmentId),
            "an unknown scope type must fail closed");
        Check(!StatRunDirectProjectionService.FoundationScopeMatches(
                "ROOT", null, workId, assignmentId, rootAssignmentId),
            "a null scope id must fail closed");
        return Task.CompletedTask;
    }

    private static Task FoundationBindingSeparation()
    {
        var outer = new StatRunCandidateBinding(
            StatRunCapabilityActivation.RequiredChainId,
            StatRunCapabilityActivation.PublishedPromptId,
            9,
            StatRunCapabilityActivation.RequiredCatalogVersion,
            StatRunCapabilityActivation.PublishedCatalogRawSha256,
            StatRunCapabilityActivation.PublishedCatalogSemanticSha256,
            StatRunCapabilityActivation.PublishedSchemaRawSha256,
            StatRunCapabilityActivation.PublishedSchemaSemanticSha256,
            StatRunCapabilityActivation.PublishedSealStageLockRawSha256,
            "tdtd_p9_test",
            Array.Empty<string>());
        var inner = new StatRunCandidateBinding(
            StatRunCapabilityActivation.RequiredChainId,
            StatRunCapabilityActivation.LifecycleRequiredPromptId,
            0,
            StatRunCapabilityActivation.RequiredCatalogVersion,
            StatRunCapabilityActivation.RequiredCandidateCatalogRawSha256,
            StatRunCapabilityActivation.RequiredCandidateCatalogSemanticSha256,
            StatRunCapabilityActivation.RequiredCandidateSchemaRawSha256,
            StatRunCapabilityActivation.RequiredCandidateSchemaSemanticSha256,
            StatRunCapabilityActivation.LifecycleRequiredStageLockRawSha256,
            "tdtd_p9_test",
            Array.Empty<string>());
        var pin = BuildFoundationPin(outer);

        Check(StatRunDirectProjectionService.FoundationRefreshBindingContractMatches(
                pin, outer, inner),
            "a valid P9-12 outer pin must coexist with an independently resolved P9-02 projector binding");
        Check(StatRunDirectProjectionService.FoundationRefreshBindingContractMatches(
                pin, outer, outer),
            "published mode may independently resolve P9-12 for both routes");

        var crossWiredPin = pin with
        {
            CatalogVersion = inner.CatalogVersion,
            CatalogRawSha256 = inner.CatalogRawSha256,
            CatalogSemanticSha256 = inner.CatalogSemanticSha256,
            SchemaRawSha256 = inner.SchemaRawSha256,
            SchemaSemanticSha256 = inner.SchemaSemanticSha256,
            StageLockSha256 = inner.StageLockSha256,
            CandidateChainId = inner.ChainId
        };
        Check(!StatRunDirectProjectionService.FoundationRefreshBindingContractMatches(
                crossWiredPin, outer, inner),
            "an inner P9-02 bundle copied into the outer pin must fail closed");
        Check(!StatRunDirectProjectionService.FoundationRefreshBindingContractMatches(
                pin, inner, outer),
            "swapping the request and projection bindings must fail closed");
        Check(!StatRunDirectProjectionService.FoundationRefreshBindingContractMatches(
                pin, outer, inner with { DatabaseName = "tdtd_p9_other" }),
            "outer and inner bindings must belong to the same database owner");
        return Task.CompletedTask;
    }

    private static Task FoundationTerminalizationPolicy()
    {
        var terminal = new InvalidOperationException("P9_DIRECT_CONFIG_PIN_STALE");
        Check(StatRunDirectProjectionService.ShouldTerminalizeClaimedProjectionFailure(
                true, terminal),
            "a deterministic Foundation failure after claim must terminalize the inner run");
        Check(!StatRunDirectProjectionService.ShouldTerminalizeClaimedProjectionFailure(
                false, terminal),
            "legacy lifecycle projection must retain its existing terminal-stale handling");
        Check(!StatRunDirectProjectionService.ShouldTerminalizeClaimedProjectionFailure(
                true, new InvalidOperationException("P9_DIRECT_RUN_CLAIM_BUSY")),
            "a retryable Foundation validation failure must retain retry handling");
        Check(!StatRunDirectProjectionService.ShouldTerminalizeClaimedProjectionFailure(
                true, new TimeoutException("private dependency detail")),
            "a transient dependency failure must retain bounded retry handling");
        return Task.CompletedTask;
    }

    private static Task LegacyIdentityShape()
    {
        var legacy = new WorkReportStatisticRebuildJob();
        Check(legacy.DirectProjectionIdentityVersion is null,
            "legacy lifecycle identity version must remain absent/null");
        Check(legacy.DirectProjectionIdentityKey is null,
            "legacy lifecycle identity key must remain absent/null");
        return Task.CompletedTask;
    }

    private static StatRunFoundationRefreshPin BuildFoundationPin(
        StatRunCandidateBinding requestBinding)
        => new(
            CapabilityId: StatRunCapabilities.DirectFieldTableLabel,
            RunKind: WorkReportStatisticRebuildJobRunKinds.Foundation,
            WorkId: "888888888888888888888888",
            ScopeType: "WORK",
            ScopeId: "888888888888888888888888",
            SourceReportId: "111111111111111111111111",
            SourceRevision: 7,
            SourceHash: Hash('2'),
            LifecycleRevision: 9,
            DynamicFormTemplateId: "555555555555555555555555",
            ConfigId: "666666666666666666666666",
            ConfigVersionId: "777777777777777777777777",
            ConfigVersionNo: 2,
            ConfigRevision: 13,
            ConfigHash: Hash('4'),
            CatalogVersion: requestBinding.CatalogVersion,
            CatalogRawSha256: requestBinding.CatalogRawSha256,
            CatalogSemanticSha256: requestBinding.CatalogSemanticSha256,
            SchemaRawSha256: requestBinding.SchemaRawSha256,
            SchemaSemanticSha256: requestBinding.SchemaSemanticSha256,
            StageLockSha256: requestBinding.StageLockSha256,
            CandidateChainId: requestBinding.ChainId,
            FlowTemplateId: null,
            FlowFamilyRevision: null,
            FlowTemplateVersionNo: null,
            FlowTemplateVersionId: null,
            FlowPayloadHash: null,
            FlowCatalogVersion: null,
            FlowCatalogSemanticHash: null,
            FlowInstanceId: null,
            FlowInstanceRevision: null,
            FlowInstanceState: null,
            FlowExecutionEpoch: null,
            FlowExecutionEpochId: null,
            FlowExecutionEpochRevision: null,
            FlowExecutionEpochState: null,
            FlowBranchId: null,
            FlowStepId: null,
            FlowAttemptNo: null,
            FlowStepInstanceId: null,
            FlowStepInstanceRevision: null,
            FlowStepInstanceState: null,
            PeriodKey: "2026-09",
            PeriodInstanceKey: "2026-09",
            PeriodKind: "MONTH",
            PeriodStartUtc: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            PeriodEndUtc: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

    private static WorkReportStatisticRebuildJob BuildJob(
        StatRunDirectProjectionCanonicalIdentity identity)
    {
        const string reportId = "111111111111111111111111";
        const string chainId = "p9_chain_20260804012144_7185";
        const string promptId = StatRunCapabilityActivation.LifecycleRequiredPromptId;
        var eventKey = Hash('a');
        var publicationScopeKey = Hash('b');
        var membershipSignature = Hash('d');
        return new WorkReportStatisticRebuildJob
        {
            Id = identity.RunId,
            DedupeKey = identity.DedupeKey,
            ReceiptId = identity.ReceiptId,
            DirectProjectionIdentityVersion = identity.Version,
            DirectProjectionIdentityKey = identity.Key,
            CapabilityId = StatRunCapabilities.DirectFieldTableLabel,
            RouteId = StatRunRouteRegistry.LifecycleDirectProjector,
            RunKind = WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection,
            ActorUserId = "222222222222222222222222",
            TenantUnitId = "333333333333333333333333",
            ScopeType = "WORK_PERIOD_TEMPLATE",
            ScopeId = "888888888888888888888888",
            SourceReportId = reportId,
            SourcePayloadRevision = 7,
            SourcePayloadHash = Hash('2'),
            SourceLifecycleRevision = 9,
            SourceLifecycleEventKey = eventKey,
            SourceMembershipSignature = membershipSignature,
            DirectSourceRevision = 11,
            PublicationScopeKey = publicationScopeKey,
            SourceStatus = "APPROVED",
            GenerationId = StatRunDirectProjectionService.BuildFoundationRefreshGenerationId(
                identity.Key,
                chainId,
                promptId,
                reportId,
                eventKey,
                publicationScopeKey,
                membershipSignature),
            DynamicFormFamilyId = "444444444444444444444444",
            DynamicFormTemplateId = "555555555555555555555555",
            DynamicFormVersionNo = 2,
            DynamicFormSchemaHash = Hash('3'),
            ConfigId = "666666666666666666666666",
            ConfigVersionId = "777777777777777777777777",
            ConfigVersionNo = 2,
            ConfigRevision = 13,
            ConfigHash = Hash('4'),
            CandidateChainId = chainId,
            CandidatePromptId = promptId,
            ScopeKind = WorkReportStatisticRebuildJobScopeKinds.Bounded,
            WorkId = "888888888888888888888888",
            WorkAssignmentId = "999999999999999999999999",
            PeriodKey = "2026-09",
            PeriodInstanceKey = "2026-09",
            PeriodKind = "MONTH",
            PeriodStartUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            PeriodEndUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            Status = WorkReportStatisticRebuildJobStatuses.Completed,
            StateRevision = 2,
            IsActive = false,
            IsCurrentPublication = true,
            DirectPublicationRevision = 1,
            FreshnessState = WorkReportStatisticRebuildJobFreshnessStates.Fresh,
            TotalReportCount = 1,
            ComputedAtUtc = new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    private static string Hash(char value) => new(value, 64);

    private static bool IsLowerHex(string value, int length)
        => value.Length == length && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected={expected}, actual={actual}");
    }

    private static void NotEqual<T>(T left, T right, string message)
    {
        if (EqualityComparer<T>.Default.Equals(left, right))
            throw new InvalidOperationException(message);
    }
}