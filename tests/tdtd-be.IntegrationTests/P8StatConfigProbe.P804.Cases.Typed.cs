using System.Net;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP804BasicSummaryCasesAsync(CancellationToken ct)
    {
        await SeedP804FixturesAsync(ct);
        await AwaitP804InfrastructureBaselineAsync(ct);
        await RunP804TypedOperationCasesAsync(ct);
        await RunP804ScopePeriodCasesAsync(ct);
        await RunP804VersionCasesAsync(ct);
        await RunP804IsolationCasesAsync(ct);
    }

    private async Task RunP804TypedOperationCasesAsync(
        CancellationToken ct)
    {
        await RunBasicTypedFamilyCaseAsync(
            "P8-BAS-001",
            "001",
            "NUMBER",
            ["SUM", "MIN", "MAX", "MEAN", "COUNT"],
            "p8-bas-001-number-matrix",
            "NUMBER preserved the explicit SUM/MIN/MAX/MEAN/COUNT matrix; MEAN stayed metadata-only.",
            "type=NUMBER;ops=SUM,MIN,MAX,MEAN,COUNT;mean=sum/numericValueCount+metadataOnly;hash+mongo=true",
            ct);
        await RunBasicTypedFamilyCaseAsync(
            "P8-BAS-002",
            "002",
            "DATE",
            ["MIN_DATE", "MAX_DATE", "COUNT"],
            "p8-bas-002-date-matrix",
            "DATE preserved the explicit MIN_DATE/MAX_DATE/COUNT matrix.",
            "type=DATE;ops=MIN_DATE,MAX_DATE,COUNT;fallback=false;hash+mongo=true",
            ct);
        await RunBasicTypedFamilyCaseAsync(
            "P8-BAS-003",
            "003",
            "BOOLEAN",
            ["TRUE_COUNT", "FALSE_COUNT", "COUNT"],
            "p8-bas-003-boolean-matrix",
            "BOOLEAN preserved the explicit TRUE_COUNT/FALSE_COUNT/COUNT matrix.",
            "type=BOOLEAN;ops=TRUE_COUNT,FALSE_COUNT,COUNT;fallback=false;hash+mongo=true",
            ct);
        await RunBasicTypedFamilyCaseAsync(
            "P8-BAS-004",
            "004",
            "CHOICE",
            ["BUCKET_COUNT", "COUNT"],
            "p8-bas-004-choice-matrix",
            "CHOICE preserved the explicit BUCKET_COUNT/COUNT matrix.",
            "type=CHOICE;ops=BUCKET_COUNT,COUNT;fallback=false;hash+mongo=true",
            ct);
        await RunBasicTypedFamilyCaseAsync(
            "P8-BAS-005",
            "005",
            "TEXT",
            ["JOIN", "COUNT"],
            "p8-bas-005-text-matrix",
            "TEXT preserved the explicit JOIN/COUNT matrix.",
            "type=TEXT;ops=JOIN,COUNT;fallback=false;hash+mongo=true",
            ct);

        var incompatibleCommands = new[]
        {
            "p8-bas-006-number-join",
            "p8-bas-006-date-sum",
            "p8-bas-006-boolean-mean",
            "p8-bas-006-choice-max",
            "p8-bas-006-text-true-count"
        };
        await RunEvidenceCaseAsync(
            "P8-BAS-006",
            "system_admin",
            incompatibleCommands,
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = BasicFixture("006");
                var current = await ReadBasicConfigAsync(
                    actor,
                    fixture,
                    ct,
                    requirePersisted: false);
                var probes = new[]
                {
                    (incompatibleCommands[0], "NUMBER", "JOIN"),
                    (incompatibleCommands[1], "DATE", "SUM"),
                    (incompatibleCommands[2], "BOOLEAN", "MEAN"),
                    (incompatibleCommands[3], "CHOICE", "MAX"),
                    (incompatibleCommands[4], "TEXT", "TRUE_COUNT")
                };
                foreach (var probe in probes)
                {
                    var key = BasicTargetKeys[probe.Item2][0];
                    await RequireZeroWriteBasicRejectionAsync(
                        $"P8-BAS-006/{probe.Item1}",
                        () => _api.PutAsync(
                            BasicConfigRoute(fixture),
                            Envelope(
                                probe.Item1,
                                current.Revision,
                                current.ConfigHash,
                                BasicDirectPayload([
                                    BasicTarget(
                                        "FIELD",
                                        key,
                                        probe.Item2,
                                        probe.Item3)
                                ])),
                            actor.Token,
                            ct: ct),
                        HttpStatusCode.BadRequest,
                        "STAT_CONFIG_SCHEMA_INVALID",
                        "$.payload.targets[0].operation",
                        "BASIC_SUMMARY_OPERATION_INCOMPATIBLE",
                        ct);
                }
                return new CaseObservation(
                    "Every explicit cross-type operation failed closed with whole-database zero delta.",
                    "NUMBER+JOIN=0W;DATE+SUM=0W;BOOLEAN+MEAN=0W;CHOICE+MAX=0W;TEXT+TRUE_COUNT=0W;fallback=false");
            },
            ct);
    }

    private async Task RunBasicTypedFamilyCaseAsync(
        string caseId,
        string fixtureKey,
        string dataType,
        string[] operations,
        string commandId,
        string detail,
        string fingerprint,
        CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            caseId,
            "system_admin",
            [commandId],
            BasicMutationWrites,
            BasicMutationWrites,
            async () =>
            {
                var fixture = BasicFixture(fixtureKey);
                var payload = BasicDirectPayload(
                    BasicFieldTargets(dataType, operations));
                var (_, identity) = await PutBasicConfigAsync(
                    Actor("system_admin"),
                    fixture,
                    commandId,
                    payload,
                    ct);
                HarnessAssert.Equal(false, identity.IsVirtualEmpty,
                    "Successful Basic PUT remained virtual");
                HarnessAssert.Equal(1, identity.VersionNo,
                    "Initial Basic version number mismatch");
                HarnessAssert.Equal(1L, identity.Revision,
                    "Initial Basic revision mismatch");
                HarnessAssert.Equal("DRAFT", identity.Status,
                    "Initial Basic status mismatch");
                HarnessAssert.Equal(1, identity.Versions.Count,
                    "Initial Basic version snapshot is absent");
                RequireBasicPayloadContract(identity, payload);
                return new CaseObservation(detail, fingerprint);
            },
            ct);
    }
}
