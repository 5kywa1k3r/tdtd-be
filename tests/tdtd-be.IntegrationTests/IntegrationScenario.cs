using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data.Indexes;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

internal sealed partial class IntegrationScenario
{
    private const string OwnerUsername = "p1_owner";
    private const string OutsiderUsername = "p1_outsider";
    private const string AssigneeUsername = "p2_assignee";
    private const string ReviewerUsername = "p2_reviewer";
    private const string FormCode = "P1_IT_TYPED_FORM";
    private const string AmbiguousFormCode = "P1_IT_AMBIGUOUS_FORM";
    private const string FlowCode = "P1_IT_TYPED_FLOW";
    private const string MissingRevisionFormCode = "P2_IT_MISSING_REVISION";
    private const string StaleUpdateFormCode = "P2_IT_STALE_UPDATE";
    private const string ConcurrentUpdateFormCode = "P2_IT_CONCURRENT_UPDATE";
    private const string StaleImportFormCode = "P2_IT_STALE_IMPORT";
    private const string PublishStaleFormCode = "P2_IT_PUBLISH_STALE";
    private const string DuplicateFormCode = "P2_IT_DUPLICATE_CODE";
    private const string IdempotentPublishFormCode = "P2_IT_PUBLISH_IDEMPOTENT";
    private const string ConcurrentPublishFormCode = "P2_IT_PUBLISH_CONCURRENT";
    private const string StaleDeleteFormCode = "P2_IT_STALE_DELETE";
    private const string SnapshotMirrorFormCode = "P2_IT_SNAPSHOT_MIRROR";
    private const string GrantedCloneFormCode = "P2_IT_GRANTED_CLONE";
    private const string StableEnumCatalogId = "64b00000000000000000f201";

    private static readonly string[] P2StatisticIsolationCollections =
    [
        "work_report_field_stat_values",
        "work_report_field_stat_aggregates",
        "work_report_table_stat_values",
        "work_report_table_stat_aggregates",
        "work_report_label_stat_values",
        "work_report_label_stat_aggregates",
        "work_assignment_basic_summary_snapshots",
        "work_assignment_advanced_summary_day_nodes",
        "work_assignment_advanced_summary_month_nodes",
        "work_assignment_advanced_summary_year_nodes",
        "work_report_statistic_rebuild_jobs",
        "work_report_statistic_diff_results",
        "work_report_statistic_diff_exports",
        "work_report_statistic_exports",
        "stat_config_outbox"
    ];

    private readonly MongoReplicaSetLease _mongo;
    private readonly BackendServerLease _backend;
    private readonly IMongoDatabase _database;
    private readonly ApiHarnessClient _api;
    private readonly string _iterationRoot;
    private readonly bool _deliberateFailure;
    private readonly LegacyProvenanceFixture _legacyProvenanceFixture;
    private readonly string _ownerId = ObjectId.GenerateNewId().ToString();
    private readonly string _outsiderId = ObjectId.GenerateNewId().ToString();
    private readonly string _assigneeId = ObjectId.GenerateNewId().ToString();
    private readonly string _reviewerId = ObjectId.GenerateNewId().ToString();
    private readonly string _unitId = ObjectId.GenerateNewId().ToString();
    private readonly string _unitTypeId = ObjectId.GenerateNewId().ToString();
    private readonly string _assignmentId = ObjectId.GenerateNewId().ToString();
    private readonly string _workId = ObjectId.GenerateNewId().ToString();
    private readonly string _dynamicExcelId = ObjectId.GenerateNewId().ToString();
    private readonly List<string> _fieldTypes = [];
    private readonly List<string> _valueSources = [];
    private readonly Dictionary<string, string> _p2FormIds = new(StringComparer.Ordinal);

    private string? _adminToken;
    private string? _adminId;
    private string? _adminPassword;
    private string? _ownerToken;
    private string? _outsiderToken;
    private string? _assigneeToken;
    private string? _reviewerToken;
    private string? _formId;
    private int? _formRevision;
    private string? _flowId;
    private string? _lockedFlowVersionId;
    private string? _lockedFlowPayloadHash;
    private string? _reopenedFlowVersionId;
    private int? _reopenedFlowDraftRevision;
    private string? _reopenedFlowPayloadHash;
    private string? _enumCatalogId;
    private string? _bindingWorkId;
    private string? _v1AssignmentId;
    private string? _v2AssignmentId;
    private string? _v2FormId;
    private string? _grantedCloneFormId;
    private string? _wrappedPublishedFormId;
    private string? _wrappedReplacementDraftId;
    private string? _concurrentWrappedDraftId;
    private string? _v1PublishedSchemaHash;
    private string? _v2PublishedSchemaHash;

    public IntegrationScenario(
        MongoReplicaSetLease mongo,
        BackendServerLease backend,
        string iterationRoot,
        bool deliberateFailure,
        LegacyProvenanceFixture legacyProvenanceFixture)
    {
        _mongo = mongo;
        _backend = backend;
        _iterationRoot = iterationRoot;
        _deliberateFailure = deliberateFailure;
        _legacyProvenanceFixture = legacyProvenanceFixture;
        _database = mongo.Client.GetDatabase(mongo.DatabaseName);
        _api = new ApiHarnessClient(backend.BaseUri);
    }

    public string? CatalogVersion { get; private set; }
    public string? CatalogSha256 { get; private set; }

    public async Task RunAsync(HarnessCaseRunner cases, CancellationToken ct)
    {
        await cases.RunAsync("P2-DF-MIGRATION-001-RUNTIME-PROVENANCE-BACKFILL", () => VerifyRuntimeProvenanceBackfillAsync(ct));
        await cases.RunAsync("P1-BE-001-MONGO-PRIMARY", () => VerifyMongoPrimaryAsync(ct));
        await cases.RunAsync("P1-BE-002-BOOTSTRAP-ADMIN", () => BootstrapAndLoginAdminAsync(ct));
        await cases.RunAsync("P1-BE-003-SEED-ACTOR-LOGIN", () => SeedAndLoginActorsAsync(ct));
        await cases.RunAsync("P1-BE-004-CATALOG-ETAG", () => VerifyCatalogEtagAsync(ct));
        await cases.RunAsync("P1-BE-005-FORM-TYPED-CREATE-GET", () => CreateAndGetTypedFormAsync(ct));
        await cases.RunAsync("P1-BE-006-FORM-UPDATE-PUBLISH", () => UpdateAndPublishTypedFormAsync(ct));
        await cases.RunAsync("P4-FLOW-CRUD-ROLLBACK-AFTER-RECEIPT", () => VerifyFlowDefinitionTransactionRollbackAsync(ct));
        await cases.RunAsync("P2-DF-MIGRATION-002-LIVE-SCHEMA-DRIFT-FAIL-CLOSED", () => RejectStartupBackfillLiveSchemaDriftAsync(ct));
        await cases.RunAsync("P1-BE-007-FORM-MIXED-REJECT", () => RejectMixedTypedLegacyFormAsync(ct));
        await cases.RunAsync("P1-BE-008-FLOW-TYPED-CREATE-LOCK", () => CreateTypedFlowAsync(ct));
        await cases.RunAsync("P1-BE-009-FLOW-OUTSIDER-DENY", () => VerifyOutsiderDeniedAsync(ct));
        await cases.RunAsync("P1-BE-010-FLOW-ROOT-LAUNCH-DENY", () => VerifyOutsiderRootLaunchDeniedAsync(ct));
        await cases.RunAsync("P1-BE-011-FLOW-PARTICIPANT-VERSION-GRANT", () => GrantAndVerifyParticipantAsync(ct));
        await cases.RunAsync("P4-FLOW-CRUD-12-SAVE-SAVE-RACE", () => VerifyFlowDraftSaveRaceAsync(ct));
        await RunP4DefinitionMatrixAsync(cases, ct);
        await cases.RunAsync("P1-BE-012-MONGO-PERSISTENCE", () => VerifyPersistenceAsync(ct));
        await cases.RunAsync("P1-BE-013-SCOPED-JOB-POLLER", () => VerifyScopedJobPollerAsync(ct));
        await cases.RunAsync("P2-DF-CAS-001-MISSING-EXPECTED-REVISION", () => RejectMissingExpectedRevisionAsync(ct));
        await cases.RunAsync("P2-DF-CAS-002-STALE-PUT", () => RejectStaleUpdateAsync(ct));
        await cases.RunAsync("P2-DF-CAS-003-CONCURRENT-PUT", () => ResolveConcurrentUpdatesAsync(ct));
        await cases.RunAsync("P2-DF-CAS-004-STALE-IMPORT", () => RejectStaleImportAsync(ct));
        await cases.RunAsync("P2-DF-CAS-005-PUBLISH-STALE-UPDATE", () => ProtectPublishedFormFromStaleUpdateAsync(ct));
        await cases.RunAsync("P2-DF-CAS-006-DUPLICATE-CODE", () => RejectDuplicateCodeAsync(ct));
        await cases.RunAsync("P2-DF-CAS-007-PUBLISH-IDEMPOTENT", () => VerifyIdempotentPublishAsync(ct));
        await cases.RunAsync("P2-DF-CAS-008-CONCURRENT-PUBLISH", () => VerifyConcurrentPublishAsync(ct));
        await cases.RunAsync("P2-DF-CAS-009-STALE-DELETE", () => RejectMissingAndStaleDeleteAsync(ct));
        await cases.RunAsync("P2-DF-CAS-010-CONCURRENT-WRAP", () => VerifyConcurrentDynamicExcelWrapAsync(ct));
        await cases.RunAsync("P2-DF-CAS-012-WRAP-REUSE-REACTIVATION-GUARD", () => VerifyWrapReuseReactivationGuardAsync(ct));
        await cases.RunAsync("P2-DF-CAS-011-DRAFT-FORM-FLOW-BIND-RACE", () => RejectDraftFormFlowBindingRaceAsync(ct));
        await cases.RunAsync("P2-DF-LC-001-PUBLISHED-SNAPSHOT", () => VerifyPublishedSchemaSnapshotAsync(ct));
        await cases.RunAsync("P2-DF-LC-002-WRAP-DYNAMIC-EXCEL", () => VerifyWrappedDynamicExcelLifecycleAsync(ct));
        await cases.RunAsync("P2-DF-LC-003-V1-RUNTIME-BINDING", () => CreateV1RuntimeBindingAsync(ct));
        await cases.RunAsync("P2-DF-LC-004-ACTOR-MATRIX-CLONE", () => VerifyActorMatrixAndCloneAsync(ct));
        await cases.RunAsync("P2-DF-LC-005-CONCURRENT-NEXT-VERSION", () => CreateConcurrentNextVersionAsync(ct));
        await cases.RunAsync("P2-DF-LC-006-V2-PUBLISH-OWNERSHIP", () => UpdateAndPublishV2Async(ct));
        await cases.RunAsync("P2-DF-LC-007-EXACT-VERSION-BINDING", () => VerifyExactVersionAssignmentBindingAsync(ct));
        await cases.RunAsync("P2-DF-SCHEMA-01-BLANK-SECTION-ID", () => RejectSchemaCreateAsync(1, "DYNAMIC_FORM_SECTION_CONFIG_INVALID", ct));
        await cases.RunAsync("P2-DF-SCHEMA-02-DUPLICATE-SECTION-ID", () => RejectSchemaCreateAsync(2, "DYNAMIC_FORM_SECTION_CONFIG_INVALID", ct));
        await cases.RunAsync("P2-DF-SCHEMA-03-BLANK-SECTION-TITLE", () => RejectSchemaCreateAsync(3, "DYNAMIC_FORM_SECTION_CONFIG_INVALID", ct));
        await cases.RunAsync("P2-DF-SCHEMA-04-BLANK-FIELD-ID", () => RejectSchemaCreateAsync(4, "DYNAMIC_FORM_FIELD_CONFIG_INVALID", ct));
        await cases.RunAsync("P2-DF-SCHEMA-05-DUPLICATE-FIELD-ID", () => RejectSchemaCreateAsync(5, "DYNAMIC_FORM_FIELD_CONFIG_INVALID", ct));
        await cases.RunAsync("P2-DF-SCHEMA-06-ORPHAN-FIELD-SECTION", () => RejectSchemaCreateAsync(6, "DYNAMIC_FORM_FIELD_CONFIG_INVALID", ct));
        await cases.RunAsync("P2-DF-SCHEMA-07-UNKNOWN-FIELD-TYPE", () => RejectSchemaCreateAsync(7, "DYNAMIC_FORM_FIELD_CONFIG_INVALID", ct));
        await cases.RunAsync("P2-DF-SCHEMA-08-DUPLICATE-FIELD-KEY", () => RejectSchemaCreateAsync(8, "DYNAMIC_FORM_FIELD_CONFIG_INVALID", ct));
        await cases.RunAsync("P2-DF-SCHEMA-09-DUPLICATE-OPTION-CODE", () => RejectSchemaCreateAsync(9, "DYNAMIC_FORM_FIELD_CONFIG_INVALID", ct));
        await cases.RunAsync("P2-DF-SCHEMA-10-OPTION-LIMIT-101", () => RejectSchemaCreateAsync(10, "DYNAMIC_FORM_LIMIT_EXCEEDED", ct));
        await cases.RunAsync("P2-DF-SCHEMA-11-BAD-VALUE-SOURCE", () => RejectSchemaCreateAsync(11, "DYNAMIC_FORM_FIELD_CONFIG_INVALID", ct));
        await cases.RunAsync("P2-DF-SCHEMA-12-FIELD-LIMIT-201", () => RejectSchemaCreateAsync(12, "DYNAMIC_FORM_LIMIT_EXCEEDED", ct));
        await cases.RunAsync("P2-DF-SCHEMA-13-BLOCK-LIMIT-31", () => RejectSchemaCreateAsync(13, "DYNAMIC_FORM_LIMIT_EXCEEDED", ct));
        await cases.RunAsync("P2-DF-SCHEMA-14-DUPLICATE-DYNAMIC-EXCEL", () => RejectSchemaCreateAsync(14, "DYNAMIC_FORM_BLOCK_DUPLICATE", ct));
        await cases.RunAsync("P2-DF-SCHEMA-15-INCOMPATIBLE-TABLE-MODE", () => RejectSchemaCreateAsync(15, "DYNAMIC_FORM_TABLE_MODE_MISMATCH", ct));
        await cases.RunAsync("P2-DF-SCHEMA-16-OVERSIZED-SCHEMA-JSON", () => RejectSchemaCreateAsync(16, "DYNAMIC_FORM_LIMIT_EXCEEDED", ct));
        await cases.RunAsync("P2-DF-SCHEMA-17-TYPED-LEGACY-WRONG-KIND", () => RejectTypedLegacySchemaWrongKindsAsync(ct));
        await cases.RunAsync("P2-DF-CRUD-001-SEARCH-FILTER-PAGE", () => VerifyCrudSearchFilterPageAsync(ct));
        await cases.RunAsync("P2-DF-CRUD-007-SEARCH-OFFSET-STABLE-TIE", () => VerifyCrudSearchOffsetAndStableTiesAsync(ct));
        await cases.RunAsync("P2-DF-CRUD-008-MALFORMED-IDS-FAIL-CLOSED", () => RejectMalformedDynamicFormAndExcelIdsAsync(ct));
        await cases.RunAsync("P2-DF-CRUD-002-DETAIL-REOPEN", () => VerifyCrudDetailReopenAsync(ct));
        await cases.RunAsync("P2-DF-CRUD-003-SOFT-DELETE", () => VerifyCrudSoftDeleteAsync(ct));
        await cases.RunAsync("P2-DF-CRUD-004-DELETE-GUARD-LINKED", () => VerifyCrudLinkedDeleteGuardAsync(ct));
        await cases.RunAsync("P2-DF-CRUD-005-DELETE-GUARD-DRAFT-FLOW", () => VerifyDraftFlowDeleteGuardAsync(ct));
        await cases.RunAsync("P2-DF-CRUD-006-DELETE-GUARD-FLOW-MAPPING-ALIASES", () => VerifyFlowMappingAliasDeleteGuardsAsync(ct));
        await cases.RunAsync("P2-DF-VERSION-001-STATISTICS-ONLY", () => UpdatePublishedStatisticsOnlyAsync(ct));
        await cases.RunAsync("P2-DF-VERSION-002-STATISTICS-STRUCTURAL-DRIFT", () => RejectStatisticsStructuralDriftAsync(ct));
        await cases.RunAsync("P2-DF-VERSION-003-PUBLISHED-PUT-REJECTED", () => RejectNormalPutOnPublishedAsync(ct));
        await cases.RunAsync("P2-DF-VERSION-004-PUBLISHED-DELETE-REJECTED", () => RejectDeleteOnPublishedAsync(ct));
        await cases.RunAsync("P2-DF-VERSION-005-CORRUPT-SNAPSHOT-BIND-FAIL-CLOSED", () => RejectCorruptPublishedSnapshotBindingAsync(ct));
        await cases.RunAsync("P2-DF-VERSION-006-DYNAMIC-EXCEL-METADATA-DRIFT", () => RejectDynamicExcelMetadataDriftAsync(ct));
        await cases.RunAsync("P2-DF-VERSION-007-LIVE-SCHEMA-DRIFT-BIND-FAIL-CLOSED", () => RejectLiveSchemaDriftBindingAsync(ct));
        await cases.RunAsync("P2-DF-VERSION-008-DEACTIVATED-ENUM-PUBLISH-REJECTED", () => RejectDeactivatedEnumCatalogPublishAsync(ct));
        await cases.RunAsync("P2-DF-PERM-001-ENDPOINT-ACTION-MATRIX", () => VerifyEndpointPermissionMatrixAsync(ct));
        await cases.RunAsync("P2-DF-PERM-002-HIDDEN-FORM-ASSIGNMENT-BIND-DENIED", () => RejectHiddenFormAssignmentBindingAsync(ct));
        await cases.RunAsync("P2-DF-PERM-003-HIDDEN-FORM-FLOW-BIND-DENIED", () => RejectHiddenFormFlowBindingAsync(ct));
        await RunP3DynamicFormRuntimeAsync(cases, ct);
        if (_deliberateFailure)
        {
            await cases.RunAsync(
                "P3-DF-SELFTEST-DELIBERATE-FAILURE",
                () => DeliberatelyFailAssertionAsync(ct));
        }
    }

    public void Dispose() => _api.Dispose();

    public async Task WriteArtifactsAsync(IReadOnlyList<HarnessCaseResult> cases, CancellationToken ct)
    {
        await EvidenceJson.WriteAsync(
            Path.Combine(_iterationRoot, "manifest.json"),
            new
            {
                source = "FULL-P1-TEST-HARNESS",
                databaseName = _mongo.DatabaseName,
                catalog = new { version = CatalogVersion, sha256 = CatalogSha256 },
                seedMatrix = new
                {
                    source = "GET /api/capabilities/dynamic-form-flow",
                    dynamicFormFieldTypes = _fieldTypes,
                    dynamicFormValueSources = _valueSources
                },
                actors = new
                {
                    admin = new { username = "admin", id = _adminId },
                    owner = new { username = OwnerUsername, id = _ownerId },
                    outsider = new { username = OutsiderUsername, id = _outsiderId },
                    assignee = new { username = AssigneeUsername, id = _assigneeId },
                    reviewer = new { username = ReviewerUsername, id = _reviewerId }
                },
                ids = new
                {
                    unitTypeId = _unitTypeId,
                    unitId = _unitId,
                    enumCatalogId = _enumCatalogId,
                    formId = _formId,
                    formRevision = _formRevision,
                    flowId = _flowId,
                    lockedFlowVersionId = _lockedFlowVersionId,
                    workId = _workId,
                    participantAssignmentId = _assignmentId,
                    dynamicExcelId = _dynamicExcelId,
                    bindingWorkId = _bindingWorkId,
                    v1AssignmentId = _v1AssignmentId,
                    v2AssignmentId = _v2AssignmentId,
                    v2FormId = _v2FormId,
                    grantedCloneFormId = _grantedCloneFormId,
                    wrappedPublishedFormId = _wrappedPublishedFormId,
                    wrappedReplacementDraftId = _wrappedReplacementDraftId,
                    concurrentWrappedDraftId = _concurrentWrappedDraftId,
                    v1PublishedSchemaHash = _v1PublishedSchemaHash,
                    v2PublishedSchemaHash = _v2PublishedSchemaHash,
                    p2CasFormIds = _p2FormIds
                },
                traceMapping = new
                {
                    dynamicFormCrud = new[]
                    {
                        new { requirement = "DF-CRUD-CREATE", caseIds = new[] { "P1-BE-005-FORM-TYPED-CREATE-GET" } },
                        new { requirement = "DF-CRUD-READ-REOPEN", caseIds = new[] { "P2-DF-CRUD-002-DETAIL-REOPEN" } },
                        new { requirement = "DF-CRUD-SEARCH-FILTER-PAGE", caseIds = new[] { "P2-DF-CRUD-001-SEARCH-FILTER-PAGE", "P2-DF-CRUD-007-SEARCH-OFFSET-STABLE-TIE" } },
                        new { requirement = "DF-CRUD-MALFORMED-IDS-FAIL-CLOSED", caseIds = new[] { "P2-DF-CRUD-008-MALFORMED-IDS-FAIL-CLOSED" } },
                        new { requirement = "DF-CRUD-UPDATE", caseIds = new[] { "P1-BE-006-FORM-UPDATE-PUBLISH", "P2-DF-CAS-002-STALE-PUT", "P2-DF-CAS-003-CONCURRENT-PUT" } },
                        new { requirement = "DF-CRUD-SOFT-DELETE", caseIds = new[] { "P2-DF-CRUD-003-SOFT-DELETE", "P2-DF-CAS-009-STALE-DELETE" } },
                        new { requirement = "DF-CRUD-DELETE-LINK-GUARD", caseIds = new[] { "P2-DF-CRUD-004-DELETE-GUARD-LINKED", "P2-DF-CRUD-005-DELETE-GUARD-DRAFT-FLOW", "P2-DF-CRUD-006-DELETE-GUARD-FLOW-MAPPING-ALIASES" } },
                        new { requirement = "DF-CRUD-CLONE", caseIds = new[] { "P2-DF-LC-004-ACTOR-MATRIX-CLONE", "P2-DF-PERM-001-ENDPOINT-ACTION-MATRIX" } },
                        new { requirement = "DF-CRUD-HISTORY", caseIds = new[] { "P2-DF-LC-004-ACTOR-MATRIX-CLONE", "P2-DF-LC-005-CONCURRENT-NEXT-VERSION" } },
                        new { requirement = "DF-CRUD-WRAP-DYNAMIC-EXCEL", caseIds = new[] { "P2-DF-CAS-010-CONCURRENT-WRAP", "P2-DF-CAS-012-WRAP-REUSE-REACTIVATION-GUARD", "P2-DF-LC-002-WRAP-DYNAMIC-EXCEL" } },
                        new { requirement = "DF-CRUD-IMPORT-DYNAMIC-EXCEL-CAS", caseIds = new[] { "P2-DF-CAS-004-STALE-IMPORT", "P2-DF-PERM-001-ENDPOINT-ACTION-MATRIX" } },
                        new { requirement = "DF-CRUD-DUPLICATE-CODE", caseIds = new[] { "P2-DF-CAS-006-DUPLICATE-CODE" } },
                        new { requirement = "DF-CRUD-CONCURRENT-WRITE", caseIds = new[] { "P2-DF-CAS-003-CONCURRENT-PUT", "P2-DF-CAS-008-CONCURRENT-PUBLISH", "P2-DF-CAS-010-CONCURRENT-WRAP", "P2-DF-CAS-012-WRAP-REUSE-REACTIVATION-GUARD", "P2-DF-CAS-011-DRAFT-FORM-FLOW-BIND-RACE", "P2-DF-LC-005-CONCURRENT-NEXT-VERSION" } }
                    },
                    dynamicFormSchema = Enumerable.Range(1, 17)
                        .Select(number => new
                        {
                            requirement = $"DF-SCHEMA-{number:00}",
                            caseIds = new[]
                            {
                                cases.Single(result => result.CaseId.StartsWith($"P2-DF-SCHEMA-{number:00}-", StringComparison.Ordinal)).CaseId
                            }
                        })
                        .ToArray(),
                    dynamicFormPermission = new[]
                    {
                        new { requirement = "DF-PERM-01", scope = "OWNER", caseIds = new[] { "P2-DF-LC-004-ACTOR-MATRIX-CLONE", "P2-DF-PERM-001-ENDPOINT-ACTION-MATRIX" } },
                        new { requirement = "DF-PERM-02", scope = "SYSTEM_ADMIN", caseIds = new[] { "P2-DF-LC-004-ACTOR-MATRIX-CLONE", "P2-DF-PERM-001-ENDPOINT-ACTION-MATRIX" } },
                        new { requirement = "DF-PERM-03", scope = "CLONE_GRANT", caseIds = new[] { "P2-DF-LC-004-ACTOR-MATRIX-CLONE", "P2-DF-PERM-001-ENDPOINT-ACTION-MATRIX" } },
                        new { requirement = "DF-PERM-04", scope = "RUNTIME_ASSIGNEE", caseIds = new[] { "P2-DF-LC-003-V1-RUNTIME-BINDING", "P2-DF-LC-007-EXACT-VERSION-BINDING", "P2-DF-PERM-001-ENDPOINT-ACTION-MATRIX", "P2-DF-PERM-002-HIDDEN-FORM-ASSIGNMENT-BIND-DENIED" } },
                        new { requirement = "DF-PERM-05", scope = "RUNTIME_REVIEWER", caseIds = new[] { "P2-DF-LC-003-V1-RUNTIME-BINDING", "P2-DF-LC-004-ACTOR-MATRIX-CLONE", "P2-DF-PERM-001-ENDPOINT-ACTION-MATRIX" } },
                        new { requirement = "DF-PERM-06", scope = "OUTSIDER", caseIds = new[] { "P2-DF-LC-004-ACTOR-MATRIX-CLONE", "P2-DF-PERM-001-ENDPOINT-ACTION-MATRIX", "P2-DF-PERM-002-HIDDEN-FORM-ASSIGNMENT-BIND-DENIED", "P2-DF-PERM-003-HIDDEN-FORM-FLOW-BIND-DENIED" } },
                        new { requirement = "DF-PERM-07", scope = "DESIGN_SEARCH_VISIBILITY", caseIds = new[] { "P2-DF-LC-004-ACTOR-MATRIX-CLONE", "P2-DF-LC-006-V2-PUBLISH-OWNERSHIP", "P2-DF-LC-007-EXACT-VERSION-BINDING", "P2-DF-PERM-001-ENDPOINT-ACTION-MATRIX" } },
                        new { requirement = "DF-PERM-08", scope = "HISTORY_SCOPE", caseIds = new[] { "P2-DF-LC-004-ACTOR-MATRIX-CLONE", "P2-DF-LC-005-CONCURRENT-NEXT-VERSION", "P2-DF-LC-006-V2-PUBLISH-OWNERSHIP", "P2-DF-LC-007-EXACT-VERSION-BINDING", "P2-DF-PERM-001-ENDPOINT-ACTION-MATRIX" } },
                        new { requirement = "DF-PERM-09", scope = "MUTATION_ENDPOINTS", caseIds = new[] { "P2-DF-LC-004-ACTOR-MATRIX-CLONE", "P2-DF-CRUD-004-DELETE-GUARD-LINKED", "P2-DF-CRUD-005-DELETE-GUARD-DRAFT-FLOW", "P2-DF-VERSION-003-PUBLISHED-PUT-REJECTED", "P2-DF-VERSION-004-PUBLISHED-DELETE-REJECTED", "P2-DF-PERM-001-ENDPOINT-ACTION-MATRIX" } },
                        new { requirement = "DF-PERM-10", scope = "EXACT_RUNTIME_DEEP_LINK_AND_FORBIDDEN_ENVELOPE_NON_LEAK", caseIds = new[] { "P2-DF-LC-003-V1-RUNTIME-BINDING", "P2-DF-LC-006-V2-PUBLISH-OWNERSHIP", "P2-DF-LC-007-EXACT-VERSION-BINDING", "P2-DF-PERM-001-ENDPOINT-ACTION-MATRIX", "P2-DF-PERM-002-HIDDEN-FORM-ASSIGNMENT-BIND-DENIED", "P2-DF-PERM-003-HIDDEN-FORM-FLOW-BIND-DENIED" } }
                    },
                    dynamicFormVersion = new[]
                    {
                        new { requirement = "DF-VERSION-LEGACY-RUNTIME-PROVENANCE", caseIds = new[] { "P2-DF-MIGRATION-001-RUNTIME-PROVENANCE-BACKFILL" } },
                        new { requirement = "DF-VERSION-PUBLISHED-SNAPSHOT", caseIds = new[] { "P2-DF-LC-001-PUBLISHED-SNAPSHOT" } },
                        new { requirement = "DF-VERSION-NEXT-CONCURRENT", caseIds = new[] { "P2-DF-LC-005-CONCURRENT-NEXT-VERSION" } },
                        new { requirement = "DF-VERSION-OWNER-PRESERVED", caseIds = new[] { "P2-DF-LC-005-CONCURRENT-NEXT-VERSION", "P2-DF-LC-006-V2-PUBLISH-OWNERSHIP" } },
                        new { requirement = "DF-VERSION-EXACT-RUNTIME-BINDING", caseIds = new[] { "P2-DF-LC-007-EXACT-VERSION-BINDING" } },
                        new { requirement = "DF-VERSION-STATISTICS-ONLY", caseIds = new[] { "P2-DF-VERSION-001-STATISTICS-ONLY" } },
                        new { requirement = "DF-VERSION-STATISTICS-STRUCTURAL-DRIFT", caseIds = new[] { "P2-DF-VERSION-002-STATISTICS-STRUCTURAL-DRIFT" } },
                        new { requirement = "DF-VERSION-PUBLISHED-PUT-GUARD", caseIds = new[] { "P2-DF-VERSION-003-PUBLISHED-PUT-REJECTED" } },
                        new { requirement = "DF-VERSION-PUBLISHED-DELETE-GUARD", caseIds = new[] { "P2-DF-VERSION-004-PUBLISHED-DELETE-REJECTED" } },
                        new { requirement = "DF-VERSION-PUBLISHED-SNAPSHOT-INTEGRITY", caseIds = new[] { "P2-DF-VERSION-005-CORRUPT-SNAPSHOT-BIND-FAIL-CLOSED" } },
                        new { requirement = "DF-VERSION-DYNAMIC-EXCEL-METADATA-DRIFT", caseIds = new[] { "P2-DF-VERSION-006-DYNAMIC-EXCEL-METADATA-DRIFT" } },
                        new { requirement = "DF-VERSION-LIVE-SCHEMA-DRIFT", caseIds = new[] { "P2-DF-VERSION-007-LIVE-SCHEMA-DRIFT-BIND-FAIL-CLOSED" } },
                        new { requirement = "DF-VERSION-STARTUP-LIVE-SCHEMA-INTEGRITY", caseIds = new[] { "P2-DF-MIGRATION-002-LIVE-SCHEMA-DRIFT-FAIL-CLOSED" } },
                        new { requirement = "DF-VERSION-ENUM-CATALOG-PUBLISH-REVALIDATION", caseIds = new[] { "P2-DF-VERSION-008-DEACTIVATED-ENUM-PUBLISH-REJECTED" } },
                        new { requirement = "DF-FLOW-PUBLISHED-FORM-BIND-ONLY", caseIds = new[] { "P2-DF-CAS-011-DRAFT-FORM-FLOW-BIND-RACE" } }
                    },
                    dynamicFormRuntime = new[]
                    {
                        new { requirement = "P3-RUNTIME-CAPABILITY-ACTOR-STATE-MATRIX", caseIds = new[] { "P3-DF-CAP-001-ACTOR-STATE-MATRIX", "P3-DF-LC-004-APPROVE-RECALL" } },
                        new { requirement = "P3-LIFECYCLE-POST-COMMIT-OUTBOX-RECOVERY", caseIds = new[] { "P3-DF-OUTBOX-001-POST-COMMIT-FAILURE-RECOVERY" } },
                        new { requirement = "P3-SECTION-RECREATE-AND-CANONICAL-SOURCE", caseIds = new[] { "P3-DF-OUTBOX-001-POST-COMMIT-FAILURE-RECOVERY", "P3-DF-SECTION-002-STALE-REPAIR", "P3-DF-SECTION-001-DIRECT-MONGO-RECONCILIATION" } },
                        new { requirement = "P3-EVIDENCE-SECRET-REDACTION", caseIds = new[] { "P3-DF-EVIDENCE-001-SECRET-REDACTION" } }
                    }
                },
                expectedLedger = cases.Select(x => new
                {
                    x.CaseId,
                    expectedVerdict = x.CaseId == "P3-DF-SELFTEST-DELIBERATE-FAILURE"
                        ? HarnessVerdict.KHONG_DAT
                        : HarnessVerdict.DAT
                }),
                deliberateFailureMode = _deliberateFailure
            },
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(_iterationRoot, "request-response.redacted.json"),
            new
            {
                redaction = "password/token/bootstrap-key/secret property values",
                exchanges = _api.Exchanges
            },
            ct);
    }

    private async Task<CaseObservation> VerifyRuntimeProvenanceBackfillAsync(CancellationToken ct)
    {
        var formObjectId = ObjectId.Parse(_legacyProvenanceFixture.FormId);
        var runtimeObjectId = ObjectId.Parse(_legacyProvenanceFixture.RuntimeDocumentId);
        var flowFamilyObjectId = ObjectId.Parse(_legacyProvenanceFixture.FlowFamilyId);
        var flowVersionObjectId = ObjectId.Parse(_legacyProvenanceFixture.FlowVersionId);
        var forms = _database.GetCollection<BsonDocument>("dynamic_form_templates");
        var runtime = _database.GetCollection<BsonDocument>(_legacyProvenanceFixture.RuntimeCollectionName);
        var flowFamilies = _database.GetCollection<BsonDocument>("dynamic_flow_templates");
        var flowVersions = _database.GetCollection<BsonDocument>("dynamic_flow_template_versions");
        var flowManifests = _database.GetCollection<BsonDocument>(
            DynamicFlowDefinitionMetadataBackfill.RollbackManifestCollectionName);
        var flowFamilyFilter = Builders<BsonDocument>.Filter.Eq("_id", flowFamilyObjectId);
        var flowVersionFilter = Builders<BsonDocument>.Filter.Eq("_id", flowVersionObjectId);
        var flowManifestFilter =
            Builders<BsonDocument>.Filter.Eq(
                "migrationId",
                DynamicFlowDefinitionMetadataBackfill.MigrationId) &
            Builders<BsonDocument>.Filter.In(
                "documentId",
                new BsonValue[] { flowFamilyObjectId, flowVersionObjectId });
        try
        {
            var form = await forms.Find(Builders<BsonDocument>.Filter.Eq("_id", formObjectId)).SingleAsync(ct);
            HarnessAssert.Equal(
                _legacyProvenanceFixture.FormId,
                form["familyId"].AsObjectId.ToString(),
                "Legacy Dynamic Form familyId backfill mismatch");
            HarnessAssert.Equal(1, form["versionNo"].ToInt32(), "Legacy Dynamic Form versionNo backfill mismatch");
            HarnessAssert.Equal(1, form["revision"].ToInt32(), "Legacy Dynamic Form revision backfill mismatch");
            HarnessAssert.Equal("LEGACY", form["lineageStatus"].AsString, "Legacy Dynamic Form lineage backfill mismatch");
            var snapshotJson = form["publishedSchemaSnapshotJson"].AsString;
            var schemaHash = form["publishedSchemaHash"].AsString;
            HarnessAssert.True(!string.IsNullOrWhiteSpace(snapshotJson), "Legacy Dynamic Form snapshot was not backfilled");
            HarnessAssert.Equal(64, schemaHash.Length, "Legacy Dynamic Form schema hash length mismatch");
            HarnessAssert.Equal(ComputeSha256(snapshotJson), schemaHash, "Legacy Dynamic Form snapshot/hash mismatch");

            var runtimeDoc = await runtime
                .Find(Builders<BsonDocument>.Filter.Eq("_id", runtimeObjectId))
                .SingleAsync(ct);
            HarnessAssert.Equal(
                _legacyProvenanceFixture.FormId,
                runtimeDoc["dynamicFormFamilyId"].AsObjectId.ToString(),
                "Runtime family provenance backfill mismatch");
            HarnessAssert.Equal(1, runtimeDoc["dynamicFormVersionNo"].ToInt32(), "Runtime version provenance backfill mismatch");
            HarnessAssert.Equal(schemaHash, runtimeDoc["dynamicFormSchemaHash"].AsString, "Runtime schema provenance backfill mismatch");

            var migratedFamily = await flowFamilies.Find(flowFamilyFilter).SingleAsync(ct);
            var migratedVersion = await flowVersions.Find(flowVersionFilter).SingleAsync(ct);
            HarnessAssert.Equal(1, migratedFamily["familyRevision"].ToInt32(), "Legacy Flow family revision backfill mismatch");
            HarnessAssert.Equal(
                _legacyProvenanceFixture.FlowActorId,
                migratedFamily["ownerUserId"].AsObjectId.ToString(),
                "Legacy Flow owner backfill mismatch");
            HarnessAssert.Equal(
                _legacyProvenanceFixture.FormId,
                migratedFamily["rootDynamicFormTemplateId"].AsObjectId.ToString(),
                "Legacy Flow family root backfill mismatch");
            HarnessAssert.True(
                migratedFamily["hasLockedVersion"].AsBoolean,
                "Legacy Flow immutable-version evidence was not preserved");
            HarnessAssert.Equal(1, migratedVersion["draftRevision"].ToInt32(), "Legacy Flow draft revision backfill mismatch");
            HarnessAssert.Equal(
                DynamicFlowDefinitionMigrationStates.RequiresReview,
                migratedVersion["migrationState"].AsString,
                "Unproven locked legacy Flow must require review");
            HarnessAssert.True(
                !migratedVersion["definitionLockable"].AsBoolean,
                "Migration silently made an unproven locked Flow lockable");
            HarnessAssert.Equal(
                DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase,
                migratedVersion["executionEligibility"].AsString,
                "Legacy Flow runtime eligibility backfill mismatch");
            HarnessAssert.Equal(
                _legacyProvenanceFixture.FlowPayloadJson,
                migratedVersion["payloadJson"].AsString,
                "Immutable legacy Flow payload bytes were rewritten");
            HarnessAssert.Equal(
                _legacyProvenanceFixture.FlowPayloadHash,
                migratedVersion["payloadHash"].AsString,
                "Immutable legacy Flow hash was rewritten");
            HarnessAssert.Equal(
                2L,
                await flowManifests.CountDocumentsAsync(flowManifestFilter, cancellationToken: ct),
                "P4 migration did not persist exact family+version rollback manifests");
            var appliedManifests = await flowManifests
                .Find(flowManifestFilter)
                .Sort(new BsonDocument("phase", 1))
                .ToListAsync(ct);
            HarnessAssert.Equal(
                DynamicFlowDefinitionMetadataBackfill.FamilyPhase,
                appliedManifests[0]["phase"].AsInt32,
                "P4 family rollback manifest phase mismatch");
            HarnessAssert.Equal(
                DynamicFlowDefinitionMetadataBackfill.VersionPhase,
                appliedManifests[1]["phase"].AsInt32,
                "P4 version rollback manifest phase mismatch");
            HarnessAssert.True(
                appliedManifests.All(manifest =>
                    manifest["state"].AsString ==
                    DynamicFlowDefinitionMetadataBackfill.ManifestApplied),
                "P4 rollback manifests were not committed as APPLIED");
            HarnessAssert.True(
                appliedManifests.All(manifest =>
                    manifest["beforeObservedSha256"].AsString.Length == 64 &&
                    manifest["afterObservedSha256"].AsString.Length == 64),
                "P4 rollback manifests did not persist exact pre/postimage hashes");

            var migratedFamilySha = ComputeBsonSha256(migratedFamily);
            var migratedVersionSha = ComputeBsonSha256(migratedVersion);
            var migrationOptions = new MongoOptions
            {
                ConnectionString = _mongo.ConnectionString,
                Database = _mongo.DatabaseName
            };
            await DynamicFlowDefinitionMetadataBackfill.RunAsync(
                _database,
                migrationOptions,
                ct);
            HarnessAssert.Equal(
                migratedFamilySha,
                ComputeBsonSha256(await flowFamilies.Find(flowFamilyFilter).SingleAsync(ct)),
                "Second real-Mongo Flow migration run changed family bytes");
            HarnessAssert.Equal(
                migratedVersionSha,
                ComputeBsonSha256(await flowVersions.Find(flowVersionFilter).SingleAsync(ct)),
                "Second real-Mongo Flow migration run changed version bytes");
            HarnessAssert.Equal(
                2L,
                await flowManifests.CountDocumentsAsync(flowManifestFilter, cancellationToken: ct),
                "Second migration run duplicated rollback manifests");

            var originalMigrationState = migratedVersion["migrationState"].DeepClone();
            await flowVersions.UpdateOneAsync(
                flowVersionFilter,
                Builders<BsonDocument>.Update.Set("migrationState", "P4_ROLLBACK_DRIFT_PROBE"),
                cancellationToken: ct);
            var driftRefused = false;
            try
            {
                await DynamicFlowDefinitionMetadataBackfill.RollbackAsync(
                    _database,
                    migrationOptions,
                    ct);
            }
            catch (InvalidOperationException error)
            {
                driftRefused = error.Message.Contains(
                    "postimage",
                    StringComparison.OrdinalIgnoreCase);
            }
            HarnessAssert.True(
                driftRefused,
                "P4 rollback did not fail closed on a concurrent version postimage drift");
            HarnessAssert.Equal(
                migratedFamilySha,
                ComputeBsonSha256(await flowFamilies.Find(flowFamilyFilter).SingleAsync(ct)),
                "P4 rollback did not process version phase before family phase");
            HarnessAssert.Equal(
                "P4_ROLLBACK_DRIFT_PROBE",
                (await flowVersions.Find(flowVersionFilter).SingleAsync(ct))["migrationState"].AsString,
                "P4 rollback overwrote concurrent version drift");
            HarnessAssert.True(
                (await flowManifests.Find(flowManifestFilter).ToListAsync(ct)).All(manifest =>
                    manifest["state"].AsString ==
                    DynamicFlowDefinitionMetadataBackfill.ManifestApplied),
                "Failed rollback left a partially advanced manifest state");
            await flowVersions.UpdateOneAsync(
                flowVersionFilter,
                Builders<BsonDocument>.Update.Set("migrationState", originalMigrationState),
                cancellationToken: ct);

            await DynamicFlowDefinitionMetadataBackfill.RollbackAsync(
                _database,
                migrationOptions,
                ct);
            HarnessAssert.Equal(
                _legacyProvenanceFixture.FlowFamilyBeforeSha256,
                ComputeBsonSha256(await flowFamilies.Find(flowFamilyFilter).SingleAsync(ct)),
                "P4 Flow family rollback did not restore the exact before document");
            HarnessAssert.Equal(
                _legacyProvenanceFixture.FlowVersionBeforeSha256,
                ComputeBsonSha256(await flowVersions.Find(flowVersionFilter).SingleAsync(ct)),
                "P4 Flow version rollback did not restore the exact before document");
            HarnessAssert.True(
                (await flowManifests.Find(flowManifestFilter).ToListAsync(ct)).All(manifest =>
                    manifest["state"].AsString ==
                    DynamicFlowDefinitionMetadataBackfill.ManifestRolledBack),
                "P4 rollback did not atomically mark both manifests ROLLED_BACK");

            await DynamicFlowDefinitionMetadataBackfill.RunAsync(
                _database,
                migrationOptions,
                ct);
            HarnessAssert.Equal(
                migratedFamilySha,
                ComputeBsonSha256(await flowFamilies.Find(flowFamilyFilter).SingleAsync(ct)),
                "P4 Flow family did not reproduce exact after bytes following rollback");
            HarnessAssert.Equal(
                migratedVersionSha,
                ComputeBsonSha256(await flowVersions.Find(flowVersionFilter).SingleAsync(ct)),
                "P4 Flow version did not reproduce exact after bytes following rollback");
            HarnessAssert.True(
                (await flowManifests.Find(flowManifestFilter).ToListAsync(ct)).All(manifest =>
                    manifest["state"].AsString ==
                    DynamicFlowDefinitionMetadataBackfill.ManifestApplied),
                "P4 migration resume did not return rolled-back manifests to APPLIED");
        }
        finally
        {
            await flowVersions.DeleteOneAsync(flowVersionFilter, cancellationToken: ct);
            await flowFamilies.DeleteOneAsync(flowFamilyFilter, cancellationToken: ct);
            await flowManifests.DeleteManyAsync(flowManifestFilter, cancellationToken: ct);
            await runtime.DeleteOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", runtimeObjectId),
                cancellationToken: ct);
            await forms.DeleteOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", formObjectId),
                cancellationToken: ct);
        }

        HarnessAssert.Equal(
            0L,
            await runtime.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Eq("_id", runtimeObjectId),
                cancellationToken: ct),
            "Legacy runtime migration fixture cleanup failed");
        HarnessAssert.Equal(
            0L,
            await forms.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Eq("_id", formObjectId),
                cancellationToken: ct),
            "Legacy form migration fixture cleanup failed");
        HarnessAssert.Equal(
            0L,
            await flowFamilies.CountDocumentsAsync(flowFamilyFilter, cancellationToken: ct),
            "Legacy Flow family migration fixture cleanup failed");
        HarnessAssert.Equal(
            0L,
            await flowVersions.CountDocumentsAsync(flowVersionFilter, cancellationToken: ct),
            "Legacy Flow version migration fixture cleanup failed");
        HarnessAssert.Equal(
            0L,
            await flowManifests.CountDocumentsAsync(flowManifestFilter, cancellationToken: ct),
            "Legacy Flow rollback manifest cleanup failed");
        return new CaseObservation(
            "backend startup migrated real legacy Form/runtime and Flow Mongo documents; P4 preserved immutable bytes, committed phase 1/2 manifests, reran idempotently, refused version drift before family rollback, restored exact before bytes, and resumed exact after bytes",
            "startupBackfill=true;formFamilyExact=true;runtimeVersion=1;flowBeforeAfter=true;flowSecondRun=0W;flowManifestState=APPLIED;flowRollbackDrift=refused;flowRollbackOrder=2,1;flowRollbackExact=true;flowManifestStateAfterRollback=ROLLED_BACK;flowReapplyExact=true;fixtureCleanup=true");
    }

    private async Task<CaseObservation> RejectStartupBackfillLiveSchemaDriftAsync(CancellationToken ct)
    {
        var sourceId = HarnessAssert.Required(_formId, "P1-BE-006 published Dynamic Form");
        var typedForms = _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates");
        var rawForms = _database.GetCollection<BsonDocument>("dynamic_form_templates");
        var fixtureId = ObjectId.GenerateNewId();
        var fixtureFilter = Builders<BsonDocument>.Filter.Eq("_id", fixtureId);
        try
        {
            var source = await rawForms
                .Find(Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(sourceId)))
                .SingleAsync(ct);
            HarnessAssert.True(
                source.TryGetValue("publishedSchemaSnapshotJson", out var snapshotValue) &&
                snapshotValue.IsString &&
                !string.IsNullOrWhiteSpace(snapshotValue.AsString),
                "Startup integrity source snapshot is missing");
            HarnessAssert.True(
                source.TryGetValue("publishedSchemaHash", out var hashValue) &&
                hashValue.IsString &&
                ComputeSha256(snapshotValue.AsString) == hashValue.AsString,
                "Startup integrity source snapshot/hash pair is invalid");

            var fixture = source.DeepClone().AsBsonDocument;
            fixture["_id"] = fixtureId;
            fixture["code"] = "P2_IT_STARTUP_LIVE_SCHEMA_DRIFT";
            fixture["familyId"] = fixtureId;
            fixture["versionNo"] = 1;
            fixture["revision"] = 1;
            fixture["lineageStatus"] = DynamicFormLineageStatuses.Root;
            fixture["previousVersionId"] = BsonNull.Value;
            fixture["clonedFromVersionId"] = BsonNull.Value;
            fixture["createdAtUtc"] = new BsonDateTime(new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            fixture["updatedAtUtc"] = fixture["createdAtUtc"];
            var driftedFields = JsonNode.Parse(fixture["fieldsJson"].AsString)?.AsArray()
                ?? throw new InvalidOperationException("Startup integrity fixture fields must be an array");
            var firstField = driftedFields[0]?.AsObject()
                ?? throw new InvalidOperationException("Startup integrity fixture needs one field");
            firstField["name"] = "Out-of-band live structural drift";
            fixture["fieldsJson"] = driftedFields.ToJsonString();
            await rawForms.InsertOneAsync(fixture, cancellationToken: ct);

            var before = await LoadRawFormCollectionSnapshotAsync(ct);
            var backfillType = typeof(DynamicFormTemplate).Assembly.GetType(
                "tdtd_be.Data.Indexes.DynamicFormVersionMetadataBackfill",
                throwOnError: true)
                ?? throw new InvalidOperationException("Dynamic Form metadata backfill type was not found");
            var runMethod = backfillType.GetMethod(
                "RunAsync",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Dynamic Form metadata backfill RunAsync was not found");

            Exception? observed = null;
            try
            {
                var task = runMethod.Invoke(null, new object[] { typedForms, ct }) as Task
                    ?? throw new InvalidOperationException("Dynamic Form metadata backfill did not return a Task");
                await task;
            }
            catch (Exception ex)
            {
                observed = ex is System.Reflection.TargetInvocationException { InnerException: not null } invocation
                    ? invocation.InnerException
                    : ex;
            }

            HarnessAssert.True(
                observed is InvalidOperationException,
                $"Startup backfill accepted live structural drift; observed={observed?.GetType().Name ?? "none"}");
            HarnessAssert.True(
                observed!.Message.Contains("live structure is invalid", StringComparison.OrdinalIgnoreCase),
                $"Startup backfill failure did not identify live-structure integrity: {observed.Message}");
            AssertRawFormCollectionSnapshotUnchanged(
                before,
                await LoadRawFormCollectionSnapshotAsync(ct),
                "Startup backfill live-schema integrity failure");
        }
        finally
        {
            await rawForms.DeleteOneAsync(fixtureFilter, cancellationToken: ct);
        }

        HarnessAssert.Equal(
            0L,
            await rawForms.CountDocumentsAsync(fixtureFilter, cancellationToken: ct),
            "Startup integrity fixture cleanup failed");
        return new CaseObservation(
            "the startup metadata backfill rejected a valid immutable snapshot/hash pair whose live field structure drifted, before any Dynamic Form write",
            "directRunAsync=true;validPair=true;liveNameDrift=true;throws=InvalidOperationException;formWrites=0;fixtureCleanup=true");
    }

    private async Task<CaseObservation> VerifyMongoPrimaryAsync(CancellationToken ct)
    {
        var hello = await _mongo.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
            new BsonDocument("hello", 1),
            cancellationToken: ct);
        HarnessAssert.True(hello.GetValue("isWritablePrimary", false).ToBoolean(), "Mongo is not writable PRIMARY");
        HarnessAssert.Equal(
            _mongo.ReplicaSetName,
            hello.GetValue("setName", string.Empty).AsString,
            "Mongo replica-set name mismatch");
        // Kestrel initializes indexes before the scenario starts, which can create
        // the database itself. Cleanliness therefore means no scenario/business
        // documents rather than an absent database name.
        var initialBusinessDocuments =
            await _database.GetCollection<AppUser>("users").CountDocumentsAsync(FilterDefinition<AppUser>.Empty, cancellationToken: ct) +
            await _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates").CountDocumentsAsync(FilterDefinition<DynamicFormTemplate>.Empty, cancellationToken: ct) +
            await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates").CountDocumentsAsync(FilterDefinition<DynamicFlowTemplate>.Empty, cancellationToken: ct) +
            await _database.GetCollection<WorkAssignment>("work_assignments").CountDocumentsAsync(FilterDefinition<WorkAssignment>.Empty, cancellationToken: ct);
        HarnessAssert.Equal(0L, initialBusinessDocuments, "Iteration business collections were not clean before scenario writes");
        return new CaseObservation(
            $"PRIMARY set={_mongo.ReplicaSetName}; port={_mongo.Port}",
            "primary=true;businessDocumentsInitially=0");
    }

    private async Task<CaseObservation> BootstrapAndLoginAdminAsync(CancellationToken ct)
    {
        var bootstrap = await _api.PostAsync(
            "api/system/bootstrap",
            new { },
            headers: new Dictionary<string, string> { ["X-System-Bootstrap-Key"] = _backend.BootstrapKey },
            ct: ct);
        ApiHarnessClient.ExpectStatus(bootstrap, HttpStatusCode.OK, "system bootstrap");
        var bootstrapBody = ApiHarnessClient.RequiredObject(bootstrap.Json, "system bootstrap response");
        var bootstrapPassword = ApiHarnessClient.RequiredString(bootstrapBody, "defaultPassword");
        _adminPassword = bootstrapPassword;

        var users = _database.GetCollection<AppUser>("users");
        var units = _database.GetCollection<Unit>("units");
        var admin = await users.Find(x => x.Username == "admin" && !x.IsDeleted).SingleAsync(ct);
        var rootCount = await units.CountDocumentsAsync(x => x.ParentUnitId == null && x.Code == "100", cancellationToken: ct);
        HarnessAssert.Equal(1L, rootCount, "Bootstrap must create exactly one root unit");
        HarnessAssert.True(admin.Roles.Contains("SYSTEM_ADMIN"), "Bootstrap admin lacks SYSTEM_ADMIN role");

        _adminToken = await _api.LoginAsync("admin", bootstrapPassword, ct);
        _adminId = admin.Id;
        HarnessAssert.True(_adminToken.Length > 40, "Admin access token is unexpectedly short");
        return new CaseObservation(
            $"bootstrap admin={admin.Username}; rootCount={rootCount}",
            "adminLogin=true;rootCount=1");
    }

    private async Task<CaseObservation> SeedAndLoginActorsAsync(CancellationToken ct)
    {
        var adminId = HarnessAssert.Required(_adminId, "P1-BE-002 admin");
        _ = HarnessAssert.Required(_adminToken, "P1-BE-002 admin token");
        var users = _database.GetCollection<AppUser>("users");
        var units = _database.GetCollection<Unit>("units");
        var unitTypes = _database.GetCollection<UnitType>("unit_types");
        var root = await units.Find(x => x.ParentUnitId == null && x.Code == "100").SingleAsync(ct);
        var fixedAt = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);

        await unitTypes.InsertOneAsync(
            new UnitType
            {
                Id = _unitTypeId,
                Code = "P1_TEST_UNIT",
                Name = "P1 Integration Unit",
                Version = 1,
                CreatedByUserId = adminId,
                UpdatedByUserId = adminId,
                CreatedAtUtc = fixedAt,
                UpdatedAtUtc = fixedAt,
                IsDeleted = false
            },
            cancellationToken: ct);
        await units.InsertOneAsync(
            new Unit
            {
                Id = _unitId,
                FullName = "P1 Integration Unit",
                ShortName = "P1 IT",
                Symbol = "P1IT",
                Code = "100001",
                Level = 1,
                Version = 1,
                UnitTypeCodes = ["P1_TEST_UNIT"],
                PrimaryUnitTypeCode = "P1_TEST_UNIT",
                ParentUnitId = root.Id,
                CreatedByUserId = adminId,
                UpdatedByUserId = adminId,
                CreatedAtUtc = fixedAt,
                UpdatedAtUtc = fixedAt,
                IsDeleted = false
            },
            cancellationToken: ct);

        var hasher = new PasswordHasher<AppUser>();
        var owner = NewActor(_ownerId, OwnerUsername, "P1 Owner", fixedAt, adminId, _unitId);
        owner.Roles = ["DYNAMIC_FLOW_MANAGER"];
        owner.PasswordHash = hasher.HashPassword(owner, _backend.ActorPassword);
        var outsider = NewActor(_outsiderId, OutsiderUsername, "P1 Outsider", fixedAt, adminId, _unitId);
        outsider.PasswordHash = hasher.HashPassword(outsider, _backend.ActorPassword);
        var assignee = NewActor(_assigneeId, AssigneeUsername, "P2 Assignee", fixedAt, adminId, _unitId);
        assignee.PasswordHash = hasher.HashPassword(assignee, _backend.ActorPassword);
        var reviewer = NewActor(_reviewerId, ReviewerUsername, "P2 Reviewer", fixedAt, adminId, _unitId);
        reviewer.PasswordHash = hasher.HashPassword(reviewer, _backend.ActorPassword);
        await users.InsertManyAsync([owner, outsider, assignee, reviewer], cancellationToken: ct);

        await _database.GetCollection<DynamicExcelTemplate>("dynamic_excel_templates")
            .InsertOneAsync(
                new DynamicExcelTemplate
                {
                    Id = _dynamicExcelId,
                    Code = "P2_IT_EXCEL",
                    Name = "P2 CAS import fixture",
                    TableMode = "FIXED_GRID",
                    ContractVersion = 1,
                    CreatedByUsername = OwnerUsername,
                    RawWorkbookDataJson = "[]",
                    SpecJson = "{\"kind\":\"MATRIX\",\"defaultDataType\":\"NUMBER\"}",
                    DataRectR0 = 0,
                    DataRectC0 = 0,
                    DataRectR1 = 0,
                    DataRectC1 = 0,
                    W = 1,
                    H = 1,
                    CreatedByUserId = _ownerId,
                    UpdatedByUserId = _ownerId,
                    CreatedAtUtc = fixedAt,
                    UpdatedAtUtc = fixedAt,
                    IsDeleted = false
                },
                cancellationToken: ct);

        _ownerToken = await _api.LoginAsync(OwnerUsername, _backend.ActorPassword, ct);
        _outsiderToken = await _api.LoginAsync(OutsiderUsername, _backend.ActorPassword, ct);
        _assigneeToken = await _api.LoginAsync(AssigneeUsername, _backend.ActorPassword, ct);
        _reviewerToken = await _api.LoginAsync(ReviewerUsername, _backend.ActorPassword, ct);
        var actorCount = await users.CountDocumentsAsync(x =>
            (x.Id == _ownerId || x.Id == _outsiderId || x.Id == _assigneeId || x.Id == _reviewerId) &&
            !x.IsDeleted, cancellationToken: ct);
        HarnessAssert.Equal(4L, actorCount, "Runtime actor seed count mismatch");
        var dynamicExcelCount = await _database.GetCollection<DynamicExcelTemplate>("dynamic_excel_templates")
            .CountDocumentsAsync(x => x.Id == _dynamicExcelId && !x.IsDeleted, cancellationToken: ct);
        HarnessAssert.Equal(1L, dynamicExcelCount, "Dynamic Excel CAS fixture seed mismatch");
        return new CaseObservation(
            "four isolated actors and one Dynamic Excel fixture seeded; actors authenticated",
            "actors=4;dynamicExcelFixtures=1;ownerLogin=true;outsiderLogin=true;assigneeLogin=true;reviewerLogin=true");
    }

    private async Task<CaseObservation> VerifyCatalogEtagAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var first = await _api.GetAsync("api/capabilities/dynamic-form-flow", token, ct: ct);
        ApiHarnessClient.ExpectStatus(first, HttpStatusCode.OK, "capability catalog first read");
        var catalog = ApiHarnessClient.RequiredObject(first.Json, "capability catalog");
        CatalogVersion = ApiHarnessClient.RequiredString(catalog, "catalogVersion");
        CatalogSha256 = ApiHarnessClient.RequiredString(catalog, "catalogSha256");
        var etag = first.Header("ETag")
            ?? throw new InvalidOperationException("Capability response is missing ETag.");
        var domains = ApiHarnessClient.RequiredObject(catalog["domains"], "capability domains");
        var fieldTypes = ApiHarnessClient.RequiredArray(domains["dynamicFormFieldTypes"], "dynamicFormFieldTypes");
        var valueSources = ApiHarnessClient.RequiredArray(domains["dynamicFormValueSources"], "dynamicFormValueSources");
        HarnessAssert.Equal(10, fieldTypes.Count, "Capability field type count mismatch");
        HarnessAssert.Equal(6, valueSources.Count, "Capability value source count mismatch");
        _fieldTypes.Clear();
        _fieldTypes.AddRange(ReadCapabilityIds(fieldTypes, "dynamicFormFieldTypes"));
        _valueSources.Clear();
        _valueSources.AddRange(ReadCapabilityIds(valueSources, "dynamicFormValueSources"));
        HarnessAssert.Equal(10, _fieldTypes.Distinct(StringComparer.Ordinal).Count(), "Capability field type IDs must be unique");
        HarnessAssert.Equal(6, _valueSources.Distinct(StringComparer.Ordinal).Count(), "Capability value source IDs must be unique");

        var second = await _api.GetAsync(
            "api/capabilities/dynamic-form-flow",
            token,
            new Dictionary<string, string> { ["If-None-Match"] = etag },
            ct);
        ApiHarnessClient.ExpectStatus(second, HttpStatusCode.NotModified, "capability ETag read");
        HarnessAssert.True(string.IsNullOrEmpty(second.Body), "304 response must not contain a body");
        return new CaseObservation(
            $"catalog={CatalogVersion}; sha256={CatalogSha256}; 10 field types/6 sources; ETag revalidated",
            $"catalog={CatalogVersion};fieldTypes=10;valueSources=6;etag304=true");
    }

    private async Task<CaseObservation> CreateAndGetTypedFormAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var adminToken = HarnessAssert.Required(_adminToken, "P1-BE-002 admin token");
        HarnessAssert.True(_fieldTypes.Count > 0 && _valueSources.Count > 0, "Capability-driven seed matrix is unavailable");
        var enumCatalog = await _api.PostAsync(
            "api/label-enum-catalogs",
            new
            {
                code = "P1_IT_ENUM",
                name = "P1 integration enum",
                description = "Capability-driven Dynamic Form seed",
                options = new[] { new { code = "CAT_A", label = "Catalog A", order = 1, isActive = true } },
                scopeType = "GLOBAL",
                scopeId = (string?)null,
                isActive = true
            },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(enumCatalog, HttpStatusCode.OK, "enum catalog seed");
        var generatedCatalogId = ApiHarnessClient.RequiredString(enumCatalog.Json, "id");
        var catalogs = _database.GetCollection<LabelEnumCatalog>("label_enum_catalogs");
        var createdCatalog = await catalogs
            .FindOneAndDeleteAsync(x => x.Id == generatedCatalogId && !x.IsDeleted, cancellationToken: ct)
            ?? throw new InvalidOperationException("Created enum catalog disappeared before deterministic re-key.");
        createdCatalog.Id = StableEnumCatalogId;
        await catalogs.InsertOneAsync(createdCatalog, cancellationToken: ct);
        await _database.GetCollection<LabelEnumOptionReadModel>("label_enum_option_read_models")
            .UpdateManyAsync(
                x => x.CatalogId == generatedCatalogId && !x.IsDeleted,
                Builders<LabelEnumOptionReadModel>.Update.Set(x => x.CatalogId, StableEnumCatalogId),
                cancellationToken: ct);
        _enumCatalogId = StableEnumCatalogId;

        var request = BuildFormRequest(FormCode, "P1 typed form", updated: false);
        var created = await _api.PostAsync("api/dynamic-forms", request, token, ct: ct);
        ApiHarnessClient.ExpectStatus(created, HttpStatusCode.OK, "typed Dynamic Form create");
        var createdObject = ApiHarnessClient.RequiredObject(created.Json, "created Dynamic Form");
        _formId = ApiHarnessClient.RequiredString(createdObject, "id");
        _formRevision = ApiHarnessClient.RequiredInt(createdObject, "revision");
        HarnessAssert.Equal(1, _formRevision.Value, "New Dynamic Form revision mismatch");
        HarnessAssert.Equal(FormCode, ApiHarnessClient.RequiredString(createdObject, "code"), "Dynamic Form code mismatch");
        var createdSchema = ApiHarnessClient.RequiredObject(createdObject["schema"], "created Dynamic Form schema");
        var createdFields = ApiHarnessClient.RequiredArray(createdSchema["fields"], "created Dynamic Form fields");
        AssertCapabilityDrivenFields(createdFields);

        var reopened = await _api.GetAsync($"api/dynamic-forms/{_formId}", token, ct: ct);
        ApiHarnessClient.ExpectStatus(reopened, HttpStatusCode.OK, "typed Dynamic Form reopen");
        var reopenedObject = ApiHarnessClient.RequiredObject(reopened.Json, "reopened Dynamic Form");
        HarnessAssert.Equal(_formRevision.Value, ApiHarnessClient.RequiredInt(reopenedObject, "revision"), "Reopened Dynamic Form revision mismatch");
        var reopenedSchema = ApiHarnessClient.RequiredObject(reopenedObject["schema"], "reopened Dynamic Form schema");
        AssertCapabilityDrivenFields(ApiHarnessClient.RequiredArray(reopenedSchema["fields"], "reopened fields"));
        HarnessAssert.True(reopenedObject["fieldsJson"] is JsonValue, "Legacy fieldsJson companion is missing");
        return new CaseObservation(
            $"created/reopened form {_formId} from catalog matrix (10 types/6 sources); enum fixture re-keyed to stable isolated ID",
            $"typedFields={createdFields.Count};distinctTypes=10;distinctSources=6;stableCatalogId=true;legacyCompanion=true;reopen=true");
    }

    private async Task<CaseObservation> UpdateAndPublishTypedFormAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var formId = HarnessAssert.Required(_formId, "P1-BE-005 form");
        var expectedRevision = _formRevision
            ?? throw new HarnessCaseNotRunnableException("Missing prerequisite: P1-BE-005 form revision.");
        var updatedRequest = BuildFormRequest(FormCode, "P1 typed form updated", updated: true);
        updatedRequest.Remove("code");
        updatedRequest["expectedRevision"] = expectedRevision;
        var updated = await _api.PutAsync($"api/dynamic-forms/{formId}", updatedRequest, token, ct: ct);
        ApiHarnessClient.ExpectStatus(updated, HttpStatusCode.OK, "typed Dynamic Form update");
        var updatedObject = ApiHarnessClient.RequiredObject(updated.Json, "updated Dynamic Form");
        _formRevision = ApiHarnessClient.RequiredInt(updatedObject, "revision");
        HarnessAssert.Equal(expectedRevision + 1, _formRevision.Value, "Dynamic Form update revision mismatch");
        HarnessAssert.Equal(
            "P1 typed form updated",
            ApiHarnessClient.RequiredString(updatedObject, "name"),
            "Dynamic Form update was not persisted");

        var published = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/publish",
            new { expectedRevision = _formRevision.Value },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(published, HttpStatusCode.OK, "Dynamic Form publish");
        var publishedObject = ApiHarnessClient.RequiredObject(published.Json, "published Dynamic Form");
        _formRevision = ApiHarnessClient.RequiredInt(publishedObject, "revision");
        HarnessAssert.Equal(expectedRevision + 2, _formRevision.Value, "Dynamic Form publish revision mismatch");
        HarnessAssert.True(publishedObject["isPublished"]?.GetValue<bool>() == true, "Dynamic Form is not published");

        var persisted = await _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
            .Find(x => x.Id == formId && !x.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.True(persisted.IsPublished, "Published state is missing in Mongo");
        HarnessAssert.Equal(_formRevision.Value, persisted.Revision, "Published revision is missing in Mongo");
        HarnessAssert.Equal("P1 typed form updated", persisted.Name, "Updated form name is missing in Mongo");
        return new CaseObservation(
            "typed draft updated and published; Mongo source verified",
            "updated=true;published=true;revision=3;mongoVerified=true");
    }

    private async Task<CaseObservation> RejectMixedTypedLegacyFormAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var request = BuildFormRequest(AmbiguousFormCode, "P1 ambiguous form", updated: false);
        request["sectionsJson"] = "[]";
        var response = await _api.PostAsync("api/dynamic-forms", request, token, ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.BadRequest, "mixed typed/legacy Dynamic Form create");
        var reason = ApiHarnessClient.FindStringRecursive(response.Json, "reason");
        HarnessAssert.Equal("DYNAMIC_FORM_SCHEMA_AMBIGUOUS", reason, "Ambiguous Dynamic Form reason mismatch");
        var count = await _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
            .CountDocumentsAsync(x => x.Code == AmbiguousFormCode && !x.IsDeleted, cancellationToken: ct);
        HarnessAssert.Equal(0L, count, "Ambiguous Dynamic Form request wrote data");
        return new CaseObservation(
            "mixed typed/legacy request rejected before Mongo write",
            "http=400;reason=DYNAMIC_FORM_SCHEMA_AMBIGUOUS;writes=0");
    }

    private async Task<CaseObservation> RejectMissingExpectedRevisionAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var created = await CreateDraftFormAsync(MissingRevisionFormCode, "P2 missing revision form", token, ct);
        var formId = ApiHarnessClient.RequiredString(created, "id");
        var before = await LoadFormDocumentAsync(formId, ct);

        var response = await _api.PutAsync(
            $"api/dynamic-forms/{formId}",
            BuildFormUpdateRequest(MissingRevisionFormCode, "must not be written", expectedRevision: null),
            token,
            ct: ct);

        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.BadRequest, "Dynamic Form update without expectedRevision");
        AssertErrorCode(response, "DYNAMIC_FORM_REVISION_REQUIRED", "missing expectedRevision");
        var after = await LoadFormDocumentAsync(formId, ct);
        AssertFormDocumentUnchanged(before, after, "Missing expectedRevision request");
        return new CaseObservation(
            "PUT without expectedRevision returned 400 and left the draft unchanged in Mongo",
            "http=400;reason=DYNAMIC_FORM_REVISION_REQUIRED;revision=1;writes=0");
    }

    private async Task<CaseObservation> RejectStaleUpdateAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var created = await CreateDraftFormAsync(StaleUpdateFormCode, "P2 stale update form", token, ct);
        var formId = ApiHarnessClient.RequiredString(created, "id");
        var initialRevision = ApiHarnessClient.RequiredInt(created, "revision");

        var winner = await _api.PutAsync(
            $"api/dynamic-forms/{formId}",
            BuildFormUpdateRequest(StaleUpdateFormCode, "P2 accepted update", initialRevision),
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(winner, HttpStatusCode.OK, "fresh Dynamic Form update");
        HarnessAssert.Equal(
            initialRevision + 1,
            ApiHarnessClient.RequiredInt(winner.Json, "revision"),
            "Fresh Dynamic Form update did not advance revision");

        var beforeStale = await LoadFormDocumentAsync(formId, ct);
        var stale = await _api.PutAsync(
            $"api/dynamic-forms/{formId}",
            BuildFormUpdateRequest(StaleUpdateFormCode, "P2 stale update must not win", initialRevision),
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(stale, HttpStatusCode.Conflict, "stale Dynamic Form update");
        AssertRevisionConflict(stale, initialRevision, initialRevision + 1, "stale Dynamic Form update");

        var afterStale = await LoadFormDocumentAsync(formId, ct);
        AssertFormDocumentUnchanged(beforeStale, afterStale, "Stale Dynamic Form update");
        HarnessAssert.Equal("P2 accepted update", afterStale.Name, "Stale update replaced the accepted value");
        return new CaseObservation(
            "stale PUT returned stable 409 conflict metadata and made no Mongo write",
            "http=409;reason=DYNAMIC_FORM_REVISION_CONFLICT;expected=1;current=2;writes=0");
    }

    private async Task<CaseObservation> ResolveConcurrentUpdatesAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var created = await CreateDraftFormAsync(ConcurrentUpdateFormCode, "P2 concurrent update form", token, ct);
        var formId = ApiHarnessClient.RequiredString(created, "id");
        var initialRevision = ApiHarnessClient.RequiredInt(created, "revision");
        const string firstName = "P2 concurrent candidate A";
        const string secondName = "P2 concurrent candidate B";

        var firstTask = _api.PutAsync(
            $"api/dynamic-forms/{formId}",
            BuildFormUpdateRequest(ConcurrentUpdateFormCode, firstName, initialRevision),
            token,
            ct: ct);
        var secondTask = _api.PutAsync(
            $"api/dynamic-forms/{formId}",
            BuildFormUpdateRequest(ConcurrentUpdateFormCode, secondName, initialRevision),
            token,
            ct: ct);
        await Task.WhenAll(firstTask, secondTask);

        var responses = new[] { await firstTask, await secondTask };
        var successes = responses.Where(x => x.StatusCode == HttpStatusCode.OK).ToArray();
        var conflicts = responses.Where(x => x.StatusCode == HttpStatusCode.Conflict).ToArray();
        HarnessAssert.Equal(1, successes.Length, "Concurrent PUT must have exactly one successful writer");
        HarnessAssert.Equal(1, conflicts.Length, "Concurrent PUT must have exactly one revision conflict");
        AssertRevisionConflict(conflicts[0], initialRevision, initialRevision + 1, "concurrent Dynamic Form loser");

        var winnerBody = ApiHarnessClient.RequiredObject(successes[0].Json, "concurrent Dynamic Form winner");
        var winnerName = ApiHarnessClient.RequiredString(winnerBody, "name");
        HarnessAssert.True(
            winnerName is firstName or secondName,
            "Concurrent PUT returned an unexpected winning value");
        HarnessAssert.Equal(
            initialRevision + 1,
            ApiHarnessClient.RequiredInt(winnerBody, "revision"),
            "Concurrent winner revision mismatch");

        var persisted = await LoadFormDocumentAsync(formId, ct);
        HarnessAssert.Equal(initialRevision + 1, persisted.Revision, "Concurrent PUT advanced revision more than once");
        HarnessAssert.Equal(winnerName, persisted.Name, "Concurrent PUT response does not match Mongo winner");
        var formCount = await _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
            .CountDocumentsAsync(x => x.Id == formId && !x.IsDeleted, cancellationToken: ct);
        HarnessAssert.Equal(1L, formCount, "Concurrent PUT changed Dynamic Form document cardinality");
        return new CaseObservation(
            $"two same-revision PUTs produced one winner ({winnerName[^1]}) and one 409; Mongo revision advanced once",
            "success=1;conflict=1;revision=2;winnerPersisted=true;documents=1");
    }

    private async Task<CaseObservation> RejectStaleImportAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var created = await CreateDraftFormAsync(StaleImportFormCode, "P2 stale import form", token, ct);
        var formId = ApiHarnessClient.RequiredString(created, "id");
        var initialRevision = ApiHarnessClient.RequiredInt(created, "revision");

        var update = await _api.PutAsync(
            $"api/dynamic-forms/{formId}",
            BuildFormUpdateRequest(StaleImportFormCode, "P2 import revision winner", initialRevision),
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(update, HttpStatusCode.OK, "fresh update before stale import");
        var currentRevision = ApiHarnessClient.RequiredInt(update.Json, "revision");
        var beforeStaleImport = await LoadFormDocumentAsync(formId, ct);

        var staleImport = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/blocks/import-dynamic-excel",
            new
            {
                dynamicExcelTemplateId = _dynamicExcelId,
                sectionId = "main",
                expectedRevision = initialRevision
            },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(staleImport, HttpStatusCode.Conflict, "stale Dynamic Excel block import");
        AssertRevisionConflict(staleImport, initialRevision, currentRevision, "stale Dynamic Excel block import");

        var afterStaleImport = await LoadFormDocumentAsync(formId, ct);
        AssertFormDocumentUnchanged(beforeStaleImport, afterStaleImport, "Stale Dynamic Excel block import");
        HarnessAssert.Equal("[]", afterStaleImport.BlocksJson, "Stale import appended a Dynamic Excel block");
        return new CaseObservation(
            "stale Dynamic Excel import returned 409 before appending a block; Mongo source stayed unchanged",
            "http=409;reason=DYNAMIC_FORM_REVISION_CONFLICT;revision=2;blocks=0;writes=0");
    }

    private async Task<CaseObservation> ProtectPublishedFormFromStaleUpdateAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var created = await CreateDraftFormAsync(PublishStaleFormCode, "P2 publish stale form", token, ct);
        var formId = ApiHarnessClient.RequiredString(created, "id");
        var draftRevision = ApiHarnessClient.RequiredInt(created, "revision");

        var published = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/publish",
            new { expectedRevision = draftRevision },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(published, HttpStatusCode.OK, "publish before stale update");
        var publishedRevision = ApiHarnessClient.RequiredInt(published.Json, "revision");
        HarnessAssert.Equal(draftRevision + 1, publishedRevision, "Publish revision mismatch");
        var beforeStaleUpdate = await LoadFormDocumentAsync(formId, ct);
        HarnessAssert.True(beforeStaleUpdate.IsPublished, "Publish did not persist before stale update");

        var staleUpdate = await _api.PutAsync(
            $"api/dynamic-forms/{formId}",
            BuildFormUpdateRequest(PublishStaleFormCode, "must not mutate published form", draftRevision),
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(staleUpdate, HttpStatusCode.Conflict, "stale update after publish");
        AssertRevisionConflict(staleUpdate, draftRevision, publishedRevision, "stale update after publish");

        var afterStaleUpdate = await LoadFormDocumentAsync(formId, ct);
        AssertFormDocumentUnchanged(beforeStaleUpdate, afterStaleUpdate, "Stale update after publish");
        HarnessAssert.True(afterStaleUpdate.IsPublished, "Stale update changed published state");
        HarnessAssert.Equal("P2 publish stale form", afterStaleUpdate.Name, "Stale update mutated published payload");
        return new CaseObservation(
            "publish advanced the revision; a stale PUT was rejected and could not mutate the published Mongo document",
            "publishRevision=2;staleHttp=409;reason=DYNAMIC_FORM_REVISION_CONFLICT;published=true;writes=0");
    }

    private async Task<CaseObservation> RejectDuplicateCodeAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        _ = await CreateDraftFormAsync(DuplicateFormCode, "P2 duplicate code winner", token, ct);

        var duplicate = await _api.PostAsync(
            "api/dynamic-forms",
            BuildFormRequest(DuplicateFormCode, "P2 duplicate code loser", updated: false),
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(duplicate, HttpStatusCode.Conflict, "duplicate Dynamic Form code");
        AssertErrorCode(duplicate, "DYNAMIC_FORM_CODE_CONFLICT", "duplicate Dynamic Form code");
        var count = await _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
            .CountDocumentsAsync(x => x.Code == DuplicateFormCode && !x.IsDeleted, cancellationToken: ct);
        HarnessAssert.Equal(1L, count, "Duplicate Dynamic Form code wrote a second document");
        return new CaseObservation(
            "duplicate code returned stable 409 reason and retained exactly one Mongo document",
            "http=409;reason=DYNAMIC_FORM_CODE_CONFLICT;documents=1");
    }

    private async Task<CaseObservation> VerifyIdempotentPublishAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var created = await CreateDraftFormAsync(IdempotentPublishFormCode, "P2 idempotent publish form", token, ct);
        var formId = ApiHarnessClient.RequiredString(created, "id");
        var draftRevision = ApiHarnessClient.RequiredInt(created, "revision");

        var beforeMissingRevision = await LoadFormDocumentAsync(formId, ct);
        var missingRevision = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/publish",
            new { },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(missingRevision, HttpStatusCode.BadRequest, "draft publish without expectedRevision");
        AssertErrorCode(missingRevision, "DYNAMIC_FORM_REVISION_REQUIRED", "draft publish without expectedRevision");
        AssertFormDocumentUnchanged(
            beforeMissingRevision,
            await LoadFormDocumentAsync(formId, ct),
            "Draft publish without expectedRevision");

        var advanced = await _api.PutAsync(
            $"api/dynamic-forms/{formId}",
            BuildFormUpdateRequest(IdempotentPublishFormCode, "P2 idempotent publish form updated", draftRevision),
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(advanced, HttpStatusCode.OK, "advance draft before stale publish");
        var currentRevision = ApiHarnessClient.RequiredInt(advanced.Json, "revision");
        HarnessAssert.Equal(draftRevision + 1, currentRevision, "Pre-publish update revision mismatch");

        var beforeStalePublish = await LoadFormDocumentAsync(formId, ct);
        var stale = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/publish",
            new { expectedRevision = draftRevision },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(stale, HttpStatusCode.Conflict, "stale draft publish");
        AssertRevisionConflict(stale, draftRevision, currentRevision, "stale draft publish");
        AssertFormDocumentUnchanged(
            beforeStalePublish,
            await LoadFormDocumentAsync(formId, ct),
            "Stale draft publish");

        var first = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/publish",
            new { expectedRevision = currentRevision },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(first, HttpStatusCode.OK, "first Dynamic Form publish");
        var firstBody = ApiHarnessClient.RequiredObject(first.Json, "first Dynamic Form publish");
        var publishedRevision = ApiHarnessClient.RequiredInt(firstBody, "revision");
        HarnessAssert.Equal(currentRevision + 1, publishedRevision, "First publish revision mismatch");
        HarnessAssert.True(firstBody["isPublished"]?.GetValue<bool>() == true, "First publish did not return published state");
        var afterFirst = await LoadFormDocumentAsync(formId, ct);

        var retry = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/publish",
            new { expectedRevision = draftRevision },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(retry, HttpStatusCode.OK, "idempotent Dynamic Form publish retry");
        var retryBody = ApiHarnessClient.RequiredObject(retry.Json, "idempotent Dynamic Form publish retry");
        HarnessAssert.Equal(publishedRevision, ApiHarnessClient.RequiredInt(retryBody, "revision"), "Publish retry advanced response revision");
        HarnessAssert.True(retryBody["isPublished"]?.GetValue<bool>() == true, "Publish retry lost published state");

        var afterRetry = await LoadFormDocumentAsync(formId, ct);
        AssertFormDocumentUnchanged(afterFirst, afterRetry, "Idempotent publish retry");
        return new CaseObservation(
            "draft publish required a current revision, stale publish was rejected, and a published retry remained idempotent",
            "missing=400;stale=409;first=200;retry=200;revision=3;published=true;retryWrites=0");
    }

    private async Task<CaseObservation> VerifyConcurrentPublishAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var created = await CreateDraftFormAsync(
            ConcurrentPublishFormCode,
            "P2 concurrent publish form",
            token,
            ct);
        var formId = ApiHarnessClient.RequiredString(created, "id");
        var expectedRevision = ApiHarnessClient.RequiredInt(created, "revision");
        var before = await LoadFormDocumentAsync(formId, ct);

        var responses = await Task.WhenAll(
            _api.PostAsync(
                $"api/dynamic-forms/{formId}/publish",
                new { expectedRevision },
                token,
                ct: ct),
            _api.PostAsync(
                $"api/dynamic-forms/{formId}/publish",
                new { expectedRevision },
                token,
                ct: ct));

        for (var index = 0; index < responses.Length; index++)
            ApiHarnessClient.ExpectStatus(responses[index], HttpStatusCode.OK, $"concurrent publish response {index + 1}");

        var first = ApiHarnessClient.RequiredObject(responses[0].Json, "concurrent publish first response");
        var second = ApiHarnessClient.RequiredObject(responses[1].Json, "concurrent publish second response");
        HarnessAssert.Equal(formId, ApiHarnessClient.RequiredString(first, "id"), "Concurrent publish first ID mismatch");
        HarnessAssert.Equal(formId, ApiHarnessClient.RequiredString(second, "id"), "Concurrent publish second ID mismatch");
        HarnessAssert.Equal(expectedRevision + 1, ApiHarnessClient.RequiredInt(first, "revision"), "Concurrent publish first revision mismatch");
        HarnessAssert.Equal(expectedRevision + 1, ApiHarnessClient.RequiredInt(second, "revision"), "Concurrent publish second revision mismatch");
        HarnessAssert.Equal(
            ApiHarnessClient.RequiredString(first, "publishedSchemaHash"),
            ApiHarnessClient.RequiredString(second, "publishedSchemaHash"),
            "Concurrent publish responses returned different schema hashes");
        HarnessAssert.Equal(
            ApiHarnessClient.RequiredString(first, "publishedSchemaSnapshotJson"),
            ApiHarnessClient.RequiredString(second, "publishedSchemaSnapshotJson"),
            "Concurrent publish responses returned different snapshots");

        var after = await LoadFormDocumentAsync(formId, ct);
        var snapshot = RequirePublishedSnapshot(after, "concurrent publish persisted form");
        HarnessAssert.True(after.IsPublished, "Concurrent publish did not persist published state");
        HarnessAssert.True(after.PublishedAtUtc.HasValue, "Concurrent publish timestamp is missing");
        HarnessAssert.Equal(before.Revision + 1, after.Revision, "Concurrent publish performed more than one state transition");
        HarnessAssert.Equal(snapshot.Hash, ApiHarnessClient.RequiredString(first, "publishedSchemaHash"), "Concurrent publish persisted hash mismatch");
        HarnessAssert.Equal(snapshot.Json, ApiHarnessClient.RequiredString(first, "publishedSchemaSnapshotJson"), "Concurrent publish persisted snapshot mismatch");
        var activeCount = await _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
            .CountDocumentsAsync(x => x.Code == ConcurrentPublishFormCode && !x.IsDeleted, cancellationToken: ct);
        HarnessAssert.Equal(1L, activeCount, "Concurrent publish changed form cardinality");
        return new CaseObservation(
            "two simultaneous publish requests with the same expected revision both returned the same canonical published result while Mongo recorded one transition",
            $"requests=2;http200=2;sameId=true;sameRevision=true;sameHash=true;revisionDelta=1;documents={activeCount};hash={snapshot.Hash}");
    }

    private async Task<CaseObservation> RejectMissingAndStaleDeleteAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var created = await CreateDraftFormAsync(StaleDeleteFormCode, "P2 stale delete form", token, ct);
        var formId = ApiHarnessClient.RequiredString(created, "id");
        var initialRevision = ApiHarnessClient.RequiredInt(created, "revision");
        var initial = await LoadFormDocumentAsync(formId, ct);

        var missing = await _api.DeleteAsync($"api/dynamic-forms/{formId}", token, ct: ct);
        ApiHarnessClient.ExpectStatus(missing, HttpStatusCode.BadRequest, "delete without expectedRevision");
        AssertErrorCode(missing, "DYNAMIC_FORM_REVISION_REQUIRED", "delete without expectedRevision");
        AssertFormDocumentUnchanged(initial, await LoadFormDocumentAsync(formId, ct), "Delete without expectedRevision");

        var advanced = await _api.PutAsync(
            $"api/dynamic-forms/{formId}",
            BuildFormUpdateRequest(StaleDeleteFormCode, "P2 stale delete winner", initialRevision),
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(advanced, HttpStatusCode.OK, "advance form before stale delete");
        var currentRevision = ApiHarnessClient.RequiredInt(advanced.Json, "revision");
        var beforeStaleDelete = await LoadFormDocumentAsync(formId, ct);

        var stale = await _api.DeleteAsync(
            $"api/dynamic-forms/{formId}?expectedRevision={initialRevision}",
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(stale, HttpStatusCode.Conflict, "stale Dynamic Form delete");
        AssertRevisionConflict(stale, initialRevision, currentRevision, "stale Dynamic Form delete");
        AssertFormDocumentUnchanged(
            beforeStaleDelete,
            await LoadFormDocumentAsync(formId, ct),
            "Stale Dynamic Form delete");
        return new CaseObservation(
            "DELETE now requires caller revision; missing and stale revisions failed closed without a soft-delete write",
            $"missing=400;missingReason=DYNAMIC_FORM_REVISION_REQUIRED;stale=409;staleReason=DYNAMIC_FORM_REVISION_CONFLICT;currentRevision={currentRevision};writes=0");
    }

    private async Task<CaseObservation> VerifyConcurrentDynamicExcelWrapAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var request = new
        {
            dynamicExcelTemplateId = _dynamicExcelId,
            code = (string?)null,
            name = (string?)null,
            description = (string?)null,
            tagCodes = Array.Empty<string>()
        };
        var forms = _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates");
        var before = await forms.CountDocumentsAsync(
            x => x.ExcelBlockDynamicExcelTemplateId == _dynamicExcelId &&
                 x.CreatedByUserId == _ownerId &&
                 x.LineageStatus == "WRAPPED" &&
                 !x.IsPublished &&
                 !x.IsDeleted,
            cancellationToken: ct);
        HarnessAssert.Equal(0L, before, "Concurrent wrap prerequisite already had an owner draft");

        var responses = await Task.WhenAll(
            _api.PostAsync("api/dynamic-forms/wrap-dynamic-excel", request, token, ct: ct),
            _api.PostAsync("api/dynamic-forms/wrap-dynamic-excel", request, token, ct: ct));
        for (var index = 0; index < responses.Length; index++)
            ApiHarnessClient.ExpectStatus(responses[index], HttpStatusCode.OK, $"concurrent Dynamic Excel wrap {index + 1}");

        var firstId = ApiHarnessClient.RequiredString(responses[0].Json, "id");
        var secondId = ApiHarnessClient.RequiredString(responses[1].Json, "id");
        HarnessAssert.Equal(firstId, secondId, "Concurrent wraps returned different draft IDs");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(responses[0].Json, "revision"), "Concurrent wrap first revision mismatch");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(responses[1].Json, "revision"), "Concurrent wrap second revision mismatch");
        HarnessAssert.True(!ApiHarnessClient.RequiredBool(responses[0].Json, "isPublished"), "Concurrent wrap returned a published form");

        var wrappers = await forms
            .Find(x =>
                x.ExcelBlockDynamicExcelTemplateId == _dynamicExcelId &&
                x.CreatedByUserId == _ownerId &&
                x.LineageStatus == "WRAPPED" &&
                !x.IsPublished &&
                !x.IsDeleted)
            .ToListAsync(ct);
        HarnessAssert.Equal(1, wrappers.Count, "Concurrent wrap persisted more than one owner draft");
        HarnessAssert.Equal(firstId, wrappers[0].Id, "Concurrent wrap persisted ID mismatch");
        HarnessAssert.Equal(1, wrappers[0].Revision, "Concurrent wrap persisted revision mismatch");
        _concurrentWrappedDraftId = firstId;
        return new CaseObservation(
            "two simultaneous wrap requests converged on one reusable owner/Dynamic-Excel draft",
            "requests=2;http200=2;sameId=true;drafts=1;revision=1");
    }

    private async Task<CaseObservation> VerifyWrapReuseReactivationGuardAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var excelId = ObjectId.GenerateNewId().ToString();
        var excelTemplates = _database.GetCollection<DynamicExcelTemplate>("dynamic_excel_templates");
        var forms = _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates");
        var fixedAt = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        await excelTemplates.InsertOneAsync(
            new DynamicExcelTemplate
            {
                Id = excelId,
                Code = "P2_IT_WRAP_REUSE_GUARD",
                Name = "P2 wrap reuse guard fixture",
                TableMode = "FIXED_GRID",
                ContractVersion = 1,
                CreatedByUsername = OwnerUsername,
                RawWorkbookDataJson = "[]",
                SpecJson = "{\"kind\":\"MATRIX\",\"defaultDataType\":\"NUMBER\"}",
                DataRectR0 = 0,
                DataRectC0 = 0,
                DataRectR1 = 0,
                DataRectC1 = 0,
                W = 1,
                H = 1,
                CreatedByUserId = _ownerId,
                UpdatedByUserId = _ownerId,
                CreatedAtUtc = fixedAt,
                UpdatedAtUtc = fixedAt,
                IsDeleted = false
            },
            cancellationToken: ct);

        try
        {
            var custom = await _api.PostAsync(
                "api/dynamic-forms/wrap-dynamic-excel",
                new
                {
                    dynamicExcelTemplateId = excelId,
                    code = "P2_DF_WRAP_REUSE_CUSTOM",
                    name = "P2 custom wrapper",
                    description = "custom wrapper must not become the default reuse winner",
                    tagCodes = Array.Empty<string>()
                },
                token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(custom, HttpStatusCode.OK, "custom Dynamic Excel wrapper");
            var customBody = ApiHarnessClient.RequiredObject(custom.Json, "custom Dynamic Excel wrapper");
            var customId = ApiHarnessClient.RequiredString(customBody, "id");
            var customDocument = await LoadFormDocumentAsync(customId, ct);
            HarnessAssert.True(
                string.IsNullOrWhiteSpace(customDocument.WrapReuseKey),
                "Custom wrapper unexpectedly received a default reuse key");

            var defaultRequest = new
            {
                dynamicExcelTemplateId = excelId,
                code = (string?)null,
                name = (string?)null,
                description = (string?)null,
                tagCodes = Array.Empty<string>()
            };
            var firstDefault = await _api.PostAsync(
                "api/dynamic-forms/wrap-dynamic-excel",
                defaultRequest,
                token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(firstDefault, HttpStatusCode.OK, "first keyed default wrapper");
            var firstDefaultBody = ApiHarnessClient.RequiredObject(firstDefault.Json, "first keyed default wrapper");
            var firstDefaultId = ApiHarnessClient.RequiredString(firstDefaultBody, "id");
            HarnessAssert.True(
                !string.Equals(customId, firstDefaultId, StringComparison.Ordinal),
                "Default wrap incorrectly reused a custom wrapper");
            var firstDefaultDocument = await LoadFormDocumentAsync(firstDefaultId, ct);
            var reuseKey = HarnessAssert.Required(
                firstDefaultDocument.WrapReuseKey,
                "keyed default wrapper reuse key");
            HarnessAssert.True(firstDefaultDocument.IsActive, "First keyed default wrapper is inactive");

            var concurrentDefaults = await Task.WhenAll(
                _api.PostAsync("api/dynamic-forms/wrap-dynamic-excel", defaultRequest, token, ct: ct),
                _api.PostAsync("api/dynamic-forms/wrap-dynamic-excel", defaultRequest, token, ct: ct));
            for (var index = 0; index < concurrentDefaults.Length; index++)
            {
                ApiHarnessClient.ExpectStatus(
                    concurrentDefaults[index],
                    HttpStatusCode.OK,
                    $"concurrent keyed default wrapper {index + 1}");
                HarnessAssert.Equal(
                    firstDefaultId,
                    ApiHarnessClient.RequiredString(concurrentDefaults[index].Json, "id"),
                    $"Concurrent default wrapper {index + 1} did not reuse the keyed winner");
            }

            var deactivate = await _api.PutAsync(
                $"api/dynamic-forms/{firstDefaultId}",
                BuildWrappedFormStateUpdate(firstDefaultBody, isActive: false, expectedRevision: 1),
                token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(deactivate, HttpStatusCode.OK, "deactivate keyed wrapper A");
            var inactiveBody = ApiHarnessClient.RequiredObject(deactivate.Json, "inactive keyed wrapper A");
            var inactiveRevision = ApiHarnessClient.RequiredInt(inactiveBody, "revision");
            HarnessAssert.Equal(2, inactiveRevision, "Inactive keyed wrapper A revision mismatch");
            HarnessAssert.True(!ApiHarnessClient.RequiredBool(inactiveBody, "isActive"), "Keyed wrapper A stayed active");

            var replacement = await _api.PostAsync(
                "api/dynamic-forms/wrap-dynamic-excel",
                defaultRequest,
                token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(replacement, HttpStatusCode.OK, "replacement keyed wrapper B");
            var replacementBody = ApiHarnessClient.RequiredObject(replacement.Json, "replacement keyed wrapper B");
            var replacementId = ApiHarnessClient.RequiredString(replacementBody, "id");
            HarnessAssert.True(
                !string.Equals(firstDefaultId, replacementId, StringComparison.Ordinal),
                "Default wrap reused inactive wrapper A instead of creating B");
            var replacementDocument = await LoadFormDocumentAsync(replacementId, ct);
            HarnessAssert.Equal(reuseKey, replacementDocument.WrapReuseKey, "Replacement wrapper reuse key drifted");
            HarnessAssert.True(replacementDocument.IsActive, "Replacement keyed wrapper B is inactive");

            var beforeReactivate = await LoadFormDocumentAsync(firstDefaultId, ct);
            var reactivate = await _api.PutAsync(
                $"api/dynamic-forms/{firstDefaultId}",
                BuildWrappedFormStateUpdate(
                    inactiveBody,
                    isActive: true,
                    expectedRevision: inactiveRevision),
                token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(reactivate, HttpStatusCode.Conflict, "reactivate keyed wrapper A");
            AssertErrorCode(reactivate, "DYNAMIC_FORM_WRAP_REUSE_CONFLICT", "reactivate keyed wrapper A");
            HarnessAssert.Equal(
                "DYNAMIC_FORM_ACTIVE_WRAP_REUSE_EXISTS",
                ApiHarnessClient.FindStringRecursive(reactivate.Json, "reason"),
                "Wrapper reactivation conflict reason mismatch");
            AssertFormDocumentUnchanged(
                beforeReactivate,
                await LoadFormDocumentAsync(firstDefaultId, ct),
                "Rejected keyed wrapper A reactivation");

            var keyedWrappers = await forms
                .Find(x => x.WrapReuseKey == reuseKey && !x.IsDeleted && !x.IsPublished)
                .ToListAsync(ct);
            HarnessAssert.Equal(2, keyedWrappers.Count, "Keyed wrapper family cardinality mismatch");
            var activeWinners = keyedWrappers.Where(x => x.IsActive).ToArray();
            HarnessAssert.Equal(1, activeWinners.Length, "Reuse key has more than one active wrapper winner");
            HarnessAssert.Equal(replacementId, activeWinners[0].Id, "Replacement wrapper B is not the active winner");
        }
        finally
        {
            await forms.DeleteManyAsync(
                x => x.ExcelBlockDynamicExcelTemplateId == excelId,
                cancellationToken: ct);
            await excelTemplates.DeleteOneAsync(x => x.Id == excelId, cancellationToken: ct);
        }

        HarnessAssert.Equal(
            0L,
            await forms.CountDocumentsAsync(
                x => x.ExcelBlockDynamicExcelTemplateId == excelId,
                cancellationToken: ct),
            "Wrap reuse guard form fixture cleanup failed");
        HarnessAssert.Equal(
            0L,
            await excelTemplates.CountDocumentsAsync(x => x.Id == excelId, cancellationToken: ct),
            "Wrap reuse guard Dynamic Excel fixture cleanup failed");
        return new CaseObservation(
            "custom wrap stayed unkeyed; concurrent default wraps converged on one keyed winner; disabling A created B and reactivating A failed closed without a revision write",
            "customKey=null;defaultDifferent=true;concurrentDefaultSame=true;A.inactive=true;B.active=true;reactivate=409/DYNAMIC_FORM_WRAP_REUSE_CONFLICT/DYNAMIC_FORM_ACTIVE_WRAP_REUSE_EXISTS;activeWinners=1;fixtureCleanup=true");
    }

    private static JsonObject BuildWrappedFormStateUpdate(
        JsonObject detail,
        bool isActive,
        int expectedRevision)
    {
        var blocks = BuildNonStatisticTableWriterPayload(detail, "wrapped form state update");
        return new JsonObject
        {
            ["name"] = ApiHarnessClient.RequiredString(detail, "name"),
            ["description"] = detail["description"]?.DeepClone(),
            ["tagCodes"] = detail["tagCodes"]?.DeepClone() ?? new JsonArray(),
            ["schemaVersion"] = ApiHarnessClient.RequiredInt(detail, "schemaVersion"),
            ["sectionsJson"] = ApiHarnessClient.RequiredString(detail, "sectionsJson"),
            ["fieldsJson"] = ApiHarnessClient.RequiredString(detail, "fieldsJson"),
            ["excelBlockJson"] = blocks.ExcelBlockJson,
            ["blocksJson"] = blocks.BlocksJson,
            ["isActive"] = isActive,
            ["expectedRevision"] = expectedRevision
        };
    }

    private static (string BlocksJson, string? ExcelBlockJson) BuildNonStatisticTableWriterPayload(
        JsonObject detail,
        string context)
    {
        var blocks = JsonNode.Parse(ApiHarnessClient.RequiredString(detail, "blocksJson")) as JsonArray
                     ?? throw new InvalidOperationException($"{context} blocksJson must be an array");
        foreach (var block in blocks.OfType<JsonObject>())
        {
            foreach (var property in new[]
                     {
                         "metricLabelTargets",
                         "allowedRowLabelCodes",
                         "statisticsDisabled",
                         "statisticsDisabledReason",
                         "statisticColumns",
                         "statisticColumnLabels"
                     })
            {
                block.Remove(property);
            }

            if (block["metricRules"] is JsonArray metricRules)
            {
                foreach (var metric in metricRules.OfType<JsonObject>())
                    metric.Remove("aggregateOps");
            }
        }

        var firstBlock = blocks.OfType<JsonObject>().FirstOrDefault();
        return (blocks.ToJsonString(), firstBlock?.ToJsonString());
    }

    private async Task<CaseObservation> RejectDraftFormFlowBindingRaceAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var existingFlowId = HarnessAssert.Required(_flowId, "P1-BE-008 flow");
        var forms = _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates");
        var flows = _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates");
        var versions = _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions");
        const int raceCount = 4;
        for (var index = 0; index < raceCount; index++)
        {
            string? formId = null;
            var flowCode = $"P2_DF_DRAFT_BIND_RACE_{index + 1}";
            try
            {
                var created = await _api.PostAsync(
                    "api/dynamic-forms",
                    BuildMinimalFormRequest(
                        $"P2_DF_DRAFT_BIND_RACE_FORM_{index + 1}",
                        $"P2 draft-form Flow bind race {index + 1}"),
                    ownerToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(created, HttpStatusCode.OK, $"create draft form for Flow bind race {index + 1}");
                formId = ApiHarnessClient.RequiredString(created.Json, "id");
                var revision = ApiHarnessClient.RequiredInt(created.Json, "revision");
                var flowCountBefore = await flows.CountDocumentsAsync(
                    FilterDefinition<DynamicFlowTemplate>.Empty,
                    cancellationToken: ct);
                var versionCountBefore = await versions.CountDocumentsAsync(
                    FilterDefinition<DynamicFlowTemplateVersion>.Empty,
                    cancellationToken: ct);

                if (index == 0)
                {
                    var existingFlowBefore = await flows.Find(x => x.Id == existingFlowId).SingleAsync(ct);
                    var existingVersionsBefore = await versions.Find(x => x.TemplateId == existingFlowId)
                        .SortBy(x => x.VersionNo)
                        .ToListAsync(ct);
                    var createRejected = await _api.PostAsync(
                        "api/dynamic-flow-templates",
                        new JsonObject
                        {
                            ["commandId"] = "p2-draft-form-create-preflight",
                            ["code"] = $"{flowCode}_PREFLIGHT",
                            ["name"] = "P2 draft-form Flow create preflight",
                            ["description"] = "Create must reject a draft Dynamic Form before writes",
                            ["rootDynamicFormTemplateId"] = formId,
                            ["payload"] = BuildFlowPayload(formId)
                        },
                        ownerToken,
                        ct: ct);
                    AssertDraftFormFlowBindRejected(createRejected, "draft-form Flow create preflight");

                    var updateRootRejected = await _api.PutAsync(
                        $"api/dynamic-flow-templates/{existingFlowId}",
                        new
                        {
                            commandId = "p2-draft-form-root-update",
                            expectedFamilyRevision = existingFlowBefore.FamilyRevision,
                            rootDynamicFormTemplateId = formId
                        },
                        ownerToken,
                        ct: ct);
                    ApiHarnessClient.ExpectStatus(updateRootRejected, HttpStatusCode.BadRequest, "locked-family root update");
                    AssertErrorCode(updateRootRejected, "COMMON_VALIDATION_FAILED", "locked-family root update");
                    HarnessAssert.Equal(
                        "DYNAMIC_FLOW_LOCKED_FAMILY_ROOT_IMMUTABLE",
                        ApiHarnessClient.FindStringRecursive(updateRootRejected.Json, "reason"),
                        "locked-family root update reason mismatch");

                    var activeDraft = existingVersionsBefore.Single(x =>
                        x.Status == DynamicFlowTemplateVersionStatuses.Draft && !x.IsDeleted);

                    var saveDraftRejected = await _api.PutAsync(
                        $"api/dynamic-flow-templates/{existingFlowId}/versions/draft",
                        new JsonObject
                        {
                            ["commandId"] = "p2-draft-form-save-preflight",
                            ["expectedDraftRevision"] = activeDraft.DraftRevision,
                            ["expectedPayloadHash"] = activeDraft.PayloadHash,
                            ["payload"] = BuildFlowPayloadWithForeignChild(
                                HarnessAssert.Required(_formId, "P1-BE-005 published Flow root"),
                                formId)
                        },
                        ownerToken,
                        ct: ct);
                    AssertDraftFormFlowBindRejected(saveDraftRejected, "draft-form Flow draft save");

                    var existingFlowAfter = await flows.Find(x => x.Id == existingFlowId).SingleAsync(ct);
                    var existingVersionsAfter = await versions.Find(x => x.TemplateId == existingFlowId)
                        .SortBy(x => x.VersionNo)
                        .ToListAsync(ct);
                    HarnessAssert.True(
                        existingFlowBefore.ToBsonDocument().Equals(existingFlowAfter.ToBsonDocument()),
                        "Rejected draft-form root update mutated the existing Flow template");
                    HarnessAssert.Equal(existingVersionsBefore.Count, existingVersionsAfter.Count, "Rejected draft-form save changed Flow version count");
                    for (var versionIndex = 0; versionIndex < existingVersionsBefore.Count; versionIndex++)
                    {
                        HarnessAssert.True(
                            existingVersionsBefore[versionIndex].ToBsonDocument().Equals(
                                existingVersionsAfter[versionIndex].ToBsonDocument()),
                            $"Rejected draft-form save mutated Flow version {versionIndex + 1}");
                    }
                    HarnessAssert.Equal(
                        flowCountBefore,
                        await flows.CountDocumentsAsync(FilterDefinition<DynamicFlowTemplate>.Empty, cancellationToken: ct),
                        "Rejected draft-form create wrote a Flow template");
                    HarnessAssert.Equal(
                        versionCountBefore,
                        await versions.CountDocumentsAsync(FilterDefinition<DynamicFlowTemplateVersion>.Empty, cancellationToken: ct),
                        "Rejected draft-form create/save wrote a Flow version");
                }

                var createFlowTask = _api.PostAsync(
                    "api/dynamic-flow-templates",
                    new JsonObject
                    {
                        ["commandId"] = $"p2-draft-form-race-{index + 1}",
                        ["code"] = flowCode,
                        ["name"] = $"P2 draft-form Flow bind race {index + 1}",
                        ["description"] = "Flow draft must bind only an already-published Dynamic Form",
                        ["rootDynamicFormTemplateId"] = formId,
                        ["payload"] = BuildFlowPayload(formId)
                    },
                    ownerToken,
                    ct: ct);
                var deleteFormTask = _api.DeleteAsync(
                    $"api/dynamic-forms/{formId}?expectedRevision={revision}",
                    ownerToken,
                    ct: ct);
                var responses = await Task.WhenAll(createFlowTask, deleteFormTask);
                var createFlow = responses[0];
                var deleteForm = responses[1];

                AssertDraftFormFlowBindRejected(
                    createFlow,
                    $"draft-form Flow bind race {index + 1}",
                    allowDeletedRaceNotFound: true);
                ApiHarnessClient.ExpectStatus(deleteForm, HttpStatusCode.OK, $"draft-form delete race {index + 1}");
                var deletedForm = await forms.Find(x => x.Id == formId).SingleAsync(ct);
                HarnessAssert.True(deletedForm.IsDeleted, $"Draft form race {index + 1} was not soft-deleted");
                HarnessAssert.Equal(revision + 1, deletedForm.Revision, $"Draft form race {index + 1} revision mismatch");
                var flowCountAfter = await flows.CountDocumentsAsync(
                    FilterDefinition<DynamicFlowTemplate>.Empty,
                    cancellationToken: ct);
                var versionCountAfter = await versions.CountDocumentsAsync(
                    FilterDefinition<DynamicFlowTemplateVersion>.Empty,
                    cancellationToken: ct);
                HarnessAssert.Equal(flowCountBefore, flowCountAfter, $"Draft-form Flow race {index + 1} wrote a Flow template");
                HarnessAssert.Equal(versionCountBefore, versionCountAfter, $"Draft-form Flow race {index + 1} wrote a Flow version");
            }
            finally
            {
                var preflightFlowCode = $"{flowCode}_PREFLIGHT";
                var leakedFlows = await flows.Find(x => x.Code == flowCode || x.Code == preflightFlowCode).ToListAsync(ct);
                foreach (var leakedFlow in leakedFlows)
                    await versions.DeleteManyAsync(x => x.TemplateId == leakedFlow.Id, ct);
                await flows.DeleteManyAsync(x => x.Code == flowCode || x.Code == preflightFlowCode, ct);
                if (formId is not null)
                    await forms.DeleteOneAsync(x => x.Id == formId, ct);
            }
        }

        return new CaseObservation(
            "Create and SaveDraft returned the exact published-forms-required contract, locked-family root mutation stayed immutable, and four Flow-create/Form-delete races left no dangling Flow",
            "publishedFormContractEndpoints=2;lockedRootImmutable=1;http400=3;errorCode=COMMON_VALIDATION_FAILED;reason=DYNAMIC_FLOW_TEMPLATE_PUBLISHED_FORMS_REQUIRED;races=4;draftBindRejected=4;formDelete200=4;formsSoftDeleted=4;flowWrites=0;versionWrites=0;fixtureCleanup=true");
    }

    private async Task<CaseObservation> VerifyPublishedSchemaSnapshotAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var formId = HarnessAssert.Required(_formId, "P1-BE-005 form");
        var source = await LoadFormDocumentAsync(formId, ct);
        var sourceSnapshot = RequirePublishedSnapshot(source, "primary published form");

        var detail = await _api.GetAsync($"api/dynamic-forms/{formId}", token, ct: ct);
        ApiHarnessClient.ExpectStatus(detail, HttpStatusCode.OK, "published Dynamic Form snapshot detail");
        HarnessAssert.Equal(
            sourceSnapshot.Hash,
            ApiHarnessClient.RequiredString(detail.Json, "publishedSchemaHash"),
            "Published snapshot hash response mismatch");
        HarnessAssert.Equal(
            sourceSnapshot.Json,
            ApiHarnessClient.RequiredString(detail.Json, "publishedSchemaSnapshotJson"),
            "Published snapshot JSON response mismatch");

        var beforeRetry = await LoadFormDocumentAsync(formId, ct);
        var retry = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/publish",
            new { expectedRevision = 1 },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(retry, HttpStatusCode.OK, "published snapshot idempotent retry");
        HarnessAssert.Equal(
            sourceSnapshot.Hash,
            ApiHarnessClient.RequiredString(retry.Json, "publishedSchemaHash"),
            "Idempotent publish changed the response hash");
        AssertFormDocumentUnchanged(beforeRetry, await LoadFormDocumentAsync(formId, ct), "Published snapshot retry");

        var mirrorCreate = await _api.PostAsync(
            "api/dynamic-forms",
            BuildFormRequest(SnapshotMirrorFormCode, "P2 snapshot mirror", updated: true),
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(mirrorCreate, HttpStatusCode.OK, "snapshot mirror create");
        var mirror = ApiHarnessClient.RequiredObject(mirrorCreate.Json, "snapshot mirror draft");
        var mirrorId = ApiHarnessClient.RequiredString(mirror, "id");
        _p2FormIds[SnapshotMirrorFormCode] = mirrorId;
        var mirrorPublish = await _api.PostAsync(
            $"api/dynamic-forms/{mirrorId}/publish",
            new { expectedRevision = ApiHarnessClient.RequiredInt(mirror, "revision") },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(mirrorPublish, HttpStatusCode.OK, "snapshot mirror publish");
        var mirrorDoc = await LoadFormDocumentAsync(mirrorId, ct);
        var mirrorSnapshot = RequirePublishedSnapshot(mirrorDoc, "mirror published form");
        HarnessAssert.Equal(sourceSnapshot.Hash, mirrorSnapshot.Hash, "Equivalent schemas produced different hashes");
        HarnessAssert.Equal(sourceSnapshot.Json, mirrorSnapshot.Json, "Equivalent schemas produced different canonical JSON");

        _v1PublishedSchemaHash = sourceSnapshot.Hash;
        return new CaseObservation(
            $"publish persisted canonical JSON and stable SHA-256 {sourceSnapshot.Hash}; an equivalent form matched exactly",
            $"snapshotPersisted=true;rawShaVerified=true;equivalentSchema=true;hash={sourceSnapshot.Hash}");
    }

    private async Task<CaseObservation> VerifyWrappedDynamicExcelLifecycleAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var wrapRequest = new
        {
            dynamicExcelTemplateId = _dynamicExcelId,
            code = (string?)null,
            name = (string?)null,
            description = (string?)null,
            tagCodes = Array.Empty<string>()
        };

        var first = await _api.PostAsync("api/dynamic-forms/wrap-dynamic-excel", wrapRequest, token, ct: ct);
        ApiHarnessClient.ExpectStatus(first, HttpStatusCode.OK, "first Dynamic Excel wrapper");
        var draft = ApiHarnessClient.RequiredObject(first.Json, "wrapped Dynamic Form draft");
        var draftId = ApiHarnessClient.RequiredString(draft, "id");
        HarnessAssert.Equal(
            HarnessAssert.Required(_concurrentWrappedDraftId, "P2-DF-CAS-010 concurrent wrapper draft"),
            draftId,
            "Lifecycle wrap did not reuse the draft created by simultaneous requests");
        HarnessAssert.True(!ApiHarnessClient.RequiredBool(draft, "isPublished"), "Wrapped form must start as draft");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(draft, "revision"), "Wrapped draft revision mismatch");
        HarnessAssert.Equal("WRAPPED", ApiHarnessClient.RequiredString(draft, "lineageStatus"), "Wrapped lineage mismatch");
        HarnessAssert.Equal(
            _dynamicExcelId,
            ApiHarnessClient.RequiredString(draft, "excelBlockDynamicExcelTemplateId"),
            "Wrapped Dynamic Excel reference mismatch");

        var reused = await _api.PostAsync("api/dynamic-forms/wrap-dynamic-excel", wrapRequest, token, ct: ct);
        ApiHarnessClient.ExpectStatus(reused, HttpStatusCode.OK, "reuse Dynamic Excel wrapper draft");
        HarnessAssert.Equal(draftId, ApiHarnessClient.RequiredString(reused.Json, "id"), "Second wrap did not reuse the draft");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(reused.Json, "revision"), "Draft reuse changed revision");

        var nonStatisticBlocks = BuildNonStatisticTableWriterPayload(
            draft,
            "edit wrapped Dynamic Form draft");
        var update = new JsonObject
        {
            ["name"] = "P2 wrapped Dynamic Excel edited",
            ["description"] = "edited before publish",
            ["tagCodes"] = new JsonArray(),
            ["schemaVersion"] = ApiHarnessClient.RequiredInt(draft, "schemaVersion"),
            ["sectionsJson"] = ApiHarnessClient.RequiredString(draft, "sectionsJson"),
            ["fieldsJson"] = ApiHarnessClient.RequiredString(draft, "fieldsJson"),
            ["excelBlockJson"] = nonStatisticBlocks.ExcelBlockJson,
            ["blocksJson"] = nonStatisticBlocks.BlocksJson,
            ["isActive"] = true,
            ["expectedRevision"] = 1
        };
        var edited = await _api.PutAsync($"api/dynamic-forms/{draftId}", update, token, ct: ct);
        ApiHarnessClient.ExpectStatus(edited, HttpStatusCode.OK, "edit wrapped Dynamic Form draft");
        var editedRevision = ApiHarnessClient.RequiredInt(edited.Json, "revision");
        HarnessAssert.Equal(2, editedRevision, "Wrapped draft edit revision mismatch");

        var published = await _api.PostAsync(
            $"api/dynamic-forms/{draftId}/publish",
            new { expectedRevision = editedRevision },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(published, HttpStatusCode.OK, "publish wrapped Dynamic Form");
        HarnessAssert.True(ApiHarnessClient.RequiredBool(published.Json, "isPublished"), "Wrapped form was not published");
        _wrappedPublishedFormId = draftId;

        var replacement = await _api.PostAsync("api/dynamic-forms/wrap-dynamic-excel", wrapRequest, token, ct: ct);
        ApiHarnessClient.ExpectStatus(replacement, HttpStatusCode.OK, "wrap Dynamic Excel after published wrapper");
        var replacementId = ApiHarnessClient.RequiredString(replacement.Json, "id");
        HarnessAssert.True(!string.Equals(draftId, replacementId, StringComparison.Ordinal), "Wrap reused a published form");
        HarnessAssert.True(!ApiHarnessClient.RequiredBool(replacement.Json, "isPublished"), "Replacement wrapper is not a draft");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(replacement.Json, "revision"), "Replacement wrapper revision mismatch");
        _wrappedReplacementDraftId = replacementId;

        var wrappers = await _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
            .Find(x =>
                x.ExcelBlockDynamicExcelTemplateId == _dynamicExcelId &&
                x.CreatedByUserId == _ownerId &&
                !x.IsDeleted)
            .ToListAsync(ct);
        HarnessAssert.Equal(2, wrappers.Count, "Wrapper lifecycle Mongo cardinality mismatch");
        HarnessAssert.Equal(1, wrappers.Count(x => x.IsPublished), "Wrapper lifecycle published count mismatch");
        HarnessAssert.Equal(1, wrappers.Count(x => !x.IsPublished), "Wrapper lifecycle draft count mismatch");
        return new CaseObservation(
            "wrap reused the single concurrent draft, kept it editable, published it, then created one replacement draft",
            "concurrentDraftReuse=true;draftReuse=true;edited=true;published=1;replacementDraft=1;documents=2");
    }

    private async Task<CaseObservation> CreateV1RuntimeBindingAsync(CancellationToken ct)
    {
        var adminToken = HarnessAssert.Required(_adminToken, "P1-BE-002 admin token");
        var adminId = HarnessAssert.Required(_adminId, "P1-BE-002 admin id");
        var formId = HarnessAssert.Required(_formId, "P1-BE-005 form");
        var familyHash = HarnessAssert.Required(_v1PublishedSchemaHash, "P2-DF-LC-001 v1 hash");

        _bindingWorkId = ObjectId.GenerateNewId().ToString();
        var fixedAt = new DateTime(2026, 7, 22, 2, 0, 0, DateTimeKind.Utc);
        await _database.GetCollection<Work>("works").InsertOneAsync(
            new Work
            {
                Id = _bindingWorkId,
                AutoCode = "P2-DF-VERSION-BIND",
                Code = "P2-DF-VERSION-BIND",
                Name = "P2 exact Dynamic Form binding work",
                Description = "integration version binding fixture",
                Status = WorkStatus.S1,
                Type = WorkType.TASK,
                Priority = WorkPriority.MEDIUM,
                LeaderDirectiveUserId = null!,
                LeaderWatchUserIds = [],
                LeaderWatch = [],
                Owner = new UserRef
                {
                    UserId = adminId,
                    Username = "admin",
                    FullName = "System Admin"
                },
                CreatedByUserId = adminId,
                UpdatedByUserId = adminId,
                CreatedAtUtc = fixedAt,
                UpdatedAtUtc = fixedAt,
                IsDeleted = false
            },
            cancellationToken: ct);

        var assignment = await CreateVersionAssignmentAsync(formId, "P2 bind published v1", adminToken, ct);
        _v1AssignmentId = ApiHarnessClient.RequiredString(assignment, "id");
        AssertAssignmentVersionIdentity(assignment, formId, formId, 1, familyHash, "v1 assignment API");

        var persisted = await _database.GetCollection<WorkAssignment>("work_assignments")
            .Find(x => x.Id == _v1AssignmentId && !x.IsDeleted)
            .SingleAsync(ct);
        AssertAssignmentVersionIdentity(persisted, formId, formId, 1, familyHash, "v1 assignment Mongo");
        var binding = await _database.GetCollection<WorkTemplateAssignee>("work_template_assignees")
            .Find(x => x.WorkAssignmentId == _v1AssignmentId && x.AssigneeUserId == _assigneeId && !x.IsDeleted)
            .SingleAsync(ct);
        AssertAssignmentVersionIdentity(binding, formId, formId, 1, familyHash, "v1 assignee binding Mongo");

        var now = fixedAt;
        var reportId = ObjectId.GenerateNewId().ToString();
        await _database.GetCollection<ReviewReportListDocRole>("review_report_list_doc_roles")
            .InsertOneAsync(
                new ReviewReportListDocRole
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    DocType = DocType.WORK_REPORT,
                    DocId = reportId,
                    UserId = _reviewerId,
                    WorkId = _bindingWorkId,
                    AssignmentId = _v1AssignmentId,
                    WorkReportPeriodId = ObjectId.GenerateNewId().ToString(),
                    CurrentReportId = reportId,
                    ReviewerUserId = _reviewerId,
                    DynamicExcelId = _dynamicExcelId,
                    DynamicExcelCode = "P2_IT_EXCEL",
                    DynamicExcelName = "P2 CAS import fixture",
                    DynamicFormTemplateId = formId,
                    DynamicFormTemplateCode = FormCode,
                    DynamicFormTemplateName = "P1 typed form updated",
                    DynamicFormFamilyId = formId,
                    DynamicFormVersionNo = 1,
                    DynamicFormSchemaHash = familyHash,
                    AssigneeUserId = _assigneeId,
                    ReportIsActive = true,
                    CreatedByUserId = _adminId,
                    UpdatedByUserId = _adminId,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    IsDeleted = false
                },
                cancellationToken: ct);

        return new CaseObservation(
            "API assignment and runtime projections bound exact published v1 identity/hash; reviewer projection seeded for read matrix",
            "apiVersion=1;assignmentMongo=true;assigneeBindingMongo=true;reviewerProjection=1;hashExact=true");
    }

    private async Task<CaseObservation> VerifyActorMatrixAndCloneAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var adminToken = HarnessAssert.Required(_adminToken, "P1-BE-002 admin token");
        var assigneeToken = HarnessAssert.Required(_assigneeToken, "P1-BE-003 assignee token");
        var reviewerToken = HarnessAssert.Required(_reviewerToken, "P1-BE-003 reviewer token");
        var outsiderToken = HarnessAssert.Required(_outsiderToken, "P1-BE-003 outsider token");
        var formId = HarnessAssert.Required(_formId, "P1-BE-005 form");
        var assignmentId = HarnessAssert.Required(_v1AssignmentId, "P2-DF-LC-003 v1 assignment");

        var ownerDetail = await RequireFormDetailAsync(formId, ownerToken, "owner published form", ct);
        var adminDetail = await RequireFormDetailAsync(formId, adminToken, "admin published form", ct);
        AssertPublishedOwnerActions(ownerDetail, "owner published actions");
        AssertPublishedOwnerActions(adminDetail, "admin published actions");
        await AssertDesignSearchVisibilityAsync(ownerToken, formId, true, "owner", ct);
        await AssertDesignSearchVisibilityAsync(adminToken, formId, true, "admin", ct);
        await AssertVersionHistoryAsync(ownerToken, formId, [formId], "owner v1 history", ct);
        await AssertVersionHistoryAsync(adminToken, formId, [formId], "admin v1 history", ct);

        await AssertRuntimeReadOnlyAsync(assigneeToken, formId, "assignee runtime v1", ct);
        await AssertRuntimeReadOnlyAsync(reviewerToken, formId, "reviewer runtime v1", ct);
        await AssertOutsiderDeniedAsync(outsiderToken, formId, ct);

        var request = await _api.PostAsync(
            $"api/work-assignments/{assignmentId}/dynamic-form-clone-requests",
            new { reason = "P2 integration clone grant" },
            assigneeToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(request, HttpStatusCode.OK, "assignee clone request");
        var requestId = ApiHarnessClient.RequiredString(request.Json, "id");
        HarnessAssert.Equal("PENDING", ApiHarnessClient.RequiredString(request.Json, "status"), "Clone request status mismatch");

        var approved = await _api.PostAsync(
            $"api/dynamic-form-clone-requests/{requestId}/approve",
            new { comment = "approved by assignment owner" },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(approved, HttpStatusCode.OK, "approve assignee clone request");
        HarnessAssert.Equal("APPROVED", ApiHarnessClient.RequiredString(approved.Json, "status"), "Clone approval status mismatch");

        var grantedDetail = await RequireFormDetailAsync(formId, assigneeToken, "clone-granted form", ct);
        HarnessAssert.True(ApiHarnessClient.RequiredBool(grantedDetail, "canViewByCloneGrant"), "Clone grant marker is false");
        AssertCloneGrantActions(grantedDetail, "clone-granted actions");
        await AssertDesignSearchVisibilityAsync(assigneeToken, formId, true, "clone-granted assignee", ct);

        var deniedHistory = await _api.GetAsync($"api/dynamic-forms/{formId}/versions", assigneeToken, ct: ct);
        ApiHarnessClient.ExpectStatus(deniedHistory, HttpStatusCode.Forbidden, "clone-granted version history");
        AssertErrorCode(deniedHistory, "DYNAMIC_FORM_READ_FORBIDDEN", "clone-granted version history");
        var deniedUpdate = await _api.PutAsync(
            $"api/dynamic-forms/{formId}",
            BuildFormUpdateRequest(FormCode, "clone grant must not edit", ApiHarnessClient.RequiredInt(ownerDetail, "revision")),
            assigneeToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(deniedUpdate, HttpStatusCode.Forbidden, "clone-granted update");
        AssertErrorCode(deniedUpdate, "DYNAMIC_FORM_MUTATE_FORBIDDEN", "clone-granted update");
        var deniedSuccessor = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/versions",
            new { expectedRevision = ApiHarnessClient.RequiredInt(ownerDetail, "revision") },
            assigneeToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(deniedSuccessor, HttpStatusCode.Forbidden, "clone-granted successor");
        AssertErrorCode(deniedSuccessor, "DYNAMIC_FORM_MUTATE_FORBIDDEN", "clone-granted successor");
        var deniedPublish = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/publish",
            new { expectedRevision = ApiHarnessClient.RequiredInt(ownerDetail, "revision") },
            assigneeToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(deniedPublish, HttpStatusCode.Forbidden, "clone-granted publish");
        AssertErrorCode(deniedPublish, "DYNAMIC_FORM_MUTATE_FORBIDDEN", "clone-granted publish");

        var cloneResponse = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/clone",
            new { code = GrantedCloneFormCode, name = "P2 granted clone" },
            assigneeToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(cloneResponse, HttpStatusCode.OK, "clone-granted clone endpoint");
        var clone = ApiHarnessClient.RequiredObject(cloneResponse.Json, "clone-granted clone");
        _grantedCloneFormId = ApiHarnessClient.RequiredString(clone, "id");
        HarnessAssert.Equal(_grantedCloneFormId, ApiHarnessClient.RequiredString(clone, "familyId"), "Clone family must be new");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(clone, "versionNo"), "Clone version number mismatch");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(clone, "revision"), "Clone revision mismatch");
        HarnessAssert.Equal(formId, ApiHarnessClient.RequiredString(clone, "clonedFromVersionId"), "Clone source mismatch");
        HarnessAssert.Equal("CLONE", ApiHarnessClient.RequiredString(clone, "lineageStatus"), "Clone lineage mismatch");
        HarnessAssert.True(!ApiHarnessClient.RequiredBool(clone, "isPublished"), "Clone must start as draft");
        HarnessAssert.Equal(_assigneeId, ApiHarnessClient.RequiredString(clone, "createdByUserId"), "Clone owner mismatch");

        var cloneDoc = await LoadFormDocumentAsync(_grantedCloneFormId, ct);
        HarnessAssert.Equal(_grantedCloneFormId, cloneDoc.FamilyId, "Mongo clone family mismatch");
        HarnessAssert.Equal(formId, cloneDoc.ClonedFromVersionId, "Mongo clone source mismatch");
        HarnessAssert.Equal(1, cloneDoc.VersionNo, "Mongo clone version mismatch");
        HarnessAssert.Equal(_assigneeId, cloneDoc.CreatedByUserId, "Mongo clone owner mismatch");
        return new CaseObservation(
            "owner/admin had design actions; assignee/reviewer were exact runtime-readonly; outsider denied; approved grant enabled only get/clone into a new family",
            "owner=true;admin=true;runtimeReaders=2;runtimeSearch=false;outsider=403;cloneGrant=get+clone;cloneFamily=new;cloneVersion=1");
    }

    private async Task<CaseObservation> CreateConcurrentNextVersionAsync(CancellationToken ct)
    {
        var adminToken = HarnessAssert.Required(_adminToken, "P1-BE-002 admin token");
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var assigneeToken = HarnessAssert.Required(_assigneeToken, "P1-BE-003 assignee token");
        var reviewerToken = HarnessAssert.Required(_reviewerToken, "P1-BE-003 reviewer token");
        var outsiderToken = HarnessAssert.Required(_outsiderToken, "P1-BE-003 outsider token");
        var formId = HarnessAssert.Required(_formId, "P1-BE-005 form");
        var sourceBefore = await LoadFormDocumentAsync(formId, ct);
        var expectedRevision = sourceBefore.Revision;

        var firstTask = _api.PostAsync(
            $"api/dynamic-forms/{formId}/versions",
            new { expectedRevision, name = "P2 successor A" },
            adminToken,
            ct: ct);
        var secondTask = _api.PostAsync(
            $"api/dynamic-forms/{formId}/versions",
            new { expectedRevision, name = "P2 successor B" },
            adminToken,
            ct: ct);
        await Task.WhenAll(firstTask, secondTask);

        var responses = new[] { await firstTask, await secondTask };
        var successes = responses.Where(x => x.StatusCode == HttpStatusCode.OK).ToArray();
        var conflicts = responses.Where(x => x.StatusCode == HttpStatusCode.Conflict).ToArray();
        HarnessAssert.Equal(1, successes.Length, "Concurrent successor creation must have exactly one winner");
        HarnessAssert.Equal(1, conflicts.Length, "Concurrent successor creation must have exactly one 409 loser");
        var loserCode = ApiHarnessClient.FindStringRecursive(conflicts[0].Json, "errorCode")
                        ?? ApiHarnessClient.FindStringRecursive(conflicts[0].Json, "reason");
        HarnessAssert.True(
            loserCode is "DYNAMIC_FORM_REVISION_CONFLICT" or "DYNAMIC_FORM_VERSION_CONFLICT",
            $"Concurrent successor loser reason mismatch: {loserCode}");

        var next = ApiHarnessClient.RequiredObject(successes[0].Json, "concurrent successor winner");
        _v2FormId = ApiHarnessClient.RequiredString(next, "id");
        HarnessAssert.Equal(FormCode, ApiHarnessClient.RequiredString(next, "code"), "v2 code mismatch");
        HarnessAssert.Equal(formId, ApiHarnessClient.RequiredString(next, "familyId"), "v2 family mismatch");
        HarnessAssert.Equal(formId, ApiHarnessClient.RequiredString(next, "previousVersionId"), "v2 previous mismatch");
        HarnessAssert.Equal(2, ApiHarnessClient.RequiredInt(next, "versionNo"), "v2 number mismatch");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(next, "revision"), "v2 draft revision mismatch");
        HarnessAssert.True(!ApiHarnessClient.RequiredBool(next, "isPublished"), "v2 must start as draft");
        HarnessAssert.Equal(_ownerId, ApiHarnessClient.RequiredString(next, "createdByUserId"), "Admin transferred family ownership");

        var sourceAfter = await LoadFormDocumentAsync(formId, ct);
        HarnessAssert.Equal(expectedRevision + 1, sourceAfter.Revision, "Successor CAS did not reserve source revision exactly once");
        var v2Doc = await LoadFormDocumentAsync(_v2FormId, ct);
        HarnessAssert.Equal(_ownerId, v2Doc.CreatedByUserId, "Mongo v2 owner mismatch");
        HarnessAssert.Equal(OwnerUsername, v2Doc.CreatedByUsername, "Mongo v2 owner username mismatch");
        HarnessAssert.Equal(_adminId, v2Doc.UpdatedByUserId, "Mongo v2 creating actor mismatch");
        var familyV2Count = await _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
            .CountDocumentsAsync(
                x => x.FamilyId == formId && x.VersionNo == 2 && !x.IsDeleted,
                cancellationToken: ct);
        HarnessAssert.Equal(1L, familyV2Count, "Concurrent successor race wrote duplicate v2 documents");

        await AssertVersionHistoryAsync(ownerToken, _v2FormId, [_v2FormId, formId], "owner v2 history", ct);
        await AssertVersionHistoryAsync(adminToken, formId, [_v2FormId, formId], "admin v2 history", ct);
        await AssertHistoryDeniedAsync(assigneeToken, formId, "clone-granted assignee history", ct);
        await AssertHistoryDeniedAsync(reviewerToken, formId, "runtime reviewer history", ct);
        await AssertHistoryDeniedAsync(outsiderToken, formId, "outsider history", ct);

        var ownerV2 = await RequireFormDetailAsync(_v2FormId, ownerToken, "owner admin-created v2", ct);
        var adminV2 = await RequireFormDetailAsync(_v2FormId, adminToken, "admin v2", ct);
        AssertDraftOwnerActions(ownerV2, "owner admin-created v2 actions");
        AssertDraftOwnerActions(adminV2, "admin v2 actions");
        return new CaseObservation(
            "two admin successor requests produced one v2 and one 409; source CAS advanced once and original family ownership/history remained intact",
            $"success=1;conflict=1;loser={loserCode};sourceRevisionDelta=1;v2Documents=1;ownerRetained=true;history=2");
    }

    private async Task<CaseObservation> UpdateAndPublishV2Async(CancellationToken ct)
    {
        var adminToken = HarnessAssert.Required(_adminToken, "P1-BE-002 admin token");
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var assigneeToken = HarnessAssert.Required(_assigneeToken, "P1-BE-003 assignee token");
        var reviewerToken = HarnessAssert.Required(_reviewerToken, "P1-BE-003 reviewer token");
        var outsiderToken = HarnessAssert.Required(_outsiderToken, "P1-BE-003 outsider token");
        var formId = HarnessAssert.Required(_formId, "P1-BE-005 form");
        var v2Id = HarnessAssert.Required(_v2FormId, "P2-DF-LC-005 v2 form");
        var v1Hash = HarnessAssert.Required(_v1PublishedSchemaHash, "P2-DF-LC-001 v1 hash");

        var updateRequest = BuildFormUpdateRequest(FormCode, "P2 published successor", expectedRevision: 1);
        updateRequest["schemaVersion"] = 3;
        var schema = ApiHarnessClient.RequiredObject(updateRequest["schema"], "v2 update schema");
        var fields = ApiHarnessClient.RequiredArray(schema["fields"], "v2 update fields");
        var firstField = ApiHarnessClient.RequiredObject(fields[0], "v2 first field");
        firstField["name"] = "Thong tin phien ban v2";

        var updated = await _api.PutAsync($"api/dynamic-forms/{v2Id}", updateRequest, adminToken, ct: ct);
        ApiHarnessClient.ExpectStatus(updated, HttpStatusCode.OK, "admin update v2 draft");
        HarnessAssert.Equal(2, ApiHarnessClient.RequiredInt(updated.Json, "revision"), "v2 update revision mismatch");
        HarnessAssert.Equal(_ownerId, ApiHarnessClient.RequiredString(updated.Json, "createdByUserId"), "Admin update transferred v2 ownership");

        var published = await _api.PostAsync(
            $"api/dynamic-forms/{v2Id}/publish",
            new { expectedRevision = 2 },
            ownerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(published, HttpStatusCode.OK, "owner publish admin-created v2");
        HarnessAssert.True(ApiHarnessClient.RequiredBool(published.Json, "isPublished"), "v2 was not published");
        HarnessAssert.Equal(3, ApiHarnessClient.RequiredInt(published.Json, "revision"), "v2 publish revision mismatch");
        _v2PublishedSchemaHash = ApiHarnessClient.RequiredString(published.Json, "publishedSchemaHash");
        HarnessAssert.True(
            !string.Equals(v1Hash, _v2PublishedSchemaHash, StringComparison.Ordinal),
            "Changed v2 schema reused the v1 published hash");

        var v2Doc = await LoadFormDocumentAsync(v2Id, ct);
        var v2Snapshot = RequirePublishedSnapshot(v2Doc, "published v2");
        HarnessAssert.Equal(_v2PublishedSchemaHash, v2Snapshot.Hash, "Mongo v2 published hash mismatch");
        HarnessAssert.Equal(formId, v2Doc.FamilyId, "Mongo v2 family mismatch");
        HarnessAssert.Equal(formId, v2Doc.PreviousVersionId, "Mongo v2 previous mismatch");
        HarnessAssert.Equal(_ownerId, v2Doc.CreatedByUserId, "Mongo v2 owner changed after admin update");
        HarnessAssert.Equal(_ownerId, v2Doc.PublishedByUserId, "Mongo v2 publisher mismatch");

        var ownerDetail = await RequireFormDetailAsync(v2Id, ownerToken, "owner published v2", ct);
        var adminDetail = await RequireFormDetailAsync(v2Id, adminToken, "admin published v2", ct);
        AssertPublishedOwnerActions(ownerDetail, "owner published v2 actions");
        AssertPublishedOwnerActions(adminDetail, "admin published v2 actions");
        await AssertVersionHistoryAsync(ownerToken, v2Id, [v2Id, formId], "owner published v2 history", ct);
        await AssertVersionHistoryAsync(adminToken, v2Id, [v2Id, formId], "admin published v2 history", ct);

        var ownerAuthorizedProbe = await _api.PostAsync(
            $"api/dynamic-forms/{v2Id}/versions",
            new { },
            ownerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(ownerAuthorizedProbe, HttpStatusCode.BadRequest, "owner successor authorization probe");
        AssertErrorCode(ownerAuthorizedProbe, "DYNAMIC_FORM_REVISION_REQUIRED", "owner successor authorization probe");
        var v3Count = await _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
            .CountDocumentsAsync(x => x.FamilyId == formId && x.VersionNo == 3 && !x.IsDeleted, cancellationToken: ct);
        HarnessAssert.Equal(0L, v3Count, "Authorization probe wrote a v3 document");

        await AssertFormGetDeniedAsync(assigneeToken, v2Id, "v1 clone-grantee exact v2", ct);
        await AssertFormGetDeniedAsync(reviewerToken, v2Id, "v1 reviewer exact v2", ct);
        await AssertFormGetDeniedAsync(outsiderToken, v2Id, "outsider v2", ct);
        await AssertDesignSearchVisibilityAsync(assigneeToken, v2Id, false, "v1-only clone grant", ct);
        await AssertDesignSearchVisibilityAsync(reviewerToken, v2Id, false, "v1-only reviewer", ct);
        return new CaseObservation(
            "admin edited v2 without changing family ownership; owner published a distinct hash and remained authorized for history/future versioning",
            "adminUpdate=true;ownerPublish=true;ownerRetained=true;history=2;canCreateVersion=true;v3Probe=400;v3Writes=0;v2RuntimePrebind=403");
    }

    private async Task<CaseObservation> VerifyExactVersionAssignmentBindingAsync(CancellationToken ct)
    {
        var adminToken = HarnessAssert.Required(_adminToken, "P1-BE-002 admin token");
        var assigneeToken = HarnessAssert.Required(_assigneeToken, "P1-BE-003 assignee token");
        var formId = HarnessAssert.Required(_formId, "P1-BE-005 form");
        var v2Id = HarnessAssert.Required(_v2FormId, "P2-DF-LC-005 v2 form");
        var v1AssignmentId = HarnessAssert.Required(_v1AssignmentId, "P2-DF-LC-003 v1 assignment");
        var v1Hash = HarnessAssert.Required(_v1PublishedSchemaHash, "P2-DF-LC-001 v1 hash");
        var v2Hash = HarnessAssert.Required(_v2PublishedSchemaHash, "P2-DF-LC-006 v2 hash");

        var before = await _database.GetCollection<WorkAssignment>("work_assignments")
            .Find(x => x.Id == v1AssignmentId && !x.IsDeleted)
            .SingleAsync(ct);
        AssertAssignmentVersionIdentity(before, formId, formId, 1, v1Hash, "v1 assignment before v2 binding");

        var v2Assignment = await CreateVersionAssignmentAsync(v2Id, "P2 bind published v2", adminToken, ct);
        _v2AssignmentId = ApiHarnessClient.RequiredString(v2Assignment, "id");
        AssertAssignmentVersionIdentity(v2Assignment, v2Id, formId, 2, v2Hash, "v2 assignment API");

        var after = await _database.GetCollection<WorkAssignment>("work_assignments")
            .Find(x => x.Id == v1AssignmentId && !x.IsDeleted)
            .SingleAsync(ct);
        AssertAssignmentVersionIdentity(after, formId, formId, 1, v1Hash, "v1 assignment after v2 publish/binding");
        var persistedV2 = await _database.GetCollection<WorkAssignment>("work_assignments")
            .Find(x => x.Id == _v2AssignmentId && !x.IsDeleted)
            .SingleAsync(ct);
        AssertAssignmentVersionIdentity(persistedV2, v2Id, formId, 2, v2Hash, "v2 assignment Mongo");

        var bindings = await _database.GetCollection<WorkTemplateAssignee>("work_template_assignees")
            .Find(x =>
                x.WorkId == _bindingWorkId &&
                x.AssigneeUserId == _assigneeId &&
                x.IsActive &&
                !x.IsDeleted)
            .ToListAsync(ct);
        HarnessAssert.Equal(2, bindings.Count, "Expected one active assignee binding per exact form version");
        var v1Binding = bindings.Single(x => x.WorkAssignmentId == v1AssignmentId);
        var v2Binding = bindings.Single(x => x.WorkAssignmentId == _v2AssignmentId);
        AssertAssignmentVersionIdentity(v1Binding, formId, formId, 1, v1Hash, "v1 assignee binding after v2");
        AssertAssignmentVersionIdentity(v2Binding, v2Id, formId, 2, v2Hash, "v2 assignee binding");

        var runtimeV2 = await RequireFormDetailAsync(v2Id, assigneeToken, "assignee exact v2 runtime read", ct);
        AssertRuntimeReadOnlyActions(runtimeV2, "assignee exact v2 actions");
        await AssertDesignSearchVisibilityAsync(assigneeToken, v2Id, false, "runtime v2 assignment", ct);
        await AssertHistoryDeniedAsync(assigneeToken, v2Id, "runtime v2 history", ct);

        var family = await _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
            .Find(x => x.FamilyId == formId && !x.IsDeleted)
            .SortBy(x => x.VersionNo)
            .ToListAsync(ct);
        HarnessAssert.Equal(2, family.Count, "Dynamic Form family cardinality mismatch");
        HarnessAssert.Equal(v1Hash, RequirePublishedSnapshot(family[0], "family v1").Hash, "Family v1 hash drifted");
        HarnessAssert.Equal(v2Hash, RequirePublishedSnapshot(family[1], "family v2").Hash, "Family v2 hash drifted");
        return new CaseObservation(
            "v1 assignment/binding retained exact ID/family/version/hash after v2 publish; the new assignment bound exact v2 identity/hash",
            $"v1Exact=true;v2Exact=true;bindings=2;familyVersions=2;hashesDistinct=true;v1Hash={v1Hash};v2Hash={v2Hash}");
    }

    private async Task<CaseObservation> RejectSchemaCreateAsync(
        int schemaCase,
        string expectedErrorCode,
        CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var anchorId = HarnessAssert.Required(_formId, "P1-BE-005 form");
        var forms = _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates");
        var countBefore = await forms.CountDocumentsAsync(FilterDefinition<DynamicFormTemplate>.Empty, cancellationToken: ct);
        var anchorBefore = await LoadFormDocumentAsync(anchorId, ct);
        var code = $"P2_DF_SCHEMA_{schemaCase:00}";

        var response = await _api.PostAsync(
            "api/dynamic-forms",
            BuildInvalidSchemaRequest(schemaCase, code),
            ownerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.BadRequest, $"DF-SCHEMA-{schemaCase:00}");
        AssertErrorCode(response, expectedErrorCode, $"DF-SCHEMA-{schemaCase:00}");

        var countAfter = await forms.CountDocumentsAsync(FilterDefinition<DynamicFormTemplate>.Empty, cancellationToken: ct);
        HarnessAssert.Equal(countBefore, countAfter, $"DF-SCHEMA-{schemaCase:00} changed Dynamic Form cardinality");
        var malformedWrites = await forms.CountDocumentsAsync(x => x.Code == code, cancellationToken: ct);
        HarnessAssert.Equal(0L, malformedWrites, $"DF-SCHEMA-{schemaCase:00} persisted an invalid form");
        AssertFormDocumentUnchanged(
            anchorBefore,
            await LoadFormDocumentAsync(anchorId, ct),
            $"DF-SCHEMA-{schemaCase:00} anchor");
        return new CaseObservation(
            $"DF-SCHEMA-{schemaCase:00} returned HTTP 400/{expectedErrorCode}; total count and anchor revision stayed unchanged",
            $"http=400;errorCode={expectedErrorCode};documentsDelta=0;anchorRevisionDelta=0");
    }

    private async Task<CaseObservation> RejectTypedLegacySchemaWrongKindsAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var jobs = _database.GetCollection<BsonDocument>("work_report_statistic_rebuild_jobs");
        var jobsBefore = await jobs.CountDocumentsAsync(
            FilterDefinition<BsonDocument>.Empty,
            cancellationToken: ct);
        HarnessAssert.Equal(0L, jobsBefore, "Typed legacy wrong-kind prerequisite expected zero statistic jobs");
        var baseline = await LoadRawFormCollectionSnapshotAsync(ct);
        var variants = new[] { "section.order", "field.colSpan", "block.tableMode" };

        for (var index = 0; index < variants.Length; index++)
        {
            var request = BuildMinimalFormRequest(
                $"P2_DF_SCHEMA_17_{index + 1}",
                $"P2 typed legacy wrong kind {index + 1}");
            var schema = ApiHarnessClient.RequiredObject(request["schema"], $"SCHEMA-17 {variants[index]} schema");
            var sections = ApiHarnessClient.RequiredArray(schema["sections"], $"SCHEMA-17 {variants[index]} sections");
            var fields = ApiHarnessClient.RequiredArray(schema["fields"], $"SCHEMA-17 {variants[index]} fields");
            var blocks = ApiHarnessClient.RequiredArray(schema["blocks"], $"SCHEMA-17 {variants[index]} blocks");
            switch (index)
            {
                case 0:
                    ApiHarnessClient.RequiredObject(sections[0], "SCHEMA-17 section")["order"] = "bad";
                    break;
                case 1:
                    ApiHarnessClient.RequiredObject(fields[0], "SCHEMA-17 field")["colSpan"] = "bad";
                    break;
                case 2:
                    blocks.Add(new JsonObject
                    {
                        ["blockId"] = "wrong_kind_block",
                        ["sectionId"] = "main",
                        ["dynamicExcelTemplateId"] = _dynamicExcelId,
                        ["tableMode"] = 123
                    });
                    break;
            }

            request.Remove("schema");
            request["sectionsJson"] = sections.ToJsonString();
            request["fieldsJson"] = fields.ToJsonString();
            request["blocksJson"] = blocks.ToJsonString();
            var response = await _api.PostAsync(
                "api/dynamic-forms",
                request,
                ownerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                response,
                HttpStatusCode.BadRequest,
                $"Dynamic Form typed legacy wrong kind {variants[index]}");
            AssertErrorCode(
                response,
                "DYNAMIC_FORM_JSON_KIND_INVALID",
                $"Dynamic Form typed legacy wrong kind {variants[index]}");
            HarnessAssert.Equal(
                "DYNAMIC_FORM_TYPED_SCHEMA_KIND_INVALID",
                ApiHarnessClient.FindStringRecursive(response.Json, "reason"),
                $"Typed legacy wrong-kind reason mismatch for {variants[index]}");
            AssertRawFormCollectionSnapshotUnchanged(
                baseline,
                await LoadRawFormCollectionSnapshotAsync(ct),
                $"Typed legacy wrong kind {variants[index]}");
            HarnessAssert.Equal(
                jobsBefore,
                await jobs.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct),
                $"Typed legacy wrong kind {variants[index]} queued a statistic job");
        }

        return new CaseObservation(
            "legacy JSON with wrong typed kinds for section order, field layout, and a known block property failed before any form or statistic-job write",
            "variants=section.order,field.colSpan,block.tableMode;http=400;error=DYNAMIC_FORM_JSON_KIND_INVALID;reason=DYNAMIC_FORM_TYPED_SCHEMA_KIND_INVALID;formWrites=0;jobs=0");
    }

    private JsonObject BuildInvalidSchemaRequest(int schemaCase, string code)
    {
        var request = BuildMinimalFormRequest(code, $"DF schema case {schemaCase:00}");
        var schema = ApiHarnessClient.RequiredObject(request["schema"], $"DF-SCHEMA-{schemaCase:00} schema");
        var sections = ApiHarnessClient.RequiredArray(schema["sections"], $"DF-SCHEMA-{schemaCase:00} sections");
        var fields = ApiHarnessClient.RequiredArray(schema["fields"], $"DF-SCHEMA-{schemaCase:00} fields");
        var blocks = ApiHarnessClient.RequiredArray(schema["blocks"], $"DF-SCHEMA-{schemaCase:00} blocks");
        var firstSection = ApiHarnessClient.RequiredObject(sections[0], $"DF-SCHEMA-{schemaCase:00} section");
        var firstField = ApiHarnessClient.RequiredObject(fields[0], $"DF-SCHEMA-{schemaCase:00} field");

        switch (schemaCase)
        {
            case 1:
                firstSection["id"] = " ";
                break;
            case 2:
                sections.Add(new JsonObject { ["id"] = "main", ["title"] = "Duplicate main", ["order"] = 2 });
                break;
            case 3:
                firstSection["title"] = " ";
                break;
            case 4:
                firstField["id"] = " ";
                break;
            case 5:
                fields.Add(BuildMinimalField("field_main", "answer_second", "main", "Second specific question"));
                break;
            case 6:
                firstField["sectionId"] = "missing_section";
                break;
            case 7:
                firstField["type"] = "unknownType";
                break;
            case 8:
                fields.Add(BuildMinimalField("field_second", "answer_main", "main", "Second specific question"));
                break;
            case 9:
                firstField["type"] = "singleSelect";
                firstField["options"] = new JsonArray
                {
                    new JsonObject { ["code"] = "DUP", ["label"] = "First" },
                    new JsonObject { ["code"] = "dup", ["label"] = "Second" }
                };
                break;
            case 10:
            {
                firstField["type"] = "singleSelect";
                var options = new JsonArray();
                for (var index = 0; index < 101; index++)
                    options.Add(new JsonObject { ["code"] = $"O{index:000}", ["label"] = $"Option {index:000}" });
                firstField["options"] = options;
                break;
            }
            case 11:
                firstField["type"] = "singleSelect";
                firstField["valueSource"] = new JsonObject { ["sourceType"] = "UNKNOWN_SOURCE" };
                break;
            case 12:
                fields.Clear();
                for (var index = 0; index < 201; index++)
                {
                    fields.Add(BuildMinimalField(
                        $"field_{index:000}",
                        $"answer_{index:000}",
                        "main",
                        $"Specific question {index:000}"));
                }
                break;
            case 13:
                for (var index = 0; index < 31; index++)
                    blocks.Add(new JsonObject());
                break;
            case 14:
                blocks.Add(BuildDynamicExcelBlock("block_one", "FIXED_GRID"));
                blocks.Add(BuildDynamicExcelBlock("block_two", "FIXED_GRID"));
                break;
            case 15:
                blocks.Add(BuildDynamicExcelBlock("block_incompatible", "APPEND_ROWS"));
                break;
            case 16:
                firstField["helpText"] = new string('x', 1_049_000);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(schemaCase), schemaCase, "Unknown DF schema case");
        }

        return request;
    }

    private JsonObject BuildDynamicExcelBlock(string blockId, string tableMode)
        => new()
        {
            ["blockId"] = blockId,
            ["sectionId"] = "main",
            ["dynamicExcelTemplateId"] = _dynamicExcelId,
            ["tableMode"] = tableMode
        };

    private static JsonObject BuildMinimalField(
        string id,
        string key,
        string sectionId,
        string name)
        => new()
        {
            ["id"] = id,
            ["sectionId"] = sectionId,
            ["key"] = key,
            ["name"] = name,
            ["type"] = "number",
            ["required"] = false,
            ["order"] = 1
        };

    private static JsonObject BuildMinimalFormRequest(string code, string name)
        => new()
        {
            ["code"] = code,
            ["name"] = name,
            ["description"] = "P2 Dynamic Form integration fixture",
            ["tagCodes"] = new JsonArray(),
            ["schemaVersion"] = 1,
            ["isActive"] = true,
            ["schema"] = new JsonObject
            {
                ["sections"] = new JsonArray
                {
                    new JsonObject { ["id"] = "main", ["title"] = "Main section", ["order"] = 1 }
                },
                ["fields"] = new JsonArray
                {
                    BuildMinimalField("field_main", "answer_main", "main", "Primary specific question")
                },
                ["blocks"] = new JsonArray()
            }
        };

    private async Task<CaseObservation> VerifyCrudSearchFilterPageAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var expected = new[]
        {
            (Code: "P2_DF_CRUD_PAGE_A", Name: "P2 CRUD Alpha"),
            (Code: "P2_DF_CRUD_PAGE_B", Name: "P2 CRUD Beta"),
            (Code: "P2_DF_CRUD_PAGE_C", Name: "P2 CRUD Gamma")
        };
        foreach (var fixture in expected)
            _ = await CreateDraftFormAsync(fixture.Code, fixture.Name, ownerToken, ct);

        var firstPage = await _api.PostAsync(
            "api/dynamic-forms/search",
            new
            {
                code = "P2_DF_CRUD_PAGE_",
                createdBy = OwnerUsername,
                isActive = true,
                isPublished = false,
                page = 0,
                pageSize = 1,
                sortField = "code",
                sortDirection = "asc"
            },
            ownerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(firstPage, HttpStatusCode.OK, "Dynamic Form CRUD first page");
        var firstBody = ApiHarnessClient.RequiredObject(firstPage.Json, "Dynamic Form CRUD first page");
        HarnessAssert.Equal(3, ApiHarnessClient.RequiredInt(firstBody, "totalRows"), "CRUD filtered total mismatch");
        HarnessAssert.Equal(0, ApiHarnessClient.RequiredInt(firstBody, "page"), "CRUD first page index mismatch");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(firstBody, "pageSize"), "CRUD first page size mismatch");
        AssertSingleSearchCode(firstBody, expected[0].Code, "CRUD first page");

        var secondPage = await _api.PostAsync(
            "api/dynamic-forms/search",
            new
            {
                code = "P2_DF_CRUD_PAGE_",
                createdBy = OwnerUsername,
                isActive = true,
                isPublished = false,
                page = 1,
                pageSize = 1,
                sortField = "code",
                sortDirection = "asc"
            },
            ownerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(secondPage, HttpStatusCode.OK, "Dynamic Form CRUD second page");
        var secondBody = ApiHarnessClient.RequiredObject(secondPage.Json, "Dynamic Form CRUD second page");
        HarnessAssert.Equal(3, ApiHarnessClient.RequiredInt(secondBody, "totalRows"), "CRUD second-page total mismatch");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(secondBody, "page"), "CRUD second page index mismatch");
        AssertSingleSearchCode(secondBody, expected[1].Code, "CRUD second page");

        var nameFilter = await _api.PostAsync(
            "api/dynamic-forms/search",
            new { name = "P2 CRUD Gamma", isPublished = false, page = 0, pageSize = 10 },
            ownerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(nameFilter, HttpStatusCode.OK, "Dynamic Form CRUD name filter");
        var nameBody = ApiHarnessClient.RequiredObject(nameFilter.Json, "Dynamic Form CRUD name filter");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(nameBody, "totalRows"), "CRUD name filter total mismatch");
        AssertSingleSearchCode(nameBody, expected[2].Code, "CRUD name filter");

        var literalRegex = await _api.PostAsync(
            "api/dynamic-forms/search",
            new { q = ".*", page = 0, pageSize = 100 },
            ownerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(literalRegex, HttpStatusCode.OK, "Dynamic Form literal regex search");
        var literalRegexBody = ApiHarnessClient.RequiredObject(literalRegex.Json, "Dynamic Form literal regex search");
        HarnessAssert.Equal(0, ApiHarnessClient.RequiredInt(literalRegexBody, "totalRows"), "Regex metacharacters matched all forms instead of literal text");
        HarnessAssert.Equal(
            0,
            ApiHarnessClient.RequiredArray(literalRegexBody["rows"], "Dynamic Form literal regex rows").Count,
            "Literal regex search returned unexpected rows");

        var oversized = await _api.PostAsync(
            "api/dynamic-forms/search",
            new { q = new string('x', 201), page = 0, pageSize = 20 },
            ownerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(oversized, HttpStatusCode.BadRequest, "Dynamic Form oversized search term");
        AssertErrorCode(oversized, "COMMON_VALIDATION_FAILED", "Dynamic Form oversized search term");
        HarnessAssert.Equal(
            "DYNAMIC_FORM_SEARCH_TERM_TOO_LONG",
            ApiHarnessClient.FindStringRecursive(oversized.Json, "reason"),
            "Dynamic Form oversized search reason mismatch");
        HarnessAssert.Equal(
            200,
            ApiHarnessClient.FindIntRecursive(oversized.Json, "maxLength") ?? -1,
            "Dynamic Form oversized search maxLength mismatch");
        return new CaseObservation(
            "owner search honored filters/pagination/order, treated regex metacharacters literally, and rejected a 201-character term at the 200-character bound",
            "filtered=3;page0=A;page1=B;nameFilter=C;pageSize=1;sort=code.asc;literalRegexRows=0;oversized=400;maxLength=200");
    }

    private async Task<CaseObservation> VerifyCrudSearchOffsetAndStableTiesAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        const string tiedName = "P2 CRUD stable tie";
        var tiedCodes = new[]
        {
            "P2_DF_CRUD_TIE_A",
            "P2_DF_CRUD_TIE_B",
            "P2_DF_CRUD_TIE_C"
        };
        var expectedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in tiedCodes)
        {
            var created = await CreateDraftFormAsync(code, tiedName, ownerToken, ct);
            expectedIds.Add(ApiHarnessClient.RequiredString(created, "id"));
        }

        var forms = _database.GetCollection<BsonDocument>("dynamic_form_templates");
        var tiedObjectIds = expectedIds
            .Select(id => (BsonValue)new BsonObjectId(ObjectId.Parse(id)))
            .ToArray();
        var tiedFilter = Builders<BsonDocument>.Filter.In("_id", tiedObjectIds);
        var tiedTimestamp = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var tieUpdate = await forms.UpdateManyAsync(
            tiedFilter,
            Builders<BsonDocument>.Update
                .Set("createdAtUtc", tiedTimestamp)
                .Set("updatedAtUtc", tiedTimestamp),
            cancellationToken: ct);
        HarnessAssert.Equal(3L, tieUpdate.MatchedCount, "Stable-tie fixture count mismatch");

        var allFormsSort = Builders<BsonDocument>.Sort.Ascending("_id");
        var beforeOverflow = await forms
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(allFormsSort)
            .ToListAsync(ct);
        var overflow = await _api.PostAsync(
            "api/dynamic-forms/search",
            new
            {
                code = "P2_DF_CRUD_TIE_",
                page = int.MaxValue,
                pageSize = 100,
                sortField = "updatedAtUtc",
                sortDirection = "desc"
            },
            ownerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(overflow, HttpStatusCode.BadRequest, "Dynamic Form search offset overflow");
        AssertErrorCode(overflow, "COMMON_VALIDATION_FAILED", "Dynamic Form search offset overflow");
        HarnessAssert.Equal(
            "DYNAMIC_FORM_SEARCH_OFFSET_TOO_LARGE",
            ApiHarnessClient.FindStringRecursive(overflow.Json, "reason"),
            "Dynamic Form search offset overflow reason mismatch");

        var afterOverflow = await forms
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(allFormsSort)
            .ToListAsync(ct);
        HarnessAssert.Equal(beforeOverflow.Count, afterOverflow.Count, "Offset overflow changed the Dynamic Form count");
        for (var index = 0; index < beforeOverflow.Count; index++)
        {
            HarnessAssert.True(
                beforeOverflow[index].Equals(afterOverflow[index]),
                $"Offset overflow changed Dynamic Form Mongo document index {index}");
        }

        var returnedIds = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < 3; page++)
        {
            var response = await _api.PostAsync(
                "api/dynamic-forms/search",
                new
                {
                    name = tiedName,
                    isPublished = false,
                    page,
                    pageSize = 1,
                    sortField = "updatedAtUtc",
                    sortDirection = "desc"
                },
                ownerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"Dynamic Form stable-tie page {page}");
            var body = ApiHarnessClient.RequiredObject(response.Json, $"Dynamic Form stable-tie page {page}");
            HarnessAssert.Equal(3, ApiHarnessClient.RequiredInt(body, "totalRows"), $"Stable-tie page {page} total mismatch");
            HarnessAssert.Equal(page, ApiHarnessClient.RequiredInt(body, "page"), $"Stable-tie page {page} index mismatch");
            HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(body, "pageSize"), $"Stable-tie page {page} size mismatch");
            var rows = ApiHarnessClient.RequiredArray(body["rows"], $"Dynamic Form stable-tie page {page} rows");
            HarnessAssert.Equal(1, rows.Count, $"Stable-tie page {page} row count mismatch");
            var row = ApiHarnessClient.RequiredObject(rows[0], $"Dynamic Form stable-tie page {page} row");
            var returnedId = ApiHarnessClient.RequiredString(row, "id");
            HarnessAssert.True(returnedIds.Add(returnedId), $"Stable-tie pagination repeated Dynamic Form {returnedId}");
        }

        HarnessAssert.True(
            returnedIds.SetEquals(expectedIds),
            "Stable-tie pagination omitted or substituted a Dynamic Form ID");
        return new CaseObservation(
            "an int.MaxValue page failed before any form write, while three rows sharing both timestamp sort keys paged into three unique IDs without omission",
            "overflow=400/COMMON_VALIDATION_FAILED/DYNAMIC_FORM_SEARCH_OFFSET_TOO_LARGE;mongoWrites=0;tiedRows=3;pages=0..2;uniqueIds=3;omissions=0");
    }

    private async Task<CaseObservation> RejectMalformedDynamicFormAndExcelIdsAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var anchorId = _p2FormIds["P2_DF_CRUD_PAGE_A"];
        const string malformedFormId = "malformed-form-template-id-needle";
        const string malformedExcelId = "malformed-dynamic-excel-id-needle";
        var baseline = await LoadRawFormCollectionSnapshotAsync(ct);

        var malformedGet = await _api.GetAsync(
            $"api/dynamic-forms/{malformedFormId}",
            ownerToken,
            ct: ct);
        AssertMalformedIdResponse(
            malformedGet,
            "DYNAMIC_FORM_TEMPLATE_ID_INVALID",
            malformedFormId,
            "malformed Dynamic Form route ID");

        var malformedWrap = await _api.PostAsync(
            "api/dynamic-forms/wrap-dynamic-excel",
            new
            {
                dynamicExcelTemplateId = malformedExcelId,
                code = (string?)null,
                name = (string?)null,
                description = (string?)null,
                tagCodes = Array.Empty<string>()
            },
            ownerToken,
            ct: ct);
        AssertMalformedIdResponse(
            malformedWrap,
            "DYNAMIC_EXCEL_TEMPLATE_ID_INVALID",
            malformedExcelId,
            "malformed Dynamic Excel wrap ID");

        var malformedImport = await _api.PostAsync(
            $"api/dynamic-forms/{anchorId}/blocks/import-dynamic-excel",
            new
            {
                dynamicExcelTemplateId = malformedExcelId,
                sectionId = "main",
                expectedRevision = 1
            },
            ownerToken,
            ct: ct);
        AssertMalformedIdResponse(
            malformedImport,
            "DYNAMIC_EXCEL_TEMPLATE_ID_INVALID",
            malformedExcelId,
            "malformed Dynamic Excel import ID");

        var malformedCreate = BuildMinimalFormRequest(
            "P2_DF_CRUD_BAD_EXCEL_ID",
            "P2 malformed Dynamic Excel block ID");
        var createSchema = ApiHarnessClient.RequiredObject(malformedCreate["schema"], "malformed create schema");
        var createBlocks = ApiHarnessClient.RequiredArray(createSchema["blocks"], "malformed create blocks");
        var malformedBlock = BuildDynamicExcelBlock("malformed_excel_block", "FIXED_GRID");
        malformedBlock["dynamicExcelTemplateId"] = malformedExcelId;
        createBlocks.Add(malformedBlock);
        var malformedCreateResponse = await _api.PostAsync(
            "api/dynamic-forms",
            malformedCreate,
            ownerToken,
            ct: ct);
        AssertMalformedIdResponse(
            malformedCreateResponse,
            "DYNAMIC_EXCEL_TEMPLATE_ID_INVALID",
            malformedExcelId,
            "malformed Dynamic Excel create-schema ID");

        AssertRawFormCollectionSnapshotUnchanged(
            baseline,
            await LoadRawFormCollectionSnapshotAsync(ct),
            "Malformed Dynamic Form/Dynamic Excel identifiers");
        return new CaseObservation(
            "malformed Dynamic Form route and Dynamic Excel wrap/import/schema identifiers returned generic validation errors without echoing raw IDs or writing a form",
            "formRoute=400/DYNAMIC_FORM_TEMPLATE_ID_INVALID;excelWrapImportCreate=400/DYNAMIC_EXCEL_TEMPLATE_ID_INVALID;rawIdEcho=false;formWrites=0");
    }

    private static void AssertMalformedIdResponse(
        ApiHarnessResponse response,
        string expectedReason,
        string rawIdentifier,
        string context)
    {
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.BadRequest, context);
        AssertErrorCode(response, "COMMON_VALIDATION_FAILED", context);
        HarnessAssert.Equal(
            expectedReason,
            ApiHarnessClient.FindStringRecursive(response.Json, "reason"),
            $"{context} reason mismatch");
        HarnessAssert.True(
            !response.Body.Contains(rawIdentifier, StringComparison.OrdinalIgnoreCase),
            $"{context} echoed the raw malformed identifier");
    }

    private static void AssertSingleSearchCode(JsonObject body, string expectedCode, string context)
    {
        var rows = ApiHarnessClient.RequiredArray(body["rows"], $"{context} rows");
        HarnessAssert.Equal(1, rows.Count, $"{context} row count mismatch");
        var row = ApiHarnessClient.RequiredObject(rows[0], $"{context} row");
        HarnessAssert.Equal(expectedCode, ApiHarnessClient.RequiredString(row, "code"), $"{context} code mismatch");
    }

    private async Task<CaseObservation> VerifyCrudDetailReopenAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var formId = _p2FormIds["P2_DF_CRUD_PAGE_A"];
        var first = await RequireFormDetailAsync(formId, ownerToken, "CRUD detail first open", ct);
        var reopened = await RequireFormDetailAsync(formId, ownerToken, "CRUD detail reopen", ct);
        HarnessAssert.Equal(
            ApiHarnessClient.RequiredString(first, "code"),
            ApiHarnessClient.RequiredString(reopened, "code"),
            "Reopened CRUD detail code drifted");
        HarnessAssert.Equal(
            ApiHarnessClient.RequiredInt(first, "revision"),
            ApiHarnessClient.RequiredInt(reopened, "revision"),
            "Reopened CRUD detail revision drifted");
        HarnessAssert.True(!ApiHarnessClient.RequiredBool(reopened, "isPublished"), "Reopened CRUD form is no longer a draft");
        AssertDraftOwnerActions(reopened, "CRUD reopened draft actions");
        var schema = ApiHarnessClient.RequiredObject(reopened["schema"], "CRUD reopened typed schema");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredArray(schema["sections"], "CRUD reopened sections").Count, "CRUD reopened section count mismatch");
        HarnessAssert.True(ApiHarnessClient.RequiredArray(schema["fields"], "CRUD reopened fields").Count > 0, "CRUD reopened fields are empty");
        var persisted = await LoadFormDocumentAsync(formId, ct);
        HarnessAssert.Equal(ApiHarnessClient.RequiredInt(reopened, "revision"), persisted.Revision, "CRUD reopened Mongo revision mismatch");
        return new CaseObservation(
            "the saved draft reopened by deep link with the same identity/revision, complete typed schema, and draft capabilities",
            "opens=2;identityStable=true;revisionStable=true;typedSchema=true;draftActions=true");
    }

    private async Task<CaseObservation> VerifyCrudSoftDeleteAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var created = await CreateDraftFormAsync("P2_DF_CRUD_SOFT_DELETE", "P2 CRUD soft delete", ownerToken, ct);
        var formId = ApiHarnessClient.RequiredString(created, "id");
        var forms = _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates");
        var activeBefore = await forms.CountDocumentsAsync(x => !x.IsDeleted, cancellationToken: ct);

        var deleted = await _api.DeleteAsync($"api/dynamic-forms/{formId}?expectedRevision=1", ownerToken, ct: ct);
        AssertDeleteSuccess(deleted, "owner Dynamic Form soft delete");
        var persisted = await forms.Find(x => x.Id == formId).SingleAsync(ct);
        HarnessAssert.True(persisted.IsDeleted, "Dynamic Form soft delete flag was not persisted");
        HarnessAssert.Equal(2, persisted.Revision, "Dynamic Form soft delete revision mismatch");
        HarnessAssert.Equal(_ownerId, persisted.DeletedByUserId, "Dynamic Form soft delete actor mismatch");
        HarnessAssert.True(persisted.DeletedAtUtc.HasValue, "Dynamic Form soft delete timestamp is missing");
        var activeAfter = await forms.CountDocumentsAsync(x => !x.IsDeleted, cancellationToken: ct);
        HarnessAssert.Equal(activeBefore - 1, activeAfter, "Dynamic Form soft delete active-count delta mismatch");

        var reopen = await _api.GetAsync($"api/dynamic-forms/{formId}", ownerToken, ct: ct);
        ApiHarnessClient.ExpectStatus(reopen, HttpStatusCode.NotFound, "soft-deleted Dynamic Form detail");
        AssertErrorCode(reopen, "DYNAMIC_FORM_TEMPLATE_NOT_FOUND", "soft-deleted Dynamic Form detail");
        await AssertDesignSearchVisibilityAsync(ownerToken, formId, false, "soft-deleted owner", ct);
        return new CaseObservation(
            "draft DELETE soft-deleted exactly one document, advanced revision once, and hid it from detail/search",
            "http=200;isDeleted=true;revision=2;activeDelta=-1;detail=404;search=false");
    }

    private async Task<CaseObservation> VerifyCrudLinkedDeleteGuardAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var created = await CreateDraftFormAsync("P2_DF_CRUD_LINKED_DELETE", "P2 CRUD linked delete guard", ownerToken, ct);
        var formId = ApiHarnessClient.RequiredString(created, "id");
        var before = await LoadFormDocumentAsync(formId, ct);
        var rawAssignments = _database.GetCollection<BsonDocument>("work_assignments");
        var linkId = ObjectId.GenerateNewId().ToString();
        await rawAssignments.InsertOneAsync(
            new BsonDocument
            {
                ["_id"] = linkId,
                ["dynamicFormTemplateId"] = formId,
                ["isDeleted"] = false
            },
            cancellationToken: ct);

        try
        {
            var response = await _api.DeleteAsync(
                $"api/dynamic-forms/{formId}?expectedRevision={before.Revision}",
                ownerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Conflict, "linked draft Dynamic Form delete");
            AssertErrorCode(response, "DYNAMIC_FORM_IN_USE_BY_ASSIGNMENT", "linked draft Dynamic Form delete");
            AssertFormDocumentUnchanged(before, await LoadFormDocumentAsync(formId, ct), "linked draft delete guard");
        }
        finally
        {
            await rawAssignments.DeleteOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", linkId),
                cancellationToken: ct);
        }

        return new CaseObservation(
            "a runtime-linked draft returned 409/in-use and retained its exact Mongo document/revision",
            "http=409;errorCode=DYNAMIC_FORM_IN_USE_BY_ASSIGNMENT;writes=0;revisionDelta=0");
    }

    private async Task<CaseObservation> VerifyDraftFlowDeleteGuardAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var forms = _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates");
        var flows = _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates");
        var versions = _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions");
        string? formId = null;
        string? flowId = null;
        try
        {
            var formResponse = await _api.PostAsync(
                "api/dynamic-forms",
                BuildMinimalFormRequest("P2_DF_DRAFT_FLOW_DELETE", "P2 draft flow delete guard"),
                ownerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(formResponse, HttpStatusCode.OK, "create draft form for flow delete guard");
            formId = ApiHarnessClient.RequiredString(formResponse.Json, "id");

            // Preserve a realistic legacy/pre-contract draft Flow reference. New API
            // writes are covered separately and must reject draft-form binding.
            flowId = ObjectId.GenerateNewId().ToString();
            var flowVersionId = ObjectId.GenerateNewId().ToString();
            var flowPayloadJson = BuildFlowPayload(formId).ToJsonString();
            var fixedAt = new DateTime(2026, 7, 22, 4, 0, 0, DateTimeKind.Utc);
            await flows.InsertOneAsync(
                new DynamicFlowTemplate
                {
                    Id = flowId,
                    Code = "P2_DF_DRAFT_FLOW_DELETE",
                    Name = "P2 draft flow delete guard",
                    Description = "legacy draft flow references draft Dynamic Form",
                    RootDynamicFormTemplateId = formId,
                    Status = DynamicFlowTemplateStatuses.Draft,
                    CurrentVersionId = flowVersionId,
                    CurrentVersionNo = 1,
                    CreatedByUserId = _ownerId,
                    UpdatedByUserId = _ownerId,
                    CreatedAtUtc = fixedAt,
                    UpdatedAtUtc = fixedAt,
                    IsDeleted = false
                },
                cancellationToken: ct);
            await versions.InsertOneAsync(
                new DynamicFlowTemplateVersion
                {
                    Id = flowVersionId,
                    TemplateId = flowId,
                    RootDynamicFormTemplateId = formId,
                    VersionNo = 1,
                    Status = DynamicFlowTemplateVersionStatuses.Draft,
                    DraftRevision = 1,
                    PayloadJson = flowPayloadJson,
                    PayloadHash = ComputeSha256(flowPayloadJson),
                    CreatedByUserId = _ownerId,
                    UpdatedByUserId = _ownerId,
                    CreatedAtUtc = fixedAt,
                    UpdatedAtUtc = fixedAt,
                    IsDeleted = false
                },
                cancellationToken: ct);

            var beforeForm = await LoadFormDocumentAsync(formId, ct);
            var beforeFlow = await flows.Find(x => x.Id == flowId).SingleAsync(ct);
            var beforeVersions = await versions.Find(x => x.TemplateId == flowId).ToListAsync(ct);
            HarnessAssert.Equal(1, beforeVersions.Count, "Draft flow delete guard version prerequisite mismatch");

            var response = await _api.DeleteAsync(
                $"api/dynamic-forms/{formId}?expectedRevision={beforeForm.Revision}",
                ownerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Conflict, "delete draft form referenced by draft flow");
            AssertErrorCode(response, "DYNAMIC_FORM_IN_USE_BY_FLOW", "delete draft form referenced by draft flow");
            AssertFormDocumentUnchanged(
                beforeForm,
                await LoadFormDocumentAsync(formId, ct),
                "Draft-flow Dynamic Form delete guard");
            var afterFlow = await flows.Find(x => x.Id == flowId).SingleAsync(ct);
            HarnessAssert.True(
                beforeFlow.ToBsonDocument().Equals(afterFlow.ToBsonDocument()),
                "Rejected form delete mutated the referencing Dynamic Flow template");
            var afterVersions = await versions.Find(x => x.TemplateId == flowId).ToListAsync(ct);
            HarnessAssert.Equal(beforeVersions.Count, afterVersions.Count, "Rejected form delete changed flow version cardinality");
            HarnessAssert.True(
                beforeVersions[0].ToBsonDocument().Equals(afterVersions[0].ToBsonDocument()),
                "Rejected form delete mutated the referencing Dynamic Flow version");
        }
        finally
        {
            if (flowId is not null)
            {
                await versions.DeleteManyAsync(x => x.TemplateId == flowId, ct);
                await flows.DeleteOneAsync(x => x.Id == flowId, ct);
            }

            if (formId is not null)
                await forms.DeleteOneAsync(x => x.Id == formId, ct);
        }

        return new CaseObservation(
            "a legacy draft Dynamic Form reference already persisted in a draft Dynamic Flow blocked deletion; form, flow, and version documents stayed byte-equivalent",
            "legacyFixture=true;http=409;errorCode=DYNAMIC_FORM_IN_USE_BY_FLOW;formWrites=0;flowWrites=0;versionWrites=0;fixtureCleanup=true");
    }

    private async Task<CaseObservation> VerifyFlowMappingAliasDeleteGuardsAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var forms = _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates");
        var flows = _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates");
        var versions = _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions");
        string? formId = null;
        try
        {
            var formResponse = await _api.PostAsync(
                "api/dynamic-forms",
                BuildMinimalFormRequest("P2_DF_MAPPING_ALIAS_DELETE", "P2 mapping alias delete guard"),
                ownerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(formResponse, HttpStatusCode.OK, "create draft form for mapping-alias delete guard");
            formId = ApiHarnessClient.RequiredString(formResponse.Json, "id");

            var aliases = new[]
            {
                "sourceDynamicFormTemplateId",
                "sourceFormTemplateId",
                "targetDynamicFormTemplateId",
                "targetFormTemplateId"
            };
            for (var index = 0; index < aliases.Length; index++)
            {
                var alias = aliases[index];
                var flowId = ObjectId.GenerateNewId().ToString();
                var versionId = ObjectId.GenerateNewId().ToString();
                var fixedAt = new DateTime(2026, 7, 22, 4, 10 + index, 0, DateTimeKind.Utc);
                var mapping = new JsonObject { [alias] = formId };
                var mappingRules = new JsonArray { mapping };
                var payload = new JsonObject { ["mappingRules"] = mappingRules };
                var payloadJson = payload.ToJsonString();
                try
                {
                    await flows.InsertOneAsync(
                        new DynamicFlowTemplate
                        {
                            Id = flowId,
                            Code = $"P2_DF_MAPPING_ALIAS_{index + 1}",
                            Name = $"P2 mapping-only alias {alias}",
                            Description = "mapping-only legacy flow fixture with no root/form-node/step reference",
                            RootDynamicFormTemplateId = null,
                            Status = DynamicFlowTemplateStatuses.Draft,
                            CurrentVersionId = versionId,
                            CurrentVersionNo = 1,
                            CreatedByUserId = _ownerId,
                            UpdatedByUserId = _ownerId,
                            CreatedAtUtc = fixedAt,
                            UpdatedAtUtc = fixedAt,
                            IsDeleted = false
                        },
                        cancellationToken: ct);
                    await versions.InsertOneAsync(
                        new DynamicFlowTemplateVersion
                        {
                            Id = versionId,
                            TemplateId = flowId,
                            RootDynamicFormTemplateId = null,
                            VersionNo = 1,
                            Status = DynamicFlowTemplateVersionStatuses.Draft,
                            DraftRevision = 1,
                            PayloadJson = payloadJson,
                            PayloadHash = ComputeSha256(payloadJson),
                            CreatedByUserId = _ownerId,
                            UpdatedByUserId = _ownerId,
                            CreatedAtUtc = fixedAt,
                            UpdatedAtUtc = fixedAt,
                            IsDeleted = false
                        },
                        cancellationToken: ct);

                    var beforeForm = await LoadFormDocumentAsync(formId, ct);
                    var beforeFlow = await flows.Find(x => x.Id == flowId).SingleAsync(ct);
                    var beforeVersion = await versions.Find(x => x.Id == versionId).SingleAsync(ct);
                    var response = await _api.DeleteAsync(
                        $"api/dynamic-forms/{formId}?expectedRevision={beforeForm.Revision}",
                        ownerToken,
                        ct: ct);
                    ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Conflict, $"delete form referenced by mapping alias {alias}");
                    AssertErrorCode(response, "DYNAMIC_FORM_IN_USE_BY_FLOW", $"delete form referenced by mapping alias {alias}");
                    AssertFormDocumentUnchanged(
                        beforeForm,
                        await LoadFormDocumentAsync(formId, ct),
                        $"mapping alias {alias} delete guard");
                    var afterFlow = await flows.Find(x => x.Id == flowId).SingleAsync(ct);
                    var afterVersion = await versions.Find(x => x.Id == versionId).SingleAsync(ct);
                    HarnessAssert.True(
                        beforeFlow.ToBsonDocument().Equals(afterFlow.ToBsonDocument()),
                        $"mapping alias {alias} delete guard mutated the Flow template");
                    HarnessAssert.True(
                        beforeVersion.ToBsonDocument().Equals(afterVersion.ToBsonDocument()),
                        $"mapping alias {alias} delete guard mutated the Flow version");
                }
                finally
                {
                    await versions.DeleteManyAsync(x => x.TemplateId == flowId, ct);
                    await flows.DeleteOneAsync(x => x.Id == flowId, ct);
                }
            }
        }
        finally
        {
            if (formId is not null)
                await forms.DeleteOneAsync(x => x.Id == formId, ct);
        }

        return new CaseObservation(
            "each source/target Dynamic Form mapping alias independently blocked deletion from a mapping-only Flow payload without relying on root, form-node, or step references",
            "aliases=4;mappingOnly=true;rootRefs=0;formNodeRefs=0;stepRefs=0;http409=4;errorCode=DYNAMIC_FORM_IN_USE_BY_FLOW;formWrites=0;flowWrites=0;versionWrites=0;fixtureCleanup=true");
    }

    private async Task<CaseObservation> UpdatePublishedStatisticsOnlyAsync(CancellationToken ct)
    {
        const string commandId = "p2-df-version-001-statistics-only";
        var adminToken = HarnessAssert.Required(_adminToken, "P1-BE-002 admin token");
        var adminId = HarnessAssert.Required(_adminId, "P1-BE-002 admin id");
        var v2Id = HarnessAssert.Required(_v2FormId, "P2-DF-LC-005 v2 form");
        var before = await LoadFormDocumentAsync(v2Id, ct);
        var snapshotBefore = RequirePublishedSnapshot(before, "statistics-only v2 before");
        var fields = JsonNode.Parse(before.FieldsJson) as JsonArray
                     ?? throw new InvalidOperationException("Published v2 FieldsJson must be an array");
        var target = fields.OfType<JsonObject>().FirstOrDefault(field =>
                         !string.Equals(field["type"]?.GetValue<string>(), "richText", StringComparison.Ordinal) &&
                         field["isStatistic"]?.GetValue<bool>() != true)
                     ?? throw new InvalidOperationException("Published v2 has no disabled statistic-eligible field");
        var targetId = ApiHarnessClient.RequiredString(target, "id");
        var configBefore = await RequireCanonicalStatisticConfigAsync(
            v2Id,
            adminToken,
            "statistics-only v2 canonical config before",
            ct);
        var isolationBefore = await CaptureP2StatisticIsolationCountsAsync(ct);
        var receipts = _database.GetCollection<BsonDocument>("stat_config_command_receipts");
        var receiptCountBefore = await receipts.CountDocumentsAsync(
            FilterDefinition<BsonDocument>.Empty,
            cancellationToken: ct);

        var response = await _api.PatchAsync(
            $"api/dynamic-forms/{v2Id}/statistics",
            BuildCanonicalStatisticEnvelope(
                commandId,
                configBefore,
                new JsonObject
                {
                    ["fields"] = new JsonArray(
                        BuildCanonicalFieldStatisticMutation(targetId, isStatistic: true))
                }),
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, "published statistics-only update");
        var result = RequireCanonicalStatisticMutationResult(
            response,
            configBefore,
            v2Id,
            "published statistics-only update");

        var after = await LoadFormDocumentAsync(v2Id, ct);
        HarnessAssert.Equal(before.Revision + 1, after.Revision, "Statistics-only owner-template revision mismatch");
        HarnessAssert.Equal(
            ApiHarnessClient.RequiredInt(result, "revision"),
            checked((int)after.StatisticConfigRevision),
            "Statistics-only config revision differs between API and Mongo");
        HarnessAssert.Equal(
            ApiHarnessClient.RequiredString(result, "configHash"),
            after.StatisticConfigHash,
            "Statistics-only config hash differs between API and Mongo");
        HarnessAssert.Equal(adminId, after.StatisticConfigUpdatedByUserId, "Statistics-only Mongo actor mismatch");
        HarnessAssert.True(after.StatisticConfigUpdatedAtUtc.HasValue, "Statistics-only Mongo timestamp is missing");
        HarnessAssert.True(
            !string.IsNullOrWhiteSpace(after.StatisticConfigUpdateMonthKey),
            "Statistics-only canonical month key is missing");
        HarnessAssert.True(!string.Equals(before.FieldsJson, after.FieldsJson, StringComparison.Ordinal), "Statistics-only field config did not change");
        var afterFields = JsonNode.Parse(after.FieldsJson) as JsonArray
                          ?? throw new InvalidOperationException("Statistics-only FieldsJson after must be an array");
        var afterTarget = afterFields.OfType<JsonObject>().Single(field =>
            string.Equals(field["id"]?.GetValue<string>(), targetId, StringComparison.Ordinal));
        HarnessAssert.True(
            afterTarget["isStatistic"]?.GetValue<bool>() == true,
            "Statistics-only target was not enabled");
        var statistic = ApiHarnessClient.RequiredObject(
            afterTarget["statistic"],
            "statistics-only target settings");
        HarnessAssert.Equal(
            "NONE",
            ApiHarnessClient.RequiredString(statistic, "bucketMode"),
            "Statistics-only target bucket mode mismatch");
        var operations = ApiHarnessClient.RequiredArray(
            statistic["aggregateOps"],
            "statistics-only target operations");
        HarnessAssert.Equal(1, operations.Count, "Statistics-only target operation count mismatch");
        HarnessAssert.Equal("COUNT", operations[0]!.GetValue<string>(), "Statistics-only target operation mismatch");

        var snapshotAfter = RequirePublishedSnapshot(after, "statistics-only v2 after");
        HarnessAssert.Equal(snapshotBefore.Json, snapshotAfter.Json, "Statistics-only update changed immutable published snapshot JSON");
        HarnessAssert.Equal(snapshotBefore.Hash, snapshotAfter.Hash, "Statistics-only update changed immutable published snapshot hash");
        var receiptCountAfter = await receipts.CountDocumentsAsync(
            FilterDefinition<BsonDocument>.Empty,
            cancellationToken: ct);
        HarnessAssert.Equal(receiptCountBefore + 1, receiptCountAfter, "Statistics-only receipt cardinality mismatch");
        await AssertCanonicalStatisticReceiptAsync(result, v2Id, commandId, adminId, ct);
        AssertP2StatisticIsolationUnchanged(
            isolationBefore,
            await CaptureP2StatisticIsolationCountsAsync(ct),
            "published statistics-only update");
        return new CaseObservation(
            "admin changed only canonical field statistic config on published v2; config/template revisions and one receipt advanced while rebuild/outbox/results and immutable snapshot/hash stayed exact",
            "http=200;statisticsChanged=true;configRevisionDelta=1;templateRevisionDelta=1;receiptWrites=1;jobOutboxResultWrites=0;snapshotStable=true;hashStable=true");
    }

    private async Task<CaseObservation> RejectStatisticsStructuralDriftAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var adminToken = HarnessAssert.Required(_adminToken, "P1-BE-002 admin token");
        var forms = _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates");

        string? formId = null;
        try
        {
            var created = await _api.PostAsync(
                "api/dynamic-forms",
                new JsonObject
                {
                    ["code"] = "P2_DF_STAT_STRUCTURAL_VARIANTS",
                    ["name"] = "P2 statistics structural variants",
                    ["description"] = "Five independent GAP-DF-03 structural drift variants",
                    ["tagCodes"] = new JsonArray(),
                    ["schemaVersion"] = 1,
                    ["isActive"] = true,
                    ["schema"] = new JsonObject
                    {
                        ["sections"] = new JsonArray
                        {
                            new JsonObject { ["id"] = "main", ["title"] = "Main section", ["order"] = 1 },
                            new JsonObject { ["id"] = "secondary", ["title"] = "Secondary section", ["order"] = 2 }
                        },
                        ["fields"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = "field_number",
                                ["sectionId"] = "main",
                                ["key"] = "number_value",
                                ["name"] = "Numeric answer",
                                ["type"] = "number",
                                ["required"] = false,
                                ["order"] = 1
                            },
                            new JsonObject
                            {
                                ["id"] = "field_choice",
                                ["sectionId"] = "main",
                                ["key"] = "choice_value",
                                ["name"] = "Choice answer",
                                ["type"] = "singleSelect",
                                ["required"] = false,
                                ["order"] = 2,
                                ["valueSource"] = new JsonObject
                                {
                                    ["sourceType"] = "FIXED_ENUM",
                                    ["options"] = new JsonArray
                                    {
                                        new JsonObject { ["code"] = "A", ["label"] = "Option A" },
                                        new JsonObject { ["code"] = "B", ["label"] = "Option B" }
                                    }
                                }
                            }
                        },
                        ["blocks"] = new JsonArray()
                    }
                },
                ownerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(created, HttpStatusCode.OK, "create GAP-DF-03 statistics fixture");
            formId = ApiHarnessClient.RequiredString(created.Json, "id");
            var published = await _api.PostAsync(
                $"api/dynamic-forms/{formId}/publish",
                new { expectedRevision = ApiHarnessClient.RequiredInt(created.Json, "revision") },
                ownerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(published, HttpStatusCode.OK, "publish GAP-DF-03 statistics fixture");
            var byteBaseline = await LoadFormDocumentAsync(formId, ct);
            _ = RequirePublishedSnapshot(byteBaseline, "GAP-DF-03 statistics fixture baseline");
            var configBaseline = await RequireCanonicalStatisticConfigAsync(
                formId,
                adminToken,
                "GAP-DF-03 canonical config baseline",
                ct);
            var isolationBaseline = await CaptureP2StatisticIsolationCountsAsync(ct);
            var receipts = _database.GetCollection<BsonDocument>("stat_config_command_receipts");
            var receiptBaseline = await receipts.CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty,
                cancellationToken: ct);
            var variants = new (string Name, string FieldId, string Property, JsonNode Value)[]
            {
                ("name", "field_number", "name", JsonValue.Create("Renamed numeric answer")!),
                ("type", "field_number", "type", JsonValue.Create("boolean")!),
                ("options", "field_choice", "options", new JsonArray(
                    new JsonObject { ["code"] = "A", ["label"] = "Option A changed" })),
                ("layout", "field_number", "order", JsonValue.Create(20)!),
                ("section", "field_number", "sectionId", JsonValue.Create("secondary")!)
            };

            foreach (var variant in variants)
            {
                var before = await LoadFormDocumentAsync(formId, ct);
                AssertFormDocumentUnchanged(byteBaseline, before, $"{variant.Name} variant baseline reload");
                var configBefore = await RequireCanonicalStatisticConfigAsync(
                    formId,
                    adminToken,
                    $"statistics {variant.Name} structural config before",
                    ct);
                HarnessAssert.Equal(
                    ApiHarnessClient.RequiredInt(configBaseline, "revision"),
                    ApiHarnessClient.RequiredInt(configBefore, "revision"),
                    $"statistics {variant.Name} structural config revision baseline drifted");
                HarnessAssert.Equal(
                    ApiHarnessClient.RequiredString(configBaseline, "configHash"),
                    ApiHarnessClient.RequiredString(configBefore, "configHash"),
                    $"statistics {variant.Name} structural config hash baseline drifted");
                var mutation = BuildCanonicalFieldStatisticMutation(variant.FieldId, isStatistic: true);
                mutation[variant.Property] = variant.Value.DeepClone();
                var response = await _api.PatchAsync(
                    $"api/dynamic-forms/{formId}/statistics",
                    BuildCanonicalStatisticEnvelope(
                        $"p2-df-version-002-{variant.Name}",
                        configBefore,
                        new JsonObject
                        {
                            ["fields"] = new JsonArray(mutation)
                        }),
                    adminToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(response, HttpStatusCode.BadRequest, $"statistics {variant.Name} structural drift");
                AssertErrorCode(
                    response,
                    "DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID",
                    $"statistics {variant.Name} structural drift");
                var details = ApiHarnessClient.RequiredObject(
                    ApiHarnessClient.RequiredObject(response.Json, $"statistics {variant.Name} structural error")["details"],
                    $"statistics {variant.Name} structural details");
                HarnessAssert.Equal(
                    $"$.payload.fields[0].{variant.Property}",
                    ApiHarnessClient.RequiredString(details, "path"),
                    $"statistics {variant.Name} structural path mismatch");
                HarnessAssert.Equal(
                    "SCHEMA_MISMATCH",
                    ApiHarnessClient.RequiredString(details, "reason"),
                    $"statistics {variant.Name} structural reason mismatch");
                AssertFormDocumentUnchanged(
                    byteBaseline,
                    await LoadFormDocumentAsync(formId, ct),
                    $"statistics {variant.Name} structural drift");
                var configAfter = await RequireCanonicalStatisticConfigAsync(
                    formId,
                    adminToken,
                    $"statistics {variant.Name} structural config after",
                    ct);
                HarnessAssert.Equal(
                    ApiHarnessClient.RequiredInt(configBaseline, "revision"),
                    ApiHarnessClient.RequiredInt(configAfter, "revision"),
                    $"statistics {variant.Name} structural rejection changed config revision");
                HarnessAssert.Equal(
                    ApiHarnessClient.RequiredString(configBaseline, "configHash"),
                    ApiHarnessClient.RequiredString(configAfter, "configHash"),
                    $"statistics {variant.Name} structural rejection changed config hash");
                HarnessAssert.Equal(
                    receiptBaseline,
                    await receipts.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct),
                    $"statistics {variant.Name} structural rejection wrote a receipt");
                AssertP2StatisticIsolationUnchanged(
                    isolationBaseline,
                    await CaptureP2StatisticIsolationCountsAsync(ct),
                    $"statistics {variant.Name} structural drift");
            }
        }
        finally
        {
            if (formId is not null)
                await forms.DeleteOneAsync(x => x.Id == formId, ct);
        }

        return new CaseObservation(
            "five independent canonical statistics PATCH variants (name/type/options/order/sectionId) each returned the exact strict-schema path without template, receipt, rebuild, outbox or result writes",
            "variants=5;name=400;type=400;options=400;layout=400;section=400;errorCode=DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID;reason=SCHEMA_MISMATCH;sameConfigBaseline=true;templateWrites=0;receiptWrites=0;jobOutboxResultWrites=0;fixtureCleanup=true");
    }

    private async Task<CaseObservation> RejectDynamicExcelMetadataDriftAsync(CancellationToken ct)
    {
        const string commandId = "p2-df-version-006-dynamic-excel-metadata-drift";
        var adminToken = HarnessAssert.Required(_adminToken, "P1-BE-002 admin token");
        var formId = HarnessAssert.Required(_wrappedPublishedFormId, "P2-DF-LC-002 published Dynamic Excel wrapper");
        var before = await LoadFormDocumentAsync(formId, ct);
        var snapshotBefore = RequirePublishedSnapshot(before, "Dynamic Excel metadata-drift form before");
        var persistedBlocks = JsonNode.Parse(before.BlocksJson) as JsonArray
                              ?? throw new InvalidOperationException("Published Dynamic Excel wrapper BlocksJson must be an array");
        var persistedPrimary = persistedBlocks.OfType<JsonObject>().FirstOrDefault()
                               ?? throw new InvalidOperationException("Published Dynamic Excel wrapper must contain a block");
        HarnessAssert.Equal(
            _dynamicExcelId,
            ApiHarnessClient.RequiredString(persistedPrimary, "dynamicExcelTemplateId"),
            "Published wrapper Dynamic Excel reference mismatch");
        HarnessAssert.Equal(
            "NUMBER",
            ApiHarnessClient.RequiredString(persistedPrimary, "defaultDataType"),
            "Published wrapper type-metadata prerequisite mismatch");
        var configBefore = await RequireCanonicalStatisticConfigAsync(
            formId,
            adminToken,
            "Dynamic Excel metadata-drift canonical config before",
            ct);
        var isolationBefore = await CaptureP2StatisticIsolationCountsAsync(ct);
        var receipts = _database.GetCollection<BsonDocument>("stat_config_command_receipts");
        var receiptCountBefore = await receipts.CountDocumentsAsync(
            FilterDefinition<BsonDocument>.Empty,
            cancellationToken: ct);

        var refreshedSpec = new JsonObject
        {
            ["kind"] = "MATRIX",
            ["defaultDataType"] = "MULTI_SELECT",
            ["defaultOptions"] = new JsonArray
            {
                new JsonObject { ["code"] = "REFRESHED_A", ["label"] = "Refreshed A" },
                new JsonObject { ["code"] = "REFRESHED_B", ["label"] = "Refreshed B" }
            },
            ["dataTypeOverrides"] = new JsonArray
            {
                new JsonObject
                {
                    ["scope"] = "RANGE",
                    ["id"] = "refreshed-range",
                    ["r0"] = 0,
                    ["c0"] = 0,
                    ["r1"] = 0,
                    ["c1"] = 0,
                    ["dataType"] = "SHORT_TEXT"
                }
            },
            ["specialRanges"] = new JsonArray()
        };
        var refreshedSpecJson = refreshedSpec.ToJsonString();
        var excelDocuments = _database.GetCollection<BsonDocument>("dynamic_excel_templates");
        var excelFilter = Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(_dynamicExcelId));
        var originalExcel = await excelDocuments.Find(excelFilter).SingleAsync(ct);

        try
        {
            var seedResult = await excelDocuments.UpdateOneAsync(
                excelFilter,
                Builders<BsonDocument>.Update.Set("specJson", refreshedSpecJson),
                cancellationToken: ct);
            HarnessAssert.Equal(1L, seedResult.ModifiedCount, "Failed to seed changed Dynamic Excel type metadata");
            var seededExcel = await excelDocuments.Find(excelFilter).SingleAsync(ct);
            HarnessAssert.Equal(
                refreshedSpecJson,
                seededExcel["specJson"].AsString,
                "Changed Dynamic Excel type metadata was not persisted");

            var refreshedBlocks = JsonNode.Parse(before.BlocksJson) as JsonArray
                                  ?? throw new InvalidOperationException("Published Dynamic Excel wrapper BlocksJson must be an array");
            var refreshedPrimary = refreshedBlocks.OfType<JsonObject>().FirstOrDefault()
                                   ?? throw new InvalidOperationException("Published Dynamic Excel wrapper must contain a block");
            refreshedPrimary["excelSpecKind"] = refreshedSpec["kind"]?.DeepClone();
            refreshedPrimary["defaultDataType"] = refreshedSpec["defaultDataType"]?.DeepClone();
            refreshedPrimary["defaultOptions"] = refreshedSpec["defaultOptions"]?.DeepClone();
            refreshedPrimary["dataTypeOverrides"] = refreshedSpec["dataTypeOverrides"]?.DeepClone();
            refreshedPrimary["specialRanges"] = refreshedSpec["specialRanges"]?.DeepClone();
            var refreshedBlocksJson = refreshedBlocks.ToJsonString();
            HarnessAssert.True(
                !string.Equals(before.BlocksJson, refreshedBlocksJson, StringComparison.Ordinal),
                "Refreshed Dynamic Excel block did not carry the changed type metadata");
            HarnessAssert.Equal(
                "MULTI_SELECT",
                ApiHarnessClient.RequiredString(refreshedPrimary, "defaultDataType"),
                "Refreshed Dynamic Excel defaultDataType mismatch");

            var tableMutation = new JsonObject
            {
                ["blockId"] = ApiHarnessClient.RequiredString(persistedPrimary, "blockId"),
                ["tableMode"] = ApiHarnessClient.RequiredString(persistedPrimary, "tableMode"),
                ["statisticsDisabled"] = persistedPrimary["statisticsDisabled"]?.GetValue<bool>() ?? false,
                ["metrics"] = new JsonArray(),
                ["metricLabelTargets"] = new JsonArray(),
                ["allowedRowLabelCodes"] = new JsonArray(),
                ["defaultDataType"] = ApiHarnessClient.RequiredString(refreshedPrimary, "defaultDataType")
            };
            var response = await _api.PatchAsync(
                $"api/dynamic-forms/{formId}/statistics",
                BuildCanonicalStatisticEnvelope(
                    commandId,
                    configBefore,
                    new JsonObject
                    {
                        ["tables"] = new JsonArray(tableMutation)
                    }),
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.BadRequest, "statistics PATCH with refreshed Dynamic Excel metadata");
            AssertErrorCode(
                response,
                "DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID",
                "statistics PATCH with refreshed Dynamic Excel metadata");
            var details = ApiHarnessClient.RequiredObject(
                ApiHarnessClient.RequiredObject(response.Json, "refreshed Dynamic Excel metadata error")["details"],
                "refreshed Dynamic Excel metadata details");
            HarnessAssert.Equal(
                "$.payload.tables[0].defaultDataType",
                ApiHarnessClient.RequiredString(details, "path"),
                "Refreshed Dynamic Excel metadata error path mismatch");
            HarnessAssert.Equal(
                "SCHEMA_MISMATCH",
                ApiHarnessClient.RequiredString(details, "reason"),
                "Refreshed Dynamic Excel metadata error reason mismatch");

            var after = await LoadFormDocumentAsync(formId, ct);
            AssertFormDocumentUnchanged(before, after, "Rejected Dynamic Excel metadata drift");
            HarnessAssert.Equal(before.Revision, after.Revision, "Rejected Dynamic Excel metadata drift changed owner revision");
            HarnessAssert.Equal(before.BlocksJson, after.BlocksJson, "Rejected Dynamic Excel metadata drift changed BlocksJson");
            HarnessAssert.Equal(before.ExcelBlockJson, after.ExcelBlockJson, "Rejected Dynamic Excel metadata drift changed ExcelBlockJson");
            var snapshotAfter = RequirePublishedSnapshot(after, "Dynamic Excel metadata-drift form after");
            HarnessAssert.Equal(snapshotBefore.Json, snapshotAfter.Json, "Rejected Dynamic Excel metadata drift changed snapshot JSON");
            HarnessAssert.Equal(snapshotBefore.Hash, snapshotAfter.Hash, "Rejected Dynamic Excel metadata drift changed snapshot hash");
            var configAfter = await RequireCanonicalStatisticConfigAsync(
                formId,
                adminToken,
                "Dynamic Excel metadata-drift canonical config after",
                ct);
            HarnessAssert.Equal(
                ApiHarnessClient.RequiredInt(configBefore, "revision"),
                ApiHarnessClient.RequiredInt(configAfter, "revision"),
                "Rejected Dynamic Excel metadata drift changed config revision");
            HarnessAssert.Equal(
                ApiHarnessClient.RequiredString(configBefore, "configHash"),
                ApiHarnessClient.RequiredString(configAfter, "configHash"),
                "Rejected Dynamic Excel metadata drift changed config hash");
            HarnessAssert.Equal(
                receiptCountBefore,
                await receipts.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct),
                "Rejected Dynamic Excel metadata drift wrote a receipt");
            AssertP2StatisticIsolationUnchanged(
                isolationBefore,
                await CaptureP2StatisticIsolationCountsAsync(ct),
                "Rejected Dynamic Excel metadata drift");
        }
        finally
        {
            var restoreResult = await excelDocuments.ReplaceOneAsync(excelFilter, originalExcel, cancellationToken: ct);
            HarnessAssert.Equal(1L, restoreResult.MatchedCount, "Failed to restore Dynamic Excel metadata fixture");
            var restoredExcel = await excelDocuments.Find(excelFilter).SingleAsync(ct);
            HarnessAssert.True(
                originalExcel.Equals(restoredExcel),
                "Dynamic Excel metadata fixture was not restored byte-equivalent");
        }

        return new CaseObservation(
            "after publish, refreshed Dynamic Excel defaultDataType was rejected as a strict canonical table-payload schema property before any form, receipt, rebuild, outbox or result write; snapshot/hash and source fixture stayed exact",
            "http=400;errorCode=DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID;path=$.payload.tables[0].defaultDataType;reason=SCHEMA_MISMATCH;liveSpecChanged=true;revisionDelta=0;blocksStable=true;snapshotStable=true;hashStable=true;receiptWrites=0;jobOutboxResultWrites=0;fixtureRestored=true");
    }
    private async Task<CaseObservation> RejectNormalPutOnPublishedAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var v2Id = HarnessAssert.Required(_v2FormId, "P2-DF-LC-005 v2 form");
        var before = await LoadFormDocumentAsync(v2Id, ct);
        var response = await _api.PutAsync(
            $"api/dynamic-forms/{v2Id}",
            BuildFormUpdateRequest(FormCode, "published PUT must fail", before.Revision),
            ownerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.BadRequest, "normal PUT on published Dynamic Form");
        AssertErrorCode(response, "DYNAMIC_FORM_DRAFT_REQUIRED", "normal PUT on published Dynamic Form");
        AssertFormDocumentUnchanged(before, await LoadFormDocumentAsync(v2Id, ct), "normal PUT on published Dynamic Form");
        return new CaseObservation(
            "normal PUT on the current published revision returned draft-required and left Mongo unchanged",
            "http=400;errorCode=DYNAMIC_FORM_DRAFT_REQUIRED;writes=0;revisionDelta=0");
    }

    private async Task<CaseObservation> RejectDeleteOnPublishedAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var v2Id = HarnessAssert.Required(_v2FormId, "P2-DF-LC-005 v2 form");
        var before = await LoadFormDocumentAsync(v2Id, ct);
        var response = await _api.DeleteAsync(
            $"api/dynamic-forms/{v2Id}?expectedRevision={before.Revision}",
            ownerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.BadRequest, "delete published Dynamic Form");
        AssertErrorCode(response, "DYNAMIC_FORM_DRAFT_REQUIRED", "delete published Dynamic Form");
        AssertFormDocumentUnchanged(before, await LoadFormDocumentAsync(v2Id, ct), "delete published Dynamic Form");
        return new CaseObservation(
            "DELETE on a published and runtime-linked version returned draft-required before any write",
            "http=400;errorCode=DYNAMIC_FORM_DRAFT_REQUIRED;published=true;linked=true;writes=0");
    }

    private async Task<CaseObservation> RejectCorruptPublishedSnapshotBindingAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var adminToken = HarnessAssert.Required(_adminToken, "P1-BE-002 admin token");
        var workId = HarnessAssert.Required(_bindingWorkId, "P2-DF-LC-003 binding work");
        var forms = _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates");
        string? formId = null;
        try
        {
            var created = await _api.PostAsync(
                "api/dynamic-forms",
                BuildMinimalFormRequest("P2_DF_CORRUPT_SNAPSHOT", "P2 corrupt published snapshot"),
                ownerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(created, HttpStatusCode.OK, "create corrupt-snapshot fixture draft");
            formId = ApiHarnessClient.RequiredString(created.Json, "id");
            var draftRevision = ApiHarnessClient.RequiredInt(created.Json, "revision");
            var published = await _api.PostAsync(
                $"api/dynamic-forms/{formId}/publish",
                new { expectedRevision = draftRevision },
                ownerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(published, HttpStatusCode.OK, "publish corrupt-snapshot fixture");

            var valid = await LoadFormDocumentAsync(formId, ct);
            var validSnapshot = RequirePublishedSnapshot(valid, "corrupt-snapshot fixture before corruption");
            var forgedHash = new string(validSnapshot.Hash[0] == 'f' ? 'e' : 'f', 64);
            var corruptUpdate = await forms.UpdateOneAsync(
                x => x.Id == formId && !x.IsDeleted,
                Builders<DynamicFormTemplate>.Update.Set(x => x.PublishedSchemaHash, forgedHash),
                cancellationToken: ct);
            HarnessAssert.Equal(1L, corruptUpdate.ModifiedCount, "Failed to seed mismatched published schema hash");
            var corrupted = await LoadFormDocumentAsync(formId, ct);
            HarnessAssert.Equal(validSnapshot.Json, corrupted.PublishedSchemaSnapshotJson, "Corrupt fixture changed snapshot JSON");
            HarnessAssert.Equal(forgedHash, corrupted.PublishedSchemaHash, "Corrupt fixture hash seed mismatch");
            HarnessAssert.True(
                !string.Equals(ComputeSha256(validSnapshot.Json), corrupted.PublishedSchemaHash, StringComparison.Ordinal),
                "Corrupt fixture did not produce a snapshot/hash mismatch");

            var writesBefore = await CaptureAssignmentWriteSnapshotAsync(workId, ct);
            var response = await PostAssignmentAsync(
                workId,
                formId,
                "P2 corrupt snapshot bind must fail",
                _assigneeId,
                adminToken,
                ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Conflict, "bind published form with corrupt snapshot hash");
            AssertErrorCode(
                response,
                "DYNAMIC_FORM_PUBLISHED_SCHEMA_INTEGRITY_FAILED",
                "bind published form with corrupt snapshot hash");
            var writesAfter = await CaptureAssignmentWriteSnapshotAsync(workId, ct);
            HarnessAssert.Equal(writesBefore, writesAfter, "Corrupt published snapshot binding wrote runtime documents");
            AssertFormDocumentUnchanged(
                corrupted,
                await LoadFormDocumentAsync(formId, ct),
                "Corrupt published snapshot binding");
        }
        finally
        {
            if (formId is not null)
                await forms.DeleteOneAsync(x => x.Id == formId, ct);
        }

        return new CaseObservation(
            "assignment binding recomputed the published snapshot hash and failed closed on seeded corruption before any assignment, binding, or doc-role write",
            "http=409;errorCode=DYNAMIC_FORM_PUBLISHED_SCHEMA_INTEGRITY_FAILED;snapshotHashMismatch=true;runtimeWrites=0;fixtureCleanup=true");
    }

    private async Task<CaseObservation> RejectLiveSchemaDriftBindingAsync(CancellationToken ct)
    {
        var adminToken = HarnessAssert.Required(_adminToken, "P1-BE-002 admin token");
        var workId = HarnessAssert.Required(_bindingWorkId, "P2-DF-LC-003 binding work");
        var formId = HarnessAssert.Required(_v2FormId, "P2-DF-LC-005 v2 form");
        var before = await LoadFormDocumentAsync(formId, ct);
        var validSnapshot = RequirePublishedSnapshot(before, "live-schema-drift fixture before corruption");
        var liveFields = JsonNode.Parse(before.FieldsJson) as JsonArray
                         ?? throw new InvalidOperationException("Published v2 FieldsJson must be an array");
        var liveTarget = liveFields.OfType<JsonObject>().FirstOrDefault(field =>
                             field["isStatistic"]?.GetValue<bool>() == true)
                         ?? throw new InvalidOperationException(
                             "Live-schema-drift fixture requires the preceding positive statistics-only update");
        var targetId = ApiHarnessClient.RequiredString(liveTarget, "id");
        var snapshotRoot = JsonNode.Parse(validSnapshot.Json) as JsonObject
                           ?? throw new InvalidOperationException("Published v2 snapshot must be an object");
        var snapshotFields = ApiHarnessClient.RequiredArray(
            snapshotRoot["fields"],
            "live-schema-drift snapshot fields");
        var snapshotTarget = snapshotFields.OfType<JsonObject>().Single(field =>
            string.Equals(field["id"]?.GetValue<string>(), targetId, StringComparison.Ordinal));
        HarnessAssert.True(
            snapshotTarget["isStatistic"]?.GetValue<bool>() != true,
            "Live-schema-drift fixture did not retain a legitimate statistics-only delta from its snapshot");

        var originalName = ApiHarnessClient.RequiredString(liveTarget, "name");
        liveTarget["name"] = $"{originalName} forbidden live structural drift";
        var driftedFieldsJson = liveFields.ToJsonString();
        HarnessAssert.True(
            !string.Equals(before.FieldsJson, driftedFieldsJson, StringComparison.Ordinal),
            "Live-schema-drift fixture did not change the field structure");

        var formDocuments = _database.GetCollection<BsonDocument>("dynamic_form_templates");
        var formFilter = Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(formId));
        var originalDocument = await formDocuments.Find(formFilter).SingleAsync(ct);
        try
        {
            var seedResult = await formDocuments.UpdateOneAsync(
                formFilter,
                Builders<BsonDocument>.Update.Set("fieldsJson", driftedFieldsJson),
                cancellationToken: ct);
            HarnessAssert.Equal(1L, seedResult.ModifiedCount, "Failed to seed live published schema drift");
            var drifted = await LoadFormDocumentAsync(formId, ct);
            HarnessAssert.Equal(before.Revision, drifted.Revision, "Live schema drift seed changed revision");
            HarnessAssert.Equal(validSnapshot.Json, drifted.PublishedSchemaSnapshotJson, "Live schema drift seed changed snapshot JSON");
            HarnessAssert.Equal(validSnapshot.Hash, drifted.PublishedSchemaHash, "Live schema drift seed changed snapshot hash");
            _ = RequirePublishedSnapshot(drifted, "live-schema-drift fixture with still-valid snapshot/hash");

            var writesBefore = await CaptureAssignmentWriteSnapshotAsync(workId, ct);
            var response = await PostAssignmentAsync(
                workId,
                formId,
                "P2 valid snapshot but drifted live schema must fail",
                _assigneeId,
                adminToken,
                ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Conflict, "bind published form with live structural drift");
            AssertErrorCode(
                response,
                "DYNAMIC_FORM_PUBLISHED_SCHEMA_INTEGRITY_FAILED",
                "bind published form with live structural drift");
            HarnessAssert.Equal(
                "PUBLISHED_SCHEMA_SNAPSHOT_OR_LIVE_STRUCTURE_MISMATCH",
                ApiHarnessClient.FindStringRecursive(response.Json, "reason"),
                "Live published schema drift reason mismatch");
            var writesAfter = await CaptureAssignmentWriteSnapshotAsync(workId, ct);
            HarnessAssert.Equal(writesBefore, writesAfter, "Live published schema drift binding wrote runtime documents");
            AssertFormDocumentUnchanged(
                drifted,
                await LoadFormDocumentAsync(formId, ct),
                "Live published schema drift binding");
        }
        finally
        {
            var restoreResult = await formDocuments.ReplaceOneAsync(formFilter, originalDocument, cancellationToken: ct);
            HarnessAssert.Equal(1L, restoreResult.MatchedCount, "Failed to restore live-schema-drift form fixture");
            var restoredDocument = await formDocuments.Find(formFilter).SingleAsync(ct);
            HarnessAssert.True(
                originalDocument.Equals(restoredDocument),
                "Live-schema-drift form fixture was not restored byte-equivalent");
        }

        return new CaseObservation(
            "binding rejected a published form whose immutable snapshot/hash remained valid but whose live field name drifted; the legitimate statistics-only delta was stripped before structural comparison and no runtime write occurred",
            "http=409;errorCode=DYNAMIC_FORM_PUBLISHED_SCHEMA_INTEGRITY_FAILED;snapshotHashValid=true;statisticsOnlyDeltaPresent=true;structuralNameDrift=true;runtimeWrites=0;formWrites=0;fixtureRestored=true");
    }

    private async Task<CaseObservation> RejectDeactivatedEnumCatalogPublishAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var catalogId = HarnessAssert.Required(_enumCatalogId, "P1-BE-005 enum catalog");
        var forms = _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates");
        var catalogDocuments = _database.GetCollection<BsonDocument>("label_enum_catalogs");
        var catalogFilter = Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(catalogId));
        var originalCatalog = await catalogDocuments.Find(catalogFilter).SingleAsync(ct);
        string? formId = null;
        try
        {
            var created = await _api.PostAsync(
                "api/dynamic-forms",
                BuildFormRequest(
                    "P2_DF_DEACTIVATED_ENUM_PUBLISH",
                    "P2 deactivated enum publish guard",
                    updated: false),
                ownerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(created, HttpStatusCode.OK, "create draft with active ENUM_CATALOG");
            formId = ApiHarnessClient.RequiredString(created.Json, "id");
            var before = await LoadFormDocumentAsync(formId, ct);
            HarnessAssert.True(!before.IsPublished, "ENUM_CATALOG publish-guard fixture must start as draft");
            HarnessAssert.True(before.PublishedSchemaSnapshotJson is null, "Draft ENUM_CATALOG fixture unexpectedly has a snapshot");
            HarnessAssert.True(before.PublishedSchemaHash is null, "Draft ENUM_CATALOG fixture unexpectedly has a hash");

            var deactivateResult = await catalogDocuments.UpdateOneAsync(
                catalogFilter,
                Builders<BsonDocument>.Update.Set("isActive", false),
                cancellationToken: ct);
            HarnessAssert.Equal(1L, deactivateResult.ModifiedCount, "Failed to deactivate ENUM_CATALOG fixture");

            var response = await _api.PostAsync(
                $"api/dynamic-forms/{formId}/publish",
                new { expectedRevision = before.Revision },
                ownerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.NotFound, "publish draft after ENUM_CATALOG deactivation");
            AssertErrorCode(response, "LABEL_ENUM_CATALOG_NOT_FOUND", "publish draft after ENUM_CATALOG deactivation");
            HarnessAssert.Equal(
                catalogId,
                ApiHarnessClient.FindStringRecursive(response.Json, "catalogId"),
                "Deactivated ENUM_CATALOG publish error metadata mismatch");

            var after = await LoadFormDocumentAsync(formId, ct);
            AssertFormDocumentUnchanged(before, after, "Deactivated ENUM_CATALOG publish guard");
            HarnessAssert.True(!after.IsPublished, "Rejected deactivated ENUM_CATALOG publish changed draft state");
            HarnessAssert.Equal(before.Revision, after.Revision, "Rejected deactivated ENUM_CATALOG publish changed revision");
            HarnessAssert.True(after.PublishedSchemaSnapshotJson is null, "Rejected deactivated ENUM_CATALOG publish wrote snapshot JSON");
            HarnessAssert.True(after.PublishedSchemaHash is null, "Rejected deactivated ENUM_CATALOG publish wrote snapshot hash");
        }
        finally
        {
            var restoreResult = await catalogDocuments.ReplaceOneAsync(catalogFilter, originalCatalog, cancellationToken: ct);
            HarnessAssert.Equal(1L, restoreResult.MatchedCount, "Failed to restore ENUM_CATALOG fixture");
            var restoredCatalog = await catalogDocuments.Find(catalogFilter).SingleAsync(ct);
            HarnessAssert.True(originalCatalog.Equals(restoredCatalog), "ENUM_CATALOG fixture was not restored byte-equivalent");
            if (formId is not null)
                await forms.DeleteOneAsync(x => x.Id == formId, ct);
        }

        return new CaseObservation(
            "a draft created with an active ENUM_CATALOG failed publish after the catalog was deactivated; draft state/revision and empty snapshot/hash stayed byte-exact",
            "http=404;errorCode=LABEL_ENUM_CATALOG_NOT_FOUND;catalogRevalidatedAtPublish=true;isPublished=false;revisionDelta=0;snapshotWrites=0;hashWrites=0;formWrites=0;catalogRestored=true;fixtureCleanup=true");
    }

    private async Task<CaseObservation> VerifyEndpointPermissionMatrixAsync(CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var adminToken = HarnessAssert.Required(_adminToken, "P1-BE-002 admin token");
        var assigneeToken = HarnessAssert.Required(_assigneeToken, "P1-BE-003 assignee token");
        var reviewerToken = HarnessAssert.Required(_reviewerToken, "P1-BE-003 reviewer token");
        var outsiderToken = HarnessAssert.Required(_outsiderToken, "P1-BE-003 outsider token");
        var v1Id = HarnessAssert.Required(_formId, "P1-BE-005 form");
        var v2Id = HarnessAssert.Required(_v2FormId, "P2-DF-LC-005 v2 form");

        await AssertStatisticConfigAllowedAsync(
            ownerToken,
            v1Id,
            _ownerId,
            "p2-df-perm-owner-statistics",
            "owner statistics permission",
            ct);
        await AssertStatisticConfigAllowedAsync(
            adminToken,
            v2Id,
            HarnessAssert.Required(_adminId, "admin id"),
            "p2-df-perm-admin-statistics",
            "admin statistics permission",
            ct);

        foreach (var actor in new[]
                 {
                     (Name: "owner", Token: ownerToken, UserId: _ownerId, CloneCode: "P2_DF_PERM_OWNER_CLONE"),
                     (Name: "admin", Token: adminToken, UserId: HarnessAssert.Required(_adminId, "admin id"), CloneCode: "P2_DF_PERM_ADMIN_CLONE")
                 })
        {
            var detail = await RequireFormDetailAsync(v2Id, actor.Token, $"{actor.Name} permission detail", ct);
            AssertPublishedOwnerActions(detail, $"{actor.Name} permission actions");
            await AssertDesignSearchVisibilityAsync(actor.Token, v2Id, true, actor.Name, ct);
            await AssertVersionHistoryAsync(actor.Token, v2Id, [v2Id, v1Id], $"{actor.Name} permission history", ct);
            await AssertPublishedDraftOnlyEndpointsAsync(actor.Token, v2Id, actor.Name, ct);
            var clone = await CloneAndAssertAsync(v2Id, actor.CloneCode, actor.UserId, actor.Token, ct);
            _p2FormIds[actor.CloneCode] = ApiHarnessClient.RequiredString(clone, "id");
        }

        var cloneGranted = await RequireFormDetailAsync(v1Id, assigneeToken, "clone-grant permission detail", ct);
        HarnessAssert.True(ApiHarnessClient.RequiredBool(cloneGranted, "canViewByCloneGrant"), "Clone-grant permission marker is false");
        AssertCloneGrantActions(cloneGranted, "clone-grant permission actions");
        await AssertDesignSearchVisibilityAsync(assigneeToken, v1Id, true, "clone-grant assignee", ct);
        await AssertHistoryDeniedAsync(assigneeToken, v1Id, "clone-grant permission history", ct);
        await AssertNonCloneMutationsDeniedAsync(assigneeToken, v1Id, "clone-grant permission", ct);
        await AssertStatisticConfigDeniedAsync(assigneeToken, v1Id, "p2-df-perm-clone-grant-statistics-denied", "clone-grant statistics permission", ct);
        var grantedClone = await CloneAndAssertAsync(
            v1Id,
            "P2_DF_PERM_GRANTED_CLONE",
            _assigneeId,
            assigneeToken,
            ct);
        _p2FormIds["P2_DF_PERM_GRANTED_CLONE"] = ApiHarnessClient.RequiredString(grantedClone, "id");

        await AssertRuntimeReadOnlyAsync(assigneeToken, v2Id, "runtime assignee exact v2 permission", ct);
        await AssertStatisticConfigDeniedAsync(assigneeToken, v2Id, "p2-df-perm-assignee-statistics-denied", "runtime assignee statistics permission", ct);
        await AssertRuntimeReadOnlyAsync(reviewerToken, v1Id, "runtime reviewer exact v1 permission", ct);
        await AssertStatisticConfigDeniedAsync(reviewerToken, v1Id, "p2-df-perm-reviewer-statistics-denied", "runtime reviewer statistics permission", ct);
        await AssertOutsiderDeniedAsync(outsiderToken, v2Id, ct);
        await AssertStatisticConfigDeniedAsync(outsiderToken, v2Id, "p2-df-perm-outsider-statistics-denied", "outsider statistics permission", ct);
        return new CaseObservation(
            "endpoint matrix asserted owner/admin canonical statistic mutations, clone-grant and exact runtime assignee/reviewer, and outsider behavior; hidden statistic owners matched canonical 404 while legacy Dynamic Form endpoints retained generic 403 envelopes without metadata leakage",
            "actors=6;owner=design+canonicalStatistics;admin=design+canonicalStatistics;cloneGrant=read+clone;assignee=exactRuntime;reviewer=exactRuntime;outsider=deny;statisticsAllowed=2;statisticsHidden404=4;statisticReceiptWrites=2;statisticJobOutboxResultWrites=0;metadataLeak=false");
    }

    private async Task<CaseObservation> RejectHiddenFormAssignmentBindingAsync(CancellationToken ct)
    {
        var outsiderToken = HarnessAssert.Required(_outsiderToken, "P1-BE-003 outsider token");
        var hiddenFormId = HarnessAssert.Required(_formId, "P1-BE-005 owner form");
        var workId = ObjectId.GenerateNewId().ToString();
        var works = _database.GetCollection<Work>("works");
        await InsertOwnedWorkAsync(
            workId,
            "P2-DF-HIDDEN-BIND",
            "P2 hidden Dynamic Form binding work",
            _outsiderId,
            OutsiderUsername,
            "P1 Outsider",
            ct);
        try
        {
            await AssertFormGetDeniedAsync(outsiderToken, hiddenFormId, "hidden-form assignment prerequisite", ct);
            var before = await CaptureAssignmentWriteSnapshotAsync(workId, ct);
            var response = await PostAssignmentAsync(
                workId,
                hiddenFormId,
                "P2 hidden foreign form bind must fail",
                _assigneeId,
                outsiderToken,
                ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Forbidden, "normal creator binds hidden published form");
            AssertErrorCode(response, "AUTH_FORBIDDEN", "normal creator binds hidden published form");
            HarnessAssert.True(
                !response.Body.Contains(hiddenFormId, StringComparison.Ordinal) &&
                !response.Body.Contains(FormCode, StringComparison.Ordinal),
                "Hidden-form assignment denial leaked foreign form identity");
            var after = await CaptureAssignmentWriteSnapshotAsync(workId, ct);
            HarnessAssert.Equal(before, after, "Hidden-form assignment denial wrote assignment/binding/doc-role state");
            await AssertFormGetDeniedAsync(outsiderToken, hiddenFormId, "hidden-form assignment postcondition", ct);
        }
        finally
        {
            await works.DeleteOneAsync(x => x.Id == workId, ct);
        }

        return new CaseObservation(
            "a normal assignment creator on their own work received generic 403 when binding an owner-hidden published form; no assignment, binding, or doc-role write occurred and direct GET stayed forbidden",
            "http=403;errorCode=AUTH_FORBIDDEN;formMetadataLeak=false;assignmentWrites=0;bindingWrites=0;docRoleWrites=0;getAfter=403;fixtureCleanup=true");
    }

    private async Task<CaseObservation> RejectHiddenFormFlowBindingAsync(CancellationToken ct)
    {
        var outsiderToken = HarnessAssert.Required(_outsiderToken, "P1-BE-003 outsider token");
        var hiddenFormId = HarnessAssert.Required(_formId, "P1-BE-005 owner form");
        var forms = _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates");
        var flows = _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates");
        var versions = _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions");
        var users = _database.GetCollection<AppUser>("users");
        var outsider = await users.Find(x => x.Id == _outsiderId && !x.IsDeleted).SingleAsync(ct);
        var originalRoles = (outsider.Roles ?? new List<string>()).ToList();
        await users.UpdateOneAsync(
            x => x.Id == _outsiderId && !x.IsDeleted,
            Builders<AppUser>.Update.Set(
                x => x.Roles,
                originalRoles.Append("DYNAMIC_FLOW_MANAGER").Distinct(StringComparer.OrdinalIgnoreCase).ToList()),
            cancellationToken: ct);
        string? ownFormId = null;
        string? ownFlowId = null;
        try
        {
            await AssertFormGetDeniedAsync(outsiderToken, hiddenFormId, "hidden-form flow prerequisite", ct);
            var flowCountBeforeCreate = await flows.CountDocumentsAsync(FilterDefinition<DynamicFlowTemplate>.Empty, cancellationToken: ct);
            var versionCountBeforeCreate = await versions.CountDocumentsAsync(FilterDefinition<DynamicFlowTemplateVersion>.Empty, cancellationToken: ct);
            var hiddenCreate = await _api.PostAsync(
                "api/dynamic-flow-templates",
                new JsonObject
                {
                    ["commandId"] = "p2-hidden-form-flow-create",
                    ["code"] = "P2_DF_HIDDEN_FLOW_CREATE",
                    ["name"] = "P2 hidden flow create must fail",
                    ["description"] = "must not bind an owner-hidden form",
                    ["rootDynamicFormTemplateId"] = hiddenFormId,
                    ["payload"] = BuildFlowPayload(hiddenFormId)
                },
                outsiderToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(hiddenCreate, HttpStatusCode.Forbidden, "create Dynamic Flow with hidden form");
            AssertErrorCode(hiddenCreate, "AUTH_FORBIDDEN", "create Dynamic Flow with hidden form");
            HarnessAssert.True(
                !hiddenCreate.Body.Contains(hiddenFormId, StringComparison.Ordinal) &&
                !hiddenCreate.Body.Contains(FormCode, StringComparison.Ordinal),
                "Hidden-form flow-create denial leaked foreign form identity");
            HarnessAssert.Equal(
                flowCountBeforeCreate,
                await flows.CountDocumentsAsync(FilterDefinition<DynamicFlowTemplate>.Empty, cancellationToken: ct),
                "Hidden-form flow create wrote a template");
            HarnessAssert.Equal(
                versionCountBeforeCreate,
                await versions.CountDocumentsAsync(FilterDefinition<DynamicFlowTemplateVersion>.Empty, cancellationToken: ct),
                "Hidden-form flow create wrote a version");

            var ownForm = await _api.PostAsync(
                "api/dynamic-forms",
                BuildMinimalFormRequest("P2_DF_OUTSIDER_FLOW_ROOT", "P2 outsider flow root"),
                outsiderToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(ownForm, HttpStatusCode.OK, "create outsider-owned flow root form");
            ownFormId = ApiHarnessClient.RequiredString(ownForm.Json, "id");
            var ownPublish = await _api.PostAsync(
                $"api/dynamic-forms/{ownFormId}/publish",
                new { expectedRevision = ApiHarnessClient.RequiredInt(ownForm.Json, "revision") },
                outsiderToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(ownPublish, HttpStatusCode.OK, "publish outsider-owned flow root form");

            var ownFlow = await _api.PostAsync(
                "api/dynamic-flow-templates",
                new JsonObject
                {
                    ["commandId"] = "p2-hidden-form-own-flow-create",
                    ["code"] = "P2_DF_OUTSIDER_OWN_FLOW",
                    ["name"] = "P2 outsider-owned flow",
                    ["description"] = "valid control fixture before hidden-form save",
                    ["rootDynamicFormTemplateId"] = ownFormId,
                    ["payload"] = BuildFlowPayload(ownFormId)
                },
                outsiderToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(ownFlow, HttpStatusCode.OK, "create outsider-owned control flow");
            ownFlowId = ApiHarnessClient.RequiredString(ownFlow.Json, "id");
            var flowBeforeSave = await flows.Find(x => x.Id == ownFlowId).SingleAsync(ct);
            var versionsBeforeSave = await versions.Find(x => x.TemplateId == ownFlowId).ToListAsync(ct);
            HarnessAssert.Equal(1, versionsBeforeSave.Count, "Outsider control flow draft count mismatch");
            var ownDraft = versionsBeforeSave.Single();

            var hiddenSave = await _api.PutAsync(
                $"api/dynamic-flow-templates/{ownFlowId}/versions/draft",
                new JsonObject
                {
                    ["commandId"] = "p2-hidden-form-flow-save",
                    ["expectedDraftRevision"] = ownDraft.DraftRevision,
                    ["expectedPayloadHash"] = ownDraft.PayloadHash,
                    ["payload"] = BuildFlowPayloadWithForeignChild(ownFormId, hiddenFormId)
                },
                outsiderToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(hiddenSave, HttpStatusCode.Forbidden, "save Dynamic Flow draft with hidden form");
            AssertErrorCode(hiddenSave, "AUTH_FORBIDDEN", "save Dynamic Flow draft with hidden form");
            HarnessAssert.True(
                !hiddenSave.Body.Contains(hiddenFormId, StringComparison.Ordinal) &&
                !hiddenSave.Body.Contains(FormCode, StringComparison.Ordinal),
                "Hidden-form flow-save denial leaked foreign form identity");
            var flowAfterSave = await flows.Find(x => x.Id == ownFlowId).SingleAsync(ct);
            HarnessAssert.True(
                flowBeforeSave.ToBsonDocument().Equals(flowAfterSave.ToBsonDocument()),
                "Hidden-form flow-save denial mutated the flow template");
            var versionsAfterSave = await versions.Find(x => x.TemplateId == ownFlowId).ToListAsync(ct);
            HarnessAssert.Equal(versionsBeforeSave.Count, versionsAfterSave.Count, "Hidden-form flow-save denial changed version count");
            HarnessAssert.True(
                versionsBeforeSave[0].ToBsonDocument().Equals(versionsAfterSave[0].ToBsonDocument()),
                "Hidden-form flow-save denial mutated the draft version");
            await AssertFormGetDeniedAsync(outsiderToken, hiddenFormId, "hidden-form flow postcondition", ct);
        }
        finally
        {
            if (ownFlowId is not null)
            {
                await versions.DeleteManyAsync(x => x.TemplateId == ownFlowId, ct);
                await flows.DeleteOneAsync(x => x.Id == ownFlowId, ct);
            }

            if (ownFormId is not null)
                await forms.DeleteOneAsync(x => x.Id == ownFormId, ct);

            await users.UpdateOneAsync(
                x => x.Id == _outsiderId && !x.IsDeleted,
                Builders<AppUser>.Update.Set(x => x.Roles, originalRoles),
                cancellationToken: CancellationToken.None);
        }

        return new CaseObservation(
            "Dynamic Flow create and draft-save both rejected a hidden foreign form with generic 403 before flow/version writes or provenance disclosure",
            "create=403;save=403;errorCode=AUTH_FORBIDDEN;metadataLeak=false;createWrites=0;saveWrites=0;getAfter=403;fixtureCleanup=true");
    }

    private async Task AssertStatisticConfigAllowedAsync(
        string token,
        string formId,
        string expectedActorId,
        string commandId,
        string context,
        CancellationToken ct)
    {
        var before = await LoadFormDocumentAsync(formId, ct);
        var beforeSnapshot = RequirePublishedSnapshot(before, $"{context} before");
        var toggle = FindFirstEligibleStatisticToggle(before.FieldsJson, context);
        var configBefore = await RequireCanonicalStatisticConfigAsync(
            formId,
            token,
            $"{context} canonical config before",
            ct);
        var isolationBefore = await CaptureP2StatisticIsolationCountsAsync(ct);
        var receipts = _database.GetCollection<BsonDocument>("stat_config_command_receipts");
        var receiptCountBefore = await receipts.CountDocumentsAsync(
            FilterDefinition<BsonDocument>.Empty,
            cancellationToken: ct);
        var response = await _api.PatchAsync(
            $"api/dynamic-forms/{formId}/statistics",
            BuildCanonicalStatisticEnvelope(
                commandId,
                configBefore,
                new JsonObject
                {
                    ["fields"] = new JsonArray(
                        BuildCanonicalFieldStatisticMutation(
                            toggle.FieldId,
                            toggle.NextIsStatistic))
                }),
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, context);
        var result = RequireCanonicalStatisticMutationResult(
            response,
            configBefore,
            formId,
            context);

        var after = await LoadFormDocumentAsync(formId, ct);
        HarnessAssert.Equal(before.Revision + 1, after.Revision, $"{context} owner-template revision mismatch");
        HarnessAssert.Equal(
            ApiHarnessClient.RequiredInt(result, "revision"),
            checked((int)after.StatisticConfigRevision),
            $"{context} config revision differs between API and Mongo");
        HarnessAssert.Equal(
            ApiHarnessClient.RequiredString(result, "configHash"),
            after.StatisticConfigHash,
            $"{context} config hash differs between API and Mongo");
        HarnessAssert.Equal(expectedActorId, after.StatisticConfigUpdatedByUserId, $"{context} Mongo actor mismatch");
        HarnessAssert.True(after.StatisticConfigUpdatedAtUtc.HasValue, $"{context} update timestamp is missing");
        HarnessAssert.True(
            !string.IsNullOrWhiteSpace(after.StatisticConfigUpdateMonthKey),
            $"{context} canonical month key is missing");
        var afterFields = JsonNode.Parse(after.FieldsJson) as JsonArray
                          ?? throw new InvalidOperationException($"{context} fieldsJson after must be an array");
        var target = afterFields.OfType<JsonObject>().Single(field =>
            string.Equals(field["id"]?.GetValue<string>(), toggle.FieldId, StringComparison.Ordinal));
        HarnessAssert.Equal(
            toggle.NextIsStatistic,
            target["isStatistic"]?.GetValue<bool>() ?? false,
            $"{context} statistic flag mismatch");
        if (toggle.NextIsStatistic)
        {
            var statistic = ApiHarnessClient.RequiredObject(target["statistic"], $"{context} statistic settings");
            var operations = ApiHarnessClient.RequiredArray(statistic["aggregateOps"], $"{context} statistic operations");
            HarnessAssert.Equal(1, operations.Count, $"{context} statistic operation count mismatch");
            HarnessAssert.Equal("COUNT", operations[0]!.GetValue<string>(), $"{context} statistic operation mismatch");
        }
        else
        {
            HarnessAssert.True(target["statistic"] is null, $"{context} disabled statistic retained settings");
        }

        var afterSnapshot = RequirePublishedSnapshot(after, $"{context} after");
        HarnessAssert.Equal(beforeSnapshot.Json, afterSnapshot.Json, $"{context} changed immutable snapshot JSON");
        HarnessAssert.Equal(beforeSnapshot.Hash, afterSnapshot.Hash, $"{context} changed immutable snapshot hash");
        HarnessAssert.Equal(
            receiptCountBefore + 1,
            await receipts.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct),
            $"{context} receipt cardinality mismatch");
        await AssertCanonicalStatisticReceiptAsync(result, formId, commandId, expectedActorId, ct);
        AssertP2StatisticIsolationUnchanged(
            isolationBefore,
            await CaptureP2StatisticIsolationCountsAsync(ct),
            context);
    }

    private async Task AssertStatisticConfigDeniedAsync(
        string token,
        string formId,
        string commandId,
        string context,
        CancellationToken ct)
    {
        var before = await LoadFormDocumentAsync(formId, ct);
        var toggle = FindFirstEligibleStatisticToggle(before.FieldsJson, context);
        var oracleToken = HarnessAssert.Required(_adminToken, "P1-BE-002 admin token");
        var configBefore = await RequireCanonicalStatisticConfigAsync(
            formId,
            oracleToken,
            $"{context} authorized config oracle",
            ct);
        var isolationBefore = await CaptureP2StatisticIsolationCountsAsync(ct);
        var receipts = _database.GetCollection<BsonDocument>("stat_config_command_receipts");
        var receiptCountBefore = await receipts.CountDocumentsAsync(
            FilterDefinition<BsonDocument>.Empty,
            cancellationToken: ct);
        var response = await _api.PatchAsync(
            $"api/dynamic-forms/{formId}/statistics",
            BuildCanonicalStatisticEnvelope(
                commandId,
                configBefore,
                new JsonObject
                {
                    ["fields"] = new JsonArray(
                        BuildCanonicalFieldStatisticMutation(
                            toggle.FieldId,
                            toggle.NextIsStatistic))
                }),
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.NotFound, context);
        AssertCanonicalStatisticConfigNotFound(response, before, context);
        AssertFormDocumentUnchanged(before, await LoadFormDocumentAsync(formId, ct), context);
        HarnessAssert.Equal(
            receiptCountBefore,
            await receipts.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct),
            $"{context} wrote a canonical command receipt");
        AssertP2StatisticIsolationUnchanged(
            isolationBefore,
            await CaptureP2StatisticIsolationCountsAsync(ct),
            context);
    }

    private static (string FieldId, bool NextIsStatistic) FindFirstEligibleStatisticToggle(
        string fieldsJson,
        string context)
    {
        var fields = JsonNode.Parse(fieldsJson) as JsonArray
                     ?? throw new InvalidOperationException($"{context} fieldsJson must be an array");
        var target = fields.OfType<JsonObject>().FirstOrDefault(field =>
                         !string.Equals(field["type"]?.GetValue<string>(), "richText", StringComparison.Ordinal))
                     ?? throw new InvalidOperationException($"{context} has no statistic-eligible field");
        return (
            ApiHarnessClient.RequiredString(target, "id"),
            !(target["isStatistic"]?.GetValue<bool>() ?? false));
    }
    private async Task AssertPublishedDraftOnlyEndpointsAsync(
        string token,
        string formId,
        string context,
        CancellationToken ct)
    {
        var current = await LoadFormDocumentAsync(formId, ct);
        var update = await _api.PutAsync(
            $"api/dynamic-forms/{formId}",
            BuildFormUpdateRequest(FormCode, $"{context} published update must fail", current.Revision),
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(update, HttpStatusCode.BadRequest, $"{context} published update");
        AssertErrorCode(update, "DYNAMIC_FORM_DRAFT_REQUIRED", $"{context} published update");

        var import = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/blocks/import-dynamic-excel",
            new { dynamicExcelTemplateId = _dynamicExcelId, sectionId = "main", expectedRevision = current.Revision },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(import, HttpStatusCode.BadRequest, $"{context} published import");
        AssertErrorCode(import, "DYNAMIC_FORM_DRAFT_REQUIRED", $"{context} published import");

        var delete = await _api.DeleteAsync(
            $"api/dynamic-forms/{formId}?expectedRevision={current.Revision}",
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(delete, HttpStatusCode.BadRequest, $"{context} published delete");
        AssertErrorCode(delete, "DYNAMIC_FORM_DRAFT_REQUIRED", $"{context} published delete");

        var publishRetry = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/publish",
            new { expectedRevision = 1 },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(publishRetry, HttpStatusCode.OK, $"{context} published idempotent publish");
        HarnessAssert.Equal(current.Revision, ApiHarnessClient.RequiredInt(publishRetry.Json, "revision"), $"{context} publish retry revision mismatch");
        AssertFormDocumentUnchanged(current, await LoadFormDocumentAsync(formId, ct), $"{context} published draft-only endpoints");
    }

    private async Task AssertNonCloneMutationsDeniedAsync(
        string token,
        string formId,
        string context,
        CancellationToken ct)
    {
        var current = await LoadFormDocumentAsync(formId, ct);
        var update = await _api.PutAsync(
            $"api/dynamic-forms/{formId}",
            BuildFormUpdateRequest(FormCode, "clone grant must not update", current.Revision),
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(update, HttpStatusCode.Forbidden, $"{context} update");
        AssertForbiddenFormResponse(
            update,
            current,
            "DYNAMIC_FORM_MUTATE_FORBIDDEN",
            "MUTATE_DYNAMIC_FORM",
            $"{context} update");

        var publish = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/publish",
            new { expectedRevision = current.Revision },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(publish, HttpStatusCode.Forbidden, $"{context} publish");
        AssertForbiddenFormResponse(
            publish,
            current,
            "DYNAMIC_FORM_MUTATE_FORBIDDEN",
            "MUTATE_DYNAMIC_FORM",
            $"{context} publish");

        var import = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/blocks/import-dynamic-excel",
            new { dynamicExcelTemplateId = _dynamicExcelId, sectionId = "main", expectedRevision = current.Revision },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(import, HttpStatusCode.Forbidden, $"{context} import");
        AssertForbiddenFormResponse(
            import,
            current,
            "DYNAMIC_FORM_MUTATE_FORBIDDEN",
            "MUTATE_DYNAMIC_FORM",
            $"{context} import");

        var delete = await _api.DeleteAsync(
            $"api/dynamic-forms/{formId}?expectedRevision={current.Revision}",
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(delete, HttpStatusCode.Forbidden, $"{context} delete");
        AssertForbiddenFormResponse(
            delete,
            current,
            "DYNAMIC_FORM_MUTATE_FORBIDDEN",
            "MUTATE_DYNAMIC_FORM",
            $"{context} delete");
        AssertFormDocumentUnchanged(current, await LoadFormDocumentAsync(formId, ct), context);
    }

    private async Task<JsonObject> CloneAndAssertAsync(
        string sourceId,
        string code,
        string expectedOwnerId,
        string token,
        CancellationToken ct)
    {
        var response = await _api.PostAsync(
            $"api/dynamic-forms/{sourceId}/clone",
            new { code, name = $"Permission clone {code}" },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"permission clone {code}");
        var clone = ApiHarnessClient.RequiredObject(response.Json, $"permission clone {code}");
        var cloneId = ApiHarnessClient.RequiredString(clone, "id");
        HarnessAssert.Equal(code, ApiHarnessClient.RequiredString(clone, "code"), $"Permission clone {code} code mismatch");
        HarnessAssert.Equal(cloneId, ApiHarnessClient.RequiredString(clone, "familyId"), $"Permission clone {code} family mismatch");
        HarnessAssert.Equal(sourceId, ApiHarnessClient.RequiredString(clone, "clonedFromVersionId"), $"Permission clone {code} source mismatch");
        HarnessAssert.Equal(expectedOwnerId, ApiHarnessClient.RequiredString(clone, "createdByUserId"), $"Permission clone {code} owner mismatch");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(clone, "revision"), $"Permission clone {code} revision mismatch");
        HarnessAssert.True(!ApiHarnessClient.RequiredBool(clone, "isPublished"), $"Permission clone {code} must be a draft");
        return clone;
    }

    private static void AssertDeleteSuccess(ApiHarnessResponse response, string context)
        => ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, context);

    private static (string Json, string Hash) RequirePublishedSnapshot(
        DynamicFormTemplate doc,
        string context)
    {
        HarnessAssert.True(doc.IsPublished, $"{context} is not published");
        var json = HarnessAssert.Required(doc.PublishedSchemaSnapshotJson, $"{context} snapshot JSON");
        var hash = HarnessAssert.Required(doc.PublishedSchemaHash, $"{context} snapshot hash");
        HarnessAssert.Equal(64, hash.Length, $"{context} SHA-256 length mismatch");
        HarnessAssert.Equal(hash.ToLowerInvariant(), hash, $"{context} SHA-256 must be lowercase");
        HarnessAssert.Equal(ComputeSha256(json), hash, $"{context} raw snapshot SHA-256 mismatch");
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException($"{context} snapshot must be a JSON object");
        _ = ApiHarnessClient.RequiredInt(root, "schemaVersion");
        _ = ApiHarnessClient.RequiredArray(root["sections"], $"{context} snapshot sections");
        _ = ApiHarnessClient.RequiredArray(root["fields"], $"{context} snapshot fields");
        _ = ApiHarnessClient.RequiredArray(root["blocks"], $"{context} snapshot blocks");
        return (json, hash);
    }

    private static string ComputeSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ComputeBsonSha256(BsonDocument value)
        => Convert.ToHexString(SHA256.HashData(value.ToBson())).ToLowerInvariant();

    private async Task<JsonObject> RequireCanonicalStatisticConfigAsync(
        string formId,
        string token,
        string context,
        CancellationToken ct)
    {
        var response = await _api.GetAsync(
            $"api/dynamic-forms/{formId}/statistics",
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, context);
        var root = ApiHarnessClient.RequiredObject(response.Json, context);
        HarnessAssert.Equal("DYNAMIC_FORM", ApiHarnessClient.RequiredString(root, "ownerKind"), $"{context} ownerKind mismatch");
        HarnessAssert.Equal(formId, ApiHarnessClient.RequiredString(root, "ownerId"), $"{context} ownerId mismatch");
        HarnessAssert.True(
            ObjectId.TryParse(ApiHarnessClient.RequiredString(root, "configId"), out _),
            $"{context} configId is not an ObjectId");
        HarnessAssert.True(
            ObjectId.TryParse(ApiHarnessClient.RequiredString(root, "versionId"), out _),
            $"{context} versionId is not an ObjectId");
        HarnessAssert.True(ApiHarnessClient.RequiredInt(root, "versionNo") >= 1, $"{context} versionNo must be positive");
        HarnessAssert.True(ApiHarnessClient.RequiredInt(root, "revision") >= 1, $"{context} revision must be positive");
        HarnessAssert.Equal("LOCKED", ApiHarnessClient.RequiredString(root, "status"), $"{context} status mismatch");
        var configHash = ApiHarnessClient.RequiredString(root, "configHash");
        HarnessAssert.Equal(64, configHash.Length, $"{context} configHash length mismatch");
        HarnessAssert.Equal(configHash.ToLowerInvariant(), configHash, $"{context} configHash must be lowercase");
        _ = ApiHarnessClient.RequiredArray(root["dependencyPins"], $"{context} dependencyPins");
        _ = ApiHarnessClient.RequiredArray(root["fields"], $"{context} fields");
        _ = ApiHarnessClient.RequiredArray(root["tableConfig"], $"{context} tableConfig");
        var versions = ApiHarnessClient.RequiredArray(root["versions"], $"{context} versions");
        HarnessAssert.Equal(
            ApiHarnessClient.RequiredInt(root, "versionNo"),
            versions.Count,
            $"{context} version snapshot cardinality mismatch");
        var permissions = ApiHarnessClient.RequiredObject(root["permissions"], $"{context} permissions");
        HarnessAssert.True(ApiHarnessClient.RequiredBool(permissions, "canReadConfig"), $"{context} canReadConfig is false");
        HarnessAssert.True(ApiHarnessClient.RequiredBool(permissions, "canManageDraft"), $"{context} canManageDraft is false");
        return root;
    }

    private static JsonObject BuildCanonicalStatisticEnvelope(
        string commandId,
        JsonObject current,
        JsonObject payload)
        => new()
        {
            ["commandId"] = commandId,
            ["expectedRevision"] = ApiHarnessClient.RequiredInt(current, "revision"),
            ["expectedConfigHash"] = ApiHarnessClient.RequiredString(current, "configHash"),
            ["payload"] = payload
        };

    private static JsonObject BuildCanonicalFieldStatisticMutation(
        string fieldId,
        bool isStatistic)
    {
        var mutation = new JsonObject
        {
            ["fieldId"] = fieldId,
            ["isStatistic"] = isStatistic,
            ["statisticLabelCodes"] = new JsonArray()
        };
        if (isStatistic)
        {
            mutation["statistic"] = new JsonObject
            {
                ["aggregateOps"] = new JsonArray("COUNT"),
                ["bucketMode"] = "NONE",
                ["showInDetail"] = true,
                ["showInTree"] = false
            };
        }
        return mutation;
    }

    private static JsonObject RequireCanonicalStatisticMutationResult(
        ApiHarnessResponse response,
        JsonObject before,
        string formId,
        string context)
    {
        var result = ApiHarnessClient.RequiredObject(response.Json, context);
        HarnessAssert.Equal("DYNAMIC_FORM", ApiHarnessClient.RequiredString(result, "ownerKind"), $"{context} ownerKind mismatch");
        HarnessAssert.Equal(formId, ApiHarnessClient.RequiredString(result, "ownerId"), $"{context} ownerId mismatch");
        HarnessAssert.Equal(
            ApiHarnessClient.RequiredString(before, "configId"),
            ApiHarnessClient.RequiredString(result, "configId"),
            $"{context} configId changed");
        HarnessAssert.Equal(
            ApiHarnessClient.RequiredInt(before, "versionNo") + 1,
            ApiHarnessClient.RequiredInt(result, "versionNo"),
            $"{context} config versionNo mismatch");
        HarnessAssert.Equal(
            ApiHarnessClient.RequiredInt(before, "revision") + 1,
            ApiHarnessClient.RequiredInt(result, "revision"),
            $"{context} config revision mismatch");
        HarnessAssert.True(
            !string.Equals(
                ApiHarnessClient.RequiredString(before, "versionId"),
                ApiHarnessClient.RequiredString(result, "versionId"),
                StringComparison.Ordinal),
            $"{context} config versionId did not advance");
        HarnessAssert.True(
            !string.Equals(
                ApiHarnessClient.RequiredString(before, "configHash"),
                ApiHarnessClient.RequiredString(result, "configHash"),
                StringComparison.Ordinal),
            $"{context} configHash did not change");
        HarnessAssert.Equal("LOCKED", ApiHarnessClient.RequiredString(result, "status"), $"{context} status mismatch");
        _ = ApiHarnessClient.RequiredString(result, "receiptId");
        var permissions = ApiHarnessClient.RequiredObject(result["permissions"], $"{context} permissions");
        HarnessAssert.True(ApiHarnessClient.RequiredBool(permissions, "canManageDraft"), $"{context} canManageDraft is false");
        return result;
    }

    private async Task AssertCanonicalStatisticReceiptAsync(
        JsonObject result,
        string formId,
        string commandId,
        string actorId,
        CancellationToken ct)
    {
        var receipts = _database.GetCollection<BsonDocument>("stat_config_command_receipts");
        var receipt = await receipts.Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("ownerKind", "DYNAMIC_FORM"),
                    Builders<BsonDocument>.Filter.Eq("ownerId", formId),
                    Builders<BsonDocument>.Filter.Eq("commandId", commandId)))
            .SingleAsync(ct);
        var expectedReceiptId = ComputeSha256($"DYNAMIC_FORM\0{formId}\0{commandId}");
        HarnessAssert.Equal(expectedReceiptId, receipt["_id"].AsString, "Canonical statistic receipt deterministic id mismatch");
        HarnessAssert.Equal(
            expectedReceiptId,
            ApiHarnessClient.RequiredString(result, "receiptId"),
            "Canonical statistic API receiptId mismatch");
        HarnessAssert.Equal("UPDATE_DYNAMIC_FORM_STATISTICS", receipt["commandKind"].AsString, "Canonical statistic receipt commandKind mismatch");
        var receiptActor = receipt["actorUserId"];
        HarnessAssert.True(receiptActor.IsObjectId, "Canonical statistic receipt actor BSON type mismatch");
        HarnessAssert.Equal(actorId, receiptActor.AsObjectId.ToString(), "Canonical statistic receipt actor mismatch");
        HarnessAssert.Equal(
            ApiHarnessClient.RequiredInt(result, "revision"),
            checked((int)receipt["resultRevision"].ToInt64()),
            "Canonical statistic receipt resultRevision mismatch");
        HarnessAssert.Equal(
            ApiHarnessClient.RequiredString(result, "configHash"),
            receipt["resultConfigHash"].AsString,
            "Canonical statistic receipt resultConfigHash mismatch");
    }

    private async Task<Dictionary<string, long>> CaptureP2StatisticIsolationCountsAsync(
        CancellationToken ct)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var collectionName in P2StatisticIsolationCollections)
        {
            result[collectionName] = await _database
                .GetCollection<BsonDocument>(collectionName)
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
        }
        return result;
    }

    private static void AssertP2StatisticIsolationUnchanged(
        IReadOnlyDictionary<string, long> before,
        IReadOnlyDictionary<string, long> after,
        string context)
    {
        HarnessAssert.Equal(before.Count, after.Count, $"{context} statistic isolation collection count mismatch");
        foreach (var collectionName in P2StatisticIsolationCollections)
        {
            HarnessAssert.True(before.ContainsKey(collectionName), $"{context} missing before count for {collectionName}");
            HarnessAssert.True(after.ContainsKey(collectionName), $"{context} missing after count for {collectionName}");
            HarnessAssert.Equal(
                before[collectionName],
                after[collectionName],
                $"{context} wrote prohibited statistic collection {collectionName}");
        }
    }

    private static void AssertCanonicalStatisticConfigNotFound(
        ApiHarnessResponse response,
        DynamicFormTemplate form,
        string context)
    {
        AssertErrorCode(response, "DYNAMIC_FORM_TEMPLATE_NOT_FOUND", context);
        var envelope = ApiHarnessClient.RequiredObject(response.Json, $"{context} not-found envelope");
        var details = ApiHarnessClient.RequiredObject(envelope["details"], $"{context} not-found details");
        HarnessAssert.Equal(2, details.Count, $"{context} canonical not-found details cardinality mismatch");
        HarnessAssert.Equal("DYNAMIC_FORM", ApiHarnessClient.RequiredString(details, "ownerKind"), $"{context} ownerKind mismatch");
        HarnessAssert.Equal(form.Id, ApiHarnessClient.RequiredString(details, "ownerId"), $"{context} echoed ownerId mismatch");

        var sensitiveValues = new[]
            {
                form.Code,
                form.Name,
                form.CreatedByUsername,
                form.CreatedByUserId,
                form.PublishedByUserId,
                form.FamilyId,
                form.PublishedSchemaHash
            }
            .Where(value =>
                !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, form.Id, StringComparison.Ordinal))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal);
        foreach (var sensitiveValue in sensitiveValues)
        {
            HarnessAssert.True(
                !response.Body.Contains(sensitiveValue, StringComparison.Ordinal),
                $"{context} raw canonical not-found body leaked Dynamic Form metadata");
            HarnessAssert.True(
                !JsonContainsString(response.Json, sensitiveValue),
                $"{context} parsed canonical not-found envelope leaked Dynamic Form metadata");
        }
    }
    private async Task<JsonObject> CreateVersionAssignmentAsync(
        string dynamicFormTemplateId,
        string name,
        string adminToken,
        CancellationToken ct)
    {
        var workId = HarnessAssert.Required(_bindingWorkId, "P2-DF-LC-003 binding work");
        var response = await PostAssignmentAsync(
            workId,
            dynamicFormTemplateId,
            name,
            _assigneeId,
            adminToken,
            ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Created, $"create assignment {name}");
        return ApiHarnessClient.RequiredObject(response.Json, $"assignment {name}");
    }

    private Task<ApiHarnessResponse> PostAssignmentAsync(
        string workId,
        string dynamicFormTemplateId,
        string name,
        string assigneeUserId,
        string token,
        CancellationToken ct)
        => _api.PostAsync(
            $"api/works/{workId}/assignments",
            new
            {
                name,
                dynamicFormTemplateId,
                assignmentType = "ONCE",
                aggregationType = "MATRIX",
                dueAtUtc = "2026-08-15T10:00:00Z",
                assigneeUserIds = new[] { assigneeUserId },
                assigneeUnitIds = Array.Empty<string>(),
                leaderWatcherUserIds = Array.Empty<string>(),
                description = "P2 exact version binding",
                isActive = true
            },
            token,
            ct: ct);

    private async Task InsertOwnedWorkAsync(
        string workId,
        string code,
        string name,
        string ownerUserId,
        string ownerUsername,
        string ownerFullName,
        CancellationToken ct)
    {
        var fixedAt = new DateTime(2026, 7, 22, 3, 0, 0, DateTimeKind.Utc);
        await _database.GetCollection<Work>("works").InsertOneAsync(
            new Work
            {
                Id = workId,
                AutoCode = code,
                Code = code,
                Name = name,
                Description = "P2 isolated assignment authorization fixture",
                Status = WorkStatus.S1,
                Type = WorkType.TASK,
                Priority = WorkPriority.MEDIUM,
                LeaderDirectiveUserId = null!,
                LeaderWatchUserIds = [],
                LeaderWatch = [],
                Owner = new UserRef
                {
                    UserId = ownerUserId,
                    Username = ownerUsername,
                    FullName = ownerFullName
                },
                CreatedByUserId = ownerUserId,
                UpdatedByUserId = ownerUserId,
                CreatedAtUtc = fixedAt,
                UpdatedAtUtc = fixedAt,
                IsDeleted = false
            },
            cancellationToken: ct);
    }

    private async Task<AssignmentWriteSnapshot> CaptureAssignmentWriteSnapshotAsync(
        string workId,
        CancellationToken ct)
    {
        var workIdFilter = BuildWorkIdFilter(workId);
        return new AssignmentWriteSnapshot(
            await _database.GetCollection<WorkAssignment>("work_assignments")
                .CountDocumentsAsync(x => x.WorkId == workId, cancellationToken: ct),
            await _database.GetCollection<WorkTemplateAssignee>("work_template_assignees")
                .CountDocumentsAsync(x => x.WorkId == workId, cancellationToken: ct),
            await _database.GetCollection<BsonDocument>("doc_roles")
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct),
            await _database.GetCollection<BsonDocument>("work_list_doc_roles")
                .CountDocumentsAsync(workIdFilter, cancellationToken: ct),
            await _database.GetCollection<BsonDocument>("assignment_list_doc_roles")
                .CountDocumentsAsync(workIdFilter, cancellationToken: ct),
            await _database.GetCollection<BsonDocument>("my_report_template_list_doc_roles")
                .CountDocumentsAsync(workIdFilter, cancellationToken: ct),
            await _database.GetCollection<BsonDocument>("my_report_period_list_doc_roles")
                .CountDocumentsAsync(workIdFilter, cancellationToken: ct),
            await _database.GetCollection<BsonDocument>("review_report_list_doc_roles")
                .CountDocumentsAsync(workIdFilter, cancellationToken: ct),
            await _database.GetCollection<BsonDocument>("review_assignment_summary_doc_roles")
                .CountDocumentsAsync(workIdFilter, cancellationToken: ct));
    }

    private static FilterDefinition<BsonDocument> BuildWorkIdFilter(string workId)
    {
        var f = Builders<BsonDocument>.Filter;
        return ObjectId.TryParse(workId, out var objectId)
            ? f.Or(f.Eq("workId", objectId), f.Eq("workId", workId))
            : f.Eq("workId", workId);
    }

    private sealed record AssignmentWriteSnapshot(
        long AssignmentCount,
        long BindingCount,
        long DocRoleCount,
        long WorkListDocRoleCount,
        long AssignmentListDocRoleCount,
        long MyReportTemplateListDocRoleCount,
        long MyReportPeriodListDocRoleCount,
        long ReviewReportListDocRoleCount,
        long ReviewAssignmentSummaryDocRoleCount);

    private static void AssertAssignmentVersionIdentity(
        JsonObject assignment,
        string templateId,
        string familyId,
        int versionNo,
        string schemaHash,
        string context)
    {
        HarnessAssert.Equal(templateId, ApiHarnessClient.RequiredString(assignment, "dynamicFormTemplateId"), $"{context} template ID mismatch");
        HarnessAssert.Equal(familyId, ApiHarnessClient.RequiredString(assignment, "dynamicFormFamilyId"), $"{context} family ID mismatch");
        HarnessAssert.Equal(versionNo, ApiHarnessClient.RequiredInt(assignment, "dynamicFormVersionNo"), $"{context} version mismatch");
        HarnessAssert.Equal(schemaHash, ApiHarnessClient.RequiredString(assignment, "dynamicFormSchemaHash"), $"{context} schema hash mismatch");
    }

    private static void AssertAssignmentVersionIdentity(
        WorkAssignment assignment,
        string templateId,
        string familyId,
        int versionNo,
        string schemaHash,
        string context)
    {
        HarnessAssert.Equal(templateId, assignment.DynamicFormTemplateId, $"{context} template ID mismatch");
        HarnessAssert.Equal(familyId, assignment.DynamicFormFamilyId, $"{context} family ID mismatch");
        HarnessAssert.Equal(versionNo, assignment.DynamicFormVersionNo, $"{context} version mismatch");
        HarnessAssert.Equal(schemaHash, assignment.DynamicFormSchemaHash, $"{context} schema hash mismatch");
    }

    private static void AssertAssignmentVersionIdentity(
        WorkTemplateAssignee binding,
        string templateId,
        string familyId,
        int versionNo,
        string schemaHash,
        string context)
    {
        HarnessAssert.Equal(templateId, binding.DynamicFormTemplateId, $"{context} template ID mismatch");
        HarnessAssert.Equal(familyId, binding.DynamicFormFamilyId, $"{context} family ID mismatch");
        HarnessAssert.Equal(versionNo, binding.DynamicFormVersionNo, $"{context} version mismatch");
        HarnessAssert.Equal(schemaHash, binding.DynamicFormSchemaHash, $"{context} schema hash mismatch");
    }

    private async Task<JsonObject> RequireFormDetailAsync(
        string formId,
        string token,
        string context,
        CancellationToken ct)
    {
        var response = await _api.GetAsync($"api/dynamic-forms/{formId}", token, ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, context);
        var detail = ApiHarnessClient.RequiredObject(response.Json, context);
        HarnessAssert.Equal(formId, ApiHarnessClient.RequiredString(detail, "id"), $"{context} form ID mismatch");
        return detail;
    }

    private static void AssertPublishedOwnerActions(JsonObject detail, string context)
    {
        HarnessAssert.True(ApiHarnessClient.RequiredBool(detail, "canMutate"), $"{context} canMutate mismatch");
        HarnessAssert.True(ApiHarnessClient.RequiredBool(detail, "canClone"), $"{context} canClone mismatch");
        AssertActions(
            detail,
            context,
            ("canRead", true),
            ("canUpdate", false),
            ("canDelete", false),
            ("canPublish", false),
            ("canCreateVersion", true),
            ("canViewHistory", true),
            ("canClone", true),
            ("canImport", false),
            ("canUpdateStatistics", true));
    }

    private static void AssertDraftOwnerActions(JsonObject detail, string context)
    {
        HarnessAssert.True(ApiHarnessClient.RequiredBool(detail, "canMutate"), $"{context} canMutate mismatch");
        HarnessAssert.True(ApiHarnessClient.RequiredBool(detail, "canClone"), $"{context} canClone mismatch");
        AssertActions(
            detail,
            context,
            ("canRead", true),
            ("canUpdate", true),
            ("canDelete", true),
            ("canPublish", true),
            ("canCreateVersion", false),
            ("canViewHistory", true),
            ("canClone", true),
            ("canImport", true),
            ("canUpdateStatistics", true));
    }

    private static void AssertRuntimeReadOnlyActions(JsonObject detail, string context)
    {
        HarnessAssert.True(!ApiHarnessClient.RequiredBool(detail, "canMutate"), $"{context} canMutate must fail closed");
        HarnessAssert.True(!ApiHarnessClient.RequiredBool(detail, "canClone"), $"{context} canClone must fail closed");
        AssertActions(
            detail,
            context,
            ("canRead", true),
            ("canUpdate", false),
            ("canDelete", false),
            ("canPublish", false),
            ("canCreateVersion", false),
            ("canViewHistory", false),
            ("canClone", false),
            ("canImport", false),
            ("canUpdateStatistics", false));
    }

    private static void AssertCloneGrantActions(JsonObject detail, string context)
    {
        HarnessAssert.True(!ApiHarnessClient.RequiredBool(detail, "canMutate"), $"{context} canMutate must fail closed");
        HarnessAssert.True(ApiHarnessClient.RequiredBool(detail, "canClone"), $"{context} canClone mismatch");
        AssertActions(
            detail,
            context,
            ("canRead", true),
            ("canUpdate", false),
            ("canDelete", false),
            ("canPublish", false),
            ("canCreateVersion", false),
            ("canViewHistory", false),
            ("canClone", true),
            ("canImport", false),
            ("canUpdateStatistics", false));
    }

    private static void AssertActions(
        JsonObject detail,
        string context,
        params (string Property, bool Expected)[] expected)
    {
        var actions = ApiHarnessClient.RequiredObject(detail["actions"], $"{context} actions");
        foreach (var item in expected)
        {
            HarnessAssert.Equal(
                item.Expected,
                ApiHarnessClient.RequiredBool(actions, item.Property),
                $"{context} {item.Property} mismatch");
        }
    }

    private async Task AssertDesignSearchVisibilityAsync(
        string token,
        string formId,
        bool expectedVisible,
        string context,
        CancellationToken ct)
    {
        var response = await _api.PostAsync(
            "api/dynamic-forms/search",
            new { code = FormCode, page = 0, pageSize = 100 },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"{context} Dynamic Form search");
        var rows = ApiHarnessClient.RequiredArray(
            ApiHarnessClient.RequiredObject(response.Json, $"{context} Dynamic Form search")["rows"],
            $"{context} Dynamic Form rows");
        var visible = rows.OfType<JsonObject>()
            .Any(x => string.Equals(x["id"]?.GetValue<string>(), formId, StringComparison.Ordinal));
        HarnessAssert.Equal(expectedVisible, visible, $"{context} Dynamic Form search visibility mismatch");
    }

    private async Task AssertRuntimeReadOnlyAsync(
        string token,
        string formId,
        string context,
        CancellationToken ct)
    {
        var detail = await RequireFormDetailAsync(formId, token, context, ct);
        HarnessAssert.True(!ApiHarnessClient.RequiredBool(detail, "canViewByCloneGrant"), $"{context} unexpectedly has clone grant");
        AssertRuntimeReadOnlyActions(detail, $"{context} actions");
        await AssertDesignSearchVisibilityAsync(token, formId, false, context, ct);
        await AssertHistoryDeniedAsync(token, formId, $"{context} history", ct);
        await AssertMutationDeniedAsync(token, formId, $"{context} mutation", ct);
    }

    private async Task AssertOutsiderDeniedAsync(
        string token,
        string formId,
        CancellationToken ct)
    {
        await AssertDesignSearchVisibilityAsync(token, formId, false, "outsider", ct);
        await AssertFormGetDeniedAsync(token, formId, "outsider", ct);
        await AssertHistoryDeniedAsync(token, formId, "outsider history", ct);
        await AssertMutationDeniedAsync(token, formId, "outsider mutation", ct);
    }

    private async Task AssertFormGetDeniedAsync(
        string token,
        string formId,
        string context,
        CancellationToken ct)
    {
        var current = await LoadFormDocumentAsync(formId, ct);
        var response = await _api.GetAsync($"api/dynamic-forms/{formId}", token, ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Forbidden, $"{context} Dynamic Form get");
        AssertForbiddenFormResponse(
            response,
            current,
            "DYNAMIC_FORM_READ_FORBIDDEN",
            "READ_DYNAMIC_FORM",
            $"{context} Dynamic Form get");
    }

    private async Task AssertHistoryDeniedAsync(
        string token,
        string formId,
        string context,
        CancellationToken ct)
    {
        var current = await LoadFormDocumentAsync(formId, ct);
        var response = await _api.GetAsync($"api/dynamic-forms/{formId}/versions", token, ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Forbidden, context);
        AssertForbiddenFormResponse(
            response,
            current,
            "DYNAMIC_FORM_READ_FORBIDDEN",
            "READ_VERSION_HISTORY",
            context);
    }

    private async Task AssertMutationDeniedAsync(
        string token,
        string formId,
        string context,
        CancellationToken ct)
    {
        var current = await LoadFormDocumentAsync(formId, ct);
        var update = await _api.PutAsync(
            $"api/dynamic-forms/{formId}",
            BuildFormUpdateRequest(FormCode, "must not mutate", current.Revision),
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(update, HttpStatusCode.Forbidden, $"{context} update");
        AssertForbiddenFormResponse(
            update,
            current,
            "DYNAMIC_FORM_MUTATE_FORBIDDEN",
            "MUTATE_DYNAMIC_FORM",
            $"{context} update");

        var successor = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/versions",
            new { expectedRevision = current.Revision },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(successor, HttpStatusCode.Forbidden, $"{context} successor");
        AssertForbiddenFormResponse(
            successor,
            current,
            "DYNAMIC_FORM_MUTATE_FORBIDDEN",
            "MUTATE_DYNAMIC_FORM",
            $"{context} successor");

        var publish = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/publish",
            new { expectedRevision = current.Revision },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(publish, HttpStatusCode.Forbidden, $"{context} publish");
        AssertForbiddenFormResponse(
            publish,
            current,
            "DYNAMIC_FORM_MUTATE_FORBIDDEN",
            "MUTATE_DYNAMIC_FORM",
            $"{context} publish");

        var import = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/blocks/import-dynamic-excel",
            new
            {
                dynamicExcelTemplateId = _dynamicExcelId,
                sectionId = "main",
                expectedRevision = current.Revision
            },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(import, HttpStatusCode.Forbidden, $"{context} import");
        AssertForbiddenFormResponse(
            import,
            current,
            "DYNAMIC_FORM_MUTATE_FORBIDDEN",
            "MUTATE_DYNAMIC_FORM",
            $"{context} import");

        var delete = await _api.DeleteAsync(
            $"api/dynamic-forms/{formId}?expectedRevision={current.Revision}",
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(delete, HttpStatusCode.Forbidden, $"{context} delete");
        AssertForbiddenFormResponse(
            delete,
            current,
            "DYNAMIC_FORM_MUTATE_FORBIDDEN",
            "MUTATE_DYNAMIC_FORM",
            $"{context} delete");

        var clone = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/clone",
            new { code = $"DENIED_{Guid.NewGuid():N}", name = "must not clone" },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(clone, HttpStatusCode.Forbidden, $"{context} clone");
        AssertForbiddenFormResponse(
            clone,
            current,
            "DYNAMIC_FORM_CLONE_FORBIDDEN",
            "CLONE_DYNAMIC_FORM",
            $"{context} clone");

        AssertFormDocumentUnchanged(current, await LoadFormDocumentAsync(formId, ct), context);
    }

    private async Task AssertVersionHistoryAsync(
        string token,
        string sourceId,
        IReadOnlyList<string> expectedVersionIds,
        string context,
        CancellationToken ct)
    {
        var response = await _api.GetAsync($"api/dynamic-forms/{sourceId}/versions", token, ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, context);
        var body = ApiHarnessClient.RequiredObject(response.Json, context);
        var rows = ApiHarnessClient.RequiredArray(body["versions"], $"{context} versions");
        HarnessAssert.Equal(expectedVersionIds.Count, rows.Count, $"{context} count mismatch");
        for (var index = 0; index < expectedVersionIds.Count; index++)
        {
            var row = ApiHarnessClient.RequiredObject(rows[index], $"{context}[{index}]");
            HarnessAssert.Equal(expectedVersionIds[index], ApiHarnessClient.RequiredString(row, "id"), $"{context} order mismatch");
            HarnessAssert.Equal(expectedVersionIds.Count - index, ApiHarnessClient.RequiredInt(row, "versionNo"), $"{context} version number mismatch");
        }
    }

    private async Task<JsonObject> CreateDraftFormAsync(
        string code,
        string name,
        string token,
        CancellationToken ct)
    {
        var response = await _api.PostAsync(
            "api/dynamic-forms",
            BuildFormRequest(code, name, updated: false),
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"create isolated Dynamic Form {code}");
        var form = ApiHarnessClient.RequiredObject(response.Json, $"created isolated Dynamic Form {code}");
        var formId = ApiHarnessClient.RequiredString(form, "id");
        HarnessAssert.Equal(code, ApiHarnessClient.RequiredString(form, "code"), $"Created Dynamic Form {code} code mismatch");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(form, "revision"), $"Created Dynamic Form {code} revision mismatch");
        HarnessAssert.True(form["isPublished"]?.GetValue<bool>() == false, $"Created Dynamic Form {code} is not a draft");
        _p2FormIds.Add(code, formId);

        var persisted = await LoadFormDocumentAsync(formId, ct);
        HarnessAssert.Equal(1, persisted.Revision, $"Mongo Dynamic Form {code} revision mismatch");
        HarnessAssert.True(!persisted.IsPublished, $"Mongo Dynamic Form {code} is not a draft");
        return form;
    }

    private JsonObject BuildFormUpdateRequest(string code, string name, int? expectedRevision)
    {
        var request = BuildFormRequest(code, name, updated: true);
        request.Remove("code");
        if (expectedRevision.HasValue)
            request["expectedRevision"] = expectedRevision.Value;
        return request;
    }

    private async Task<DynamicFormTemplate> LoadFormDocumentAsync(string formId, CancellationToken ct)
        => await _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
            .Find(x => x.Id == formId && !x.IsDeleted)
            .SingleAsync(ct);

    private async Task<List<BsonDocument>> LoadRawFormCollectionSnapshotAsync(CancellationToken ct)
        => await _database.GetCollection<BsonDocument>("dynamic_form_templates")
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);

    private static void AssertRawFormCollectionSnapshotUnchanged(
        IReadOnlyList<BsonDocument> before,
        IReadOnlyList<BsonDocument> after,
        string context)
    {
        HarnessAssert.Equal(before.Count, after.Count, $"{context} changed Dynamic Form cardinality");
        for (var index = 0; index < before.Count; index++)
        {
            HarnessAssert.True(
                before[index].Equals(after[index]),
                $"{context} changed Dynamic Form Mongo document index {index}");
        }
    }

    private static void AssertErrorCode(
        ApiHarnessResponse response,
        string expectedErrorCode,
        string context)
        => HarnessAssert.Equal(
            expectedErrorCode,
            ApiHarnessClient.RequiredString(response.Json, "errorCode"),
            $"{context} errorCode mismatch");

    private static void AssertDraftFormFlowBindRejected(
        ApiHarnessResponse response,
        string context,
        bool allowDeletedRaceNotFound = false)
    {
        var errorCode = ApiHarnessClient.RequiredString(response.Json, "errorCode");
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            HarnessAssert.Equal("COMMON_VALIDATION_FAILED", errorCode, $"{context} errorCode mismatch");
            HarnessAssert.Equal(
                "DYNAMIC_FLOW_TEMPLATE_PUBLISHED_FORMS_REQUIRED",
                ApiHarnessClient.FindStringRecursive(response.Json, "reason"),
                $"{context} published-form reason mismatch");
            return;
        }

        if (allowDeletedRaceNotFound && response.StatusCode == HttpStatusCode.NotFound)
        {
            HarnessAssert.Equal("COMMON_NOT_FOUND", errorCode, $"{context} delete-winning race errorCode mismatch");
            HarnessAssert.Equal(
                "DYNAMIC_FORM_TEMPLATE_NOT_FOUND",
                ApiHarnessClient.FindStringRecursive(response.Json, "reason"),
                $"{context} delete-winning race reason mismatch");
            return;
        }

        throw new InvalidOperationException(
            $"{context} must reject draft-form binding before Flow writes. HTTP={(int)response.StatusCode}; errorCode={errorCode}");
    }

    private static void AssertForbiddenFormResponse(
        ApiHarnessResponse response,
        DynamicFormTemplate form,
        string expectedErrorCode,
        string expectedAction,
        string context)
    {
        AssertErrorCode(response, expectedErrorCode, context);
        var envelope = ApiHarnessClient.RequiredObject(response.Json, $"{context} forbidden envelope");
        var details = ApiHarnessClient.RequiredObject(envelope["details"], $"{context} forbidden details");
        HarnessAssert.Equal(2, details.Count, $"{context} forbidden details must contain only reason/action");
        HarnessAssert.Equal(
            "DYNAMIC_FORM_ACCESS_FORBIDDEN",
            ApiHarnessClient.RequiredString(details, "reason"),
            $"{context} generic forbidden reason mismatch");
        HarnessAssert.Equal(
            expectedAction,
            ApiHarnessClient.RequiredString(details, "action"),
            $"{context} generic forbidden action mismatch");

        var sensitiveValues = new[]
            {
                form.Id,
                form.Code,
                form.Name,
                form.CreatedByUsername,
                form.CreatedByUserId,
                form.PublishedByUserId,
                form.FamilyId,
                form.PublishedSchemaHash
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal);
        foreach (var sensitiveValue in sensitiveValues)
        {
            HarnessAssert.True(
                !response.Body.Contains(sensitiveValue, StringComparison.Ordinal),
                $"{context} raw forbidden body leaked sensitive Dynamic Form metadata");
            HarnessAssert.True(
                !JsonContainsString(response.Json, sensitiveValue),
                $"{context} parsed forbidden envelope leaked sensitive Dynamic Form metadata");
        }
    }

    private static bool JsonContainsString(JsonNode? node, string value)
    {
        if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            return text.Contains(value, StringComparison.Ordinal);
        if (node is JsonObject obj)
            return obj.Any(pair => JsonContainsString(pair.Value, value));
        return node is JsonArray array && array.Any(item => JsonContainsString(item, value));
    }

    private static void AssertRevisionConflict(
        ApiHarnessResponse response,
        int expectedRevision,
        int currentRevision,
        string context)
    {
        AssertErrorCode(response, "DYNAMIC_FORM_REVISION_CONFLICT", context);
        HarnessAssert.Equal(
            expectedRevision,
            ApiHarnessClient.FindIntRecursive(response.Json, "expectedRevision") ?? -1,
            $"{context} expectedRevision metadata mismatch");
        HarnessAssert.Equal(
            currentRevision,
            ApiHarnessClient.FindIntRecursive(response.Json, "currentRevision") ?? -1,
            $"{context} currentRevision metadata mismatch");
    }

    private static void AssertFormDocumentUnchanged(
        DynamicFormTemplate before,
        DynamicFormTemplate after,
        string context)
    {
        HarnessAssert.True(
            before.ToBsonDocument().Equals(after.ToBsonDocument()),
            $"{context} changed the Mongo Dynamic Form document");
    }

    private string BuildP4FixtureMarker(string scope)
    {
        var runKey = Path.GetFileName(Directory.GetParent(_iterationRoot)?.FullName) ?? "run";
        var iteration = Path.GetFileName(_iterationRoot);
        var input = Encoding.UTF8.GetBytes($"{runKey}/{iteration}/{scope}");
        var digest = Convert.ToHexString(SHA256.HashData(input))[..12];
        return $"P4_{scope.ToUpperInvariant()}_{digest}";
    }

    private async Task<CaseObservation> VerifyFlowDraftSaveRaceAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var flowId = HarnessAssert.Required(_flowId, "P1-BE-008 flow");
        var formId = HarnessAssert.Required(_formId, "P1-BE-005 form");
        var versionId = HarnessAssert.Required(_reopenedFlowVersionId, "P1-BE-008 reopened draft");
        var expectedRevision = _reopenedFlowDraftRevision
            ?? throw new InvalidOperationException("P1-BE-008 reopened draft revision is missing");
        var expectedHash = HarnessAssert.Required(_reopenedFlowPayloadHash, "P1-BE-008 reopened hash");
        var versions = _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions");
        var receipts = _database.GetCollection<DynamicFlowDefinitionCommandReceipt>("dynamic_flow_definition_command_receipts");
        var audits = _database.GetCollection<UserActionLog>("user_action_logs");
        var receiptCountBefore = await receipts.CountDocumentsAsync(FilterDefinition<DynamicFlowDefinitionCommandReceipt>.Empty, cancellationToken: ct);
        var auditCountBefore = await audits.CountDocumentsAsync(FilterDefinition<UserActionLog>.Empty, cancellationToken: ct);

        var payloadA = BuildFlowPayload(formId);
        ApiHarnessClient.RequiredObject(ApiHarnessClient.RequiredArray(payloadA["nodes"], "race A nodes")[0], "race A node")["name"] = "Save race candidate A";
        var payloadB = BuildFlowPayload(formId);
        ApiHarnessClient.RequiredObject(ApiHarnessClient.RequiredArray(payloadB["nodes"], "race B nodes")[0], "race B node")["name"] = "Save race candidate B";
        var requestA = new JsonObject
        {
            ["commandId"] = "p4-save-race-a",
            ["expectedDraftRevision"] = expectedRevision,
            ["expectedPayloadHash"] = expectedHash,
            ["payload"] = payloadA
        };
        var requestB = new JsonObject
        {
            ["commandId"] = "p4-save-race-b",
            ["expectedDraftRevision"] = expectedRevision,
            ["expectedPayloadHash"] = expectedHash,
            ["payload"] = payloadB
        };
        var path = $"api/dynamic-flow-templates/{flowId}/versions/{versionId}/draft";
        var firstTask = _api.PutAsync(path, requestA, token, ct: ct);
        var secondTask = _api.PutAsync(path, requestB, token, ct: ct);
        await Task.WhenAll(firstTask, secondTask);
        var first = await firstTask;
        var second = await secondTask;
        var responses = new[] { first, second };
        var successes = responses.Where(item => item.StatusCode == HttpStatusCode.OK).ToArray();
        var conflicts = responses.Where(item => item.StatusCode == HttpStatusCode.Conflict).ToArray();
        HarnessAssert.Equal(1, successes.Length, "Concurrent P4 save/save must have exactly one winner");
        HarnessAssert.Equal(1, conflicts.Length, "Concurrent P4 save/save must have exactly one loser");
        AssertErrorCode(conflicts[0], "DYNAMIC_FLOW_REVISION_CONFLICT", "concurrent P4 save/save loser");

        var winnerIsA = ReferenceEquals(successes[0], first);
        var winnerRequest = winnerIsA ? requestA : requestB;
        var otherPayload = winnerIsA ? payloadB : payloadA;
        var winner = ApiHarnessClient.RequiredObject(successes[0].Json, "concurrent P4 save winner");
        var winnerHash = ApiHarnessClient.RequiredString(winner, "payloadHash");
        HarnessAssert.Equal(expectedRevision + 1, ApiHarnessClient.RequiredInt(winner, "draftRevision"), "P4 save winner revision mismatch");
        var stored = await versions.Find(x => x.Id == versionId && x.TemplateId == flowId && !x.IsDeleted).SingleAsync(ct);
        HarnessAssert.Equal(expectedRevision + 1, stored.DraftRevision, "P4 save race advanced Mongo draftRevision more than once");
        HarnessAssert.Equal(winnerHash, stored.PayloadHash, "P4 save winner response does not match Mongo hash");
        HarnessAssert.Equal(receiptCountBefore + 1, await receipts.CountDocumentsAsync(FilterDefinition<DynamicFlowDefinitionCommandReceipt>.Empty, cancellationToken: ct), "P4 save race committed more than one receipt");
        HarnessAssert.Equal(auditCountBefore + 1, await audits.CountDocumentsAsync(FilterDefinition<UserActionLog>.Empty, cancellationToken: ct), "P4 save race committed more than one success audit");

        var exactReplay = await _api.PutAsync(path, winnerRequest.DeepClone(), token, ct: ct);
        ApiHarnessClient.ExpectStatus(exactReplay, HttpStatusCode.OK, "P4 save winner exact replay");
        HarnessAssert.Equal(winnerHash, ApiHarnessClient.RequiredString(exactReplay.Json, "payloadHash"), "P4 save exact replay hash mismatch");
        var changedReplayRequest = winnerRequest.DeepClone().AsObject();
        changedReplayRequest["payload"] = otherPayload.DeepClone();
        var changedReplay = await _api.PutAsync(path, changedReplayRequest, token, ct: ct);
        ApiHarnessClient.ExpectStatus(changedReplay, HttpStatusCode.Conflict, "P4 save changed replay");
        AssertErrorCode(changedReplay, "DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT", "P4 save changed replay");
        HarnessAssert.Equal(receiptCountBefore + 1, await receipts.CountDocumentsAsync(FilterDefinition<DynamicFlowDefinitionCommandReceipt>.Empty, cancellationToken: ct), "P4 save replays wrote a receipt");
        HarnessAssert.Equal(auditCountBefore + 1, await audits.CountDocumentsAsync(FilterDefinition<UserActionLog>.Empty, cancellationToken: ct), "P4 save replays wrote a success audit");

        var laterSaveRequest = new JsonObject
        {
            ["commandId"] = "p4-save-after-receipt-snapshot",
            ["expectedDraftRevision"] = stored.DraftRevision,
            ["expectedPayloadHash"] = stored.PayloadHash,
            ["payload"] = otherPayload.DeepClone()
        };
        var laterSave = await _api.PutAsync(path, laterSaveRequest, token, ct: ct);
        ApiHarnessClient.ExpectStatus(laterSave, HttpStatusCode.OK, "P4 later save after winner receipt");
        var later = ApiHarnessClient.RequiredObject(laterSave.Json, "P4 later save body");
        var laterRevision = ApiHarnessClient.RequiredInt(later, "draftRevision");
        var laterHash = ApiHarnessClient.RequiredString(later, "payloadHash");
        HarnessAssert.Equal(stored.DraftRevision + 1, laterRevision, "P4 later save revision mismatch");
        HarnessAssert.True(
            !string.Equals(laterHash, winnerHash, StringComparison.Ordinal),
            "P4 later save did not change the payload hash");
        var afterLaterSave = await versions
            .Find(x => x.Id == versionId && x.TemplateId == flowId && !x.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.Equal(laterRevision, afterLaterSave.DraftRevision, "P4 later save Mongo revision mismatch");
        HarnessAssert.Equal(laterHash, afterLaterSave.PayloadHash, "P4 later save Mongo hash mismatch");
        HarnessAssert.Equal(
            receiptCountBefore + 2,
            await receipts.CountDocumentsAsync(
                FilterDefinition<DynamicFlowDefinitionCommandReceipt>.Empty,
                cancellationToken: ct),
            "P4 later save receipt cardinality");
        HarnessAssert.Equal(
            auditCountBefore + 2,
            await audits.CountDocumentsAsync(
                FilterDefinition<UserActionLog>.Empty,
                cancellationToken: ct),
            "P4 later save audit cardinality");

        var formsForSaveReplay = _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates");
        var saveReplayForm = await formsForSaveReplay
            .Find(x => x.Id == formId)
            .SingleAsync(ct);
        ApiHarnessResponse historicalReplay;
        try
        {
            var hideForm = await formsForSaveReplay.UpdateOneAsync(
                x => x.Id == formId,
                Builders<DynamicFormTemplate>.Update.Set(x => x.IsDeleted, true),
                cancellationToken: ct);
            HarnessAssert.Equal(1L, hideForm.ModifiedCount, "P4 historical save replay hidden-Form fixture");
            historicalReplay = await _api.PutAsync(path, winnerRequest.DeepClone(), token, ct: ct);
        }
        finally
        {
            await formsForSaveReplay.UpdateOneAsync(
                x => x.Id == formId,
                Builders<DynamicFormTemplate>.Update.Set(x => x.IsDeleted, saveReplayForm.IsDeleted),
                cancellationToken: CancellationToken.None);
        }
        ApiHarnessClient.ExpectStatus(
            historicalReplay,
            HttpStatusCode.OK,
            "P4 exact replay after a subsequent mutation and Form state change");
        HarnessAssert.Equal(
            expectedRevision + 1,
            ApiHarnessClient.RequiredInt(historicalReplay.Json, "draftRevision"),
            "P4 historical replay returned the current instead of receipt revision");
        HarnessAssert.Equal(
            winnerHash,
            ApiHarnessClient.RequiredString(historicalReplay.Json, "payloadHash"),
            "P4 historical replay returned the current instead of receipt hash");
        var afterHistoricalReplay = await versions
            .Find(x => x.Id == versionId && x.TemplateId == flowId && !x.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.Equal(
            afterLaterSave.ToBsonDocument(),
            afterHistoricalReplay.ToBsonDocument(),
            "P4 historical exact replay mutated the current draft");
        var winnerReceipt = await receipts
            .Find(x =>
                x.ActorUserId == _ownerId &&
                x.CommandKind == "SAVE_DRAFT" &&
                x.CommandId == (winnerIsA ? "p4-save-race-a" : "p4-save-race-b"))
            .SingleAsync(ct);
        HarnessAssert.True(
            winnerReceipt.ResultFamilySnapshot is not null &&
            winnerReceipt.ResultVersionSnapshot is not null,
            "P4 save receipt omitted exact committed result snapshots");
        HarnessAssert.Equal(
            receiptCountBefore + 2,
            await receipts.CountDocumentsAsync(
                FilterDefinition<DynamicFlowDefinitionCommandReceipt>.Empty,
                cancellationToken: ct),
            "P4 historical replay wrote a receipt");
        HarnessAssert.Equal(
            auditCountBefore + 2,
            await audits.CountDocumentsAsync(
                FilterDefinition<UserActionLog>.Empty,
                cancellationToken: ct),
            "P4 historical replay wrote an audit");

        _reopenedFlowDraftRevision = afterLaterSave.DraftRevision;
        _reopenedFlowPayloadHash = afterLaterSave.PayloadHash;
        await VerifyP4SaveVsLockRaceAsync(token, formId, ct);
        await VerifyP4LockVsArchiveRaceAsync(token, ct);
        await VerifyP4VersionReplayAfterArchiveAsync(token, ct);
        return new CaseObservation(
            $"save/save produced one winner ({(winnerIsA ? "A" : "B")}); isolated save/lock and lock/archive races each produced one winner, one stable 409, one receipt/audit and no orphan; save stayed historically exact after a later save/Form change, lock/reopen stayed exact after archive, and a tampered snapshot was rejected with 409",
            "races=3;eachSuccess=1;eachRevisionConflict=1;eachReceipt=1;eachAudit=1;orphan=0;directMongo=true;exactReplay=200/0W;historicalExactReplay=save-after-save+Form-change+lock/reopen-after-archive;changedReplay=409/0W;tamperedSnapshotReplay=409/0W;activeActorReplayGuard=contract;exactCleanup=true");
    }

    private async Task<CaseObservation> CreateTypedFlowAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var formId = HarnessAssert.Required(_formId, "P1-BE-005 form");
        var publishedForm = await LoadFormDocumentAsync(formId, ct);
        var publishedSnapshot = RequirePublishedSnapshot(publishedForm, "P1-BE-008 published form provenance");
        var expectedFamilyId = HarnessAssert.Required(publishedForm.FamilyId, "P1-BE-008 published form family");
        var payload = BuildFlowPayload(formId);
        const string createCommandId = "p4-flow-create-001";
        const string lockCommandId = "p4-flow-lock-001";
        const string reopenCommandId = "p4-flow-reopen-001";
        var receipts = _database.GetCollection<DynamicFlowDefinitionCommandReceipt>("dynamic_flow_definition_command_receipts");
        var audits = _database.GetCollection<UserActionLog>("user_action_logs");
        var receiptCountBefore = await receipts.CountDocumentsAsync(FilterDefinition<DynamicFlowDefinitionCommandReceipt>.Empty, cancellationToken: ct);
        var auditCountBefore = await audits.CountDocumentsAsync(FilterDefinition<UserActionLog>.Empty, cancellationToken: ct);
        var request = new JsonObject
        {
            ["commandId"] = createCommandId,
            ["code"] = FlowCode,
            ["name"] = "P1 typed flow",
            ["description"] = "P1 isolated integration flow",
            ["rootDynamicFormTemplateId"] = formId,
            ["payload"] = payload
        };
        var response = await _api.PostAsync("api/dynamic-flow-templates", request, token, ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, "typed Dynamic Flow create");
        var flow = ApiHarnessClient.RequiredObject(response.Json, "created Dynamic Flow");
        _flowId = ApiHarnessClient.RequiredString(flow, "id");
        HarnessAssert.Equal(FlowCode, ApiHarnessClient.RequiredString(flow, "code"), "Dynamic Flow code mismatch");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(flow, "familyRevision"), "Created Dynamic Flow family revision mismatch");
        HarnessAssert.True(ApiHarnessClient.RequiredBool(flow, "canRead"), "Owner create response canRead=false");
        HarnessAssert.True(ApiHarnessClient.RequiredBool(flow, "canManage"), "Owner create response canManage=false");
        HarnessAssert.True(!ApiHarnessClient.RequiredBool(flow, "executeGrant"), "Owner received an implicit execute grant");
        HarnessAssert.True(!ApiHarnessClient.RequiredBool(flow, "canExecute"), "P4 create response became executable");
        HarnessAssert.Equal(
            "BLOCKED_UNTIL_TARGET_PHASE",
            ApiHarnessClient.RequiredString(flow, "executionEligibility"),
            "P4 create eligibility mismatch");
        var draft = ApiHarnessClient.RequiredObject(flow["draftVersion"], "Dynamic Flow draft version");
        var draftVersionId = ApiHarnessClient.RequiredString(draft, "id");
        var draftRevision = ApiHarnessClient.RequiredInt(draft, "draftRevision");
        var draftPayloadHash = ApiHarnessClient.RequiredString(draft, "payloadHash");
        HarnessAssert.Equal(1, draftRevision, "Initial Dynamic Flow draft revision mismatch");
        HarnessAssert.Equal(64, draftPayloadHash.Length, "Initial Dynamic Flow payload hash length mismatch");
        var typedPayload = ApiHarnessClient.RequiredObject(draft["payload"], "Dynamic Flow typed payload");
        AssertCanonicalFlowFormProvenance(
            typedPayload,
            formId,
            expectedFamilyId,
            publishedForm.VersionNo,
            publishedSnapshot.Hash,
            "Dynamic Flow draft typed payload");
        AssertCanonicalFlowFormProvenance(
            ParseFlowPayloadJson(draft, "Dynamic Flow draft legacy payload"),
            formId,
            expectedFamilyId,
            publishedForm.VersionNo,
            publishedSnapshot.Hash,
            "Dynamic Flow draft legacy payload");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredArray(typedPayload["nodes"], "Dynamic Flow nodes").Count, "Dynamic Flow typed node count mismatch");
        HarnessAssert.True(draft["payloadJson"] is JsonValue, "Dynamic Flow legacy payloadJson companion is missing");
        HarnessAssert.Equal(
            receiptCountBefore + 1,
            await receipts.CountDocumentsAsync(FilterDefinition<DynamicFlowDefinitionCommandReceipt>.Empty, cancellationToken: ct),
            "Flow create did not commit exactly one receipt");
        HarnessAssert.Equal(
            auditCountBefore + 1,
            await audits.CountDocumentsAsync(FilterDefinition<UserActionLog>.Empty, cancellationToken: ct),
            "Flow create did not commit exactly one success audit");

        var replay = await _api.PostAsync(
            "api/dynamic-flow-templates",
            request.DeepClone(),
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(replay, HttpStatusCode.OK, "exact Dynamic Flow create replay");
        HarnessAssert.Equal(_flowId, ApiHarnessClient.RequiredString(replay.Json, "id"), "Exact create replay returned another family");
        HarnessAssert.Equal(
            receiptCountBefore + 1,
            await receipts.CountDocumentsAsync(FilterDefinition<DynamicFlowDefinitionCommandReceipt>.Empty, cancellationToken: ct),
            "Exact create replay wrote another receipt");
        HarnessAssert.Equal(
            auditCountBefore + 1,
            await audits.CountDocumentsAsync(FilterDefinition<UserActionLog>.Empty, cancellationToken: ct),
            "Exact create replay wrote another success audit");

        var forms = _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates");
        var formBeforeReplay = await forms
            .Find(x => x.Id == formId)
            .SingleAsync(ct);
        try
        {
            var hideForm = await forms.UpdateOneAsync(
                x => x.Id == formId,
                Builders<DynamicFormTemplate>.Update.Set(x => x.IsDeleted, true),
                cancellationToken: ct);
            HarnessAssert.Equal(1L, hideForm.ModifiedCount, "Create replay hidden-Form fixture");
            var replayAfterFormStateChange = await _api.PostAsync(
                "api/dynamic-flow-templates",
                request.DeepClone(),
                token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                replayAfterFormStateChange,
                HttpStatusCode.OK,
                "exact Dynamic Flow create replay after Form state change");
            HarnessAssert.Equal(
                _flowId,
                ApiHarnessClient.RequiredString(replayAfterFormStateChange.Json, "id"),
                "Create replay after Form state change returned another family");
        }
        finally
        {
            await forms.UpdateOneAsync(
                x => x.Id == formId,
                Builders<DynamicFormTemplate>.Update.Set(x => x.IsDeleted, formBeforeReplay.IsDeleted),
                cancellationToken: CancellationToken.None);
        }

        var changedReplayRequest = request.DeepClone().AsObject();
        changedReplayRequest["name"] = "P1 changed replay must fail";
        var changedReplay = await _api.PostAsync(
            "api/dynamic-flow-templates",
            changedReplayRequest,
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(changedReplay, HttpStatusCode.Conflict, "changed Dynamic Flow create replay");
        AssertErrorCode(changedReplay, "DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT", "changed Dynamic Flow create replay");

        var locked = await _api.PostAsync(
            $"api/dynamic-flow-templates/{_flowId}/versions/{draftVersionId}/lock",
            new
            {
                commandId = lockCommandId,
                expectedFamilyRevision = 1,
                expectedDraftRevision = draftRevision,
                expectedPayloadHash = draftPayloadHash
            },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(locked, HttpStatusCode.OK, "Dynamic Flow version lock");
        var lockedVersion = ApiHarnessClient.RequiredObject(locked.Json, "locked Dynamic Flow version");
        HarnessAssert.Equal("LOCKED", ApiHarnessClient.RequiredString(lockedVersion, "status"), "Dynamic Flow version was not locked");
        HarnessAssert.Equal(1, lockedVersion["versionNo"]?.GetValue<int>() ?? 0, "Locked Dynamic Flow version number mismatch");
        AssertCanonicalFlowFormProvenance(
            ApiHarnessClient.RequiredObject(lockedVersion["payload"], "locked Dynamic Flow typed payload"),
            formId,
            expectedFamilyId,
            publishedForm.VersionNo,
            publishedSnapshot.Hash,
            "locked Dynamic Flow typed payload");
        AssertCanonicalFlowFormProvenance(
            ParseFlowPayloadJson(lockedVersion, "locked Dynamic Flow legacy payload"),
            formId,
            expectedFamilyId,
            publishedForm.VersionNo,
            publishedSnapshot.Hash,
            "locked Dynamic Flow legacy payload");
        _lockedFlowVersionId = ApiHarnessClient.RequiredString(lockedVersion, "id");
        _lockedFlowPayloadHash = ApiHarnessClient.RequiredString(lockedVersion, "payloadHash");
        HarnessAssert.Equal(draftPayloadHash, _lockedFlowPayloadHash, "Lock changed the canonical payload hash");
        HarnessAssert.True(ApiHarnessClient.RequiredBool(lockedVersion, "definitionLockable"), "Locked snapshot is not definition-lockable");
        HarnessAssert.True(!ApiHarnessClient.RequiredBool(lockedVersion, "canExecute"), "Locked P4 snapshot became executable");
        HarnessAssert.Equal("P5", ApiHarnessClient.RequiredString(lockedVersion, "blockedUntilPhase"), "FLOW-T01 target phase mismatch");
        HarnessAssert.Equal(
            receiptCountBefore + 2,
            await receipts.CountDocumentsAsync(FilterDefinition<DynamicFlowDefinitionCommandReceipt>.Empty, cancellationToken: ct),
            "Flow lock did not commit exactly one receipt");
        HarnessAssert.Equal(
            auditCountBefore + 2,
            await audits.CountDocumentsAsync(FilterDefinition<UserActionLog>.Empty, cancellationToken: ct),
            "Flow lock did not commit exactly one success audit");

        var lockedFamily = await _api.GetAsync($"api/dynamic-flow-templates/{_flowId}", token, ct: ct);
        ApiHarnessClient.ExpectStatus(lockedFamily, HttpStatusCode.OK, "locked Dynamic Flow family detail");
        var familyRevision = ApiHarnessClient.RequiredInt(lockedFamily.Json, "familyRevision");
        HarnessAssert.Equal(2, familyRevision, "Lock did not advance familyRevision once");

        var futureDraft = await _api.PostAsync(
            $"api/dynamic-flow-templates/{_flowId}/versions/{draftVersionId}/reopen",
            new { commandId = reopenCommandId, expectedFamilyRevision = familyRevision },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(futureDraft, HttpStatusCode.OK, "explicit Dynamic Flow reopen");
        var futureDraftObject = ApiHarnessClient.RequiredObject(futureDraft.Json, "future Dynamic Flow draft");
        HarnessAssert.Equal("DRAFT", ApiHarnessClient.RequiredString(futureDraftObject, "status"), "Future version is not draft");
        HarnessAssert.Equal(2, futureDraftObject["versionNo"]?.GetValue<int>() ?? 0, "Future draft version number mismatch");
        _reopenedFlowVersionId = ApiHarnessClient.RequiredString(futureDraftObject, "id");
        _reopenedFlowDraftRevision = ApiHarnessClient.RequiredInt(futureDraftObject, "draftRevision");
        _reopenedFlowPayloadHash = ApiHarnessClient.RequiredString(futureDraftObject, "payloadHash");
        HarnessAssert.Equal(draftPayloadHash, _reopenedFlowPayloadHash, "Reopen did not byte-copy the locked payload hash");
        HarnessAssert.Equal(
            receiptCountBefore + 3,
            await receipts.CountDocumentsAsync(FilterDefinition<DynamicFlowDefinitionCommandReceipt>.Empty, cancellationToken: ct),
            "Flow reopen did not commit exactly one receipt");
        HarnessAssert.Equal(
            auditCountBefore + 3,
            await audits.CountDocumentsAsync(FilterDefinition<UserActionLog>.Empty, cancellationToken: ct),
            "Flow reopen did not commit exactly one success audit");
        return new CaseObservation(
            $"created typed flow {_flowId}; exact create replay was 0W, changed replay conflicted, lock pinned P5 and explicit reopen copied v1 to v2",
            "create=200;exactReplay=200/0W;changedReplay=409;receipts=3;audits=3;draftProvenanceExact=true;lockedProvenanceExact=true;v1=LOCKED/P5/BLOCKED;v2=DRAFT");
    }

    private static JsonObject ParseFlowPayloadJson(JsonObject version, string context)
    {
        var payloadJson = ApiHarnessClient.RequiredString(version, "payloadJson");
        return JsonNode.Parse(payloadJson) as JsonObject
               ?? throw new InvalidOperationException($"{context} must be a JSON object");
    }

    private static void AssertCanonicalFlowFormProvenance(
        JsonObject payload,
        string expectedTemplateId,
        string expectedFamilyId,
        int expectedVersionNo,
        string expectedSchemaHash,
        string context)
    {
        var nodes = ApiHarnessClient.RequiredArray(payload["formNodes"], $"{context} formNodes");
        HarnessAssert.Equal(1, nodes.Count, $"{context} form-node count mismatch");
        var node = ApiHarnessClient.RequiredObject(nodes[0], $"{context} form node");
        HarnessAssert.Equal(expectedTemplateId, ApiHarnessClient.RequiredString(node, "dynamicFormTemplateId"), $"{context} exact template mismatch");
        HarnessAssert.Equal(expectedFamilyId, ApiHarnessClient.RequiredString(node, "dynamicFormFamilyId"), $"{context} canonical family mismatch");
        HarnessAssert.Equal(expectedVersionNo, ApiHarnessClient.RequiredInt(node, "dynamicFormVersionNo"), $"{context} canonical version mismatch");
        HarnessAssert.Equal(expectedSchemaHash, ApiHarnessClient.RequiredString(node, "dynamicFormSchemaHash"), $"{context} canonical schema hash mismatch");

        var topologyNodes = ApiHarnessClient.RequiredArray(payload["nodes"], $"{context} nodes");
        HarnessAssert.Equal(1, topologyNodes.Count, $"{context} node count mismatch");
        var step = ApiHarnessClient.RequiredObject(topologyNodes[0], $"{context} node");
        HarnessAssert.Equal("step_root", ApiHarnessClient.RequiredString(step, "nodeId"), $"{context} node id mismatch");
        HarnessAssert.Equal("root_form", ApiHarnessClient.RequiredString(step, "formNodeId"), $"{context} node form binding mismatch");
        HarnessAssert.True(!step.ContainsKey("dynamicFormFamilyId"), $"{context} node duplicated family provenance");
        HarnessAssert.True(!step.ContainsKey("dynamicFormVersionNo"), $"{context} node duplicated version provenance");
        HarnessAssert.True(!step.ContainsKey("dynamicFormSchemaHash"), $"{context} node duplicated schema-hash provenance");
    }

    private async Task<CaseObservation> VerifyOutsiderDeniedAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_outsiderToken, "P1-BE-003 outsider token");
        var flowId = HarnessAssert.Required(_flowId, "P1-BE-008 flow");
        var search = await _api.PostAsync(
            "api/dynamic-flow-templates/search",
            new { query = FlowCode, page = 0, pageSize = 20 },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(search, HttpStatusCode.OK, "outsider Dynamic Flow search");
        var searchObject = ApiHarnessClient.RequiredObject(search.Json, "outsider search result");
        var rows = ApiHarnessClient.RequiredArray(searchObject["rows"], "outsider search rows");
        HarnessAssert.True(
            rows.OfType<JsonObject>().All(x => !string.Equals(x["id"]?.GetValue<string>(), flowId, StringComparison.Ordinal)),
            "Outsider search leaked Dynamic Flow");

        var get = await _api.GetAsync($"api/dynamic-flow-templates/{flowId}", token, ct: ct);
        ApiHarnessClient.ExpectStatus(get, HttpStatusCode.Forbidden, "outsider Dynamic Flow get");
        HarnessAssert.Equal(
            "DYNAMIC_FLOW_DEFINITION_ACCESS_FORBIDDEN",
            ApiHarnessClient.FindStringRecursive(get.Json, "reason"),
            "Outsider get denial reason mismatch");
        return new CaseObservation(
            "outsider search filtered flow and direct get returned 403",
            "searchVisible=false;get=403;reason=DYNAMIC_FLOW_DEFINITION_ACCESS_FORBIDDEN");
    }

    private async Task<CaseObservation> VerifyOutsiderRootLaunchDeniedAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_outsiderToken, "P1-BE-003 outsider token");
        var lockedVersionId = HarnessAssert.Required(_lockedFlowVersionId, "P1-BE-008 locked flow version");
        var response = await _api.PostAsync(
            $"api/works/{_workId}/dynamic-flows/instances",
            new
            {
                flowTemplateVersionId = lockedVersionId,
                stepId = "step_root",
                targetUnitIds = new[] { _unitId },
                name = "Must not launch",
                assignmentType = "ONCE",
                aggregationType = "MATRIX",
                dueAtUtc = "2026-08-01T10:00:00Z",
                leaderWatcherUserIds = Array.Empty<string>(),
                isActive = true
            },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Forbidden, "outsider root Dynamic Flow launch");
        HarnessAssert.Equal(
            "DYNAMIC_FLOW_DEFINITION_ACCESS_FORBIDDEN",
            ApiHarnessClient.FindStringRecursive(response.Json, "reason"),
            "Outsider root launch denial reason mismatch");
        var writes = await _database.GetCollection<WorkAssignment>("work_assignments")
            .CountDocumentsAsync(x => x.WorkId == _workId && !x.IsDeleted, cancellationToken: ct);
        HarnessAssert.Equal(0L, writes, "Forbidden root launch wrote assignments");
        return new CaseObservation(
            "outsider root launch denied before assignment writes",
            "http=403;reason=DYNAMIC_FLOW_DEFINITION_ACCESS_FORBIDDEN;writes=0");
    }

    private async Task<CaseObservation> GrantAndVerifyParticipantAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_outsiderToken, "P1-BE-003 outsider token");
        var flowId = HarnessAssert.Required(_flowId, "P1-BE-008 flow");
        var fixedAt = new DateTime(2026, 7, 22, 1, 0, 0, DateTimeKind.Utc);
        var assignment = new WorkAssignment
        {
            Id = _assignmentId,
            WorkId = _workId,
            WorkType = "P1_INTEGRATION",
            AssignmentType = "ONCE",
            AggregationType = "NONE",
            Assignees =
            [
                new UserRef
                {
                    UserId = _outsiderId,
                    Username = OutsiderUsername,
                    FullName = "P1 Outsider",
                    UnitId = _unitId,
                    UnitSymbol = "P1IT",
                    UnitShortName = "P1 IT",
                    UnitName = "P1 Integration Unit"
                }
            ],
            IsActive = true,
            RootAssignmentId = _assignmentId,
            Level = 0,
            Code = "P1-WA-001",
            Name = "P1 Flow participant grant",
            Path = $"/{_assignmentId}/",
            FlowTemplateId = flowId,
            FlowTemplateVersionNo = 1,
            FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Effective,
            CreatedByUserId = _reviewerId,
            UpdatedByUserId = _reviewerId,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };
        await _database.GetCollection<WorkAssignment>("work_assignments")
            .InsertOneAsync(assignment, cancellationToken: ct);

        var mixedOwnerFamilyId = ObjectId.GenerateNewId().ToString();
        var mixedOwnerDraftId = ObjectId.GenerateNewId().ToString();
        var mixedOwnerPayloadSource = await _database
            .GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.Id == HarnessAssert.Required(_lockedFlowVersionId, "mixed owner payload source"))
            .SingleAsync(ct);
        var mixedOwnerFamily = new DynamicFlowTemplate
        {
            Id = mixedOwnerFamilyId,
            Code = $"{FlowCode}_MIXED_OWNER",
            Name = "P1 mixed owner search projection",
            FamilyRevision = 1,
            OwnerUserId = _outsiderId,
            Status = DynamicFlowTemplateStatuses.Draft,
            HasLockedVersion = false,
            CreatedByUserId = _outsiderId,
            UpdatedByUserId = _outsiderId,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };
        var mixedOwnerDraft = new DynamicFlowTemplateVersion
        {
            Id = mixedOwnerDraftId,
            TemplateId = mixedOwnerFamilyId,
            RootDynamicFormTemplateId = mixedOwnerPayloadSource.RootDynamicFormTemplateId,
            VersionNo = 1,
            Status = DynamicFlowTemplateVersionStatuses.Draft,
            DraftRevision = 1,
            SchemaVersion = mixedOwnerPayloadSource.SchemaVersion,
            AdapterVersion = mixedOwnerPayloadSource.AdapterVersion,
            CatalogVersion = mixedOwnerPayloadSource.CatalogVersion,
            CatalogSemanticHash = mixedOwnerPayloadSource.CatalogSemanticHash,
            PayloadJson = mixedOwnerPayloadSource.PayloadJson,
            PayloadHash = mixedOwnerPayloadSource.PayloadHash,
            DefinitionLockable = mixedOwnerPayloadSource.DefinitionLockable,
            ExecutionEligibility = mixedOwnerPayloadSource.ExecutionEligibility,
            ExecutionBlockedReason = mixedOwnerPayloadSource.ExecutionBlockedReason,
            BlockedUntilPhase = mixedOwnerPayloadSource.BlockedUntilPhase,
            MigrationState = mixedOwnerPayloadSource.MigrationState,
            CreatedByUserId = _outsiderId,
            UpdatedByUserId = _outsiderId,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };
        var flowFamilies = _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates");
        var flowVersions = _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions");
        await flowFamilies.InsertOneAsync(mixedOwnerFamily, cancellationToken: ct);
        try
        {
            await flowVersions.InsertOneAsync(mixedOwnerDraft, cancellationToken: ct);
            var search = await _api.PostAsync(
                "api/dynamic-flow-templates/search",
                new { query = FlowCode, page = 0, pageSize = 20 },
                token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(search, HttpStatusCode.OK, "mixed owner/participant Dynamic Flow search");
            var rows = ApiHarnessClient.RequiredArray(
                ApiHarnessClient.RequiredObject(search.Json, "mixed owner/participant search")["rows"],
                "mixed owner/participant rows");
            var participantSearchRow = rows
                .OfType<JsonObject>()
                .SingleOrDefault(x => string.Equals(x["id"]?.GetValue<string>(), flowId, StringComparison.Ordinal));
            HarnessAssert.True(participantSearchRow is not null, "Assignment participant cannot find Dynamic Flow");
            HarnessAssert.True(participantSearchRow!["draftVersion"] is null, "Participant search leaked owner-only future draft");
            AssertOnlyLockedVersionOne(
                ApiHarnessClient.RequiredArray(participantSearchRow["versions"], "participant search versions"),
                "participant search");
            HarnessAssert.Equal(
                HarnessAssert.Required(_lockedFlowVersionId, "participant locked version"),
                ApiHarnessClient.RequiredString(participantSearchRow, "currentVersionId"),
                "Participant search current version was not the exact granted locked version");

            var ownerSearchRow = rows
                .OfType<JsonObject>()
                .SingleOrDefault(x => string.Equals(x["id"]?.GetValue<string>(), mixedOwnerFamilyId, StringComparison.Ordinal));
            HarnessAssert.True(ownerSearchRow is not null, "Mixed search omitted actor-owned draft family");
            HarnessAssert.True(ApiHarnessClient.RequiredBool(ownerSearchRow!, "canRead"), "Mixed owner row canRead");
            HarnessAssert.True(ApiHarnessClient.RequiredBool(ownerSearchRow, "canManage"), "Mixed owner row canManage");
            HarnessAssert.True(!ApiHarnessClient.RequiredBool(ownerSearchRow, "hasLockedVersion"), "Mixed owner draft-only locked flag");
            HarnessAssert.True(ownerSearchRow["currentVersionId"] is null, "Mixed owner draft-only currentVersionId");
            HarnessAssert.True(ownerSearchRow["currentVersionNo"] is null, "Mixed owner draft-only currentVersionNo");
            HarnessAssert.True(ownerSearchRow["currentVersionHash"] is null, "Mixed owner draft-only currentVersionHash");
            HarnessAssert.True(ownerSearchRow["currentVersion"] is null, "Mixed owner draft-only currentVersion summary");
            HarnessAssert.Equal(
                mixedOwnerDraftId,
                ApiHarnessClient.RequiredString(
                    ApiHarnessClient.RequiredObject(ownerSearchRow["draftVersion"], "mixed owner draft"),
                    "id"),
                "Mixed owner draft id");
            var ownerVersions = ApiHarnessClient.RequiredArray(ownerSearchRow["versions"], "mixed owner versions");
            HarnessAssert.Equal(1, ownerVersions.Count, "Mixed owner version count");
            HarnessAssert.Equal(
                "DRAFT",
                ApiHarnessClient.RequiredString(ApiHarnessClient.RequiredObject(ownerVersions[0], "mixed owner version"), "status"),
                "Mixed owner version status");
        }
        finally
        {
            await flowVersions.DeleteOneAsync(x => x.Id == mixedOwnerDraftId, ct);
            await flowFamilies.DeleteOneAsync(x => x.Id == mixedOwnerFamilyId, ct);
        }

        var get = await _api.GetAsync($"api/dynamic-flow-templates/{flowId}", token, ct: ct);
        ApiHarnessClient.ExpectStatus(get, HttpStatusCode.OK, "participant Dynamic Flow get");
        var participantFlow = ApiHarnessClient.RequiredObject(get.Json, "participant flow");
        HarnessAssert.Equal(flowId, ApiHarnessClient.RequiredString(participantFlow, "id"), "Participant opened the wrong Dynamic Flow");
        HarnessAssert.True(participantFlow["draftVersion"] is null, "Participant detail leaked owner-only future draft");
        var visibleVersions = ApiHarnessClient.RequiredArray(participantFlow["versions"], "participant detail versions");
        AssertOnlyLockedVersionOne(visibleVersions, "participant detail");

        var versionList = await _api.GetAsync($"api/dynamic-flow-templates/{flowId}/versions", token, ct: ct);
        ApiHarnessClient.ExpectStatus(versionList, HttpStatusCode.OK, "participant Dynamic Flow version list");
        AssertOnlyLockedVersionOne(ApiHarnessClient.RequiredArray(versionList.Json, "participant version list"), "participant version list");

        var assignments = _database.GetCollection<WorkAssignment>("work_assignments");
        await assignments.UpdateOneAsync(
            x => x.Id == _assignmentId,
            Builders<WorkAssignment>.Update.Set(x => x.FlowEffectiveStatus, DynamicFlowEffectiveStatuses.Invalidated),
            cancellationToken: ct);
        await AssertFlowHiddenAndForbiddenAsync(token, flowId, "invalidated assignment", ct);

        await assignments.UpdateOneAsync(
            x => x.Id == _assignmentId,
            Builders<WorkAssignment>.Update
                .Set(x => x.FlowEffectiveStatus, DynamicFlowEffectiveStatuses.Effective)
                .Set(x => x.IsActive, false),
            cancellationToken: ct);
        await AssertFlowHiddenAndForbiddenAsync(token, flowId, "inactive assignment", ct);

        await assignments.UpdateOneAsync(
            x => x.Id == _assignmentId,
            Builders<WorkAssignment>.Update
                .Set(x => x.FlowEffectiveStatus, DynamicFlowEffectiveStatuses.Effective)
                .Set(x => x.IsActive, true),
            cancellationToken: ct);
        return new CaseObservation(
            "one mixed page projected the actor-owned draft and exact participant locked v1; invalidated/inactive grants failed closed",
            "mixedOwnerDraft=true;effectiveGrant=true;lockedVersions=1;draftLeak=false;invalidated=403;inactive=403");
    }

    private async Task AssertFlowHiddenAndForbiddenAsync(
        string token,
        string flowId,
        string context,
        CancellationToken ct)
    {
        var search = await _api.PostAsync(
            "api/dynamic-flow-templates/search",
            new { query = FlowCode, page = 0, pageSize = 20 },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(search, HttpStatusCode.OK, $"{context} search");
        var rows = ApiHarnessClient.RequiredArray(
            ApiHarnessClient.RequiredObject(search.Json, $"{context} search result")["rows"],
            $"{context} search rows");
        HarnessAssert.True(
            rows.OfType<JsonObject>().All(x => !string.Equals(x["id"]?.GetValue<string>(), flowId, StringComparison.Ordinal)),
            $"{context} leaked Dynamic Flow in search");

        var get = await _api.GetAsync($"api/dynamic-flow-templates/{flowId}", token, ct: ct);
        ApiHarnessClient.ExpectStatus(get, HttpStatusCode.Forbidden, $"{context} get");
    }

    private static void AssertOnlyLockedVersionOne(JsonArray versions, string context)
    {
        HarnessAssert.Equal(1, versions.Count, $"{context} must expose exactly one assigned version");
        var version = ApiHarnessClient.RequiredObject(versions[0], $"{context}[0]");
        HarnessAssert.Equal(1, version["versionNo"]?.GetValue<int>() ?? 0, $"{context} version number mismatch");
        HarnessAssert.Equal("LOCKED", ApiHarnessClient.RequiredString(version, "status"), $"{context} exposed a non-locked version");
    }

    private async Task<CaseObservation> VerifyPersistenceAsync(CancellationToken ct)
    {
        var formId = HarnessAssert.Required(_formId, "P1-BE-005 form");
        var flowId = HarnessAssert.Required(_flowId, "P1-BE-008 flow");
        var form = await _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
            .Find(x => x.Id == formId && !x.IsDeleted)
            .SingleAsync(ct);
        var flow = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Id == flowId && !x.IsDeleted)
            .SingleAsync(ct);
        var versions = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.TemplateId == flowId && !x.IsDeleted)
            .SortBy(x => x.VersionNo)
            .ToListAsync(ct);
        var assignmentCount = await _database.GetCollection<WorkAssignment>("work_assignments")
            .CountDocumentsAsync(x =>
                x.Id == _assignmentId &&
                x.FlowTemplateId == flowId &&
                x.FlowTemplateVersionNo == 1 &&
                x.FlowEffectiveStatus == DynamicFlowEffectiveStatuses.Effective &&
                x.IsActive &&
                !x.IsDeleted,
                cancellationToken: ct);

        HarnessAssert.True(form.IsPublished, "Mongo form source is not published");
        HarnessAssert.Equal(_ownerId, form.CreatedByUserId, "Mongo form owner mismatch");
        HarnessAssert.Equal(_ownerId, flow.CreatedByUserId, "Mongo flow owner mismatch");
        HarnessAssert.Equal(2, versions.Count, "Mongo flow version count mismatch");
        HarnessAssert.Equal("LOCKED", versions[0].Status, "Mongo v1 is not locked");
        HarnessAssert.Equal("DRAFT", versions[1].Status, "Mongo v2 is not draft");
        HarnessAssert.True(versions.All(x => x.PayloadJson.Contains("formNodes", StringComparison.Ordinal)), "Mongo flow payload is incomplete");
        HarnessAssert.Equal(1L, assignmentCount, "Mongo participant assignment mismatch");
        return new CaseObservation(
            "source documents and participant assignment verified directly in isolated Mongo",
            "forms=1;flows=1;versions=2;locked=1;draft=1;participantAssignments=1");
    }

    private async Task<CaseObservation> VerifyScopedJobPollerAsync(CancellationToken ct)
    {
        var invoked = false;
        var simulatedScopedWorker = new System.Diagnostics.Stopwatch();
        var result = await ScopedJobPoller.InvokeAndPollAsync(
            invokeScopedWorker: _ =>
            {
                invoked = true;
                simulatedScopedWorker.Start();
                return Task.CompletedTask;
            },
            readSnapshot: _ => Task.FromResult(
                simulatedScopedWorker.Elapsed < TimeSpan.FromMilliseconds(75)
                    ? new ScopedJobSnapshot("RUNNING", "scope=p1-foundation")
                    : new ScopedJobSnapshot("SUCCEEDED", "scope=p1-foundation")),
            isTerminal: x => x.Status is "SUCCEEDED" or "FAILED",
            isSuccessful: x => x.Status == "SUCCEEDED",
            timeout: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(25),
            ct);
        HarnessAssert.True(invoked, "Scoped worker delegate was not invoked");
        HarnessAssert.Equal("SUCCEEDED", result.FinalStatus, "Scoped job poll final status mismatch");
        HarnessAssert.True(result.Timeline.Count >= 2, "Scoped job poll timeline did not observe an in-flight state");
        await EvidenceJson.WriteAsync(
            Path.Combine(_iterationRoot, "job-timeline.json"),
            new
            {
                foundationOnly = true,
                productionWorkerBinding = "DEFERRED_TO_JOB_OWNING_SLICE",
                timeoutMs = 2000,
                pollIntervalMs = 25,
                result
            },
            ct);
        return new CaseObservation(
            $"scoped worker invoked and polled to success in {result.Timeline.Count} observations",
            "scopedInvoke=true;timeoutBounded=true;observedRunning=true;final=SUCCEEDED");
    }

    private static AppUser NewActor(
        string id,
        string username,
        string fullName,
        DateTime fixedAt,
        string adminId,
        string unitId)
        => new()
        {
            Id = id,
            Username = username,
            FullName = fullName,
            UnitId = unitId,
            PositionCode = null,
            AccountKind = "NORMAL_USER",
            Roles = [],
            CreatedByUserId = adminId,
            UpdatedByUserId = adminId,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };

    private JsonObject BuildFormRequest(string code, string name, bool updated)
    {
        var enumCatalogId = HarnessAssert.Required(_enumCatalogId, "P1-BE-005 enum catalog");
        var remainingSources = new Queue<string>(_valueSources);
        var fields = new JsonArray();
        var order = 1;
        foreach (var type in _fieldTypes)
        {
            var key = ToSeedKey(type);
            var field = new JsonObject
            {
                ["id"] = $"field_{key}",
                ["sectionId"] = "main",
                ["key"] = $"value_{key}",
                ["name"] = updated && order == 1
                    ? "Thông tin kiểm thử 01 đã cập nhật"
                    : $"Thông tin kiểm thử {order:00}",
                ["type"] = type,
                ["required"] = order == 1,
                ["order"] = order++
            };
            if (IsChoiceType(type) && remainingSources.TryDequeue(out var sourceType))
                field["valueSource"] = BuildValueSource(sourceType, enumCatalogId);
            fields.Add(field);
        }

        var sourceHostType = _fieldTypes.FirstOrDefault(x => string.Equals(x, "singleSelect", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Capability catalog has no singleSelect host for remaining value sources.");
        while (remainingSources.TryDequeue(out var sourceType))
        {
            var key = ToSeedKey(sourceType);
            fields.Add(new JsonObject
            {
                ["id"] = $"field_source_{key}",
                ["sectionId"] = "main",
                ["key"] = $"value_source_{key}",
                ["name"] = $"Lựa chọn kiểm thử {order:00}",
                ["type"] = sourceHostType,
                ["required"] = false,
                ["order"] = order++,
                ["valueSource"] = BuildValueSource(sourceType, enumCatalogId)
            });
        }

        var schema = new JsonObject
        {
            ["sections"] = new JsonArray
            {
                new JsonObject { ["id"] = "main", ["title"] = "Main section", ["order"] = 1 }
            },
            ["fields"] = fields,
            ["blocks"] = new JsonArray()
        };

        return new JsonObject
        {
            ["code"] = code,
            ["name"] = name,
            ["description"] = "P1 isolated integration form",
            ["tagCodes"] = new JsonArray(),
            ["schemaVersion"] = updated ? 2 : 1,
            ["isActive"] = true,
            ["schema"] = schema
        };
    }

    private static JsonObject BuildValueSource(string sourceType, string enumCatalogId)
    {
        var source = new JsonObject { ["sourceType"] = sourceType };
        if (string.Equals(sourceType, "FIXED_ENUM", StringComparison.Ordinal))
        {
            source["options"] = new JsonArray
            {
                new JsonObject { ["code"] = "FIXED_A", ["label"] = "Fixed A" }
            };
        }
        else if (string.Equals(sourceType, "ENUM_CATALOG", StringComparison.Ordinal))
        {
            source["catalogId"] = enumCatalogId;
        }
        return source;
    }

    private void AssertCapabilityDrivenFields(JsonArray fields)
    {
        var objects = fields.OfType<JsonObject>().ToArray();
        HarnessAssert.Equal(fields.Count, objects.Length, "Every Dynamic Form field must be an object");
        var actualTypes = objects
            .Select(x => ApiHarnessClient.RequiredString(x, "type"))
            .ToHashSet(StringComparer.Ordinal);
        var actualSources = objects
            .Select(x => x["valueSource"] as JsonObject)
            .Where(x => x is not null)
            .Select(x => ApiHarnessClient.RequiredString(x, "sourceType"))
            .ToHashSet(StringComparer.Ordinal);
        HarnessAssert.True(
            _fieldTypes.All(actualTypes.Contains),
            $"Typed seed is missing catalog field types: {string.Join(',', _fieldTypes.Where(x => !actualTypes.Contains(x)))}");
        HarnessAssert.True(
            _valueSources.All(actualSources.Contains),
            $"Typed seed is missing catalog value sources: {string.Join(',', _valueSources.Where(x => !actualSources.Contains(x)))}");
        HarnessAssert.Equal(_fieldTypes.Count, actualTypes.Count, "Typed seed introduced a field type outside the catalog matrix");
        HarnessAssert.Equal(_valueSources.Count, actualSources.Count, "Typed seed introduced a value source outside the catalog matrix");
    }

    private static IReadOnlyList<string> ReadCapabilityIds(JsonArray items, string context)
        => items
            .Select((item, index) => ApiHarnessClient.RequiredString(
                ApiHarnessClient.RequiredObject(item, $"{context}[{index}]"),
                "id"))
            .ToArray();

    private static bool IsChoiceType(string type)
        => type is "shortText" or "singleSelect" or "multiSelect";

    private static string ToSeedKey(string value)
        => new(value
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray());

    private static JsonObject BuildFlowPayload(string formId)
        => new()
        {
            ["schemaVersion"] = 2,
            ["archetypeId"] = "FLOW-T01",
            ["entryStepId"] = "step_root",
            ["rootDynamicFormTemplateId"] = formId,
            ["formNodes"] = new JsonArray
            {
                new JsonObject
                {
                    ["formNodeId"] = "root_form",
                    ["role"] = "ROOT",
                    ["dynamicFormTemplateId"] = formId
                }
            },
            ["nodes"] = new JsonArray
            {
                new JsonObject
                {
                    ["nodeId"] = "step_root",
                    ["nodeCode"] = "ROOT",
                    ["nodeKind"] = "FORM_STEP",
                    ["formNodeId"] = "root_form",
                    ["declaredRoles"] = new JsonArray("OWNER")
                }
            },
            ["edges"] = new JsonArray(),
            ["actorPolicies"] = new JsonArray
            {
                new JsonObject
                {
                    ["policyId"] = "actor-owner-root",
                    ["stepId"] = "step_root",
                    ["stepCode"] = "*",
                    ["actorRole"] = "OWNER",
                    ["allowForward"] = true
                }
            },
            ["fieldPolicies"] = new JsonArray
            {
                new JsonObject
                {
                    ["policyId"] = "fields-owner-root",
                    ["dynamicFormTemplateId"] = "*",
                    ["stepId"] = "step_root",
                    ["stepCode"] = "*",
                    ["actorRole"] = "OWNER",
                    ["fieldId"] = "*",
                    ["fieldKey"] = "*",
                    ["read"] = true,
                    ["write"] = true
                }
            },
            ["tableColumnPolicies"] = new JsonArray(),
            ["mappingRules"] = new JsonArray(),
            ["rollbackPolicy"] = new JsonObject(),
            ["finalResultPolicy"] = new JsonObject(),
            ["statisticProfile"] = new JsonObject()
        };

    private static JsonObject BuildForgedFlowPayload(string formId)
    {
        var payload = BuildFlowPayload(formId);
        var formNodes = ApiHarnessClient.RequiredArray(payload["formNodes"], "forged Dynamic Flow formNodes");
        var root = ApiHarnessClient.RequiredObject(formNodes[0], "forged Dynamic Flow root form node");
        root["dynamicFormFamilyId"] = "64b00000000000000000f301";
        root["dynamicFormVersionNo"] = 999;
        root["dynamicFormSchemaHash"] = "client-forged-hash";
        return payload;
    }

    private static JsonObject BuildFlowPayloadWithForeignChild(string rootFormId, string foreignFormId)
    {
        var payload = BuildFlowPayload(rootFormId);
        payload["archetypeId"] = "FLOW-T03";
        ApiHarnessClient.RequiredArray(payload["formNodes"], "mixed Dynamic Flow formNodes").Add(
            new JsonObject
            {
                ["formNodeId"] = "foreign_form",
                ["role"] = "CHILD",
                ["dynamicFormTemplateId"] = foreignFormId
            });
        ApiHarnessClient.RequiredArray(payload["nodes"], "mixed Dynamic Flow nodes").Add(
            new JsonObject
            {
                ["nodeId"] = "step_foreign",
                ["nodeCode"] = "FOREIGN",
                ["nodeKind"] = "FORM_STEP",
                ["formNodeId"] = "foreign_form",
                ["declaredRoles"] = new JsonArray("OWNER")
            });
        ApiHarnessClient.RequiredArray(payload["edges"], "mixed Dynamic Flow edges").Add(
            new JsonObject
            {
                ["transitionId"] = "root-to-foreign",
                ["fromNodeId"] = "step_root",
                ["toNodeId"] = "step_foreign"
            });
        return payload;
    }
}
