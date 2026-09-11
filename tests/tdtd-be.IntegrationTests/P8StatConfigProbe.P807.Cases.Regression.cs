using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string P7ManifestSha256 =
        "d7f839f4bd64d52256f018e4904000bf4a34fc088603b73adc5ce98323d9d921";
    private const string P7HandoffSha256 =
        "1c1c37ec15ed9b0f6a33e21e272e2d5b54053f01bf98140771f8c245976dc35d";
    private const string P7EvidenceSha256 =
        "b23abfde020c56fa12c2d9f6b286e370707e9adac84f816040e0a4ae3e761d25";
    private const string P7FinalCompositeSha256 =
        "e746b922084a16fbaa60c13da5128debb1e0cf63b8ccdc1ee38d019f992cdc50";
    private const string P7MappingDomainSemanticSha256 =
        "5b3f14c0953e6c900973d151afc678bcb4b7f0dfc1097187ad6eb57ebe54a789";
    private const string P7StatisticsDomainSemanticSha256 =
        "46e1c2b6429127b25963d948abaaf55ac7ad69abc9a97167a6b85ddefb4a8b8e";

    private async Task RunP807RegressionCasesAsync(CancellationToken ct)
    {
        var exchangeStart = _api.Exchanges.Count;

        await RunEvidenceCaseAsync(
            "P8-FLW-010",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var response = await _api.GetAsync(
                    "api/capabilities/dynamic-form-flow",
                    Actor("system_admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.OK,
                    "P8 Flow inherited capability GET");
                var root = ApiHarnessClient.RequiredObject(
                    response.Json,
                    "P8 Flow capability response");
                var activeCatalogVersion = RequiredString(
                    root,
                    "catalogVersion");
                HarnessAssert.True(
                    activeCatalogVersion is "1.4" or "1.5",
                    "P8 supports only the exact v1.4 baseline or v1.5 successor");
                HarnessAssert.Equal(
                    tdtd_be.Common.Capabilities
                        .DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                    activeCatalogVersion,
                    "Capability API differs from generated CURRENT");
                var domains = root["domains"] as JsonObject
                              ?? throw new InvalidOperationException(
                                  "Capability response lacks domains");
                var actual = domains["statisticsCapabilities"] as JsonArray
                             ?? throw new InvalidOperationException(
                                 "Capability response lacks statisticsCapabilities");
                var expected = new[]
                {
                    ("DIRECT_FIELD_TABLE_LABEL", "PATCH_REQUIRED", "P9"),
                    ("BASIC_SUMMARY", "PATCH_REQUIRED", "P9"),
                    ("ADVANCED_SUMMARY", "PATCH_REQUIRED", "P9"),
                    ("DIFF", "PATCH_REQUIRED", "P9"),
                    ("FLOW_SCOPES", "PATCH_REQUIRED", "P9"),
                    ("FLOW_STATISTIC_PROFILE", "INTENTIONAL_BLOCK", (string?)null)
                };
                HarnessAssert.Equal(expected.Length, actual.Count,
                    "Inherited statistics capability count drifted");
                for (var index = 0; index < expected.Length; index++)
                {
                    var item = actual[index] as JsonObject
                               ?? throw new InvalidOperationException(
                                   $"statisticsCapabilities[{index}] is malformed");
                    HarnessAssert.Equal(expected[index].Item1,
                        RequiredString(item, "id"),
                        $"statistics capability {index} id drifted");
                    HarnessAssert.Equal(expected[index].Item2,
                        RequiredString(item, "status"),
                        $"statistics capability {index} status drifted");
                    HarnessAssert.Equal(expected[index].Item3,
                        OptionalString(item, "targetPhase"),
                        $"statistics capability {index} targetPhase drifted");
                }

                var catalog = LoadWorkspaceJsonObject(
                    "docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_4.json");
                var sourceDomains = catalog["domains"] as JsonObject
                                    ?? throw new InvalidOperationException(
                                        "v1.4 source catalog lacks domains");
                var sourceStatistics = sourceDomains["statisticsCapabilities"]
                                       as JsonArray
                                       ?? throw new InvalidOperationException(
                                           "v1.4 source lacks statisticsCapabilities");
                var statisticsSemantic = Sha256(Encoding.UTF8.GetBytes(
                    Canonicalize(sourceStatistics)));
                HarnessAssert.Equal(P7StatisticsDomainSemanticSha256,
                    statisticsSemantic,
                    "Six inherited statistics capability semantic hash drifted");
                return new CaseObservation(
                    "The real capability API retained all six v1.4 entries in exact order/status/targetPhase and the source domain retained its frozen semantic hash.",
                    "count=6;five=PATCH_REQUIRED/P9;profile=INTENTIONAL_BLOCK/null;semantic=46e1c2b6;http=200;writes=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLW-011",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                var familyId = RequireMainFlow().FamilyId;
                var exclude = await ReadFlowVersionAsync(
                    actor,
                    familyId,
                    RequireExcludeLocked().Id,
                    ct);
                var include = await ReadFlowVersionAsync(
                    actor,
                    familyId,
                    RequireIncludeLocked().Id,
                    ct);
                HarnessAssert.Equal(exclude.PayloadJson, include.PayloadJson,
                    "Contribution variants do not share exact P7 payload bytes");
                HarnessAssert.Equal(exclude.PayloadHash, include.PayloadHash,
                    "Contribution variants do not share exact P7 payload hash");
                HarnessAssert.Equal(exclude.CatalogVersion, include.CatalogVersion,
                    "Contribution variants do not share catalogVersion");
                HarnessAssert.Equal(exclude.CatalogSemanticHash,
                    include.CatalogSemanticHash,
                    "Contribution variants do not share catalogSemanticHash");
                RequireNonEmptyP7MappingBaseline(exclude);
                RequireNonEmptyP7MappingBaseline(include);

                var catalog = LoadWorkspaceJsonObject(
                    "docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_4.json");
                var domains = catalog["domains"] as JsonObject
                              ?? throw new InvalidOperationException(
                                  "v1.4 catalog lacks domains");
                var mapping = domains["dynamicFlowMappingCapabilities"]
                              as JsonArray
                              ?? throw new InvalidOperationException(
                                  "v1.4 catalog lacks mapping domain");
                HarnessAssert.Equal(P7MappingDomainSemanticSha256,
                    Sha256(Encoding.UTF8.GetBytes(Canonicalize(mapping))),
                    "P7 mapping domain semantic pin drifted");

                var after = CaptureP7OwnerSourceFingerprints();
                P9P7OwnerSuccessorEvidence? p9Successor = null;
                if (_p9SuccessorOverlayEnabled)
                {
                    p9Successor = await VerifyP9P7OwnerSuccessorAsync(
                        after, ct);
                }
                else
                {
                    HarnessAssert.True(P7OwnerSourcePins.All(pair =>
                            after.TryGetValue(pair.Key, out var hash) &&
                            string.Equals(pair.Value, hash,
                                StringComparison.Ordinal)),
                        "A P7 owner source fingerprint differs from the canonical P7-12 handoff");
                }
                HarnessAssert.True(_p7SourceFingerprintsBefore.Count ==
                                   after.Count &&
                                   _p7SourceFingerprintsBefore.All(pair =>
                                       after.TryGetValue(pair.Key, out var hash) &&
                                       string.Equals(pair.Value, hash,
                                           StringComparison.Ordinal)),
                    "P8-07 changed a P7 evaluator/engine/source/signature/provenance owner");
                await EvidenceJson.WriteAsync(
                    Path.Combine(_paths.RunRoot,
                        "p8-07-p7-source-fingerprint.json"),
                    new
                    {
                        chainId = ChainId,
                        promptId = _promptId,
                        before = _p7SourceFingerprintsBefore,
                        after,
                        canonicalP7HandoffPins = P7OwnerSourcePins,
                        unchanged = true,
                        canonicalMatch = p9Successor is null,
                        p9SuccessorOverlay = p9Successor,
                        mappingDomainSemanticSha256 =
                            P7MappingDomainSemanticSha256,
                        evaluatorApplySourceSignatureProvenanceModified = false
                    },
                    ct);
                return new CaseObservation(
                    p9Successor is null
                        ? "Both locked versions have byte-identical non-empty P7 mapping/source/policy payload and exact catalog/form pins; all P7 owner source fingerprints remained unchanged."
                        : "Both locked versions retained the exact P7 mapping semantics; 13 owner files matched P7-12 and the sole P9 lifecycle successor matched its archived P7 bytes plus effective P9-02 lineage, with all 14 files unchanged during the run.",
                    p9Successor is null
                        ? "payloadBytesEqual=true;mappingRule=1;catalogPinsEqual=true;mappingDomain=5b3f14c0;p7OwnerFilesUnchanged=14;canonicalP7Pins=14/14;writes=0"
                        : "payloadBytesEqual=true;mappingRule=1;catalogPinsEqual=true;mappingDomain=5b3f14c0;p7OwnerFilesUnchanged=14;canonicalP7Pins=13/14;p9Successor=1/1;writes=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLW-012",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                _ = await ReadFlowFamilyAsync(
                    Actor("system_admin"),
                    RequireMainFlow().FamilyId,
                    ct);
                var immutable = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["docs/features/p7-mapping-policy/FULL_P7_MAPPING_POLICY_PROMPT_MANIFEST.json"] =
                        P7ManifestSha256,
                    [".p7-artifacts/handoffs/p7_chain_20260730150623_7a4d/P7-12.attempt-001.json"] =
                        P7HandoffSha256,
                    ["docs/features/FULL_P7_MAPPING_POLICY_EVIDENCE_2026_08_01.md"] =
                        P7EvidenceSha256,
                    [".p7-artifacts/test-runs/P7-12-final-gate-summary-20260731.json"] =
                        P7FinalCompositeSha256
                };
                foreach (var pair in immutable)
                {
                    HarnessAssert.Equal(pair.Value,
                        HashWorkspaceFile(pair.Key),
                        $"Immutable P7 artifact drifted: {pair.Key}");
                }

                var composite = LoadWorkspaceJsonObject(
                    ".p7-artifacts/test-runs/P7-12-final-gate-summary-20260731.json");
                HarnessAssert.Equal("P7-12-FINAL-GATE-SUMMARY-1",
                    RequiredString(composite, "contract"),
                    "P7 final composite contract mismatch");
                HarnessAssert.Equal("PASS", RequiredString(composite, "status"),
                    "P7 final composite status mismatch");
                HarnessAssert.Equal("ACHIEVED",
                    RequiredString(composite, "phaseClaim"),
                    "P7 final composite phase mismatch");
                HarnessAssert.Equal(true, RequiredBool(composite, "passed"),
                    "P7 final composite did not pass");
                var freshRegression = composite["freshRegression"] as JsonObject
                                      ?? throw new InvalidOperationException(
                                          "P7 final composite lacks freshRegression");
                HarnessAssert.Equal(true,
                    RequiredBool(freshRegression, "passed"),
                    "P7 final fresh regression was not green");
                HarnessAssert.Equal(0,
                    RequiredInt(freshRegression, "backendContractsFailed"),
                    "P7 final backend contract regression had failures");
                HarnessAssert.Equal(0,
                    RequiredInt(freshRegression, "frontendTestsFailed"),
                    "P7 final frontend regression had failures");

                var forbiddenExchanges = _api.Exchanges
                    .Skip(exchangeStart)
                    .Where(exchange =>
                        exchange.Path.Contains("preview", StringComparison.OrdinalIgnoreCase) ||
                        exchange.Path.Contains("/apply", StringComparison.OrdinalIgnoreCase) ||
                        exchange.Path.Contains("/run", StringComparison.OrdinalIgnoreCase))
                    .Select(exchange => new
                    {
                        exchange.Method,
                        exchange.Path
                    })
                    .ToArray();
                HarnessAssert.Equal(0, forbiddenExchanges.Length,
                    "P8-07 invoked preview/apply/run despite the phase boundary");
                foreach (var collection in ProhibitedCollections)
                {
                    HarnessAssert.Equal(0L,
                        await _database.GetCollection<BsonDocument>(collection)
                            .CountDocumentsAsync(
                                FilterDefinition<BsonDocument>.Empty,
                                cancellationToken: ct),
                        $"P8-07 left prohibited rows in {collection}");
                }

                var candidateRoot =
                    ".p8-artifacts/catalog-candidates/" +
                    $"{ChainId}/P8-07";
                var candidate = new
                {
                    catalogSha256 = HashWorkspaceFile(
                        $"{candidateRoot}/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_5.candidate.json"),
                    schemaSha256 = HashWorkspaceFile(
                        $"{candidateRoot}/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_5.schema.candidate.json"),
                    lockSha256 = HashWorkspaceFile(
                        $"{candidateRoot}/candidate-lock.json")
                };
                HarnessAssert.Equal(
                    "5b6e3fb828a6130bce2464dd98b1e8e66fd540335b0997a19187c799a969f5b5",
                    candidate.catalogSha256,
                    "P8-07 candidate catalog drifted");
                HarnessAssert.Equal(
                    "d6eba41c05a1ff4d65d8c497fa5cd56b53d6d99ae3145475553bab55722cffa4",
                    candidate.schemaSha256,
                    "P8-07 candidate schema drifted");
                HarnessAssert.Equal(
                    "3b833d0ff1c766d55d59b3b281bd9a12e681e38b1ab4cace6133c6d8e2748a67",
                    candidate.lockSha256,
                    "P8-07 candidate lock drifted");

                await EvidenceJson.WriteAsync(
                    Path.Combine(_paths.RunRoot,
                        "p8-07-p7-regression.json"),
                    new
                    {
                        chainId = ChainId,
                        promptId = _promptId,
                        immutable,
                        finalComposite = new
                        {
                            contract = RequiredString(composite, "contract"),
                            status = RequiredString(composite, "status"),
                            phaseClaim = RequiredString(composite, "phaseClaim"),
                            passed = RequiredBool(composite, "passed"),
                            freshRegressionPassed =
                                RequiredBool(freshRegression, "passed")
                        },
                        currentP7PromptVerifier = new
                        {
                            status = "SUCCESSOR_STALE_EXPECTED",
                            reason = "Authorized P8 shared-file and catalog-generator drift; immutable P7 artifacts are never rewritten."
                        },
                        candidate,
                        forbiddenEndpointCalls = forbiddenExchanges,
                        prohibitedCollectionRows = 0,
                        previewApplyRunInvoked = false,
                        contributionOutcomeCreated = false
                    },
                    ct);
                return new CaseObservation(
                    "Exact immutable P7 manifest/handoff/evidence/final composite remain hash-pinned and PASS; P8-07 candidate is exact, no preview/apply/run exchange occurred, and every result/mapping/runtime/contribution collection is empty.",
                    "p7Immutable=4/4;finalComposite=PASS;freshRegression=PASS;candidateExact=true;previewApplyRun=0;prohibitedRows=0");
            },
            ct);
    }

    private JsonObject LoadWorkspaceJsonObject(string relativePath)
    {
        var path = Path.Combine(
            _paths.WorkspaceRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
               ?? throw new InvalidOperationException(
                   $"Workspace JSON is not an object: {relativePath}");
    }

    private string HashWorkspaceFile(string relativePath)
        => Sha256(File.ReadAllBytes(Path.Combine(
            _paths.WorkspaceRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar))));
}
