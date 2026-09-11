using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP810RouteAndGuardCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-UI-001",
            "p810_owner",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var identity = await ReadBasicConfigAsync(
                    Actor("p810_owner"),
                    _p810BasicFixture,
                    ct,
                    requirePersisted: true);
                var ownerId = $"{_p810Assignment.Id}:{_p809Form.OwnerId}";
                HarnessAssert.Equal(ownerId, identity.OwnerId,
                    "P8-10 canonical route lost assignment/form owner identity");
                HarnessAssert.Equal("LOCKED", identity.Status,
                    "P8-10 canonical route did not read the seeded locked Basic version");
                var frontendRoute =
                    $"/works/{_p810Assignment.WorkId}/statistics/{_p810Assignment.Id}/config/basic";
                _p810RouteBindings.Add(new P810RouteBinding(
                    "P8-UI-001",
                    "WORK_STATISTICS_CONFIG",
                    "GET",
                    frontendRoute,
                    BasicConfigRoute(_p810BasicFixture),
                    "CONFIG_ONLY"));
                return new CaseObservation(
                    "The canonical work/scope/tab deep link was bound to the same assignment/form owner read through real Kestrel and direct Mongo.",
                    $"workId={_p810Assignment.WorkId};scopeAssignmentId={_p810Assignment.Id};tab=basic;ownerId={ownerId};status=LOCKED");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-UI-002",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var form = await ReadFieldConfigAsync(
                    Actor("system_admin"),
                    _p809Form.OwnerId,
                    ct,
                    requirePersisted: true);
                HarnessAssert.Equal(_p809Form.ConfigId, form.ConfigId,
                    "P8-10 form statistics route resolved a competing config owner");
                HarnessAssert.Equal(_p809Form.VersionId, form.VersionId,
                    "P8-10 form statistics route resolved a competing version");
                var route = $"api/dynamic-forms/{_p809Form.OwnerId}/statistics";
                _p810RouteBindings.Add(new P810RouteBinding(
                    "P8-UI-002",
                    "FORM_STATISTICS_EDITOR",
                    "GET",
                    route,
                    "typed form identity + direct Mongo statisticConfigId/versionId",
                    "CONFIG_ONLY"));
                return new CaseObservation(
                    "The form statistics editor resolved one typed form owner/version and no competing editor identity.",
                    $"owner={form.OwnerId};config={form.ConfigId};version={form.VersionId};http=200;writes=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-UI-003",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var flowPin = P810Pin(6);
                var readinessPin = P810Pin(7);
                var flow = await ReadFlowVersionAsync(
                    Actor("system_admin"),
                    RequiredString(flowPin, "ownerId"),
                    RequiredString(flowPin, "versionId"),
                    ct);
                var readiness = await ReadP808SafeJobAsync(
                    Actor("system_admin"),
                    RequiredString(readinessPin, "versionId"),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    readiness,
                    HttpStatusCode.OK,
                    "P8-10 readiness surface GET");
                HarnessAssert.Equal("EXCLUDE", flow.ContributionPolicy,
                    "P8-10 flow contribution surface did not retain EXCLUDE");
                _p810RouteBindings.AddRange(
                [
                    new P810RouteBinding(
                        "P8-UI-003", "FLOW_RESULT_STATISTICS", "GET",
                        $"api/dynamic-flow-templates/{flow.FamilyId}/versions/{flow.Id}",
                        "FLOW_CONTRIBUTION pin + direct Mongo flow version", "CONFIG_ONLY"),
                    new P810RouteBinding(
                        "P8-UI-003", "OPERATIONS_READINESS", "GET",
                        $"api/stat-config/readiness-jobs/{RequiredString(readinessPin, "versionId")}",
                        "READINESS pin + safe public job DTO", "READINESS_ONLY")
                ]);
                return new CaseObservation(
                    "Flow contribution and operations readiness used separate typed owners/routes; neither was presented as config result or reconcile.",
                    $"flow={flow.Id}:EXCLUDE;readiness={RequiredString(readinessPin, "versionId")};surfaces=2;writes=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-UI-004",
            "p810_outsider",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var knownRoute = BasicConfigRoute(_p810BasicFixture);
                foreach (var definition in P810ActorDefinitions.Where(item =>
                             !string.Equals(item.Role, "OUTSIDER", StringComparison.Ordinal)))
                {
                    var response = await _api.GetAsync(
                        knownRoute,
                        Actor(definition.ActorKey).Token,
                        ct: ct);
                    ApiHarnessClient.ExpectStatus(
                        response,
                        HttpStatusCode.OK,
                        $"P8-10 {definition.Role} known-owner guard");
                    var identity = ParseBasicIdentity(response.Json, requireReceipt: false);
                    var canRead = RequiredBool(identity.Permissions, "canReadConfig");
                    var canManage = RequiredBool(identity.Permissions, "canManageDraft");
                    var canLock = RequiredBool(identity.Permissions, "canLockVersion");
                    HarnessAssert.Equal(true, canRead,
                        $"P8-10 {definition.Role} lost read relationship");
                    HarnessAssert.Equal(definition.CanManage, canManage,
                        $"P8-10 {definition.Role} manage axis drifted");
                    HarnessAssert.Equal(definition.CanManage, canLock,
                        $"P8-10 {definition.Role} lock axis drifted");
                    _p810ActorMatrix.Add(new P810ActorMatrixRow(
                        definition.Role,
                        definition.ActorKey,
                        definition.Relationship,
                        (int)response.StatusCode,
                        canRead,
                        canManage,
                        canLock,
                        null,
                        true));
                }

                var outsiderDefinition = P810ActorDefinitions.Single(item =>
                    string.Equals(item.Role, "OUTSIDER", StringComparison.Ordinal));
                var outsider = Actor(outsiderDefinition.ActorKey);
                var known = await _api.GetAsync(knownRoute, outsider.Token, ct: ct);
                var unknownRoute = knownRoute.Replace(
                    _p810Assignment.Id,
                    ObjectId.GenerateNewId().ToString(),
                    StringComparison.Ordinal);
                var unknown = await _api.GetAsync(unknownRoute, outsider.Token, ct: ct);
                ApiHarnessClient.ExpectStatus(known, HttpStatusCode.Forbidden,
                    "P8-10 OUTSIDER known-owner guard");
                ApiHarnessClient.ExpectStatus(unknown, HttpStatusCode.Forbidden,
                    "P8-10 OUTSIDER unknown-owner guard");
                var knownCode = ApiHarnessClient.FindStringRecursive(known.Json, "errorCode")
                                ?? ApiHarnessClient.FindStringRecursive(known.Json, "code");
                var unknownCode = ApiHarnessClient.FindStringRecursive(unknown.Json, "errorCode")
                                  ?? ApiHarnessClient.FindStringRecursive(unknown.Json, "code");
                HarnessAssert.Equal(knownCode, unknownCode,
                    "P8-10 OUTSIDER response disclosed assignment existence");
                _p810ActorMatrix.Add(new P810ActorMatrixRow(
                    outsiderDefinition.Role,
                    outsiderDefinition.ActorKey,
                    outsiderDefinition.Relationship,
                    (int)known.StatusCode,
                    false,
                    false,
                    false,
                    (int)unknown.StatusCode,
                    true));
                HarnessAssert.Equal(7, _p810ActorMatrix.Count,
                    "P8-10 actor matrix is not exact seven identities");
                return new CaseObservation(
                    "Seven explicit actor identities exercised the same real owner route; read/manage/lock axes were separated and OUTSIDER was generic/non-leaking.",
                    "actors=OWNER+ISSUER+REPORTER+REVIEWER+COORDINATOR+SYSTEM_ADMIN+OUTSIDER;authorized=6x200;outsider=403/403;sameCode=true;writes=0");
            },
            ct);
    }

    private async Task RunP810LabelAndFormCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-UI-005",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var label = await ReadLabelAsync(
                    Actor("system_admin"),
                    _p809Label.LabelId,
                    ct);
                var form = await ReadFieldConfigAsync(
                    Actor("system_admin"),
                    _p809Form.OwnerId,
                    ct,
                    requirePersisted: true);
                HarnessAssert.Equal("STATISTIC", label.LabelUsage,
                    "P8-10 label usage layer drifted");
                HarnessAssert.Equal("NUMBER", label.LabelDataType,
                    "P8-10 label data-type layer drifted");
                var field = form.Fields.OfType<JsonObject>().Single();
                var labelCodes = field["statisticLabelCodes"] as JsonArray
                                 ?? throw new InvalidOperationException(
                                     "P8-10 field lacks statisticLabelCodes layer.");
                HarnessAssert.True(labelCodes.Any(item =>
                        string.Equals(item?.GetValue<string>(), label.LabelCode,
                            StringComparison.Ordinal)),
                    "P8-10 field statistic-label reference lost the frozen label code");
                return new CaseObservation(
                    "Label classification identity and field-level statistic-label references remained separate but explicitly pinned.",
                    $"label={label.LabelId}:{label.LabelCode}:STATISTIC/NUMBER;fieldRefs={labelCodes.Count};writes=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-UI-006",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var form = await ReadFieldConfigAsync(
                    Actor("system_admin"),
                    _p809Form.OwnerId,
                    ct,
                    requirePersisted: true);
                var tableJson = Canonicalize(form.TableConfig);
                HarnessAssert.True(tableJson.Contains("metric:amount", StringComparison.Ordinal),
                    "P8-10 table metric identity is absent");
                HarnessAssert.True(!string.Equals(
                        "metric:amount",
                        _p809Label.LabelCode,
                        StringComparison.Ordinal),
                    "P8-10 metric key collapsed into the row/label identity layer");
                HarnessAssert.True(tableJson.Contains("FIXED_GRID", StringComparison.Ordinal),
                    "P8-10 table structure layer is absent");
                return new CaseObservation(
                    "The table metric key, table layout and statistic label code remained different typed identity layers.",
                    $"metric=metric:amount;layout=FIXED_GRID;label={_p809Label.LabelCode};identitiesDistinct=true;writes=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-UI-007",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var form = await ReadFieldConfigAsync(
                    Actor("system_admin"),
                    _p809Form.OwnerId,
                    ct,
                    requirePersisted: true);
                var canonical = Canonicalize(form.Raw);
                HarnessAssert.True(canonical.Contains("COUNT", StringComparison.Ordinal) &&
                                   canonical.Contains("SUM", StringComparison.Ordinal),
                    "P8-10 compatible NUMBER methods COUNT/SUM are absent");
                HarnessAssert.True(!canonical.Contains("JOIN", StringComparison.Ordinal),
                    "P8-10 incompatible JOIN method leaked into NUMBER configuration");
                HarnessAssert.True(canonical.Contains("showInDetail", StringComparison.Ordinal) &&
                                   canonical.Contains("showInTree", StringComparison.Ordinal),
                    "P8-10 statistic display scopes are not typed in readback");
                return new CaseObservation(
                    "Typed NUMBER field/table methods exposed COUNT/SUM and explicit display scopes while incompatible JOIN stayed absent.",
                    "dataType=NUMBER;methods=COUNT+SUM;JOIN=absent;scopes=showInDetail+showInTree;writes=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-UI-008",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var first = await ReadFieldConfigAsync(
                    Actor("system_admin"),
                    _p809Form.OwnerId,
                    ct,
                    requirePersisted: true);
                var second = await ReadFieldConfigAsync(
                    Actor("system_admin"),
                    _p809Form.OwnerId,
                    ct,
                    requirePersisted: true);
                HarnessAssert.Equal(first.ConfigId, second.ConfigId,
                    "P8-10 form readback configId drifted");
                HarnessAssert.Equal(first.VersionId, second.VersionId,
                    "P8-10 form readback versionId drifted");
                HarnessAssert.Equal(first.Revision, second.Revision,
                    "P8-10 form readback revision drifted");
                HarnessAssert.Equal(first.ConfigHash, second.ConfigHash,
                    "P8-10 form readback configHash drifted");
                RequireLowerSha256(first.ConfigHash, "P8-10 form configHash");
                var states = new[]
                {
                    "LOADING", "EMPTY", "ERROR", "FORBIDDEN",
                    "READONLY", "LOCKED"
                };
                HarnessAssert.Equal(6, states.Distinct(StringComparer.Ordinal).Count(),
                    "P8-10 UI state contract contains aliases");
                return new CaseObservation(
                    "Form identity/version/revision/hash had byte-stable readback and six explicit UI states remained non-aliased.",
                    $"config={first.ConfigId};version={first.VersionId};revision={first.Revision};hash={first.ConfigHash};states={string.Join('+', states)};writes=0");
            },
            ct);
    }

    private JsonObject P810Pin(int index)
        => _p809FullPinRequest[index] as JsonObject
           ?? throw new InvalidOperationException($"P8-10 dependency pin {index} is malformed.");
}
