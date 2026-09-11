using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private readonly Dictionary<string, P8FormFixture> _fieldFixtures =
        new(StringComparer.Ordinal);
    private P8ConfigIdentity _fieldValidLabel = default!;
    private P8ConfigIdentity _fieldInactiveLabel = default!;
    private P8ConfigIdentity _fieldWrongUsageLabel = default!;
    private P8ConfigIdentity _fieldWrongTypeLabel = default!;
    private P8ConfigIdentity _fieldAmbiguousGlobalLabel = default!;
    private P8ConfigIdentity _fieldAmbiguousUnitLabel = default!;
    private string _fieldReportId = default!;

    private async Task RunFieldCasesAsync(CancellationToken ct)
    {
        await SeedP802FixturesAsync(ct);

        await RunEvidenceCaseAsync(
            "P8-FLD-001",
            "system_admin",
            ["p8-fld-001-number"],
            FieldMutationWrites,
            FieldMutationWrites,
            async () =>
            {
                var fixture = FieldFixture("001");
                var field = fixture.Fields.Single();
                var (_, identity) = await PatchFieldConfigAsync(
                    Actor("system_admin"),
                    fixture,
                    "p8-fld-001-number",
                    FieldPayload(FieldPatch(
                        field.Id,
                        ["COUNT", "SUM", "AVG", "MIN", "MAX", "LATEST"],
                        showInDetail: true,
                        showInTree: false)),
                    ct);
                RequireFieldReadback(
                    identity,
                    field.Id,
                    "NUMBER",
                    ["COUNT", "SUM", "AVG", "MIN", "MAX", "LATEST"],
                    "NONE",
                    true,
                    false,
                    expectedSnapshotCount: 0);
                return new CaseObservation(
                    "NUMBER accepted its full frozen operation matrix with typed hints and stable readback.",
                    "type=NUMBER;ops=COUNT,SUM,AVG,MIN,MAX,LATEST;bucket=NONE;readback=stable;mongo+receipt+hash=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLD-002",
            "system_admin",
            ["p8-fld-002-date-full-date"],
            FieldMutationWrites,
            FieldMutationWrites,
            async () =>
            {
                var fixture = FieldFixture("002");
                var date = fixture.Field("date");
                var fullDate = fixture.Field("full-date");
                var (_, identity) = await PatchFieldConfigAsync(
                    Actor("system_admin"),
                    fixture,
                    "p8-fld-002-date-full-date",
                    FieldPayload(
                        FieldPatch(date.Id, ["COUNT", "MIN", "MAX", "LATEST"], "DATE", true, true),
                        FieldPatch(fullDate.Id, ["COUNT", "MIN", "MAX", "LATEST"], "NONE", false, true)),
                    ct);
                RequireFieldReadback(identity, date.Id, "DATE",
                    ["COUNT", "MIN", "MAX", "LATEST"], "DATE", true, true, expectedSnapshotCount: 0);
                RequireFieldReadback(identity, fullDate.Id, "FULL_DATE",
                    ["COUNT", "MIN", "MAX", "LATEST"], "NONE", false, true, expectedSnapshotCount: 0);
                return new CaseObservation(
                    "DATE and FULL_DATE accepted COUNT/MIN/MAX/LATEST and the compatible DATE/NONE buckets.",
                    "types=DATE,FULL_DATE;ops=COUNT,MIN,MAX,LATEST;buckets=DATE,NONE;typedHints=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLD-003",
            "system_admin",
            ["p8-fld-003-boolean"],
            FieldMutationWrites,
            FieldMutationWrites,
            async () =>
            {
                var fixture = FieldFixture("003");
                var field = fixture.Fields.Single();
                var (_, identity) = await PatchFieldConfigAsync(
                    Actor("system_admin"),
                    fixture,
                    "p8-fld-003-boolean",
                    FieldPayload(FieldPatch(
                        field.Id,
                        ["COUNT", "TRUE_COUNT", "FALSE_COUNT"],
                        "NONE",
                        true,
                        true)),
                    ct);
                RequireFieldReadback(identity, field.Id, "BOOLEAN",
                    ["COUNT", "TRUE_COUNT", "FALSE_COUNT"], "NONE", true, true,
                    expectedSnapshotCount: 0);
                return new CaseObservation(
                    "BOOLEAN accepted only COUNT/TRUE_COUNT/FALSE_COUNT with both frozen display hints.",
                    "type=BOOLEAN;ops=COUNT,TRUE_COUNT,FALSE_COUNT;bucket=NONE;hints=true,true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLD-004",
            "system_admin",
            ["p8-fld-004-choice-text-list"],
            FieldMutationWrites,
            FieldMutationWrites,
            async () =>
            {
                var fixture = FieldFixture("004");
                var single = fixture.Field("single");
                var multi = fixture.Field("multi");
                var shortText = fixture.Field("short");
                var longText = fixture.Field("long");
                var list = fixture.Field("list");
                var (_, identity) = await PatchFieldConfigAsync(
                    Actor("system_admin"),
                    fixture,
                    "p8-fld-004-choice-text-list",
                    FieldPayload(
                        FieldPatch(single.Id, ["COUNT", "BUCKET_COUNT", "LATEST"], "OPTION", true, false),
                        FieldPatch(multi.Id, ["COUNT", "BUCKET_COUNT"], "OPTION", false, true),
                        FieldPatch(shortText.Id, ["COUNT", "LATEST", "CONCAT"], "NONE", true, false),
                        FieldPatch(longText.Id, ["COUNT", "LATEST", "CONCAT"], "NONE", false, true),
                        FieldPatch(list.Id, ["COUNT"], "NONE", true, true)),
                    ct);
                RequireFieldReadback(identity, single.Id, "SINGLE_SELECT",
                    ["COUNT", "BUCKET_COUNT", "LATEST"], "OPTION", true, false, expectedSnapshotCount: 0);
                RequireFieldReadback(identity, multi.Id, "MULTI_SELECT",
                    ["COUNT", "BUCKET_COUNT"], "OPTION", false, true, expectedSnapshotCount: 0);
                RequireFieldReadback(identity, shortText.Id, "SHORT_TEXT",
                    ["COUNT", "LATEST", "CONCAT"], "NONE", true, false, expectedSnapshotCount: 0);
                RequireFieldReadback(identity, longText.Id, "LONG_TEXT",
                    ["COUNT", "LATEST", "CONCAT"], "NONE", false, true, expectedSnapshotCount: 0);
                RequireFieldReadback(identity, list.Id, "STRING_LIST",
                    ["COUNT"], "NONE", true, true, expectedSnapshotCount: 0);
                return new CaseObservation(
                    "Choice, text and STRING_LIST types accepted the exact compatible matrices without implicit methods.",
                    "single=COUNT,BUCKET_COUNT,LATEST;multi=COUNT,BUCKET_COUNT;text=COUNT,LATEST,CONCAT;stringList=COUNT");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLD-005",
            "system_admin",
            ["p8-fld-005-number-concat", "p8-fld-005-string-list-sum"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var fixture = FieldFixture("005");
                var actor = Actor("system_admin");
                var current = await ReadFieldConfigAsync(actor, fixture.Id, ct, requirePersisted: false);
                await RequireZeroWriteFieldRejectionAsync(
                    "P8-FLD-005/number-concat",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Id}/statistics",
                        Envelope("p8-fld-005-number-concat", current.Revision, current.ConfigHash,
                            FieldPayload(FieldPatch(fixture.Field("number").Id, ["CONCAT"]))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_OPERATION_INVALID",
                    "$.payload.fields[0].statistic.aggregateOps[0]",
                    "FIELD_STATISTIC_OPERATION_INCOMPATIBLE",
                    ct);
                await RequireZeroWriteFieldRejectionAsync(
                    "P8-FLD-005/string-list-sum",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Id}/statistics",
                        Envelope("p8-fld-005-string-list-sum", current.Revision, current.ConfigHash,
                            FieldPayload(FieldPatch(fixture.Field("list").Id, ["SUM"]))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_OPERATION_INVALID",
                    "$.payload.fields[0].statistic.aggregateOps[0]",
                    "FIELD_STATISTIC_OPERATION_INCOMPATIBLE",
                    ct);
                return new CaseObservation(
                    "Explicit incompatible NUMBER and STRING_LIST operations were rejected at the exact item path.",
                    "incompatibleOps=2;reason=FIELD_STATISTIC_OPERATION_INCOMPATIBLE;fallback=false;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLD-006",
            "system_admin",
            ["p8-fld-006-number-option", "p8-fld-006-date-option"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var fixture = FieldFixture("006");
                var actor = Actor("system_admin");
                var current = await ReadFieldConfigAsync(actor, fixture.Id, ct, requirePersisted: false);
                foreach (var probe in new[]
                         {
                             (Command: "p8-fld-006-number-option", Field: fixture.Field("number"), Ops: new[] { "COUNT" }),
                             (Command: "p8-fld-006-date-option", Field: fixture.Field("date"), Ops: new[] { "COUNT" })
                         })
                {
                    await RequireZeroWriteFieldRejectionAsync(
                        $"P8-FLD-006/{probe.Command}",
                        () => _api.PatchAsync(
                            $"api/dynamic-forms/{fixture.Id}/statistics",
                            Envelope(probe.Command, current.Revision, current.ConfigHash,
                                FieldPayload(FieldPatch(probe.Field.Id, probe.Ops, "OPTION"))),
                            actor.Token,
                            ct: ct),
                        HttpStatusCode.BadRequest,
                        "DYNAMIC_FORM_STATISTIC_BUCKET_MODE_INVALID",
                        "$.payload.fields[0].statistic.bucketMode",
                        "FIELD_STATISTIC_BUCKET_MODE_INCOMPATIBLE",
                        ct);
                }
                return new CaseObservation(
                    "OPTION bucket was rejected for NUMBER and DATE without bucket fallback.",
                    "incompatibleBuckets=2;reason=FIELD_STATISTIC_BUCKET_MODE_INCOMPATIBLE;fallback=false;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLD-007",
            "system_admin",
            ["p8-fld-007-rich-text", "p8-fld-007-list-mixed"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var fixture = FieldFixture("007");
                var actor = Actor("system_admin");
                var current = await ReadFieldConfigAsync(actor, fixture.Id, ct, requirePersisted: false);
                await RequireZeroWriteFieldRejectionAsync(
                    "P8-FLD-007/rich-text",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Id}/statistics",
                        Envelope("p8-fld-007-rich-text", current.Revision, current.ConfigHash,
                            FieldPayload(FieldPatch(fixture.Field("rich").Id, ["COUNT"]))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID",
                    "$.payload.fields[0].fieldId",
                    "FIELD_TYPE_STATISTIC_FORBIDDEN",
                    ct);
                await RequireZeroWriteFieldRejectionAsync(
                    "P8-FLD-007/string-list-mixed",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Id}/statistics",
                        Envelope("p8-fld-007-list-mixed", current.Revision, current.ConfigHash,
                            FieldPayload(FieldPatch(fixture.Field("list").Id, ["COUNT", "SUM"]))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_OPERATION_INVALID",
                    "$.payload.fields[0].statistic.aggregateOps[1]",
                    "FIELD_STATISTIC_OPERATION_INCOMPATIBLE",
                    ct);
                return new CaseObservation(
                    "RICH_TEXT and mixed STRING_LIST methods fail closed; accepted prefixes were not partially applied.",
                    "richText=forbidden;stringListMixed=rejected;partialApply=false;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLD-008",
            "system_admin",
            ["p8-fld-008-unknown", "p8-fld-008-duplicate-op", "p8-fld-008-malformed"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var fixture = FieldFixture("008");
                var actor = Actor("system_admin");
                var current = await ReadFieldConfigAsync(actor, fixture.Id, ct, requirePersisted: false);
                var field = fixture.Fields.Single();
                var unknown = FieldPatch(field.Id, ["COUNT"]);
                ((JsonObject)unknown["statistic"]!)["fallbackOperation"] = "COUNT";
                await RequireZeroWriteFieldRejectionAsync(
                    "P8-FLD-008/unknown",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Id}/statistics",
                        Envelope("p8-fld-008-unknown", current.Revision, current.ConfigHash, FieldPayload(unknown)),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID",
                    "$.payload.fields[0].statistic.fallbackOperation",
                    "SCHEMA_MISMATCH",
                    ct);

                await RequireZeroWriteFieldRejectionAsync(
                    "P8-FLD-008/duplicate-op",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Id}/statistics",
                        Envelope("p8-fld-008-duplicate-op", current.Revision, current.ConfigHash,
                            FieldPayload(FieldPatch(field.Id, ["COUNT", "count"]))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID",
                    "$.payload.fields[0].statistic.aggregateOps[1]",
                    "DUPLICATE_OPERATION",
                    ct);

                var malformed = FieldPatch(field.Id, ["COUNT"]);
                ((JsonObject)malformed["statistic"]!)["bucketMode"] = 7;
                await RequireZeroWriteFieldRejectionAsync(
                    "P8-FLD-008/malformed",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Id}/statistics",
                        Envelope("p8-fld-008-malformed", current.Revision, current.ConfigHash, FieldPayload(malformed)),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID",
                    "$.payload.fields[0].statistic.bucketMode",
                    "SCHEMA_MISMATCH",
                    ct);
                return new CaseObservation(
                    "Unknown properties, duplicate normalized operations and malformed typed values were rejected at exact paths.",
                    "strictUnknown=true;enumNormalization=true;duplicateOperationRejected=true;malformedTypedValue=true;fallback=false;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLD-009",
            "system_admin",
            ["p8-fld-009-thirty"],
            FieldMutationWrites,
            FieldMutationWrites,
            async () =>
            {
                var fixture = FieldFixture("009");
                var payload = FieldPayload(fixture.Fields
                    .Select(field => FieldPatch(field.Id, ["COUNT"]))
                    .ToArray());
                var (_, identity) = await PatchFieldConfigAsync(
                    Actor("system_admin"), fixture, "p8-fld-009-thirty", payload, ct);
                HarnessAssert.Equal(30, identity.Fields.Count,
                    "Exact 30-target configuration did not persist/read back 30 targets");
                RequireFieldReadback(identity, fixture.Fields[0].Id, "NUMBER",
                    ["COUNT"], "NONE", true, false, expectedSnapshotCount: 0);
                RequireFieldReadback(identity, fixture.Fields[^1].Id, "NUMBER",
                    ["COUNT"], "NONE", true, false, expectedSnapshotCount: 0);
                return new CaseObservation(
                    "Exactly 30 statistic field targets were accepted and returned stably.",
                    "targetCount=30;accepted=true;readbackCount=30;mongo+receipt+hash=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLD-010",
            "system_admin",
            ["p8-fld-010-thirty-one"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var fixture = FieldFixture("010");
                var actor = Actor("system_admin");
                var current = await ReadFieldConfigAsync(actor, fixture.Id, ct, requirePersisted: false);
                await RequireZeroWriteFieldRejectionAsync(
                    "P8-FLD-010/thirty-one",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Id}/statistics",
                        Envelope("p8-fld-010-thirty-one", current.Revision, current.ConfigHash,
                            FieldPayload(fixture.Fields
                                .Select(field => FieldPatch(field.Id, ["COUNT"]))
                                .ToArray())),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_TARGET_LIMIT_EXCEEDED",
                    "$.payload.fields",
                    "FIELD_STATISTIC_TARGET_LIMIT_30",
                    ct);
                return new CaseObservation(
                    "The 31st statistic target was rejected at the collection path with no partial owner/receipt write.",
                    "targetCount=31;accepted=false;path=$.payload.fields;reason=FIELD_STATISTIC_TARGET_LIMIT_30;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLD-011",
            "system_admin",
            [
                "p8-fld-011-valid",
                "p8-fld-011-inactive",
                "p8-fld-011-wrong-usage",
                "p8-fld-011-wrong-type",
                "p8-fld-011-ambiguous",
                "p8-fld-011-cross-field"
            ],
            FieldMutationWrites,
            FieldMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = FieldFixture("011");
                var first = fixture.Field("first");
                var second = fixture.Field("second");
                var (_, identity) = await PatchFieldConfigAsync(
                    actor,
                    fixture,
                    "p8-fld-011-valid",
                    FieldPayload(FieldPatch(
                        first.Id,
                        ["COUNT", "SUM"],
                        statisticLabelCodes:
                        [
                            $"  {_fieldValidLabel.LabelCode.ToUpperInvariant()}  ",
                            _fieldValidLabel.LabelCode
                        ])),
                    ct);
                RequireFieldReadback(identity, first.Id, "NUMBER",
                    ["COUNT", "SUM"], "NONE", true, false,
                    [_fieldValidLabel.LabelCode], 1);
                HarnessAssert.Equal(1, identity.DependencyPins.Count,
                    "One normalized label target must produce exactly one dependency pin");
                RequirePinnedFieldLabelSnapshot(identity, first.Id, _fieldValidLabel);

                foreach (var probe in new[]
                         {
                             (Command: "p8-fld-011-inactive", Code: _fieldInactiveLabel.LabelCode,
                                 ErrorCode: "DYNAMIC_FORM_LABEL_NOT_FOUND_OR_INACTIVE", Reason: "FIELD_STATISTIC_LABEL_NOT_FOUND_OR_INACTIVE"),
                             (Command: "p8-fld-011-wrong-usage", Code: _fieldWrongUsageLabel.LabelCode,
                                 ErrorCode: "DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID", Reason: "FIELD_STATISTIC_LABEL_USAGE_INCOMPATIBLE"),
                             (Command: "p8-fld-011-wrong-type", Code: _fieldWrongTypeLabel.LabelCode,
                                 ErrorCode: "DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID", Reason: "FIELD_STATISTIC_LABEL_TYPE_INCOMPATIBLE"),
                             (Command: "p8-fld-011-ambiguous", Code: _fieldAmbiguousGlobalLabel.LabelCode,
                                 ErrorCode: "DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID", Reason: "FIELD_STATISTIC_LABEL_AMBIGUOUS")
                         })
                {
                    await RequireZeroWriteFieldRejectionAsync(
                        $"P8-FLD-011/{probe.Command}",
                        () => _api.PatchAsync(
                            $"api/dynamic-forms/{fixture.Id}/statistics",
                            Envelope(probe.Command, identity.Revision, identity.ConfigHash,
                                FieldPayload(FieldPatch(first.Id, ["COUNT"],
                                    statisticLabelCodes: [probe.Code]))),
                            actor.Token,
                            ct: ct),
                        HttpStatusCode.BadRequest,
                        probe.ErrorCode,
                        "$.payload.fields[0].statisticLabelCodes[0]",
                        probe.Reason,
                        ct);
                }

                await RequireZeroWriteFieldRejectionAsync(
                    "P8-FLD-011/cross-field",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Id}/statistics",
                        Envelope("p8-fld-011-cross-field", identity.Revision, identity.ConfigHash,
                            FieldPayload(
                                FieldPatch(first.Id, ["COUNT"], statisticLabelCodes: [_fieldValidLabel.LabelCode]),
                                FieldPatch(second.Id, ["COUNT"], statisticLabelCodes: [_fieldValidLabel.LabelCode]))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_LABEL_STATISTIC_TARGET_CONFLICT",
                    "$.payload.fields",
                    "FIELD_STATISTIC_LABEL_TARGET_CONFLICT",
                    ct);
                return new CaseObservation(
                    "Active STATISTIC labels were pinned and duplicate set entries normalized; inactive, usage/type, ambiguous and cross-field targets rejected zero-write.",
                    "labelSnapshot=exact;declaredSetDistinct=true;dependencyPins=1;invalidProbes=5;deltaOnReject=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLD-012",
            "system_admin",
            ["p8-fld-012-structural"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = FieldFixture("012");
                var current = await ReadFieldConfigAsync(actor, fixture.Id, ct, requirePersisted: false);
                var field = fixture.Fields.Single();
                var structural = FieldPatch(field.Id, ["COUNT"]);
                structural["name"] = "Forbidden structural drift";
                await RequireZeroWriteFieldRejectionAsync(
                    "P8-FLD-012/patch-structural",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Id}/statistics",
                        Envelope("p8-fld-012-structural", current.Revision, current.ConfigHash,
                            FieldPayload(structural)),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID",
                    "$.payload.fields[0].name",
                    "SCHEMA_MISMATCH",
                    ct);

                var changedFields = FixtureFieldsWithStatistic(fixture, field.Id);
                await RequireZeroWriteFieldRejectionAsync(
                    "P8-FLD-012/put-alternate-writer",
                    () => _api.PutAsync(
                        $"api/dynamic-forms/{fixture.Id}",
                        new JsonObject
                        {
                            ["name"] = fixture.Name,
                            ["description"] = fixture.Description,
                            ["tagCodes"] = new JsonArray(),
                            ["schemaVersion"] = fixture.SchemaVersion,
                            ["sectionsJson"] = fixture.SectionsJson,
                            ["fieldsJson"] = changedFields.ToJsonString(),
                            ["excelBlockJson"] = null,
                            ["blocksJson"] = fixture.BlocksJson,
                            ["isActive"] = true,
                            ["expectedRevision"] = fixture.TemplateRevision
                        },
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_CONFIG_STRUCTURE_INVALID",
                    "$.schema.fields[0].isStatistic",
                    "USE_CANONICAL_STATISTICS_PATCH",
                    ct);

                await RequireZeroWriteFieldRejectionAsync(
                    "P8-FLD-012/post-alternate-writer",
                    () => _api.PostAsync(
                        "api/dynamic-forms",
                        new JsonObject
                        {
                            ["code"] = "P8_FLD_012_ALTERNATE_CREATE",
                            ["name"] = "P8 alternate writer create",
                            ["description"] = null,
                            ["tagCodes"] = new JsonArray(),
                            ["schemaVersion"] = fixture.SchemaVersion,
                            ["sectionsJson"] = null,
                            ["fieldsJson"] = null,
                            ["excelBlockJson"] = null,
                            ["blocksJson"] = null,
                            ["isActive"] = true,
                            ["schema"] = new JsonObject
                            {
                                ["sections"] = JsonNode.Parse(fixture.SectionsJson),
                                ["fields"] = changedFields.DeepClone(),
                                ["blocks"] = JsonNode.Parse(fixture.BlocksJson)
                            }
                        },
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_CONFIG_STRUCTURE_INVALID",
                    "$.schema.fields[0].isStatistic",
                    "USE_CANONICAL_STATISTICS_PATCH",
                    ct);
                return new CaseObservation(
                    "Structural PATCH fields and full-form PUT/POST alternate statistic writers were rejected at exact paths.",
                    "canonicalWriter=PATCH-/statistics;structuralPatch=400;alternatePut=400;alternatePost=400;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLD-013",
            "system_admin",
            ["p8-fld-013-report-replay"],
            FieldMutationWrites,
            FieldMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = FieldFixture("013");
                var field = fixture.Fields.Single();
                var reports = _database.GetCollection<BsonDocument>("work_assignment_reports");
                var reportBefore = await reports.Find(
                        Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(_fieldReportId)))
                    .SingleAsync(ct);
                var initial = await ReadFieldConfigAsync(actor, fixture.Id, ct, requirePersisted: false);
                var payload = FieldPayload(FieldPatch(field.Id, ["COUNT", "SUM"]));
                var first = await PatchFieldConfigAsync(
                    actor,
                    fixture,
                    "p8-fld-013-report-replay",
                    payload,
                    ct,
                    initial.Revision,
                    initial.ConfigHash);
                var afterFirst = await CaptureDatabaseSnapshotAsync(ct);
                var replay = await _api.PatchAsync(
                    $"api/dynamic-forms/{fixture.Id}/statistics",
                    Envelope("p8-fld-013-report-replay", initial.Revision, initial.ConfigHash, payload.DeepClone()),
                    actor.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(replay, HttpStatusCode.OK, "exact dynamic-form command replay");
                HarnessAssert.Equal(CanonicalResponse(first.Response), CanonicalResponse(replay),
                    "Exact dynamic-form replay response differs byte-semantically");
                var replayIdentity = ParseFieldIdentity(replay.Json, requireReceipt: true);
                RequireFieldIdentityContract(replayIdentity, fixture.Id);
                await RequireDirectFieldIdentityAsync(
                    replayIdentity,
                    "p8-fld-013-report-replay",
                    actor.Id,
                    ct);
                var afterReplay = await CaptureDatabaseSnapshotAsync(ct);
                VerifyCollectionContract(
                    "P8-FLD-013/replay",
                    BuildDeltas(afterFirst, afterReplay),
                    Array.Empty<string>(),
                    Array.Empty<string>());
                HarnessAssert.Equal(1L,
                    await CountFormReceiptsAsync(fixture.Id, "p8-fld-013-report-replay", ct),
                    "Exact replay wrote a second receipt");
                var reportAfter = await reports.Find(
                        Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(_fieldReportId)))
                    .SingleAsync(ct);
                HarnessAssert.Equal(Sha256(reportBefore.ToBson()), Sha256(reportAfter.ToBson()),
                    "Existing report changed during field config mutation/replay");
                return new CaseObservation(
                    "A pre-existing report stayed byte-stable and exact command replay reused one response/receipt without rebuild or result writes.",
                    "existingReports=1;reportDelta=zero;exactReplay=true;receiptCount=1;rebuild+results=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLD-014",
            "system_admin",
            ["p8-fld-014-published-isolation"],
            FieldMutationWrites,
            FieldMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = FieldFixture("014");
                var field = fixture.Fields.Single();
                var before = await RequireFormDocumentAsync(fixture.Id, ct);
                HarnessAssert.Equal(false, BsonBool(before, "isPublished"),
                    "P8-FLD-014 fixture must start as a draft");
                var (_, draft) = await PatchFieldConfigAsync(
                    actor,
                    fixture,
                    "p8-fld-014-published-isolation",
                    FieldPayload(FieldPatch(field.Id, ["COUNT", "SUM", "AVG"], "NONE", true, true)),
                    ct);
                HarnessAssert.Equal("DRAFT", draft.Status,
                    "Draft statistic PATCH did not return DRAFT status");
                RequireFieldReadback(draft, field.Id, "NUMBER",
                    ["COUNT", "SUM", "AVG"], "NONE", true, true, expectedSnapshotCount: 0);
                var afterPatch = await RequireFormDocumentAsync(fixture.Id, ct);
                HarnessAssert.Equal(BsonInt(before, "revision") + 1, BsonInt(afterPatch, "revision"),
                    "Draft statistic PATCH did not advance owner revision exactly once");
                HarnessAssert.Equal(
                    CanonicalFieldStructure(BsonString(before, "fieldsJson")),
                    CanonicalFieldStructure(BsonString(afterPatch, "fieldsJson")),
                    "Draft statistic PATCH changed field structure");

                var beforePublish = await CaptureDatabaseSnapshotAsync(ct);
                var publish = await _api.PostAsync(
                    $"api/dynamic-forms/{fixture.Id}/publish",
                    new JsonObject { ["expectedRevision"] = BsonInt(afterPatch, "revision") },
                    actor.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(publish, HttpStatusCode.OK,
                    "publish draft with statistic config");
                HarnessAssert.True(ApiHarnessClient.RequiredBool(publish.Json, "isPublished"),
                    "Form publish response is not published");
                var afterPublishSnapshot = await CaptureDatabaseSnapshotAsync(ct);
                VerifyCollectionContract(
                    "P8-FLD-014/publish-transition",
                    BuildDeltas(beforePublish, afterPublishSnapshot),
                    [DynamicFormsCollection],
                    [DynamicFormsCollection]);

                var locked = await ReadFieldConfigAsync(actor, fixture.Id, ct, requirePersisted: true);
                RequireSameFieldConfigContent(draft, locked);
                HarnessAssert.Equal("LOCKED", locked.Status,
                    "Published form current statistic config is not LOCKED");
                var lockedSnapshot = locked.Versions.OfType<JsonObject>()
                    .Single(version => RequiredInt(version, "versionNo") == locked.VersionNo);
                HarnessAssert.Equal("LOCKED", RequiredString(lockedSnapshot, "status"),
                    "Published form current statistic version snapshot is not LOCKED");
                RequireFieldReadback(locked, field.Id, "NUMBER",
                    ["COUNT", "SUM", "AVG"], "NONE", true, true, expectedSnapshotCount: 0);

                var after = await RequireFormDocumentAsync(fixture.Id, ct);
                foreach (var property in new[]
                         {
                             "code", "name", "description", "tagCodes", "schemaVersion", "versionNo",
                             "familyId", "previousVersionId", "clonedFromVersionId", "lineageStatus",
                             "wrapReuseKey", "isActive", "sectionsJson", "excelBlockJson", "blocksJson",
                             "excelBlockDynamicExcelTemplateId"
                         })
                {
                    HarnessAssert.True(
                        before.GetValue(property, BsonNull.Value).Equals(after.GetValue(property, BsonNull.Value)),
                        $"Draft PATCH/publish changed structure/lineage property {property}");
                }
                HarnessAssert.True(BsonBool(after, "isPublished") == true,
                    "Draft form was not published");
                HarnessAssert.True(
                    afterPatch.GetValue("fieldsJson", BsonNull.Value).Equals(after.GetValue("fieldsJson", BsonNull.Value)),
                    "Form publish rewrote field statistic content");
                HarnessAssert.Equal(BsonInt(afterPatch, "revision") + 1, BsonInt(after, "revision"),
                    "Form publish did not advance owner revision exactly once");
                var typed = await _database.GetCollection<DynamicFormTemplate>(DynamicFormsCollection)
                    .Find(form => form.Id == fixture.Id)
                    .SingleAsync(ct);
                var validated = DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(typed);
                HarnessAssert.Equal(typed.PublishedSchemaSnapshotJson, validated.Json,
                    "Published canonical structure snapshot is not exact");
                HarnessAssert.Equal(typed.PublishedSchemaHash, validated.Sha256,
                    "Published canonical structure hash is not exact");
                return new CaseObservation(
                    "Draft statistic PATCH preserved table/structure, then form publish atomically locked the exact current config without job/result writes.",
                    "draftPatch=DRAFT;publish=LOCKED;configContent+hash=exact;currentSnapshot=LOCKED;structure+lineage=exact;publishDelta=formOnly");
            },
            ct);
    }

    private async Task SeedP802FixturesAsync(CancellationToken ct)
    {
        await SeedP802LabelsAsync(ct);
        var fixtures = new[]
        {
            NewFieldFixture("001", [FixtureField("number", "number")]),
            NewFieldFixture("002", [FixtureField("date", "date"), FixtureField("full-date", "fullDate")]),
            NewFieldFixture("003", [FixtureField("boolean", "boolean")]),
            NewFieldFixture("004",
            [
                FixtureField("single", "singleSelect", options: true),
                FixtureField("multi", "multiSelect", options: true),
                FixtureField("short", "shortText"),
                FixtureField("long", "longText"),
                FixtureField("list", "stringList")
            ]),
            NewFieldFixture("005", [FixtureField("number", "number"), FixtureField("list", "stringList")]),
            NewFieldFixture("006", [FixtureField("number", "number"), FixtureField("date", "date")]),
            NewFieldFixture("007", [FixtureField("rich", "richText"), FixtureField("list", "stringList")]),
            NewFieldFixture("008", [FixtureField("number", "number")]),
            NewFieldFixture("009", Enumerable.Range(1, 30)
                .Select(index => FixtureField($"target-{index:00}", "number")).ToArray()),
            NewFieldFixture("010", Enumerable.Range(1, 31)
                .Select(index => FixtureField($"target-{index:00}", "number")).ToArray()),
            NewFieldFixture("011", [FixtureField("first", "number"), FixtureField("second", "number")]),
            NewFieldFixture("012", [FixtureField("number", "number")]),
            NewFieldFixture("013", [FixtureField("number", "number")]),
            NewFieldFixture("014", [FixtureField("number", "number")])
        };
        foreach (var fixture in fixtures)
            _fieldFixtures.Add(fixture.Key, fixture);

        await _database.GetCollection<DynamicFormTemplate>(DynamicFormsCollection)
            .InsertManyAsync(fixtures.Select(fixture => fixture.Template), cancellationToken: ct);

        _fieldReportId = ObjectId.GenerateNewId().ToString();
        var reportFixture = new BsonDocument
        {
            ["_id"] = ObjectId.Parse(_fieldReportId),
            ["dynamicFormTemplateId"] = ObjectId.Parse(FieldFixture("013").Id),
            ["dynamicFormVersionId"] = ObjectId.Parse(FieldFixture("013").Id),
            ["status"] = "DRAFT",
            ["payloadJson"] = "{}",
            ["createdByUserId"] = ObjectId.Parse(_adminId),
            ["updatedByUserId"] = ObjectId.Parse(_adminId),
            ["createdAtUtc"] = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
            ["updatedAtUtc"] = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
            ["isDeleted"] = false
        };
        await _database.GetCollection<BsonDocument>("work_assignment_reports")
            .InsertOneAsync(reportFixture, cancellationToken: ct);

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-02-fixtures.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                forms = fixtures.Select(fixture => new
                {
                    fixture.Key,
                    fixture.Id,
                    fixture.Code,
                    fieldCount = fixture.Fields.Count,
                    fixture.Template.IsPublished,
                    fixture.PublishedSchemaHash
                }),
                labels = new[]
                {
                    _fieldValidLabel.LabelId,
                    _fieldInactiveLabel.LabelId,
                    _fieldWrongUsageLabel.LabelId,
                    _fieldWrongTypeLabel.LabelId,
                    _fieldAmbiguousGlobalLabel.LabelId,
                    _fieldAmbiguousUnitLabel.LabelId
                },
                existingReportId = _fieldReportId,
                fixtureWritesOutsideCaseDeltas = true
            },
            ct);
    }

    private async Task SeedP802LabelsAsync(CancellationToken ct)
    {
        var actor = Actor("system_admin");
        (_, _fieldValidLabel) = await CreateLabelAsync(
            actor,
            "p8-fld-setup-valid",
            LabelPayload("p8.fld.valid.number", "P8 valid number concept", "GLOBAL", null,
                usage: "STATISTIC", dataType: "NUMBER"),
            ct);
        var (_, inactiveActive) = await CreateLabelAsync(
            actor,
            "p8-fld-setup-inactive-create",
            LabelPayload("p8.fld.inactive.number", "P8 inactive number concept", "GLOBAL", null,
                usage: "STATISTIC", dataType: "NUMBER"),
            ct);
        (_, _fieldInactiveLabel) = await UpdateLabelAsync(
            actor,
            inactiveActive,
            "p8-fld-setup-inactive-disable",
            LabelPayload("p8.fld.inactive.number", "P8 inactive number concept", "GLOBAL", null,
                usage: "STATISTIC", dataType: "NUMBER", isActive: false),
            ct);
        (_, _fieldWrongUsageLabel) = await CreateLabelAsync(
            actor,
            "p8-fld-setup-wrong-usage",
            LabelPayload("p8.fld.wrong.usage", "P8 wrong usage", "GLOBAL", null,
                usage: "CLASSIFICATION", dataType: "NUMBER"),
            ct);
        (_, _fieldWrongTypeLabel) = await CreateLabelAsync(
            actor,
            "p8-fld-setup-wrong-type",
            LabelPayload("p8.fld.wrong.type", "P8 wrong type", "GLOBAL", null,
                usage: "STATISTIC", dataType: "BOOLEAN"),
            ct);
        (_, _fieldAmbiguousGlobalLabel) = await CreateLabelAsync(
            actor,
            "p8-fld-setup-ambiguous-global",
            LabelPayload("p8.fld.ambiguous", "P8 ambiguous global", "GLOBAL", null,
                usage: "STATISTIC", dataType: "NUMBER"),
            ct);
        (_, _fieldAmbiguousUnitLabel) = await CreateLabelAsync(
            actor,
            "p8-fld-setup-ambiguous-unit",
            LabelPayload("p8.fld.ambiguous", "P8 ambiguous unit", "UNIT", _unitAId,
                usage: "STATISTIC", dataType: "NUMBER"),
            ct);
    }

    private P8FormFixture NewFieldFixture(
        string key,
        IReadOnlyList<P8FixtureField> fields,
        bool published = false)
    {
        var id = ObjectId.GenerateNewId().ToString();
        const string sectionsJson =
            "[{\"id\":\"main\",\"title\":\"P8 statistic section\",\"description\":null,\"tagCodes\":[],\"order\":0}]";
        var fieldNodes = new JsonArray(fields.Select((field, index) =>
        {
            var node = new JsonObject
            {
                ["id"] = field.Id,
                ["sectionId"] = "main",
                ["key"] = field.Key,
                ["name"] = $"P8 {field.Key}",
                ["type"] = field.SchemaType,
                ["required"] = false,
                ["order"] = index,
                ["statisticLabelCodes"] = new JsonArray(),
                ["isStatistic"] = false
            };
            if (field.HasOptions)
            {
                node["options"] = new JsonArray(
                    new JsonObject { ["code"] = "alpha", ["label"] = "Alpha" },
                    new JsonObject { ["code"] = "beta", ["label"] = "Beta" });
            }
            return (JsonNode)node;
        }).ToArray());
        var fieldsJson = fieldNodes.ToJsonString();
        const string blocksJson = "[]";
        var fixedAt = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
        var template = new DynamicFormTemplate
        {
            Id = id,
            Code = $"P8_FLD_{key}",
            Name = $"P8 field fixture {key}",
            Description = $"P8-FLD-{key} isolated integration fixture",
            TagCodes = [],
            CreatedByUsername = Actor("system_admin").Username,
            SchemaVersion = 1,
            VersionNo = 1,
            FamilyId = id,
            PreviousVersionId = null,
            ClonedFromVersionId = null,
            LineageStatus = DynamicFormLineageStatuses.Root,
            Revision = 1,
            IsActive = true,
            IsPublished = published,
            PublishedAtUtc = published ? fixedAt : null,
            PublishedByUserId = published ? _adminId : null,
            SectionsJson = sectionsJson,
            FieldsJson = fieldsJson,
            ExcelBlockJson = null,
            BlocksJson = blocksJson,
            CreatedByUserId = _adminId,
            UpdatedByUserId = _adminId,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };
        string? snapshotJson = null;
        string? snapshotHash = null;
        if (published)
        {
            var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(template);
            snapshotJson = snapshot.Json;
            snapshotHash = snapshot.Sha256;
            template.PublishedSchemaSnapshotJson = snapshotJson;
            template.PublishedSchemaHash = snapshotHash;
        }
        return new P8FormFixture(
            key,
            id,
            template.Code,
            template.Name,
            template.Description,
            template.SchemaVersion,
            template.Revision,
            sectionsJson,
            fieldsJson,
            blocksJson,
            fields,
            snapshotJson,
            snapshotHash,
            template);
    }

    private static P8FixtureField FixtureField(string key, string schemaType, bool options = false)
        => new($"fld-{key}", key.Replace('-', '_'), schemaType, options);

    private P8FormFixture FieldFixture(string key)
        => _fieldFixtures.TryGetValue(key, out var fixture)
            ? fixture
            : throw new HarnessCaseNotRunnableException($"P8 field fixture {key} was not seeded.");

    private static JsonArray FixtureFieldsWithStatistic(P8FormFixture fixture, string fieldId)
    {
        var fields = JsonNode.Parse(fixture.FieldsJson) as JsonArray
                     ?? throw new InvalidOperationException("Fixture fields are not an array.");
        var field = fields.OfType<JsonObject>()
            .Single(item => string.Equals(OptionalString(item, "id"), fieldId, StringComparison.Ordinal));
        field["isStatistic"] = true;
        field["statisticLabelCodes"] = new JsonArray();
        field["statistic"] = FieldStatistic(["COUNT"]);
        return fields;
    }

    private static string CanonicalFieldStructure(string? fieldsJson)
    {
        var fields = JsonNode.Parse(fieldsJson ?? "[]") as JsonArray
                     ?? throw new InvalidOperationException("Dynamic Form fields are not an array.");
        foreach (var field in fields.OfType<JsonObject>())
        {
            field.Remove("isStatistic");
            field.Remove("statisticLabelCodes");
            field.Remove("statistic");
        }
        return Canonicalize(fields);
    }

    private static void RequireSameFieldConfigContent(
        P8FieldConfigIdentity before,
        P8FieldConfigIdentity after)
    {
        HarnessAssert.Equal(before.OwnerId, after.OwnerId, "Publish changed field ownerId");
        HarnessAssert.Equal(before.ConfigId, after.ConfigId, "Publish changed configId");
        HarnessAssert.Equal(before.VersionId, after.VersionId, "Publish changed versionId");
        HarnessAssert.Equal(before.VersionNo, after.VersionNo, "Publish changed versionNo");
        HarnessAssert.Equal(before.Revision, after.Revision, "Publish changed config revision");
        HarnessAssert.Equal(before.ConfigHash, after.ConfigHash, "Publish changed configHash");
        HarnessAssert.Equal(before.FieldSectionHash, after.FieldSectionHash,
            "Publish changed fieldSectionHash");
        HarnessAssert.Equal(before.TableSectionHash, after.TableSectionHash,
            "Publish changed tableSectionHash");
        HarnessAssert.True(before.DependencyPins.SequenceEqual(after.DependencyPins, StringComparer.Ordinal),
            "Publish changed dependency pins");
        HarnessAssert.Equal(Canonicalize(before.Fields), Canonicalize(after.Fields),
            "Publish changed field config content");
        HarnessAssert.Equal(Canonicalize(before.TableConfig), Canonicalize(after.TableConfig),
            "Publish changed table config content");
        HarnessAssert.Equal(before.Versions.Count, after.Versions.Count,
            "Publish appended a statistic config version");
        var beforeCurrent = (JsonObject)before.Versions.OfType<JsonObject>()
            .Single(version => RequiredInt(version, "versionNo") == before.VersionNo).DeepClone();
        var afterCurrent = (JsonObject)after.Versions.OfType<JsonObject>()
            .Single(version => RequiredInt(version, "versionNo") == after.VersionNo).DeepClone();
        beforeCurrent.Remove("status");
        afterCurrent.Remove("status");
        HarnessAssert.Equal(Canonicalize(beforeCurrent), Canonicalize(afterCurrent),
            "Publish changed current statistic snapshot content beyond status promotion");
    }

    private static void RequirePinnedFieldLabelSnapshot(
        P8FieldConfigIdentity identity,
        string fieldId,
        P8ConfigIdentity label)
    {
        var field = identity.Fields.OfType<JsonObject>()
            .Single(item => string.Equals(RequiredString(item, "fieldId"), fieldId, StringComparison.Ordinal));
        var snapshots = field["labelSnapshots"] as JsonArray
                        ?? throw new InvalidOperationException("Field labelSnapshots is absent.");
        var snapshot = snapshots.OfType<JsonObject>().Single();
        HarnessAssert.Equal(label.LabelId, RequiredString(snapshot, "labelId"), "Pinned field labelId mismatch");
        HarnessAssert.Equal(label.LabelCode, RequiredString(snapshot, "code"), "Pinned field label code mismatch");
        HarnessAssert.Equal("STATISTIC", RequiredString(snapshot, "usage"), "Pinned field label usage mismatch");
        HarnessAssert.Equal("NUMBER", RequiredString(snapshot, "dataType"), "Pinned field label type mismatch");
        HarnessAssert.Equal(label.LabelScopeType, RequiredString(snapshot, "scopeType"),
            "Pinned field label scopeType mismatch");
        HarnessAssert.Equal(label.LabelScopeId, OptionalString(snapshot, "scopeId"),
            "Pinned field label scopeId mismatch");
        HarnessAssert.Equal(label.VersionNo, RequiredInt(snapshot, "versionNo"),
            "Pinned field label versionNo mismatch");
        HarnessAssert.Equal(label.VersionId, RequiredString(snapshot, "versionId"),
            "Pinned field label versionId mismatch");
        HarnessAssert.Equal(label.ConfigHash, RequiredString(snapshot, "configHash"),
            "Pinned field label configHash mismatch");
        HarnessAssert.True(RequiredBool(snapshot, "isActive"), "Pinned field label must be active");
    }
}

internal sealed record P8FixtureField(
    string Id,
    string Key,
    string SchemaType,
    bool HasOptions);

internal sealed record P8FormFixture(
    string Key,
    string Id,
    string Code,
    string Name,
    string? Description,
    int SchemaVersion,
    int TemplateRevision,
    string SectionsJson,
    string FieldsJson,
    string BlocksJson,
    IReadOnlyList<P8FixtureField> Fields,
    string? PublishedSchemaSnapshotJson,
    string? PublishedSchemaHash,
    DynamicFormTemplate Template)
{
    public P8FixtureField Field(string key)
        => Fields.Single(field => string.Equals(field.Key, key.Replace('-', '_'), StringComparison.Ordinal));
}
