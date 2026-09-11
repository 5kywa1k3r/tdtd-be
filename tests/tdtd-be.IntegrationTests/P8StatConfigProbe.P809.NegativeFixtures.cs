using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private P8ConfigIdentity _p809MixedLabel = default!;
    private JsonObject _p809MixedLabelReadinessPin = default!;
    private JsonObject _p809MixedAssignmentBasicPin = default!;
    private JsonObject _p809ForgedReadinessPin = default!;
    private JsonObject _p809NonCurrentFlowPin = default!;
    private JsonArray _p809MixedLabelPinRequest = default!;

    private async Task<P8FlowVersionIdentity>
        SeedP809NegativeFixturesAsync(
            P8Actor actor,
            BsonDocument formOwner,
            string publishedSchemaHash,
            string formFieldId,
            P8FlowDraftFixture flowFamily,
            P8FlowVersionIdentity flowV1,
            P809ReadinessSeed completedReadiness,
            CancellationToken ct)
    {
        (_, _p809MixedLabel) = await CreateLabelAsync(
            actor,
            "p809-mixed-label",
            LabelPayload(
                "p8.bundle.mixed",
                "P8 mixed bundle label",
                "GLOBAL",
                null,
                usage: "STATISTIC",
                dataType: "NUMBER"),
            ct);
        var mixedReadinessResponse = await EnqueueP808JobAsync(
            actor,
            _p809MixedLabel.OwnerKind,
            _p809MixedLabel.OwnerId,
            P808EnqueueEnvelope(
                "p809-mixed-label-readiness",
                _p809MixedLabel),
            ct);
        ApiHarnessClient.ExpectStatus(
            mixedReadinessResponse,
            HttpStatusCode.OK,
            "P8-09 mixed-label readiness enqueue");
        var mixedReadinessIdentity = ParseP808JobIdentity(
            mixedReadinessResponse.Json,
            "P8-09 mixed-label readiness enqueue");
        var mixedProcess = await ProcessP808JobsAsync(actor, 1, ct);
        ApiHarnessClient.ExpectStatus(
            mixedProcess,
            HttpStatusCode.OK,
            "P8-09 mixed-label readiness worker");
        HarnessAssert.Equal(
            1,
            ApiHarnessClient.FindIntRecursive(mixedProcess.Json, "completed"),
            "P8-09 mixed-label readiness worker did not complete one job");
        var mixedReadinessDocument = await RequireP808JobAsync(
            mixedReadinessIdentity.JobId,
            ct);
        HarnessAssert.Equal(
            "COMPLETED",
            BsonString(mixedReadinessDocument, "status"),
            "P8-09 mixed-label readiness internal status drifted");
        var mixedReadiness =
            P809ReadinessFromPersistedDocument(mixedReadinessDocument);
        _p809MixedLabelReadinessPin = P809Pin(
            "READINESS",
            _p809MixedLabel.OwnerId,
            mixedReadiness.ConfigId,
            mixedReadiness.VersionId,
            1,
            mixedReadiness.StateRevision,
            mixedReadiness.ConfigHash,
            mixedReadiness.ContributionHash);

        var mixedAssignmentId = ObjectId.GenerateNewId().ToString();
        var mixedAssignmentAt = new DateTime(
            2026, 8, 2, 1, 9, 0, DateTimeKind.Utc);
        var mixedAssignment = new WorkAssignment
        {
            Id = mixedAssignmentId,
            WorkId = ObjectId.GenerateNewId().ToString(),
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
                    FullName = "P8-09 mixed assignment",
                    UnitId = actor.UnitId,
                    UnitSymbol = "ROOT",
                    UnitShortName = "ROOT",
                    UnitName = "P8 Root"
                }
            ],
            LeaderWatcherUserIds = [actor.Id],
            RootAssignmentId = mixedAssignmentId,
            Level = 0,
            Code = "P8-BND-MIXED-ASSIGNMENT",
            Name = "P8-09 cross-assignment negative owner",
            Path = $"/{mixedAssignmentId}/",
            IssuedByUnitId = _unitAId,
            IsActive = true,
            CreatedByUserId = actor.Id,
            UpdatedByUserId = actor.Id,
            CreatedAtUtc = mixedAssignmentAt,
            UpdatedAtUtc = mixedAssignmentAt,
            IsDeleted = false
        };
        await _database.GetCollection<WorkAssignment>("work_assignments")
            .InsertOneAsync(
                mixedAssignment,
                cancellationToken: ct);
        var mixedBasicFixture = new P8BasicFixture(
            "p809-mixed-assignment",
            _p809Form.OwnerId,
            mixedAssignment,
            ObjectId.GenerateNewId().ToString(),
            "p809-mixed-assignment",
            ObjectId.GenerateNewId().ToString());
        var (_, mixedBasic) = await PutBasicConfigAsync(
            actor,
            mixedBasicFixture,
            "p809-mixed-assignment-basic",
            BasicDirectPayload(
                [BasicTarget("FIELD", formFieldId, "NUMBER", "SUM")]),
            ct);
        _p809MixedAssignmentBasicPin = P809Pin(
            "BASIC",
            mixedBasic.OwnerId,
            mixedBasic.ConfigId,
            mixedBasic.VersionId,
            mixedBasic.VersionNo,
            mixedBasic.Revision,
            mixedBasic.ConfigHash,
            mixedBasic.ConfigHash);

        var familyRead = await ReadFlowFamilyAsync(
            actor,
            flowFamily.FamilyId,
            ct);
        var committedV1 = flowFamily with
        {
            FamilyRevision = RequiredInt(
                familyRead,
                "familyRevision"),
            Version = flowV1
        };
        var reopened = await ReopenFlowVersionAsync(
            actor,
            committedV1,
            flowV1,
            "p809-flow-reopen-v2",
            ct);
        var flowV2 = await LockFlowVersionAsync(
            actor,
            reopened,
            "p809-flow-lock-v2",
            contributionPolicy: null,
            acknowledgeWarning: null,
            ct);
        _p809NonCurrentFlowPin = P809Pin(
            "FLOW_CONTRIBUTION",
            flowV1.FamilyId,
            flowV1.FamilyId,
            flowV1.Id,
            flowV1.VersionNo,
            flowV1.DraftRevision,
            flowV1.PayloadHash,
            flowV1.ContributionPolicyHash!);

        var forgedDocument =
            completedReadiness.Document.DeepClone().AsBsonDocument;
        var forgedJobId = ObjectId.GenerateNewId().ToString();
        forgedDocument["_id"] = ObjectId.Parse(forgedJobId);
        forgedDocument["dedupeKey"] =
            StatConfigCanonicalJson.HashObject(new
            {
                kind = "P8_BND_FORGED_READINESS",
                forgedJobId
            });
        forgedDocument["enqueueCommandId"] =
            "p809-forged-readiness";
        forgedDocument["commandReceiptId"] =
            ObjectId.GenerateNewId().ToString();
        forgedDocument["auditOutboxId"] =
            StatConfigCanonicalJson.HashUtf8(
                $"P8_BND_FORGED_READINESS\0{forgedJobId}");
        forgedDocument["correlationId"] =
            StatConfigCanonicalJson.HashObject(new
            {
                queue = "STAT_CONFIG_VALIDATION",
                forgedJobId
            });
        forgedDocument["stateHash"] = new string('f', 64);
        await _database.GetCollection<BsonDocument>(P808JobsCollection)
            .InsertOneAsync(
                forgedDocument,
                cancellationToken: ct);
        var forgedReadiness =
            P809ReadinessFromPersistedDocument(forgedDocument);
        _p809ForgedReadinessPin = P809Pin(
            "READINESS",
            _p809Label.OwnerId,
            forgedReadiness.ConfigId,
            forgedReadiness.VersionId,
            1,
            forgedReadiness.StateRevision,
            forgedReadiness.ConfigHash,
            forgedReadiness.ContributionHash);

        await EvidenceJson.WriteAsync(
            Path.Combine(
                _paths.RunRoot,
                "p8-bnd-negative-fixtures.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                mixedLabel = new
                {
                    _p809MixedLabel.OwnerId,
                    _p809MixedLabel.ConfigId,
                    _p809MixedLabel.VersionId,
                    readinessJobId = mixedReadiness.VersionId
                },
                mixedAssignment = new
                {
                    assignmentId = mixedAssignmentId,
                    basicPin = _p809MixedAssignmentBasicPin
                },
                nonCurrentFlow = new
                {
                    familyId = flowV1.FamilyId,
                    nonCurrentVersionId = flowV1.Id,
                    currentVersionId = flowV2.Id
                },
                forgedReadiness = new
                {
                    jobId = forgedJobId,
                    integrityField = "stateHash",
                    expectedRejection = "BUNDLE_READINESS_INTEGRITY"
                },
                setupViaRealApis = new[]
                {
                    "P8-01 LABEL",
                    "P8-04 BASIC",
                    "P8-07 FLOW",
                    "P8-08 READINESS"
                },
                intentionalCorruptionOutsideCaseSnapshots =
                    new[] { "forged readiness stateHash" }
            },
            ct);
        return flowV2;
    }
}
