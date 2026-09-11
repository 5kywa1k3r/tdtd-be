using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

var tests = new (string Name, Func<Task> Run)[]
{
    ("T15_ORDERED_PERMUTATION_DIFFERS", Tests.OrderedPermutationDiffers),
    ("T15_ORDERED_MULTIPLICITY_PRESERVED", Tests.OrderedMultiplicityPreserved),
    ("T15_UNORDERED_PERMUTATION_DEDUPES", Tests.UnorderedPermutationDedupes),
    ("T15_UNORDERED_MEMBER_CHANGE_DIFFERS", Tests.UnorderedMemberChangeDiffers),
    ("T15_UNORDERED_NON_LIST_REJECTED", Tests.UnorderedNonListRejected),
    ("T15_CULTURE_INVARIANT", Tests.CultureInvariant),
    ("T15_VALID_GENERATION_INTEGRITY", Tests.ValidGenerationIntegrity),
    ("T15_MISSING_COMMIT_FAILS", Tests.MissingCommitFails),
    ("T15_COMMIT_COUNT_TAMPER_FAILS", Tests.CommitCountTamperFails),
    ("T15_MANIFEST_TAMPER_FAILS", Tests.ManifestTamperFails),
    ("T15_HEADER_TAMPER_FAILS", Tests.HeaderTamperFails),
    ("T15_ATOM_TAMPER_FAILS", Tests.AtomTamperFails),
    ("T15_SOURCE_TAMPER_FAILS", Tests.SourceTamperFails),
    ("T15_STREAMING_INTEGRITY_BUCKETS", Tests.StreamingIntegrityBuckets),
    ("T15_BUSINESS_SUMMARY_AGGREGATES", Tests.BusinessSummaryAggregates),
    ("T15_BUSINESS_ALLOWLIST_REDACTS", Tests.BusinessAllowlistRedacts),
    ("T15_CURSOR_HMAC_TAMPER_FAILS", Tests.CursorHmacTamperFails),
    ("T15_CURSOR_CROSS_BINDING_FAILS", Tests.CursorCrossBindingFails),
    ("T15_CURSOR_MALFORMED_FAILS", Tests.CursorMalformedFails),
    ("T15_CURSOR_OVERSIZE_FAILS", Tests.CursorOversizeFails),
    ("T15_CURSOR_NONCANONICAL_FAILS", Tests.CursorNoncanonicalFails),
    ("T15_READ_BOUNDARY_ORDERED", Tests.ReadBoundaryOrdered),
    ("T15_READ_BOUNDARY_FAILS_CLOSED", Tests.ReadBoundaryFailsClosed),
    ("T15_PAGE_SIZE_INDEPENDENT", Tests.PageSizeIndependent),
    ("T15_SHUFFLED_INSERTION_DETERMINISTIC", Tests.ShuffledInsertionDeterministic),
    ("T15_PROVENANCE_DETAIL_EXPLICIT", Tests.ProvenanceDetailExplicit),
    ("T15_PAGE_SIZE_PRODUCTION_CONTRACT", Tests.PageSizeProductionContract),
    ("T15_GENERATION_BOUND_EXACT_MAX", Tests.GenerationBoundExactMax),
    ("T15_GENERATION_BOUND_MAX_PLUS_ONE_FAILS", Tests.GenerationBoundMaxPlusOneFails)
};

const int expectedCheckCount = 29;
if (tests.Length != expectedCheckCount ||
    tests.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() !=
    expectedCheckCount)
    throw new InvalidOperationException("P10-T15 exact test registry drifted.");

var passed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
        passed++;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(
            $"FAIL {test.Name}: {error.GetType().Name}: {error.Message}");
        Environment.ExitCode = 1;
        return;
    }
}
Console.WriteLine($"P10_T15_OK checks={passed} stopBefore=P10-T16");

internal static class Tests
{
    internal static Task OrderedPermutationDiffers()
    {
        var first = Fixture.StringListGeneration("[\"a\",\"b\"]", false);
        var second = Fixture.StringListGeneration("[\"b\",\"a\"]", false);
        var firstAtom = Fixture.ValueAtom(first);
        var secondAtom = Fixture.ValueAtom(second);
        NotEqual(firstAtom.CanonicalValue, secondAtom.CanonicalValue, "canonical sequence");
        NotEqual(firstAtom.ValueIdentitySha256, secondAtom.ValueIdentitySha256,
            "ordered value identity");
        NotEqual(firstAtom.AtomSemanticSha256, secondAtom.AtomSemanticSha256,
            "ordered atom semantic");
        NotEqual(first.TypedSemanticSha256, second.TypedSemanticSha256,
            "ordered typed semantic");
        NotEqual(first.GenerationId, second.GenerationId, "ordered generation");
        return Task.CompletedTask;
    }

    internal static Task OrderedMultiplicityPreserved()
    {
        var one = Fixture.ValueAtom(
            Fixture.StringListGeneration("[\"a\",\"b\"]", false));
        var duplicate = Fixture.ValueAtom(
            Fixture.StringListGeneration("[\"a\",\"b\",\"b\"]", false));
        Equal("[\"a\",\"b\",\"b\"]", duplicate.CanonicalValue, "duplicates retained");
        NotEqual(one.ValueIdentitySha256, duplicate.ValueIdentitySha256,
            "multiplicity identity");
        return Task.CompletedTask;
    }

    internal static Task UnorderedPermutationDedupes()
    {
        var first = Fixture.StringListGeneration("[\"b\",\"a\",\"a\"]", true);
        var second = Fixture.StringListGeneration("[\"a\",\"b\"]", true);
        var firstAtom = Fixture.ValueAtom(first);
        var secondAtom = Fixture.ValueAtom(second);
        Equal("[\"a\",\"b\"]", firstAtom.CanonicalValue, "ordinal set canonical");
        Equal(firstAtom.ValueIdentitySha256, secondAtom.ValueIdentitySha256,
            "set value identity");
        Equal(firstAtom.AtomSemanticSha256, secondAtom.AtomSemanticSha256,
            "set atom semantic");
        Equal(first.TypedSemanticSha256, second.TypedSemanticSha256,
            "set typed semantic");
        // Immutable source hashes still bind raw approved payload revisions.
        NotEqual(first.GenerationId, second.GenerationId, "source payload remains pinned");
        return Task.CompletedTask;
    }

    internal static Task UnorderedMemberChangeDiffers()
    {
        var first = Fixture.ValueAtom(
            Fixture.StringListGeneration("[\"a\",\"b\"]", true));
        var second = Fixture.ValueAtom(
            Fixture.StringListGeneration("[\"a\",\"c\"]", true));
        NotEqual(first.CanonicalValue, second.CanonicalValue, "set members");
        NotEqual(first.ValueIdentitySha256, second.ValueIdentitySha256, "set identity");
        return Task.CompletedTask;
    }

    internal static Task UnorderedNonListRejected()
    {
        try
        {
            _ = Fixture.Generation(
                "{\"value\":1}",
                Fixture.Config(
                    StatisticReconciliationExpectedValueTypes.Number,
                    "/value",
                    true,
                    "metric-number",
                    "field-number"));
            throw new InvalidOperationException("Expected unordered NUMBER to fail.");
        }
        catch (StatisticReconciliationExpectedLedgerInputException error)
            when (error.Reason ==
                  StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid)
        {
            return Task.CompletedTask;
        }
    }

    internal static Task CultureInvariant()
    {
        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("vi-VN");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("vi-VN");
            var first = Fixture.StringListGeneration("[\"ı\",\"I\",\"i\"]", true);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var second = Fixture.StringListGeneration("[\"ı\",\"I\",\"i\"]", true);
            Equal(first.TypedSemanticSha256, second.TypedSemanticSha256,
                "culture invariant typed hash");
            Equal(Fixture.ValueAtom(first).CanonicalValue,
                Fixture.ValueAtom(second).CanonicalValue, "culture invariant canonical");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
        return Task.CompletedTask;
    }

    internal static async Task ValidGenerationIntegrity()
    {
        var documents = await Fixture.Documents();
        var commit = StatisticReconciliationExpectedObservationIntegrity
            .ValidateGeneration(documents);
        Equal(StatisticReconciliationObservationRecordKinds.GenerationCommit,
            commit.RecordKind, "commit kind");
        Equal(documents.Count, commit.Commit!.DocumentCount, "document count");
    }

    internal static async Task MissingCommitFails()
    {
        var documents = (await Fixture.Documents())
            .Where(item => item.RecordKind !=
                           StatisticReconciliationObservationRecordKinds.GenerationCommit)
            .ToArray();
        FailsIntegrity(documents, "missing commit");
    }

    internal static async Task CommitCountTamperFails()
    {
        var documents = Clone(await Fixture.Documents());
        var commit = documents.Single(item => item.Commit is not null);
        commit.Commit!.AtomCount++;
        commit.DocumentSemanticSha256 =
            StatisticReconciliationExpectedObservationIntegrity
                .BuildDocumentSemanticSha256(commit);
        FailsIntegrity(documents, "commit count");
    }

    internal static async Task ManifestTamperFails()
    {
        var documents = Clone(await Fixture.Documents());
        var commit = documents.Single(item => item.Commit is not null);
        commit.Commit!.ManifestSha256 = Fixture.Sha("wrong-manifest");
        commit.DocumentSemanticSha256 =
            StatisticReconciliationExpectedObservationIntegrity
                .BuildDocumentSemanticSha256(commit);
        FailsIntegrity(documents, "manifest");
    }

    internal static async Task HeaderTamperFails()
    {
        var documents = Clone(await Fixture.Documents());
        documents.First(item => item.Commit is null).WorkId = "other-work";
        FailsIntegrity(documents, "common header");
    }

    internal static async Task AtomTamperFails()
    {
        var documents = Clone(await Fixture.Documents());
        documents.First(item => item.Atom is not null).Atom!.CanonicalValue += "-tampered";
        FailsIntegrity(documents, "atom semantic");
    }

    internal static async Task SourceTamperFails()
    {
        var documents = Clone(await Fixture.Documents());
        documents.First(item => item.SourceDecision is not null)
            .SourceDecision!.ReasonCode = "TAMPERED";
        FailsIntegrity(documents, "source semantic");
    }

    internal static async Task StreamingIntegrityBuckets()
    {
        var documents = (await Fixture.Documents(twoHiddenIdentities: true))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var committedDocument = documents.Single(item => item.Commit is not null);
        var accumulator =
            new StatisticReconciliationExpectedObservationIntegrityAccumulator();
        foreach (var document in documents)
            accumulator.Add(document);
        var completedDocument = accumulator.Complete();

        Equal(committedDocument, completedDocument,
            "streaming completion returns committed document");
        Equal(committedDocument.DocumentSemanticSha256,
            completedDocument.DocumentSemanticSha256,
            "streaming committed document semantic");
        var buckets = accumulator.BusinessBuckets;
        Equal((long)committedDocument.Commit!.AtomCount,
            buckets.Sum(item => item.AtomCount),
            "streaming bucket atom total");
        True(buckets.Count > 1 && buckets.All(item =>
                !string.IsNullOrWhiteSpace(item.Family) &&
                !string.IsNullOrWhiteSpace(item.Kind) &&
                !string.IsNullOrWhiteSpace(item.AtomKind) &&
                !string.IsNullOrWhiteSpace(item.ValueType) &&
                !string.IsNullOrWhiteSpace(item.ValueState) &&
                item.AtomCount > 0),
            "streaming buckets contain positive coarse keys");
        Equal(buckets.Count, buckets
                .Select(item => (
                    item.Family,
                    item.Kind,
                    item.AtomKind,
                    item.ValueType,
                    item.ValueState))
                .Distinct()
                .Count(),
            "streaming coarse keys are unique");
        ExactProperties<StatisticReconciliationExpectedBusinessBucket>(
            "streaming business bucket DTO",
            "Family",
            "Kind",
            "AtomKind",
            "ValueType",
            "ValueState",
            "AtomCount");
        var canonicalBuckets = buckets
            .OrderBy(item => item.Family, StringComparer.Ordinal)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.AtomKind, StringComparer.Ordinal)
            .ThenBy(item => item.ValueType, StringComparer.Ordinal)
            .ThenBy(item => item.ValueState, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < buckets.Count; index++)
            Equal(canonicalBuckets[index], buckets[index],
                "streaming canonical bucket order " + index);

        const string actorUserId = "viewer-streaming";
        const string permissionCode = "RECONCILIATION_SUMMARY";
        var atoms = documents
            .Where(item => item.Atom is not null)
            .Select(item => item.Atom!)
            .ToArray();
        var fromObservations =
            StatisticReconciliationExpectedObservationPresentation.BusinessSummary(
                committedDocument.ReconciliationId,
                committedDocument.GenerationId,
                actorUserId,
                false,
                permissionCode,
                documents);
        var fromAtoms =
            StatisticReconciliationExpectedObservationPresentation.BusinessSummary(
                committedDocument.ReconciliationId,
                committedDocument.GenerationId,
                actorUserId,
                false,
                permissionCode,
                atoms);
        var fromBuckets =
            StatisticReconciliationExpectedObservationPresentation.BusinessSummary(
                committedDocument.ReconciliationId,
                committedDocument.GenerationId,
                actorUserId,
                false,
                permissionCode,
                buckets);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var observationsJson = JsonSerializer.Serialize(fromObservations, jsonOptions);
        Equal(observationsJson, JsonSerializer.Serialize(fromAtoms, jsonOptions),
            "streaming summary equals atom summary");
        Equal(observationsJson, JsonSerializer.Serialize(fromBuckets, jsonOptions),
            "streaming summary equals observation summary");

        var tamperedDocuments = Clone(documents)
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        tamperedDocuments.First(item => item.Atom is not null)
            .Atom!.CanonicalValue += "-tampered";
        FailsIntegrityAction(
            () =>
            {
                var tamperedAccumulator =
                    new StatisticReconciliationExpectedObservationIntegrityAccumulator();
                foreach (var document in tamperedDocuments)
                    tamperedAccumulator.Add(document);
                _ = tamperedAccumulator.Complete();
            },
            "ATOM_VALUE_IDENTITY_SHA256_MISMATCH",
            "streaming tampered atom");
    }

    internal static async Task BusinessSummaryAggregates()
    {
        var documents = await Fixture.Documents(twoHiddenIdentities: true);
        StatisticReconciliationExpectedObservationIntegrity.ValidateGeneration(documents);
        var commit = documents.Single(item => item.Commit is not null);
        var response = StatisticReconciliationExpectedObservationPresentation.BusinessSummary(
            commit.ReconciliationId,
            commit.GenerationId,
            "viewer-current",
            true,
            "RECONCILIATION_SUMMARY",
            documents);
        Equal(16L, response.Permission.RowCountBeforeRedaction,
            "two hidden identities emit sixteen typed atoms");
        Equal(8L, response.Permission.RowCountAfterRedaction,
            "eight safe atom groups after redaction");
        Equal(8, response.Rows.Count, "safe aggregate row count");
        Equal(8L, response.Total, "redacted total");
        Equal(16L, response.Rows.Sum(item => item.AtomCount),
            "atom count preserves all typed atoms exactly once");
        True(response.Rows.All(item => item.AtomCount == 2),
            "each safe atom group combines both hidden identities");
        Equal("viewer-current", response.Permission.ActorUserId, "current viewer");
        True(response.Permission.DetailedProvenanceAllowed, "current detail permission");
    }

    internal static async Task BusinessAllowlistRedacts()
    {
        var documents = await Fixture.Documents(twoHiddenIdentities: true);
        var commit = StatisticReconciliationExpectedObservationIntegrity
            .ValidateGeneration(documents);
        var response = StatisticReconciliationExpectedObservationPresentation.BusinessSummary(
            commit.ReconciliationId,
            commit.GenerationId,
            "viewer-business",
            false,
            "RECONCILIATION_SUMMARY",
            documents);
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(
            JsonSerializerDefaults.Web));
        foreach (var canary in new[]
                 {
                     "field-secret-a",
                     "field-secret-b",
                     "metric-tags",
                     "MONTH:2026-08",
                     "report-s1",
                     "payload-s1",
                     "provenance-s1",
                     "config-field",
                     "owner-0"
                 })
            True(!json.Contains(canary, StringComparison.Ordinal), $"redacts {canary}");
        ExactProperties<StatisticReconciliationExpectedBusinessSummaryResponse>(
            "business summary DTO",
            "ReconciliationId",
            "GenerationId",
            "CompilationStatus",
            "Ordering",
            "RedactionPolicy",
            "Permission",
            "Rows",
            "Total");
        ExactProperties<StatisticReconciliationExpectedPermissionSnapshotResponse>(
            "permission snapshot DTO",
            "ActorUserId",
            "PermissionCode",
            "DetailedProvenanceAllowed",
            "RowCountBeforeRedaction",
            "RowCountAfterRedaction");
        ExactProperties<StatisticReconciliationExpectedBusinessMetricRowResponse>(
            "business row DTO",
            "Family",
            "Kind",
            "AtomKind",
            "ValueType",
            "ValueState",
            "AtomCount");
    }

    internal static async Task CursorHmacTamperFails()
    {
        var documents = await Fixture.Documents();
        var commit = StatisticReconciliationExpectedObservationIntegrity
            .ValidateGeneration(documents);
        var codec = Fixture.Codec();
        var scope = Fixture.Scope(commit);
        var position = new StatisticReconciliationExpectedCursorPosition(
            documents[0].RecordKind,
            documents[0].Id);
        var cursor = codec.Encode(scope, position);
        Equal(position, codec.Decode(cursor, scope), "cursor roundtrip");
        var tail = cursor[^1] == 'A' ? 'B' : 'A';
        FailsCursor(() => codec.Decode(cursor[..^1] + tail, scope), "tamper");
    }

    internal static async Task CursorCrossBindingFails()
    {
        var documents = await Fixture.Documents();
        var commit = StatisticReconciliationExpectedObservationIntegrity
            .ValidateGeneration(documents);
        var codec = Fixture.Codec();
        var scope = Fixture.Scope(commit);
        var cursor = codec.Encode(
            scope,
            new StatisticReconciliationExpectedCursorPosition(
                documents[0].RecordKind,
                documents[0].Id));
        FailsCursor(() => codec.Decode(cursor, scope with { ActorUserId = "other" }),
            "actor");
        FailsCursor(() => codec.Decode(cursor, scope with { WorkId = "other" }), "work");
        FailsCursor(() => codec.Decode(cursor, scope with
        {
            ScopeAssignmentId = "other"
        }), "scope assignment");
        FailsCursor(() => codec.Decode(cursor, scope with
        {
            ReconciliationId = "other"
        }), "reconciliation");
        FailsCursor(() => codec.Decode(cursor, scope with
        {
            GenerationId = Fixture.Sha("other-generation")
        }), "generation");
        FailsCursor(() => codec.Decode(cursor, scope with
        {
            ManifestSha256 = Fixture.Sha("other-manifest")
        }), "manifest");
        FailsCursor(() => codec.Decode(cursor, scope with { View = "OTHER_VIEW" }), "view");
        FailsCursor(() => codec.Decode(cursor, scope with
        {
            PermissionCode = "OTHER_PERMISSION"
        }), "permission");
    }

    internal static async Task CursorMalformedFails()
    {
        var documents = await Fixture.Documents();
        var commit = StatisticReconciliationExpectedObservationIntegrity
            .ValidateGeneration(documents);
        var codec = Fixture.Codec();
        var scope = Fixture.Scope(commit);
        foreach (var malformed in new[]
                 {
                     "",
                     ".",
                     "payload-only",
                     "payload.signature.extra",
                     "not+base64url.signature",
                     "payload.not+base64url",
                     "payload.signature" + (char)0x1f
                 })
            FailsCursor(() => codec.Decode(malformed, scope), "malformed cursor");
    }

    internal static async Task CursorOversizeFails()
    {
        var documents = await Fixture.Documents();
        var commit = StatisticReconciliationExpectedObservationIntegrity
            .ValidateGeneration(documents);
        var codec = Fixture.Codec();
        var scope = Fixture.Scope(commit);
        var oversized = new string(
            'A',
            StatisticReconciliationExpectedObservationCursorCodec.MaxCursorLength + 1);
        FailsCursor(() => codec.Decode(oversized, scope), "oversized cursor");
    }

    internal static async Task CursorNoncanonicalFails()
    {
        var documents = await Fixture.Documents();
        var commit = StatisticReconciliationExpectedObservationIntegrity
            .ValidateGeneration(documents);
        var codec = Fixture.Codec();
        var scope = Fixture.Scope(commit);
        var cursor = codec.Encode(
            scope,
            new StatisticReconciliationExpectedCursorPosition(
                documents[0].RecordKind,
                documents[0].Id));
        FailsCursor(
            () => codec.Decode(BuildNoncanonicalCursor(cursor), scope),
            "noncanonical signed cursor");
    }

    internal static async Task ReadBoundaryOrdered()
    {
        var trace = new List<string>();
        var result = await StatisticReconciliationExpectedObservationReadBoundary
            .ExecuteAsync<string, int, string>(
                true,
                () => trace.Add("detail"),
                () =>
                {
                    trace.Add("scope");
                    return Task.FromResult("authorized-scope");
                },
                scope =>
                {
                    Equal("authorized-scope", scope, "authorized scope forwarding");
                    trace.Add("run-load");
                    return Task.FromResult(7);
                },
                run =>
                {
                    Equal(7, run, "authorized run forwarding");
                    trace.Add("run-integrity");
                },
                (scope, run) =>
                {
                    Equal("authorized-scope", scope, "target scope forwarding");
                    Equal(7, run, "target run forwarding");
                    trace.Add("target");
                    return Task.FromResult("target-result");
                });
        Equal("target-result", result, "read boundary result");
        Equal("detail|scope|run-load|run-integrity|target",
            string.Join("|", trace), "read boundary order");
    }

    internal static async Task ReadBoundaryFailsClosed()
    {
        var gates = new[] { "detail", "scope", "run-load", "run-integrity" };
        foreach (var failureGate in gates)
        {
            var trace = new List<string>();
            var targetCalls = 0;
            try
            {
                _ = await StatisticReconciliationExpectedObservationReadBoundary
                    .ExecuteAsync<string, int, string>(
                        true,
                        () =>
                        {
                            trace.Add("detail");
                            ThrowAtGate(failureGate, "detail");
                        },
                        () =>
                        {
                            trace.Add("scope");
                            ThrowAtGate(failureGate, "scope");
                            return Task.FromResult("authorized-scope");
                        },
                        scope =>
                        {
                            Equal("authorized-scope", scope,
                                "failed boundary authorized scope forwarding");
                            trace.Add("run-load");
                            ThrowAtGate(failureGate, "run-load");
                            return Task.FromResult(7);
                        },
                        run =>
                        {
                            Equal(7, run, "failed boundary authorized run forwarding");
                            trace.Add("run-integrity");
                            ThrowAtGate(failureGate, "run-integrity");
                        },
                        (_, _) =>
                        {
                            targetCalls++;
                            trace.Add("target");
                            return Task.FromResult("unexpected-target");
                        });
                throw new InvalidOperationException(
                    "Expected read boundary failure at " + failureGate + ".");
            }
            catch (ReadBoundaryProbeException error)
                when (error.Gate == failureGate)
            {
            }

            Equal(0, targetCalls, "zero target reads after " + failureGate);
            var expectedTrace = gates.Take(Array.IndexOf(gates, failureGate) + 1);
            Equal(string.Join("|", expectedTrace), string.Join("|", trace),
                "failure order at " + failureGate);
        }
    }

    internal static async Task PageSizeIndependent()
    {
        var documents = await Fixture.Documents();
        var commit = StatisticReconciliationExpectedObservationIntegrity
            .ValidateGeneration(documents);
        var one = ReadAllPages(documents, commit, 1);
        var defaultPage = ReadAllPages(
            documents,
            commit,
            StatisticReconciliationRunService.ExpectedObservationDefaultPageSize);
        Equal(string.Join("|", one), string.Join("|", defaultPage),
            "page concatenation");
        Equal(documents.Count, one.Count, "page count");
        Equal(one.Count, one.Distinct(StringComparer.Ordinal).Count(), "no duplicate rows");
    }

    internal static async Task ShuffledInsertionDeterministic()
    {
        var documents = await Fixture.Documents();
        var commit = StatisticReconciliationExpectedObservationIntegrity
            .ValidateGeneration(documents);
        var shuffled = documents.OrderBy(_ => Guid.NewGuid()).ToArray();
        var first = ReadAllPages(documents, commit, 3);
        var second = ReadAllPages(shuffled, commit, 3);
        Equal(string.Join("|", first), string.Join("|", second), "shuffled order");

        var codec = Fixture.Codec();
        var scope = Fixture.Scope(commit);
        var pageA = StatisticReconciliationExpectedObservationPresentation.ProvenancePage(
            commit.ReconciliationId, commit.GenerationId, commit.Commit!.ManifestSha256,
            "viewer-admin", "RECONCILIATION_DETAIL", documents, 3, null,
            position => codec.Encode(scope, position));
        var pageB = StatisticReconciliationExpectedObservationPresentation.ProvenancePage(
            commit.ReconciliationId, commit.GenerationId, commit.Commit.ManifestSha256,
            "viewer-admin", "RECONCILIATION_DETAIL", shuffled, 3, null,
            position => codec.Encode(scope, position));
        Equal(pageA.NextCursor, pageB.NextCursor, "deterministic cursor");
    }

    internal static async Task ProvenanceDetailExplicit()
    {
        var documents = await Fixture.Documents();
        var source = documents.First(item => item.SourceDecision is not null);
        var row = StatisticReconciliationExpectedObservationPresentation
            .ToProvenanceRow(source);
        Equal(source.SourceDecision!.ReportId, row.SourceDecision!.ReportId,
            "operator report provenance");
        Equal(source.DocumentSemanticSha256, row.DocumentSemanticSha256,
            "operator document hash");
        True(row.ConfigurationPin is null && row.Atom is null && row.Commit is null,
            "record payload exclusivity");
        ExactProperties<StatisticReconciliationExpectedProvenanceRowResponse>(
            "operator provenance row DTO",
            "RecordId",
            "RecordKind",
            "Header",
            "SourceDecision",
            "ConfigurationPin",
            "LineagePin",
            "Atom",
            "Commit",
            "DocumentSemanticSha256");
        True(!typeof(StatisticReconciliationExpectedBusinessMetricRowResponse)
                .IsAssignableFrom(typeof(StatisticReconciliationExpectedProvenanceRowResponse)) &&
             !typeof(StatisticReconciliationExpectedProvenanceRowResponse)
                .IsAssignableFrom(typeof(StatisticReconciliationExpectedBusinessMetricRowResponse)),
            "business and operator DTOs are separate");
    }

    internal static Task PageSizeProductionContract()
    {
        Equal(50, StatisticReconciliationRunService.ExpectedObservationDefaultPageSize,
            "production default page size");
        Equal(200, StatisticReconciliationRunService.ExpectedObservationMaxPageSize,
            "production maximum page size");
        True(StatisticReconciliationRunService.ExpectedObservationDefaultPageSize >= 1 &&
             StatisticReconciliationRunService.ExpectedObservationDefaultPageSize <=
             StatisticReconciliationRunService.ExpectedObservationMaxPageSize,
            "production default lies inside public bounds");
        return Task.CompletedTask;
    }

    internal static Task GenerationBoundExactMax()
    {
        var atomCount = StatisticReconciliationExpectedTypedCompiler.MaxCompiledAtoms;
        var nonAtomCount =
            StatisticReconciliationExpectedObservationIntegrity.MaxGenerationDocuments -
            atomCount - 1;
        True(atomCount > 0 && nonAtomCount >= 0,
            "atom and non-atom budgets are closed");
        var total = StatisticReconciliationExpectedObservationIntegrity
            .RequireGenerationDocumentCount(nonAtomCount, 0, 0, atomCount);
        Equal(StatisticReconciliationExpectedObservationIntegrity.MaxGenerationDocuments,
            total, "exact generation document maximum accepted");
        return Task.CompletedTask;
    }

    internal static Task GenerationBoundMaxPlusOneFails()
    {
        var atomCount = StatisticReconciliationExpectedTypedCompiler.MaxCompiledAtoms;
        var nonAtomCount =
            StatisticReconciliationExpectedObservationIntegrity.MaxGenerationDocuments -
            atomCount;
        FailsIntegrityAction(
            () => StatisticReconciliationExpectedObservationIntegrity
                .RequireGenerationDocumentCount(nonAtomCount, 0, 0, atomCount),
            "GENERATION_DOCUMENT_COUNT_OUT_OF_RANGE",
            "generation document maximum plus one");
        return Task.CompletedTask;
    }

    private static List<string> ReadAllPages(
        IReadOnlyList<StatisticReconciliationObservation> documents,
        StatisticReconciliationObservation commit,
        int pageSize)
    {
        var result = new List<string>();
        var codec = Fixture.Codec();
        var scope = Fixture.Scope(commit);
        StatisticReconciliationExpectedCursorPosition? after = null;
        while (true)
        {
            var page = StatisticReconciliationExpectedObservationPresentation.ProvenancePage(
                commit.ReconciliationId,
                commit.GenerationId,
                commit.Commit!.ManifestSha256,
                "viewer-admin",
                "RECONCILIATION_DETAIL",
                documents,
                pageSize,
                after,
                position => codec.Encode(scope, position));
            Equal(documents.Count, checked((int)page.Total), "stable total");
            result.AddRange(page.Rows.Select(item => item.RecordId));
            if (page.NextCursor is null)
                break;
            after = codec.Decode(page.NextCursor, scope);
        }
        return result;
    }

    private static string BuildNoncanonicalCursor(string canonicalCursor)
    {
        var separator = canonicalCursor.LastIndexOf('.');
        True(separator > 0, "canonical cursor separator");
        var canonicalPayload = canonicalCursor[..separator];
        var canonicalBytes = DecodeBase64Url(canonicalPayload);
        var noncanonicalBytes = Encoding.UTF8.GetBytes(
            " " + Encoding.UTF8.GetString(canonicalBytes));
        var noncanonicalPayload = Base64Url(noncanonicalBytes);
        var signature = HMACSHA256.HashData(
            Fixture.CursorSigningKey(),
            Encoding.UTF8.GetBytes(noncanonicalPayload));
        return noncanonicalPayload + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            0 => normalized,
            2 => normalized + "==",
            3 => normalized + "=",
            _ => throw new InvalidOperationException("Canonical cursor payload is invalid.")
        };
        return Convert.FromBase64String(normalized);
    }

    private static void ThrowAtGate(string failureGate, string currentGate)
    {
        if (string.Equals(failureGate, currentGate, StringComparison.Ordinal))
            throw new ReadBoundaryProbeException(currentGate);
    }

    private static void ExactProperties<T>(string name, params string[] expectedNames)
    {
        var expected = expectedNames.Order(StringComparer.Ordinal);
        var actual = typeof(T).GetProperties()
            .Select(item => item.Name)
            .Order(StringComparer.Ordinal);
        Equal(string.Join("|", expected), string.Join("|", actual), name);
    }

    private static StatisticReconciliationObservation[] Clone(
        IReadOnlyList<StatisticReconciliationObservation> documents)
        => documents.Select(item => BsonSerializer.Deserialize<
            StatisticReconciliationObservation>(item.ToBson())).ToArray();

    private static void FailsIntegrity(
        IReadOnlyList<StatisticReconciliationObservation> documents,
        string name)
    {
        try
        {
            StatisticReconciliationExpectedObservationIntegrity.ValidateGeneration(documents);
            throw new InvalidOperationException($"Expected integrity failure: {name}.");
        }
        catch (StatisticReconciliationExpectedObservationIntegrityException)
        {
        }
    }

    private static void FailsIntegrityAction(
        Action action,
        string expectedReason,
        string name)
    {
        try
        {
            action();
            throw new InvalidOperationException(
                "Expected integrity failure: " + name + ".");
        }
        catch (StatisticReconciliationExpectedObservationIntegrityException error)
            when (error.Reason == expectedReason)
        {
        }
    }

    private static void FailsCursor(Action action, string name)
    {
        try
        {
            action();
            throw new InvalidOperationException($"Expected cursor failure: {name}.");
        }
        catch (StatisticReconciliationExpectedCursorException)
        {
        }
    }

    private static void True(bool value, string name)
    {
        if (!value)
            throw new InvalidOperationException($"Assertion failed: {name}.");
    }

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"Assertion failed: {name}; expected={expected}; actual={actual}.");
    }

    private static void NotEqual<T>(T first, T second, string name)
    {
        if (EqualityComparer<T>.Default.Equals(first, second))
            throw new InvalidOperationException($"Assertion failed: {name}; values equal.");
    }

    private sealed class ReadBoundaryProbeException(string gate) : Exception
    {
        internal string Gate { get; } = gate;
    }
}

internal sealed record SourceSpec(string StableSourceId, string PayloadJson);

internal static class Fixture
{
    private static readonly DateTime UtcNow =
        new(2026, 8, 10, 8, 0, 0, DateTimeKind.Utc);

    internal static StatisticReconciliationExpectedCompiledGeneration StringListGeneration(
        string listJson,
        bool unordered)
        => Generation(
            $"{{\"tags\":{listJson}}}",
            Config(
                StatisticReconciliationExpectedValueTypes.StringList,
                "/tags",
                unordered,
                "metric-tags",
                "field-secret-a"));

    internal static StatisticReconciliationExpectedCompiledGeneration Generation(
        string payloadJson,
        string configurationJson)
    {
        var plan = Plan(configurationJson, [new SourceSpec("s1", payloadJson)]);
        return new StatisticReconciliationExpectedTypedCompiler(
            new StatisticReconciliationExpectedMetricIdentityCompiler())
            .Compile(plan, CatalogPins());
    }

    internal static StatisticReconciliationExpectedTypedAtom ValueAtom(
        StatisticReconciliationExpectedCompiledGeneration generation)
        => generation.Atoms.Single(item =>
            item.AtomKind == StatisticReconciliationExpectedAtomKinds.StringList &&
            item.ValueState == StatisticReconciliationExpectedValueStates.Value);

    internal static string Config(
        string valueType,
        string pointer,
        bool unordered,
        string metricId,
        string fieldId)
        => JsonSerializer.Serialize(new
        {
            expectedMetrics = new[]
            {
                new
                {
                    family = StatisticReconciliationExpectedMetricFamilies.Direct,
                    kind = StatisticReconciliationExpectedMetricKinds.Field,
                    metricId,
                    fieldId,
                    jsonPointer = pointer,
                    valueType,
                    operations = new[]
                    {
                        StatisticReconciliationExpectedMetricOperations.Values
                    },
                    unordered
                }
            }
        });

    internal static async Task<IReadOnlyList<StatisticReconciliationObservation>> Documents(
        bool twoHiddenIdentities = false)
    {
        var config = twoHiddenIdentities
            ? JsonSerializer.Serialize(new
            {
                expectedMetrics = new object[]
                {
                    Metric("field-secret-a"),
                    Metric("field-secret-b")
                }
            })
            : Config(
                StatisticReconciliationExpectedValueTypes.StringList,
                "/tags",
                true,
                "metric-tags",
                "field-secret-a");
        var generation = Generation("{\"tags\":[\"b\",\"a\",\"a\"]}", config);
        var backend = new FakeObservationBackend();
        var store = new StatisticReconciliationExpectedObservationStore(
            backend,
            new StatisticReconciliationExpectedMetricIdentityCompiler());
        await store.AppendGenerationAsync(generation, UtcNow);
        return backend.Documents.Values.ToArray();
    }

    internal static StatisticReconciliationExpectedObservationCursorCodec Codec()
        => new(CursorSigningKey());

    internal static byte[] CursorSigningKey()
        => SHA256.HashData(Encoding.UTF8.GetBytes("p10-t15-test-cursor-key"));

    internal static StatisticReconciliationExpectedCursorScope Scope(
        StatisticReconciliationObservation commit)
        => new(
            "viewer-admin",
            commit.WorkId,
            commit.ScopeAssignmentId,
            commit.ReconciliationId,
            commit.GenerationId,
            commit.Commit!.ManifestSha256,
            StatisticReconciliationExpectedObservationPresentation.ProvenanceView,
            "RECONCILIATION_DETAIL");

    internal static string Sha(string value)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            "P10_T15_FIXTURE_V1",
            value);

    private static object Metric(string fieldId)
        => new
        {
            family = StatisticReconciliationExpectedMetricFamilies.Direct,
            kind = StatisticReconciliationExpectedMetricKinds.Field,
            metricId = "metric-tags",
            fieldId,
            jsonPointer = "/tags",
            valueType = StatisticReconciliationExpectedValueTypes.StringList,
            operations = new[] { StatisticReconciliationExpectedMetricOperations.Values },
            unordered = true
        };

    private static StatisticReconciliationExpectedSourcePlan Plan(
        string configurationJson,
        IReadOnlyList<SourceSpec> sources)
    {
        var context = Context();
        var candidates = sources.Select(source => Candidate(context, source)).ToArray();
        var membership = new CurrentEpochFlowMembership(
            context,
            context.FlowTemplateVersionId,
            context.FlowPayloadSha256,
            context.FlowInstanceId,
            context.ExecutionEpochId,
            3,
            7,
            sources.Select(item => item.StableSourceId));
        var canonicalConfig = StatisticReconciliationExpectedLedgerCanonicalizer
            .NormalizeObject(
                configurationJson,
                4 * 1024 * 1024,
                "$.fixture.config",
                StatisticReconciliationExpectedLedgerInputFailureReasons.ConfigurationInvalid);
        var configuration = new LockedP8Configuration(
            context,
            context.P8ConfigurationOwnerId,
            context.P8ConfigurationBundleSha256,
            canonicalConfig.Sha256,
            canonicalConfig.Value,
            [
                new LockedP8ConfigurationPin(
                    StatisticReconciliationExpectedLedgerConfigurationKinds.Field,
                    context.P8ConfigurationOwnerId,
                    "config-field",
                    "config-field-v1",
                    1,
                    1,
                    Sha("config-field"))
            ]);
        var lineage = new P5P7RuntimeMappingContributionLineage(
            context,
            StatisticReconciliationExpectedLedgerLineageLayers.Required.Select((layer, index) =>
                new ExpectedLedgerLineagePin(
                    layer,
                    $"owner-{index}",
                    $"version-{index}",
                    index + 1,
                    Sha($"lineage-{index}"))));
        var contributions = sources.Select(item => new ExpectedContributionCandidate(
            item.StableSourceId,
            StatisticReconciliationExpectedContributionPolicies.Include,
            "contribution-version-1",
            1,
            Sha($"policy-{item.StableSourceId}"),
            $"provenance-{item.StableSourceId}",
            Sha($"provenance-{item.StableSourceId}"),
            true));
        return new StatisticReconciliationExpectedSourcePlanner(
            new StatisticReconciliationExpectedLedgerCompiler()).Plan(
                new ExpectedAuthoritativeSourceSnapshot(
                    context,
                    candidates,
                    membership,
                    configuration,
                    lineage,
                    contributions));
    }

    private static ExpectedLifecycleRevisionCandidate Candidate(
        ExpectedLedgerCompilationContextPin context,
        SourceSpec source)
    {
        var canonical = StatisticReconciliationExpectedLedgerCanonicalizer.NormalizeObject(
            source.PayloadJson,
            16 * 1024 * 1024,
            "$.fixture.payload",
            StatisticReconciliationExpectedLedgerInputFailureReasons.PayloadJsonInvalid);
        var identity = new ExpectedSourceIdentityPin(
            source.StableSourceId,
            "report-" + source.StableSourceId,
            context.WorkId,
            context.ScopeAssignmentId,
            1,
            Sha("payload-owner-" + source.StableSourceId),
            canonical.Sha256,
            1,
            Sha("lifecycle-" + source.StableSourceId),
            context.DynamicFormVersionId,
            context.FlowInstanceId,
            context.ExecutionEpochId);
        return new ExpectedLifecycleRevisionCandidate(
            source.StableSourceId,
            identity,
            "payload-" + source.StableSourceId,
            canonical.Value,
            StatisticReconciliationExpectedLifecycleStatuses.Approved,
            true,
            true,
            StatisticReconciliationExpectedRuntimeDispositions.Current);
    }

    private static StatisticReconciliationExpectedCatalogPins CatalogPins()
        => StatisticReconciliationExpectedCatalogPins.Create(
            "P9-CATALOG-V1",
            Sha("p9-catalog-raw"),
            Sha("p9-catalog-semantic"),
            Sha("p9-schema-raw"),
            Sha("p9-schema-semantic"),
            Sha("p9-stage-lock"),
            "p10-chain",
            "P10-01",
            "P10-CANDIDATE-V1",
            Sha("candidate-catalog-raw"),
            Sha("candidate-catalog-semantic"),
            Sha("candidate-schema-raw"),
            Sha("candidate-schema-semantic"),
            Sha("candidate-stage-lock"));

    private static ExpectedLedgerCompilationContextPin Context()
        => new(
            "reconciliation-1",
            Sha("immutable-identity"),
            Sha("immutable-header"),
            "tenant-1",
            "work-1",
            "assignment-1",
            "p10-chain",
            "P10-01",
            "MONTH:2026-08",
            "period-instance-1",
            "concept-1",
            "MONTH",
            "APPROVED_AT",
            Sha("filter"),
            "form-version-1",
            Sha("form-schema"),
            "flow-template-version-1",
            Sha("flow-payload"),
            "flow-instance-1",
            "epoch-1",
            "p8-owner-1",
            Sha("p8-bundle"));
}

internal sealed class FakeObservationBackend
    : IStatisticReconciliationExpectedObservationBackend
{
    internal Dictionary<string, StatisticReconciliationObservation> Documents { get; } =
        new(StringComparer.Ordinal);

    public Task<IReadOnlyList<StatisticReconciliationExpectedStoredObservation>>
        ReadGenerationAsync(
            string reconciliationId,
            string generationId,
            CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<StatisticReconciliationExpectedStoredObservation>>(
            Documents.Values
                .Where(item =>
                    item.ReconciliationId == reconciliationId &&
                    item.GenerationId == generationId)
                .Select(item => new StatisticReconciliationExpectedStoredObservation(
                    item.Id,
                    item.RecordKind,
                    item.DocumentSemanticSha256))
                .ToArray());

    public Task AppendContentAsync(
        IReadOnlyList<StatisticReconciliationObservation> observations,
        CancellationToken cancellationToken)
    {
        foreach (var observation in observations)
            Documents.TryAdd(observation.Id, observation);
        return Task.CompletedTask;
    }

    public Task AppendCommitAsync(
        StatisticReconciliationObservation observation,
        CancellationToken cancellationToken)
    {
        Documents.TryAdd(observation.Id, observation);
        return Task.CompletedTask;
    }
}
