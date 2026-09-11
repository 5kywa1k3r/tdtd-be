using System.Reflection;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

var tests = new (string Name, Action Run)[]
{
    ("opaque boundary exposes exactly five server-only inputs", OpaqueBoundaryHasFiveInputs),
    ("valid inputs are detached and canonically ordered", ValidInputsAreDetachedAndOrdered),
    ("semantic JSON variants canonicalize identically", SemanticJsonCanonicalizationIsStable),
    ("payload content hash is recomputed", PayloadContentHashIsRecomputed),
    ("configuration content hash is recomputed", ConfigurationContentHashIsRecomputed),
    ("all five inputs share one exact context", MixedContextFailsClosed),
    ("payload and Flow membership equal the source set", SourceSetsMatchExactly),
    ("P8 kinds and owners are closed", ConfigurationDependencySetIsClosed),
    ("P5-P7 lineage layers are closed and complete", LineageDependencySetIsClosed),
    ("identifier and JSON grammar fail closed", IdentifierAndJsonGrammarFailClosed),
    ("set ordering does not change the binding fingerprint", FingerprintIsSetOrderIndependent),
    ("changed immutable inputs change the binding fingerprint", ChangedInputsChangeFingerprint),
    ("dependency graph excludes every actual path", DependencyGraphExcludesActualPaths),
    ("T09 stops before lifecycle resolution and later ledger work", StopBoundaryIsExact),
    ("compiler builds without a backend project reference", CompilerBuildIsIsolated),
    ("authoritative provider has no T09 implementation or product caller", ProviderStartsAtT10),
    ("aggregate payload byte budget fails closed", AggregatePayloadByteBudgetFailsClosed),
    ("authoritative input collections are bounded while minting", AuthoritativeInputCollectionsAreBoundedWhileMinting),
    ("lifecycle candidate budget stops at 49,999", LifecycleCandidateBudgetStopsAt49999)
};

var passed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        passed++;
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
        Environment.ExitCode = 1;
        break;
    }
}

if (Environment.ExitCode == 0)
    Console.WriteLine($"P10_T09_EXPECTED_COMPILER_OK checks={passed} next=P10-T10");

return;

static void OpaqueBoundaryHasFiveInputs()
{
    var inputProperties = typeof(StatisticReconciliationExpectedLedgerCompileInput)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(property => property.Name)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();
    var expected = new[]
    {
        nameof(StatisticReconciliationExpectedLedgerCompileInput.ApprovedEffectivePayloadRevisions),
        nameof(StatisticReconciliationExpectedLedgerCompileInput.CurrentEpochFlowMembership),
        nameof(StatisticReconciliationExpectedLedgerCompileInput.ImmutableSourceIdentitySet),
        nameof(StatisticReconciliationExpectedLedgerCompileInput.LockedP8Configuration),
        nameof(StatisticReconciliationExpectedLedgerCompileInput.RuntimeMappingContributionLineage)
    }.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    Assert(inputProperties.SequenceEqual(expected, StringComparer.Ordinal),
        "Compile input must expose exactly five domain inputs.");

    foreach (var type in new[]
             {
                 typeof(StatisticReconciliationExpectedLedgerCompileInput),
                 typeof(ExpectedLedgerCompilationContextPin),
                 typeof(ExpectedSourceIdentityPin),
                 typeof(ApprovedEffectiveReportPayloadRevision),
                 typeof(ApprovedEffectiveReportPayloadRevisionSet),
                 typeof(CurrentEpochFlowMembership),
                 typeof(LockedP8ConfigurationPin),
                 typeof(LockedP8Configuration),
                 typeof(ExpectedLedgerLineagePin),
                 typeof(P5P7RuntimeMappingContributionLineage),
                 typeof(ImmutableSourceIdentitySet)
             })
    {
        Assert(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length == 0,
            $"{type.Name} must not be minted by a client assembly.");
    }

    var selfAttestationProperties = typeof(StatisticReconciliationExpectedLedgerCompileInput)
        .Assembly
        .GetTypes()
        .Where(type => type.Namespace ==
                       "tdtd_be.Services.StatisticsReconciliation.ExpectedLedger")
        .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        .Where(property => property.Name is "Authority" or "IsCurrentEpoch" or "IsLocked")
        .Select(property => $"{property.DeclaringType?.Name}.{property.Name}")
        .ToArray();
    Assert(selfAttestationProperties.Length == 0,
        "Caller-controlled authority/current/locked flags are forbidden.");

    var constructors = typeof(StatisticReconciliationExpectedLedgerCompiler)
        .GetConstructors(BindingFlags.Public | BindingFlags.Instance);
    Assert(constructors.Length == 1 && constructors[0].GetParameters().Length == 0,
        "Compiler must own no query service or calculator.");
    Assert(typeof(StatisticReconciliationExpectedLedgerCompiler)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Length == 0,
        "Compiler must own no mutable or query-backed instance state.");
}

static void ValidInputsAreDetachedAndOrdered()
{
    var fixture = ValidFixture();

    fixture.Payloads.Clear();
    fixture.Sources.Clear();
    fixture.MembershipKeys.Clear();
    fixture.ConfigurationPins.Clear();
    fixture.LineagePins.Clear();

    var bound = new StatisticReconciliationExpectedLedgerCompiler()
        .BindInputs(fixture.Input);

    Assert(bound.ApprovedEffectivePayloadRevisions
            .Select(item => item.Identity.IdentityKey)
            .SequenceEqual(new[] { "source-a", "source-b" }, StringComparer.Ordinal),
        "Payloads are not ordered by source identity.");
    Assert(bound.ImmutableSourceIdentitySet.Sources
            .Select(item => item.IdentityKey)
            .SequenceEqual(new[] { "source-a", "source-b" }, StringComparer.Ordinal),
        "Sources are not ordered by identity.");
    Assert(bound.CurrentEpochFlowMembership.SourceIdentityKeys
            .SequenceEqual(new[] { "source-a", "source-b" }, StringComparer.Ordinal),
        "Membership is not ordered by identity.");

    Assert(bound.ApprovedEffectivePayloadRevisions.Length == 2,
        "Payload input wrapper aliases caller collection.");
    Assert(bound.ImmutableSourceIdentitySet.Sources.Length == 2,
        "Source input wrapper aliases caller collection.");
    Assert(bound.CurrentEpochFlowMembership.SourceIdentityKeys.Length == 2,
        "Membership input wrapper aliases caller collection.");
    Assert(bound.LockedP8Configuration.Pins.Length == 2,
        "Configuration input wrapper aliases caller collection.");
    Assert(bound.RuntimeMappingContributionLineage.Pins.Length == 4,
        "Lineage input wrapper aliases caller collection.");
}

static void SemanticJsonCanonicalizationIsStable()
{
    var first = new StatisticReconciliationExpectedLedgerCompiler()
        .BindInputs(ValidFixture(
            payloadAJson: """{"fields":{"amount":1}}""",
            configurationJson: """{"z":2,"a":1}""").Input);
    var second = new StatisticReconciliationExpectedLedgerCompiler()
        .BindInputs(ValidFixture(
            payloadAJson: """ { "fields" : { "amount" : 1.0e0 } } """,
            configurationJson: """{ "a" : 1e0, "z" : 2.00 }""").Input);

    Assert(first.ApprovedEffectivePayloadRevisions[0].PayloadJson ==
           second.ApprovedEffectivePayloadRevisions[0].PayloadJson,
        "Equivalent payload JSON did not canonicalize identically.");
    Assert(first.LockedP8Configuration.CanonicalConfigurationJson ==
           second.LockedP8Configuration.CanonicalConfigurationJson,
        "Equivalent configuration JSON did not canonicalize identically.");
    Assert(first.InputFingerprints.InputBindingSha256 ==
           second.InputFingerprints.InputBindingSha256,
        "Equivalent JSON changed the input binding fingerprint.");
}

static void PayloadContentHashIsRecomputed()
{
    var fixture = ValidFixture();
    var original = fixture.Payloads[0];
    fixture.Payloads[0] = new ApprovedEffectiveReportPayloadRevision(
        original.Identity,
        original.PayloadDocumentId,
        original.DynamicFormVersionId,
        original.DynamicFormSchemaSha256,
        original.PayloadCanonicalSha256,
        """{"fields":{"amount":999}}""");

    ExpectFailure(
        StatisticReconciliationExpectedLedgerInputFailureReasons
            .PayloadContentHashMismatch,
        () => new StatisticReconciliationExpectedLedgerCompiler()
            .BindInputs(Snapshot(fixture)));
}

static void ConfigurationContentHashIsRecomputed()
{
    var fixture = ValidFixture();
    var input = fixture.Input;
    var changed = new LockedP8Configuration(
        fixture.Context,
        input.LockedP8Configuration.OwnerId,
        input.LockedP8Configuration.BundleOwnerSha256,
        input.LockedP8Configuration.ConfigurationCanonicalSha256,
        """{"changed":true}""",
        fixture.ConfigurationPins);

    ExpectFailure(
        StatisticReconciliationExpectedLedgerInputFailureReasons
            .ConfigurationContentHashMismatch,
        () => new StatisticReconciliationExpectedLedgerCompiler()
            .BindInputs(Rebuild(fixture, configuration: changed)));
}

static void MixedContextFailsClosed()
{
    var fixture = ValidFixture();
    var otherContext = CloneContext(fixture.Context, workId: "other-work");
    var current = fixture.Input.CurrentEpochFlowMembership;
    var mixed = new CurrentEpochFlowMembership(
        otherContext,
        current.FlowTemplateVersionId,
        current.FlowPayloadSha256,
        current.FlowInstanceId,
        current.ExecutionEpochId,
        current.ExecutionEpoch!.Value,
        current.ExecutionEpochRevision!.Value,
        fixture.MembershipKeys);

    ExpectFailure(
        StatisticReconciliationExpectedLedgerInputFailureReasons.ContextMismatch,
        () => new StatisticReconciliationExpectedLedgerCompiler()
            .BindInputs(Rebuild(fixture, membership: mixed)));

    fixture = ValidFixture();
    var source = fixture.Sources[0];
    fixture.Sources[0] = new ExpectedSourceIdentityPin(
        source.IdentityKey,
        source.ReportId,
        "other-work",
        source.ScopeAssignmentId,
        source.PayloadRevision,
        source.PayloadOwnerSha256,
        source.PayloadCanonicalSha256,
        source.LifecycleRevision,
        source.LifecycleSha256,
        source.DynamicFormVersionId,
        source.FlowInstanceId,
        source.ExecutionEpochId);
    ExpectFailure(
        StatisticReconciliationExpectedLedgerInputFailureReasons.ContextMismatch,
        () => new StatisticReconciliationExpectedLedgerCompiler()
            .BindInputs(Snapshot(fixture)));
}

static void SourceSetsMatchExactly()
{
    var fixture = ValidFixture();
    fixture.MembershipKeys[1] = "hidden-source";
    ExpectFailure(
        StatisticReconciliationExpectedLedgerInputFailureReasons.SourceSetMismatch,
        () => new StatisticReconciliationExpectedLedgerCompiler()
            .BindInputs(Snapshot(fixture)));

    fixture = ValidFixture();
    fixture.Payloads[1] = fixture.Payloads[0];
    ExpectFailure(
        StatisticReconciliationExpectedLedgerInputFailureReasons.SourceIdentityDuplicate,
        () => new StatisticReconciliationExpectedLedgerCompiler()
            .BindInputs(Snapshot(fixture)));
}

static void ConfigurationDependencySetIsClosed()
{
    var fixture = ValidFixture();
    var pin = fixture.ConfigurationPins[0];
    fixture.ConfigurationPins[0] = new LockedP8ConfigurationPin(
        "UNKNOWN",
        pin.OwnerId,
        pin.ConfigId,
        pin.VersionId,
        pin.VersionNo,
        pin.Revision,
        pin.ConfigSha256);
    ExpectFailure(
        StatisticReconciliationExpectedLedgerInputFailureReasons
            .ConfigurationKindInvalid,
        () => new StatisticReconciliationExpectedLedgerCompiler()
            .BindInputs(Snapshot(fixture)));

    fixture = ValidFixture();
    pin = fixture.ConfigurationPins[0];
    fixture.ConfigurationPins[0] = new LockedP8ConfigurationPin(
        pin.Kind,
        "other-owner",
        pin.ConfigId,
        pin.VersionId,
        pin.VersionNo,
        pin.Revision,
        pin.ConfigSha256);
    ExpectFailure(
        StatisticReconciliationExpectedLedgerInputFailureReasons.ContextMismatch,
        () => new StatisticReconciliationExpectedLedgerCompiler()
            .BindInputs(Snapshot(fixture)));
}

static void LineageDependencySetIsClosed()
{
    var fixture = ValidFixture();
    var pin = fixture.LineagePins[0];
    fixture.LineagePins[0] = new ExpectedLedgerLineagePin(
        "P9_RESULT",
        pin.OwnerId,
        pin.VersionId,
        pin.Revision,
        pin.Sha256);
    ExpectFailure(
        StatisticReconciliationExpectedLedgerInputFailureReasons.LineageLayerInvalid,
        () => new StatisticReconciliationExpectedLedgerCompiler()
            .BindInputs(Snapshot(fixture)));

    fixture = ValidFixture();
    fixture.LineagePins.RemoveAll(pin =>
        pin.Layer == StatisticReconciliationExpectedLedgerLineageLayers.P7Contribution);
    ExpectFailure(
        StatisticReconciliationExpectedLedgerInputFailureReasons.LineageLayerMissing,
        () => new StatisticReconciliationExpectedLedgerCompiler()
            .BindInputs(Snapshot(fixture)));
}

static void IdentifierAndJsonGrammarFailClosed()
{
    var fixture = ValidFixture();
    var source = fixture.Sources[0];
    fixture.Sources[0] = new ExpectedSourceIdentityPin(
        $"bad{(char)0x1f}identity",
        source.ReportId,
        source.WorkId,
        source.ScopeAssignmentId,
        source.PayloadRevision,
        source.PayloadOwnerSha256,
        source.PayloadCanonicalSha256,
        source.LifecycleRevision,
        source.LifecycleSha256,
        source.DynamicFormVersionId,
        source.FlowInstanceId,
        source.ExecutionEpochId);
    ExpectFailure(
        StatisticReconciliationExpectedLedgerInputFailureReasons.IdentifierInvalid,
        () => new StatisticReconciliationExpectedLedgerCompiler()
            .BindInputs(Snapshot(fixture)));

    fixture = ValidFixture();
    var payload = fixture.Payloads[0];
    fixture.Payloads[0] = new ApprovedEffectiveReportPayloadRevision(
        payload.Identity,
        payload.PayloadDocumentId,
        payload.DynamicFormVersionId,
        payload.DynamicFormSchemaSha256,
        payload.PayloadCanonicalSha256,
        """{"value":1,"value":2}""");
    ExpectFailure(
        StatisticReconciliationExpectedLedgerInputFailureReasons.PayloadJsonInvalid,
        () => new StatisticReconciliationExpectedLedgerCompiler()
            .BindInputs(Snapshot(fixture)));
}

static void FingerprintIsSetOrderIndependent()
{
    var fixture = ValidFixture();
    var first = new StatisticReconciliationExpectedLedgerCompiler()
        .BindInputs(Snapshot(fixture));

    fixture.Payloads.Reverse();
    fixture.Sources.Reverse();
    fixture.MembershipKeys.Reverse();
    fixture.ConfigurationPins.Reverse();
    fixture.LineagePins.Reverse();
    var second = new StatisticReconciliationExpectedLedgerCompiler()
        .BindInputs(Snapshot(fixture));

    Assert(first.InputFingerprints.InputBindingSha256 ==
           second.InputFingerprints.InputBindingSha256,
        "Set-like input ordering changed the binding fingerprint.");
    Assert(first.InputFingerprints == new StatisticReconciliationExpectedLedgerCompiler()
               .BindInputs(ValidFixture().Input).InputFingerprints,
        "Identical inputs did not produce identical fingerprints.");
}

static void ChangedInputsChangeFingerprint()
{
    var fixture = ValidFixture();
    var baseline = new StatisticReconciliationExpectedLedgerCompiler()
        .BindInputs(Snapshot(fixture)).InputFingerprints;

    var current = fixture.Input.CurrentEpochFlowMembership;
    var changedMembership = new CurrentEpochFlowMembership(
        fixture.Context,
        current.FlowTemplateVersionId,
        current.FlowPayloadSha256,
        current.FlowInstanceId,
        current.ExecutionEpochId,
        current.ExecutionEpoch!.Value,
        current.ExecutionEpochRevision!.Value + 1,
        fixture.MembershipKeys);
    var membershipResult = new StatisticReconciliationExpectedLedgerCompiler()
        .BindInputs(Rebuild(fixture, membership: changedMembership)).InputFingerprints;
    Assert(baseline.CurrentEpochFlowMembershipSha256 !=
           membershipResult.CurrentEpochFlowMembershipSha256 &&
           baseline.InputBindingSha256 != membershipResult.InputBindingSha256,
        "Changed Flow revision did not change its component and overall fingerprint.");

    fixture = ValidFixture();
    baseline = new StatisticReconciliationExpectedLedgerCompiler()
        .BindInputs(Snapshot(fixture)).InputFingerprints;
    var configPin = fixture.ConfigurationPins[0];
    fixture.ConfigurationPins[0] = new LockedP8ConfigurationPin(
        configPin.Kind,
        configPin.OwnerId,
        configPin.ConfigId,
        configPin.VersionId,
        configPin.VersionNo,
        configPin.Revision + 1,
        configPin.ConfigSha256);
    var configurationResult = new StatisticReconciliationExpectedLedgerCompiler()
        .BindInputs(Snapshot(fixture)).InputFingerprints;
    Assert(baseline.LockedP8ConfigurationSha256 !=
           configurationResult.LockedP8ConfigurationSha256 &&
           baseline.InputBindingSha256 != configurationResult.InputBindingSha256,
        "Changed P8 revision did not change its component and overall fingerprint.");

    fixture = ValidFixture();
    baseline = new StatisticReconciliationExpectedLedgerCompiler()
        .BindInputs(Snapshot(fixture)).InputFingerprints;
    var lineagePin = fixture.LineagePins[0];
    fixture.LineagePins[0] = new ExpectedLedgerLineagePin(
        lineagePin.Layer,
        lineagePin.OwnerId,
        lineagePin.VersionId,
        lineagePin.Revision + 1,
        lineagePin.Sha256);
    var lineageResult = new StatisticReconciliationExpectedLedgerCompiler()
        .BindInputs(Snapshot(fixture)).InputFingerprints;
    Assert(baseline.RuntimeMappingContributionLineageSha256 !=
           lineageResult.RuntimeMappingContributionLineageSha256 &&
           baseline.InputBindingSha256 != lineageResult.InputBindingSha256,
        "Changed lineage revision did not change its component and overall fingerprint.");
}

static void DependencyGraphExcludesActualPaths()
{
    var graph = new StatisticReconciliationExpectedLedgerCompiler()
        .DescribeDependencies();
    Assert(graph.AllowedDomainInputs.Length == 5,
        "Dependency graph must expose exactly five allowed inputs.");
    foreach (var token in new[]
             {
                 "P9_PROJECTION_QUERY_OR_ROWS",
                 "P9_AGGREGATE_QUERY_OR_ROWS",
                 "P9_RESULT_QUERY_OR_ROWS",
                 "P9_API_OR_EXPORT_AS_EXPECTED",
                 "CLIENT_MEMBERSHIP_OR_VALUES",
                 "SHARED_P9_CALCULATOR"
             })
        Assert(graph.ForbiddenDependencies.Contains(token, StringComparer.Ordinal),
            $"Missing forbidden dependency: {token}.");
}

static void StopBoundaryIsExact()
{
    var assembly = typeof(StatisticReconciliationExpectedLedgerCompiler).Assembly;
    var publicSurface = assembly.GetTypes()
        .Where(type => type.Namespace ==
                       "tdtd_be.Services.StatisticsReconciliation.ExpectedLedger")
        .SelectMany(type =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Concat(type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Select(method => method.Name)))
        .ToHashSet(StringComparer.Ordinal);

    foreach (var forbidden in new[]
             {
                 "LifecycleCandidates",
                 "ExpectedAtoms",
                 "Observations",
                 "SourceSetSha256",
                 "ExpectedAlgorithmSha256",
                 "ExpectedAlgorithmRevision",
                 "ExpectedLedgerSemanticSha256",
                 "Verdict",
                 "Publish"
             })
        Assert(!publicSurface.Contains(forbidden),
            $"T09 crossed its stop boundary with {forbidden}.");

    var methods = typeof(IStatisticReconciliationExpectedLedgerCompiler)
        .GetMethods()
        .Select(method => method.Name)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();
    Assert(methods.SequenceEqual(
            new[] { "BindInputs", "DescribeDependencies" },
            StringComparer.Ordinal),
        "Compiler interface contains lifecycle or later-ledger work.");
}

static void CompilerBuildIsIsolated()
{
    var backendRoot = FindBackendRoot();
    var projectText = File.ReadAllText(Path.Combine(
        backendRoot, "tests", "tdtd-be.P10T09Tests", "tdtd-be.P10T09Tests.csproj"));
    Assert(!projectText.Contains("<ProjectReference", StringComparison.Ordinal),
        "Isolated compiler test project references the backend assembly.");
    var isolatedFiles = new[]
    {
        "IStatisticReconciliationExpectedLedgerCompiler.cs",
        "IStatisticReconciliationExpectedLedgerAuthoritativeInputProvider.cs",
        "StatisticReconciliationExpectedLedgerCanonicalizer.cs",
        "StatisticReconciliationExpectedLedgerCompiler.cs",
        "StatisticReconciliationExpectedLedgerContracts.cs"
    };
    foreach (var file in isolatedFiles)
        Assert(projectText.Contains(file, StringComparison.Ordinal),
            $"Isolated compiler source is not linked directly: {file}.");

    var expectedDirectory = Path.Combine(
        backendRoot, "Services", "StatisticsReconciliation", "ExpectedLedger");
    var source = string.Join(
        "\\n",
        isolatedFiles.OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => File.ReadAllText(Path.Combine(expectedDirectory, name))));
    foreach (var forbidden in new[]
             {
                 "using tdtd_be.Services.StatisticsRun",
                 "using tdtd_be.Services.WorkAssignmentReports.Statistics",
                 "using MongoDB",
                 "MongoDbContext",
                 "IMongoCollection<",
                 "P9DirectResultService",
                 "WorkReportFieldStatValue",
                 "WorkReportTableStatValue",
                 "WorkReportLabelStatValue",
                 "WorkReportFieldStatAggregate",
                 "WorkReportTableStatAggregate",
                 "WorkReportLabelStatAggregate",
                 "System.Reflection",
                 "IServiceProvider",
                 "Assembly.Load",
                 "Type.GetType",
                 "File.",
                 "Directory.",
                 "HttpClient",
                 "WebRequest",
                 "FileStream",
                 "Socket",
                 "TcpClient",
                 "DllImport",
                 "LibraryImport",
                 "NativeLibrary",
                 "Process.",
                 "Environment."
             })
        Assert(!source.Contains(forbidden, StringComparison.Ordinal),
            $"Compiler source contains forbidden dependency: {forbidden}.");
}

static void ProviderStartsAtT10()
{
    var provider = typeof(IStatisticReconciliationExpectedLedgerAuthoritativeInputProvider);
    var implementations = provider.Assembly.GetTypes()
        .Where(type => type != provider &&
                       !type.IsInterface &&
                       !type.IsAbstract &&
                       provider.IsAssignableFrom(type))
        .ToArray();
    Assert(implementations.Length == 0,
        "The isolated T09 compiler assembly must not implement the later authoritative provider.");
}
static void AggregatePayloadByteBudgetFailsClosed()
{
    var limit = StatisticReconciliationExpectedLedgerCompiler
        .MaxAggregatePayloadJsonBytes;
    var exact = StatisticReconciliationExpectedLedgerCompiler
        .AccumulatePayloadBytes(limit - 1, 1, "$.payloads[boundary]");
    Assert(exact == limit,
        "Aggregate payload byte budget rejected its exact boundary.");

    ExpectFailure(
        StatisticReconciliationExpectedLedgerInputFailureReasons
            .PayloadAggregateTooLarge,
        () => StatisticReconciliationExpectedLedgerCompiler
            .AccumulatePayloadBytes(limit, 1, "$.payloads[overflow]"));
}

static void AuthoritativeInputCollectionsAreBoundedWhileMinting()
{
    var fixture = ValidFixture();
    var observed = 0;
    var maximum = StatisticReconciliationExpectedLedgerInputLimits.MaxSources;

    IEnumerable<ApprovedEffectiveReportPayloadRevision> Oversized()
    {
        for (var index = 0; index < maximum + 2; index++)
        {
            observed++;
            yield return fixture.Payloads[index % fixture.Payloads.Count];
        }
    }

    ExpectFailure(
        StatisticReconciliationExpectedLedgerInputFailureReasons
            .PayloadRevisionInvalid,
        () => new ApprovedEffectiveReportPayloadRevisionSet(
            fixture.Context,
            Oversized()));
    Assert(observed == maximum + 1,
        $"Bounded snapshot consumed {observed} items instead of stopping at {maximum + 1}.");
}

static Fixture ValidFixture(
    string payloadAJson = """{"fields":{"amount":1}}""",
    string configurationJson = """{"schemaVersion":"P8_CONFIG_BUNDLE_V1"}""")
{
    var context = new ExpectedLedgerCompilationContextPin(
        "reconciliation-1",
        Hash('a'),
        Hash('b'),
        "tenant-1",
        "work-1",
        "assignment-1",
        "p10-chain",
        "P10-01",
        "2026-08",
        "2026-08:instance",
        "concept-1",
        "DIRECT",
        "PERIOD",
        Hash('c'),
        "form-version-1",
        Hash('d'),
        "flow-version-1",
        Hash('e'),
        "flow-instance-1",
        "flow-epoch-1",
        "config-owner",
        Hash('f'));

    var payloadBJson = """{"fields":{"amount":2}}""";
    var payloadAHash = JsonHash(payloadAJson);
    var payloadBHash = JsonHash(payloadBJson);
    var sourceB = Source(context, "source-b", "report-b", 2, Hash('1'),
        payloadBHash, 3, Hash('2'));
    var sourceA = Source(context, "source-a", "report-a", 1, Hash('3'),
        payloadAHash, 4, Hash('4'));
    var sources = new List<ExpectedSourceIdentityPin> { sourceB, sourceA };
    var payloads = new List<ApprovedEffectiveReportPayloadRevision>
    {
        new(sourceB, "payload-b", context.DynamicFormVersionId,
            context.DynamicFormSchemaSha256, payloadBHash, payloadBJson),
        new(sourceA, "payload-a", context.DynamicFormVersionId,
            context.DynamicFormSchemaSha256, payloadAHash, payloadAJson)
    };
    var membershipKeys = new List<string> { "source-b", "source-a" };
    var configPins = new List<LockedP8ConfigurationPin>
    {
        new(StatisticReconciliationExpectedLedgerConfigurationKinds.Field,
            context.P8ConfigurationOwnerId, "config-b", "version-b", 2, 3, Hash('5')),
        new(StatisticReconciliationExpectedLedgerConfigurationKinds.Label,
            context.P8ConfigurationOwnerId, "config-a", "version-a", 1, 2, Hash('6'))
    };
    var lineagePins = new List<ExpectedLedgerLineagePin>
    {
        new(StatisticReconciliationExpectedLedgerLineageLayers.P7Contribution,
            "scope", "contribution-v1", 4, Hash('7')),
        new(StatisticReconciliationExpectedLedgerLineageLayers.P5Runtime,
            "flow", "runtime-v1", 1, Hash('8')),
        new(StatisticReconciliationExpectedLedgerLineageLayers.P7Mapping,
            "mapping", "mapping-v1", 3, Hash('9')),
        new(StatisticReconciliationExpectedLedgerLineageLayers.P6FlowTopology,
            "flow", "topology-v1", 2, Hash('0'))
    };

    var input = new StatisticReconciliationExpectedLedgerCompileInput(
        new ApprovedEffectiveReportPayloadRevisionSet(context, payloads),
        new CurrentEpochFlowMembership(
            context,
            context.FlowTemplateVersionId,
            context.FlowPayloadSha256,
            context.FlowInstanceId,
            context.ExecutionEpochId,
            2,
            7,
            membershipKeys),
        new LockedP8Configuration(
            context,
            context.P8ConfigurationOwnerId,
            context.P8ConfigurationBundleSha256,
            JsonHash(configurationJson),
            configurationJson,
            configPins),
        new P5P7RuntimeMappingContributionLineage(context, lineagePins),
        new ImmutableSourceIdentitySet(context, sources));

    return new Fixture(
        context,
        input,
        payloads,
        sources,
        membershipKeys,
        configPins,
        lineagePins);
}

static ExpectedSourceIdentityPin Source(
    ExpectedLedgerCompilationContextPin context,
    string identityKey,
    string reportId,
    int payloadRevision,
    string payloadOwnerHash,
    string payloadCanonicalHash,
    int lifecycleRevision,
    string lifecycleHash)
    => new(
        identityKey,
        reportId,
        context.WorkId,
        context.ScopeAssignmentId,
        payloadRevision,
        payloadOwnerHash,
        payloadCanonicalHash,
        lifecycleRevision,
        lifecycleHash,
        context.DynamicFormVersionId,
        context.FlowInstanceId,
        context.ExecutionEpochId);

static StatisticReconciliationExpectedLedgerCompileInput Rebuild(
    Fixture fixture,
    ApprovedEffectiveReportPayloadRevisionSet? payloads = null,
    CurrentEpochFlowMembership? membership = null,
    LockedP8Configuration? configuration = null,
    P5P7RuntimeMappingContributionLineage? lineage = null,
    ImmutableSourceIdentitySet? sources = null)
    => new(
        payloads ?? fixture.Input.ApprovedEffectivePayloadRevisions,
        membership ?? fixture.Input.CurrentEpochFlowMembership,
        configuration ?? fixture.Input.LockedP8Configuration,
        lineage ?? fixture.Input.RuntimeMappingContributionLineage,
        sources ?? fixture.Input.ImmutableSourceIdentitySet);

static StatisticReconciliationExpectedLedgerCompileInput Snapshot(Fixture fixture)
{
    var membership = fixture.Input.CurrentEpochFlowMembership;
    var configuration = fixture.Input.LockedP8Configuration;

    return new StatisticReconciliationExpectedLedgerCompileInput(
        new ApprovedEffectiveReportPayloadRevisionSet(
            fixture.Context,
            fixture.Payloads),
        new CurrentEpochFlowMembership(
            fixture.Context,
            membership.FlowTemplateVersionId,
            membership.FlowPayloadSha256,
            membership.FlowInstanceId,
            membership.ExecutionEpochId,
            membership.ExecutionEpoch!.Value,
            membership.ExecutionEpochRevision!.Value,
            fixture.MembershipKeys),
        new LockedP8Configuration(
            fixture.Context,
            configuration.OwnerId,
            configuration.BundleOwnerSha256,
            configuration.ConfigurationCanonicalSha256,
            configuration.ConfigurationJson,
            fixture.ConfigurationPins),
        new P5P7RuntimeMappingContributionLineage(
            fixture.Context,
            fixture.LineagePins),
        new ImmutableSourceIdentitySet(
            fixture.Context,
            fixture.Sources));
}

static ExpectedLedgerCompilationContextPin CloneContext(
    ExpectedLedgerCompilationContextPin source,
    string? workId = null)
    => new(
        source.ReconciliationId,
        source.ImmutableIdentitySha256,
        source.ImmutableHeaderSha256,
        source.TenantUnitId,
        workId ?? source.WorkId,
        source.ScopeAssignmentId,
        source.CandidateChainId,
        source.CandidatePromptId,
        source.PeriodKey,
        source.PeriodInstanceKey,
        source.ConceptKey,
        source.Grain,
        source.TimeAxis,
        source.FilterSha256,
        source.DynamicFormVersionId,
        source.DynamicFormSchemaSha256,
        source.FlowTemplateVersionId,
        source.FlowPayloadSha256,
        source.FlowInstanceId,
        source.ExecutionEpochId,
        source.P8ConfigurationOwnerId,
        source.P8ConfigurationBundleSha256);

static void ExpectFailure(string reason, Action action)
{
    try
    {
        action();
    }
    catch (StatisticReconciliationExpectedLedgerInputException exception)
        when (exception.Reason == reason)
    {
        return;
    }
    throw new InvalidOperationException($"Expected failure reason {reason}.");
}

static string JsonHash(string json)
    => StatisticReconciliationExpectedLedgerCanonicalizer.NormalizeObject(
        json,
        1024 * 1024,
        "$.fixture",
        StatisticReconciliationExpectedLedgerInputFailureReasons.PayloadJsonInvalid)
        .Sha256;

static string Hash(char character) => new(character, 64);

static string FindBackendRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "tdtd-be.csproj")))
            return directory.FullName;
        var nested = Path.Combine(directory.FullName, "tdtd-be");
        if (File.Exists(Path.Combine(nested, "tdtd-be.csproj")))
            return nested;
        directory = directory.Parent;
    }
    throw new InvalidOperationException("Could not locate backend root.");
}

static bool IsBuildOrTestPath(string path)
{
    var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    return normalized.Contains(
               $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}",
               StringComparison.OrdinalIgnoreCase) ||
           normalized.Contains(
               $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
               StringComparison.OrdinalIgnoreCase) ||
           normalized.Contains(
               $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
               StringComparison.OrdinalIgnoreCase) ||
           normalized.Contains(
               $"{Path.DirectorySeparatorChar}.build{Path.DirectorySeparatorChar}",
               StringComparison.OrdinalIgnoreCase);
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void LifecycleCandidateBudgetStopsAt49999()
{
    const int expectedMaximum = 49_999;
    var planningPath = Path.Combine(
        FindBackendRoot(),
        "Services",
        "StatisticsReconciliation",
        "ExpectedLedger",
        "StatisticReconciliationExpectedSourcePlanningContracts.cs");
    var planningSource = File.ReadAllText(planningPath);
    Assert(planningSource.Contains(
            "internal const int MaxLifecycleCandidates = 49_999;",
            StringComparison.Ordinal),
        "Lifecycle planning limit must reserve room for one unchanged recheck.");
    Assert(planningSource.Contains(
            "internal const int MaxContributionCandidates = 49_999;",
            StringComparison.Ordinal),
        "Contribution planning limit must match the lifecycle planning limit.");

    var observed = 0;
    IEnumerable<int> Oversized()
    {
        for (var index = 0; index <= expectedMaximum; index++)
        {
            observed++;
            yield return index;
        }
    }

    ExpectFailure(
        StatisticReconciliationExpectedLedgerInputFailureReasons
            .PayloadRevisionInvalid,
        () => StatisticReconciliationExpectedLedgerInputLimits.Snapshot(
            Oversized(),
            expectedMaximum,
            StatisticReconciliationExpectedLedgerInputFailureReasons
                .PayloadRevisionInvalid,
            "$.lifecycleCandidates"));
    Assert(observed == expectedMaximum + 1,
        $"Lifecycle snapshot consumed {observed} items instead of stopping at {expectedMaximum + 1}.");
}
internal sealed record Fixture(
    ExpectedLedgerCompilationContextPin Context,
    StatisticReconciliationExpectedLedgerCompileInput Input,
    List<ApprovedEffectiveReportPayloadRevision> Payloads,
    List<ExpectedSourceIdentityPin> Sources,
    List<string> MembershipKeys,
    List<LockedP8ConfigurationPin> ConfigurationPins,
    List<ExpectedLedgerLineagePin> LineagePins);
