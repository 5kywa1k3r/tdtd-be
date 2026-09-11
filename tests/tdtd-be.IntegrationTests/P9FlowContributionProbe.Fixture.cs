using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task PrepareFlowContributionVersionsAsync(CancellationToken ct)
    {
        var database = RequireDatabase();
        var fixture = Fixture();
        var versions = database.GetCollection<DynamicFlowTemplateVersion>(
            "dynamic_flow_template_versions");
        var exclude = await versions
            .Find(version => version.Id == fixture.FlowVersionId && !version.IsDeleted)
            .SingleAsync(ct);
        DynamicFlowContributionPolicyContract.ApplyLockedPolicy(
            exclude,
            DynamicFlowContributionPolicyContract.ResolveLockSelection(
                DynamicFlowContributionPolicyContract.Exclude,
                null));
        DynamicFlowContributionPolicyContract.ValidateLockedPolicy(exclude);
        await versions.UpdateOneAsync(
            version => version.Id == exclude.Id && !version.IsDeleted,
            Builders<DynamicFlowTemplateVersion>.Update
                .Set(version => version.ContributionPolicy, exclude.ContributionPolicy)
                .Set(version => version.ContributionPolicyHash, exclude.ContributionPolicyHash)
                .Set(version => version.ContributionWarning, exclude.ContributionWarning),
            cancellationToken: ct);

        var includeDocument = exclude.ToBsonDocument();
        var includeId = ObjectId.GenerateNewId();
        includeDocument["_id"] = includeId;
        includeDocument["versionNo"] = 2;
        includeDocument["originFamilyId"] = ObjectId.Parse(fixture.FlowFamilyId);
        includeDocument["originVersionId"] = ObjectId.Parse(exclude.Id);
        includeDocument["createdAtUtc"] = DateTime.UtcNow;
        includeDocument["updatedAtUtc"] = DateTime.UtcNow;
        includeDocument.Remove("contributionPolicy");
        includeDocument.Remove("contributionPolicyHash");
        includeDocument.Remove("contributionWarning");
        var include = BsonSerializer.Deserialize<DynamicFlowTemplateVersion>(
            includeDocument);
        DynamicFlowContributionPolicyContract.EnsureIncludeOrigin(include, exclude);
        DynamicFlowContributionPolicyContract.ApplyLockedPolicy(
            include,
            DynamicFlowContributionPolicyContract.ResolveLockSelection(
                DynamicFlowContributionPolicyContract.Include,
                true));
        DynamicFlowContributionPolicyContract.ValidateLockedPolicy(include);
        await versions.InsertOneAsync(include, cancellationToken: ct);

        _flwExcludeVersionId = exclude.Id;
        _flwIncludeVersionId = include.Id;
    }

    private async Task SeedCanonicalP7MappingLineageAsync(
        CancellationToken ct,
        string commandId = "p9-flw-p7-apply-001",
        string? flowVersionId = null)
    {
        var database = RequireDatabase();
        var fixture = Fixture();
        var reportCollection = database.GetCollection<WorkAssignmentReport>(
            "work_assignment_report");
        var report = await reportCollection
            .Find(item => item.Id == fixture.ReportId && !item.IsDeleted)
            .SingleAsync(ct);
        var payloadCollection = database.GetCollection<WorkReportPayload>(
            "work_report_payloads");
        var payload = await payloadCollection
            .Find(item => item.ReportId == report.Id && !item.IsDeleted)
            .SingleAsync(ct);
        var flowVersion = await database
            .GetCollection<DynamicFlowTemplateVersion>(
                "dynamic_flow_template_versions")
            .Find(version => version.Id == (flowVersionId ?? _flwExcludeVersionId) && !version.IsDeleted)
            .SingleAsync(ct);

        var now = DateTime.UtcNow;
        var nextPayloadRevision = report.PayloadRevision + 1;
        var mappingSummary = new JsonObject
        {
            ["kind"] = "DYNAMIC_FLOW_MAPPING",
            ["schemaVersion"] = "P7-MAP-RESULT-1",
            ["sourceSignature"] = StatRunCanonicalJson.HashText(
                $"P9-FLW-SOURCE\n{report.Id}\n{report.PayloadHash}"),
            ["result"] = "P9-07 canonical mapped payload"
        }.ToJsonString();
        var resultPayloadHash = ComputeWorkReportPayloadHash(
            payload.Values1DJson,
            payload.FieldValuesJson ?? "{}",
            payload.TableValuesRootJson ?? "[]",
            mappingSummary);
        var receiptId = ObjectId.GenerateNewId().ToString();
        var provenanceId = ObjectId.GenerateNewId().ToString();
        var eventId = ObjectId.GenerateNewId().ToString();
        var outboxId = ObjectId.GenerateNewId().ToString();
        var mappingRuleSetHash = StatRunCanonicalJson.HashText(
            $"P9-FLW-MAPPING-RULESET-V1\n{flowVersion.PayloadHash}");
        var sourceSignature = StatRunCanonicalJson.HashText(
            $"P9-FLW-MAPPING-SOURCE-SIGNATURE-V1\n{report.Id}\n{report.PayloadRevision}\n{report.PayloadHash}");
        var resultSemanticHash = StatRunCanonicalJson.HashText(
            $"P9-FLW-MAPPING-RESULT-V1\n{resultPayloadHash}");
        var runtimePin = new DynamicFlowMappingRuntimePin
        {
            FlowFamilyId = fixture.FlowFamilyId,
            FlowVersionId = flowVersion.Id,
            FlowVersionNo = flowVersion.VersionNo,
            FlowPayloadHash = flowVersion.PayloadHash,
            CatalogVersion = flowVersion.CatalogVersion,
            CatalogSemanticHash = flowVersion.CatalogSemanticHash,
            MappingRuleSetHash = mappingRuleSetHash,
            EvaluatorVersion = "P7-MAP-EVALUATOR-1",
            FunctionRegistryVersion = "P7-MAP-FUNCTIONS-1",
            FunctionRegistryHash = StatRunCanonicalJson.HashText(
                "P7-MAP-FUNCTIONS-1"),
            FlowInstanceId = fixture.FlowInstanceId,
            ExecutionEpoch = 1,
            StepInstanceId = fixture.FlowStepInstanceId,
            StepId = "P9_CORE_STEP",
            BranchId = await LoadFlowBranchIdAsync(ct),
            AttemptNo = 1,
            FormFamilyId = fixture.TemplateId,
            FormVersionId = fixture.TemplateId,
            FormVersionNo = 1,
            FormSchemaHash = report.DynamicFormSchemaHash!,
            FormSnapshotHash = report.DynamicFormSchemaHash!
        };
        var resultSnapshot = new BsonDocument
        {
            ["schemaVersion"] = "P7-MAP-RESULT-1",
            ["targetReportId"] = ObjectId.Parse(report.Id),
            ["targetPayloadRevision"] = nextPayloadRevision,
            ["targetPayloadHash"] = resultPayloadHash,
            ["summarySourceJson"] = mappingSummary
        };
        var resultSnapshotHash =
            DynamicFlowMappingLifecycleContract.ComputeDocumentHash(resultSnapshot);
        var provenanceSnapshot = new BsonDocument
        {
            ["schemaVersion"] = "P7-MAP-PROVENANCE-1",
            ["receiptId"] = ObjectId.Parse(receiptId),
            ["targetReportId"] = ObjectId.Parse(report.Id),
            ["mappingRuleSetHash"] = mappingRuleSetHash,
            ["sourceSignature"] = sourceSignature,
            ["resultSemanticHash"] = resultSemanticHash,
            ["resultSnapshotHash"] = resultSnapshotHash
        };
        var provenanceHash =
            DynamicFlowMappingLifecycleContract.ComputeDocumentHash(
                provenanceSnapshot);
        var eventPayload = new BsonDocument
        {
            ["schemaVersion"] = "P7-MAP-EVENT-1",
            ["receiptId"] = ObjectId.Parse(receiptId),
            ["provenanceId"] = ObjectId.Parse(provenanceId),
            ["provenanceHash"] = provenanceHash,
            ["resultPayloadHash"] = resultPayloadHash
        };
        var eventPayloadHash =
            DynamicFlowMappingLifecycleContract.ComputeDocumentHash(eventPayload);
        var eventKey = DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(
            new
            {
                operation = DynamicFlowMappingLifecycleContract.ApplyInitialOperation,
                receiptId,
                provenanceId,
                targetReportId = report.Id,
                resultPayloadHash
            });
        var actorUserId = Actor("executor").Id;
        var intent = new DynamicFlowMappingReconcileIntent
        {
            ActorUserId = actorUserId,
            ReceiptId = receiptId,
            ProvenanceId = provenanceId,
            EventId = eventId,
            TargetReportId = report.Id,
            TargetAssignmentId = report.WorkAssignmentId,
            CommandId = commandId,
            TargetPayloadRevision = nextPayloadRevision,
            TargetPayloadHash = resultPayloadHash,
            TargetLifecycleRevision = report.LifecycleRevision,
            SourceSignatureVersion = "P7-MAP-SOURCE-SIGNATURE-1",
            SourceSignature = sourceSignature,
            ResultSemanticHash = resultSemanticHash,
            MappingRuleSetHash = mappingRuleSetHash,
            ProvenanceHash = provenanceHash,
            RuntimePin = runtimePin,
            ProjectionSnapshot = resultSnapshot,
            ProjectionSnapshotHash = resultSnapshotHash,
            AuditSnapshot = provenanceSnapshot,
            AuditSnapshotHash = provenanceHash,
            ProjectionBusinessKeys = [report.Id],
            CommittedAtUtc = now
        };
        var intentHash = DynamicFlowMappingLifecycleContract.ComputeDocumentHash(
            intent.ToBsonDocument());
        var outboxDedupeKey =
            DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(new
            {
                operation = DynamicFlowMappingLifecycleContract.ApplyInitialOperation,
                eventKey
            });
        var mappingEvent = new DynamicFlowMappingEvent
        {
            Id = eventId,
            EventKey = eventKey,
            EventType = "DYNAMIC_FLOW_MAPPING_APPLIED",
            ReceiptId = receiptId,
            ProvenanceId = provenanceId,
            TargetReportId = report.Id,
            TargetAssignmentId = report.WorkAssignmentId,
            CommandId = commandId,
            CorrelationId = commandId,
            RuntimePin = runtimePin,
            TargetPayloadRevision = nextPayloadRevision,
            TargetPayloadHash = resultPayloadHash,
            TargetLifecycleRevision = report.LifecycleRevision,
            SourceSignatureVersion = "P7-MAP-SOURCE-SIGNATURE-1",
            SourceSignature = sourceSignature,
            ResultSemanticHash = resultSemanticHash,
            ProvenanceHash = provenanceHash,
            Payload = eventPayload,
            PayloadHash = eventPayloadHash,
            ActorUserId = actorUserId,
            OccurredAtUtc = now
        };
        var outbox = new DynamicFlowMappingOutboxItem
        {
            Id = outboxId,
            ReceiptId = receiptId,
            EventId = eventId,
            ProvenanceId = provenanceId,
            TargetReportId = report.Id,
            CommandId = commandId,
            Operation = DynamicFlowMappingLifecycleContract.ApplyInitialOperation,
            DedupeKey = outboxDedupeKey,
            Intent = intent,
            IntentHash = intentHash,
            State = DynamicFlowMappingOutboxStates.Reconciled,
            AttemptCount = 1,
            RepairEpoch = 0,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ReconciledAtUtc = now
        };
        var provenance = new DynamicFlowMappingProvenanceRecord
        {
            Id = provenanceId,
            ReceiptId = receiptId,
            WorkId = report.WorkId,
            TargetAssignmentId = report.WorkAssignmentId,
            TargetReportId = report.Id,
            CommandId = commandId,
            TargetPayloadRevision = nextPayloadRevision,
            TargetPayloadHash = resultPayloadHash,
            TargetLifecycleRevision = report.LifecycleRevision,
            SourceSignatureVersion = "P7-MAP-SOURCE-SIGNATURE-1",
            SourceSignature = sourceSignature,
            ResultSemanticHash = resultSemanticHash,
            MappingRuleSetHash = mappingRuleSetHash,
            RuntimePin = runtimePin,
            SourcePins = [],
            ResultSnapshot = resultSnapshot,
            ResultSnapshotHash = resultSnapshotHash,
            ProvenanceSnapshot = provenanceSnapshot,
            ProvenanceHash = provenanceHash,
            OwnedTargetRefs = ["FIELD:amount", "FIELD:approved", "TABLE:amount", "LABEL:p9_lfc_label"],
            State = DynamicFlowMappingProvenanceStates.Current,
            CreatedByUserId = actorUserId,
            CreatedAtUtc = now
        };
        var writeSetHash = DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(
            new
            {
                receiptId,
                provenanceId,
                eventId,
                eventKey,
                eventPayloadHash,
                outboxId,
                outboxDedupeKey,
                intentHash,
                targetReportId = report.Id,
                resultPayloadRevision = nextPayloadRevision,
                resultPayloadHash,
                resultLifecycleRevision = report.LifecycleRevision,
                provenanceHash
            });
        var receipt = new DynamicFlowMappingApplyReceipt
        {
            Id = receiptId,
            WorkId = report.WorkId,
            TargetAssignmentId = report.WorkAssignmentId,
            TargetReportId = report.Id,
            CommandId = commandId,
            RequestHash = DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(
                new { commandId, report.Id, nextPayloadRevision, mappingRuleSetHash }),
            PreviewTokenId = "p9-flw-signed-preview-001",
            PreviewTokenHash = DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(
                new { preview = "p9-flw-signed-preview-001", report.Id, mappingRuleSetHash }),
            PreviewIssuedAtUtc = now.AddMinutes(-1),
            PreviewExpiresAtUtc = now.AddMinutes(29),
            SourceSignatureVersion = "P7-MAP-SOURCE-SIGNATURE-1",
            SourceSignature = sourceSignature,
            ResultSemanticHash = resultSemanticHash,
            AuthorizationSnapshotHash = DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(
                new { actorUserId, report.WorkId, report.WorkAssignmentId }),
            ExpectedTargetPayloadRevision = report.PayloadRevision,
            ExpectedTargetPayloadHash = report.PayloadHash!,
            ExpectedTargetLifecycleRevision = report.LifecycleRevision,
            ExpectedTargetStatus = report.Status.ToString().ToUpperInvariant(),
            ExpectedTargetIsActive = report.IsActive,
            RuntimePin = runtimePin,
            SourcePins = [],
            State = DynamicFlowMappingApplyStates.Committed,
            ResultSnapshot = resultSnapshot,
            ResultSnapshotHash = resultSnapshotHash,
            WriteSetHash = writeSetHash,
            ResultPayloadRevision = nextPayloadRevision,
            ResultPayloadHash = resultPayloadHash,
            ResultLifecycleRevision = report.LifecycleRevision,
            ProvenanceId = provenanceId,
            ProvenanceHash = provenanceHash,
            EventId = eventId,
            OutboxIntentId = outboxId,
            ActorUserId = actorUserId,
            CreatedAtUtc = now,
            CommittedAtUtc = now,
            UpdatedAtUtc = now,
            ReconciledAtUtc = now
        };

        await database.GetCollection<DynamicFlowMappingApplyReceipt>(
                "dynamic_flow_mapping_apply_receipts")
            .InsertOneAsync(receipt, cancellationToken: ct);
        await database.GetCollection<DynamicFlowMappingProvenanceRecord>(
                "dynamic_flow_mapping_provenance")
            .InsertOneAsync(provenance, cancellationToken: ct);
        await database.GetCollection<DynamicFlowMappingEvent>(
                "dynamic_flow_mapping_events")
            .InsertOneAsync(mappingEvent, cancellationToken: ct);
        await database.GetCollection<DynamicFlowMappingOutboxItem>(
                "dynamic_flow_mapping_outbox")
            .InsertOneAsync(outbox, cancellationToken: ct);

        payload.PayloadRevision = nextPayloadRevision;
        payload.SummarySourceJson = mappingSummary;
        payload.PayloadHash = resultPayloadHash;
        payload.PayloadSizeBytes = Encoding.UTF8.GetByteCount(payload.Values1DJson) +
                                   Encoding.UTF8.GetByteCount(payload.FieldValuesJson ?? string.Empty) +
                                   Encoding.UTF8.GetByteCount(payload.TableValuesRootJson ?? string.Empty) +
                                   Encoding.UTF8.GetByteCount(mappingSummary);
        payload.UpdatedAtUtc = now;
        payload.UpdatedByUserId = actorUserId;
        await payloadCollection.ReplaceOneAsync(
            item => item.Id == payload.Id,
            payload,
            cancellationToken: ct);

        report.PayloadRevision = nextPayloadRevision;
        report.PayloadHash = resultPayloadHash;
        report.PayloadSizeBytes = payload.PayloadSizeBytes;
        report.SummarySourceJson = mappingSummary;
        report.CumulativeContributionMode = "EXCLUDE";
        report.DynamicFlowMappingReceiptId = receiptId;
        report.DynamicFlowMappingProvenanceId = provenanceId;
        report.DynamicFlowMappingProvenanceHash = provenanceHash;
        report.DynamicFlowMappingResultPayloadRevision = nextPayloadRevision;
        report.DynamicFlowMappingResultPayloadHash = resultPayloadHash;
        report.UpdatedAtUtc = now;
        report.UpdatedByUserId = actorUserId;
        await reportCollection.ReplaceOneAsync(
            item => item.Id == report.Id,
            report,
            cancellationToken: ct);

        _flwMappingReceiptId = receiptId;
        _flwMappingProvenanceId = provenanceId;
        _flwMappingProvenanceHash = provenanceHash;
        _flwMappingBytesBefore = await CaptureP7MappingBytesAsync(ct);
    }

    private async Task<string> LoadFlowBranchIdAsync(CancellationToken ct)
    {
        var assignment = await RequireDatabase()
            .GetCollection<BsonDocument>("work_assignments")
            .Find(new BsonDocument("_id", ObjectId.Parse(Fixture().AssignmentId)))
            .SingleAsync(ct);
        return assignment["flowBranchId"].AsObjectId.ToString();
    }

    private async Task<string> CaptureP7MappingBytesAsync(CancellationToken ct)
    {
        var database = RequireDatabase();
        var documents = new List<BsonDocument>
        {
            await database.GetCollection<BsonDocument>("dynamic_flow_mapping_apply_receipts")
                .Find(new BsonDocument("_id", ObjectId.Parse(_flwMappingReceiptId!)))
                .SingleAsync(ct),
            await database.GetCollection<BsonDocument>("dynamic_flow_mapping_provenance")
                .Find(new BsonDocument("_id", ObjectId.Parse(_flwMappingProvenanceId!)))
                .SingleAsync(ct)
        };
        return HashBytes(documents.SelectMany(document => document.ToBson()).ToArray());
    }
}
