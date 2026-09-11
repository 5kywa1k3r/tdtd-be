using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Models.Enums;

namespace tdtd_be.IntegrationTests;

internal sealed partial class IntegrationScenario
{
    private const string P3FormCode = "P3_IT_RUNTIME_FORM";
    private const string P3PositionCode = "P3_TEST_POSITION";
    private const string P3PeriodKey = "P3-ONCE";

    private static readonly (string Type, string CaseToken)[] P3FieldTypes =
    [
        ("shortText", "SHORTTEXT"),
        ("longText", "LONGTEXT"),
        ("richText", "RICHTEXT"),
        ("stringList", "STRINGLIST"),
        ("number", "NUMBER"),
        ("date", "DATE"),
        ("fullDate", "FULLDATE"),
        ("singleSelect", "SINGLESELECT"),
        ("multiSelect", "MULTISELECT"),
        ("boolean", "BOOLEAN")
    ];

    private static readonly string[] P3SourceTypes =
    [
        "FIXED_ENUM",
        "ENUM_CATALOG",
        "SYSTEM_UNIT",
        "SYSTEM_USER",
        "SYSTEM_POSITION",
        "SYSTEM_UNIT_TYPE"
    ];

    private static readonly (string Mode, string BlockId)[] P3WritableTableModes =
    [
        ("FIXED_GRID", "p3_fixed"),
        ("APPEND_ROWS", "p3_append_rows"),
        ("APPEND_COLUMNS", "p3_append_columns"),
        ("MATRIX", "p3_matrix")
    ];

    private readonly string _p3WorkId = ObjectId.GenerateNewId().ToString();
    private readonly string _p3PeriodId = ObjectId.GenerateNewId().ToString();
    private readonly string _p3PositionId = ObjectId.GenerateNewId().ToString();
    private string? _p3FormId;
    private string? _p3FormFieldsJson;
    private string? _p3SchemaHash;
    private string? _p3EnumCatalogOptionCode;
    private int? _p3FormRevision;
    private string? _p3AssignmentId;
    private string? _p3BindingId;
    private string? _p3ReportId;
    private int _p3PayloadRevision;
    private int _p3LifecycleRevision;
    private int _p3ReportStatus;
    private string? _p3LatestFieldValuesJson;
    private string? _p3LatestTableValuesJson;
    private string? _p3LifecycleRaceWinnerPath;
    private JsonObject? _p3LifecycleRaceWinnerRequest;
    private JsonObject? _p3LifecycleRaceChangedRequest;
    private int _p3LifecycleRaceWinnerStatus;

    private async Task RunP3DynamicFormRuntimeAsync(HarnessCaseRunner cases, CancellationToken ct)
    {
        await cases.RunAsync("P3-DF-RUNTIME-000-PUBLISHED-FORM-REPORT", () => SetupP3RuntimeAsync(ct));
        await cases.RunAsync("P3-DF-CAP-001-ACTOR-STATE-MATRIX", () => VerifyP3DraftCapabilityMatrixAsync(ct));

        foreach (var field in P3FieldTypes)
        {
            await cases.RunAsync($"DF-FIELD-{field.CaseToken}-01", () => SaveP3FieldTypeAsync(field.Type, ct));
            await cases.RunAsync($"DF-FIELD-{field.CaseToken}-02", () => ReopenP3FieldTypeAsync(field.Type, ct));
            await cases.RunAsync($"DF-FIELD-{field.CaseToken}-03", () => RejectP3InvalidFieldTypeAsync(field.Type, ct));
            await cases.RunAsync($"DF-FIELD-{field.CaseToken}-04", () => VerifyP3ReviewerReadonlyFieldAsync(field.Type, ct));
        }

        for (var index = 0; index < P3SourceTypes.Length; index++)
        {
            var sourceType = P3SourceTypes[index];
            await cases.RunAsync($"DF-SOURCE-{index + 1:00}", () => VerifyP3RuntimeSourceAsync(sourceType, ct));
        }

        foreach (var table in P3WritableTableModes)
            await cases.RunAsync($"DF-TABLE-{table.Mode}", () => VerifyP3WritableTableModeAsync(table.Mode, table.BlockId, ct));
        await cases.RunAsync("DF-TABLE-SUMMARY_TEMPLATE", () => RejectP3SummaryTemplateInputAsync(ct));
        await cases.RunAsync("DF-TABLE-BLOCK-MATRIX-01-03-25-30-31", () => VerifyP3BlockCountMatrixAsync(ct));

        await cases.RunAsync("P3-DF-CAS-001-CONCURRENT-SAVE-REPLAY", () => VerifyP3ConcurrentSaveAndReplayAsync(ct));
        await cases.RunAsync("P3-DF-LC-001-REQUIRED-SUBMIT-ZERO-WRITE", () => RejectP3RequiredSubmitWithoutWritesAsync(ct));
        await cases.RunAsync("P3-DF-OUTBOX-001-POST-COMMIT-FAILURE-RECOVERY", () => VerifyP3PostCommitOutboxRecoveryAsync(ct));
        await cases.RunAsync("P3-DF-LC-002-SUBMIT-WITHDRAW-CAS", () => VerifyP3SubmitAndWithdrawAsync(ct));
        await cases.RunAsync("P3-DF-LC-003-REVIEW-RETURN", () => VerifyP3ReviewerReturnAsync(ct));
        await cases.RunAsync("P3-DF-LC-004-APPROVE-RECALL", () => VerifyP3ApproveAndRecallAsync(ct));
        await cases.RunAsync("P3-DF-RACE-001-APPROVE-VS-RETURN", () => VerifyP3ApproveVsReturnRaceAsync(ct));
        await cases.RunAsync("P3-DF-RACE-002-EXACT-VS-CHANGED-REPLAY", () => VerifyP3LifecycleReplayIdentityAsync(ct));
        await cases.RunAsync("P3-DF-RACE-003-RECALL-VS-DEACTIVATE", () => VerifyP3RecallVsDeactivateRaceAsync(ct));
        await cases.RunAsync("P3-DF-RACE-004-CONCURRENT-REACTIVATE", () => VerifyP3ConcurrentReactivateAsync(ct));
        await cases.RunAsync("P3-DF-RACE-005-MAPPING-VS-SAVE", () => VerifyP3MappingVsSaveRaceAsync(ct));
        await cases.RunAsync("P3-DF-LC-005-IMMUTABLE-VERSION-BINDING", () => VerifyP3ImmutableVersionBindingAsync(ct));
        await cases.RunAsync("P3-DF-SECTION-002-STALE-REPAIR", () => VerifyP3StaleSectionRepairAsync(ct));
        await cases.RunAsync("P3-DF-SECTION-001-DIRECT-MONGO-RECONCILIATION", () => VerifyP3SectionProjectionAsync(ct));
        await cases.RunAsync("P3-DF-EVIDENCE-001-SECRET-REDACTION", () => VerifyP3SecretRedactionAsync(ct));
    }

    public async Task RunP3BrowserFixtureAsync(
        HarnessCaseRunner cases,
        TimeSpan timeout,
        CancellationToken ct)
    {
        await cases.RunAsync("P2-DF-MIGRATION-001-RUNTIME-PROVENANCE-BACKFILL", () => VerifyRuntimeProvenanceBackfillAsync(ct));
        await cases.RunAsync("P1-BE-001-MONGO-PRIMARY", () => VerifyMongoPrimaryAsync(ct));
        await cases.RunAsync("P1-BE-002-BOOTSTRAP-ADMIN", () => BootstrapAndLoginAdminAsync(ct));
        await cases.RunAsync("P1-BE-003-SEED-ACTOR-LOGIN", () => SeedAndLoginActorsAsync(ct));
        await cases.RunAsync("P1-BE-004-CATALOG-ETAG", () => VerifyCatalogEtagAsync(ct));
        await cases.RunAsync("P1-BE-005-FORM-TYPED-CREATE-GET", () => CreateAndGetTypedFormAsync(ct));
        await cases.RunAsync("P1-BE-006-FORM-UPDATE-PUBLISH", () => UpdateAndPublishTypedFormAsync(ct));
        await cases.RunAsync("P1-BE-008-FLOW-TYPED-CREATE-LOCK", () => CreateTypedFlowAsync(ct));
        await cases.RunAsync("P1-BE-011-FLOW-PARTICIPANT-VERSION-GRANT", () => GrantAndVerifyParticipantAsync(ct));
        await cases.RunAsync("P3-DF-RUNTIME-000-PUBLISHED-FORM-REPORT", () => SetupP3RuntimeAsync(ct));

        if (cases.Results.Any(x => x.Verdict != HarnessVerdict.DAT))
            return;

        var secretPath = Path.Combine(_iterationRoot, "browser-fixture.runtime.secret.json");
        var redactedPath = Path.Combine(_iterationRoot, "browser-fixture.json");
        var stopPath = Path.Combine(_iterationRoot, "browser-fixture.stop");
        var readyAtUtc = DateTime.UtcNow;
        var expiresAtUtc = readyAtUtc.Add(timeout);

        await cases.RunAsync(
            "P3-BROWSER-FIXTURE-READY",
            async () =>
            {
                HarnessAssert.Equal(
                    (int)WorkAssignmentReportStatus.Draft,
                    _p3ReportStatus,
                    "Browser fixture report must start in Draft");
                HarnessAssert.True(!File.Exists(stopPath), "Browser fixture stop file already exists");

                await EvidenceJson.WriteAsync(
                    secretPath,
                    BuildP3BrowserFixtureSecretManifest(readyAtUtc, expiresAtUtc, stopPath),
                    ct);
                await EvidenceJson.WriteAsync(
                    redactedPath,
                    BuildP3BrowserFixtureRedactedManifest(
                        "READY",
                        readyAtUtc,
                        expiresAtUtc,
                        stopPath,
                        stopReason: null,
                        secretManifestDeleted: false),
                    ct);

                Console.WriteLine($"P3_BROWSER_FIXTURE_READY={secretPath}");
                Console.WriteLine($"P3_BROWSER_FIXTURE_REDACTED={redactedPath}");
                Console.WriteLine($"P3_BROWSER_FIXTURE_STOP_FILE={stopPath}");
                Console.Out.Flush();
                return new CaseObservation(
                    "isolated browser fixture is live with a Draft report plus a locked Dynamic Flow v1, reopened v2, and exact participant grant",
                    "state=READY;reportStatus=Draft;fieldTypes=10;valueSources=6;tableBlocks=5;flowV1=LOCKED;flowV2=DRAFT;flowParticipant=true;credentials=secret-file-only");
            });

        if (cases.Results.Last().Verdict != HarnessVerdict.DAT)
            return;

        var stopReason = "TIMEOUT";
        try
        {
            while (DateTime.UtcNow < expiresAtUtc)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(stopPath))
                {
                    stopReason = "STOP_FILE";
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
        }
        finally
        {
            if (File.Exists(secretPath))
                File.Delete(secretPath);
            if (File.Exists(stopPath))
                File.Delete(stopPath);

            await EvidenceJson.WriteAsync(
                redactedPath,
                BuildP3BrowserFixtureRedactedManifest(
                    "STOPPED",
                    readyAtUtc,
                    expiresAtUtc,
                    stopPath,
                    stopReason,
                    secretManifestDeleted: true),
                CancellationToken.None);
        }

        await cases.RunAsync(
            "P3-BROWSER-FIXTURE-STOPPED",
            () => Task.FromResult(new CaseObservation(
                $"browser fixture stopped by {stopReason}; secret manifest was deleted before database/API cleanup",
                $"state=STOPPED;reason={stopReason};secretManifestDeleted=true")));
    }

    private object BuildP3BrowserFixtureSecretManifest(
        DateTime readyAtUtc,
        DateTime expiresAtUtc,
        string stopPath)
        => new
        {
            schemaVersion = 1,
            containsSecrets = true,
            doNotPublish = true,
            lifecycle = new
            {
                state = "READY",
                readyAtUtc,
                expiresAtUtc,
                stopFile = stopPath,
                stopProtocol = "Create the stop-file (contents ignored) after browser verification."
            },
            backend = new
            {
                baseUrl = _backend.BaseUri.ToString(),
                databaseName = _mongo.DatabaseName
            },
            runtime = BuildP3BrowserFixtureRuntime(),
            actors = new
            {
                admin = P3BrowserActor(
                    "admin",
                    HarnessAssert.Required(_adminId, "P3 browser fixture admin ID"),
                    "SYSTEM_ADMIN",
                    _adminToken,
                    HarnessAssert.Required(_adminPassword, "P3 browser fixture admin password"),
                    flowAccess: "ADMIN"),
                owner = P3BrowserActor(OwnerUsername, _ownerId, "FORM_OWNER", _ownerToken, flowAccess: "OWNER"),
                assignee = P3BrowserActor(AssigneeUsername, _assigneeId, "REPORTER_ASSIGNEE", _assigneeToken, flowAccess: "UNASSIGNED_OUTSIDER"),
                reviewer = P3BrowserActor(ReviewerUsername, _reviewerId, "REPORT_REVIEWER_ASSIGNMENT_OWNER", _reviewerToken, flowAccess: "UNASSIGNED_OUTSIDER"),
                outsider = P3BrowserActor(OutsiderUsername, _outsiderId, "REPORT_OUTSIDER", _outsiderToken, flowAccess: "PARTICIPANT_LOCKED_V1")
            },
            uatPersonas = new
            {
                reporter = "actors.assignee",
                reviewer = "actors.reviewer",
                admin = "actors.admin",
                assignmentOwner = "actors.reviewer",
                dynamicFormOwner = "actors.owner",
                outsider = "actors.outsider",
                dynamicFlowOwner = "actors.owner",
                dynamicFlowAdmin = "actors.admin",
                dynamicFlowParticipant = "actors.outsider",
                dynamicFlowUnassignedOutsider = "actors.assignee"
            }
        };

    private object BuildP3BrowserFixtureRedactedManifest(
        string state,
        DateTime readyAtUtc,
        DateTime expiresAtUtc,
        string stopPath,
        string? stopReason,
        bool secretManifestDeleted)
        => new
        {
            schemaVersion = 1,
            containsSecrets = false,
            credentials = "REDACTED",
            lifecycle = new
            {
                state,
                readyAtUtc,
                expiresAtUtc,
                stoppedAtUtc = state == "STOPPED" ? DateTime.UtcNow : (DateTime?)null,
                stopReason,
                stopFile = stopPath,
                secretManifestDeleted
            },
            backend = new
            {
                baseUrl = _backend.BaseUri.ToString(),
                databaseName = _mongo.DatabaseName
            },
            runtime = BuildP3BrowserFixtureRuntime(),
            actors = new
            {
                admin = new { username = "admin", userId = _adminId, role = "SYSTEM_ADMIN", flowAccess = "ADMIN" },
                owner = new { username = OwnerUsername, userId = _ownerId, role = "FORM_OWNER", flowAccess = "OWNER" },
                assignee = new { username = AssigneeUsername, userId = _assigneeId, role = "REPORTER_ASSIGNEE", flowAccess = "UNASSIGNED_OUTSIDER" },
                reviewer = new { username = ReviewerUsername, userId = _reviewerId, role = "REPORT_REVIEWER_ASSIGNMENT_OWNER", flowAccess = "UNASSIGNED_OUTSIDER" },
                outsider = new { username = OutsiderUsername, userId = _outsiderId, role = "REPORT_OUTSIDER", flowAccess = "PARTICIPANT_LOCKED_V1" }
            },
            uatPersonas = new
            {
                reporter = "actors.assignee",
                reviewer = "actors.reviewer",
                admin = "actors.admin",
                assignmentOwner = "actors.reviewer",
                dynamicFormOwner = "actors.owner",
                outsider = "actors.outsider",
                dynamicFlowOwner = "actors.owner",
                dynamicFlowAdmin = "actors.admin",
                dynamicFlowParticipant = "actors.outsider",
                dynamicFlowUnassignedOutsider = "actors.assignee"
            }
        };

    private object BuildP3BrowserFixtureRuntime()
        => new
        {
            reportId = P3ReportId(),
            periodId = _p3PeriodId,
            workId = _p3WorkId,
            assignmentId = _p3AssignmentId,
            assigneeBindingId = _p3BindingId,
            dynamicFormTemplateId = _p3FormId,
            dynamicFormVersionNo = 1,
            dynamicFormSchemaHash = _p3SchemaHash,
            payloadRevision = _p3PayloadRevision,
            lifecycleRevision = _p3LifecycleRevision,
            status = ((WorkAssignmentReportStatus)_p3ReportStatus).ToString(),
            dynamicFlow = new
            {
                familyId = HarnessAssert.Required(_flowId, "browser fixture Dynamic Flow family"),
                lockedVersionId = HarnessAssert.Required(_lockedFlowVersionId, "browser fixture locked Flow version"),
                lockedVersionNo = 1,
                lockedPayloadHash = HarnessAssert.Required(_lockedFlowPayloadHash, "browser fixture locked Flow hash"),
                reopenedVersionId = HarnessAssert.Required(_reopenedFlowVersionId, "browser fixture reopened Flow version"),
                reopenedVersionNo = 2,
                reopenedDraftRevision = _reopenedFlowDraftRevision,
                reopenedPayloadHash = HarnessAssert.Required(_reopenedFlowPayloadHash, "browser fixture reopened Flow hash"),
                participantAssignmentId = _assignmentId,
                participantUserId = _outsiderId,
                unassignedOutsiderUserId = _assigneeId,
                endpoints = new
                {
                    family = $"api/dynamic-flow-templates/{_flowId}",
                    versions = $"api/dynamic-flow-templates/{_flowId}/versions",
                    lockedVersion = $"api/dynamic-flow-templates/{_flowId}/versions/{_lockedFlowVersionId}",
                    reopenedVersion = $"api/dynamic-flow-templates/{_flowId}/versions/{_reopenedFlowVersionId}"
                }
            },
            fieldTypeCount = P3FieldTypes.Length,
            valueSourceCount = P3SourceTypes.Length,
            tableBlocks = new[]
            {
                new { id = "p3_fixed", mode = "FIXED_GRID" },
                new { id = "p3_append_rows", mode = "APPEND_ROWS" },
                new { id = "p3_append_columns", mode = "APPEND_COLUMNS" },
                new { id = "p3_matrix", mode = "MATRIX" },
                new { id = "p3_summary", mode = "SUMMARY_TEMPLATE" }
            },
            endpoints = new
            {
                report = $"api/work-assignment-reports/{P3ReportId()}",
                sections = $"api/work-assignment-reports/{P3ReportId()}/sections",
                openPeriod = $"api/work-report-periods/{_p3PeriodId}/open"
            }
        };

    private object P3BrowserActor(
        string username,
        string userId,
        string role,
        string? token,
        string? password = null,
        string? flowAccess = null)
        => new
        {
            username,
            password = password ?? _backend.ActorPassword,
            bearerToken = HarnessAssert.Required(token, $"{username} browser fixture token"),
            userId,
            role,
            flowAccess
        };

    private async Task<CaseObservation> SetupP3RuntimeAsync(CancellationToken ct)
    {
        var reviewerToken = HarnessAssert.Required(_reviewerToken, "P1-BE-003 reviewer token");
        var catalog = await _api.GetAsync(
            $"api/label-enum-catalogs/{HarnessAssert.Required(_enumCatalogId, "P1-BE-005 enum catalog")}",
            HarnessAssert.Required(_adminToken, "P1-BE-003 admin token"),
            ct: ct);
        ApiHarnessClient.ExpectStatus(catalog, HttpStatusCode.OK, "P3 runtime enum catalog lookup");
        var catalogJson = ApiHarnessClient.RequiredObject(catalog.Json, "P3 runtime enum catalog");
        var catalogOptions = ApiHarnessClient.RequiredArray(catalogJson["options"], "P3 runtime enum catalog options");
        var firstCatalogOption = ApiHarnessClient.RequiredObject(catalogOptions.FirstOrDefault(), "P3 runtime enum catalog first option");
        _p3EnumCatalogOptionCode = ApiHarnessClient.RequiredString(firstCatalogOption, "code");
        var schemaRequest = BuildP3RuntimeFormRequest();

        await _database.GetCollection<Position>("positions").InsertOneAsync(
            new Position
            {
                Id = _p3PositionId,
                Code = P3PositionCode,
                Name = "P3 integration reviewer position",
                Order = 1,
                Rank = 1,
                UnitTypeCodes = ["P1_TEST_UNIT"],
                Version = 1,
                CreatedByUserId = _reviewerId,
                UpdatedByUserId = _reviewerId,
                CreatedAtUtc = new DateTime(2026, 7, 22, 4, 0, 0, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 7, 22, 4, 0, 0, DateTimeKind.Utc),
                IsDeleted = false
            },
            cancellationToken: ct);

        var create = await _api.PostAsync("api/dynamic-forms", schemaRequest, reviewerToken, ct: ct);
        ApiHarnessClient.ExpectStatus(create, HttpStatusCode.OK, "P3 runtime Dynamic Form create");
        _p3FormId = ApiHarnessClient.RequiredString(create.Json, "id");
        _p3FormRevision = ApiHarnessClient.RequiredInt(create.Json, "revision");

        var publish = await _api.PostAsync(
            $"api/dynamic-forms/{_p3FormId}/publish",
            new { expectedRevision = _p3FormRevision },
            reviewerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(publish, HttpStatusCode.OK, "P3 runtime Dynamic Form publish");
        _p3FormRevision = ApiHarnessClient.RequiredInt(publish.Json, "revision");
        _p3SchemaHash = ApiHarnessClient.RequiredString(publish.Json, "publishedSchemaHash");

        var form = await _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
            .Find(x => x.Id == _p3FormId && !x.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.True(form.IsPublished, "P3 runtime form was not published in Mongo");
        HarnessAssert.Equal(_p3SchemaHash, form.PublishedSchemaHash, "P3 published schema hash mismatch");
        _p3FormFieldsJson = form.FieldsJson;

        await InsertOwnedWorkAsync(
            _p3WorkId,
            "P3-DF-RUNTIME",
            "P3 Dynamic Form runtime integration work",
            _reviewerId,
            ReviewerUsername,
            "P2 Reviewer",
            ct);

        var assignmentResponse = await PostAssignmentAsync(
            _p3WorkId,
            _p3FormId,
            "P3 runtime assignment",
            _assigneeId,
            reviewerToken,
            ct);
        ApiHarnessClient.ExpectStatus(assignmentResponse, HttpStatusCode.Created, "P3 runtime assignment create");
        var assignment = ApiHarnessClient.RequiredObject(assignmentResponse.Json, "P3 runtime assignment");
        _p3AssignmentId = ApiHarnessClient.RequiredString(assignment, "id");
        HarnessAssert.Equal(_p3FormId, ApiHarnessClient.RequiredString(assignment, "dynamicFormTemplateId"), "P3 assignment template ID mismatch");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(assignment, "dynamicFormVersionNo"), "P3 assignment version mismatch");
        HarnessAssert.Equal(_p3SchemaHash, ApiHarnessClient.RequiredString(assignment, "dynamicFormSchemaHash"), "P3 assignment hash mismatch");

        var binding = await _database.GetCollection<WorkTemplateAssignee>("work_template_assignees")
            .Find(x => x.WorkAssignmentId == _p3AssignmentId && x.AssigneeUserId == _assigneeId && !x.IsDeleted)
            .SingleAsync(ct);
        _p3BindingId = binding.Id;
        var fixedAt = new DateTime(2026, 7, 22, 5, 0, 0, DateTimeKind.Utc);
        await _database.GetCollection<WorkReportPeriod>("work_report_periods").InsertOneAsync(
            new WorkReportPeriod
            {
                Id = _p3PeriodId,
                WorkId = _p3WorkId,
                WorkAssignmentId = _p3AssignmentId,
                WorkTemplateAssigneeId = binding.Id,
                DynamicFormTemplateId = _p3FormId,
                DynamicFormTemplateCode = P3FormCode,
                DynamicFormTemplateName = "P3 Dynamic Form runtime",
                DynamicFormFamilyId = _p3FormId,
                DynamicFormVersionNo = 1,
                DynamicFormSchemaHash = _p3SchemaHash,
                AssigneeUserId = _assigneeId,
                AssigneeUnitId = _unitId,
                PeriodKey = P3PeriodKey,
                PeriodInstanceKey = P3PeriodKey,
                PeriodKind = WorkReportPeriodKind.Scheduled,
                PeriodStart = fixedAt,
                PeriodEnd = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc),
                DueAtUtc = new DateTime(2026, 8, 15, 10, 0, 0, DateTimeKind.Utc),
                Status = WorkReportPeriodStatus.Pending,
                IsActive = true,
                CreatedByUserId = _reviewerId,
                UpdatedByUserId = _reviewerId,
                CreatedAtUtc = fixedAt,
                UpdatedAtUtc = fixedAt,
                IsDeleted = false
            },
            cancellationToken: ct);

        var open = await _api.PostAsync(
            $"api/work-report-periods/{_p3PeriodId}/open",
            body: null,
            HarnessAssert.Required(_assigneeToken, "P1-BE-003 assignee token"),
            ct: ct);
        ApiHarnessClient.ExpectStatus(open, HttpStatusCode.OK, "P3 runtime period open");
        ApplyP3ReportState(open.Json);
        HarnessAssert.Equal(_p3FormId, ApiHarnessClient.RequiredString(open.Json, "dynamicFormTemplateId"), "P3 report template ID mismatch");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(open.Json, "dynamicFormVersionNo"), "P3 report version mismatch");
        HarnessAssert.Equal(_p3SchemaHash, ApiHarnessClient.RequiredString(open.Json, "dynamicFormSchemaHash"), "P3 report hash mismatch");

        var fields = P3PublishedFields();
        var blocks = JsonNode.Parse(form.BlocksJson) as JsonArray
                     ?? throw new InvalidOperationException("P3 BlocksJson must be an array");
        HarnessAssert.Equal(13, fields.Count, "P3 runtime field/source fixture count mismatch");
        HarnessAssert.Equal(5, blocks.Count, "P3 runtime table-mode fixture count mismatch");
        return new CaseObservation(
            "published a two-section runtime form via API, bound assignment/period, and opened a real report with exact v1 identity",
            "fields=13;types=10;sources=6;sections=2;tableModes=5;published=true;reportOpened=true;version=1;hashExact=true");
    }

    private async Task<CaseObservation> VerifyP3DraftCapabilityMatrixAsync(CancellationToken ct)
    {
        var assignee = await GetP3ReportAsync(
            HarnessAssert.Required(_assigneeToken, "P3 assignee token"),
            ct,
            updateState: false);
        AssertP3ReportCapabilities(
            assignee.Json,
            canEditPayload: true,
            canSubmit: true,
            canWithdraw: false,
            "P3 Draft assignee capabilities");

        var reviewer = await GetP3ReportAsync(
            HarnessAssert.Required(_reviewerToken, "P3 reviewer token"),
            ct,
            updateState: false);
        AssertP3ReportCapabilities(
            reviewer.Json,
            canEditPayload: false,
            canSubmit: false,
            canWithdraw: false,
            "P3 Draft reviewer/assignment-owner capabilities");

        var adminStatus = await AssertP3ReadonlyOrForbiddenAsync(
            HarnessAssert.Required(_adminToken, "P3 admin token"),
            "system admin",
            ct);
        var ownerStatus = await AssertP3ReadonlyOrForbiddenAsync(
            HarnessAssert.Required(_ownerToken, "P3 Dynamic Form owner token"),
            "Dynamic Form owner",
            ct);
        var outsiderStatus = await AssertP3ReadonlyOrForbiddenAsync(
            HarnessAssert.Required(_outsiderToken, "P3 outsider token"),
            "outsider",
            ct);

        return new CaseObservation(
            "Draft capability flags were true only for the report assignee; reviewer/assignment-owner was read-only, while admin, form-owner, and outsider were either read-only when ACL-visible or denied",
            $"status=Draft;assigneeEdit=true;assigneeSubmit=true;assigneeWithdraw=false;reviewerReadonly=true;adminDetail={(int)adminStatus};ownerDetail={(int)ownerStatus};outsiderDetail={(int)outsiderStatus};nonAssigneeEdit=false");
    }

    private async Task<CaseObservation> SaveP3FieldTypeAsync(string fieldType, CancellationToken ct)
    {
        var field = P3FindFieldByType(fieldType);
        var requestValues = BuildP3ValidFieldValues();
        var response = await SaveP3DraftAsync(
            requestValues,
            _p3LatestTableValuesJson,
            $"p3-field-{fieldType.ToLowerInvariant()}-save",
            HarnessAssert.Required(_assigneeToken, "P3 assignee token"),
            HttpStatusCode.OK,
            ct);
        AssertP3CanonicalFieldValue(response.Json, field, fieldType);
        return new CaseObservation(
            $"{fieldType} saved through the report draft API with exact JSON kind",
            $"type={fieldType};save=200;payloadRevisionAdvanced=true;canonical=true");
    }

    private async Task<CaseObservation> ReopenP3FieldTypeAsync(string fieldType, CancellationToken ct)
    {
        var field = P3FindFieldByType(fieldType);
        var response = await GetP3ReportAsync(HarnessAssert.Required(_assigneeToken, "P3 assignee token"), ct);
        AssertP3CanonicalFieldValue(response.Json, field, fieldType);
        HarnessAssert.Equal(_p3PayloadRevision, ApiHarnessClient.RequiredInt(response.Json, "payloadRevision"), $"{fieldType} reopen payload revision mismatch");
        return new CaseObservation(
            $"{fieldType} reopened from the canonical external payload without kind loss",
            $"type={fieldType};reopen=200;roundTrip=true;revisionExact=true");
    }

    private async Task<CaseObservation> RejectP3InvalidFieldTypeAsync(string fieldType, CancellationToken ct)
    {
        var field = P3FindFieldByType(fieldType);
        var values = BuildP3ValidFieldValues();
        values[ApiHarnessClient.RequiredString(field, "id")] = P3InvalidFieldValue(fieldType);
        var before = await CaptureP3CoreWriteSnapshotAsync(ct);
        var response = await SaveP3DraftAsync(
            values,
            _p3LatestTableValuesJson,
            $"p3-field-{fieldType.ToLowerInvariant()}-invalid",
            HarnessAssert.Required(_assigneeToken, "P3 assignee token"),
            HttpStatusCode.BadRequest,
            ct,
            updateState: false);
        AssertErrorCode(response, "WORK_ASSIGNMENT_REPORT_VALUES_INVALID", $"P3 invalid {fieldType}");
        HarnessAssert.Equal(before, await CaptureP3CoreWriteSnapshotAsync(ct), $"Invalid {fieldType} changed report persistence");
        return new CaseObservation(
            $"{fieldType} wrong-kind payload failed closed before report/payload/section/log writes",
            $"type={fieldType};http=400;error=WORK_ASSIGNMENT_REPORT_VALUES_INVALID;writes=0");
    }

    private async Task<CaseObservation> VerifyP3ReviewerReadonlyFieldAsync(string fieldType, CancellationToken ct)
    {
        var reviewerToken = HarnessAssert.Required(_reviewerToken, "P3 reviewer token");
        var field = P3FindFieldByType(fieldType);
        var read = await GetP3ReportAsync(reviewerToken, ct, updateState: false);
        AssertP3CanonicalFieldValue(read.Json, field, fieldType);

        var before = await CaptureP3CoreWriteSnapshotAsync(ct);
        var denied = await SaveP3DraftAsync(
            BuildP3ValidFieldValues(),
            _p3LatestTableValuesJson,
            $"p3-field-{fieldType.ToLowerInvariant()}-reviewer-denied",
            reviewerToken,
            HttpStatusCode.Forbidden,
            ct,
            updateState: false);
        HarnessAssert.True(
            ApiHarnessClient.FindStringRecursive(denied.Json, "errorCode") is not null,
            $"Reviewer {fieldType} denial lacks stable error code");
        HarnessAssert.Equal(before, await CaptureP3CoreWriteSnapshotAsync(ct), $"Reviewer {fieldType} denial changed report persistence");
        return new CaseObservation(
            $"reviewer read {fieldType} but draft mutation returned 403 with zero writes",
            $"type={fieldType};review=200;save=403;writes=0");
    }

    private async Task<CaseObservation> VerifyP3RuntimeSourceAsync(string sourceType, CancellationToken ct)
    {
        var field = P3FindFieldBySource(sourceType);
        var fieldId = ApiHarnessClient.RequiredString(field, "id");
        var selectedCode = P3SelectedSourceCode(sourceType);
        var values = BuildP3ValidFieldValues();
        var save = await SaveP3DraftAsync(
            values,
            _p3LatestTableValuesJson,
            $"p3-source-{sourceType.ToLowerInvariant()}-active",
            HarnessAssert.Required(_assigneeToken, "P3 assignee token"),
            HttpStatusCode.OK,
            ct);
        var canonical = ParseP3FieldValues(save.Json);
        var provenance = ApiHarnessClient.RequiredObject(canonical.Root["sourceProvenance"], "P3 source provenance");
        var sourceEvidence = ApiHarnessClient.RequiredObject(provenance[fieldId], $"P3 provenance {sourceType}");
        HarnessAssert.Equal(sourceType, ApiHarnessClient.RequiredString(sourceEvidence, "sourceType"), $"{sourceType} provenance type mismatch");
        HarnessAssert.Equal(_p3SchemaHash, ApiHarnessClient.RequiredString(sourceEvidence, "sourceVersion"), $"{sourceType} provenance version mismatch");
        var codes = ApiHarnessClient.RequiredArray(sourceEvidence["codes"], $"{sourceType} provenance codes");
        HarnessAssert.True(codes.Any(x => x?.GetValue<string>() == selectedCode), $"{sourceType} provenance lost selected code");

        var staleValues = BuildP3ValidFieldValues();
        var staleCode = sourceType switch
        {
            "SYSTEM_UNIT" or "SYSTEM_USER" => ObjectId.GenerateNewId().ToString(),
            _ => $"P3_STALE_{sourceType}"
        };
        staleValues[fieldId] = field["type"]?.GetValue<string>() == "multiSelect"
            ? new JsonArray(staleCode)
            : JsonValue.Create(staleCode);
        var beforeStale = await CaptureP3CoreWriteSnapshotAsync(ct);
        var stale = await SaveP3DraftAsync(
            staleValues,
            _p3LatestTableValuesJson,
            $"p3-source-{sourceType.ToLowerInvariant()}-stale",
            HarnessAssert.Required(_assigneeToken, "P3 assignee token"),
            HttpStatusCode.BadRequest,
            ct,
            updateState: false);
        AssertErrorCode(stale, "WORK_ASSIGNMENT_REPORT_VALUES_INVALID", $"P3 stale source {sourceType}");
        HarnessAssert.Equal(beforeStale, await CaptureP3CoreWriteSnapshotAsync(ct), $"Stale {sourceType} option changed persistence");

        var beforeOutsider = await CaptureP3CoreWriteSnapshotAsync(ct);
        var outsider = await SaveP3DraftAsync(
            staleValues,
            _p3LatestTableValuesJson,
            $"p3-source-{sourceType.ToLowerInvariant()}-outsider",
            HarnessAssert.Required(_outsiderToken, "P3 outsider token"),
            HttpStatusCode.Forbidden,
            ct,
            updateState: false);
        HarnessAssert.True(!outsider.Body.Contains(staleCode, StringComparison.Ordinal), $"Outsider response leaked stale {sourceType} code");
        HarnessAssert.True(!outsider.Body.Contains("sourceProvenance", StringComparison.OrdinalIgnoreCase), $"Outsider response leaked {sourceType} provenance");
        HarnessAssert.Equal(beforeOutsider, await CaptureP3CoreWriteSnapshotAsync(ct), $"Outsider {sourceType} request changed persistence");
        return new CaseObservation(
            $"{sourceType} active value persisted with schema-version provenance; stale option failed 400 and outsider failed 403 before writes",
            $"source={sourceType};active=200;provenance=true;stale=400;outsider=403;writes=0;leak=false");
    }

    private async Task<CaseObservation> VerifyP3WritableTableModeAsync(string tableMode, string blockId, CancellationToken ct)
    {
        var tableValuesJson = BuildP3TableValuesJson(tableMode, blockId, 10 + Array.FindIndex(P3WritableTableModes, x => x.Mode == tableMode));
        var response = await SaveP3DraftAsync(
            BuildP3ValidFieldValues(),
            tableValuesJson,
            $"p3-table-{tableMode.ToLowerInvariant()}-save",
            HarnessAssert.Required(_assigneeToken, "P3 assignee token"),
            HttpStatusCode.OK,
            ct);
        AssertP3TableRoundTrip(response.Json, tableMode, blockId);

        var payloadRow = await _database.GetCollection<WorkReportTableValue>("work_report_table_values")
            .Find(x => x.ReportId == _p3ReportId && x.BlockId == blockId && x.Status == WorkReportPayloadStatus.Ready && !x.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.Equal(_p3PayloadRevision, payloadRow.PayloadRevision, $"{tableMode} Mongo payload revision mismatch");
        HarnessAssert.Equal(tableMode, payloadRow.TableMode, $"{tableMode} Mongo mode mismatch");
        HarnessAssert.True(!string.IsNullOrWhiteSpace(payloadRow.PayloadHash), $"{tableMode} Mongo block hash missing");
        var slotCount = tableMode is "APPEND_ROWS" or "APPEND_COLUMNS" ? 2 : 1;
        return new CaseObservation(
            $"{tableMode} saved/reopened with valueSlots and mode-specific projection metadata; external Mongo block matched report revision",
            $"mode={tableMode};save=200;roundTrip=true;slots={slotCount};projection=true;mongoRevision=true;hash=true");
    }

    private async Task<CaseObservation> RejectP3SummaryTemplateInputAsync(CancellationToken ct)
    {
        var before = await CaptureP3CoreWriteSnapshotAsync(ct);
        var summaryPayload = BuildP3TableValuesJson("SUMMARY_TEMPLATE", "p3_summary", 99);
        var response = await SaveP3DraftAsync(
            BuildP3ValidFieldValues(),
            summaryPayload,
            "p3-table-summary-input-denied",
            HarnessAssert.Required(_assigneeToken, "P3 assignee token"),
            HttpStatusCode.BadRequest,
            ct,
            updateState: false);
        AssertErrorCode(response, "WORK_ASSIGNMENT_REPORT_TABLE_VALUES_JSON_INVALID", "P3 SUMMARY_TEMPLATE input");
        HarnessAssert.Equal(
            "DYNAMIC_FORM_SUMMARY_TEMPLATE_INPUT_FORBIDDEN",
            ApiHarnessClient.FindStringRecursive(response.Json, "reason"),
            "P3 SUMMARY_TEMPLATE reason mismatch");
        HarnessAssert.Equal(before, await CaptureP3CoreWriteSnapshotAsync(ct), "SUMMARY_TEMPLATE input changed report persistence");
        return new CaseObservation(
            "SUMMARY_TEMPLATE client input failed closed with its stable reason and no report/payload/section/log writes",
            "mode=SUMMARY_TEMPLATE;http=400;reason=DYNAMIC_FORM_SUMMARY_TEMPLATE_INPUT_FORBIDDEN;writes=0");
    }

    private async Task<CaseObservation> VerifyP3BlockCountMatrixAsync(CancellationToken ct)
    {
        var reviewerToken = HarnessAssert.Required(_reviewerToken, "P3 reviewer token");
        foreach (var count in new[] { 1, 3, 25, 30 })
        {
            var create = await _api.PostAsync(
                "api/dynamic-forms",
                BuildP3BlockCountFormRequest(count),
                reviewerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(create, HttpStatusCode.OK, $"P3 {count}-block form create");
            var schema = ApiHarnessClient.RequiredObject(create.Json?["schema"], $"P3 {count}-block schema");
            HarnessAssert.Equal(count, ApiHarnessClient.RequiredArray(schema["blocks"], $"P3 {count}-block response").Count, $"P3 {count}-block count mismatch");
        }

        var collection = _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates");
        var before = await collection.CountDocumentsAsync(FilterDefinition<DynamicFormTemplate>.Empty, cancellationToken: ct);
        var rejected = await _api.PostAsync(
            "api/dynamic-forms",
            BuildP3BlockCountFormRequest(31),
            reviewerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(rejected, HttpStatusCode.BadRequest, "P3 31-block form create");
        AssertErrorCode(rejected, "DYNAMIC_FORM_LIMIT_EXCEEDED", "P3 31-block form create");
        HarnessAssert.Equal(before, await collection.CountDocumentsAsync(FilterDefinition<DynamicFormTemplate>.Empty, cancellationToken: ct), "P3 31-block rejection wrote a form");
        return new CaseObservation(
            "1/3/25/30 block schemas persisted exactly; 31 blocks returned the stable budget error with zero form writes",
            "blocks=1,3,25,30:200;blocks=31:400;error=DYNAMIC_FORM_LIMIT_EXCEEDED;writes31=0");
    }

    private async Task<CaseObservation> VerifyP3ConcurrentSaveAndReplayAsync(CancellationToken ct)
    {
        var expectedRevision = _p3PayloadRevision;
        var firstValues = BuildP3ValidFieldValues();
        var secondValues = BuildP3ValidFieldValues();
        firstValues[ApiHarnessClient.RequiredString(P3FindFieldByType("number"), "id")] = 101;
        secondValues[ApiHarnessClient.RequiredString(P3FindFieldByType("number"), "id")] = 202;
        var first = BuildP3DraftRequest(firstValues, _p3LatestTableValuesJson, expectedRevision, "p3-cas-save-winner-a");
        var second = BuildP3DraftRequest(secondValues, _p3LatestTableValuesJson, expectedRevision, "p3-cas-save-winner-b");
        var token = HarnessAssert.Required(_assigneeToken, "P3 assignee token");
        var firstTask = _api.PutAsync($"api/work-assignment-reports/{P3ReportId()}/draft", first, token, ct: ct);
        var secondTask = _api.PutAsync($"api/work-assignment-reports/{P3ReportId()}/draft", second, token, ct: ct);
        await Task.WhenAll(firstTask, secondTask);
        var responses = new[] { await firstTask, await secondTask };
        var winnerIndex = Array.FindIndex(responses, x => x.StatusCode == HttpStatusCode.OK);
        var loserIndex = Array.FindIndex(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        HarnessAssert.True(winnerIndex >= 0 && loserIndex >= 0 && winnerIndex != loserIndex, "P3 concurrent save did not produce one winner and one conflict");
        AssertErrorCode(responses[loserIndex], "WORK_ASSIGNMENT_REPORT_PAYLOAD_REVISION_CONFLICT", "P3 concurrent save loser");
        ApplyP3ReportState(responses[winnerIndex].Json);
        HarnessAssert.Equal(expectedRevision + 1, _p3PayloadRevision, "P3 concurrent save revision advanced more than once");

        var replayRequest = winnerIndex == 0 ? first : second;
        var beforeReplay = await CaptureP3CoreWriteSnapshotAsync(ct);
        var replay = await _api.PutAsync($"api/work-assignment-reports/{P3ReportId()}/draft", replayRequest, token, ct: ct);
        ApiHarnessClient.ExpectStatus(replay, HttpStatusCode.OK, "P3 concurrent save winner replay");
        ApplyP3ReportState(replay.Json);
        HarnessAssert.Equal(expectedRevision + 1, _p3PayloadRevision, "P3 exact replay advanced payload revision");
        HarnessAssert.Equal(beforeReplay, await CaptureP3CoreWriteSnapshotAsync(ct), "P3 exact save replay wrote persistence");
        return new CaseObservation(
            "two same-revision saves produced one 200/one 409; exact winner replay returned 200 without a second write",
            "concurrent=2;success=1;conflict=1;revisionDelta=1;exactReplay=200;replayWrites=0");
    }

    private async Task<CaseObservation> RejectP3RequiredSubmitWithoutWritesAsync(CancellationToken ct)
    {
        var values = BuildP3ValidFieldValues();
        var required = P3PublishedFields().OfType<JsonObject>().First(x => x["required"]?.GetValue<bool>() == true);
        values.Remove(ApiHarnessClient.RequiredString(required, "id"));
        var request = BuildP3SubmitRequest(values, "p3-submit-required-missing");
        var before = await CaptureP3CoreWriteSnapshotAsync(ct);
        var response = await _api.PostAsync(
            $"api/work-assignment-reports/{P3ReportId()}/submit",
            request,
            HarnessAssert.Required(_assigneeToken, "P3 assignee token"),
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.BadRequest, "P3 required submit rejection");
        AssertErrorCode(response, "WORK_ASSIGNMENT_REPORT_VALUES_INVALID", "P3 required submit rejection");
        HarnessAssert.Equal(before, await CaptureP3CoreWriteSnapshotAsync(ct), "P3 required submit rejection wrote persistence");
        return new CaseObservation(
            "required validation ran at submit and rejected the one-call payload before report/payload/period/section/log writes",
            "submit=400;error=WORK_ASSIGNMENT_REPORT_VALUES_INVALID;requiredStage=submit;writes=0");
    }

    private async Task<CaseObservation> VerifyP3PostCommitOutboxRecoveryAsync(CancellationToken ct)
    {
        const string failureMarker = "TEST_ONLY_LIFECYCLE_PROJECTION_FAILURE_AFTER_COMMIT";
        const int workerMaxReports = 20;
        var commandId = BackendServerLease.P3LifecycleProjectionFailureCommandId;
        var assigneeToken = HarnessAssert.Required(_assigneeToken, "P3 assignee token");
        var adminToken = HarnessAssert.Required(_adminToken, "P3 admin token");
        var reports = _database.GetCollection<WorkAssignmentReport>("work_assignment_report");
        var periods = _database.GetCollection<WorkReportPeriod>("work_report_periods");
        var sections = _database.GetCollection<WorkAssignmentReportSection>("work_assignment_report_sections");

        var before = await LoadP3ReportDocumentAsync(ct);
        HarnessAssert.Equal(WorkAssignmentReportStatus.Draft, before.Status, "P3 outbox recovery requires Draft state");
        var periodBefore = await periods.Find(x => x.Id == _p3PeriodId && !x.IsDeleted).SingleAsync(ct);
        var request = BuildP3SubmitRequest(BuildP3ValidFieldValues(), commandId);
        var submit = await _api.PostAsync(
            $"api/work-assignment-reports/{P3ReportId()}/submit",
            request,
            assigneeToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(submit, HttpStatusCode.Accepted, "P3 post-commit injected projection failure submit");
        ApplyP3ReportState(submit.Json);
        HarnessAssert.Equal(
            "COMMITTED_PENDING_PROJECTION",
            ApiHarnessClient.RequiredString(submit.Json, "lifecycleCommitState"),
            "P3 injected submit lifecycle commit state mismatch");
        HarnessAssert.True(
            ApiHarnessClient.RequiredBool(submit.Json, "lifecycleProjectionPending"),
            "P3 injected submit did not disclose pending lifecycle projection");
        AssertP3ReportCapabilities(
            submit.Json,
            canEditPayload: false,
            canSubmit: false,
            canWithdraw: true,
            "P3 projection-pending Submitted response capabilities");

        var pendingReport = await reports.Find(x => x.Id == P3ReportId() && !x.IsDeleted).SingleAsync(ct);
        HarnessAssert.Equal(WorkAssignmentReportStatus.Submitted, pendingReport.Status, "P3 injected submit core status was not durable");
        HarnessAssert.Equal(before.PayloadRevision + 1, pendingReport.PayloadRevision, "P3 injected submit payload revision mismatch");
        HarnessAssert.Equal(before.LifecycleRevision + 1, pendingReport.LifecycleRevision, "P3 injected submit lifecycle revision mismatch");
        HarnessAssert.Equal(commandId, pendingReport.LastLifecycleCommandId, "P3 injected submit command identity mismatch");
        HarnessAssert.True(string.IsNullOrWhiteSpace(pendingReport.LifecycleProjectionClaimToken), "P3 failed foreground claim was not released");
        HarnessAssert.True(
            (pendingReport.LifecycleProjectionLastError ?? string.Empty).Contains(failureMarker, StringComparison.Ordinal),
            "P3 failed foreground projection did not retain the test-only error marker");

        var pendingMatches = (pendingReport.LifecycleProjectionOutbox ?? [])
            .Where(x => string.Equals(x.CommandId, commandId, StringComparison.Ordinal))
            .ToList();
        HarnessAssert.Equal(1, pendingMatches.Count, "P3 injected submit outbox entry was not unique");
        var pendingEntry = pendingMatches.Single();
        HarnessAssert.Equal(WorkReportLifecycleProjectionOutboxStates.Pending, pendingEntry.State, "P3 injected submit outbox was not PENDING");
        HarnessAssert.Equal(1, pendingEntry.AttemptCount, "P3 injected foreground failure attempt count mismatch");
        HarnessAssert.Equal(pendingReport.LifecycleRevision, pendingEntry.LifecycleRevision, "P3 pending outbox lifecycle revision mismatch");
        HarnessAssert.Equal(pendingReport.PayloadRevision, pendingEntry.PayloadRevision, "P3 pending outbox payload revision mismatch");
        HarnessAssert.Equal(pendingReport.PayloadHash, pendingEntry.PayloadHash, "P3 pending outbox payload hash mismatch");
        HarnessAssert.True(pendingEntry.CompletedAtUtc is null, "P3 pending outbox unexpectedly had a completion timestamp");
        HarnessAssert.True(
            (pendingEntry.LastError ?? string.Empty).Contains(failureMarker, StringComparison.Ordinal),
            "P3 pending outbox did not retain the injected failure marker");
        HarnessAssert.Equal(1, pendingEntry.BusinessEvents.Count, "P3 submit outbox business event count mismatch");
        var businessEvent = pendingEntry.BusinessEvents.Single();
        HarnessAssert.Equal("SUBMIT", businessEvent.ReportLogAction, "P3 submit outbox report-log action mismatch");
        HarnessAssert.True(!string.IsNullOrWhiteSpace(businessEvent.UserAction), "P3 submit outbox user action is missing");
        HarnessAssert.True(!string.IsNullOrWhiteSpace(businessEvent.StatusOperation), "P3 submit outbox status operation is missing");
        var eventKey = HarnessAssert.Required(businessEvent.EventKey, "P3 submit lifecycle business event key");

        var periodPending = await periods.Find(x => x.Id == _p3PeriodId && !x.IsDeleted).SingleAsync(ct);
        HarnessAssert.Equal(periodBefore.Status, periodPending.Status, "P3 failed foreground unexpectedly projected period status");
        HarnessAssert.Equal(
            periodBefore.SourceLifecycleRevision,
            periodPending.SourceLifecycleRevision,
            "P3 failed foreground unexpectedly projected period source revision");
        HarnessAssert.True(
            periodPending.SourceLifecycleRevision != pendingReport.LifecycleRevision,
            "P3 pending evidence did not retain a stale period source revision");

        var pendingLogCounts = await CaptureP3LifecycleEventCountsAsync(eventKey, ct);
        AssertP3LifecycleEventCounts(pendingLogCounts, 0, "P3 pending business projections");

        var deletedSection = await sections
            .Find(x => x.WorkAssignmentReportId == P3ReportId() && x.SectionId == "main" && !x.IsDeleted)
            .SingleAsync(ct);
        var deletion = await sections.DeleteOneAsync(x => x.Id == deletedSection.Id, ct);
        HarnessAssert.Equal(1L, deletion.DeletedCount, "P3 recovery fixture did not delete exactly one section");
        HarnessAssert.Equal(
            1L,
            await sections.CountDocumentsAsync(x => x.WorkAssignmentReportId == P3ReportId() && !x.IsDeleted, cancellationToken: ct),
            "P3 recovery fixture did not leave exactly one active section");

        var worker = await _api.PostAsync(
            $"api/admin/operations/job-runs/lifecycle-projection-outbox/process?maxReports={workerMaxReports}",
            body: null,
            bearerToken: adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(worker, HttpStatusCode.OK, "P3 lifecycle projection worker retry");
        HarnessAssert.True(ApiHarnessClient.RequiredBool(worker.Json, "ok"), "P3 lifecycle projection worker returned ok=false");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(worker.Json, "processed"), "P3 lifecycle projection worker processed count mismatch");
        HarnessAssert.Equal(workerMaxReports, ApiHarnessClient.RequiredInt(worker.Json, "maxReports"), "P3 lifecycle projection worker maxReports mismatch");

        var convergedReport = await reports.Find(x => x.Id == P3ReportId() && !x.IsDeleted).SingleAsync(ct);
        var convergedMatches = (convergedReport.LifecycleProjectionOutbox ?? [])
            .Where(x => string.Equals(x.CommandId, commandId, StringComparison.Ordinal))
            .ToList();
        HarnessAssert.Equal(1, convergedMatches.Count, "P3 converged outbox entry was not unique");
        var convergedEntry = convergedMatches.Single();
        HarnessAssert.Equal(WorkReportLifecycleProjectionOutboxStates.Completed, convergedEntry.State, "P3 worker did not complete the pending outbox");
        HarnessAssert.Equal(2, convergedEntry.AttemptCount, "P3 outbox retry attempt count mismatch");
        HarnessAssert.True(convergedEntry.CompletedAtUtc.HasValue, "P3 completed outbox timestamp is missing");
        HarnessAssert.True(string.IsNullOrWhiteSpace(convergedEntry.LastError), "P3 completed outbox retained an entry error");
        HarnessAssert.True(string.IsNullOrWhiteSpace(convergedReport.LifecycleProjectionLastError), "P3 converged report retained a projection error");
        HarnessAssert.True(string.IsNullOrWhiteSpace(convergedReport.LifecycleProjectionClaimToken), "P3 converged report retained a claim token");
        HarnessAssert.Equal(
            convergedReport.LifecycleRevision,
            convergedReport.LifecycleProjectionLastCompletedRevision,
            "P3 completed projection revision mismatch");

        var recreatedSections = await sections
            .Find(x => x.WorkAssignmentReportId == P3ReportId() && !x.IsDeleted)
            .SortBy(x => x.SectionOrder)
            .ToListAsync(ct);
        HarnessAssert.Equal(2, recreatedSections.Count, "P3 worker did not recreate the exact published section count");
        var recreatedMain = recreatedSections.Single(x => x.SectionId == "main");
        HarnessAssert.True(!string.Equals(deletedSection.Id, recreatedMain.Id, StringComparison.Ordinal), "P3 worker did not recreate the deleted section row");
        foreach (var section in recreatedSections)
        {
            HarnessAssert.Equal(convergedReport.PayloadRevision, section.SourcePayloadRevision, $"P3 recreated section {section.SectionId} payload revision mismatch");
            HarnessAssert.Equal(convergedReport.PayloadHash, section.SourcePayloadHash, $"P3 recreated section {section.SectionId} payload hash mismatch");
            HarnessAssert.Equal(convergedReport.LifecycleRevision, section.SourceLifecycleRevision, $"P3 recreated section {section.SectionId} lifecycle revision mismatch");
            HarnessAssert.Equal(convergedReport.Status, section.Status, $"P3 recreated section {section.SectionId} status mismatch");
            HarnessAssert.Equal(_p3FormId, section.DynamicFormTemplateId, $"P3 recreated section {section.SectionId} template mismatch");
            HarnessAssert.Equal(1, section.DynamicFormVersionNo, $"P3 recreated section {section.SectionId} version mismatch");
            HarnessAssert.Equal(_p3SchemaHash, section.DynamicFormSchemaHash, $"P3 recreated section {section.SectionId} schema hash mismatch");
        }

        var convergedPeriod = await periods.Find(x => x.Id == _p3PeriodId && !x.IsDeleted).SingleAsync(ct);
        HarnessAssert.Equal(P3ReportId(), convergedPeriod.CurrentReportId, "P3 converged period current report mismatch");
        HarnessAssert.Equal(P3ReportId(), convergedPeriod.SourceLifecycleReportId, "P3 converged period source report mismatch");
        HarnessAssert.Equal(convergedReport.LifecycleRevision, convergedPeriod.SourceLifecycleRevision, "P3 converged period source revision mismatch");
        HarnessAssert.Equal(WorkReportPeriodStatus.Submitted, convergedPeriod.Status, "P3 converged period status mismatch");
        HarnessAssert.True(convergedPeriod.SourceLifecycleAppliedAtUtc.HasValue, "P3 converged period source timestamp is missing");

        var projectedLogCounts = await CaptureP3LifecycleEventCountsAsync(eventKey, ct);
        AssertP3LifecycleEventCounts(projectedLogCounts, 1, "P3 converged business projections");

        var convergedApi = await GetP3ReportAsync(assigneeToken, ct);
        HarnessAssert.Equal(
            "COMMITTED",
            ApiHarnessClient.RequiredString(convergedApi.Json, "lifecycleCommitState"),
            "P3 recovered report lifecycle commit state mismatch");
        HarnessAssert.True(
            !ApiHarnessClient.RequiredBool(convergedApi.Json, "lifecycleProjectionPending"),
            "P3 recovered report still disclosed a pending lifecycle projection");
        AssertP3ReportCapabilities(
            convergedApi.Json,
            canEditPayload: false,
            canSubmit: false,
            canWithdraw: true,
            "P3 recovered Submitted capabilities");

        var beforeReplay = await CaptureP3CoreWriteSnapshotAsync(ct);
        var replay = await _api.PostAsync(
            $"api/work-assignment-reports/{P3ReportId()}/submit",
            request,
            assigneeToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(replay, HttpStatusCode.OK, "P3 recovered submit exact replay");
        ApplyP3ReportState(replay.Json);
        HarnessAssert.Equal(
            "COMMITTED",
            ApiHarnessClient.RequiredString(replay.Json, "lifecycleCommitState"),
            "P3 recovered submit replay lifecycle commit state mismatch");
        HarnessAssert.True(
            !ApiHarnessClient.RequiredBool(replay.Json, "lifecycleProjectionPending"),
            "P3 recovered submit replay unexpectedly disclosed pending projection");
        HarnessAssert.Equal(beforeReplay, await CaptureP3CoreWriteSnapshotAsync(ct), "P3 recovered submit exact replay wrote persistence");

        var beforeIdleWorker = await CaptureP3CoreWriteSnapshotAsync(ct);
        var idleWorker = await _api.PostAsync(
            $"api/admin/operations/job-runs/lifecycle-projection-outbox/process?maxReports={workerMaxReports}",
            body: null,
            bearerToken: adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(idleWorker, HttpStatusCode.OK, "P3 lifecycle projection idle worker replay");
        HarnessAssert.Equal(0, ApiHarnessClient.RequiredInt(idleWorker.Json, "processed"), "P3 idle worker unexpectedly found pending work");
        HarnessAssert.Equal(beforeIdleWorker, await CaptureP3CoreWriteSnapshotAsync(ct), "P3 idle worker changed core persistence");
        AssertP3LifecycleEventCounts(await CaptureP3LifecycleEventCountsAsync(eventKey, ct), 1, "P3 exactly-once business projections");

        await EvidenceJson.WriteAsync(
            Path.Combine(_iterationRoot, "p3-lifecycle-outbox-recovery.json"),
            new
            {
                reportId = P3ReportId(),
                commandId,
                injectedFailure = new
                {
                    marker = failureMarker,
                    httpStatus = (int)submit.StatusCode,
                    lifecycleCommitState = "COMMITTED_PENDING_PROJECTION",
                    lifecycleProjectionPending = true,
                    state = pendingEntry.State,
                    pendingEntry.AttemptCount,
                    coreStatus = pendingReport.Status.ToString(),
                    pendingReport.PayloadRevision,
                    pendingReport.LifecycleRevision,
                    periodSourceRevision = periodPending.SourceLifecycleRevision,
                    businessProjectionCounts = pendingLogCounts
                },
                retry = new
                {
                    processed = 1,
                    state = convergedEntry.State,
                    lifecycleCommitState = "COMMITTED",
                    lifecycleProjectionPending = false,
                    convergedEntry.AttemptCount,
                    sectionDeletedId = deletedSection.Id,
                    sectionRecreatedId = recreatedMain.Id,
                    sectionCount = recreatedSections.Count,
                    periodSourceReportId = convergedPeriod.SourceLifecycleReportId,
                    periodSourceRevision = convergedPeriod.SourceLifecycleRevision,
                    businessProjectionCounts = projectedLogCounts
                },
                exactlyOnce = new
                {
                    outboxEntriesForCommand = convergedMatches.Count,
                    exactApiReplayWrites = 0,
                    idleWorkerProcessed = 0,
                    businessProjectionCountEach = 1
                }
            },
            ct);

        var withdraw = BuildP3LifecycleRequest("p3-outbox-failure-recovery-restore-withdraw", "restore Draft after outbox recovery");
        var restored = await _api.PostAsync(
            $"api/work-assignment-reports/{P3ReportId()}/withdraw-submitted",
            withdraw,
            assigneeToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(restored, HttpStatusCode.OK, "P3 restore Draft after outbox recovery");
        ApplyP3ReportState(restored.Json);
        HarnessAssert.Equal((int)WorkAssignmentReportStatus.Draft, _p3ReportStatus, "P3 outbox recovery did not restore Draft state");
        AssertP3ReportCapabilities(
            restored.Json,
            canEditPayload: true,
            canSubmit: true,
            canWithdraw: false,
            "P3 restored Draft capabilities");

        return new CaseObservation(
            "a real submit returned 202 after its core commit hit a deterministic projection failure, retained one PENDING outbox, then the authenticated worker recreated a deleted section and converged period/audit projections exactly once",
            "foreground=202;commitState=COMMITTED_PENDING_PROJECTION;pendingFlag=true;pending=1;pendingAttempt=1;pendingBusinessLogs=0;workerProcessed=1;completed=1;recoveredCommitState=COMMITTED;recoveredPendingFlag=false;attempts=2;sections=2;sectionRecreated=true;periodSourceExact=true;businessLogsEach=1;outboxEntries=1;apiReplayWrites=0;idleWorkerProcessed=0;restored=Draft;artifact=p3-lifecycle-outbox-recovery.json");
    }

    private async Task<CaseObservation> VerifyP3SubmitAndWithdrawAsync(CancellationToken ct)
    {
        var submitRequest = BuildP3SubmitRequest(BuildP3ValidFieldValues(), "p3-submit-one-call-first");
        var token = HarnessAssert.Required(_assigneeToken, "P3 assignee token");
        var submit = await _api.PostAsync($"api/work-assignment-reports/{P3ReportId()}/submit", submitRequest, token, ct: ct);
        ApiHarnessClient.ExpectStatus(submit, HttpStatusCode.OK, "P3 one-call submit");
        ApplyP3ReportState(submit.Json);
        HarnessAssert.Equal((int)WorkAssignmentReportStatus.Submitted, _p3ReportStatus, "P3 submit status mismatch");
        AssertP3ReportCapabilities(
            submit.Json,
            canEditPayload: false,
            canSubmit: false,
            canWithdraw: true,
            "P3 Submitted assignee capabilities");

        var submittedPayloadRevision = _p3PayloadRevision;
        var submittedLifecycleRevision = _p3LifecycleRevision;
        var beforeSubmitReplay = await CaptureP3CoreWriteSnapshotAsync(ct);
        var submitReplay = await _api.PostAsync($"api/work-assignment-reports/{P3ReportId()}/submit", submitRequest, token, ct: ct);
        ApiHarnessClient.ExpectStatus(submitReplay, HttpStatusCode.OK, "P3 submit exact replay");
        ApplyP3ReportState(submitReplay.Json);
        HarnessAssert.Equal(beforeSubmitReplay, await CaptureP3CoreWriteSnapshotAsync(ct), "P3 submit exact replay wrote persistence");

        var withdrawA = BuildP3LifecycleRequest("p3-withdraw-race-a", "withdraw A");
        var withdrawB = BuildP3LifecycleRequest("p3-withdraw-race-b", "withdraw B");
        var taskA = _api.PostAsync($"api/work-assignment-reports/{P3ReportId()}/withdraw-submitted", withdrawA, token, ct: ct);
        var taskB = _api.PostAsync($"api/work-assignment-reports/{P3ReportId()}/withdraw-submitted", withdrawB, token, ct: ct);
        await Task.WhenAll(taskA, taskB);
        var responses = new[] { await taskA, await taskB };
        var winnerIndex = Array.FindIndex(responses, x => x.StatusCode == HttpStatusCode.OK);
        var loserIndex = Array.FindIndex(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        HarnessAssert.True(winnerIndex >= 0 && loserIndex >= 0, "P3 withdraw CAS did not produce one winner and one conflict");
        AssertErrorCode(responses[loserIndex], "WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT", "P3 withdraw CAS loser");
        ApplyP3ReportState(responses[winnerIndex].Json);
        HarnessAssert.Equal((int)WorkAssignmentReportStatus.Draft, _p3ReportStatus, "P3 withdraw status mismatch");
        AssertP3ReportCapabilities(
            responses[winnerIndex].Json,
            canEditPayload: true,
            canSubmit: true,
            canWithdraw: false,
            "P3 withdrawn Draft assignee capabilities");
        HarnessAssert.Equal(submittedPayloadRevision, _p3PayloadRevision, "P3 withdraw changed payload revision");
        HarnessAssert.Equal(submittedLifecycleRevision + 1, _p3LifecycleRevision, "P3 withdraw lifecycle revision mismatch");

        var replayRequest = winnerIndex == 0 ? withdrawA : withdrawB;
        var beforeReplay = await CaptureP3CoreWriteSnapshotAsync(ct);
        var replay = await _api.PostAsync($"api/work-assignment-reports/{P3ReportId()}/withdraw-submitted", replayRequest, token, ct: ct);
        ApiHarnessClient.ExpectStatus(replay, HttpStatusCode.OK, "P3 withdraw exact replay");
        ApplyP3ReportState(replay.Json);
        HarnessAssert.Equal(beforeReplay, await CaptureP3CoreWriteSnapshotAsync(ct), "P3 withdraw exact replay wrote persistence");
        return new CaseObservation(
            "one-call submit advanced payload+lifecycle once; exact replay wrote nothing; concurrent withdraw produced one winner/one lifecycle conflict and exact replay",
            "submit=200;submitReplay=200;submitReplayWrites=0;withdrawSuccess=1;withdrawConflict=1;payloadDeltaWithdraw=0;lifecycleDeltaWithdraw=1;withdrawReplayWrites=0");
    }

    private async Task<CaseObservation> VerifyP3ReviewerReturnAsync(CancellationToken ct)
    {
        await P3SubmitCurrentDraftAsync("p3-submit-before-review-return", ct);
        var request = BuildP3ReviewLifecycleRequest("p3-review-return", "P3 return for correction");
        var reviewerToken = HarnessAssert.Required(_reviewerToken, "P3 reviewer token");
        var response = await _api.PostAsync(
            $"api/work-assignment-review/reports/{P3ReportId()}/return",
            request,
            reviewerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, "P3 reviewer return");
        await GetP3ReportAsync(HarnessAssert.Required(_assigneeToken, "P3 assignee token"), ct);
        HarnessAssert.Equal((int)WorkAssignmentReportStatus.Draft, _p3ReportStatus, "P3 reviewer return status mismatch");
        var beforeReplay = await CaptureP3CoreWriteSnapshotAsync(ct);
        var replay = await _api.PostAsync(
            $"api/work-assignment-review/reports/{P3ReportId()}/return",
            request,
            reviewerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(replay, HttpStatusCode.OK, "P3 reviewer return replay");
        HarnessAssert.Equal(beforeReplay, await CaptureP3CoreWriteSnapshotAsync(ct), "P3 reviewer return replay wrote persistence");
        return new CaseObservation(
            "reviewer returned a submitted report to draft under lifecycle CAS; exact replay was write-free",
            "submit=200;reviewReturn=200;status=Draft;lifecycleDelta=1;exactReplay=200;replayWrites=0");
    }

    private async Task<CaseObservation> VerifyP3ApproveAndRecallAsync(CancellationToken ct)
    {
        await P3SubmitCurrentDraftAsync("p3-submit-before-approve", ct);
        var reviewerToken = HarnessAssert.Required(_reviewerToken, "P3 reviewer token");
        var approve = BuildP3ApproveRequest("p3-review-approve", "P3 approved");
        var approved = await _api.PostAsync(
            $"api/work-assignment-review/reports/{P3ReportId()}/approve",
            approve,
            reviewerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(approved, HttpStatusCode.OK, "P3 reviewer approve");
        var approvedReport = await GetP3ReportAsync(HarnessAssert.Required(_assigneeToken, "P3 assignee token"), ct);
        HarnessAssert.Equal((int)WorkAssignmentReportStatus.Approved, _p3ReportStatus, "P3 approve status mismatch");
        AssertP3ReportCapabilities(
            approvedReport.Json,
            canEditPayload: false,
            canSubmit: false,
            canWithdraw: false,
            "P3 manually Approved assignee capabilities");
        var beforeApproveReplay = await CaptureP3CoreWriteSnapshotAsync(ct);
        var approveReplay = await _api.PostAsync(
            $"api/work-assignment-review/reports/{P3ReportId()}/approve",
            approve,
            reviewerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(approveReplay, HttpStatusCode.OK, "P3 reviewer approve replay");
        HarnessAssert.Equal(beforeApproveReplay, await CaptureP3CoreWriteSnapshotAsync(ct), "P3 approve replay wrote persistence");

        var recall = BuildP3ReviewLifecycleRequest("p3-review-recall", "P3 recall approved report");
        var recalled = await _api.PostAsync(
            $"api/work-assignment-review/reports/{P3ReportId()}/recall-approved",
            recall,
            reviewerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(recalled, HttpStatusCode.OK, "P3 reviewer recall");
        var recalledReport = await GetP3ReportAsync(HarnessAssert.Required(_assigneeToken, "P3 assignee token"), ct);
        HarnessAssert.Equal((int)WorkAssignmentReportStatus.Submitted, _p3ReportStatus, "P3 recall status mismatch");
        AssertP3ReportCapabilities(
            recalledReport.Json,
            canEditPayload: false,
            canSubmit: false,
            canWithdraw: true,
            "P3 recalled Submitted assignee capabilities");
        var beforeRecallReplay = await CaptureP3CoreWriteSnapshotAsync(ct);
        var recallReplay = await _api.PostAsync(
            $"api/work-assignment-review/reports/{P3ReportId()}/recall-approved",
            recall,
            reviewerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(recallReplay, HttpStatusCode.OK, "P3 reviewer recall replay");
        HarnessAssert.Equal(beforeRecallReplay, await CaptureP3CoreWriteSnapshotAsync(ct), "P3 recall replay wrote persistence");
        return new CaseObservation(
            "reviewer approved then recalled the report under exact payload/lifecycle tokens; both command replays were write-free",
            "approve=200;statusApproved=true;approveReplayWrites=0;recall=200;statusSubmitted=true;recallReplayWrites=0");
    }

    private async Task<CaseObservation> VerifyP3ApproveVsReturnRaceAsync(CancellationToken ct)
    {
        HarnessAssert.Equal((int)WorkAssignmentReportStatus.Submitted, _p3ReportStatus, "P3 approve-vs-return race requires Submitted state");
        var expectedPayloadRevision = _p3PayloadRevision;
        var expectedLifecycleRevision = _p3LifecycleRevision;
        var approvePath = $"api/work-assignment-review/reports/{P3ReportId()}/approve";
        var returnPath = $"api/work-assignment-review/reports/{P3ReportId()}/return";
        var approve = BuildP3ApproveRequest("p3-race-approve", "P3 race approve");
        var returned = BuildP3ReviewLifecycleRequest("p3-race-return", "P3 race return");
        var reviewerToken = HarnessAssert.Required(_reviewerToken, "P3 reviewer token");

        var approveTask = _api.PostAsync(approvePath, approve, reviewerToken, ct: ct);
        var returnTask = _api.PostAsync(returnPath, returned, reviewerToken, ct: ct);
        await Task.WhenAll(approveTask, returnTask);
        var responses = new[] { await approveTask, await returnTask };
        var winnerIndex = Array.FindIndex(responses, x => x.StatusCode == HttpStatusCode.OK);
        var loserIndex = Array.FindIndex(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        HarnessAssert.True(winnerIndex >= 0 && loserIndex >= 0 && winnerIndex != loserIndex, "P3 approve-vs-return race did not produce one winner and one conflict");
        AssertErrorCode(responses[loserIndex], "WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT", "P3 approve-vs-return loser");

        _p3LifecycleRaceWinnerPath = winnerIndex == 0 ? approvePath : returnPath;
        _p3LifecycleRaceWinnerRequest = (winnerIndex == 0 ? approve : returned).DeepClone() as JsonObject
            ?? throw new InvalidOperationException("P3 lifecycle race winner request clone failed");
        _p3LifecycleRaceChangedRequest = _p3LifecycleRaceWinnerRequest.DeepClone() as JsonObject
            ?? throw new InvalidOperationException("P3 lifecycle race changed request clone failed");
        _p3LifecycleRaceChangedRequest["comment"] = winnerIndex == 0 ? "P3 changed approve replay" : "P3 changed return replay";
        _p3LifecycleRaceWinnerStatus = winnerIndex == 0
            ? (int)WorkAssignmentReportStatus.Approved
            : (int)WorkAssignmentReportStatus.Draft;

        await GetP3ReportAsync(HarnessAssert.Required(_assigneeToken, "P3 assignee token"), ct);
        HarnessAssert.Equal(_p3LifecycleRaceWinnerStatus, _p3ReportStatus, "P3 approve-vs-return winner status mismatch");
        HarnessAssert.Equal(expectedPayloadRevision, _p3PayloadRevision, "P3 approve-vs-return race changed payload revision");
        HarnessAssert.Equal(expectedLifecycleRevision + 1, _p3LifecycleRevision, "P3 approve-vs-return race advanced lifecycle more than once");
        return new CaseObservation(
            "approve and return raced on the same payload/lifecycle token; exactly one lifecycle transition committed",
            "approveVsReturn=2;success=1;conflict=1;winnerStatusOneOf=Draft|Approved;payloadDelta=0;lifecycleDelta=1");
    }

    private async Task<CaseObservation> VerifyP3LifecycleReplayIdentityAsync(CancellationToken ct)
    {
        var winnerPath = HarnessAssert.Required(_p3LifecycleRaceWinnerPath, "P3 lifecycle race winner path");
        var winnerRequest = _p3LifecycleRaceWinnerRequest
            ?? throw new InvalidOperationException("P3 lifecycle race winner request is missing");
        var changedRequest = _p3LifecycleRaceChangedRequest
            ?? throw new InvalidOperationException("P3 lifecycle race changed request is missing");
        var reviewerToken = HarnessAssert.Required(_reviewerToken, "P3 reviewer token");

        var beforeExact = await CaptureP3CoreWriteSnapshotAsync(ct);
        var exact = await _api.PostAsync(winnerPath, winnerRequest, reviewerToken, ct: ct);
        ApiHarnessClient.ExpectStatus(exact, HttpStatusCode.OK, "P3 lifecycle exact replay");
        HarnessAssert.Equal(beforeExact, await CaptureP3CoreWriteSnapshotAsync(ct), "P3 lifecycle exact replay wrote persistence");

        var beforeChanged = await CaptureP3CoreWriteSnapshotAsync(ct);
        var changed = await _api.PostAsync(winnerPath, changedRequest, reviewerToken, ct: ct);
        ApiHarnessClient.ExpectStatus(changed, HttpStatusCode.Conflict, "P3 lifecycle changed replay");
        AssertErrorCode(changed, "WORK_ASSIGNMENT_REPORT_LIFECYCLE_COMMAND_REPLAY_MISMATCH", "P3 lifecycle changed replay");
        HarnessAssert.Equal(beforeChanged, await CaptureP3CoreWriteSnapshotAsync(ct), "P3 lifecycle changed replay wrote persistence");

        if (_p3LifecycleRaceWinnerStatus == (int)WorkAssignmentReportStatus.Approved)
        {
            var recall = BuildP3ReviewLifecycleRequest("p3-race-replay-restore-recall", "P3 restore submitted after replay probe");
            var response = await _api.PostAsync(
                $"api/work-assignment-review/reports/{P3ReportId()}/recall-approved",
                recall,
                reviewerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, "P3 replay probe restore recall");
            await GetP3ReportAsync(HarnessAssert.Required(_assigneeToken, "P3 assignee token"), ct);
        }
        else
        {
            await P3SubmitCurrentDraftAsync("p3-race-replay-restore-submit", ct);
            await GetP3ReportAsync(HarnessAssert.Required(_assigneeToken, "P3 assignee token"), ct);
        }

        HarnessAssert.Equal((int)WorkAssignmentReportStatus.Submitted, _p3ReportStatus, "P3 replay probe did not restore Submitted state");
        return new CaseObservation(
            "the winning lifecycle command replayed byte-equivalent with zero writes; the same commandId with a changed comment failed 409 with zero writes",
            "exactReplay=200;exactReplayWrites=0;changedReplay=409;error=WORK_ASSIGNMENT_REPORT_LIFECYCLE_COMMAND_REPLAY_MISMATCH;changedReplayWrites=0;restored=Submitted");
    }

    private async Task<CaseObservation> VerifyP3RecallVsDeactivateRaceAsync(CancellationToken ct)
    {
        HarnessAssert.Equal((int)WorkAssignmentReportStatus.Submitted, _p3ReportStatus, "P3 recall-vs-deactivate race requires Submitted state");
        var reviewerToken = HarnessAssert.Required(_reviewerToken, "P3 reviewer token");
        var approve = BuildP3ApproveRequest("p3-race-pre-deactivate-approve", "P3 approve before recall/deactivate race");
        var approved = await _api.PostAsync(
            $"api/work-assignment-review/reports/{P3ReportId()}/approve",
            approve,
            reviewerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(approved, HttpStatusCode.OK, "P3 approve before recall/deactivate race");
        await GetP3ReportAsync(HarnessAssert.Required(_assigneeToken, "P3 assignee token"), ct);
        HarnessAssert.Equal((int)WorkAssignmentReportStatus.Approved, _p3ReportStatus, "P3 pre-race approve status mismatch");

        var raceRevision = _p3LifecycleRevision;
        var recall = BuildP3ReviewLifecycleRequest("p3-race-recall", "P3 recall/deactivate race recall");
        var deactivate = BuildP3ReviewLifecycleRequest("p3-race-deactivate", "P3 recall/deactivate race deactivate");
        var recallTask = _api.PostAsync(
            $"api/work-assignment-review/reports/{P3ReportId()}/recall-approved",
            recall,
            reviewerToken,
            ct: ct);
        var deactivateTask = _api.PostAsync(
            $"api/work-assignment-review/reports/{P3ReportId()}/deactivate",
            deactivate,
            reviewerToken,
            ct: ct);
        await Task.WhenAll(recallTask, deactivateTask);
        var responses = new[] { await recallTask, await deactivateTask };
        var winnerIndex = Array.FindIndex(responses, x => x.StatusCode == HttpStatusCode.OK);
        var loserIndex = Array.FindIndex(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        HarnessAssert.True(winnerIndex >= 0 && loserIndex >= 0 && winnerIndex != loserIndex, "P3 recall-vs-deactivate race did not produce one winner and one conflict");
        AssertErrorCode(responses[loserIndex], "WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT", "P3 recall-vs-deactivate loser");

        var persisted = await LoadP3ReportDocumentAsync(ct);
        HarnessAssert.Equal(raceRevision + 1, persisted.LifecycleRevision, "P3 recall-vs-deactivate lifecycle revision mismatch");
        if (winnerIndex == 0)
        {
            HarnessAssert.Equal(WorkAssignmentReportStatus.Submitted, persisted.Status, "P3 recall race winner status mismatch");
            HarnessAssert.True(persisted.IsActive, "P3 recall race winner unexpectedly deactivated report");
            await GetP3ReportAsync(HarnessAssert.Required(_assigneeToken, "P3 assignee token"), ct);
            var reapprove = BuildP3ApproveRequest("p3-race-reapprove-for-deactivate", "P3 reapprove for deactivate fixture");
            var reapproved = await _api.PostAsync(
                $"api/work-assignment-review/reports/{P3ReportId()}/approve",
                reapprove,
                reviewerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(reapproved, HttpStatusCode.OK, "P3 reapprove after recall race winner");
            await GetP3ReportAsync(HarnessAssert.Required(_assigneeToken, "P3 assignee token"), ct);
            var forceDeactivate = BuildP3ReviewLifecycleRequest("p3-race-force-deactivate", "P3 force inactive fixture");
            var deactivated = await _api.PostAsync(
                $"api/work-assignment-review/reports/{P3ReportId()}/deactivate",
                forceDeactivate,
                reviewerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(deactivated, HttpStatusCode.OK, "P3 force deactivate after recall race winner");
        }
        else
        {
            await GetP3ReportAsync(HarnessAssert.Required(_assigneeToken, "P3 assignee token"), ct);
            var reactivate = BuildP3ReviewLifecycleRequest("p3-race-reactivate-after-deactivate", "P3 reactivate symmetric fixture");
            var reactivated = await _api.PostAsync(
                $"api/work-assignment-review/reports/{P3ReportId()}/reactivate",
                reactivate,
                reviewerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(reactivated, HttpStatusCode.OK, "P3 reactivate after deactivate race winner");
            await GetP3ReportAsync(HarnessAssert.Required(_assigneeToken, "P3 assignee token"), ct);
            var deactivateAgain = BuildP3ReviewLifecycleRequest("p3-race-deactivate-after-reactivate", "P3 restore inactive symmetric fixture");
            var deactivatedAgain = await _api.PostAsync(
                $"api/work-assignment-review/reports/{P3ReportId()}/deactivate",
                deactivateAgain,
                reviewerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(deactivatedAgain, HttpStatusCode.OK, "P3 deactivate after symmetric reactivation");
        }

        persisted = await LoadP3ReportDocumentAsync(ct);
        HarnessAssert.Equal(WorkAssignmentReportStatus.Approved, persisted.Status, "P3 recall/deactivate fixture did not end Approved");
        HarnessAssert.True(!persisted.IsActive, "P3 recall/deactivate fixture did not end inactive");
        ApplyP3ReportStateFromDocument(persisted);
        return new CaseObservation(
            "recall-approved and deactivate raced on one lifecycle token; one committed, one conflicted, and the fixture converged to inactive Approved for reactivation",
            "recallVsDeactivate=2;success=1;conflict=1;raceWinnerOneOf=recall|deactivate;raceLifecycleDelta=1;final=Approved/inactive");
    }

    private async Task<CaseObservation> VerifyP3ConcurrentReactivateAsync(CancellationToken ct)
    {
        var before = await LoadP3ReportDocumentAsync(ct);
        HarnessAssert.Equal(WorkAssignmentReportStatus.Approved, before.Status, "P3 reactivate race requires Approved state");
        HarnessAssert.True(!before.IsActive, "P3 reactivate race requires inactive report");
        ApplyP3ReportStateFromDocument(before);
        var reviewerToken = HarnessAssert.Required(_reviewerToken, "P3 reviewer token");
        var reactivateA = BuildP3ReviewLifecycleRequest("p3-race-reactivate-a", "P3 reactivate A");
        var reactivateB = BuildP3ReviewLifecycleRequest("p3-race-reactivate-b", "P3 reactivate B");
        var taskA = _api.PostAsync(
            $"api/work-assignment-review/reports/{P3ReportId()}/reactivate",
            reactivateA,
            reviewerToken,
            ct: ct);
        var taskB = _api.PostAsync(
            $"api/work-assignment-review/reports/{P3ReportId()}/reactivate",
            reactivateB,
            reviewerToken,
            ct: ct);
        await Task.WhenAll(taskA, taskB);
        var responses = new[] { await taskA, await taskB };
        var winnerIndex = Array.FindIndex(responses, x => x.StatusCode == HttpStatusCode.OK);
        var loserIndex = Array.FindIndex(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        HarnessAssert.True(winnerIndex >= 0 && loserIndex >= 0 && winnerIndex != loserIndex, "P3 concurrent reactivate did not produce one winner and one conflict");
        AssertErrorCode(responses[loserIndex], "WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT", "P3 concurrent reactivate loser");

        var reactivated = await LoadP3ReportDocumentAsync(ct);
        HarnessAssert.True(reactivated.IsActive, "P3 concurrent reactivate did not activate report");
        HarnessAssert.Equal(WorkAssignmentReportStatus.Approved, reactivated.Status, "P3 concurrent reactivate changed status");
        HarnessAssert.Equal(before.LifecycleRevision + 1, reactivated.LifecycleRevision, "P3 concurrent reactivate advanced lifecycle more than once");
        ApplyP3ReportStateFromDocument(reactivated);

        var recall = BuildP3ReviewLifecycleRequest("p3-race-reactivate-restore-recall", "P3 restore submitted after reactivate race");
        var recalled = await _api.PostAsync(
            $"api/work-assignment-review/reports/{P3ReportId()}/recall-approved",
            recall,
            reviewerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(recalled, HttpStatusCode.OK, "P3 recall after reactivate race");
        await GetP3ReportAsync(HarnessAssert.Required(_assigneeToken, "P3 assignee token"), ct);
        var withdraw = BuildP3LifecycleRequest("p3-race-reactivate-restore-withdraw", "P3 restore draft after reactivate race");
        var withdrawn = await _api.PostAsync(
            $"api/work-assignment-reports/{P3ReportId()}/withdraw-submitted",
            withdraw,
            HarnessAssert.Required(_assigneeToken, "P3 assignee token"),
            ct: ct);
        ApiHarnessClient.ExpectStatus(withdrawn, HttpStatusCode.OK, "P3 withdraw after reactivate race");
        ApplyP3ReportState(withdrawn.Json);
        HarnessAssert.Equal((int)WorkAssignmentReportStatus.Draft, _p3ReportStatus, "P3 reactivate race did not restore Draft state");
        return new CaseObservation(
            "two reactivations on the same inactive lifecycle token produced one winner/one conflict and one lifecycle increment",
            "reactivate=2;success=1;conflict=1;lifecycleDelta=1;statusPreserved=Approved;active=true;restored=Draft");
    }

    private async Task<CaseObservation> VerifyP3MappingVsSaveRaceAsync(CancellationToken ct)
    {
        HarnessAssert.Equal((int)WorkAssignmentReportStatus.Draft, _p3ReportStatus, "P3 mapping-vs-save race requires Draft state");
        var flowTemplateId = ObjectId.GenerateNewId().ToString();
        var flowVersionId = ObjectId.GenerateNewId().ToString();
        var mappingValue = 303;
        var saveValue = 404;
        var payloadJson = BuildP3ConstantMappingPayload(mappingValue).ToJsonString();
        var fixedAt = new DateTime(2026, 7, 22, 6, 0, 0, DateTimeKind.Utc);
        await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions").InsertOneAsync(
            new DynamicFlowTemplateVersion
            {
                Id = flowVersionId,
                TemplateId = flowTemplateId,
                RootDynamicFormTemplateId = _p3FormId,
                VersionNo = 1,
                Status = DynamicFlowTemplateVersionStatuses.Locked,
                DraftRevision = 1,
                PayloadJson = payloadJson,
                PayloadHash = ComputeSha256(payloadJson),
                SchemaVersion = 2,
                CatalogVersion = null,
                CatalogSemanticHash = null,
                DefinitionLockable = false,
                ExecutionEligibility = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase,
                ExecutionBlockedReason = DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented,
                BlockedUntilPhase = "P5",
                MigrationState = DynamicFlowDefinitionMigrationStates.RequiresReview,
                LockedAtUtc = fixedAt,
                LockedByUserId = _reviewerId,
                CreatedByUserId = _reviewerId,
                UpdatedByUserId = _reviewerId,
                CreatedAtUtc = fixedAt,
                UpdatedAtUtc = fixedAt,
                IsDeleted = false
            },
            cancellationToken: ct);

        var assignments = _database.GetCollection<WorkAssignment>("work_assignments");
        await assignments.UpdateOneAsync(
            x => x.Id == _p3AssignmentId && !x.IsDeleted,
            Builders<WorkAssignment>.Update
                .Set(x => x.FlowTemplateId, flowTemplateId)
                .Set(x => x.FlowTemplateVersionNo, 1),
            cancellationToken: ct);

        try
        {
            var expectedRevision = _p3PayloadRevision;
            var assigneeToken = HarnessAssert.Required(_assigneeToken, "P3 assignee token");
            var p7MappingActive =
                tdtd_be.Services.DynamicFlows.DynamicFlowP7CatalogCandidate
                    .ActivationEnabled;

            void AssertLegacyApplyBlocked(
                ApiHarnessResponse response,
                string operation)
            {
                if (p7MappingActive)
                {
                    ApiHarnessClient.ExpectStatus(
                        response,
                        HttpStatusCode.BadRequest,
                        operation);
                    AssertErrorCode(
                        response,
                        "COMMON_VALIDATION_FAILED",
                        operation);
                    HarnessAssert.Equal(
                        "DYNAMIC_FLOW_MAPPING_CONFIG_MUST_BE_FLOW_OWNED",
                        ApiHarnessClient.FindStringRecursive(
                            response.Json,
                            "reason"),
                        $"{operation} reason mismatch");
                    HarnessAssert.Equal(
                        "sourceReportIds",
                        ApiHarnessClient.FindStringRecursive(
                            response.Json,
                            "field"),
                        $"{operation} field mismatch");
                    return;
                }

                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.Conflict,
                    operation);
                AssertErrorCode(
                    response,
                    "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                    operation);
                HarnessAssert.Equal(
                    "DYNAMIC_FLOW_MAPPING_APPLY_BLOCKED_UNTIL_P7_08",
                    ApiHarnessClient.FindStringRecursive(
                        response.Json,
                        "reason"),
                    $"{operation} reason mismatch");
                HarnessAssert.Equal(
                    "mappingSlice",
                    ApiHarnessClient.FindStringRecursive(
                        response.Json,
                        "field"),
                    $"{operation} field mismatch");
            }

            var legacyMapping = new JsonObject
            {
                ["expectedPayloadRevision"] = expectedRevision,
                ["commandId"] = "p3-legacy-requires-review-mapping",
                ["sourceReportIds"] = new JsonArray(),
                ["requireSourceReport"] = false
            };
            var beforeLegacyMapping = await CaptureP3CoreWriteSnapshotAsync(ct);
            var legacyPreview = await _api.PostAsync(
                $"api/work-assignment-reports/{P3ReportId()}/draft/preview-dynamic-flow-mapping",
                legacyMapping.DeepClone(),
                assigneeToken,
                ct: ct);
            var legacyApply = await _api.PostAsync(
                $"api/work-assignment-reports/{P3ReportId()}/draft/apply-dynamic-flow-mapping",
                legacyMapping.DeepClone(),
                assigneeToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                legacyPreview,
                HttpStatusCode.BadRequest,
                "P4 legacy preview flow-owned mapping validation");
            AssertErrorCode(
                legacyPreview,
                "COMMON_VALIDATION_FAILED",
                "P4 legacy preview flow-owned mapping validation");
            HarnessAssert.Equal(
                "DYNAMIC_FLOW_MAPPING_CONFIG_MUST_BE_FLOW_OWNED",
                ApiHarnessClient.FindStringRecursive(
                    legacyPreview.Json,
                    "reason"),
                "P4 legacy preview flow-owned mapping reason mismatch");
            HarnessAssert.Equal(
                "sourceReportIds",
                ApiHarnessClient.FindStringRecursive(
                    legacyPreview.Json,
                    "field"),
                "P4 legacy preview flow-owned mapping field mismatch");
            AssertLegacyApplyBlocked(
                legacyApply,
                "P4 legacy apply mapping guard");
            HarnessAssert.Equal(
                beforeLegacyMapping,
                await CaptureP3CoreWriteSnapshotAsync(ct),
                "P4 legacy mapping preview/apply changed report persistence");

            await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
                .UpdateOneAsync(
                    x => x.Id == flowVersionId && x.TemplateId == flowTemplateId,
                    Builders<DynamicFlowTemplateVersion>.Update
                        .Set(x => x.CatalogVersion, "1.1")
                        .Set(x => x.DefinitionLockable, true)
                        .Set(x => x.MigrationState, DynamicFlowDefinitionMigrationStates.Canonical),
                    cancellationToken: ct);

            var saveValues = BuildP3ValidFieldValues();
            var numberFieldId = ApiHarnessClient.RequiredString(P3FindFieldByType("number"), "id");
            saveValues[numberFieldId] = saveValue;
            var save = BuildP3DraftRequest(saveValues, _p3LatestTableValuesJson, expectedRevision, "p3-race-save-vs-mapping-save");
            var mapping = new JsonObject
            {
                ["expectedPayloadRevision"] = expectedRevision,
                ["commandId"] = "p3-race-save-vs-mapping-map",
                ["sourceReportIds"] = new JsonArray(),
                ["requireSourceReport"] = false
            };
            var saveTask = _api.PutAsync($"api/work-assignment-reports/{P3ReportId()}/draft", save, assigneeToken, ct: ct);
            var mappingTask = _api.PostAsync($"api/work-assignment-reports/{P3ReportId()}/draft/apply-dynamic-flow-mapping", mapping, assigneeToken, ct: ct);
            await Task.WhenAll(saveTask, mappingTask);
            var saveResponse = await saveTask;
            var mappingResponse = await mappingTask;
            ApiHarnessClient.ExpectStatus(saveResponse, HttpStatusCode.OK, "P4 manual save beside blocked mapping");
            AssertLegacyApplyBlocked(
                mappingResponse,
                "P4 concurrent apply mapping guard");
            ApplyP3ReportState(saveResponse.Json);
            HarnessAssert.Equal(expectedRevision + 1, _p3PayloadRevision, "P4 blocked mapping changed manual-save revision");
            var reopened = await GetP3ReportAsync(assigneeToken, ct);
            var (_, values) = ParseP3FieldValues(reopened.Json);
            HarnessAssert.Equal(saveValue, values[numberFieldId]?.GetValue<int>() ?? -1, "P4 blocked mapping changed manual-save value");
            var mappingGuardEvidence = p7MappingActive
                ? "400/COMMON_VALIDATION_FAILED/" +
                    "DYNAMIC_FLOW_MAPPING_CONFIG_MUST_BE_FLOW_OWNED/" +
                    "sourceReportIds"
                : "409/DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE/" +
                    "DYNAMIC_FLOW_MAPPING_APPLY_BLOCKED_UNTIL_P7_08/" +
                    "mappingSlice";
            return new CaseObservation(
                "legacy REQUIRES_REVIEW preview rejected caller-owned " +
                    "sourceReportIds with the exact flow-owned validation " +
                    "contract; legacy and concurrent apply requests failed " +
                    "closed through the catalog-appropriate mapping guard " +
                    "while the manual save committed exactly once",
                $"legacyPreview=400/COMMON_VALIDATION_FAILED/" +
                    $"DYNAMIC_FLOW_MAPPING_CONFIG_MUST_BE_FLOW_OWNED/" +
                    $"sourceReportIds/0W;legacyApply={mappingGuardEvidence}/0W;" +
                    $"concurrentApply={mappingGuardEvidence};manualSave=200;" +
                    $"payloadDelta=1;manualValueVerified=true");
        }
        finally
        {
            await assignments.UpdateOneAsync(
                x => x.Id == _p3AssignmentId && !x.IsDeleted,
                Builders<WorkAssignment>.Update
                    .Set(x => x.FlowTemplateId, (string?)null)
                    .Set(x => x.FlowTemplateVersionNo, (int?)null),
                cancellationToken: ct);
        }
    }

    private async Task<CaseObservation> VerifyP3StaleSectionRepairAsync(CancellationToken ct)
    {
        var report = await GetP3ReportAsync(HarnessAssert.Required(_assigneeToken, "P3 assignee token"), ct);
        var payloadHash = ApiHarnessClient.RequiredString(report.Json, "payloadHash");
        var sections = _database.GetCollection<WorkAssignmentReportSection>("work_assignment_report_sections");
        var stale = await sections
            .Find(x => x.WorkAssignmentReportId == P3ReportId() && x.SectionId == "main" && !x.IsDeleted)
            .SingleAsync(ct);
        await sections.UpdateOneAsync(
            x => x.Id == stale.Id && !x.IsDeleted,
            Builders<WorkAssignmentReportSection>.Update
                .Set(x => x.SourcePayloadRevision, _p3PayloadRevision)
                .Set(x => x.SourcePayloadHash, payloadHash)
                .Set(x => x.SourceLifecycleRevision, _p3LifecycleRevision)
                .Set(x => x.PayloadHash, "p3-corrupt-section-content-hash")
                .Set(x => x.FieldValuesJson, "{\"values\":{\"p3_corrupt\":true}}")
                .Set(x => x.TableValuesJson, "{\"blocks\":[]}"),
            cancellationToken: ct);

        var summariesResponse = await _api.GetAsync(
            $"api/work-assignment-reports/{P3ReportId()}/sections",
            HarnessAssert.Required(_assigneeToken, "P3 assignee token"),
            ct: ct);
        ApiHarnessClient.ExpectStatus(summariesResponse, HttpStatusCode.OK, "P3 stale section repair summaries");
        var summary = ApiHarnessClient.RequiredArray(summariesResponse.Json, "P3 repaired section summaries")
            .OfType<JsonObject>()
            .Single(x => string.Equals(x["sectionId"]?.GetValue<string>(), "main", StringComparison.Ordinal));
        HarnessAssert.Equal(_p3PayloadRevision, ApiHarnessClient.RequiredInt(summary, "sourcePayloadRevision"), "P3 repaired summary payload revision mismatch");
        HarnessAssert.Equal(payloadHash, ApiHarnessClient.RequiredString(summary, "sourcePayloadHash"), "P3 repaired summary payload hash mismatch");
        HarnessAssert.Equal(_p3LifecycleRevision, ApiHarnessClient.RequiredInt(summary, "sourceLifecycleRevision"), "P3 repaired summary lifecycle revision mismatch");

        var repaired = await sections.Find(x => x.Id == stale.Id && !x.IsDeleted).SingleAsync(ct);
        HarnessAssert.Equal(_p3PayloadRevision, repaired.SourcePayloadRevision, "P3 repaired Mongo section payload revision mismatch");
        HarnessAssert.Equal(payloadHash, repaired.SourcePayloadHash, "P3 repaired Mongo section payload hash mismatch");
        HarnessAssert.Equal(_p3LifecycleRevision, repaired.SourceLifecycleRevision, "P3 repaired Mongo section lifecycle revision mismatch");
        HarnessAssert.True(!string.Equals("p3-corrupt-section-content-hash", repaired.PayloadHash, StringComparison.Ordinal), "P3 stale section repair retained corrupt section payload hash");
        HarnessAssert.True(!(repaired.FieldValuesJson ?? string.Empty).Contains("p3_corrupt", StringComparison.Ordinal), "P3 stale section repair retained corrupt field payload");
        HarnessAssert.Equal(2L, await sections.CountDocumentsAsync(x => x.WorkAssignmentReportId == P3ReportId() && !x.IsDeleted, cancellationToken: ct), "P3 stale section repair changed section cardinality");

        var detail = await _api.GetAsync(
            $"api/work-assignment-reports/{P3ReportId()}/sections/main",
            HarnessAssert.Required(_assigneeToken, "P3 assignee token"),
            ct: ct);
        ApiHarnessClient.ExpectStatus(detail, HttpStatusCode.OK, "P3 repaired section detail");
        HarnessAssert.Equal(_p3PayloadRevision, ApiHarnessClient.RequiredInt(detail.Json, "sourcePayloadRevision"), "P3 repaired detail payload revision mismatch");
        HarnessAssert.Equal(payloadHash, ApiHarnessClient.RequiredString(detail.Json, "sourcePayloadHash"), "P3 repaired detail payload hash mismatch");
        return new CaseObservation(
            "a section whose source revision/hash/lifecycle markers still looked current but whose field/table body and section hash were corrupt was detected and rebuilt from the canonical external payload without duplicates",
            "sourceMarkersKeptCurrent=true;bodyCorruptInjected=true;sectionHashCorruptInjected=true;summaryRepair=200;detail=200;mongoRevisionExact=true;mongoHashExact=true;lifecycleExact=true;corruptRemoved=true;sections=2");
    }

    private async Task<CaseObservation> VerifyP3ImmutableVersionBindingAsync(CancellationToken ct)
    {
        var reviewerToken = HarnessAssert.Required(_reviewerToken, "P3 reviewer token");
        var formId = HarnessAssert.Required(_p3FormId, "P3 runtime form");
        var before = await _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
            .Find(x => x.Id == formId && !x.IsDeleted)
            .SingleAsync(ct);
        var next = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/versions",
            new { expectedRevision = before.Revision, name = "P3 runtime successor" },
            reviewerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(next, HttpStatusCode.OK, "P3 create successor version");
        var v2Id = ApiHarnessClient.RequiredString(next.Json, "id");
        HarnessAssert.Equal(2, ApiHarnessClient.RequiredInt(next.Json, "versionNo"), "P3 successor version number mismatch");
        var publish = await _api.PostAsync(
            $"api/dynamic-forms/{v2Id}/publish",
            new { expectedRevision = ApiHarnessClient.RequiredInt(next.Json, "revision") },
            reviewerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(publish, HttpStatusCode.OK, "P3 publish successor version");

        var report = await GetP3ReportAsync(HarnessAssert.Required(_assigneeToken, "P3 assignee token"), ct);
        HarnessAssert.Equal(formId, ApiHarnessClient.RequiredString(report.Json, "dynamicFormTemplateId"), "P3 report silently switched template version");
        HarnessAssert.Equal(formId, ApiHarnessClient.RequiredString(report.Json, "dynamicFormFamilyId"), "P3 report family changed after v2 publish");
        HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(report.Json, "dynamicFormVersionNo"), "P3 report silently switched version number");
        HarnessAssert.Equal(_p3SchemaHash, ApiHarnessClient.RequiredString(report.Json, "dynamicFormSchemaHash"), "P3 report silently switched schema hash");
        var persisted = await _database.GetCollection<WorkAssignmentReport>("work_assignment_report")
            .Find(x => x.Id == P3ReportId() && !x.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.Equal(formId, persisted.DynamicFormTemplateId, "P3 Mongo report template changed after v2 publish");
        HarnessAssert.Equal(1, persisted.DynamicFormVersionNo, "P3 Mongo report version changed after v2 publish");
        HarnessAssert.Equal(_p3SchemaHash, persisted.DynamicFormSchemaHash, "P3 Mongo report hash changed after v2 publish");
        return new CaseObservation(
            "published a newer family version, then API and Mongo report reads remained bound to the original published v1 ID/version/hash",
            "v2Published=true;reportTemplate=v1;reportVersion=1;reportHash=v1;apiExact=true;mongoExact=true");
    }

    private async Task<CaseObservation> VerifyP3SectionProjectionAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_assigneeToken, "P3 assignee token");
        var report = await GetP3ReportAsync(token, ct);
        var reportHash = ApiHarnessClient.RequiredString(report.Json, "payloadHash");
        var summariesResponse = await _api.GetAsync($"api/work-assignment-reports/{P3ReportId()}/sections", token, ct: ct);
        ApiHarnessClient.ExpectStatus(summariesResponse, HttpStatusCode.OK, "P3 section summaries");
        var summaries = ApiHarnessClient.RequiredArray(summariesResponse.Json, "P3 section summaries");
        HarnessAssert.Equal(2, summaries.Count, "P3 section projection count mismatch");
        foreach (var summaryNode in summaries)
        {
            var summary = ApiHarnessClient.RequiredObject(summaryNode, "P3 section summary");
            HarnessAssert.Equal(_p3PayloadRevision, ApiHarnessClient.RequiredInt(summary, "sourcePayloadRevision"), "P3 section API source revision mismatch");
            HarnessAssert.Equal(reportHash, ApiHarnessClient.RequiredString(summary, "sourcePayloadHash"), "P3 section API source hash mismatch");
            var sectionId = ApiHarnessClient.RequiredString(summary, "sectionId");
            var detail = await _api.GetAsync($"api/work-assignment-reports/{P3ReportId()}/sections/{sectionId}", token, ct: ct);
            ApiHarnessClient.ExpectStatus(detail, HttpStatusCode.OK, $"P3 section detail {sectionId}");
            HarnessAssert.Equal(_p3PayloadRevision, ApiHarnessClient.RequiredInt(detail.Json, "sourcePayloadRevision"), $"P3 section detail {sectionId} revision mismatch");
            HarnessAssert.Equal(reportHash, ApiHarnessClient.RequiredString(detail.Json, "sourcePayloadHash"), $"P3 section detail {sectionId} hash mismatch");
        }

        var sections = await _database.GetCollection<WorkAssignmentReportSection>("work_assignment_report_sections")
            .Find(x => x.WorkAssignmentReportId == P3ReportId() && !x.IsDeleted)
            .SortBy(x => x.SectionOrder)
            .ToListAsync(ct);
        HarnessAssert.Equal(2, sections.Count, "P3 Mongo section count mismatch");
        foreach (var section in sections)
        {
            HarnessAssert.Equal(_p3PayloadRevision, section.SourcePayloadRevision, $"P3 Mongo section {section.SectionId} source revision mismatch");
            HarnessAssert.Equal(reportHash, section.SourcePayloadHash, $"P3 Mongo section {section.SectionId} source hash mismatch");
            HarnessAssert.Equal(_p3FormId, section.DynamicFormTemplateId, $"P3 Mongo section {section.SectionId} template mismatch");
            HarnessAssert.Equal(1, section.DynamicFormVersionNo, $"P3 Mongo section {section.SectionId} version mismatch");
            HarnessAssert.Equal(_p3SchemaHash, section.DynamicFormSchemaHash, $"P3 Mongo section {section.SectionId} schema hash mismatch");
            HarnessAssert.Equal((WorkAssignmentReportStatus)_p3ReportStatus, section.Status, $"P3 Mongo section {section.SectionId} lifecycle status mismatch");
            HarnessAssert.True(section.SourceReportUpdatedAtUtc.HasValue, $"P3 Mongo section {section.SectionId} source report timestamp missing");
        }

        var payload = await _database.GetCollection<WorkReportPayload>("work_report_payloads")
            .Find(x => x.ReportId == P3ReportId() && x.Status == WorkReportPayloadStatus.Ready)
            .SingleAsync(ct);
        HarnessAssert.Equal(_p3PayloadRevision, payload.PayloadRevision, "P3 canonical payload revision mismatch");
        HarnessAssert.Equal(reportHash, payload.PayloadHash, "P3 canonical payload hash mismatch");
        await EvidenceJson.WriteAsync(
            Path.Combine(_iterationRoot, "p3-runtime-reconciliation.json"),
            new
            {
                reportId = P3ReportId(),
                form = new { id = _p3FormId, familyId = _p3FormId, versionNo = 1, schemaHash = _p3SchemaHash },
                report = new { payloadRevision = _p3PayloadRevision, lifecycleRevision = _p3LifecycleRevision, status = _p3ReportStatus, payloadHash = reportHash },
                canonicalPayload = new { payload.PayloadRevision, payload.PayloadHash, payload.Status, payload.PayloadSizeBytes },
                sections = sections.Select(x => new
                {
                    x.SectionId,
                    x.SectionOrder,
                    x.FieldCount,
                    x.BlockCount,
                    x.SourcePayloadRevision,
                    x.SourcePayloadHash,
                    x.SourceReportUpdatedAtUtc,
                    status = (int)x.Status,
                    x.DynamicFormTemplateId,
                    x.DynamicFormVersionNo,
                    x.DynamicFormSchemaHash
                })
            },
            ct);
        return new CaseObservation(
            "section summary/detail APIs and direct Mongo matched the one canonical payload revision/hash, immutable v1 identity, lifecycle status, and two-section projection",
            "sections=2;apiSummaryExact=true;apiDetailExact=true;mongoRevisionExact=true;mongoHashExact=true;versionExact=true;statusExact=true;artifact=p3-runtime-reconciliation.json");
    }

    private async Task<CaseObservation> VerifyP3SecretRedactionAsync(CancellationToken ct)
    {
        var serializedExchanges = JsonSerializer.Serialize(_api.Exchanges);
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bootstrapKey"] = _backend.BootstrapKey,
            ["actorPassword"] = _backend.ActorPassword,
            ["adminPassword"] = HarnessAssert.Required(_adminPassword, "P3 admin password"),
            ["adminToken"] = HarnessAssert.Required(_adminToken, "P3 admin token"),
            ["ownerToken"] = HarnessAssert.Required(_ownerToken, "P3 owner token"),
            ["outsiderToken"] = HarnessAssert.Required(_outsiderToken, "P3 outsider token"),
            ["assigneeToken"] = HarnessAssert.Required(_assigneeToken, "P3 assignee token"),
            ["reviewerToken"] = HarnessAssert.Required(_reviewerToken, "P3 reviewer token")
        };

        foreach (var secret in secrets)
        {
            HarnessAssert.True(
                !serializedExchanges.Contains(secret.Value, StringComparison.Ordinal),
                $"P3 redacted API exchange evidence leaked {secret.Key}");
        }

        HarnessAssert.True(
            serializedExchanges.Contains("redacted", StringComparison.OrdinalIgnoreCase),
            "P3 API exchange evidence did not contain any explicit redaction marker");
        await EvidenceJson.WriteAsync(
            Path.Combine(_iterationRoot, "p3-secret-redaction-audit.json"),
            new
            {
                exchangeCount = _api.Exchanges.Count,
                secretKindsChecked = secrets.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                rawSecretMatches = 0,
                redactionMarkerPresent = true
            },
            ct);
        return new CaseObservation(
            "serialized request/response evidence was scanned against every live harness password, token, bootstrap key, and secret marker with zero raw matches",
            $"exchanges={_api.Exchanges.Count};secretKinds={secrets.Count};rawMatches=0;redactionMarker=true;artifact=p3-secret-redaction-audit.json");
    }

    private JsonObject BuildP3RuntimeFormRequest()
    {
        var request = BuildFormRequest(P3FormCode, "P3 Dynamic Form runtime", updated: false);
        request["description"] = "P3 all-field/source/table runtime fixture";
        var schema = ApiHarnessClient.RequiredObject(request["schema"], "P3 runtime schema");
        var sections = ApiHarnessClient.RequiredArray(schema["sections"], "P3 runtime sections");
        sections.Add(new JsonObject { ["id"] = "details", ["title"] = "Details section", ["order"] = 2 });
        var fields = ApiHarnessClient.RequiredArray(schema["fields"], "P3 runtime fields");
        foreach (var field in fields.OfType<JsonObject>())
        {
            if (field["valueSource"] is JsonObject source &&
                string.Equals(source["sourceType"]?.GetValue<string>(), "FIXED_ENUM", StringComparison.Ordinal) &&
                source["options"] is JsonArray fixedOptions)
            {
                // The runtime contract resolves FIXED_ENUM codes from the canonical field option list.
                // Keep the source descriptor too so source provenance remains independently testable.
                field["options"] = fixedOptions.DeepClone();
            }
        }
        for (var index = fields.Count / 2; index < fields.Count; index++)
            ApiHarnessClient.RequiredObject(fields[index], $"P3 runtime field {index}")["sectionId"] = "details";
        var blocks = ApiHarnessClient.RequiredArray(schema["blocks"], "P3 runtime blocks");
        blocks.Add(BuildP3SchemaBlock("p3_fixed", "FIXED_GRID", "main"));
        blocks.Add(BuildP3SchemaBlock("p3_append_rows", "APPEND_ROWS", "main"));
        blocks.Add(BuildP3SchemaBlock("p3_append_columns", "APPEND_COLUMNS", "details"));
        blocks.Add(BuildP3SchemaBlock("p3_matrix", "MATRIX", "details"));
        var summary = BuildP3SchemaBlock("p3_summary", "SUMMARY_TEMPLATE", "details");
        summary["sourceBlockId"] = "p3_matrix";
        summary["groupBy"] = new JsonArray();
        summary["rowLayout"] = new JsonArray
        {
            new JsonObject
            {
                ["rowsPerUnit"] = 1,
                ["metrics"] = new JsonArray("metric_1")
            }
        };
        blocks.Add(summary);
        return request;
    }

    private static JsonObject BuildP3SchemaBlock(string blockId, string tableMode, string sectionId)
    {
        var width = tableMode == "APPEND_COLUMNS" ? 2 : 1;
        var height = tableMode == "APPEND_ROWS" ? 2 : 1;
        var indexMap = new JsonArray
        {
            new JsonObject
            {
                ["index"] = 0,
                ["rowKey"] = "row_1",
                ["columnKey"] = "col_1",
                ["metricKey"] = "metric_1"
            }
        };
        if (tableMode == "APPEND_ROWS")
        {
            indexMap.Add(new JsonObject
            {
                ["index"] = 1,
                ["rowKey"] = "row_2",
                ["columnKey"] = "col_1",
                ["metricKey"] = "metric_2"
            });
        }
        else if (tableMode == "APPEND_COLUMNS")
        {
            indexMap.Add(new JsonObject
            {
                ["index"] = 1,
                ["rowKey"] = "row_1",
                ["columnKey"] = "col_2",
                ["metricKey"] = "metric_2"
            });
        }

        return new JsonObject
        {
            ["blockId"] = blockId,
            ["sectionId"] = sectionId,
            ["tableMode"] = tableMode,
            ["dataRect"] = new JsonObject
            {
                ["r0"] = 0,
                ["c0"] = 0,
                ["r1"] = height - 1,
                ["c1"] = width - 1
            },
            ["w"] = width,
            ["h"] = height,
            ["defaultDataType"] = "NUMBER",
            ["indexMap"] = indexMap
        };
    }

    private static JsonObject BuildP3BlockCountFormRequest(int blockCount)
    {
        var request = BuildMinimalFormRequest($"P3_DF_BLOCK_COUNT_{blockCount:00}", $"P3 block count {blockCount:00}");
        var schema = ApiHarnessClient.RequiredObject(request["schema"], $"P3 block count {blockCount} schema");
        var blocks = ApiHarnessClient.RequiredArray(schema["blocks"], $"P3 block count {blockCount} blocks");
        for (var index = 0; index < blockCount; index++)
            blocks.Add(BuildP3SchemaBlock($"p3_count_{blockCount:00}_{index + 1:00}", "FIXED_GRID", "main"));
        return request;
    }

    private JsonArray P3PublishedFields()
        => JsonNode.Parse(HarnessAssert.Required(_p3FormFieldsJson, "P3 published fields JSON")) as JsonArray
           ?? throw new InvalidOperationException("P3 published fields JSON must be an array");

    private JsonObject P3FindFieldByType(string fieldType)
        => P3PublishedFields().OfType<JsonObject>()
               .FirstOrDefault(x => string.Equals(x["type"]?.GetValue<string>(), fieldType, StringComparison.Ordinal))
           ?? throw new InvalidOperationException($"P3 runtime form has no field type {fieldType}");

    private JsonObject P3FindFieldBySource(string sourceType)
        => P3PublishedFields().OfType<JsonObject>()
               .FirstOrDefault(x =>
                   x["valueSource"] is JsonObject source &&
                   string.Equals(source["sourceType"]?.GetValue<string>(), sourceType, StringComparison.Ordinal))
           ?? throw new InvalidOperationException($"P3 runtime form has no value source {sourceType}");

    private JsonObject BuildP3ValidFieldValues()
    {
        var values = new JsonObject();
        foreach (var field in P3PublishedFields().OfType<JsonObject>())
        {
            var id = ApiHarnessClient.RequiredString(field, "id");
            var type = ApiHarnessClient.RequiredString(field, "type");
            values[id] = type switch
            {
                "shortText" => JsonValue.Create(P3ChoiceCode(field)),
                "longText" => JsonValue.Create("  P3 long text  "),
                "richText" => JsonValue.Create("<p onclick=\"evil()\">P3 rich text</p>"),
                "stringList" => new JsonArray("alpha", "beta"),
                "number" => JsonValue.Create(0),
                "date" => JsonValue.Create("07/2026"),
                "fullDate" => JsonValue.Create("29/02/2024"),
                "singleSelect" => JsonValue.Create(P3ChoiceCode(field)),
                "multiSelect" => new JsonArray(P3ChoiceCode(field)),
                "boolean" => JsonValue.Create(false),
                _ => throw new InvalidOperationException($"Unsupported P3 field type {type}")
            };
        }
        return values;
    }

    private string P3ChoiceCode(JsonObject field)
    {
        var source = field["valueSource"] as JsonObject;
        var sourceType = source?["sourceType"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(sourceType))
            return P3SelectedSourceCode(sourceType);
        var options = source?["options"] as JsonArray ?? field["options"] as JsonArray;
        var first = options?.OfType<JsonObject>().FirstOrDefault();
        return first is null ? "P3_VALUE" : ApiHarnessClient.RequiredString(first, "code");
    }

    private string P3SelectedSourceCode(string sourceType)
        => sourceType switch
        {
            "FIXED_ENUM" => "FIXED_A",
            "ENUM_CATALOG" => HarnessAssert.Required(_p3EnumCatalogOptionCode, "P3 runtime enum catalog option code"),
            "SYSTEM_UNIT" => _unitId,
            "SYSTEM_USER" => _assigneeId,
            "SYSTEM_POSITION" => P3PositionCode,
            "SYSTEM_UNIT_TYPE" => "P1_TEST_UNIT",
            _ => throw new InvalidOperationException($"Unsupported P3 source {sourceType}")
        };

    private static JsonNode? P3InvalidFieldValue(string fieldType)
        => fieldType switch
        {
            "shortText" => JsonValue.Create(1),
            "longText" or "richText" => new JsonArray("wrong"),
            "stringList" => JsonValue.Create("wrong"),
            "number" => JsonValue.Create("1"),
            "date" => JsonValue.Create(2026),
            "fullDate" => JsonValue.Create("31/02/2024"),
            "singleSelect" => new JsonArray("wrong"),
            "multiSelect" => JsonValue.Create("wrong"),
            "boolean" => JsonValue.Create(0),
            _ => throw new InvalidOperationException($"Unsupported P3 invalid type {fieldType}")
        };

    private async Task<ApiHarnessResponse> SaveP3DraftAsync(
        JsonObject values,
        string? tableValuesJson,
        string commandId,
        string token,
        HttpStatusCode expectedStatus,
        CancellationToken ct,
        bool updateState = true)
    {
        var request = BuildP3DraftRequest(values, tableValuesJson, _p3PayloadRevision, commandId);
        var response = await _api.PutAsync($"api/work-assignment-reports/{P3ReportId()}/draft", request, token, ct: ct);
        ApiHarnessClient.ExpectStatus(response, expectedStatus, $"P3 save draft {commandId}");
        if (updateState && expectedStatus == HttpStatusCode.OK)
            ApplyP3ReportState(response.Json);
        return response;
    }

    private static JsonObject BuildP3DraftRequest(
        JsonObject values,
        string? tableValuesJson,
        int expectedPayloadRevision,
        string commandId)
        => new()
        {
            ["expectedPayloadRevision"] = expectedPayloadRevision,
            ["commandId"] = commandId,
            ["values1D"] = new JsonArray(0),
            ["fieldValuesJson"] = new JsonObject { ["values"] = values.DeepClone() }.ToJsonString(),
            ["tableValuesJson"] = tableValuesJson,
            ["dataOrigin"] = "MANUAL_INPUT",
            ["cumulativeContributionMode"] = "INCLUDE"
        };

    private JsonObject BuildP3SubmitRequest(JsonObject values, string commandId)
        => new()
        {
            ["expectedPayloadRevision"] = _p3PayloadRevision,
            ["expectedLifecycleRevision"] = _p3LifecycleRevision,
            ["commandId"] = commandId,
            ["values1D"] = new JsonArray(0),
            ["fieldValuesJson"] = new JsonObject { ["values"] = values.DeepClone() }.ToJsonString(),
            ["tableValuesJson"] = _p3LatestTableValuesJson,
            ["dataOrigin"] = "MANUAL_INPUT",
            ["cumulativeContributionMode"] = "INCLUDE"
        };

    private JsonObject BuildP3LifecycleRequest(string commandId, string reason)
        => new()
        {
            ["expectedPayloadRevision"] = _p3PayloadRevision,
            ["expectedLifecycleRevision"] = _p3LifecycleRevision,
            ["commandId"] = commandId,
            ["returnReason"] = reason,
            ["reviewerComment"] = reason
        };

    private JsonObject BuildP3ReviewLifecycleRequest(string commandId, string comment)
        => new()
        {
            ["expectedPayloadRevision"] = _p3PayloadRevision,
            ["expectedLifecycleRevision"] = _p3LifecycleRevision,
            ["commandId"] = commandId,
            ["comment"] = comment
        };

    private JsonObject BuildP3ApproveRequest(string commandId, string comment)
    {
        var request = BuildP3ReviewLifecycleRequest(commandId, comment);
        request["confirmHistoricalDataApproval"] = false;
        return request;
    }

    private async Task P3SubmitCurrentDraftAsync(string commandId, CancellationToken ct)
    {
        var request = BuildP3SubmitRequest(BuildP3ValidFieldValues(), commandId);
        var response = await _api.PostAsync(
            $"api/work-assignment-reports/{P3ReportId()}/submit",
            request,
            HarnessAssert.Required(_assigneeToken, "P3 assignee token"),
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"P3 submit {commandId}");
        ApplyP3ReportState(response.Json);
        HarnessAssert.Equal((int)WorkAssignmentReportStatus.Submitted, _p3ReportStatus, $"P3 submit {commandId} status mismatch");
    }

    private async Task<ApiHarnessResponse> GetP3ReportAsync(string token, CancellationToken ct, bool updateState = true)
    {
        var response = await _api.GetAsync($"api/work-assignment-reports/{P3ReportId()}", token, ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, "P3 report reopen");
        if (updateState)
            ApplyP3ReportState(response.Json);
        return response;
    }

    private async Task<HttpStatusCode> AssertP3ReadonlyOrForbiddenAsync(
        string token,
        string actor,
        CancellationToken ct)
    {
        var response = await _api.GetAsync($"api/work-assignment-reports/{P3ReportId()}", token, ct: ct);
        HarnessAssert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Forbidden,
            $"P3 {actor} detail expected 200 read-only or 403, got {(int)response.StatusCode}");
        if (response.StatusCode == HttpStatusCode.OK)
        {
            AssertP3ReportCapabilities(
                response.Json,
                canEditPayload: false,
                canSubmit: false,
                canWithdraw: false,
                $"P3 {actor} capabilities");
        }

        return response.StatusCode;
    }

    private static void AssertP3ReportCapabilities(
        JsonNode? response,
        bool canEditPayload,
        bool canSubmit,
        bool canWithdraw,
        string context)
    {
        HarnessAssert.Equal(
            canEditPayload,
            ApiHarnessClient.RequiredBool(response, "canEditPayload"),
            $"{context} canEditPayload mismatch");
        HarnessAssert.Equal(
            canSubmit,
            ApiHarnessClient.RequiredBool(response, "canSubmit"),
            $"{context} canSubmit mismatch");
        HarnessAssert.Equal(
            canWithdraw,
            ApiHarnessClient.RequiredBool(response, "canWithdraw"),
            $"{context} canWithdraw mismatch");
    }

    private void ApplyP3ReportState(JsonNode? node)
    {
        _p3ReportId ??= ApiHarnessClient.RequiredString(node, "id");
        HarnessAssert.Equal(_p3ReportId, ApiHarnessClient.RequiredString(node, "id"), "P3 report response ID mismatch");
        _p3PayloadRevision = ApiHarnessClient.RequiredInt(node, "payloadRevision");
        _p3LifecycleRevision = ApiHarnessClient.RequiredInt(node, "lifecycleRevision");
        _p3ReportStatus = ApiHarnessClient.RequiredInt(node, "status");
        if (node is JsonObject obj)
        {
            _p3LatestFieldValuesJson = P3OptionalString(obj, "fieldValuesJson") ?? _p3LatestFieldValuesJson;
            _p3LatestTableValuesJson = P3OptionalString(obj, "tableValuesJson") ?? _p3LatestTableValuesJson;
        }
    }

    private static string? P3OptionalString(JsonObject obj, string property)
        => obj[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private string P3ReportId() => HarnessAssert.Required(_p3ReportId, "P3 runtime report");

    private static (JsonObject Root, JsonObject Values) ParseP3FieldValues(JsonNode? response)
    {
        var json = ApiHarnessClient.RequiredString(response, "fieldValuesJson");
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new InvalidOperationException("P3 fieldValuesJson must be an object");
        var values = root["values"] as JsonObject ?? root;
        return (root, values);
    }

    private static void AssertP3CanonicalFieldValue(JsonNode? response, JsonObject field, string fieldType)
    {
        var parsed = ParseP3FieldValues(response);
        var id = ApiHarnessClient.RequiredString(field, "id");
        var value = parsed.Values[id] ?? throw new InvalidOperationException($"P3 canonical field {id} is missing");
        switch (fieldType)
        {
            case "shortText":
            case "singleSelect":
                HarnessAssert.True(value is JsonValue text && text.TryGetValue<string>(out var code) && !string.IsNullOrWhiteSpace(code), $"P3 {fieldType} was not a scalar code");
                break;
            case "longText":
                HarnessAssert.Equal("  P3 long text  ", value.GetValue<string>(), "P3 longText scalar changed");
                break;
            case "richText":
                HarnessAssert.Equal("<p>P3 rich text</p>", value.GetValue<string>(), "P3 richText sanitizer mismatch");
                break;
            case "stringList":
                HarnessAssert.Equal(2, ApiHarnessClient.RequiredArray(value, "P3 stringList").Count, "P3 stringList count mismatch");
                break;
            case "number":
                HarnessAssert.Equal(0, value.GetValue<int>(), "P3 numeric zero changed");
                break;
            case "date":
                HarnessAssert.Equal("07/2026", value.GetValue<string>(), "P3 date changed");
                break;
            case "fullDate":
                HarnessAssert.Equal("29/02/2024", value.GetValue<string>(), "P3 fullDate changed");
                break;
            case "multiSelect":
                HarnessAssert.Equal(1, ApiHarnessClient.RequiredArray(value, "P3 multiSelect").Count, "P3 multiSelect count mismatch");
                break;
            case "boolean":
                HarnessAssert.Equal(false, value.GetValue<bool>(), "P3 boolean false changed");
                break;
            default:
                throw new InvalidOperationException($"Unsupported P3 field assertion {fieldType}");
        }
    }

    private static string BuildP3TableValuesJson(string tableMode, string blockId, int value)
    {
        var appendMode = tableMode is "APPEND_ROWS" or "APPEND_COLUMNS";
        var block = new JsonObject
        {
            ["blockId"] = blockId,
            ["tableMode"] = tableMode,
            ["values1D"] = appendMode ? new JsonArray(value, value + 1) : new JsonArray(value),
            ["valueSlots"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["rowKey"] = "row_1",
                    ["columnKey"] = "col_1",
                    ["rowOffset"] = 0,
                    ["columnOffset"] = 0,
                    ["row"] = 0,
                    ["column"] = 0
                }
            }
        };
        if (tableMode == "APPEND_ROWS")
        {
            ApiHarnessClient.RequiredArray(block["valueSlots"], "P3 append row value slots").Add(
                new JsonObject
                {
                    ["index"] = 1,
                    ["rowKey"] = "row_2",
                    ["columnKey"] = "col_1",
                    ["rowOffset"] = 1,
                    ["columnOffset"] = 0,
                    ["row"] = 1,
                    ["column"] = 0
                });
        }
        else if (tableMode == "APPEND_COLUMNS")
        {
            ApiHarnessClient.RequiredArray(block["valueSlots"], "P3 append column value slots").Add(
                new JsonObject
                {
                    ["index"] = 1,
                    ["rowKey"] = "row_1",
                    ["columnKey"] = "col_2",
                    ["rowOffset"] = 0,
                    ["columnOffset"] = 1,
                    ["row"] = 0,
                    ["column"] = 1
                });
        }
        switch (tableMode)
        {
            case "APPEND_ROWS":
                block["rows"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["rowInstanceId"] = "p3-row-instance-1",
                        ["rowOrder"] = 1,
                        ["cells"] = new JsonObject { ["col_1"] = value }
                    },
                    new JsonObject
                    {
                        ["rowInstanceId"] = "p3-row-instance-2",
                        ["rowOrder"] = 2,
                        ["cells"] = new JsonObject { ["col_1"] = value + 1 }
                    }
                };
                break;
            case "APPEND_COLUMNS":
                block["columns"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["columnInstanceId"] = "p3-column-instance-1",
                        ["columnOrder"] = 1,
                        ["cells"] = new JsonObject { ["row_1"] = value }
                    },
                    new JsonObject
                    {
                        ["columnInstanceId"] = "p3-column-instance-2",
                        ["columnOrder"] = 2,
                        ["cells"] = new JsonObject { ["row_1"] = value + 1 }
                    }
                };
                break;
            case "MATRIX":
                block["cells"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["rowKey"] = "row_1",
                        ["columnKey"] = "col_1",
                        ["rowAxisKey"] = "row-axis-1",
                        ["columnAxisKey"] = "column-axis-1",
                        ["metricKey"] = "metric_1",
                        ["value"] = value
                    }
                };
                break;
        }
        return new JsonObject { ["blocks"] = new JsonArray(block) }.ToJsonString();
    }

    private static void AssertP3TableRoundTrip(JsonNode? response, string tableMode, string blockId)
    {
        var json = ApiHarnessClient.RequiredString(response, "tableValuesJson");
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new InvalidOperationException("P3 tableValuesJson must be an object");
        var blocks = ApiHarnessClient.RequiredArray(root["blocks"], "P3 tableValuesJson blocks");
        var block = blocks.OfType<JsonObject>().Single(x => x["blockId"]?.GetValue<string>() == blockId);
        HarnessAssert.Equal(tableMode, ApiHarnessClient.RequiredString(block, "tableMode"), $"P3 {tableMode} round-trip mode mismatch");
        var expectedCount = tableMode is "APPEND_ROWS" or "APPEND_COLUMNS" ? 2 : 1;
        HarnessAssert.Equal(expectedCount, ApiHarnessClient.RequiredArray(block["values1D"], $"P3 {tableMode} values").Count, $"P3 {tableMode} values count mismatch");
        HarnessAssert.Equal(expectedCount, ApiHarnessClient.RequiredArray(block["valueSlots"], $"P3 {tableMode} slots").Count, $"P3 {tableMode} slot count mismatch");
        if (tableMode == "APPEND_ROWS")
            HarnessAssert.Equal(2, ApiHarnessClient.RequiredArray(block["rows"], "P3 append rows projection").Count, "P3 append rows projection count mismatch");
        if (tableMode == "APPEND_COLUMNS")
            HarnessAssert.Equal(2, ApiHarnessClient.RequiredArray(block["columns"], "P3 append columns projection").Count, "P3 append columns projection count mismatch");
        if (tableMode == "MATRIX")
            HarnessAssert.Equal(1, ApiHarnessClient.RequiredArray(block["cells"], "P3 matrix projection").Count, "P3 matrix projection count mismatch");
    }

    private JsonObject BuildP3ConstantMappingPayload(int value)
    {
        var numberField = P3FindFieldByType("number");
        return new JsonObject
        {
            ["rootDynamicFormTemplateId"] = _p3FormId,
            ["steps"] = new JsonArray
            {
                new JsonObject
                {
                    ["stepId"] = "p3_target",
                    ["stepCode"] = "P3_TARGET",
                    ["dynamicFormTemplateId"] = _p3FormId
                }
            },
            ["mappingRules"] = new JsonArray
            {
                new JsonObject
                {
                    ["mappingId"] = "p3_constant_number",
                    ["mappingVersion"] = 1,
                    ["mappingKind"] = "FIELD",
                    ["evaluationGrain"] = "FLOW_INSTANCE",
                    ["errorPolicy"] = "BLOCK_APPLY",
                    ["conflictPolicy"] = "OVERWRITE",
                    ["inputs"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["inputKey"] = "constant",
                            ["dataType"] = "NUMBER",
                            ["cardinality"] = "ONE",
                            ["nullPolicy"] = "ERROR",
                            ["constantValue"] = value,
                            ["source"] = new JsonObject
                            {
                                ["kind"] = "CONSTANT",
                                ["dataType"] = "NUMBER"
                            }
                        }
                    },
                    ["target"] = new JsonObject
                    {
                        ["kind"] = "FIELD",
                        ["dynamicFormTemplateId"] = _p3FormId,
                        ["stepId"] = "p3_target",
                        ["stepCode"] = "P3_TARGET",
                        ["fieldId"] = ApiHarnessClient.RequiredString(numberField, "id"),
                        ["fieldKey"] = ApiHarnessClient.RequiredString(numberField, "key"),
                        ["dataType"] = "NUMBER"
                    },
                    ["calculation"] = new JsonObject
                    {
                        ["kind"] = "DIRECT",
                        ["operation"] = "copy",
                        ["resultDataType"] = "NUMBER"
                    }
                }
            }
        };
    }

    private async Task<WorkAssignmentReport> LoadP3ReportDocumentAsync(CancellationToken ct)
        => await _database.GetCollection<WorkAssignmentReport>("work_assignment_report")
            .Find(x => x.Id == P3ReportId() && !x.IsDeleted)
            .SingleAsync(ct);

    private void ApplyP3ReportStateFromDocument(WorkAssignmentReport report)
    {
        HarnessAssert.Equal(P3ReportId(), report.Id, "P3 Mongo report ID mismatch");
        _p3PayloadRevision = report.PayloadRevision;
        _p3LifecycleRevision = report.LifecycleRevision;
        _p3ReportStatus = (int)report.Status;
    }

    private async Task<string> CaptureP3CoreWriteSnapshotAsync(CancellationToken ct)
    {
        var reportId = P3ReportId();
        var reportObjectId = ObjectId.Parse(reportId);
        var assignmentObjectId = ObjectId.Parse(HarnessAssert.Required(_p3AssignmentId, "P3 runtime assignment"));
        var periodObjectId = ObjectId.Parse(_p3PeriodId);
        var rows = new List<string>();
        await AddCollection("work_assignments", Builders<BsonDocument>.Filter.Eq("_id", assignmentObjectId));
        await AddCollection("work_assignment_report", Builders<BsonDocument>.Filter.Eq("_id", reportObjectId));
        await AddCollection("work_report_payloads", P3IdFilter("reportId", reportId));
        await AddCollection("work_report_table_values", P3IdFilter("reportId", reportId));
        await AddCollection("work_assignment_report_sections", P3IdFilter("workAssignmentReportId", reportId));
        await AddCollection("work_assignment_report_logs", P3IdFilter("workAssignmentReportId", reportId));
        await AddCollection("user_action_logs", P3IdFilter("workAssignmentReportId", reportId));
        await AddCollection("work_report_periods", Builders<BsonDocument>.Filter.Eq("_id", periodObjectId));
        return ComputeSha256(string.Join("\n", rows));

        async Task AddCollection(string name, FilterDefinition<BsonDocument> filter)
        {
            var documents = await _database.GetCollection<BsonDocument>(name)
                .Find(filter)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .ToListAsync(ct);
            rows.Add(name);
            rows.AddRange(documents.Select(x => x.ToJson()));
        }
    }

    private async Task<SortedDictionary<string, long>> CaptureP3LifecycleEventCountsAsync(
        string eventKey,
        CancellationToken ct)
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var projection in new[]
                 {
                     (Collection: "work_assignment_report_logs", KeyField: "lifecycleEventKey"),
                     (Collection: "user_action_logs", KeyField: "idempotencyKey"),
                     (Collection: "work_status_operation_logs", KeyField: "lifecycleEventKey")
                 })
        {
            counts[projection.Collection] = await _database
                .GetCollection<BsonDocument>(projection.Collection)
                .CountDocumentsAsync(
                    Builders<BsonDocument>.Filter.Eq(projection.KeyField, eventKey),
                    cancellationToken: ct);
        }

        return counts;
    }

    private static void AssertP3LifecycleEventCounts(
        IReadOnlyDictionary<string, long> counts,
        long expected,
        string context)
    {
        foreach (var collectionName in new[]
                 {
                     "work_assignment_report_logs",
                     "user_action_logs",
                     "work_status_operation_logs"
                 })
        {
            HarnessAssert.Equal(expected, counts[collectionName], $"{context} {collectionName} count mismatch");
        }
    }

    private async Task<CaseObservation> DeliberatelyFailAssertionAsync(CancellationToken ct)
    {
        var beforeHash = await CaptureP3CoreWriteSnapshotAsync(ct);
        var beforeCounts = await CaptureP3CoreWriteCountsAsync(ct);
        var afterHash = await CaptureP3CoreWriteSnapshotAsync(ct);
        var afterCounts = await CaptureP3CoreWriteCountsAsync(ct);
        var beforeCountFingerprint = string.Join(';', beforeCounts.Select(x => $"{x.Key}={x.Value}"));
        var afterCountFingerprint = string.Join(';', afterCounts.Select(x => $"{x.Key}={x.Value}"));

        HarnessAssert.Equal(beforeHash, afterHash, "Deliberate failure probe changed the direct-Mongo document snapshot");
        HarnessAssert.Equal(beforeCountFingerprint, afterCountFingerprint, "Deliberate failure probe changed direct-Mongo collection counts");
        await EvidenceJson.WriteAsync(
            Path.Combine(_iterationRoot, "p3-deliberate-failure-zero-write.json"),
            new
            {
                reportId = P3ReportId(),
                before = new { sha256 = beforeHash, counts = beforeCounts },
                after = new { sha256 = afterHash, counts = afterCounts },
                hashesMatch = true,
                countsMatch = true,
                zeroWrite = true
            },
            ct);

        HarnessAssert.True(
            false,
            $"Deliberate assertion probe: expected command to exit nonzero and record KHONG_DAT; direct Mongo hash/count stayed unchanged ({beforeHash}; {beforeCountFingerprint})");
        return new CaseObservation("unreachable", "unreachable");
    }

    private async Task<SortedDictionary<string, long>> CaptureP3CoreWriteCountsAsync(CancellationToken ct)
    {
        var reportId = P3ReportId();
        var reportObjectId = ObjectId.Parse(reportId);
        var assignmentObjectId = ObjectId.Parse(HarnessAssert.Required(_p3AssignmentId, "P3 runtime assignment"));
        var periodObjectId = ObjectId.Parse(_p3PeriodId);
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await AddCount("work_assignments", Builders<BsonDocument>.Filter.Eq("_id", assignmentObjectId));
        await AddCount("work_assignment_report", Builders<BsonDocument>.Filter.Eq("_id", reportObjectId));
        await AddCount("work_report_payloads", P3IdFilter("reportId", reportId));
        await AddCount("work_report_table_values", P3IdFilter("reportId", reportId));
        await AddCount("work_assignment_report_sections", P3IdFilter("workAssignmentReportId", reportId));
        await AddCount("work_assignment_report_logs", P3IdFilter("workAssignmentReportId", reportId));
        await AddCount("user_action_logs", P3IdFilter("workAssignmentReportId", reportId));
        await AddCount("work_report_periods", Builders<BsonDocument>.Filter.Eq("_id", periodObjectId));
        return counts;

        async Task AddCount(string name, FilterDefinition<BsonDocument> filter)
            => counts[name] = await _database.GetCollection<BsonDocument>(name).CountDocumentsAsync(filter, cancellationToken: ct);
    }

    private static FilterDefinition<BsonDocument> P3IdFilter(string field, string id)
    {
        var fb = Builders<BsonDocument>.Filter;
        return ObjectId.TryParse(id, out var objectId)
            ? fb.Or(fb.Eq(field, objectId), fb.Eq(field, id))
            : fb.Eq(field, id);
    }
}

