using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

internal static partial class P5MaterializationProbe
{
    private static async Task<ReconciledReadbackEvidence>
        AssertRuntimeReadReconciledReadbackAsync(
            ApiHarnessClient api,
            string token,
            IMongoDatabase database,
            string workId,
            string flowInstanceId,
            CancellationToken ct)
    {
        var instance = await database
            .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(item =>
                item.Id == flowInstanceId &&
                item.WorkId == workId &&
                !item.IsDeleted)
            .SingleAsync(ct);
        var response = await api.GetAsync(
            $"api/works/{workId}/dynamic-flows/instances/{flowInstanceId}/recovery",
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            "runtime reconciled recovery readback");
        var body = response.Json as JsonObject
                   ?? throw new InvalidOperationException(
                       "Runtime reconciled recovery response is not an object.");
        Require(
            ApiHarnessClient.RequiredString(body, "state") ==
            instance.State &&
            !ApiHarnessClient.RequiredBool(body, "recoveryRequired") &&
            ApiHarnessClient.RequiredString(body, "reasonCode") ==
            "DYNAMIC_FLOW_RUNTIME_RECONCILED" &&
            ApiHarnessClient.RequiredString(body, "reconcileStatus") ==
            "RECONCILED" &&
            ApiHarnessClient.RequiredString(body, "nextAction") ==
            "REFRESH" &&
            ReadLong(body, "expectedRevision") ==
            instance.Revision &&
            ApiHarnessClient.RequiredString(body, "revisionToken") ==
            DynamicFlowRuntimeReadContract.BuildInstanceRevisionToken(
                instance.Id,
                instance.Revision,
                instance.RuntimeRecoveryEpoch) &&
            !ApiHarnessClient.RequiredBool(body, "canRetry") &&
            !ApiHarnessClient.RequiredBool(body, "canReconcile"),
            "Runtime recovery endpoint did not expose the safe reconciled readback/revision contract.");
        var serialized = response.Json?.ToJsonString() ?? string.Empty;
        Require(
            !serialized.Contains("lastErrorCode", StringComparison.OrdinalIgnoreCase) &&
            !serialized.Contains("leaseId", StringComparison.OrdinalIgnoreCase) &&
            !serialized.Contains("payload", StringComparison.OrdinalIgnoreCase),
            "Runtime recovery readback exposed internal error, lease, or payload detail.");

        return new ReconciledReadbackEvidence(
            instance.State,
            "DYNAMIC_FLOW_RUNTIME_RECONCILED",
            "RECONCILED",
            instance.Revision,
            true);
    }

    private static async Task<RuntimeReadProbeResult> RunRuntimeReadScenarioAsync(
        Uri baseUri,
        string issuerToken,
        string actorPassword,
        IMongoDatabase database,
        ProbeFixture fixture,
        string flowInstanceId,
        string iterationRoot,
        CancellationToken ct)
    {
        var instances = database.GetCollection<DynamicFlowInstance>(
            "dynamic_flow_instances");
        var steps = database.GetCollection<DynamicFlowStepInstance>(
            "dynamic_flow_step_instances");
        var events = database.GetCollection<DynamicFlowRuntimeEvent>(
            "dynamic_flow_runtime_events");
        var users = database.GetCollection<AppUser>("users");
        var instance = await instances
            .Find(item =>
                item.Id == flowInstanceId &&
                item.WorkId == fixture.MultiUnitWorkId &&
                !item.IsDeleted)
            .SingleAsync(ct);
        var mongoSteps = await steps
            .Find(item =>
                item.FlowInstanceId == flowInstanceId &&
                !item.IsDeleted)
            .Sort(
                Builders<DynamicFlowStepInstance>.Sort
                    .Ascending(item => item.FlowStepId)
                    .Ascending(item => item.TargetUnitId)
                    .Ascending(item => item.AttemptNo)
                    .Ascending(item => item.Id))
            .ToListAsync(ct);
        Require(
            mongoSteps.Count == fixture.TargetUnitIds.Count,
            "Runtime read probe requires the converged three-branch fixture.");
        var reporterStep = mongoSteps[0];
        var reviewerStep = mongoSteps[1];
        Require(
            reporterStep.Id != reviewerStep.Id &&
            reporterStep.AssignmentId is not null &&
            reviewerStep.AssignmentId is not null,
            "Runtime read actor branches are not distinct or materialized.");

        var reporter = await users
            .Find(item =>
                reporterStep.ParticipantUserIds.Contains(item.Id) &&
                !item.IsDeleted)
            .SingleAsync(ct);
        var now = DateTime.UtcNow;
        var hasher = new PasswordHasher<AppUser>();
        reporter.PasswordHash = hasher.HashPassword(reporter, actorPassword);
        await users.ReplaceOneAsync(
            item => item.Id == reporter.Id && !item.IsDeleted,
            reporter,
            cancellationToken: ct);

        var reviewer = NewRuntimeReadActor(
            "p5_runtime_read_reviewer",
            "P5 Runtime Read Reviewer",
            instance.IssuerUnitId,
            instance.IssuerUserId,
            now);
        reviewer.PasswordHash = hasher.HashPassword(reviewer, actorPassword);
        var outsider = NewRuntimeReadActor(
            "p5_runtime_read_outsider",
            "P5 Runtime Read Outsider",
            instance.IssuerUnitId,
            instance.IssuerUserId,
            now);
        outsider.PasswordHash = hasher.HashPassword(outsider, actorPassword);
        await users.InsertManyAsync([reviewer, outsider], cancellationToken: ct);
        await SeedRuntimeReviewerScopeAsync(
            database,
            instance,
            reviewerStep,
            reviewer,
            now,
            ct);

        using var issuerApi = new ApiHarnessClient(baseUri);
        using var reporterApi = new ApiHarnessClient(baseUri);
        using var reviewerApi = new ApiHarnessClient(baseUri);
        using var outsiderApi = new ApiHarnessClient(baseUri);
        var reporterToken = await reporterApi.LoginAsync(
            reporter.Username,
            actorPassword,
            ct);
        var reviewerToken = await reviewerApi.LoginAsync(
            reviewer.Username,
            actorPassword,
            ct);
        var outsiderToken = await outsiderApi.LoginAsync(
            outsider.Username,
            actorPassword,
            ct);
        var instancePath =
            $"api/works/{fixture.MultiUnitWorkId}/dynamic-flows/instances/{flowInstanceId}";
        var preview = await AssertPagedPreflightPreviewAsync(
            issuerApi,
            issuerToken,
            database,
            fixture,
            instance,
            ct);
        const string outsiderCommandId = "p5-asn06-outsider-spoof-001";
        var outsiderBefore = await CaptureLaunchWriteCountsAsync(
            database,
            fixture.OutsiderWorkId,
            outsiderCommandId,
            ct);
        var forgedLaunchRequest = new
        {
            flowTemplateVersionId = fixture.T01VersionId,
            commandId = outsiderCommandId,
            targetUnitIds = fixture.TargetUnitIds.Take(1).ToArray(),
            periodKey = "2026-07-ASN06",
            scheduleIdentityJson =
                "{\"timezone\":\"Asia/Ho_Chi_Minh\",\"cadence\":\"once\",\"anchor\":\"2026-07-23\"}",
            issuerUserId = instance.IssuerUserId,
            issuerUnitId = instance.IssuerUnitId,
            actorUserId = instance.IssuerUserId,
            roleCodes = new[] { "ADMIN", "ISSUER" },
            canExecute = true,
            snapshotToken = new string('f', 64)
        };
        var outsiderPreflight = await outsiderApi.PostAsync(
            $"api/works/{fixture.OutsiderWorkId}/dynamic-flows/preflight",
            forgedLaunchRequest,
            outsiderToken,
            ct: ct);
        var outsiderConfirm = await outsiderApi.PostAsync(
            $"api/works/{fixture.OutsiderWorkId}/dynamic-flows/confirm",
            forgedLaunchRequest,
            outsiderToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            outsiderPreflight,
            HttpStatusCode.Forbidden,
            "ASN-FLOW-06 outsider/spoof preflight");
        ApiHarnessClient.ExpectStatus(
            outsiderConfirm,
            HttpStatusCode.Forbidden,
            "ASN-FLOW-06 outsider/spoof confirm");
        var outsiderAfter = await CaptureLaunchWriteCountsAsync(
            database,
            fixture.OutsiderWorkId,
            outsiderCommandId,
            ct);
        Require(
            outsiderBefore == outsiderAfter,
            "ASN-FLOW-06 outsider/spoof launch changed direct-Mongo business state.");

        var issuerList = await issuerApi.GetAsync(
            $"api/works/{fixture.MultiUnitWorkId}/dynamic-flows/instances?limit=200",
            issuerToken,
            ct: ct);
        var reporterList = await reporterApi.GetAsync(
            $"api/works/{fixture.MultiUnitWorkId}/dynamic-flows/instances?limit=200",
            reporterToken,
            ct: ct);
        var reviewerList = await reviewerApi.GetAsync(
            $"api/works/{fixture.MultiUnitWorkId}/dynamic-flows/instances?limit=200",
            reviewerToken,
            ct: ct);
        var outsiderList = await outsiderApi.GetAsync(
            $"api/works/{fixture.MultiUnitWorkId}/dynamic-flows/instances?limit=200",
            outsiderToken,
            ct: ct);
        AssertPage(
            issuerList,
            HttpStatusCode.OK,
            1,
            1,
            "issuer instance list");
        AssertPage(
            reporterList,
            HttpStatusCode.OK,
            0,
            0,
            "reporter instance list");
        AssertPage(
            reviewerList,
            HttpStatusCode.OK,
            0,
            0,
            "reviewer instance list");
        AssertPage(
            outsiderList,
            HttpStatusCode.OK,
            0,
            0,
            "outsider instance list");

        var issuerOverview = await issuerApi.GetAsync(
            instancePath,
            issuerToken,
            ct: ct);
        var reporterOverview = await reporterApi.GetAsync(
            instancePath,
            reporterToken,
            ct: ct);
        var reviewerOverview = await reviewerApi.GetAsync(
            instancePath,
            reviewerToken,
            ct: ct);
        AssertOverview(
            issuerOverview,
            instance,
            mongoSteps.Count,
            "ISSUER",
            "issuer overview");
        AssertOverview(
            reporterOverview,
            instance,
            1,
            "REPORTER",
            "reporter overview");
        AssertOverview(
            reviewerOverview,
            instance,
            1,
            "REVIEWER",
            "reviewer overview");

        var missingInstanceId = ObjectId.GenerateNewId().ToString();
        var outsiderHidden = await outsiderApi.GetAsync(
            instancePath,
            outsiderToken,
            ct: ct);
        var outsiderMissing = await outsiderApi.GetAsync(
            $"api/works/{fixture.MultiUnitWorkId}/dynamic-flows/instances/{missingInstanceId}",
            outsiderToken,
            ct: ct);
        AssertHiddenEqualsMissing(
            outsiderHidden,
            outsiderMissing,
            "outsider instance");

        var issuerStepPages = await ReadAllPagesAsync(
            issuerApi,
            $"{instancePath}/steps",
            issuerToken,
            limit: 1,
            "issuer steps",
            ct);
        Require(
            issuerStepPages.Items.Count == mongoSteps.Count &&
            issuerStepPages.Total == mongoSteps.Count,
            "Issuer step paging did not return the complete scoped set.");
        AssertStableFirstPage(
            issuerStepPages.FirstPage,
            issuerStepPages.RepeatedFirstPage,
            "issuer steps");
        for (var index = 0; index < mongoSteps.Count; index++)
        {
            AssertStepIdentity(
                issuerStepPages.Items[index],
                instance,
                mongoSteps[index],
                "ISSUER",
                $"issuer step {index + 1}");
        }
        var validIssuerStepCursor = issuerStepPages.FirstCursor
            ?? throw new InvalidOperationException(
                "Issuer step paging did not issue a continuation cursor.");
        var tamperIndex = validIssuerStepCursor.Length / 2;
        var tamperedCursor =
            validIssuerStepCursor[..tamperIndex] +
            (validIssuerStepCursor[tamperIndex] == 'a' ? "b" : "a") +
            validIssuerStepCursor[(tamperIndex + 1)..];
        var tamperedCursorResponse = await issuerApi.GetAsync(
            $"{instancePath}/steps?limit=1&cursor={Uri.EscapeDataString(tamperedCursor)}",
            issuerToken,
            ct: ct);
        Require(
            tamperedCursorResponse.StatusCode is
                HttpStatusCode.BadRequest or
                HttpStatusCode.Forbidden or
                HttpStatusCode.NotFound,
            "Tampered runtime cursor did not fail closed.");
        var crossActorCursorResponse = await reporterApi.GetAsync(
            $"{instancePath}/steps?limit=1&cursor={Uri.EscapeDataString(validIssuerStepCursor)}",
            reporterToken,
            ct: ct);
        Require(
            crossActorCursorResponse.StatusCode is
                HttpStatusCode.BadRequest or
                HttpStatusCode.Forbidden or
                HttpStatusCode.NotFound,
            "Issuer cursor was reusable across actor scope.");
        var crossQueryCursorResponse = await issuerApi.GetAsync(
            $"{instancePath}/timeline?limit=1&cursor={Uri.EscapeDataString(validIssuerStepCursor)}",
            issuerToken,
            ct: ct);
        Require(
            crossQueryCursorResponse.StatusCode is
                HttpStatusCode.BadRequest or
                HttpStatusCode.Forbidden or
                HttpStatusCode.NotFound,
            "Step cursor was reusable across runtime query scope.");

        var reporterSteps = await reporterApi.GetAsync(
            $"{instancePath}/steps?limit=200",
            reporterToken,
            ct: ct);
        var reporterStepRows = AssertPage(
            reporterSteps,
            HttpStatusCode.OK,
            1,
            1,
            "reporter steps");
        AssertStepIdentity(
            RequiredItem(reporterStepRows, 0, "reporter steps"),
            instance,
            reporterStep,
            "REPORTER",
            "reporter step");

        var reviewerSteps = await reviewerApi.GetAsync(
            $"{instancePath}/steps?limit=200",
            reviewerToken,
            ct: ct);
        var reviewerStepRows = AssertPage(
            reviewerSteps,
            HttpStatusCode.OK,
            1,
            1,
            "reviewer steps");
        AssertStepIdentity(
            RequiredItem(reviewerStepRows, 0, "reviewer steps"),
            instance,
            reviewerStep,
            "REVIEWER",
            "reviewer step");

        var outsiderSteps = await outsiderApi.GetAsync(
            $"{instancePath}/steps?limit=200",
            outsiderToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            outsiderSteps,
            HttpStatusCode.NotFound,
            "outsider steps");

        var mongoReporterInbox = await steps
            .Find(item =>
                item.ParticipantUserIds.Contains(reporter.Id) &&
                item.State == DynamicFlowStepStates.Assigned &&
                !item.IsDeleted)
            .Sort(
                Builders<DynamicFlowStepInstance>.Sort
                    .Descending(item => item.UpdatedAtUtc)
                    .Descending(item => item.Id))
            .ToListAsync(ct);
        var reporterInbox = await ReadAllPagesAsync(
            reporterApi,
            "api/dynamic-flows/inbox?state=ASSIGNED",
            reporterToken,
            limit: 1,
            "reporter inbox",
            ct);
        Require(
            reporterInbox.Total == mongoReporterInbox.Count &&
            reporterInbox.Items
                .Select(item => ReadString(item, "stepInstanceId"))
                .SequenceEqual(
                    mongoReporterInbox.Select(item => item.Id),
                    StringComparer.Ordinal) &&
            reporterInbox.Items.Any(item =>
                ReadString(item, "stepInstanceId") == reporterStep.Id),
            "Reporter inbox does not match the direct Mongo participant set/order.");
        var reviewerInbox = await reviewerApi.GetAsync(
            "api/dynamic-flows/inbox?state=ASSIGNED&limit=200",
            reviewerToken,
            ct: ct);
        AssertPage(
            reviewerInbox,
            HttpStatusCode.OK,
            0,
            0,
            "reviewer inbox");
        var outsiderInbox = await outsiderApi.GetAsync(
            "api/dynamic-flows/inbox?state=ASSIGNED&limit=200",
            outsiderToken,
            ct: ct);
        AssertPage(
            outsiderInbox,
            HttpStatusCode.OK,
            0,
            0,
            "outsider inbox");

        var mongoEvents = await events
            .Find(item => item.FlowInstanceId == flowInstanceId)
            .SortBy(item => item.Sequence)
            .ToListAsync(ct);
        Require(mongoEvents.Count > 1, "Runtime read timeline fixture is empty.");
        var issuerTimeline = await ReadAllPagesAsync(
            issuerApi,
            $"{instancePath}/timeline",
            issuerToken,
            limit: 1,
            "issuer timeline",
            ct);
        AssertStableFirstPage(
            issuerTimeline.FirstPage,
            issuerTimeline.RepeatedFirstPage,
            "issuer timeline");
        AssertTimelineIdentity(
            issuerTimeline.Items,
            mongoEvents,
            mongoSteps,
            instance.WorkId,
            "issuer timeline");

        var reporterExpectedEvents = mongoEvents
            .Where(item =>
                item.StepInstanceId is null ||
                item.StepInstanceId == reporterStep.Id)
            .ToArray();
        var reporterTimeline = await ReadAllPagesAsync(
            reporterApi,
            $"{instancePath}/timeline",
            reporterToken,
            limit: 1,
            "reporter timeline",
            ct);
        AssertTimelineIdentity(
            reporterTimeline.Items,
            reporterExpectedEvents,
            mongoSteps,
            instance.WorkId,
            "reporter timeline");
        AssertNoSiblingReferences(
            reporterTimeline.Items,
            mongoSteps.Where(item => item.Id != reporterStep.Id),
            "reporter timeline");

        var reviewerExpectedEvents = mongoEvents
            .Where(item =>
                item.StepInstanceId is null ||
                item.StepInstanceId == reviewerStep.Id)
            .ToArray();
        var reviewerTimeline = await ReadAllPagesAsync(
            reviewerApi,
            $"{instancePath}/timeline",
            reviewerToken,
            limit: 1,
            "reviewer timeline",
            ct);
        AssertTimelineIdentity(
            reviewerTimeline.Items,
            reviewerExpectedEvents,
            mongoSteps,
            instance.WorkId,
            "reviewer timeline");
        AssertNoSiblingReferences(
            reviewerTimeline.Items,
            mongoSteps.Where(item => item.Id != reviewerStep.Id),
            "reviewer timeline");

        var outsiderTimelineHidden = await outsiderApi.GetAsync(
            $"{instancePath}/timeline?limit=1",
            outsiderToken,
            ct: ct);
        var outsiderTimelineMissing = await outsiderApi.GetAsync(
            $"api/works/{fixture.MultiUnitWorkId}/dynamic-flows/instances/{missingInstanceId}/timeline?limit=1",
            outsiderToken,
            ct: ct);
        AssertHiddenEqualsMissing(
            outsiderTimelineHidden,
            outsiderTimelineMissing,
            "outsider timeline");

        var issuerRecovery = await issuerApi.GetAsync(
            $"{instancePath}/recovery",
            issuerToken,
            ct: ct);
        var reporterRecovery = await reporterApi.GetAsync(
            $"{instancePath}/recovery",
            reporterToken,
            ct: ct);
        var reviewerRecovery = await reviewerApi.GetAsync(
            $"{instancePath}/recovery",
            reviewerToken,
            ct: ct);
        AssertHealthyRecovery(issuerRecovery, instance, "issuer recovery");
        AssertHealthyRecovery(reporterRecovery, instance, "reporter recovery");
        AssertHealthyRecovery(reviewerRecovery, instance, "reviewer recovery");
        var outsiderRecoveryHidden = await outsiderApi.GetAsync(
            $"{instancePath}/recovery",
            outsiderToken,
            ct: ct);
        var outsiderRecoveryMissing = await outsiderApi.GetAsync(
            $"api/works/{fixture.MultiUnitWorkId}/dynamic-flows/instances/{missingInstanceId}/recovery",
            outsiderToken,
            ct: ct);
        AssertHiddenEqualsMissing(
            outsiderRecoveryHidden,
            outsiderRecoveryMissing,
            "outsider recovery");

        var matrix = new[]
        {
            new
            {
                actor = "issuer",
                actorId = instance.IssuerUserId,
                listTotal = 1L,
                visibleStepIds = mongoSteps.Select(item => item.Id).ToArray(),
                timelineEventIds =
                    mongoEvents.Select(item => item.Id).ToArray(),
                recoveryState = instance.State
            },
            new
            {
                actor = "reporter",
                actorId = reporter.Id,
                listTotal = 0L,
                visibleStepIds = new[] { reporterStep.Id },
                timelineEventIds =
                    reporterExpectedEvents.Select(item => item.Id).ToArray(),
                recoveryState = instance.State
            },
            new
            {
                actor = "reviewer",
                actorId = reviewer.Id,
                listTotal = 0L,
                visibleStepIds = new[] { reviewerStep.Id },
                timelineEventIds =
                    reviewerExpectedEvents.Select(item => item.Id).ToArray(),
                recoveryState = instance.State
            },
            new
            {
                actor = "outsider",
                actorId = outsider.Id,
                listTotal = 0L,
                visibleStepIds = Array.Empty<string>(),
                timelineEventIds = Array.Empty<string>(),
                recoveryState = "HIDDEN"
            }
        };
        var caseEvidence = new
        {
            caseId = "ASN-FLOW-06+P5-READ-ACTOR-MATRIX-IDENTITY-PAGING",
            verdict = "PASS",
            workId = instance.WorkId,
            flowInstanceId = instance.Id,
            instanceState = instance.State,
            instanceRevision = instance.Revision,
            actorMatrix = matrix,
            hiddenEqualsMissing = new
            {
                instance = true,
                timeline = true,
                recovery = true
            },
            outsiderLaunch = new
            {
                preflightHttpStatus = (int)outsiderPreflight.StatusCode,
                confirmHttpStatus = (int)outsiderConfirm.StatusCode,
                spoofedClientAuthorityIgnored = true,
                businessWritePerformed = false,
                before = outsiderBefore,
                after = outsiderAfter
            },
            paging = new
            {
                issuerStepPageCount = issuerStepPages.PageCount,
                issuerStepTotal = issuerStepPages.Total,
                issuerTimelinePageCount = issuerTimeline.PageCount,
                issuerTimelineTotal = issuerTimeline.Total,
                reporterInboxPageCount = reporterInbox.PageCount,
                reporterInboxTotal = reporterInbox.Total,
                stableRepeatedFirstPages = true,
                tamperedCursorRejected = true,
                crossActorCursorRejected = true,
                crossQueryCursorRejected = true
            },
            directMongoIdentity = new
            {
                preview,
                instance = true,
                steps = mongoSteps.Count,
                events = mongoEvents.Count,
                revision = instance.Revision
            },
            recoveryReadback = new
            {
                state = instance.State,
                reasonCode = "DYNAMIC_FLOW_RUNTIME_HEALTHY",
                recoveryRequired = false
            }
        };
        var exchangeEvidence = new
        {
            phase = "runtime-read-api",
            issuer = issuerApi.Exchanges,
            reporter = reporterApi.Exchanges,
            reviewer = reviewerApi.Exchanges,
            outsider = outsiderApi.Exchanges
        };
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "p5-runtime-read-ledger.json"),
            caseEvidence,
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "p5-runtime-read-api-exchanges.json"),
            exchangeEvidence,
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "p5-runtime-read-result.json"),
            new
            {
                verdict = "PASS",
                caseId = "ASN-FLOW-06+P5-READ-ACTOR-MATRIX-IDENTITY-PAGING",
                flowInstanceId,
                completedAtUtc = DateTime.UtcNow
            },
            ct);
        return new RuntimeReadProbeResult(caseEvidence, exchangeEvidence);
    }

    private static async Task<LaunchWriteCounts> CaptureLaunchWriteCountsAsync(
        IMongoDatabase database,
        string workId,
        string commandId,
        CancellationToken ct)
    {
        var receipts = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                "dynamic_flow_runtime_command_receipts")
            .CountDocumentsAsync(
                item => item.CommandId == commandId,
                cancellationToken: ct);
        var instances = await database
            .GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .CountDocumentsAsync(
                item => item.WorkId == workId,
                cancellationToken: ct);
        var assignments = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .CountDocumentsAsync(
                item => item.WorkId == workId,
                cancellationToken: ct);
        var bindings = await database
            .GetCollection<WorkTemplateAssignee>("work_template_assignees")
            .CountDocumentsAsync(
                item => item.WorkId == workId,
                cancellationToken: ct);
        var steps = await database
            .GetCollection<BsonDocument>("dynamic_flow_step_instances")
            .CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty,
                cancellationToken: ct);
        var events = await database
            .GetCollection<BsonDocument>("dynamic_flow_runtime_events")
            .CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty,
                cancellationToken: ct);
        var outbox = await database
            .GetCollection<BsonDocument>("dynamic_flow_runtime_outbox")
            .CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty,
                cancellationToken: ct);
        return new LaunchWriteCounts(
            receipts,
            instances,
            steps,
            events,
            outbox,
            assignments,
            bindings);
    }

    private static AppUser NewRuntimeReadActor(
        string username,
        string fullName,
        string unitId,
        string createdByUserId,
        DateTime now)
        => new()
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Username = username,
            PasswordHash = string.Empty,
            FullName = fullName,
            UnitId = unitId,
            PositionCode = "SPECIALIST",
            Roles = [],
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = createdByUserId,
            UpdatedByUserId = createdByUserId,
            IsDeleted = false
        };

    private static async Task SeedRuntimeReviewerScopeAsync(
        IMongoDatabase database,
        DynamicFlowInstance instance,
        DynamicFlowStepInstance reviewerStep,
        AppUser reviewer,
        DateTime now,
        CancellationToken ct)
    {
        var assignmentId = reviewerStep.AssignmentId
            ?? throw new InvalidOperationException(
                "Reviewer step lacks an assignment.");
        await database.GetCollection<DocRole>("doc_roles").InsertOneAsync(
            new DocRole
            {
                Id = ObjectId.GenerateNewId().ToString(),
                DocType = DocType.WORK_ASSIGNMENT,
                DocId = assignmentId,
                UserId = reviewer.Id,
                Role = DocRoleType.ASSIGNER,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = instance.IssuerUserId,
                UpdatedByUserId = instance.IssuerUserId,
                IsDeleted = false
            },
            cancellationToken: ct);
        await database
            .GetCollection<ReviewAssignmentSummaryDocRole>(
                "review_assignment_summary_doc_roles")
            .InsertOneAsync(
                new ReviewAssignmentSummaryDocRole
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    DocType = DocType.WORK_ASSIGNMENT,
                    DocId = assignmentId,
                    UserId = reviewer.Id,
                    Roles = [DocRoleType.ASSIGNER],
                    WorkId = instance.WorkId,
                    AssignmentId = assignmentId,
                    ReviewerUserId = reviewer.Id,
                    DynamicExcelId = ObjectId.Empty.ToString(),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = instance.IssuerUserId,
                    UpdatedByUserId = instance.IssuerUserId,
                    IsDeleted = false
                },
                cancellationToken: ct);
    }

    private static async Task<object> AssertPagedPreflightPreviewAsync(
        ApiHarnessClient api,
        string issuerToken,
        IMongoDatabase database,
        ProbeFixture fixture,
        DynamicFlowInstance instance,
        CancellationToken ct)
    {
        const string commandId = "p5-runtime-read-preview";
        var receipts = database.GetCollection<DynamicFlowRuntimeCommandReceipt>(
            "dynamic_flow_runtime_command_receipts");
        var beforeReceipts = await receipts.CountDocumentsAsync(
            item => item.CommandId == commandId,
            cancellationToken: ct);
        var request = new
        {
            flowTemplateVersionId = fixture.T01VersionId,
            commandId,
            targetUnitIds = fixture.TargetUnitIds,
            periodKey = "2026-07-READ-PREVIEW",
            scheduleIdentityJson = "{}"
        };
        var path =
            $"api/works/{fixture.MultiUnitWorkId}/dynamic-flows/preflight?limit=1";
        var first = await api.PostAsync(
            path,
            request,
            issuerToken,
            ct: ct);
        var repeated = await api.PostAsync(
            path,
            request,
            issuerToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            first,
            HttpStatusCode.OK,
            "runtime read paged preflight");
        ApiHarnessClient.ExpectStatus(
            repeated,
            HttpStatusCode.OK,
            "runtime read repeated paged preflight");
        Require(
            first.Body == repeated.Body,
            "Repeated launch preview first page changed.");
        var firstBody = ApiHarnessClient.RequiredObject(
            first.Json,
            "runtime read paged preflight");
        var firstTargets = ApiHarnessClient.RequiredArray(
            firstBody["targets"],
            "runtime read paged preflight targets");
        Require(
            firstTargets.Count == 1 &&
            ReadLong(firstBody, "targetTotal") ==
            fixture.TargetUnitIds.Count &&
            ReadInt(firstBody, "targetLimit") == 1 &&
            ApiHarnessClient.RequiredBool(firstBody, "targetHasMore"),
            "Launch preview first target page metadata is incorrect.");
        var cursor = ReadString(firstBody, "targetNextCursor");
        var targetIds = new List<string>
        {
            ReadString(
                RequiredItem(
                    firstTargets,
                    0,
                    "runtime read paged preflight targets"),
                "targetUnitId")
        };
        while (!string.IsNullOrWhiteSpace(cursor))
        {
            var page = await api.PostAsync(
                $"{path}&cursor={Uri.EscapeDataString(cursor)}",
                request,
                issuerToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                page,
                HttpStatusCode.OK,
                "runtime read paged preflight continuation");
            var pageBody = ApiHarnessClient.RequiredObject(
                page.Json,
                "runtime read paged preflight continuation");
            Require(
                ReadLong(pageBody, "targetTotal") ==
                fixture.TargetUnitIds.Count,
                "Launch preview continuation total changed.");
            var pageTargets = ApiHarnessClient.RequiredArray(
                pageBody["targets"],
                "runtime read paged preflight continuation targets");
            Require(
                pageTargets.Count == 1,
                "Launch preview continuation page size changed.");
            targetIds.Add(
                ReadString(
                    RequiredItem(
                        pageTargets,
                        0,
                        "runtime read paged preflight continuation targets"),
                    "targetUnitId"));
            cursor = ReadNullableString(pageBody, "targetNextCursor");
        }

        var flowPin = ApiHarnessClient.RequiredObject(
            firstBody["flowPin"],
            "runtime read paged preflight flow pin");
        Require(
            ReadString(firstBody, "workId") == instance.WorkId &&
            ReadString(flowPin, "flowTemplateId") ==
            instance.FlowTemplateId &&
            ReadString(flowPin, "flowTemplateVersionId") ==
            instance.FlowTemplateVersionId &&
            ReadInt(flowPin, "flowTemplateVersionNo") ==
            instance.FlowTemplateVersionNo &&
            ReadString(flowPin, "payloadHash") ==
            instance.FlowPayloadHash &&
            targetIds.SequenceEqual(
                fixture.TargetUnitIds.OrderBy(
                    value => value,
                    StringComparer.Ordinal),
                StringComparer.Ordinal),
            "Launch preview did not preserve direct Mongo flow/target identity.");
        var afterReceipts = await receipts.CountDocumentsAsync(
            item => item.CommandId == commandId,
            cancellationToken: ct);
        Require(
            beforeReceipts == 0 && afterReceipts == 0,
            "Paged launch preview wrote a runtime command receipt.");
        return new
        {
            targetTotal = targetIds.Count,
            pageCount = targetIds.Count,
            stableRepeatedFirstPage = true,
            zeroWrite = true,
            flowIdentityMatched = true
        };
    }

    private static void AssertOverview(
        ApiHarnessResponse response,
        DynamicFlowInstance expected,
        long expectedVisibleSteps,
        string expectedScope,
        string context)
    {
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, context);
        var body = ApiHarnessClient.RequiredObject(response.Json, context);
        Require(
            ReadString(body, "workId") == expected.WorkId &&
            ReadString(body, "flowInstanceId") == expected.Id &&
            ReadString(body, "flowTemplateId") == expected.FlowTemplateId &&
            ReadString(body, "flowTemplateVersionId") ==
            expected.FlowTemplateVersionId &&
            ReadInt(body, "flowTemplateVersionNo") ==
            expected.FlowTemplateVersionNo &&
            ReadString(body, "entryFlowStepId") ==
            expected.EntryFlowStepId &&
            ReadString(body, "periodKey") == expected.PeriodKey &&
            ReadString(body, "scheduleIdentityHash") ==
            expected.ScheduleIdentityHash &&
            ReadString(body, "participantSnapshotId") ==
            expected.ParticipantSnapshotId &&
            ReadString(body, "state") == expected.State &&
            ReadLong(body, "revision") == expected.Revision &&
            ReadLong(body, "runtimeRecoveryEpoch") ==
            expected.RuntimeRecoveryEpoch &&
            ReadLong(body, "visibleStepCount") == expectedVisibleSteps,
            $"{context} does not match direct Mongo identity/revision.");
        var scopes = ApiHarnessClient.RequiredArray(
            body["visibilityScopes"],
            $"{context} visibility scopes");
        Require(
            scopes.Any(value =>
                value?.GetValue<string>() == expectedScope),
            $"{context} lacks scope {expectedScope}.");
        var capabilities = ApiHarnessClient.RequiredObject(
            body["capabilities"],
            $"{context} capabilities");
        Require(
            ReadLong(capabilities, "expectedRevision") == expected.Revision &&
            ApiHarnessClient.RequiredBool(capabilities, "canViewOverview") &&
            ApiHarnessClient.RequiredBool(capabilities, "canViewTimeline"),
            $"{context} capabilities/revision are not server-derived.");
    }

    private static void AssertStepIdentity(
        JsonObject actual,
        DynamicFlowInstance instance,
        DynamicFlowStepInstance expected,
        string expectedScope,
        string context)
    {
        Require(
            ReadString(actual, "workId") == instance.WorkId &&
            ReadString(actual, "flowInstanceId") == instance.Id &&
            ReadString(actual, "flowStepDefinitionId") ==
            expected.FlowStepId &&
            ReadString(actual, "flowStepCode") == expected.FlowStepCode &&
            ReadInt(actual, "stepOrder") == expected.StepOrder &&
            ReadString(actual, "stepInstanceId") == expected.Id &&
            ReadString(actual, "branchId") == expected.BranchId &&
            ReadInt(actual, "attemptNo") == expected.AttemptNo &&
            ReadString(actual, "targetUnitId") == expected.TargetUnitId &&
            ReadNullableString(actual, "assignmentId") ==
            expected.AssignmentId &&
            ReadNullableString(actual, "reportId") == expected.ReportId &&
            ReadString(actual, "formNodeId") == expected.FormNodeId &&
            ReadString(actual, "formFamilyId") == expected.FormFamilyId &&
            ReadString(actual, "formVersionId") == expected.FormVersionId &&
            ReadInt(actual, "formVersionNo") == expected.FormVersionNo &&
            ReadString(actual, "formSchemaHash") ==
            expected.FormSchemaHash &&
            ReadString(actual, "formSnapshotHash") ==
            expected.FormSnapshotHash &&
            ReadString(actual, "state") == expected.State &&
            ReadLong(actual, "revision") == expected.Revision,
            $"{context} does not match direct Mongo identity/revision.");
        var scopes = ApiHarnessClient.RequiredArray(
            actual["visibilityScopes"],
            $"{context} visibility scopes");
        Require(
            scopes.Any(value =>
                value?.GetValue<string>() == expectedScope),
            $"{context} lacks scope {expectedScope}.");
        var capabilities = ApiHarnessClient.RequiredObject(
            actual["capabilities"],
            $"{context} capabilities");
        Require(
            ReadLong(capabilities, "expectedRevision") == expected.Revision,
            $"{context} capability revision mismatch.");
    }

    private static async Task<PagedReadObservation> ReadAllPagesAsync(
        ApiHarnessClient api,
        string path,
        string token,
        int limit,
        string context,
        CancellationToken ct)
    {
        var items = new List<JsonObject>();
        var separator = path.Contains('?')
            ? "&"
            : "?";
        var first = await api.GetAsync(
            $"{path}{separator}limit={limit}",
            token,
            ct: ct);
        var repeatedFirst = await api.GetAsync(
            $"{path}{separator}limit={limit}",
            token,
            ct: ct);
        var firstItems = AssertPage(
            first,
            HttpStatusCode.OK,
            expectedItemCount: null,
            expectedTotal: null,
            context);
        AssertPage(
            repeatedFirst,
            HttpStatusCode.OK,
            firstItems.Count,
            ReadLong(
                ApiHarnessClient.RequiredObject(first.Json, context),
                "total"),
            $"{context} repeated first page");
        var firstBody = ApiHarnessClient.RequiredObject(first.Json, context);
        var total = ReadLong(firstBody, "total");
        AppendItems(items, firstItems, context);
        var cursor = ReadNullableString(firstBody, "nextCursor");
        var pageCount = 1;
        while (!string.IsNullOrWhiteSpace(cursor))
        {
            Require(pageCount < 32, $"{context} cursor did not terminate.");
            var response = await api.GetAsync(
                $"{path}{separator}limit={limit}&cursor={Uri.EscapeDataString(cursor)}",
                token,
                ct: ct);
            var page = AssertPage(
                response,
                HttpStatusCode.OK,
                expectedItemCount: null,
                expectedTotal: total,
                $"{context} page {pageCount + 1}");
            AppendItems(items, page, context);
            cursor = ReadNullableString(
                ApiHarnessClient.RequiredObject(
                    response.Json,
                    $"{context} page {pageCount + 1}"),
                "nextCursor");
            pageCount++;
        }

        Require(
            items.Select(item => ReadIdentity(item)).Distinct(
                    StringComparer.Ordinal)
                .Count() == items.Count,
            $"{context} returned a duplicate item across cursor pages.");
        return new PagedReadObservation(
            items,
            total,
            pageCount,
            first.Body,
            repeatedFirst.Body,
            ReadNullableString(firstBody, "nextCursor"));
    }

    private static JsonArray AssertPage(
        ApiHarnessResponse response,
        HttpStatusCode expectedStatus,
        int? expectedItemCount,
        long? expectedTotal,
        string context)
    {
        ApiHarnessClient.ExpectStatus(response, expectedStatus, context);
        var body = ApiHarnessClient.RequiredObject(response.Json, context);
        var items = ApiHarnessClient.RequiredArray(
            body["items"],
            $"{context} items");
        if (expectedItemCount.HasValue)
        {
            Require(
                items.Count == expectedItemCount.Value,
                $"{context} expected {expectedItemCount.Value} items, got {items.Count}.");
        }
        if (expectedTotal.HasValue)
        {
            Require(
                ReadLong(body, "total") == expectedTotal.Value,
                $"{context} expected total {expectedTotal.Value}, got {ReadLong(body, "total")}.");
        }

        return items;
    }

    private static void AssertStableFirstPage(
        string first,
        string repeated,
        string context)
    {
        Require(
            string.Equals(first, repeated, StringComparison.Ordinal),
            $"{context} repeated first page changed.");
    }

    private static void AssertTimelineIdentity(
        IReadOnlyList<JsonObject> actual,
        IReadOnlyList<DynamicFlowRuntimeEvent> expected,
        IReadOnlyList<DynamicFlowStepInstance> steps,
        string expectedWorkId,
        string context)
    {
        var stepById = steps.ToDictionary(
            item => item.Id,
            StringComparer.Ordinal);
        Require(
            actual.Count == expected.Count,
            $"{context} event count mismatch.");
        for (var index = 0; index < expected.Count; index++)
        {
            var row = actual[index];
            var item = expected[index];
            var expectedStep =
                item.StepInstanceId is not null &&
                stepById.TryGetValue(item.StepInstanceId, out var step)
                    ? step
                    : null;
            Require(
                ReadString(row, "workId") == expectedWorkId &&
                ReadString(row, "flowInstanceId") == item.FlowInstanceId &&
                ReadString(row, "eventId") == item.Id &&
                ReadNullableString(row, "stepInstanceId") ==
                item.StepInstanceId &&
                ReadNullableString(row, "flowStepDefinitionId") ==
                expectedStep?.FlowStepId &&
                ReadNullableString(row, "branchId") ==
                expectedStep?.BranchId &&
                ReadNullableInt(row, "attemptNo") ==
                expectedStep?.AttemptNo &&
                ReadNullableString(row, "targetUnitId") ==
                expectedStep?.TargetUnitId &&
                ReadNullableString(row, "assignmentId") ==
                expectedStep?.AssignmentId &&
                ReadNullableString(row, "reportId") ==
                expectedStep?.ReportId &&
                ReadNullableString(row, "formVersionId") ==
                expectedStep?.FormVersionId &&
                ReadNullableInt(row, "formVersionNo") ==
                expectedStep?.FormVersionNo &&
                ReadLong(row, "sequence") == item.Sequence &&
                ReadString(row, "eventType") == item.EventType &&
                ReadString(row, "commandId") == item.CommandId &&
                ReadNullableString(row, "fromState") == item.FromState &&
                ReadNullableString(row, "toState") == item.ToState &&
                ReadNullableLong(row, "fromRevision") ==
                item.FromRevision &&
                ReadNullableLong(row, "toRevision") ==
                item.ToRevision,
                $"{context} event {index + 1} identity/order mismatch.");
            Require(
                !row.ContainsKey("payload") &&
                !row.ContainsKey("payloadHash") &&
                !row.ContainsKey("visibleUnitIds") &&
                !row.ContainsKey("sourceEventKey"),
                $"{context} leaked internal event payload/visibility metadata.");
        }
    }

    private static void AssertNoSiblingReferences(
        IEnumerable<JsonObject> events,
        IEnumerable<DynamicFlowStepInstance> siblingSteps,
        string context)
    {
        var forbidden = siblingSteps
            .SelectMany(step => new[]
            {
                step.Id,
                step.AssignmentId,
                step.ReportId
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in events)
        {
            var refs = ApiHarnessClient.RequiredArray(
                item["affectedRefs"],
                $"{context} affected refs");
            Require(
                refs
                    .Select(value => value?.GetValue<string>())
                    .Where(value => value is not null)
                    .All(value => !forbidden.Any(id =>
                        value!.Contains(id, StringComparison.Ordinal))),
                $"{context} leaked a sibling branch reference.");
        }
    }

    private static void AssertHealthyRecovery(
        ApiHarnessResponse response,
        DynamicFlowInstance instance,
        string context)
    {
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, context);
        var body = ApiHarnessClient.RequiredObject(response.Json, context);
        Require(
            ReadString(body, "state") == instance.State &&
            !ApiHarnessClient.RequiredBool(body, "recoveryRequired") &&
            ReadString(body, "reasonCode") ==
            "DYNAMIC_FLOW_RUNTIME_HEALTHY" &&
            ReadString(body, "nextAction") == "NONE" &&
            ReadLong(body, "expectedRevision") == instance.Revision &&
            ReadString(body, "revisionToken") ==
            DynamicFlowRuntimeReadContract.BuildInstanceRevisionToken(
                instance.Id,
                instance.Revision,
                instance.RuntimeRecoveryEpoch) &&
            !ApiHarnessClient.RequiredBool(body, "canRetry") &&
            !ApiHarnessClient.RequiredBool(body, "canReconcile"),
            $"{context} did not read back the healthy Mongo state.");
        Require(
            !body.ContainsKey("lastErrorCode") &&
            !body.ContainsKey("leaseId") &&
            !body.ContainsKey("diagnostics") &&
            !body.ContainsKey("stackTrace"),
            $"{context} leaked recovery diagnostics.");
    }

    private static void AssertHiddenEqualsMissing(
        ApiHarnessResponse hidden,
        ApiHarnessResponse missing,
        string context)
    {
        ApiHarnessClient.ExpectStatus(
            hidden,
            HttpStatusCode.NotFound,
            $"{context} hidden");
        ApiHarnessClient.ExpectStatus(
            missing,
            HttpStatusCode.NotFound,
            $"{context} missing");
        Require(
            NormalizeError(hidden.Json) == NormalizeError(missing.Json),
            $"{context} hidden and missing error bodies differ.");
    }

    private static string NormalizeError(JsonNode? node)
    {
        var clone = node?.DeepClone() as JsonObject
            ?? throw new InvalidOperationException(
                "Expected a JSON error object.");
        clone.Remove("traceId");
        return clone.ToJsonString();
    }

    private static void AppendItems(
        ICollection<JsonObject> target,
        JsonArray source,
        string context)
    {
        foreach (var node in source)
        {
            target.Add(
                node as JsonObject
                ?? throw new InvalidOperationException(
                    $"{context} item is not an object."));
        }
    }

    private static JsonObject RequiredItem(
        JsonArray array,
        int index,
        string context)
        => array[index] as JsonObject
           ?? throw new InvalidOperationException(
               $"{context} item {index} is not an object.");

    private static string ReadIdentity(JsonObject item)
        => ReadNullableString(item, "stepInstanceId")
           ?? ReadNullableString(item, "eventId")
           ?? ReadNullableString(item, "flowInstanceId")
           ?? throw new InvalidOperationException(
               "Paged item lacks a stable identity.");

    private static string ReadString(JsonObject value, string property)
        => value[property]?.GetValue<string>()
           ?? throw new InvalidOperationException(
               $"JSON property '{property}' is missing.");

    private static string? ReadNullableString(
        JsonObject value,
        string property)
        => value[property] is null
            ? null
            : value[property]!.GetValue<string>();

    private static int ReadInt(JsonObject value, string property)
        => value[property]?.GetValue<int>()
           ?? throw new InvalidOperationException(
               $"JSON property '{property}' is missing.");

    private static int? ReadNullableInt(
        JsonObject value,
        string property)
        => value[property] is null
            ? null
            : value[property]!.GetValue<int>();

    private static long ReadLong(JsonObject value, string property)
        => value[property]?.GetValue<long>()
           ?? throw new InvalidOperationException(
               $"JSON property '{property}' is missing.");

    private static long? ReadNullableLong(
        JsonObject value,
        string property)
        => value[property] is null
            ? null
            : value[property]!.GetValue<long>();

    private sealed record PagedReadObservation(
        IReadOnlyList<JsonObject> Items,
        long Total,
        int PageCount,
        string FirstPage,
        string RepeatedFirstPage,
        string? FirstCursor);

    private sealed record RuntimeReadProbeResult(
        object CaseEvidence,
        object ExchangeEvidence);

    private sealed record LaunchWriteCounts(
        long ReceiptCount,
        long InstanceCount,
        long StepCount,
        long EventCount,
        long OutboxCount,
        long AssignmentCount,
        long BindingCount);

    private sealed record ReconciledReadbackEvidence(
        string State,
        string ReasonCode,
        string ReconcileStatus,
        long Revision,
        bool InternalDetailRedacted);
}
