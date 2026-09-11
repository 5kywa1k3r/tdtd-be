using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.StatisticsConfiguration;

using tdtd_be.Services.DynamicForms;
namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private P8ConfigIdentity _p809Label = default!;
    private P8FieldConfigIdentity _p809Form = default!;
    private P8FieldConfigIdentity _p809SecondForm = default!;
    private JsonArray _p809FullPinRequest = default!;
    private JsonArray _p809QueuedPinRequest = default!;
    private string? _p809FullBundleHash;
    private string? _p809FullCanonicalJson;
    private string? _p809EmptyBundleHash;
    private string _p809AssignmentId = string.Empty;
    private bool _p809FixturesSeeded;

    private async Task SeedP809FixturesAsync(CancellationToken ct)
    {
        if (_p809FixturesSeeded)
            return;

        var actor = Actor("system_admin");
        (_, _p809Label) = await CreateLabelAsync(
            actor,
            "p809-fixture-label",
            LabelPayload(
                "p8.bundle.canonical",
                "P8 canonical bundle label",
                "GLOBAL",
                null,
                usage: "STATISTIC",
                dataType: "NUMBER"),
            ct);

        _p809Form = await SeedP809DynamicFormAsync("p809-primary", ct);
        _p809SecondForm = await SeedP809DynamicFormAsync("p809-mixed", ct);
        var formOwner = await _database.GetCollection<BsonDocument>(DynamicFormsCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(_p809Form.OwnerId)))
            .SingleAsync(ct);
        var publishedSchemaHash = BsonString(formOwner, "publishedSchemaHash")
                                  ?? throw new InvalidOperationException("P8-09 form lacks publishedSchemaHash.");
        var formPin =
            $"DYNAMIC_FORM_STAT_CONFIG:{_p809Form.OwnerId}:{_p809Form.ConfigId}:" +
            $"{_p809Form.VersionId}:{_p809Form.VersionNo}:{_p809Form.Revision}:{_p809Form.ConfigHash}";
        var formSchemaPin =
            $"DYNAMIC_FORM_SCHEMA:{_p809Form.OwnerId}:1:{publishedSchemaHash}";

        _p809AssignmentId = ObjectId.GenerateNewId().ToString();
        var assignmentWorkId = ObjectId.GenerateNewId().ToString();
        var assignmentAt = new DateTime(2026, 8, 2, 1, 7, 30, DateTimeKind.Utc);
        var assignment = new WorkAssignment
        {
                    Id = _p809AssignmentId,
                    WorkId = assignmentWorkId,
                    DynamicFormTemplateId = _p809Form.OwnerId,
                    DynamicFormTemplateCode = BsonString(formOwner, "code"),
                    DynamicFormTemplateName = BsonString(formOwner, "name"),
                    DynamicFormFamilyId = _p809Form.OwnerId,
                    DynamicFormVersionNo = 1,
                    DynamicFormSchemaHash = publishedSchemaHash,
                    WorkType = "DYNAMIC_FORM",
                    AssignmentType = "ONCE",
                    AggregationType = "NONE",
                    Assignees =
                    [
                        new UserRef
                        {
                            UserId = actor.Id,
                            Username = actor.Username,
                            FullName = "P8-09 canonical owner",
                            UnitId = actor.UnitId,
                            UnitSymbol = "ROOT",
                            UnitShortName = "ROOT",
                            UnitName = "P8 Root"
                        }
                    ],
                    LeaderWatcherUserIds = [actor.Id],
                    RootAssignmentId = _p809AssignmentId,
                    Level = 0,
                    Code = "P8-BND-ASSIGNMENT",
                    Name = "P8-09 canonical owner assignment",
                    Path = $"/{_p809AssignmentId}/",
                    IssuedByUnitId = _unitAId,
                    IsActive = true,
                    CreatedByUserId = actor.Id,
                    UpdatedByUserId = actor.Id,
                    CreatedAtUtc = assignmentAt,
                    UpdatedAtUtc = assignmentAt,
                    IsDeleted = false
                };
        await _database.GetCollection<WorkAssignment>("work_assignments")
            .InsertOneAsync(
                assignment,
                cancellationToken: ct);
        var basicOwnerId = $"{_p809AssignmentId}:{_p809Form.OwnerId}";
        var advancedOwnerId =
            $"{_p809AssignmentId}:{_p809Form.OwnerId}:main";
        var diffOwnerId = basicOwnerId;

        var formTemplate = await _database
            .GetCollection<DynamicFormTemplate>(DynamicFormsCollection)
            .Find(item => item.Id == _p809Form.OwnerId && !item.IsDeleted)
            .SingleAsync(ct);
        var formFields = JsonNode.Parse(formTemplate.FieldsJson) as JsonArray
                         ?? throw new InvalidOperationException(
                             "P8-09 form fields are not a JSON array.");
        var formField = formFields.OfType<JsonObject>().Single();
        var formFieldId = RequiredString(formField, "id");

        var flowInstanceId = ObjectId.GenerateNewId().ToString();
        var flowBranchId = ObjectId.GenerateNewId().ToString();
        const string flowStepId = "p809-canonical-config";
        var basicFixture = new P8BasicFixture(
            "p809-bundle",
            _p809Form.OwnerId,
            assignment,
            flowInstanceId,
            flowStepId,
            flowBranchId);
        var (_, basicDraft) = await PutBasicConfigAsync(
            actor,
            basicFixture,
            "p809-basic-config",
            BasicDirectPayload(
                [BasicTarget("FIELD", formFieldId, "NUMBER", "SUM")]),
            ct);
        var (_, basicLocked) = await PostBasicActionAsync(
            actor,
            basicFixture,
            "lock",
            "p809-basic-lock",
            basicDraft,
            ct);

        var advancedFixture = new P8AdvancedFixture(
            "p809-bundle",
            _p809Form.OwnerId,
            "main",
            assignment,
            "system_admin");
        var (_, advancedDraft) = await PutAdvancedConfigAsync(
            actor,
            advancedFixture,
            "p809-advanced-config",
            AdvancedPayload(
                advancedFixture,
                sections:
                [
                    AdvancedSection(
                        "main",
                        false,
                        [AdvancedTarget(formFieldId)])
                ]),
            ct);
        var (_, advancedLocked) = await PostAdvancedActionAsync(
            actor,
            advancedFixture,
            "lock",
            "p809-advanced-lock",
            advancedDraft,
            ct);

        var diffFixture = new P8DiffFixture(
            "p809-bundle",
            _p809Form.OwnerId,
            assignment,
            flowInstanceId,
            flowStepId,
            flowBranchId);
        var (_, diffDraft) = await PutDiffConfigAsync(
            actor,
            diffFixture,
            "p809-diff-config",
            DiffPayload(DiffSelector(
                "FIELD",
                formFieldId,
                _p809Label.LabelCode)),
            ct);
        var (_, diffLocked) = await PostDiffActionAsync(
            actor,
            diffFixture,
            "lock",
            "p809-diff-lock",
            diffDraft,
            ct);

        var flowFormAt = new DateTime(
            2026, 8, 2, 1, 8, 0, DateTimeKind.Utc);
        var flowRootForm = BuildP807PublishedForm(
            actor,
            flowFormAt,
            "P8_BUNDLE_FLOW_ROOT",
            "field_note",
            "note");
        var flowChildForm = BuildP807PublishedForm(
            actor,
            flowFormAt.AddSeconds(1),
            "P8_BUNDLE_FLOW_CHILD",
            "field_child_value",
            "child_value");
        await _database.GetCollection<DynamicFormTemplate>(DynamicFormsCollection)
            .InsertManyAsync(
                [flowRootForm, flowChildForm],
                cancellationToken: ct);
        _flowRootFormId = flowRootForm.Id;
        _flowChildFormId = flowChildForm.Id;
        var flowDraft = await CreateFlowDraftAsync(
            actor,
            "bundle-canonical",
            "p809-flow-create",
            BuildP807MappedFlowPayload(),
            ct);
        var flow = await LockFlowVersionAsync(
            actor,
            flowDraft,
            "p809-flow-lock",
            contributionPolicy: null,
            acknowledgeWarning: null,
            ct);

        var readiness =
            await SeedP809CompletedReadinessJobThroughRealApiAsync(ct);

        flow = await SeedP809NegativeFixturesAsync(
            actor,
            formOwner,
            publishedSchemaHash,
            formFieldId,
            flowDraft,
            flow,
            readiness,
            ct);
        var queuedReadiness =
            await SeedP809QueuedReadinessJobThroughRealApiAsync(ct);
        var flowTemplateId = flowDraft.FamilyId;
        var flowVersionId = flow.Id;
        var flowPayloadHash = flow.PayloadHash;
        _p809FullPinRequest = new JsonArray
        {
            P809Pin("LABEL", _p809Label.OwnerId, _p809Label.ConfigId,
                _p809Label.VersionId, _p809Label.VersionNo, _p809Label.Revision,
                _p809Label.ConfigHash, _p809Label.ConfigHash),
            P809Pin("FIELD", _p809Form.OwnerId, _p809Form.ConfigId,
                _p809Form.VersionId, _p809Form.VersionNo, _p809Form.Revision,
                _p809Form.ConfigHash, _p809Form.FieldSectionHash),
            P809Pin("TABLE", _p809Form.OwnerId, _p809Form.ConfigId,
                _p809Form.VersionId, _p809Form.VersionNo, _p809Form.Revision,
                _p809Form.ConfigHash, _p809Form.TableSectionHash),
            P809Pin("BASIC", basicOwnerId, basicLocked.ConfigId,
                basicLocked.VersionId, basicLocked.VersionNo,
                basicLocked.Revision, basicLocked.ConfigHash,
                basicLocked.ConfigHash),
            P809Pin("ADVANCED", advancedOwnerId, advancedLocked.ConfigId,
                advancedLocked.VersionId, advancedLocked.VersionNo,
                advancedLocked.Revision, advancedLocked.ConfigHash,
                advancedLocked.ConfigHash),
            P809Pin("DIFF", diffOwnerId, diffLocked.ConfigId,
                diffLocked.VersionId, diffLocked.VersionNo,
                diffLocked.Revision, diffLocked.ConfigHash,
                diffLocked.ConfigHash),
            P809Pin("FLOW_CONTRIBUTION", flowTemplateId, flowTemplateId,
                flowVersionId, flow.VersionNo, flow.DraftRevision, flowPayloadHash,
                flow.ContributionPolicyHash!),
            P809Pin("READINESS", _p809Label.OwnerId, readiness.ConfigId,
                readiness.VersionId, 1, readiness.StateRevision,
                readiness.ConfigHash, readiness.ContributionHash)
        };
        _p809QueuedPinRequest = (JsonArray)_p809FullPinRequest.DeepClone();
        _p809QueuedPinRequest[7] = P809Pin(
            "READINESS",
            _p809Label.OwnerId,
            queuedReadiness.ConfigId,
            queuedReadiness.VersionId,
            1,
            queuedReadiness.StateRevision,
            queuedReadiness.ConfigHash,
            queuedReadiness.ContributionHash);
        _p809MixedLabelPinRequest =
            (JsonArray)_p809FullPinRequest.DeepClone();
        _p809MixedLabelPinRequest[0] = P809Pin(
            "LABEL",
            _p809MixedLabel.OwnerId,
            _p809MixedLabel.ConfigId,
            _p809MixedLabel.VersionId,
            _p809MixedLabel.VersionNo,
            _p809MixedLabel.Revision,
            _p809MixedLabel.ConfigHash,
            _p809MixedLabel.ConfigHash);
        _p809MixedLabelPinRequest[7] =
            _p809MixedLabelReadinessPin.DeepClone();

        _ = await P809DirectReadinessPinAsync(
            (JsonObject)(_p809FullPinRequest[7]
                         ?? throw new InvalidOperationException(
                             "P8-09 completed readiness pin is null.")),
            ct);
        _ = await P809DirectReadinessPinAsync(
            (JsonObject)(_p809QueuedPinRequest[7]
                         ?? throw new InvalidOperationException(
                             "P8-09 queued readiness pin is null.")),
            ct);
        _ = await P809DirectReadinessPinAsync(
            (JsonObject)(_p809MixedLabelPinRequest[7]
                         ?? throw new InvalidOperationException(
                             "P8-09 mixed-label readiness pin is null.")),
            ct);

        var reportDatasetCollections = new[]
        {
            "work_assignment_report",
            "work_assignment_report_sections",
            "work_report_payloads",
            "work_report_table_values",
            "work_report_periods",
            "work_assignment_report_logs"
        };
        var reportDatasetCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var collection in reportDatasetCollections)
        {
            reportDatasetCounts[collection] = await _database
                .GetCollection<BsonDocument>(collection)
                .CountDocumentsAsync(
                    FilterDefinition<BsonDocument>.Empty,
                    cancellationToken: ct);
        }
        HarnessAssert.True(reportDatasetCounts.All(pair => pair.Value == 0),
            "P8-09 fixture created a report dataset: " +
            string.Join(", ", reportDatasetCounts.Where(pair => pair.Value != 0)
                .Select(pair => $"{pair.Key}={pair.Value}")));

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-bnd-fixtures.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                seededBeforeOwnedCases = true,
                realKestrelConfigSetup = true,
                reportDatasetCreated = false,
                reportDatasetCounts,
                reportDatasetZeroVerifiedByDirectMongo = true,
                dependencyKinds = P809DependencyKinds,
                pins = _p809FullPinRequest,
                queuedReadinessPin = _p809QueuedPinRequest[7],
                readinessSetup = new
                {
                    realP808EnqueueAndWorkerApi = true,
                    commandIds = new[] { "p809-readiness-completed", "p809-readiness-queued" }
                },
                flowSetup = new { realP807CreateAndLockApi = true, commandIds = new[] { "p809-flow-create", "p809-flow-lock" } },
                assignmentOwner = new
                {
                    assignmentId = _p809AssignmentId,
                    dynamicFormTemplateId = _p809Form.OwnerId,
                    sectionId = "main",
                    basicOwnerId,
                    advancedOwnerId,
                    diffOwnerId
                },
                realConfigSetup = new
                {
                    basic = new
                    {
                        commandIds = new[]
                        {
                            "p809-basic-config",
                            "p809-basic-lock"
                        },
                        basicLocked.ConfigId,
                        basicLocked.VersionId,
                        basicLocked.VersionNo,
                        basicLocked.Revision,
                        basicLocked.ConfigHash,
                        basicLocked.Status
                    },
                    advanced = new
                    {
                        commandIds = new[]
                        {
                            "p809-advanced-config",
                            "p809-advanced-lock"
                        },
                        advancedLocked.ConfigId,
                        advancedLocked.VersionId,
                        advancedLocked.VersionNo,
                        advancedLocked.Revision,
                        advancedLocked.ConfigHash,
                        advancedLocked.Status
                    },
                    diff = new
                    {
                        commandIds = new[]
                        {
                            "p809-diff-config",
                            "p809-diff-lock"
                        },
                        diffLocked.ConfigId,
                        diffLocked.VersionId,
                        diffLocked.VersionNo,
                        diffLocked.Revision,
                        diffLocked.ConfigHash,
                        diffLocked.Status
                    }
                },
                formPin,
                fixtureCollections = new[]
                {
                    LabelsCollection,
                    DynamicFormsCollection,
                    BasicConfigsCollection,
                    AdvancedConfigsCollection,
                    DiffConfigsCollection,
                    FlowFamiliesCollection,
                    FlowVersionsCollection,
                    FlowDefinitionReceiptsCollection,
                    "work_assignments",
                    P808JobsCollection
                }
            },
            ct);
        _p809FixturesSeeded = true;
    }

    private async Task<P8FieldConfigIdentity> SeedP809DynamicFormAsync(
        string key,
        CancellationToken ct)
    {
        var fixture = NewTableFixture(
            key,
            [TableBlock(
                "bundle-table",
                "FIXED_GRID",
                [TableMetricFixture("metric:amount", "NUMBER")])]);
        fixture.Form.Template.IsPublished = true;
        fixture.Form.Template.PublishedAtUtc = new DateTime(2026, 8, 2, 1, 7, 0, DateTimeKind.Utc);
        fixture.Form.Template.PublishedByUserId = Actor("system_admin").Id;
        var published = DynamicFormPublishedSchemaSnapshotBuilder.Build(fixture.Form.Template);
        fixture.Form.Template.PublishedSchemaSnapshotJson = published.Json;
        fixture.Form.Template.PublishedSchemaHash = published.Sha256;
        await _database.GetCollection<DynamicFormTemplate>(DynamicFormsCollection)
            .InsertOneAsync(fixture.Form.Template, cancellationToken: ct);
        var field = fixture.Form.Fields.Single();
        await PatchFieldConfigAsync(
            Actor("system_admin"),
            fixture.Form,
            $"{key}-field-config",
            FieldPayload(FieldPatch(
                field.Id,
                ["COUNT", "SUM"],
                statisticLabelCodes: [_p809Label.LabelCode])),
            ct);
        var (_, identity) = await PatchTableConfigAsync(
            Actor("system_admin"),
            fixture,
            $"{key}-table-config",
            TablePayload(TablePatch(
                "bundle-table",
                "FIXED_GRID",
                false,
                [TableMetric("metric:amount", "NUMBER", ["COUNT", "SUM"])])),
            ct);
        return identity;
    }

    private async Task<P809ReadinessSeed>
        SeedP809CompletedReadinessJobThroughRealApiAsync(CancellationToken ct)
    {
        var actor = Actor("system_admin");
        var completedEnqueue = await EnqueueP808JobAsync(
            actor,
            _p809Label.OwnerKind,
            _p809Label.OwnerId,
            P808EnqueueEnvelope("p809-readiness-completed", _p809Label),
            ct);
        ApiHarnessClient.ExpectStatus(
            completedEnqueue,
            HttpStatusCode.OK,
            "P8-09 completed-readiness enqueue");
        var completedIdentity = ParseP808JobIdentity(
            completedEnqueue.Json,
            "P8-09 completed-readiness enqueue");
        HarnessAssert.Equal("QUEUED", completedIdentity.ExternalStatus,
            "P8-09 completed-readiness fixture was not initially queued");

        var process = await ProcessP808JobsAsync(actor, 1, ct);
        ApiHarnessClient.ExpectStatus(
            process,
            HttpStatusCode.OK,
            "P8-09 completed-readiness worker");
        HarnessAssert.Equal(
            1,
            ApiHarnessClient.FindIntRecursive(process.Json, "completed"),
            "P8-09 readiness worker did not complete exactly one job");
        var completedDocument = await RequireP808JobAsync(
            completedIdentity.JobId,
            ct);
        HarnessAssert.Equal("COMPLETED", BsonString(completedDocument, "status"),
            "P8-09 completed-readiness internal status drifted");
        return P809ReadinessFromPersistedDocument(completedDocument);
    }

    private async Task<P809ReadinessSeed>
        SeedP809QueuedReadinessJobThroughRealApiAsync(CancellationToken ct)
    {
        var actor = Actor("system_admin");
        var queuedEnqueue = await EnqueueP808JobAsync(
            actor,
            _p809Label.OwnerKind,
            _p809Label.OwnerId,
            P808EnqueueEnvelope("p809-readiness-queued", _p809Label),
            ct);
        ApiHarnessClient.ExpectStatus(
            queuedEnqueue,
            HttpStatusCode.OK,
            "P8-09 queued-readiness enqueue");
        var queuedIdentity = ParseP808JobIdentity(
            queuedEnqueue.Json,
            "P8-09 queued-readiness enqueue");
        HarnessAssert.Equal("QUEUED", queuedIdentity.ExternalStatus,
            "P8-09 queued-readiness fixture status drifted");
        var queuedDocument = await RequireP808JobAsync(
            queuedIdentity.JobId,
            ct);
        HarnessAssert.Equal("PENDING", BsonString(queuedDocument, "status"),
            "P8-09 queued-readiness internal status drifted");
        return P809ReadinessFromPersistedDocument(queuedDocument);
    }

    private static P809ReadinessSeed P809ReadinessFromPersistedDocument(
        BsonDocument document)
    {
        var id = BsonString(document, "_id")
                 ?? throw new InvalidOperationException("P8-09 readiness lacks _id.");
        var configId = BsonString(document, "configId")
                       ?? throw new InvalidOperationException("P8-09 readiness lacks configId.");
        var configHash = BsonString(document, "configHash")
                         ?? throw new InvalidOperationException("P8-09 readiness lacks configHash.");
        var bundleHash = BsonString(document, "bundleHash")
                         ?? throw new InvalidOperationException("P8-09 readiness lacks bundleHash.");
        var dependencyPinsHash = BsonString(document, "dependencyPinsHash")
                                 ?? throw new InvalidOperationException("P8-09 readiness lacks dependencyPinsHash.");
        var stateHash = BsonString(document, "stateHash")
                        ?? throw new InvalidOperationException("P8-09 readiness lacks stateHash.");
        var stateRevision = BsonLong(document, "stateRevision")
                            ?? throw new InvalidOperationException("P8-09 readiness lacks stateRevision.");
        var contributionHash = StatConfigCanonicalJson.HashObject(new
        {
            bundleHash,
            dependencyPinsHash,
            stateHash
        });
        return new P809ReadinessSeed(
            id,
            configId,
            configHash,
            stateRevision,
            contributionHash,
            document.DeepClone().AsBsonDocument);
    }

    private static JsonObject P809Pin(
        string kind,
        string ownerId,
        string configId,
        string versionId,
        int versionNo,
        long revision,
        string configHash,
        string contributionHash)
        => new()
        {
            ["kind"] = kind,
            ["ownerId"] = ownerId,
            ["configId"] = configId,
            ["versionId"] = versionId,
            ["versionNo"] = versionNo,
            ["revision"] = revision,
            ["configHash"] = configHash,
            ["contributionHash"] = contributionHash
        };

}

internal sealed record P809ReadinessSeed(
    string VersionId,
    string ConfigId,
    string ConfigHash,
    long StateRevision,
    string ContributionHash,
    BsonDocument Document);
