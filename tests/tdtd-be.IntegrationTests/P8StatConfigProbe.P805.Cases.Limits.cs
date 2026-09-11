using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP805LimitCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-ADV-005",
            "system_admin",
            ["p8-adv-005-one-section", "p8-adv-005-two-sections"],
            AdvancedConfigWrites,
            AdvancedConfigWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = AdvancedFixture("005");
                var oneSection = AdvancedPayload(fixture, targetCount: 1);
                var (_, accepted) = await PutAdvancedConfigAsync(
                    actor,
                    fixture,
                    "p8-adv-005-one-section",
                    oneSection,
                    ct);
                HarnessAssert.Equal(1,
                    ((JsonArray)accepted.Payload["sections"]!).Count,
                    "Advanced one-section readback count mismatch");

                var twoSections = AdvancedPayload(
                    fixture,
                    sections:
                    [
                        AdvancedSection(
                            fixture.SectionId,
                            false,
                            [AdvancedTarget(AdvancedFieldId(1))]),
                        AdvancedSection(
                            AdvancedSecondSectionId,
                            false,
                            [AdvancedTarget("adv_second_0001")])
                    ]);
                await RequireZeroWriteAdvancedRejectionAsync(
                    "P8-ADV-005/two-sections",
                    () => _api.PutAsync(
                        AdvancedConfigRoute(fixture),
                        Envelope(
                            "p8-adv-005-two-sections",
                            accepted.Revision,
                            accepted.ConfigHash,
                            twoSections),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "STAT_CONFIG_SCHEMA_INVALID",
                    "$.payload.sections",
                    "EXACTLY_ONE_SECTION_REQUIRED",
                    ct);
                return new CaseObservation(
                    "Exactly one Advanced section was accepted; two sections failed atomically.",
                    "sections=1:200+mongo;sections=2:400+0W");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-ADV-006",
            "system_admin",
            ["p8-adv-006-cumulative-249", "p8-adv-006-cumulative-250"],
            AdvancedConfigWrites,
            AdvancedConfigWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = AdvancedFixture("006");
                var (_, accepted) = await PutAdvancedConfigAsync(
                    actor,
                    fixture,
                    "p8-adv-006-cumulative-249",
                    AdvancedPayload(
                        fixture,
                        targetCount: 249,
                        isCumulative: true),
                    ct);
                var section = ((JsonArray)accepted.Payload["sections"]!)
                    .OfType<JsonObject>()
                    .Single();
                HarnessAssert.Equal(249,
                    ((JsonArray)section["targets"]!).Count,
                    "Cumulative 249 target readback mismatch");
                await RequireZeroWriteAdvancedRejectionAsync(
                    "P8-ADV-006/cumulative-250",
                    () => _api.PutAsync(
                        AdvancedConfigRoute(fixture),
                        Envelope(
                            "p8-adv-006-cumulative-250",
                            accepted.Revision,
                            accepted.ConfigHash,
                            AdvancedPayload(
                                fixture,
                                targetCount: 250,
                                isCumulative: true)),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "STAT_CONFIG_SCHEMA_INVALID",
                    "$.payload.sections[0].targets",
                    "TARGET_LIMIT_EXCEEDED",
                    ct);
                return new CaseObservation(
                    "Cumulative target gate accepted 249 and rejected 250 with no partial mutation.",
                    "cumulative=249:200;cumulative=250:400+0W;limitExclusive=250");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-ADV-007",
            "system_admin",
            ["p8-adv-007-noncumulative-1000", "p8-adv-007-noncumulative-1001"],
            AdvancedConfigWrites,
            AdvancedConfigWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = AdvancedFixture("007");
                var (_, accepted) = await PutAdvancedConfigAsync(
                    actor,
                    fixture,
                    "p8-adv-007-noncumulative-1000",
                    AdvancedPayload(
                        fixture,
                        targetCount: 1000,
                        isCumulative: false),
                    ct);
                var section = ((JsonArray)accepted.Payload["sections"]!)
                    .OfType<JsonObject>()
                    .Single();
                HarnessAssert.Equal(1000,
                    ((JsonArray)section["targets"]!).Count,
                    "Non-cumulative 1000 target readback mismatch");
                await RequireZeroWriteAdvancedRejectionAsync(
                    "P8-ADV-007/noncumulative-1001",
                    () => _api.PutAsync(
                        AdvancedConfigRoute(fixture),
                        Envelope(
                            "p8-adv-007-noncumulative-1001",
                            accepted.Revision,
                            accepted.ConfigHash,
                            AdvancedPayload(
                                fixture,
                                targetCount: 1001,
                                isCumulative: false)),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "STAT_CONFIG_SCHEMA_INVALID",
                    "$.payload.sections[0].targets",
                    "TARGET_LIMIT_EXCEEDED",
                    ct);
                return new CaseObservation(
                    "Non-cumulative target gate accepted 1000 and rejected 1001 with no partial mutation.",
                    "noncumulative=1000:200;noncumulative=1001:400+0W;limitInclusive=1000");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-ADV-008",
            "system_admin",
            [
                "p8-adv-008-depth-3",
                "p8-adv-008-depth-4",
                "p8-adv-008-bytes-1048576",
                "p8-adv-008-bytes-1048577"
            ],
            AdvancedConfigWrites,
            AdvancedConfigWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var hierarchyFixture = AdvancedFixture("008");
                var (_, depth3) = await PutAdvancedConfigAsync(
                    actor,
                    hierarchyFixture,
                    "p8-adv-008-depth-3",
                    AdvancedPayload(
                        hierarchyFixture,
                        hierarchyGrains: ["DAY", "MONTH", "YEAR"]),
                    ct);
                var grains = ((JsonArray)depth3.Payload["hierarchyGrains"]!)
                    .Select(item => item?.GetValue<string>() ?? string.Empty)
                    .ToArray();
                HarnessAssert.True(grains.SequenceEqual(
                        ["DAY", "MONTH", "YEAR"],
                        StringComparer.Ordinal),
                    "Advanced hierarchy grains were not DAY/MONTH/YEAR");
                await RequireZeroWriteAdvancedRejectionAsync(
                    "P8-ADV-008/depth-4",
                    () => _api.PutAsync(
                        AdvancedConfigRoute(hierarchyFixture),
                        Envelope(
                            "p8-adv-008-depth-4",
                            depth3.Revision,
                            depth3.ConfigHash,
                            AdvancedPayload(
                                hierarchyFixture,
                                hierarchyGrains:
                                ["DAY", "MONTH", "YEAR", "YEAR"])),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "STAT_CONFIG_SCHEMA_INVALID",
                    "$.payload.hierarchyGrains",
                    "HIERARCHY_DEPTH_EXCEEDED",
                    ct);

                var sizeFixture = AdvancedFixture("009");
                var exact = AdvancedPayloadAtCanonicalUtf8Bytes(
                    sizeFixture,
                    1_048_576);
                var (_, exactIdentity) = await PutAdvancedConfigAsync(
                    actor,
                    sizeFixture,
                    "p8-adv-008-bytes-1048576",
                    exact,
                    ct);
                HarnessAssert.Equal(1_048_576,
                    Encoding.UTF8.GetByteCount(Canonicalize(
                        exactIdentity.Payload)),
                    "Accepted Advanced canonical payload byte count mismatch");
                var oversized = AdvancedPayloadAtCanonicalUtf8Bytes(
                    sizeFixture,
                    1_048_577);
                await RequireZeroWriteAdvancedRejectionAsync(
                    "P8-ADV-008/bytes-1048577",
                    () => _api.PutAsync(
                        AdvancedConfigRoute(sizeFixture),
                        Envelope(
                            "p8-adv-008-bytes-1048577",
                            exactIdentity.Revision,
                            exactIdentity.ConfigHash,
                            oversized),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "STAT_CONFIG_SCHEMA_INVALID",
                    "$.payload",
                    "CANONICAL_PAYLOAD_TOO_LARGE",
                    ct);
                return new CaseObservation(
                    "Hierarchy depth 3 and canonical UTF-8 payload 1,048,576 bytes were exact maxima; N+1 failed closed.",
                    "grains=DAY>MONTH>YEAR;depth3=200;depth4=400+0W;bytes1048576=200;bytes1048577=400+0W");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-ADV-009",
            "system_admin",
            ["p8-adv-009-draft", "p8-adv-009-lock"],
            AdvancedLockWrites,
            AdvancedLockWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = AdvancedFixture("010");
                var (_, draft) = await PutAdvancedConfigAsync(
                    actor,
                    fixture,
                    "p8-adv-009-draft",
                    AdvancedPayload(fixture, targetCount: 4),
                    ct);
                var (_, locked) = await PostAdvancedActionAsync(
                    actor,
                    fixture,
                    "lock",
                    "p8-adv-009-lock",
                    draft,
                    ct);
                var receipt = locked.ValidationReceipt
                              ?? throw new InvalidOperationException(
                                  "Locked Advanced validation receipt missing");
                RequireAdvancedValidationReceipt(locked, fixture);
                HarnessAssert.Equal(4, RequiredInt(receipt, "targetCount"),
                    "Advanced receipt targetCount mismatch");
                HarnessAssert.Equal(1000, RequiredInt(receipt, "targetLimit"),
                    "Advanced receipt targetLimit mismatch");
                HarnessAssert.Equal(3,
                    RequiredInt(receipt, "hierarchyDepth"),
                    "Advanced receipt hierarchyDepth mismatch");
                HarnessAssert.Equal(
                    Encoding.UTF8.GetByteCount(Canonicalize(locked.Payload)),
                    RequiredInt(receipt, "canonicalPayloadBytes"),
                    "Advanced receipt canonical payload bytes mismatch");
                var readback = await ReadAdvancedConfigAsync(
                    actor,
                    fixture,
                    ct,
                    requirePersisted: true);
                HarnessAssert.Equal(Canonicalize(receipt),
                    Canonicalize(readback.ValidationReceipt
                                 ?? throw new InvalidOperationException(
                                     "Advanced readback validation receipt missing")),
                    "Advanced validation receipt was not deterministic on GET");
                return new CaseObservation(
                    "Lock emitted a deterministic CONFIG_ONLY_NO_DATASET receipt with exact gates and no runtime reads/writes.",
                    "receipt=sha256-deterministic;mode=CONFIG_ONLY_NO_DATASET;targets=4/1000;depth=3/3;payloadBytes=exact;preview+hierarchy=0");
            },
            ct);
    }
}
