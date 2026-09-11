using System.Net;
using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP809BundleCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-BND-001",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var response = await ReadP809BundleAsync(_p809FullPinRequest, ct);
                var bundle = await RequireP809BundleAsync(
                    "P8-BND-001",
                    response,
                    8,
                    _p809FullPinRequest,
                    ct);
                RequireP809PinsMatchRequest(bundle.Pins, _p809FullPinRequest, "P8-BND-001");
                RequireP809FullEligibility(bundle, "P8-BND-001");
                _p809FullBundleHash = bundle.BundleHash;
                _p809FullCanonicalJson = bundle.CanonicalJson;
                return new CaseObservation(
                    "Canonical readback resolved exact persisted label, field, table, Basic, Advanced, Diff, Flow-contribution and readiness identities.",
                    "schema=P8-BUNDLE-1;pins=8;order=LABEL>FIELD>TABLE>BASIC>ADVANCED>DIFF>FLOW_CONTRIBUTION>READINESS;hash=sha256;writes=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-BND-002",
            "system_admin",
            ["p809-bundle-validate-002"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var reversedPins = new JsonArray(
                    _p809FullPinRequest.Reverse()
                        .Select(pin => pin!.DeepClone())
                        .ToArray());
                var readResponse = await ReadP809BundleAsync(reversedPins, ct);
                var readback = await RequireP809BundleAsync(
                    "P8-BND-002",
                    readResponse,
                    8,
                    reversedPins,
                    ct);
                var expectedHash = _p809FullBundleHash
                                   ?? throw new HarnessCaseNotRunnableException(
                                       "P8-BND-001 did not retain the full bundle hash.");
                HarnessAssert.Equal(expectedHash, readback.BundleHash,
                    "Input pin order changed the canonical bundle hash");
                HarnessAssert.Equal(_p809FullCanonicalJson, readback.CanonicalJson,
                    "Input pin order changed canonical bundle JSON");

                var validateResponse = await ValidateP809BundleAsync(
                    "p809-bundle-validate-002",
                    expectedHash,
                    reversedPins,
                    ct);
                var validated = await RequireP809BundleAsync(
                    "P8-BND-002",
                    validateResponse,
                    8,
                    reversedPins,
                    ct);
                HarnessAssert.Equal(readback.CanonicalJson, validated.CanonicalJson,
                    "Validated bundle differs from persisted readback");
                HarnessAssert.Equal(readback.BundleHash, validated.BundleHash,
                    "Validated bundle hash differs from readback");
                return new CaseObservation(
                    "Reordered semantic input, persisted owner readback and command/CAS validation recomputed byte-identical canonical JSON and hash.",
                    "inputOrder=reversed;outputOrder=canonical;readHash=validateHash=ownerHash;commandCAS=1;writes=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-BND-003",
            "system_admin",
            [
                "p809-bundle-stale-hash-003",
                "p809-bundle-stale-pin-003",
                "p809-bundle-mixed-form-003",
                "p809-bundle-stale-flow-003",
                "p809-bundle-composite-owner-003",
                "p809-bundle-missing-contribution-003",
                "p809-bundle-forged-readiness-003",
                "p809-bundle-noncurrent-flow-003",
                "p809-bundle-mixed-label-003",
                "p809-bundle-mixed-assignment-003"
            ],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var fullHash = _p809FullBundleHash
                               ?? throw new HarnessCaseNotRunnableException(
                                   "P8-BND-001 did not retain the full bundle hash.");
                var staleHashResponse = await ValidateP809BundleAsync(
                    "p809-bundle-stale-hash-003",
                    new string('0', 64),
                    _p809FullPinRequest,
                    ct);
                RequireP809Stale(staleHashResponse, "BUNDLE_HASH_STALE", "P8-BND-003 stale hash");

                var stalePins = (JsonArray)_p809FullPinRequest.DeepClone();
                var staleLabel = (JsonObject)stalePins[0]!;
                staleLabel["revision"] = RequiredLong(staleLabel, "revision") + 1;
                var stalePinResponse = await ValidateP809BundleAsync(
                    "p809-bundle-stale-pin-003",
                    fullHash,
                    stalePins,
                    ct);
                RequireP809Stale(stalePinResponse, "BUNDLE_DEPENDENCY_STALE", "P8-BND-003 stale pin");

                var mixedPins = (JsonArray)_p809FullPinRequest.DeepClone();
                mixedPins[2] = P809Pin(
                    "TABLE",
                    _p809SecondForm.OwnerId,
                    _p809SecondForm.ConfigId,
                    _p809SecondForm.VersionId,
                    _p809SecondForm.VersionNo,
                    _p809SecondForm.Revision,
                    _p809SecondForm.ConfigHash,
                    _p809SecondForm.TableSectionHash);
                var mixedResponse = await ValidateP809BundleAsync(
                    "p809-bundle-mixed-form-003",
                    fullHash,
                    mixedPins,
                    ct);
                RequireP809Stale(mixedResponse, "BUNDLE_DYNAMIC_FORM_MIXED", "P8-BND-003 mixed form");

                var staleFlowPins = (JsonArray)_p809FullPinRequest.DeepClone();
                ((JsonObject)staleFlowPins[6]!)["contributionHash"] = new string('f', 64);
                var staleFlowResponse = await ValidateP809BundleAsync(
                    "p809-bundle-stale-flow-003",
                    fullHash,
                    staleFlowPins,
                    ct);
                RequireP809Stale(staleFlowResponse, "BUNDLE_DEPENDENCY_STALE", "P8-BND-003 stale Flow");

                var relabeledCompositePins =
                    (JsonArray)_p809FullPinRequest.DeepClone();
                ((JsonObject)relabeledCompositePins[3]!)["ownerId"] =
                    RequiredString(_p809MixedAssignmentBasicPin, "ownerId");
                var relabeledCompositeResponse = await ValidateP809BundleAsync(
                    "p809-bundle-composite-owner-003",
                    fullHash,
                    relabeledCompositePins,
                    ct);
                RequireP809Stale(
                    relabeledCompositeResponse,
                    "BUNDLE_BASIC_OWNER_MIXED",
                    "P8-BND-003 composite-owner relabel",
                    "BASIC");

                var missingContributionPins =
                    (JsonArray)_p809FullPinRequest.DeepClone();
                ((JsonObject)missingContributionPins[0]!)
                    .Remove("contributionHash");
                var missingContributionResponse = await ValidateP809BundleAsync(
                    "p809-bundle-missing-contribution-003",
                    fullHash,
                    missingContributionPins,
                    ct);
                RequireP809SchemaRejected(
                    missingContributionResponse,
                    "$.dependencyPins[0].contributionHash",
                    "SHA256_REQUIRED",
                    "P8-BND-003 missing contributionHash");

                var forgedReadinessPins =
                    (JsonArray)_p809FullPinRequest.DeepClone();
                forgedReadinessPins[7] = _p809ForgedReadinessPin.DeepClone();
                var forgedReadinessResponse = await ValidateP809BundleAsync(
                    "p809-bundle-forged-readiness-003",
                    fullHash,
                    forgedReadinessPins,
                    ct);
                RequireP809Stale(
                    forgedReadinessResponse,
                    "BUNDLE_READINESS_INTEGRITY",
                    "P8-BND-003 forged readiness",
                    "READINESS");

                var nonCurrentFlowPins =
                    (JsonArray)_p809FullPinRequest.DeepClone();
                nonCurrentFlowPins[6] = _p809NonCurrentFlowPin.DeepClone();
                var nonCurrentFlowResponse = await ValidateP809BundleAsync(
                    "p809-bundle-noncurrent-flow-003",
                    fullHash,
                    nonCurrentFlowPins,
                    ct);
                RequireP809Stale(
                    nonCurrentFlowResponse,
                    "BUNDLE_FLOW_FAMILY_NOT_CURRENT",
                    "P8-BND-003 non-current Flow",
                    "FLOW_CONTRIBUTION");

                var mixedLabelResponse = await ValidateP809BundleAsync(
                    "p809-bundle-mixed-label-003",
                    fullHash,
                    _p809MixedLabelPinRequest,
                    ct,
                    _p809MixedLabel.OwnerKind,
                    _p809MixedLabel.OwnerId);
                RequireP809Stale(
                    mixedLabelResponse,
                    "BUNDLE_LABEL_DEPENDENCY_MIXED",
                    "P8-BND-003 mixed Label snapshot",
                    "LABEL");

                var mixedAssignmentPins =
                    (JsonArray)_p809FullPinRequest.DeepClone();
                mixedAssignmentPins[3] =
                    _p809MixedAssignmentBasicPin.DeepClone();
                var mixedAssignmentResponse = await ValidateP809BundleAsync(
                    "p809-bundle-mixed-assignment-003",
                    fullHash,
                    mixedAssignmentPins,
                    ct);
                RequireP809Stale(
                    mixedAssignmentResponse,
                    "BUNDLE_ASSIGNMENT_OWNER_MIXED",
                    "P8-BND-003 mixed assignment",
                    "BASIC+ADVANCED+DIFF");

                return new CaseObservation(
                    "Stale CAS/dependencies, mixed Form/Label/assignment, relabeled composite owner, forged readiness and non-current Flow were rejected exactly; missing contributionHash was rejected as schema-invalid; all paths preserved whole-store zero writes.",
                    "staleHash=409;stalePin=409;mixedForm=409;staleFlow=409;basicOwnerMixed=409;missingContribution=400;readinessIntegrity=409;flowNotCurrent=409;labelMixed=409;assignmentMixed=409;autoUpgrade=false;catalogUpgrade=0;formUpgrade=0;flowUpgrade=0;writes=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-BND-004",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var getResponse = await ReadP809EmptyBundleAsync(ct);
                var getBundle = await RequireP809BundleAsync(
                    "P8-BND-004",
                    getResponse,
                    0,
                    new JsonArray(),
                    ct);
                var postResponse = await ReadP809BundleAsync(new JsonArray(), ct);
                var postBundle = await RequireP809BundleAsync(
                    "P8-BND-004",
                    postResponse,
                    0,
                    new JsonArray(),
                    ct);
                RequireP809EmptyEligibility(getBundle, "P8-BND-004 GET");
                RequireP809EmptyEligibility(postBundle, "P8-BND-004 POST");
                HarnessAssert.Equal(getBundle.CanonicalJson, postBundle.CanonicalJson,
                    "GET and POST valid-empty canonical JSON differ");
                HarnessAssert.Equal(getBundle.BundleHash, postBundle.BundleHash,
                    "GET and POST valid-empty bundle hashes differ");
                HarnessAssert.True(!string.Equals(
                        getBundle.BundleHash,
                        _p809FullBundleHash,
                        StringComparison.Ordinal),
                    "Valid-empty and full configuration bundles share a semantic hash");
                _p809EmptyBundleHash = getBundle.BundleHash;
                return new CaseObservation(
                    "GET and POST returned one deterministic valid-empty configuration bundle, distinct from the full bundle and from any result completion claim.",
                    "pins=0;isEmpty=true;configuration=EMPTY_VALID;futureResult=EMPTY_VALID;executor=UNSUPPORTED;freshness=EMPTY_VALID;fullHashDifferent=true;writes=0");
            },
            ct);
    }

    private async Task RunP809FreshnessCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-BND-013",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var empty = await RequireP809BundleAsync(
                    "P8-BND-013",
                    await ReadP809EmptyBundleAsync(ct),
                    0,
                    new JsonArray(),
                    ct);
                var full = await RequireP809BundleAsync(
                    "P8-BND-013",
                    await ReadP809BundleAsync(_p809FullPinRequest, ct),
                    8,
                    _p809FullPinRequest,
                    ct);
                RequireP809EmptyEligibility(empty, "P8-BND-013 empty");
                RequireP809FullEligibility(full, "P8-BND-013 full");
                _p809States.Add(new P809StateEvidence(
                    "P8-BND-013", "EMPTY_CONFIGURATION", empty.ConfigurationEligibility,
                    empty.Freshness, false, $"config:{empty.BundleHash}"));
                _p809States.Add(new P809StateEvidence(
                    "P8-BND-013", "EMPTY_VALID_FUTURE_RESULT", empty.FutureResultEligibility,
                    empty.Freshness, false, $"future:{empty.BundleHash}"));
                _p809States.Add(new P809StateEvidence(
                    "P8-BND-013", "ELIGIBLE_CONFIGURATION", full.ConfigurationEligibility,
                    full.Freshness, false, $"config:{full.BundleHash}"));
                HarnessAssert.True(
                    _p809States.Where(row => row.CaseId == "P8-BND-013")
                        .Select(row => row.SemanticIdentity)
                        .Distinct(StringComparer.Ordinal)
                        .Count() == 3,
                    "P8-BND-013 collapsed empty config, future result and full config states");
                return new CaseObservation(
                    "Eligibility and freshness kept empty configuration, empty-valid future result and full eligible configuration as three explicit non-complete states.",
                    "emptyConfig=EMPTY_VALID;futureResult=EMPTY_VALID;fullConfig=ELIGIBLE+FRESH;isComplete=false;states=3");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-BND-014",
            "system_admin",
            ["p809-stale-state-014", "p809-queued-barrier-014"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var full = await RequireP809BundleAsync(
                    "P8-BND-014",
                    await ReadP809BundleAsync(_p809FullPinRequest, ct),
                    8,
                    _p809FullPinRequest,
                    ct);
                var queued = await RequireP809BundleAsync(
                    "P8-BND-014",
                    await ReadP809BundleAsync(_p809QueuedPinRequest, ct),
                    8,
                    _p809QueuedPinRequest,
                    ct);
                var queuedPin = queued.Pins.OfType<JsonObject>()
                    .Single(pin => RequiredString(pin, "kind") == "READINESS");
                HarnessAssert.Equal("PENDING", RequiredString(queuedPin, "status"),
                    "Queued readiness pin did not expose PENDING");
                HarnessAssert.True(!string.Equals(queued.BundleHash, full.BundleHash, StringComparison.Ordinal),
                    "Queued readiness and completed readiness bundles share a hash");
                HarnessAssert.Equal("EMPTY_VALID", queued.FutureResultEligibility,
                    "Queued readiness incorrectly changed future-result eligibility");
                HarnessAssert.Equal("UNSUPPORTED", queued.ExecutorEligibility,
                    "Queued readiness incorrectly enabled executor");

                var staleResponse = await ValidateP809BundleAsync(
                    "p809-stale-state-014",
                    new string('0', 64),
                    _p809FullPinRequest,
                    ct);
                RequireP809Stale(staleResponse, "BUNDLE_HASH_STALE", "P8-BND-014 stale state");
                var barrier = await RequireP809BarrierAsync(
                    "P8-BND-014",
                    "P9_RESULT",
                    "p809-queued-barrier-014",
                    ct);

                var rows = new[]
                {
                    new P809StateEvidence("P8-BND-014", "UNSUPPORTED_EXECUTOR",
                        full.ExecutorEligibility, full.Freshness, false,
                        $"unsupported:{full.BundleHash}"),
                    new P809StateEvidence("P8-BND-014", "QUEUED_READINESS",
                        "PENDING", queued.Freshness, false,
                        $"queued:{queued.BundleHash}"),
                    new P809StateEvidence("P8-BND-014", "STALE_BUNDLE",
                        "STALE", "STALE", false,
                        "stale:BUNDLE_HASH_STALE"),
                    new P809StateEvidence("P8-BND-014", "PHASE_BLOCKED_RESULT",
                        barrier.Eligibility, barrier.Freshness, false,
                        "blocked:P9_RESULT")
                };
                _p809States.AddRange(rows);
                HarnessAssert.True(rows.All(row => !row.IsComplete),
                    "Queued/unsupported/stale/blocked state was marked complete");
                HarnessAssert.Equal(rows.Length,
                    rows.Select(row => row.SemanticIdentity)
                        .Distinct(StringComparer.Ordinal).Count(),
                    "Queued/unsupported/stale/blocked states collapsed semantically");
                return new CaseObservation(
                    "Unsupported executor, PENDING readiness, stale bundle and P9-blocked result remained four distinct non-complete states.",
                    "unsupported!=queued!=stale!=blocked;queuedStatus=PENDING;futureResult=EMPTY_VALID;completeClaims=0;writes=0");
            },
            ct);
    }

    private static void RequireP809PinsMatchRequest(
        JsonArray actualPins,
        JsonArray requestPins,
        string context)
    {
        var expected = requestPins.OfType<JsonObject>()
            .ToDictionary(pin => RequiredString(pin, "kind"), StringComparer.Ordinal);
        foreach (var actual in actualPins.OfType<JsonObject>())
        {
            var kind = RequiredString(actual, "kind");
            var request = expected[kind];
            foreach (var property in new[]
                     {
                         "ownerId", "configId", "versionId", "configHash", "contributionHash"
                     })
            {
                HarnessAssert.Equal(
                    RequiredString(request, property),
                    RequiredString(actual, property),
                    $"{context} {kind}.{property} differs from persisted request identity");
            }
            HarnessAssert.Equal(RequiredInt(request, "versionNo"), RequiredInt(actual, "versionNo"),
                $"{context} {kind}.versionNo drifted");
            HarnessAssert.Equal(RequiredLong(request, "revision"), RequiredLong(actual, "revision"),
                $"{context} {kind}.revision drifted");
        }
    }

    private static void RequireP809FullEligibility(P809BundleApiIdentity bundle, string context)
    {
        HarnessAssert.Equal(false, bundle.IsEmpty, $"{context} full bundle is empty");
        HarnessAssert.Equal("ELIGIBLE", bundle.ConfigurationEligibility,
            $"{context} configuration eligibility drifted");
        HarnessAssert.Equal("EMPTY_VALID", bundle.FutureResultEligibility,
            $"{context} future result eligibility drifted");
        HarnessAssert.Equal("UNSUPPORTED", bundle.ExecutorEligibility,
            $"{context} executor eligibility drifted");
        HarnessAssert.Equal("P9", bundle.TargetPhase, $"{context} target phase drifted");
        HarnessAssert.Equal("FRESH", bundle.Freshness, $"{context} freshness drifted");
    }

    private static void RequireP809EmptyEligibility(P809BundleApiIdentity bundle, string context)
    {
        HarnessAssert.Equal(true, bundle.IsEmpty, $"{context} bundle is not empty");
        HarnessAssert.Equal("EMPTY_VALID", bundle.ConfigurationEligibility,
            $"{context} configuration eligibility drifted");
        HarnessAssert.Equal("EMPTY_VALID", bundle.FutureResultEligibility,
            $"{context} future result eligibility drifted");
        HarnessAssert.Equal("UNSUPPORTED", bundle.ExecutorEligibility,
            $"{context} executor eligibility drifted");
        HarnessAssert.Equal("P9", bundle.TargetPhase, $"{context} target phase drifted");
        HarnessAssert.Equal("EMPTY_VALID", bundle.Freshness, $"{context} freshness drifted");
    }
}
