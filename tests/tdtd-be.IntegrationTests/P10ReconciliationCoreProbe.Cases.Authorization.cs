using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    private async Task RunAuthorizationCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P10-AUTH-01",
            Req("P10-REC-006"),
            O("API_KESTREL", "AUTH_BEFORE_EXISTENCE", "COLLECTION_DELTA"),
            async () =>
            {
                var before = await CountRunsAsync(ct);
                var request = NewCreateRequest("p10-auth-unauthenticated-001");
                var response = await RequireApi().PostAsync(
                    ReconciliationBasePath(),
                    request.DeepClone(),
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.Unauthorized,
                    "P10 unauthenticated create");
                HarnessAssert.Equal(
                    before,
                    await CountRunsAsync(ct),
                    "Unauthenticated P10 request wrote a run");
                return new P10CaseObservation(
                    401,
                    "Unauthenticated create stopped at HTTP authentication before lookup or write.",
                    "authenticated=false;http=401;writes=0",
                    ActorAlias: "anonymous",
                    RequestHashSha256: EvidenceRequestHash(
                        ReconciliationBasePath(),
                        request));
            });

        await RunCaseAsync(
            "P10-AUTH-02",
            Req("P10-REC-006"),
            O("API_KESTREL", "AUTH_BEFORE_EXISTENCE", "DIRECT_MONGO"),
            async () =>
            {
                var outsider = Actor("outsider");
                var randomWork = ObjectId.GenerateNewId().ToString();
                var randomScope = ObjectId.GenerateNewId().ToString();
                var probes = new[]
                {
                    await RequireApi().PostAsync(
                        ReconciliationBasePath(),
                        new JsonObject
                        {
                            ["unknownServerField"] = true
                        },
                        outsider.Token,
                        ct: ct),
                    await RequireApi().PostAsync(
                        ReconciliationBasePath(randomWork, randomScope),
                        NewCreateRequest("p10-auth-missing-002"),
                        outsider.Token,
                        ct: ct),
                    await RequireApi().PostAsync(
                        ReconciliationBasePath("malformed", "malformed"),
                        new JsonObject
                        {
                            ["actorUserId"] = Actor("admin").Id,
                            ["p9ResultId"] = "diagnostic-probe"
                        },
                        outsider.Token,
                        ct: ct)
                };
                foreach (var response in probes)
                {
                    ExpectError(
                        response,
                        HttpStatusCode.Forbidden,
                        "STAT_RECONCILIATION_FORBIDDEN",
                        "P10 hidden/missing/malformed parity");
                    HarnessAssert.True(
                        !response.Body.Contains(
                            "STRICT_SCHEMA_INVALID",
                            StringComparison.Ordinal) &&
                        !response.Body.Contains(
                            "P9_RESULT",
                            StringComparison.Ordinal),
                        "Unauthorized parity leaked schema/source diagnostics.");
                }
                var bodies = probes.Select(response =>
                        ApiHarnessClient.FindStringRecursive(
                            response.Json,
                            "errorCode") ?? string.Empty)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                HarnessAssert.Equal(
                    1,
                    bodies.Length,
                    "Hidden/missing/malformed denial codes diverged");
                return new P10CaseObservation(
                    403,
                    "Outsider hidden, missing and malformed scope/body probes were indistinguishable and auth preceded strict parsing.",
                    "variants=3;http=403;code=STAT_RECONCILIATION_FORBIDDEN;diagnostics=redacted;writes=0",
                    ActorAlias: "outsider",
                    RequestHashSha256: EvidenceRequestHash(
                        "authorization/parity",
                        new JsonObject { ["variants"] = 3 }));
            });

        await RunCaseAsync(
            "P10-AUTH-03",
            Req("P10-REC-006"),
            O("API_KESTREL", "AUTH_BEFORE_EXISTENCE", "COLLECTION_DELTA"),
            async () =>
            {
                var before = await CountRunsAsync(ct);
                var response = await CreateRunAsync(
                    NewCreateRequest("p10-auth-cross-tenant-003"),
                    Actor("outsider").Token,
                    ct);
                ExpectError(
                    response,
                    HttpStatusCode.Forbidden,
                    "STAT_RECONCILIATION_FORBIDDEN",
                    "P10 cross-tenant create");
                HarnessAssert.Equal(
                    before,
                    await CountRunsAsync(ct),
                    "Cross-tenant manager wrote a P10 run");
                return new P10CaseObservation(
                    403,
                    "A manager from another tenant was denied before P9 result or receipt lookup.",
                    "actor=outsider;tenant=unitB;scopeTenant=unitA;http=403;writes=0",
                    ActorAlias: "outsider",
                    RequestHashSha256: EvidenceRequestHash(
                        ReconciliationBasePath(),
                        NewCreateRequest("p10-auth-cross-tenant-003")));
            });

        await RunCaseAsync(
            "P10-AUTH-04",
            Req("P10-REC-006"),
            O("API_KESTREL", "AUTH_BEFORE_EXISTENCE", "DIRECT_MONGO"),
            async () =>
            {
                var token = Actor("executor").Token;
                var wrongWork = await RequireApi().GetAsync(
                    ReconciliationBasePath(
                        ObjectId.GenerateNewId().ToString(),
                        Fixture().ScopeAssignmentId),
                    token,
                    ct: ct);
                ExpectError(
                    wrongWork,
                    HttpStatusCode.Forbidden,
                    "STAT_RECONCILIATION_FORBIDDEN",
                    "P10 wrong work");
                var sibling = await RequireApi().GetAsync(
                    ReconciliationBasePath(
                        Fixture().WorkId,
                        Fixture().SiblingAssignmentId),
                    token,
                    ct: ct);
                ExpectError(
                    sibling,
                    HttpStatusCode.Forbidden,
                    "STAT_RECONCILIATION_FORBIDDEN",
                    "P10 unauthorized sibling scope");
                var exact = await RequireApi().GetAsync(
                    ReconciliationBasePath(),
                    token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    exact,
                    HttpStatusCode.OK,
                    "P10 exact allowed scope list");
                return new P10CaseObservation(
                    200,
                    "Wrong work and sibling scope were denied while the exact authorized work/scope pair was allowed.",
                    "wrongWork=403;sibling=403;exact=200",
                    ActorAlias: "executor",
                    RequestHashSha256: EvidenceRequestHash(
                        ReconciliationBasePath(),
                        null));
            });

        await RunCaseAsync(
            "P10-AUTH-05",
            Req("P10-REC-006"),
            O("API_KESTREL", "AUTH_BEFORE_EXISTENCE", "COLLECTION_DELTA"),
            async () =>
            {
                var actor = Actor("insufficient");
                var exact = await RequireApi().GetAsync(
                    ReconciliationBasePath(),
                    actor.Token,
                    ct: ct);
                var missing = await RequireApi().GetAsync(
                    ReconciliationBasePath(
                        ObjectId.GenerateNewId().ToString(),
                        ObjectId.GenerateNewId().ToString()),
                    actor.Token,
                    ct: ct);
                foreach (var response in new[] { exact, missing })
                {
                    ExpectError(
                        response,
                        HttpStatusCode.Forbidden,
                        "STAT_RECONCILIATION_FORBIDDEN",
                        "P10 insufficient-role denial");
                }
                return new P10CaseObservation(
                    403,
                    "A same-tenant watcher without a command role was denied before exact or missing scope lookup.",
                    "sameTenant=true;watcher=true;role=false;exact=403;missing=403;writes=0",
                    ActorAlias: "insufficient",
                    RequestHashSha256: EvidenceRequestHash(
                        "authorization/insufficient-role",
                        null));
            });

        await RunCaseAsync(
            "P10-AUTH-06",
            Req("P10-REC-003", "P10-REC-004", "P10-REC-006"),
            O(
                "API_KESTREL",
                "AUTH_BEFORE_EXISTENCE",
                "DIRECT_MONGO",
                "RECEIPT"),
            async () =>
            {
                var before = await CountRunsAsync(ct);
                var spoofed = NewCreateRequest("p10-auth-spoofed-006");
                spoofed["actorUserId"] = Actor("admin").Id;
                spoofed["tenantUnitId"] = Fixture().UnitBId;
                spoofed["permissionCodes"] = new JsonArray("SYSTEM_ADMIN");
                spoofed["sourceSetSha256"] = new string('a', 64);
                var schemaResponse = await CreateRunAsync(
                    spoofed,
                    Actor("executor").Token,
                    ct);
                ExpectError(
                    schemaResponse,
                    HttpStatusCode.BadRequest,
                    "STAT_RECONCILIATION_SCHEMA_INVALID",
                    "P10 spoofed server fields");
                HarnessAssert.Equal(
                    before,
                    await CountRunsAsync(ct),
                    "Spoofed P10 body wrote a run");

                var assignments = RequireDatabase()
                    .GetCollection<BsonDocument>("work_assignments");
                var assignmentFilter = new BsonDocument(
                    "_id",
                    ObjectId.Parse(Fixture().ScopeAssignmentId));
                var original = await assignments.Find(assignmentFilter)
                    .SingleAsync(ct);
                var revoked = (BsonDocument)original.DeepClone();
                revoked["createdByUserId"] = ObjectId.Parse(
                    Actor("admin").Id);
                revoked["leaderWatcherUserIds"] = new BsonArray();
                revoked["issuedByUnitId"] = ObjectId.Parse(
                    Fixture().UnitBId);
                revoked["targetUnitIds"] = new BsonArray
                {
                    ObjectId.Parse(Fixture().UnitBId)
                };
                await assignments.ReplaceOneAsync(
                    assignmentFilter,
                    revoked,
                    cancellationToken: ct);
                try
                {
                    var replay = await CreateRunAsync(
                        NewCreateRequest("p10-id-owner-001"),
                        Actor("executor").Token,
                        ct);
                    ExpectError(
                        replay,
                        HttpStatusCode.Forbidden,
                        "STAT_RECONCILIATION_FORBIDDEN",
                        "P10 replay after authorization removal");
                }
                finally
                {
                    await assignments.ReplaceOneAsync(
                        assignmentFilter,
                        original,
                        cancellationToken: CancellationToken.None);
                }

                var summary = await RequireApi().GetAsync(
                    $"{ReconciliationBasePath()}/{IdentityRunId()}",
                    Actor("executor").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    summary,
                    HttpStatusCode.OK,
                    "P10 summary visibility");
                HarnessAssert.True(
                    ApiHarnessClient.FindStringRecursive(
                        summary.Json,
                        "actorUserId") is null &&
                    ApiHarnessClient.FindStringRecursive(
                        summary.Json,
                        "requestHash") is null,
                    "P10 summary leaked detail identity fields.");
                var forbiddenDetail = await RequireApi().GetAsync(
                    $"{ReconciliationBasePath()}/{IdentityRunId()}/detail",
                    Actor("executor").Token,
                    ct: ct);
                ExpectError(
                    forbiddenDetail,
                    HttpStatusCode.Forbidden,
                    "STAT_RECONCILIATION_FORBIDDEN",
                    "P10 executor detail");
                var adminDetail = await RequireApi().GetAsync(
                    $"{ReconciliationBasePath()}/{IdentityRunId()}/detail",
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    adminDetail,
                    HttpStatusCode.OK,
                    "P10 admin detail");
                var run = await LoadRunAsync(IdentityRunId(), ct);
                return ObserveRun(
                    adminDetail,
                    run,
                    "Spoofed fields failed strict schema; replay re-authorized; summary/detail permissions remained separate.",
                    "spoof=400;replayAfterRevoke=403;summary=200;executorDetail=403;adminDetail=200",
                    "admin",
                    1,
                    1);
            });
    }
}
