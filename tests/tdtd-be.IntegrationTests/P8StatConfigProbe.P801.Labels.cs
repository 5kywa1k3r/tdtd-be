using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private string _enumCatalogId = default!;
    private P8ConfigIdentity _lblGlobal = default!;
    private P8ConfigIdentity _lblLevel = default!;
    private P8ConfigIdentity _lblUnit = default!;
    private P8ConfigIdentity _lblLifecycle = default!;
    private P8ConfigIdentity _lblLifecycleActive = default!;
    private P8ConfigIdentity _lblReferenced = default!;
    private P8ConfigIdentity _lblTombstone = default!;
    private P8ConfigIdentity _lblPinned = default!;

    private async Task RunLabelCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-LBL-001",
            "system_admin",
            [
                "p8-lbl-001-global",
                "p8-lbl-001-lifecycle",
                "p8-lbl-001-referenced",
                "p8-lbl-001-tombstone"
            ],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                var admin = Actor("system_admin");
                _lblGlobal = (await CreateLabelAsync(
                    admin,
                    "p8-lbl-001-global",
                    LabelPayload("p8.lbl.global", "P8 Global", "GLOBAL", null),
                    ct)).Identity;
                _lblLifecycle = (await CreateLabelAsync(
                    admin,
                    "p8-lbl-001-lifecycle",
                    LabelPayload("p8.lbl.lifecycle", "P8 Lifecycle", "GLOBAL", null,
                        usage: "STATISTIC", dataType: "NUMBER"),
                    ct)).Identity;
                _lblLifecycleActive = _lblLifecycle;
                _lblReferenced = (await CreateLabelAsync(
                    admin,
                    "p8-lbl-001-referenced",
                    LabelPayload("p8.lbl.referenced", "P8 Referenced", "GLOBAL", null,
                        usage: "STATISTIC", dataType: "NUMBER"),
                    ct)).Identity;
                _lblTombstone = (await CreateLabelAsync(
                    admin,
                    "p8-lbl-001-tombstone",
                    LabelPayload("p8.lbl.tombstone", "P8 Tombstone", "GLOBAL", null,
                        usage: "STATISTIC", dataType: "BOOLEAN"),
                    ct)).Identity;
                HarnessAssert.True(
                    new[] { _lblGlobal, _lblLifecycle, _lblReferenced, _lblTombstone }
                        .All(label => label.LabelScopeType == "GLOBAL" && label.LabelScopeId is null),
                    "SYSTEM_ADMIN global fixture scope mismatch");
                return new CaseObservation(
                    "SYSTEM_ADMIN created canonical GLOBAL labels through additive config API.",
                    "globalOwners=4;scopeId=null;ownerReceiptDelta=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-LBL-002",
            "level_manager",
            ["p8-lbl-002-level"],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                _lblLevel = (await CreateLabelAsync(
                    Actor("level_manager"),
                    "p8-lbl-002-level",
                    LabelPayload("p8.lbl.level", "P8 Level", "LEVEL", _levelUnitId,
                        usage: "STATISTIC", dataType: "DATE"),
                    ct)).Identity;
                HarnessAssert.Equal("LEVEL", _lblLevel.LabelScopeType, "LEVEL scope type mismatch");
                HarnessAssert.Equal(_levelUnitId, _lblLevel.LabelScopeId, "LEVEL scope id mismatch");
                return new CaseObservation(
                    "LEVEL_MANAGER created only its exact LEVEL-scoped label.",
                    "scope=LEVEL;scopeOwner=actorUnit");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-LBL-003",
            "unit_manager_a",
            ["p8-lbl-003-unit"],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                _lblUnit = (await CreateLabelAsync(
                    Actor("unit_manager_a"),
                    "p8-lbl-003-unit",
                    LabelPayload("p8.lbl.unit", "P8 Unit", "UNIT", _unitAId,
                        usage: "TABLE_TARGET", dataType: "STRING_LIST"),
                    ct)).Identity;
                HarnessAssert.Equal("UNIT", _lblUnit.LabelScopeType, "UNIT scope type mismatch");
                HarnessAssert.Equal(_unitAId, _lblUnit.LabelScopeId, "UNIT scope id mismatch");
                return new CaseObservation(
                    "MANAGER_UNIT created only its exact UNIT-scoped label.",
                    "scope=UNIT;scopeOwner=managedUnit");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-LBL-004",
            "visibility-matrix",
            [],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var ordinary = Actor("ordinary_a");
                var outsider = Actor("outsider_b");
                var levelManager = Actor("level_manager");
                _ = await ReadLabelAsync(ordinary, _lblGlobal.LabelId, ct);
                _ = await ReadLabelAsync(ordinary, _lblUnit.LabelId, ct);
                _ = await ReadLabelAsync(outsider, _lblGlobal.LabelId, ct);
                _ = await ReadLabelAsync(levelManager, _lblGlobal.LabelId, ct);
                _ = await ReadLabelAsync(levelManager, _lblLevel.LabelId, ct);

                var ordinaryLevel = await _api.GetAsync(
                    $"api/labels/{_lblLevel.LabelId}/config",
                    ordinary.Token,
                    ct: ct);
                ExpectFailure(ordinaryLevel, HttpStatusCode.NotFound, "LABEL_NOT_FOUND");
                var outsiderUnit = await _api.GetAsync(
                    $"api/labels/{_lblUnit.LabelId}/config",
                    outsider.Token,
                    ct: ct);
                ExpectFailure(outsiderUnit, HttpStatusCode.NotFound, "LABEL_NOT_FOUND");
                return new CaseObservation(
                    "GLOBAL/LEVEL/UNIT visibility matrix allowed only scoped readers and hid foreign owners.",
                    "globalVisible=true;ownUnitVisible=true;foreignLevel404=true;foreignUnit404=true");
            },
            ct);

        var typeCommands = new[]
        {
            "p8-lbl-005-number",
            "p8-lbl-005-short-text",
            "p8-lbl-005-string-list",
            "p8-lbl-005-long-text",
            "p8-lbl-005-date",
            "p8-lbl-005-boolean"
        };
        await RunEvidenceCaseAsync(
            "P8-LBL-005",
            "system_admin",
            typeCommands,
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                var dataTypes = new[]
                {
                    "NUMBER",
                    "SHORT_TEXT",
                    "STRING_LIST",
                    "LONG_TEXT",
                    "DATE",
                    "BOOLEAN"
                };
                var actual = new List<string>();
                for (var index = 0; index < dataTypes.Length; index++)
                {
                    var identity = (await CreateLabelAsync(
                        Actor("system_admin"),
                        typeCommands[index],
                        LabelPayload(
                            $"p8.lbl.type.{dataTypes[index].ToLowerInvariant().Replace('_', '-')}",
                            $"P8 Type {dataTypes[index]}",
                            "GLOBAL",
                            null,
                            usage: "STATISTIC",
                            dataType: dataTypes[index]),
                        ct)).Identity;
                    actual.Add(identity.LabelDataType);
                }
                HarnessAssert.True(dataTypes.SequenceEqual(actual, StringComparer.Ordinal),
                    "Six frozen datatypes did not round-trip exactly");
                return new CaseObservation(
                    "All six and only the frozen datatype spellings round-tripped exactly.",
                    "types=NUMBER|SHORT_TEXT|STRING_LIST|LONG_TEXT|DATE|BOOLEAN");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-LBL-006",
            "system_admin",
            ["p8-lbl-006-classification", "p8-lbl-006-statistic", "p8-lbl-006-table-target"],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                var fixtures = new[]
                {
                    (Command: "p8-lbl-006-classification", Code: " P8.Lbl-Usage_Class.01 ", Usage: "CLASSIFICATION"),
                    (Command: "p8-lbl-006-statistic", Code: " P8.Lbl-Usage_Stat.02 ", Usage: "STATISTIC"),
                    (Command: "p8-lbl-006-table-target", Code: " P8.Lbl-Usage_Table.03 ", Usage: "TABLE_TARGET")
                };
                var created = new List<P8ConfigIdentity>();
                foreach (var fixture in fixtures)
                {
                    created.Add((await CreateLabelAsync(
                        Actor("system_admin"),
                        fixture.Command,
                        LabelPayload(fixture.Code, fixture.Usage, "GLOBAL", null,
                            usage: fixture.Usage, dataType: "NUMBER"),
                        ct)).Identity);
                }
                HarnessAssert.True(
                    created.Select(item => item.LabelUsage)
                        .SequenceEqual(fixtures.Select(item => item.Usage), StringComparer.Ordinal),
                    "Three frozen usages did not round-trip exactly");
                HarnessAssert.True(
                    created.All(item => System.Text.RegularExpressions.Regex.IsMatch(
                        item.LabelCode,
                        "^[a-z0-9][a-z0-9_.-]{0,63}$")),
                    "Normalized code violates frozen regex");
                return new CaseObservation(
                    "Three usages and trim/lowercase code regex normalized exactly.",
                    "usages=CLASSIFICATION|STATISTIC|TABLE_TARGET;codeRegex=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-LBL-007",
            "system_admin",
            [
                "p8-lbl-007-legacy-type",
                "p8-lbl-007-legacy-usage",
                "p8-lbl-007-unknown-scope",
                "p8-lbl-007-legacy-source"
            ],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var admin = Actor("system_admin");
                var legacyType = await _api.PostAsync(
                    "api/labels/config",
                    Envelope("p8-lbl-007-legacy-type", 0, EmptyConfigHash,
                        LabelPayload("p8.lbl.legacy-type", "Legacy type", "GLOBAL", null,
                            usage: "STATISTIC", dataType: "TEXT")),
                    admin.Token,
                    ct: ct);
                ExpectFailure(legacyType, HttpStatusCode.BadRequest, "LABEL_DATA_TYPE_INVALID");
                var legacyUsage = await _api.PostAsync(
                    "api/labels/config",
                    Envelope("p8-lbl-007-legacy-usage", 0, EmptyConfigHash,
                        LabelPayload("p8.lbl.legacy-usage", "Legacy usage", "GLOBAL", null,
                            usage: "TAG", dataType: "NUMBER")),
                    admin.Token,
                    ct: ct);
                ExpectFailure(legacyUsage, HttpStatusCode.BadRequest, "LABEL_USAGE_INVALID");
                var unknownScope = await _api.PostAsync(
                    "api/labels/config",
                    Envelope("p8-lbl-007-unknown-scope", 0, EmptyConfigHash,
                        LabelPayload("p8.lbl.unknown-scope", "Unknown scope", "ORG", _unitAId)),
                    admin.Token,
                    ct: ct);
                ExpectFailure(unknownScope, HttpStatusCode.BadRequest, "LABEL_SCOPE_TYPE_INVALID");
                var legacySource = await _api.PostAsync(
                    "api/labels/config",
                    Envelope("p8-lbl-007-legacy-source", 0, EmptyConfigHash,
                        LabelPayload("p8.lbl.legacy-source", "Legacy source", "GLOBAL", null,
                            usage: "STATISTIC", dataType: "SHORT_TEXT", valueSourceType: "LIST")),
                    admin.Token,
                    ct: ct);
                ExpectFailure(legacySource, HttpStatusCode.BadRequest, "LABEL_VALUE_SOURCE_TYPE_INVALID");
                return new CaseObservation(
                    "Legacy aliases and unknown scope/type/usage/source values were rejected without fallback.",
                    "legacyFallback=false;rejections=4;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-LBL-008",
            "system_admin",
            ["p8-lbl-008-deactivate"],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                var before = _lblLifecycle;
                var lockedBefore = (JsonArray)before.Versions.DeepClone();
                _lblLifecycle = (await UpdateLabelAsync(
                    Actor("system_admin"),
                    before,
                    "p8-lbl-008-deactivate",
                    LabelPayload(before.LabelCode, "P8 Lifecycle", "GLOBAL", null,
                        usage: "STATISTIC", dataType: "NUMBER", isActive: false),
                    ct)).Identity;
                RequireVersionAdvanced(before, _lblLifecycle);
                HarnessAssert.Equal("INACTIVE", _lblLifecycle.Status, "Deactivate status mismatch");
                HarnessAssert.True(!_lblLifecycle.LabelIsActive, "Deactivate left label active");
                RequireSnapshotPrefixUnchanged(lockedBefore, _lblLifecycle.Versions, "deactivate");
                return new CaseObservation(
                    "Deactivate appended INACTIVE version and preserved the prior locked snapshot.",
                    "status=INACTIVE;lockedPrefixImmutable=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-LBL-009",
            "ordinary_a",
            ["p8-lbl-009-new-blocked", "p8-lbl-009-stale-unlocked"],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var admin = Actor("system_admin");
                var duplicate = await _api.PostAsync(
                    "api/labels/config",
                    Envelope(
                        "p8-lbl-009-new-blocked",
                        0,
                        EmptyConfigHash,
                        LabelPayload(_lblLifecycle.LabelCode, "P8 Lifecycle Duplicate", "GLOBAL", null,
                            usage: "STATISTIC", dataType: "NUMBER")),
                    admin.Token,
                    ct: ct);
                ExpectFailure(duplicate, HttpStatusCode.Conflict, "STAT_CONFIG_CAS_CONFLICT", "LABEL_DUPLICATE_CODE");
                var staleDraft = await _api.PutAsync(
                    $"api/labels/{_lblLifecycle.LabelId}/config",
                    Envelope(
                        "p8-lbl-009-stale-unlocked",
                        _lblLifecycleActive.Revision,
                        _lblLifecycleActive.ConfigHash,
                        LabelPayload(_lblLifecycle.LabelCode, "P8 stale unlocked", "GLOBAL", null,
                            usage: "STATISTIC", dataType: "NUMBER")),
                    admin.Token,
                    ct: ct);
                ExpectFailure(staleDraft, HttpStatusCode.Conflict, "STAT_CONFIG_CAS_CONFLICT");
                var lockedRead = await ReadLabelAsync(Actor("ordinary_a"), _lblLifecycle.LabelId, ct);
                HarnessAssert.Equal("INACTIVE", lockedRead.Status, "Inactive readback status mismatch");
                var firstVersion = lockedRead.Versions.OfType<JsonObject>()
                    .Single(version => RequiredInt(version, "versionNo") == 1);
                HarnessAssert.Equal("ACTIVE", RequiredString(firstVersion, "status"),
                    "Prior locked active snapshot is not readable");
                HarnessAssert.Equal(_lblLifecycleActive.ConfigHash, RequiredString(firstVersion, "configHash"),
                    "Prior locked active snapshot hash changed");
                return new CaseObservation(
                    "Inactive owner blocked new/stale unlocked writes while prior locked snapshot stayed readable.",
                    "newBlocked=true;staleBlocked=true;lockedReadable=true;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-LBL-010",
            "system_admin",
            ["p8-lbl-010-reactivate"],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                var before = _lblLifecycle;
                var lockedBefore = (JsonArray)before.Versions.DeepClone();
                _lblLifecycle = (await UpdateLabelAsync(
                    Actor("system_admin"),
                    before,
                    "p8-lbl-010-reactivate",
                    LabelPayload(before.LabelCode, "P8 Lifecycle", "GLOBAL", null,
                        usage: "STATISTIC", dataType: "NUMBER", isActive: true),
                    ct)).Identity;
                RequireVersionAdvanced(before, _lblLifecycle);
                HarnessAssert.Equal("ACTIVE", _lblLifecycle.Status, "Reactivate status mismatch");
                HarnessAssert.True(_lblLifecycle.LabelIsActive, "Reactivate did not activate current version");
                RequireSnapshotPrefixUnchanged(lockedBefore, _lblLifecycle.Versions, "reactivate");
                HarnessAssert.Equal("INACTIVE",
                    RequiredString(_lblLifecycle.Versions.OfType<JsonObject>()
                        .Single(version => RequiredInt(version, "versionNo") == before.VersionNo), "status"),
                    "Reactivate rewrote prior inactive version");
                return new CaseObservation(
                    "Reactivate affected only a new ACTIVE version; prior active/inactive snapshots stayed immutable.",
                    "newVersionOnly=true;lockedPrefixImmutable=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-LBL-011",
            "system_admin",
            [
                "p8-lbl-011-referenced-block",
                "p8-lbl-011-tombstone",
                "p8-lbl-011-code-reserved"
            ],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                var admin = Actor("system_admin");
                var beforeReferenced = await CaptureDatabaseSnapshotAsync(ct);
                var referenced = await _api.PostAsync(
                    $"api/labels/{_lblReferenced.LabelId}/config/tombstone",
                    Envelope(
                        "p8-lbl-011-referenced-block",
                        _lblReferenced.Revision,
                        _lblReferenced.ConfigHash,
                        new JsonObject()),
                    admin.Token,
                    ct: ct);
                ExpectFailure(referenced, HttpStatusCode.Conflict, "LABEL_DELETE_REFERENCED");
                var afterReferenced = await CaptureDatabaseSnapshotAsync(ct);
                VerifyCollectionContract(
                    "P8-LBL-011/referenced-block",
                    BuildDeltas(beforeReferenced, afterReferenced),
                    NoCollectionWrites,
                    NoCollectionWrites);

                _lblTombstone = (await DeleteLabelAsync(
                    admin,
                    _lblTombstone,
                    "p8-lbl-011-tombstone",
                    ct)).Identity;
                HarnessAssert.Equal("TOMBSTONED", _lblTombstone.Status, "Tombstone status mismatch");
                var direct = await RequireLabelDocumentAsync(_lblTombstone.LabelId, ct);
                HarnessAssert.Equal(true, BsonBool(direct, "isDeleted"), "Tombstone did not persist isDeleted");

                var beforeReserved = await CaptureDatabaseSnapshotAsync(ct);
                var reserved = await _api.PostAsync(
                    "api/labels/config",
                    Envelope(
                        "p8-lbl-011-code-reserved",
                        0,
                        EmptyConfigHash,
                        LabelPayload(_lblTombstone.LabelCode, "P8 Reserved Recreate", "GLOBAL", null,
                            usage: "STATISTIC", dataType: "BOOLEAN")),
                    admin.Token,
                    ct: ct);
                ExpectFailure(reserved, HttpStatusCode.Conflict, "LABEL_CONFIG_TOMBSTONED");
                var afterReserved = await CaptureDatabaseSnapshotAsync(ct);
                VerifyCollectionContract(
                    "P8-LBL-011/code-reserved",
                    BuildDeltas(beforeReserved, afterReserved),
                    NoCollectionWrites,
                    NoCollectionWrites);
                return new CaseObservation(
                    "Referenced tombstone was zero-write; unreferenced tombstoned; tombstone permanently reserved code.",
                    "referencedBlocked=true;unreferencedTombstoned=true;codeReserved=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-LBL-012",
            "system_admin",
            ["p8-lbl-012-snapshot-pin"],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                _lblPinned = (await CreateLabelAsync(
                    Actor("system_admin"),
                    "p8-lbl-012-snapshot-pin",
                    LabelPayload(
                        "p8.lbl.pinned",
                        "P8 Pinned Label",
                        "GLOBAL",
                        null,
                        usage: "STATISTIC",
                        dataType: "STRING_LIST",
                        valueSourceType: "ENUM_CATALOG",
                        valueSourceCatalogId: _enumCatalogId),
                    ct)).Identity;
                HarnessAssert.Equal(1, _lblPinned.DependencyPins.Count, "Expected exactly one enum dependency pin");
                HarnessAssert.True(
                    _lblPinned.DependencyPins[0].StartsWith($"LABEL_ENUM:{_enumCatalogId}:1:", StringComparison.Ordinal),
                    "Dependency pin does not bind enum catalog id+revision+hash");
                var version = _lblPinned.Versions.OfType<JsonObject>().Single();
                HarnessAssert.Equal(_lblPinned.LabelId, RequiredString(version, "labelId"),
                    "Snapshot labelId mismatch");
                HarnessAssert.Equal(_lblPinned.LabelCode, RequiredString(version, "code"),
                    "Snapshot code mismatch");
                HarnessAssert.Equal("STATISTIC", RequiredString(version, "usage"), "Snapshot usage mismatch");
                HarnessAssert.Equal("STRING_LIST", RequiredString(version, "dataType"), "Snapshot datatype mismatch");
                HarnessAssert.Equal("GLOBAL", RequiredString(version, "scopeType"), "Snapshot scope mismatch");
                HarnessAssert.Equal(_lblPinned.LabelScopeId, OptionalString(version, "scopeId"),
                    "Snapshot scopeId mismatch");
                HarnessAssert.Equal(_lblPinned.VersionNo, RequiredInt(version, "versionNo"),
                    "Snapshot versionNo mismatch");
                HarnessAssert.Equal(_lblPinned.VersionId, RequiredString(version, "versionId"),
                    "Snapshot versionId mismatch");
                HarnessAssert.Equal(_lblPinned.ConfigHash, RequiredString(version, "configHash"),
                    "Snapshot configHash mismatch");
                HarnessAssert.True(RequiredBool(version, "isActive"), "Snapshot active bit mismatch");
                var versionPins = version["dependencyPins"] as JsonArray
                                  ?? throw new InvalidOperationException("Snapshot dependencyPins missing.");
                HarnessAssert.Equal(_lblPinned.DependencyPins[0], versionPins.Single()!.GetValue<string>(),
                    "Snapshot dependency pin differs from current identity");
                var direct = await RequireLabelDocumentAsync(_lblPinned.LabelId, ct);
                HarnessAssert.Equal(_lblPinned.LabelId, BsonString(direct, "_id"),
                    "Direct Mongo labelId mismatch");
                HarnessAssert.Equal(_lblPinned.LabelCode, BsonString(direct, "code"),
                    "Direct Mongo code mismatch");
                HarnessAssert.Equal(_lblPinned.LabelDataType, BsonString(direct, "dataType"),
                    "Direct Mongo datatype mismatch");
                HarnessAssert.Equal(_lblPinned.LabelUsage, BsonString(direct, "usage"),
                    "Direct Mongo usage mismatch");
                HarnessAssert.Equal(_lblPinned.LabelScopeType, BsonString(direct, "scopeType"),
                    "Direct Mongo scopeType mismatch");
                HarnessAssert.Equal(_lblPinned.LabelScopeId, BsonString(direct, "scopeId"),
                    "Direct Mongo scopeId mismatch");
                HarnessAssert.Equal(_lblPinned.VersionNo, BsonInt(direct, "versionNo"),
                    "Direct Mongo versionNo mismatch");
                HarnessAssert.Equal(_lblPinned.VersionId, BsonString(direct, "versionId"),
                    "Direct Mongo versionId mismatch");
                HarnessAssert.Equal(_lblPinned.ConfigHash, BsonString(direct, "configHash"),
                    "Direct Mongo configHash mismatch");
                HarnessAssert.Equal(_lblPinned.DependencyPins[0],
                    direct["dependencyPins"].AsBsonArray.Single().AsString,
                    "Direct Mongo dependency pin mismatch");
                var directVersion = direct["versionSnapshots"].AsBsonArray.Single().AsBsonDocument;
                HarnessAssert.Equal(_lblPinned.LabelId, BsonString(directVersion, "labelId"),
                    "Direct snapshot labelId mismatch");
                HarnessAssert.Equal(_lblPinned.LabelCode, BsonString(directVersion, "code"),
                    "Direct snapshot code mismatch");
                HarnessAssert.Equal(_lblPinned.LabelDataType, BsonString(directVersion, "dataType"),
                    "Direct snapshot datatype mismatch");
                HarnessAssert.Equal(_lblPinned.LabelUsage, BsonString(directVersion, "usage"),
                    "Direct snapshot usage mismatch");
                HarnessAssert.Equal(_lblPinned.LabelScopeType, BsonString(directVersion, "scopeType"),
                    "Direct snapshot scopeType mismatch");
                HarnessAssert.Equal(_lblPinned.LabelScopeId, BsonString(directVersion, "scopeId"),
                    "Direct snapshot scopeId mismatch");
                HarnessAssert.Equal(_lblPinned.VersionNo, BsonInt(directVersion, "versionNo"),
                    "Direct snapshot versionNo mismatch");
                HarnessAssert.Equal(_lblPinned.VersionId, BsonString(directVersion, "versionId"),
                    "Direct snapshot versionId mismatch");
                HarnessAssert.Equal(_lblPinned.ConfigHash, BsonString(directVersion, "configHash"),
                    "Direct snapshot configHash mismatch");
                HarnessAssert.Equal(_lblPinned.DependencyPins[0],
                    directVersion["dependencyPins"].AsBsonArray.Single().AsString,
                    "Direct snapshot dependency pin mismatch");
                return new CaseObservation(
                    "API and direct Mongo locked snapshots bound exact owner, taxonomy, version, hash and dependency fields.",
                    "exactSnapshotPins=true;dependencyPins=1");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-LBL-013",
            "system_admin",
            ["p8-lbl-013-lifecycle-lock"],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                var before = _lblLifecycle;
                var lockedBefore = (JsonArray)before.Versions.DeepClone();
                _lblLifecycle = (await UpdateLabelAsync(
                    Actor("system_admin"),
                    before,
                    "p8-lbl-013-lifecycle-lock",
                    LabelPayload(before.LabelCode, "P8 Lifecycle", "GLOBAL", null,
                        usage: "STATISTIC", dataType: "NUMBER", isActive: false),
                    ct)).Identity;
                RequireVersionAdvanced(before, _lblLifecycle);
                RequireSnapshotPrefixUnchanged(lockedBefore, _lblLifecycle.Versions, "second deactivate");
                HarnessAssert.Equal("INACTIVE", _lblLifecycle.Status, "Second deactivate status mismatch");
                foreach (var snapshot in lockedBefore.OfType<JsonObject>())
                {
                    var versionNo = RequiredInt(snapshot, "versionNo");
                    var current = _lblLifecycle.Versions.OfType<JsonObject>()
                        .Single(version => RequiredInt(version, "versionNo") == versionNo);
                    HarnessAssert.Equal(RequiredString(snapshot, "configHash"), RequiredString(current, "configHash"),
                        $"Lifecycle rewrote locked hash for version {versionNo}");
                }
                return new CaseObservation(
                    "Repeated lifecycle transition appended only; every locked historical hash remained unchanged.",
                    "lifecycleAppendOnly=true;lockedHashesStable=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-LBL-014",
            "system_admin",
            ["p8-lbl-014-global", "p8-lbl-014-unit", "p8-lbl-014-duplicate"],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                var admin = Actor("system_admin");
                var global = (await CreateLabelAsync(
                    admin,
                    "p8-lbl-014-global",
                    LabelPayload(" P8.Lbl.Collision-01 ", "P8 Collision Global", "GLOBAL", null),
                    ct)).Identity;
                var unit = (await CreateLabelAsync(
                    admin,
                    "p8-lbl-014-unit",
                    LabelPayload("p8.lbl.collision-01", "P8 Collision Unit", "UNIT", _unitAId),
                    ct)).Identity;
                HarnessAssert.Equal(global.LabelCode, unit.LabelCode, "Collision code normalization mismatch");
                HarnessAssert.True(global.OwnerId != unit.OwnerId, "Same code across scopes reused owner identity");

                var beforeDuplicate = await CaptureDatabaseSnapshotAsync(ct);
                var duplicate = await _api.PostAsync(
                    "api/labels/config",
                    Envelope(
                        "p8-lbl-014-duplicate",
                        0,
                        EmptyConfigHash,
                        LabelPayload(
                            "p8.lbl.collision-01",
                            "P8 Collision Different Layer",
                            "GLOBAL",
                            null,
                            usage: "STATISTIC")),
                    admin.Token,
                    ct: ct);
                ExpectFailure(duplicate, HttpStatusCode.Conflict, "STAT_CONFIG_CAS_CONFLICT", "LABEL_DUPLICATE_CODE");
                var afterDuplicate = await CaptureDatabaseSnapshotAsync(ct);
                VerifyCollectionContract(
                    "P8-LBL-014/duplicate",
                    BuildDeltas(beforeDuplicate, afterDuplicate),
                    NoCollectionWrites,
                    NoCollectionWrites);
                var sameCodeCount = await _database.GetCollection<BsonDocument>(LabelsCollection)
                    .CountDocumentsAsync(
                        Builders<BsonDocument>.Filter.Eq("code", "p8.lbl.collision-01"),
                        cancellationToken: ct);
                HarnessAssert.Equal(2L, sameCodeCount,
                    "Collision contract did not preserve exactly one owner per scope");
                return new CaseObservation(
                    "Cross-layer normalized code collision was zero-write within GLOBAL while identical code remained valid across scopes.",
                    "sameScopeUnique=true;crossLayerRejected=true;crossScopeAllowed=true;duplicateDelta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-LBL-015",
            "ordinary_a",
            [],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var ordinary = Actor("ordinary_a");
                var hidden = await _api.GetAsync(
                    $"api/labels/{_lblLevel.LabelId}/config",
                    ordinary.Token,
                    ct: ct);
                var missing = await _api.GetAsync(
                    $"api/labels/{ObjectId.GenerateNewId()}/config",
                    ordinary.Token,
                    ct: ct);
                ExpectFailure(hidden, HttpStatusCode.NotFound, "LABEL_NOT_FOUND");
                ExpectFailure(missing, HttpStatusCode.NotFound, "LABEL_NOT_FOUND");
                HarnessAssert.Equal(hidden.StatusCode, missing.StatusCode,
                    "Ordinary hidden/missing status differs");
                HarnessAssert.Equal(ErrorCode(hidden), ErrorCode(missing),
                    "Ordinary hidden/missing error code differs");
                return new CaseObservation(
                    "Ordinary reader observed identical 404/code for hidden existing and absent owner.",
                    "ordinaryIndistinguishable=true;http=404;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-LBL-016",
            "unit_manager_b",
            [
                "p8-lbl-016-hidden-update",
                "p8-lbl-016-missing-update",
                "p8-lbl-016-forged-create"
            ],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var foreign = Actor("unit_manager_b");
                var missingId = ObjectId.GenerateNewId().ToString();
                var hiddenRead = await _api.GetAsync(
                    $"api/labels/{_lblUnit.LabelId}/config",
                    foreign.Token,
                    ct: ct);
                var missingRead = await _api.GetAsync(
                    $"api/labels/{missingId}/config",
                    foreign.Token,
                    ct: ct);
                ExpectFailure(hiddenRead, HttpStatusCode.NotFound, "LABEL_NOT_FOUND");
                ExpectFailure(missingRead, HttpStatusCode.NotFound, "LABEL_NOT_FOUND");
                HarnessAssert.Equal(ErrorCode(hiddenRead), ErrorCode(missingRead),
                    "Foreign hidden/missing read code differs");

                var foreignPayload = LabelPayload(
                    _lblUnit.LabelCode,
                    "P8 Foreign Forgery",
                    "UNIT",
                    _unitBId,
                    usage: "TABLE_TARGET",
                    dataType: "STRING_LIST");
                var hiddenUpdate = await _api.PutAsync(
                    $"api/labels/{_lblUnit.LabelId}/config",
                    Envelope(
                        "p8-lbl-016-hidden-update",
                        _lblUnit.Revision,
                        _lblUnit.ConfigHash,
                        foreignPayload),
                    foreign.Token,
                    ct: ct);
                var missingUpdate = await _api.PutAsync(
                    $"api/labels/{missingId}/config",
                    Envelope(
                        "p8-lbl-016-missing-update",
                        _lblUnit.Revision,
                        _lblUnit.ConfigHash,
                        foreignPayload.DeepClone()),
                    foreign.Token,
                    ct: ct);
                ExpectFailure(hiddenUpdate, HttpStatusCode.NotFound, "LABEL_NOT_FOUND");
                ExpectFailure(missingUpdate, HttpStatusCode.NotFound, "LABEL_NOT_FOUND");
                HarnessAssert.Equal(ErrorCode(hiddenUpdate), ErrorCode(missingUpdate),
                    "Foreign hidden/missing mutation code differs");

                var forgedScope = await _api.PostAsync(
                    "api/labels/config",
                    Envelope(
                        "p8-lbl-016-forged-create",
                        0,
                        EmptyConfigHash,
                        LabelPayload("p8.lbl.forged", "P8 Forged Scope", "UNIT", _unitAId)),
                    foreign.Token,
                    ct: ct);
                ExpectFailure(forgedScope, HttpStatusCode.Forbidden, "LABEL_SCOPE_MISMATCH");
                return new CaseObservation(
                    "Foreign manager saw hidden/missing equivalence and forged scope was denied before write.",
                    "foreignIndistinguishable=true;forgedScope403=true;delta=zero");
            },
            ct);
    }

    private static void RequireSnapshotPrefixUnchanged(
        JsonArray before,
        JsonArray after,
        string operation)
    {
        HarnessAssert.True(after.Count == before.Count + 1,
            $"{operation} did not append exactly one version snapshot");
        for (var index = 0; index < before.Count; index++)
        {
            HarnessAssert.Equal(
                CanonicalSnapshot(before[index]!),
                CanonicalSnapshot(after[index]!),
                $"{operation} rewrote historical snapshot {index + 1}");
        }
    }

    private static string CanonicalSnapshot(JsonNode snapshot)
    {
        var semantic = (JsonObject)snapshot.DeepClone();
        semantic.Remove("createdAtUtc");
        return Canonicalize(semantic);
    }

    private static string? ErrorCode(ApiHarnessResponse response)
        => ApiHarnessClient.FindStringRecursive(response.Json, "errorCode")
           ?? ApiHarnessClient.FindStringRecursive(response.Json, "code")
           ?? ApiHarnessClient.FindStringRecursive(response.Json, "reason");
}
