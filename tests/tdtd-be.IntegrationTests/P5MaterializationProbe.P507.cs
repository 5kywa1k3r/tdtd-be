using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

internal static partial class P5MaterializationProbe
{
    private static readonly string[] P507BusinessCollections =
    [
        "works",
        "dynamic_flow_instances",
        "dynamic_flow_step_instances",
        "dynamic_flow_participant_snapshots",
        "dynamic_flow_runtime_command_receipts",
        "dynamic_flow_runtime_events",
        "dynamic_flow_runtime_outbox",
        "work_assignments",
        "work_template_assignees",
        "work_report_periods",
        "work_assignment_queue",
        "doc_roles",
        "assignment_list_doc_roles",
        "my_report_period_list_doc_roles",
        "review_report_list_doc_roles",
        "review_assignment_summary_doc_roles",
        "work_assignment_report",
        "work_assignment_report_sections"
    ];

    private static async Task<P507SupplementalObservation>
        RunP507SupplementalGateAsync(
            ApiHarnessClient api,
            string adminToken,
            string actorPassword,
            IMongoDatabase database,
            ProbeFixture fixture,
            string t01InstanceId,
            CancellationToken ct)
    {
        var target = fixture.TargetUnitIds.Take(1).ToArray();

        const string changedCommandId = "p507-changed-replay-001";
        const string changedPeriod = "2026-07-P507-CHANGED";
        var changedLaunch = await LaunchAsync(
            api,
            adminToken,
            fixture,
            fixture.ChangedReplayWorkId,
            fixture.T01VersionId,
            changedCommandId,
            target,
            changedPeriod,
            ct);
        RequireStatus(
            changedLaunch.Confirm,
            "SUCCEEDED",
            "P5-07 changed-replay seed launch");
        await ValidateConvergedAsync(
            database,
            fixture,
            fixture.ChangedReplayWorkId,
            changedCommandId,
            fixture.T01VersionId,
            "FLOW-T01",
            target,
            changedPeriod,
            false,
            ct);
        var beforeChangedReplay = await CaptureP507BusinessStateHashAsync(database, ct);
        var changedReplay = await ConfirmAsync(
            api,
            adminToken,
            fixture.ChangedReplayWorkId,
            fixture.T01VersionId,
            changedCommandId,
            target,
            $"{changedPeriod}-DIFFERENT",
            changedLaunch.SnapshotToken,
            ct);
        AssertConflict(
            changedReplay,
            "DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT",
            expectedReason: null,
            "changed launch replay");
        var afterChangedReplay = await CaptureP507BusinessStateHashAsync(database, ct);
        Require(
            beforeChangedReplay == afterChangedReplay,
            "Changed replay modified direct-Mongo business state.");

        const string staleCommandId = "p507-stale-snapshot-001";
        const string stalePeriod = "2026-07-P507-STALE";
        var stalePreflight = await PreflightAsync(
            api,
            adminToken,
            fixture.StaleWorkId,
            fixture.T01VersionId,
            staleCommandId,
            target,
            stalePeriod,
            ct);
        var units = database.GetCollection<Unit>("units");
        var staleUnit = await units
            .Find(unit => unit.Id == target[0] && !unit.IsDeleted)
            .SingleAsync(ct);
        var originalStaleUnitName = staleUnit.FullName;
        var originalStaleUnitVersion = staleUnit.Version;
        var originalStaleUnitUpdatedAtUtc = staleUnit.UpdatedAtUtc;
        staleUnit.FullName = $"{staleUnit.FullName} P507-STALE";
        staleUnit.Version += 1;
        staleUnit.UpdatedAtUtc = new DateTime(
            2026,
            7,
            23,
            0,
            7,
            0,
            DateTimeKind.Utc);
        await units.ReplaceOneAsync(
            unit => unit.Id == staleUnit.Id && !unit.IsDeleted,
            staleUnit,
            cancellationToken: ct);
        var beforeStaleConfirm = await CaptureP507BusinessStateHashAsync(database, ct);
        var staleConfirm = await ConfirmAsync(
            api,
            adminToken,
            fixture.StaleWorkId,
            fixture.T01VersionId,
            staleCommandId,
            target,
            stalePeriod,
            stalePreflight.SnapshotToken,
            ct);
        AssertConflict(
            staleConfirm,
            "DYNAMIC_FLOW_REVISION_CONFLICT",
            "DYNAMIC_FLOW_PREFLIGHT_STALE",
            "stale launch snapshot");
        var afterStaleConfirm = await CaptureP507BusinessStateHashAsync(database, ct);
        Require(
            beforeStaleConfirm == afterStaleConfirm,
            "Stale confirm modified direct-Mongo business state.");
        await units.UpdateOneAsync(
            unit => unit.Id == staleUnit.Id,
            Builders<Unit>.Update
                .Set(unit => unit.FullName, originalStaleUnitName)
                .Set(unit => unit.Version, originalStaleUnitVersion)
                .Set(
                    unit => unit.UpdatedAtUtc,
                    originalStaleUnitUpdatedAtUtc),
            cancellationToken: ct);

        var blockedRows = new List<P507BarrierRow>();
        foreach (var barrier in fixture.BarrierFixtures
                     .OrderBy(item => item.ArchetypeId, StringComparer.Ordinal))
        {
            var commandId =
                $"p507-blocked-{barrier.ArchetypeId.ToLowerInvariant()}";
            var periodKey =
                $"2026-07-{barrier.ArchetypeId.Replace("FLOW-", string.Empty, StringComparison.Ordinal)}";
            var request = NewPreflightRequest(
                barrier.VersionId,
                commandId,
                target,
                periodKey);
            var before = await CaptureP507BusinessStateHashAsync(database, ct);
            var preflight = await api.PostAsync(
                $"api/works/{barrier.WorkId}/dynamic-flows/preflight",
                request,
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                preflight,
                HttpStatusCode.OK,
                $"{barrier.ArchetypeId} blocked preflight");
            Require(
                ApiHarnessClient.RequiredString(preflight.Json, "eligibility") ==
                DynamicFlowRuntimeEligibilityPolicy.BlockedPhase &&
                ApiHarnessClient.RequiredString(preflight.Json, "blockedUntilPhase") ==
                "P6",
                $"{barrier.ArchetypeId} did not remain blocked at P6.");
            var confirm = await api.PostAsync(
                $"api/works/{barrier.WorkId}/dynamic-flows/confirm",
                NewConfirmRequest(
                    request,
                    ApiHarnessClient.RequiredString(
                        preflight.Json,
                        "snapshotToken")),
                adminToken,
                ct: ct);
            AssertBlockedConfirm(confirm, $"{barrier.ArchetypeId} confirm");
            var after = await CaptureP507BusinessStateHashAsync(database, ct);
            Require(
                before == after,
                $"{barrier.ArchetypeId} blocked path changed direct-Mongo state.");
            blockedRows.Add(
                new P507BarrierRow(
                    barrier.ArchetypeId,
                    DynamicFlowRuntimeEligibilityPolicy.BlockedPhase,
                    "P6",
                    (int)confirm.StatusCode,
                    false));
        }

        const string lockedCommandId = "p507-locked-v11-001";
        var lockedRequest = NewPreflightRequest(
            fixture.LockedV11VersionId,
            lockedCommandId,
            target,
            "2026-07-P507-V11");
        var beforeLocked = await CaptureP507BusinessStateHashAsync(database, ct);
        var lockedPreflight = await api.PostAsync(
            $"api/works/{fixture.LockedV11WorkId}/dynamic-flows/preflight",
            lockedRequest,
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            lockedPreflight,
            HttpStatusCode.OK,
            "locked v1.1 preflight");
        Require(
            ApiHarnessClient.RequiredString(lockedPreflight.Json, "eligibility") ==
            DynamicFlowRuntimeEligibilityPolicy.BlockedCatalog &&
            ApiHarnessClient.RequiredString(
                lockedPreflight.Json,
                "blockedUntilPhase") == "P5",
            "Locked v1.1 preflight crossed the P5 catalog barrier.");
        var lockedConfirm = await api.PostAsync(
            $"api/works/{fixture.LockedV11WorkId}/dynamic-flows/confirm",
            NewConfirmRequest(
                lockedRequest,
                ApiHarnessClient.RequiredString(
                    lockedPreflight.Json,
                    "snapshotToken")),
            adminToken,
            ct: ct);
        AssertBlockedConfirm(lockedConfirm, "locked v1.1 confirm");
        var afterLocked = await CaptureP507BusinessStateHashAsync(database, ct);
        Require(
            beforeLocked == afterLocked,
            "Locked v1.1 path changed direct-Mongo business state.");

        var t01Step = await database
            .GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(step =>
                step.FlowInstanceId == t01InstanceId &&
                !step.IsDeleted)
            .SingleAsync(ct);
        var t01AssignmentId = t01Step.AssignmentId
                              ?? throw new InvalidOperationException(
                                  "P5-07 T01 transition fixture lacks assignment.");
        var transitionRows = new List<P507TransitionRow>();
        foreach (var action in new[] { "rollback", "terminate", "restart" })
        {
            var before = await CaptureP507BusinessStateHashAsync(database, ct);
            var response = await api.PostAsync(
                $"api/works/{fixture.T01WorkId}/dynamic-flows/assignments/{t01AssignmentId}/{action}",
                new { reason = $"P5-07 verify {action}" },
                adminToken,
                ct: ct);
            AssertConflict(
                response,
                "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                "DYNAMIC_FLOW_COMMAND_BLOCKED_UNTIL_P6",
                $"P6 {action}");
            var after = await CaptureP507BusinessStateHashAsync(database, ct);
            Require(
                before == after,
                $"P6 {action} changed direct-Mongo business state.");
            transitionRows.Add(
                new P507TransitionRow(
                    action.ToUpperInvariant(),
                    (int)response.StatusCode,
                    "P6",
                    false));
        }

        var beforeFinalize = await CaptureP507BusinessStateHashAsync(database, ct);
        var finalize = await api.PostAsync(
            $"api/works/{fixture.T01WorkId}/dynamic-flows/assignments/{t01AssignmentId}/finalize",
            new { reason = "P5-07 route-absence proof" },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            finalize,
            HttpStatusCode.NotFound,
            "P6 finalize route absence");
        var afterFinalize = await CaptureP507BusinessStateHashAsync(database, ct);
        Require(
            beforeFinalize == afterFinalize,
            "Absent finalize route changed direct-Mongo business state.");
        transitionRows.Add(
            new P507TransitionRow(
                "FINALIZE",
                (int)finalize.StatusCode,
                "P6",
                false));

        var p7Assignment = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .Find(assignment =>
                assignment.WorkId == fixture.ChangedReplayWorkId &&
                !assignment.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "P5-07 P7 fixture lacks a real P5 assignment.");
        var p7Period = await database
            .GetCollection<WorkReportPeriod>("work_report_periods")
            .Find(period =>
                period.WorkAssignmentId == p7Assignment.Id &&
                !period.IsDeleted)
            .SingleAsync(ct);
        var p7AssigneeId = p7Assignment.Assignees.Single().UserId;
        var users = database.GetCollection<AppUser>("users");
        var p7Assignee = await users
            .Find(user => user.Id == p7AssigneeId && !user.IsDeleted)
            .SingleAsync(ct);
        var p7PasswordHash = new PasswordHasher<AppUser>()
            .HashPassword(p7Assignee, actorPassword);
        await users.UpdateOneAsync(
            user => user.Id == p7Assignee.Id && !user.IsDeleted,
            Builders<AppUser>.Update
                .Set(user => user.PasswordHash, p7PasswordHash)
                .Set(
                    user => user.UpdatedAtUtc,
                    new DateTime(
                        2026,
                        7,
                        23,
                        0,
                        8,
                        0,
                        DateTimeKind.Utc)),
            cancellationToken: ct);
        var p7Token = await api.LoginAsync(
            p7Assignee.Username,
            actorPassword,
            ct);
        var p7Open = await api.PostAsync(
            $"api/work-report-periods/{p7Period.Id}/open",
            body: null,
            p7Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            p7Open,
            HttpStatusCode.OK,
            "P7 real P5 report open");
        var p7ReportId = ApiHarnessClient.RequiredString(p7Open.Json, "id");
        var p7Report = await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .Find(report =>
                report.Id == p7ReportId &&
                !report.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "P5-07 P7 fixture lacks a real P5 report.");
        var beforeP7 = await CaptureP507BusinessStateHashAsync(database, ct);
        var p7 = await api.PostAsync(
            $"api/work-assignment-reports/{p7Report.Id}/draft/apply-dynamic-flow-mapping",
            new
            {
                expectedPayloadRevision = p7Report.PayloadRevision,
                commandId = "p507-p7-mapping-001"
            },
            p7Token,
            ct: ct);
        if (DynamicFlowP7CatalogCandidate.ActivationEnabled)
        {
            AssertConflict(
                p7,
                "DYNAMIC_FLOW_MAPPING_TARGET_REVISION_CONFLICT",
                "DYNAMIC_FLOW_MAPPING_EXPECTED_TARGET_REQUIRED",
                "P7 active mapping stale-target guard");
        }
        else
        {
            AssertConflict(
                p7,
                "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                "DYNAMIC_FLOW_MAPPING_APPLY_BLOCKED_UNTIL_P7_08",
                "P7 inactive mapping barrier");
        }
        var afterP7 = await CaptureP507BusinessStateHashAsync(database, ct);
        Require(
            beforeP7 == afterP7,
            "P7 mapping guard changed direct-Mongo business state.");

        var beforeP8 = await CaptureP507BusinessStateHashAsync(database, ct);
        var p8 = await api.PostAsync(
            $"api/works/{fixture.T01WorkId}/dynamic-flows/instances/{t01InstanceId}/statistics",
            new { commandId = "p507-p8-statistics-001" },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            p8,
            HttpStatusCode.NotFound,
            "P8 runtime statistics route absence");
        var afterP8 = await CaptureP507BusinessStateHashAsync(database, ct);
        Require(
            beforeP8 == afterP8,
            "Absent P8 runtime statistics route changed direct-Mongo state.");

        var evidence = new
        {
            caseId = "P5-07-NEGATIVE-BARRIER-MATRIX",
            verdict = "PASS",
            changedReplay = new
            {
                httpStatus = (int)changedReplay.StatusCode,
                errorCode = "DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT",
                zeroWrite = true
            },
            stale = new
            {
                httpStatus = (int)staleConfirm.StatusCode,
                errorCode = "DYNAMIC_FLOW_REVISION_CONFLICT",
                reason = "DYNAMIC_FLOW_PREFLIGHT_STALE",
                zeroWrite = true
            },
            blockedArchetypes = blockedRows,
            lockedV11 = new
            {
                eligibility = DynamicFlowRuntimeEligibilityPolicy.BlockedCatalog,
                blockedUntilPhase = "P5",
                zeroWrite = true
            },
            transitions = transitionRows,
            p7 = new
            {
                httpStatus = (int)p7.StatusCode,
                catalogActivation =
                    DynamicFlowP7CatalogCandidate.ActivationEnabled,
                errorCode = ApiHarnessClient.FindStringRecursive(
                    p7.Json,
                    "errorCode"),
                reason = ApiHarnessClient.FindStringRecursive(
                    p7.Json,
                    "reason"),
                zeroWrite = true
            },
            p8 = new
            {
                httpStatus = (int)p8.StatusCode,
                runtimeRoutePresent = false,
                zeroWrite = true
            }
        };
        return new P507SupplementalObservation(
            ChangedReplayRejected: true,
            StaleSnapshotRejected: true,
            BlockedArchetypeCount: blockedRows.Count,
            LockedV11Blocked: true,
            P6TransitionsBlocked: transitionRows.Count == 4,
            P7MappingGuarded: true,
            P8StatisticsAbsent: true,
            CaseEvidence: evidence);
    }

    private static DynamicFlowPreflightRequest NewPreflightRequest(
        string versionId,
        string commandId,
        IReadOnlyCollection<string> targetUnitIds,
        string periodKey)
        => new()
        {
            FlowTemplateVersionId = versionId,
            CommandId = commandId,
            TargetUnitIds = targetUnitIds.ToList(),
            PeriodKey = periodKey,
            ScheduleIdentityJson =
                "{\"timezone\":\"Asia/Ho_Chi_Minh\",\"cadence\":\"once\",\"anchor\":\"2026-07-23\"}"
        };

    private static DynamicFlowConfirmRequest NewConfirmRequest(
        DynamicFlowPreflightRequest request,
        string snapshotToken)
        => new()
        {
            FlowTemplateVersionId = request.FlowTemplateVersionId,
            CommandId = request.CommandId,
            TargetUnitIds = request.TargetUnitIds.ToList(),
            PeriodKey = request.PeriodKey,
            ScheduleIdentityJson = request.ScheduleIdentityJson,
            SnapshotToken = snapshotToken
        };

    private static void AssertBlockedConfirm(
        ApiHarnessResponse response,
        string context)
    {
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, context);
        Require(
            ApiHarnessClient.RequiredString(response.Json, "status") ==
            "BLOCKED_UNTIL_TARGET_PHASE" &&
            !ApiHarnessClient.RequiredBool(
                response.Json,
                "businessWritePerformed"),
            $"{context} did not remain an explicit zero-write barrier.");
    }

    private static void AssertConflict(
        ApiHarnessResponse response,
        string expectedCode,
        string? expectedReason,
        string context)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Conflict,
            context);
        var actualCode =
            ApiHarnessClient.FindStringRecursive(response.Json, "errorCode") ??
            ApiHarnessClient.FindStringRecursive(response.Json, "code");
        Require(
            actualCode == expectedCode,
            $"{context} returned the wrong error code.");
        if (expectedReason is not null)
        {
            Require(
                ApiHarnessClient.FindStringRecursive(response.Json, "reason") ==
                expectedReason,
                $"{context} returned the wrong stable reason.");
        }
    }

    private static async Task<string> CaptureP507BusinessStateHashAsync(
        IMongoDatabase database,
        CancellationToken ct)
    {
        var builder = new StringBuilder();
        foreach (var collectionName in P507BusinessCollections)
        {
            builder.Append(collectionName).Append('\n');
            var documents = await database
                .GetCollection<BsonDocument>(collectionName)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .ToListAsync(ct);
            foreach (var document in documents)
                builder.Append(document.ToJson()).Append('\n');
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static IReadOnlyList<HarnessCaseResult> BuildP507GateCaseRows(
        P507SupplementalObservation observation,
        ProbeFixture fixture)
    {
        Require(observation.ChangedReplayRejected, "Changed replay oracle is missing.");
        Require(observation.StaleSnapshotRejected, "Stale snapshot oracle is missing.");
        Require(
            observation.BlockedArchetypeCount == 10,
            "T03..T12 barrier matrix is incomplete.");
        Require(observation.LockedV11Blocked, "Locked v1.1 barrier oracle is missing.");
        Require(observation.P6TransitionsBlocked, "P6 transition matrix is incomplete.");
        Require(observation.P7MappingGuarded, "P7 mapping guard oracle is missing.");
        Require(observation.P8StatisticsAbsent, "P8 runtime route-absence oracle is missing.");
        Require(fixture.FormPins.Count == 3, "Form A/B/C pin oracle is incomplete.");

        static HarnessCaseResult Row(string id, string detail, string fingerprint)
            => new(id, HarnessVerdict.DAT, detail, fingerprint, 0);

        return
        [
            Row("ASN-FLOW-01", "FLOW-T01 materialized one exact target branch.", "t01:branches=1"),
            Row("ASN-FLOW-02", "FLOW-T01 materialized three target branches.", "fanout:branches=3"),
            Row("ASN-FLOW-03", "Duplicate target units were deterministically deduplicated.", "target-dedupe:5-to-3"),
            Row("ASN-FLOW-04", "Branch-2 injected faults recovered to one converged ledger.", "branch2:recovered=true"),
            Row("ASN-FLOW-05", "Exact replay and concurrent confirm reused one durable instance.", "replay:one-winner=true"),
            Row("ASN-FLOW-06", "Outsider/spoof launch was denied with zero business writes.", "outsider-spoof:403-zero-write"),
            Row("ASN-FLOW-07", "T01 state projection stayed server-derived and P6-gated.", "state-projection:p6-gated"),
            Row("ASN-FLOW-08", "Three distinct immutable Form A/B/C pins were frozen.", "form-pins:A+B+C"),
            Row("ASN-FLOW-09", "Period and schedule identities remained exact across replay.", "period-schedule:stable"),
            Row("ASN-FLOW-10", "Assignments, reports, queues, DocRoles and read models converged.", "downstream-ledger:converged"),
            Row("P5-07-CHANGED-REPLAY", "Changed replay returned stable 409 and zero writes.", "changed-replay:409"),
            Row("P5-07-STALE-SNAPSHOT", "Stale snapshot returned revision conflict and zero writes.", "stale-snapshot:409"),
            Row("P5-07-T03-T12-BARRIER", "All ten non-P5 archetypes remained P6-blocked.", "blocked-archetypes=10"),
            Row("P5-07-LOCKED-V11-BARRIER", "Locked catalog v1.1 stayed P5-blocked and zero-write.", "locked-v11:blocked"),
            Row("P5-07-P6-TRANSITIONS", "Rollback, terminate, restart and finalize stayed closed.", "p6-transitions=4"),
            Row("P5-07-P7-MAPPING", "Real P5 report mapping failed closed through the catalog-appropriate guard with zero writes.", "p7-mapping:409-zero-write"),
            Row("P5-07-P8-STATISTICS", "No P8 runtime statistics route was exposed.", "p8-route:absent"),
            Row("P5-07-CURSOR-ADVERSARIAL", "Tampered and cross-scope cursors failed closed.", "cursor-adversarial:closed"),
            Row("P5-07-FAULT-BOUNDARIES", "Core, projector, recovery, reconcile and compensation boundaries executed.", "fault-boundaries:complete"),
            Row("P5-07-DIRECT-MONGO", "Direct Mongo pins, hashes, unique keys and zero-write barriers matched.", "direct-mongo:exact")
        ];
    }

    private static string BuildP507NormalizedSha256(
        IEnumerable<HarnessCaseResult> cases)
    {
        var normalized = cases
            .OrderBy(item => item.CaseId, StringComparer.Ordinal)
            .Select(item => new
            {
                item.CaseId,
                item.Verdict,
                item.Fingerprint
            })
            .ToArray();
        var json = JsonSerializer.Serialize(normalized, EvidenceJson.Options);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
    }

    private static string BuildP507SeedIdentity(ProbeFixture fixture)
        => Hash(string.Join(
            "\n",
            new[]
            {
                fixture.T01WorkId,
                fixture.T02WorkId,
                fixture.StateWorkId,
                fixture.ChangedReplayWorkId,
                fixture.StaleWorkId,
                fixture.LockedV11WorkId,
                fixture.T01VersionId,
                fixture.T02VersionId,
                fixture.LockedV11VersionId
            }
            .Concat(fixture.TargetUnitIds)
            .Concat(fixture.FormPins.Select(pin => pin.FormVersionId))
            .Concat(fixture.BarrierFixtures.Select(item => item.VersionId))
            .OrderBy(value => value, StringComparer.Ordinal)));

    private sealed record P507SupplementalObservation(
        bool ChangedReplayRejected,
        bool StaleSnapshotRejected,
        int BlockedArchetypeCount,
        bool LockedV11Blocked,
        bool P6TransitionsBlocked,
        bool P7MappingGuarded,
        bool P8StatisticsAbsent,
        object CaseEvidence);

    private sealed record P507BarrierRow(
        string ArchetypeId,
        string Eligibility,
        string BlockedUntilPhase,
        int ConfirmHttpStatus,
        bool BusinessWritePerformed);

    private sealed record P507TransitionRow(
        string Action,
        int HttpStatus,
        string BlockedUntilPhase,
        bool BusinessWritePerformed);
}



