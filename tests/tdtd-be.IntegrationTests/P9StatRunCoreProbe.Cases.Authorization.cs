using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task RunAuthorizationCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-CORE-AUTH-01",
            async () =>
            {
                var before = await CountJobsAsync(ct);
                var response = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    BuildCreateRequest("p9-core-unauthenticated-001"),
                    string.Empty,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.Unauthorized,
                    "Unauthenticated stat-run create");
                HarnessAssert.Equal(
                    before,
                    await CountJobsAsync(ct),
                    "Unauthenticated create changed jobs");
                return new CaseObservation(
                    "Unauthenticated command was rejected by the real ASP.NET authorization pipeline before all writes.",
                    "http=401;pipeline=Kestrel;writes=0");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-AUTH-02",
            async () =>
            {
                var before = await CountJobsAsync(ct);
                var targets = new[]
                {
                    RequireIdentityJobId(),
                    ObjectId.GenerateNewId().ToString(),
                    "malformed-hidden-job-id"
                };
                foreach (var target in targets)
                {
                    var response = await RequireApi().GetAsync(
                        $"api/stat-runs/jobs/{target}",
                        Actor("outsider").Token,
                        ct: ct);
                    ExpectError(
                        response,
                        HttpStatusCode.Forbidden,
                        "STAT_RUN_FORBIDDEN",
                        $"Auth-before-existence target {target}");
                }
                HarnessAssert.Equal(
                    before,
                    await CountJobsAsync(ct),
                    "Auth-before-existence reads changed jobs");
                return new CaseObservation(
                    "A scoped outsider received the same forbidden envelope for hidden, missing and malformed job identities.",
                    "hidden=403;missing=403;malformed=403;code=STAT_RUN_FORBIDDEN;writes=0");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-AUTH-03",
            async () =>
            {
                var before = await CountJobsAsync(ct);
                var response = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    BuildCreateRequest("p9-core-cross-tenant-003"),
                    Actor("outsider").Token,
                    ct);
                ExpectError(
                    response,
                    HttpStatusCode.Forbidden,
                    "STAT_RUN_FORBIDDEN",
                    "Cross-tenant stat-run create");
                HarnessAssert.Equal(
                    before,
                    await CountJobsAsync(ct),
                    "Cross-tenant create changed jobs");
                return new CaseObservation(
                    "A manager-role actor from another server-derived unit could not command the hidden assignment.",
                    "policy=tenant-unit;http=403;writes=0");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-AUTH-04",
            async () =>
            {
                var before = await CountJobsAsync(ct);
                var request = BuildCreateRequest(
                    "p9-core-cross-work-scope-004");
                request["workId"] = ObjectId.GenerateNewId().ToString();
                request["scopeId"] = ObjectId.GenerateNewId().ToString();
                var response = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    request,
                    Actor("executor").Token,
                    ct);
                ExpectError(
                    response,
                    HttpStatusCode.Forbidden,
                    "STAT_RUN_FORBIDDEN",
                    "Cross-work/scope stat-run create");

                var assignments = RequireDatabase()
                    .GetCollection<BsonDocument>("work_assignments");
                var sourceAssignmentId = ObjectId.Parse(
                    Fixture().AssignmentId);
                var sourceAssignment = await assignments
                    .Find(new BsonDocument("_id", sourceAssignmentId))
                    .SingleAsync(ct);
                var siblingAssignmentId = ObjectId.Parse(
                    Fixture().SiblingAssignmentId);
                var sibling = await assignments
                    .Find(new BsonDocument("_id", siblingAssignmentId))
                    .SingleAsync(ct);
                HarnessAssert.Equal(
                    Fixture().WorkId,
                    BsonText(sibling, "workId"),
                    "Sibling assignment work identity");

                var randomRootScope = BuildCreateRequest(
                    "p9-core-random-root-scope-004");
                randomRootScope["scopeType"] = "ROOT";
                randomRootScope["scopeId"] =
                    ObjectId.GenerateNewId().ToString();
                var randomRootResponse = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    randomRootScope,
                    Actor("executor").Token,
                    ct);
                ExpectError(
                    randomRootResponse,
                    HttpStatusCode.Forbidden,
                    "STAT_RUN_FORBIDDEN",
                    "Random ROOT scope stat-run create");

                var originalRootAssignmentId = sourceAssignment[
                    "rootAssignmentId"].DeepClone();
                var originalSiblingCreatedBy = sibling[
                    "createdByUserId"].DeepClone();
                var originalSiblingWatchers = sibling[
                    "leaderWatcherUserIds"].DeepClone();
                var distinctRootScopeId = siblingAssignmentId.ToString();
                string? rootJobId = null;
                try
                {
                    await assignments.UpdateOneAsync(
                        new BsonDocument("_id", siblingAssignmentId),
                        Builders<BsonDocument>.Update
                            .Set(
                                "createdByUserId",
                                ObjectId.Parse(Actor("admin").Id))
                            .Set(
                                "leaderWatcherUserIds",
                                new BsonArray
                                {
                                    Actor("insufficient").Id
                                }),
                        cancellationToken: ct);
                    await assignments.UpdateOneAsync(
                        new BsonDocument("_id", sourceAssignmentId),
                        Builders<BsonDocument>.Update.Set(
                            "rootAssignmentId",
                            distinctRootScopeId),
                        cancellationToken: ct);

                    var rootScope = BuildCreateRequest(
                        "p9-core-root-child-positive-004");
                    rootScope["scopeType"] = "ROOT";
                    rootScope["scopeId"] = distinctRootScopeId;
                    var rootScopeResponse = await CreateJobAsync(
                        "DIRECT_FIELD_TABLE_LABEL",
                        rootScope,
                        Actor("executor").Token,
                        ct);
                    ApiHarnessClient.ExpectStatus(
                        rootScopeResponse,
                        HttpStatusCode.Accepted,
                        "ROOT scope through child assignment");
                    rootJobId = RequiredString(
                        rootScopeResponse.Json,
                        "jobId");

                    var rootRead = await RequireApi().GetAsync(
                        $"api/stat-runs/jobs/{rootJobId}",
                        Actor("executor").Token,
                        ct: ct);
                    ApiHarnessClient.ExpectStatus(
                        rootRead,
                        HttpStatusCode.OK,
                        "ROOT scope accepted job read");
                    HarnessAssert.Equal(
                        rootJobId,
                        RequiredString(rootRead.Json, "jobId"),
                        "ROOT scope accepted job identity");

                    var rootReplay = await CreateJobAsync(
                        "DIRECT_FIELD_TABLE_LABEL",
                        rootScope,
                        Actor("executor").Token,
                        ct);
                    ApiHarnessClient.ExpectStatus(
                        rootReplay,
                        HttpStatusCode.OK,
                        "ROOT scope exact replay");
                    HarnessAssert.True(
                        RequiredBool(rootReplay.Json, "isReplay"),
                        "ROOT scope replay was not marked as replay.");
                    HarnessAssert.Equal(
                        rootJobId,
                        RequiredString(rootReplay.Json, "jobId"),
                        "ROOT scope replay job identity");
                }
                finally
                {
                    DeleteResult? deletedRootJob = null;
                    try
                    {
                        if (rootJobId is not null)
                        {
                            deletedRootJob = await RequireDatabase()
                                .GetCollection<BsonDocument>(
                                    "work_report_statistic_rebuild_jobs")
                                .DeleteOneAsync(
                                    new BsonDocument(
                                        "_id",
                                        ObjectId.Parse(rootJobId)),
                                    CancellationToken.None);
                        }
                    }
                    finally
                    {
                        try
                        {
                            await assignments.UpdateOneAsync(
                                new BsonDocument(
                                    "_id",
                                    sourceAssignmentId),
                                Builders<BsonDocument>.Update.Set(
                                    "rootAssignmentId",
                                    originalRootAssignmentId),
                                cancellationToken:
                                CancellationToken.None);
                        }
                        finally
                        {
                            await assignments.UpdateOneAsync(
                                new BsonDocument(
                                    "_id",
                                    siblingAssignmentId),
                                Builders<BsonDocument>.Update
                                    .Set(
                                        "createdByUserId",
                                        originalSiblingCreatedBy)
                                    .Set(
                                        "leaderWatcherUserIds",
                                        originalSiblingWatchers),
                                cancellationToken:
                                CancellationToken.None);
                        }
                    }
                    if (rootJobId is not null)
                        HarnessAssert.Equal(
                            1L,
                            deletedRootJob?.DeletedCount ?? 0L,
                            "ROOT scope accepted job cleanup count");
                }

                var originalCreatedBy = sourceAssignment[
                    "createdByUserId"].DeepClone();
                var originalWatchers = sourceAssignment[
                    "leaderWatcherUserIds"].DeepClone();
                await assignments.UpdateOneAsync(
                    new BsonDocument("_id", sourceAssignmentId),
                    Builders<BsonDocument>.Update
                        .Set(
                            "createdByUserId",
                            ObjectId.Parse(Actor("admin").Id))
                        .Set(
                            "leaderWatcherUserIds",
                            new BsonArray
                            {
                                Actor("insufficient").Id,
                                Actor("executor2").Id
                            }),
                    cancellationToken: ct);
                try
                {
                    var workScope = BuildCreateRequest(
                        "p9-core-work-coarse-auth-004");
                    workScope["scopeType"] = "WORK";
                    workScope["scopeId"] = Fixture().WorkId;
                    var workScopeResponse = await CreateJobAsync(
                        "DIRECT_FIELD_TABLE_LABEL",
                        workScope,
                        Actor("executor").Token,
                        ct);
                    ExpectError(
                        workScopeResponse,
                        HttpStatusCode.Forbidden,
                        "STAT_RUN_FORBIDDEN",
                        "WORK scope with only sibling authorization");
                }
                finally
                {
                    await assignments.UpdateOneAsync(
                        new BsonDocument("_id", sourceAssignmentId),
                        Builders<BsonDocument>.Update
                            .Set("createdByUserId", originalCreatedBy)
                            .Set(
                                "leaderWatcherUserIds",
                                originalWatchers),
                        cancellationToken: CancellationToken.None);
                }

                var hiddenTemplate = BuildCreateRequest(
                    "p9-core-hidden-template-004");
                hiddenTemplate["dynamicFormTemplateId"] =
                    ObjectId.GenerateNewId().ToString();
                var hiddenTemplateResponse = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    hiddenTemplate,
                    Actor("executor").Token,
                    ct);
                ExpectError(
                    hiddenTemplateResponse,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_SOURCE_NOT_EFFECTIVE",
                    "Hidden template mismatch");
                HarnessAssert.Equal(
                    "SOURCE_SCOPE_MISMATCH",
                    ApiHarnessClient.FindStringRecursive(
                        hiddenTemplateResponse.Json,
                        "reason"),
                    "Hidden template mismatch reason");
                HarnessAssert.Equal(
                    before,
                    await CountJobsAsync(ct),
                    "Cross-work/scope create changed jobs");
                return new CaseObservation(
                    "Random work/scope and ROOT substitutions failed before writes; a ROOT command for an authorized child whose Id differed from RootAssignmentId was accepted, readable, replay-stable, and cleaned up; WORK-scope sibling authorization still failed exact-source authorization, and the hidden template mismatch remained fail-closed.",
                    "randomScope=403;randomRoot=403;rootChild=202/read200/replay200/cleanup1;workSiblingAuthorized=true/exactSourceRevoked=403;hiddenTemplate=409/SOURCE_SCOPE_MISMATCH;writes=0");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-AUTH-05",
            async () =>
            {
                var assignment = await RequireDatabase()
                    .GetCollection<BsonDocument>("work_assignments")
                    .Find(new BsonDocument(
                        "_id",
                        ObjectId.Parse(Fixture().AssignmentId)))
                    .SingleAsync(ct);
                HarnessAssert.True(
                    assignment["leaderWatcherUserIds"]
                        .AsBsonArray
                        .Any(value =>
                            value.AsString == Actor("insufficient").Id),
                    "Insufficient actor lacks the scoped watcher fixture.");
                var before = await CountJobsAsync(ct);
                var response = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    BuildCreateRequest("p9-core-insufficient-role-005"),
                    Actor("insufficient").Token,
                    ct);
                ExpectError(
                    response,
                    HttpStatusCode.Forbidden,
                    "STAT_RUN_FORBIDDEN",
                    "Insufficient-role stat-run create");
                HarnessAssert.Equal(
                    before,
                    await CountJobsAsync(ct),
                    "Insufficient-role create changed jobs");
                return new CaseObservation(
                    "A same-tenant scoped watcher without an allowed server role failed before receipt/source lookup.",
                    "scopeWouldMatch=true;roles=0;http=403;writes=0");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-AUTH-06",
            async () =>
            {
                var before = await CountJobsAsync(ct);
                var spoof = BuildCreateRequest(
                    "p9-core-spoofed-actor-006");
                spoof["actorUserId"] = Actor("admin").Id;
                spoof["tenantUnitId"] = Fixture().UnitBId;
                var spoofResponse = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    spoof,
                    Actor("executor").Token,
                    ct);
                ExpectError(
                    spoofResponse,
                    HttpStatusCode.BadRequest,
                    "COMMON_VALIDATION_FAILED",
                    "Spoofed actor/tenant payload");
                HarnessAssert.Equal(
                    before,
                    await CountJobsAsync(ct),
                    "Spoofed payload changed jobs");

                var assignments = RequireDatabase()
                    .GetCollection<BsonDocument>("work_assignments");
                var assignmentId = ObjectId.Parse(Fixture().AssignmentId);
                var originalAssignment = await assignments
                    .Find(new BsonDocument("_id", assignmentId))
                    .SingleAsync(ct);
                var originalCreatedBy = originalAssignment[
                    "createdByUserId"].DeepClone();
                var originalWatchers = originalAssignment[
                    "leaderWatcherUserIds"].DeepClone();
                await assignments.UpdateOneAsync(
                    new BsonDocument("_id", assignmentId),
                    Builders<BsonDocument>.Update
                        .Set(
                            "createdByUserId",
                            ObjectId.Parse(Actor("admin").Id))
                        .Set(
                            "leaderWatcherUserIds",
                            new BsonArray
                            {
                                Actor("insufficient").Id
                            }),
                    cancellationToken: ct);
                try
                {
                    var replay = await CreateJobAsync(
                        "DIRECT_FIELD_TABLE_LABEL",
                        BuildCreateRequest("p9-core-identity-001"),
                        Actor("executor").Token,
                        ct);
                    ExpectError(
                        replay,
                        HttpStatusCode.Forbidden,
                        "STAT_RUN_FORBIDDEN",
                        "Replay after scope revocation");
                    HarnessAssert.Equal(
                        before,
                        await CountJobsAsync(ct),
                        "Revoked replay changed jobs");
                }
                finally
                {
                    await assignments.UpdateOneAsync(
                        new BsonDocument("_id", assignmentId),
                        Builders<BsonDocument>.Update
                            .Set("createdByUserId", originalCreatedBy)
                            .Set(
                                "leaderWatcherUserIds",
                                originalWatchers),
                        cancellationToken: CancellationToken.None);
                }
                return new CaseObservation(
                    "Strict schema rejected actor/tenant spoofing and replay re-ran current server authorization before receipt lookup.",
                    "spoof=400;reauthorizedReplay=403;duplicateWrites=0");
            },
            ct);
    }
}
