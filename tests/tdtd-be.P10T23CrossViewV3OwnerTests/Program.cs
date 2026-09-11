using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsRun;

const string Work = "work-a";
const string Scope = "scope-a";
const string ExportId = "export-a";
const string ExportFilter =
    "{\"blockId\":null,\"bucketKey\":null," +
    "\"dynamicFormTemplateId\":null,\"fieldId\":null," +
    "\"fieldKey\":null,\"labelCode\":null," +
    "\"metricKey\":null,\"periodKey\":null}";

var cases = new (string Name, Action Run)[]
{
    ("v3 command is explicit and server-only", CommandSchema),
    ("canonical API filter admits optional subset", OptionalApiFilter),
    ("canonical export filter requires exact shape", ExactExportFilter),
    ("filter extra property fails closed", ExtraFilterFails),
    ("different native authorization domains relate", DifferentAuthDomains),
    ("advanced API sentinel relation is valid", AdvancedApiSentinel),
    ("one-bit API authorization mutation fails", ApiMutationFails),
    ("one-bit export receipt mutation fails", ReceiptMutationFails),
    ("one-bit authorization target mutation fails", TargetMutationFails),
    ("v3 owner DI graph is scoped", DiRegistration)
};

var passed = 0;
foreach (var item in cases)
{
    item.Run();
    passed++;
}
Console.WriteLine($"P10_T23_CROSS_VIEW_V3_OWNER_OK checks={passed}");

static void CommandSchema()
{
    var command = new StatisticReconciliationActualCrossViewV3OwnerCommand(
        StatisticReconciliationActualCrossViewV3OwnerSchemas.Command,
        null!, null, null, null, null, null, null, null, null);
    Equal("P10_ACTUAL_CROSS_VIEW_V3_OWNER_COMMAND_V1",
        command.SchemaVersion);
    Equal(StatisticReconciliationActualCrossViewV2OwnerSchemas.Command,
        command.ToProjectionCarrier().SchemaVersion);
}

static void OptionalApiFilter()
{
    var value = new StatisticReconciliationActualCrossViewV3CanonicalFilter(
        "{\"periodInstanceKey\":\"period-a\"}");
    value.RequireAllowed("periodInstanceKey", "fieldId");
    Equal("period-a", value.String("periodInstanceKey"));
    Equal(null, value.String("fieldId"));
}

static void ExactExportFilter()
{
    var value = new StatisticReconciliationActualCrossViewV3CanonicalFilter(
        ExportFilter);
    value.RequireExact(
        "blockId", "bucketKey", "dynamicFormTemplateId", "fieldId",
        "fieldKey", "labelCode", "metricKey", "periodKey");
}

static void ExtraFilterFails()
{
    var value = new StatisticReconciliationActualCrossViewV3CanonicalFilter(
        "{\"extra\":null,\"periodInstanceKey\":\"period-a\"}");
    Throws(() => value.RequireAllowed("periodInstanceKey"));
}

static void DifferentAuthDomains()
{
    var api = Api();
    var export = ExportBinding(StatRunExportResultKinds.DirectField);
    True(api.AuthorizationSnapshotSha256 !=
         export.AuthorizationSnapshotSha256);
    StatisticReconciliationActualCrossViewAuthorizationV3Integrity
        .RequireExportBinding(export);
    var relation = StatisticReconciliationActualCrossViewAuthorization.Create(
        Work, Scope, api, export);
    StatisticReconciliationActualCrossViewAuthorizationV3Integrity
        .RequireRelation(relation, true);
    Equal(api.AuthorizationSnapshotSha256,
        relation.ApiAuthorizationSnapshotSha256);
    Equal(export.AuthorizationSnapshotSha256,
        relation.ExportAuthorizationSnapshotSha256);
}

static void AdvancedApiSentinel()
{
    var export = ExportBinding(StatRunExportResultKinds.Advanced);
    var relation = StatisticReconciliationActualCrossViewAuthorization.Create(
        Work, Scope, null, export);
    StatisticReconciliationActualCrossViewAuthorizationV3Integrity
        .RequireRelation(relation, false);
    Equal(null, relation.ApiAuthorizationSnapshotSha256);
    True(relation.ApiPermissionCodes.IsEmpty);
}

static void ApiMutationFails()
{
    var relation = StatisticReconciliationActualCrossViewAuthorization.Create(
        Work,
        Scope,
        Api(),
        ExportBinding(StatRunExportResultKinds.DirectField));
    Throws(() => StatisticReconciliationActualCrossViewAuthorizationV3Integrity
        .RequireRelation(relation with
        {
            ApiPermissionCodes = ["SYSTEM_ADMIN", "ZZZ"]
        }, true));
}

static void ReceiptMutationFails()
{
    var binding = ExportBinding(StatRunExportResultKinds.DirectField);
    Throws(() => StatisticReconciliationActualCrossViewAuthorizationV3Integrity
        .RequireExportBinding(binding with { ReceiptId = Hash('f') }));
}

static void TargetMutationFails()
{
    var export = ExportBinding(StatRunExportResultKinds.DirectField);
    Throws(() => StatisticReconciliationActualCrossViewAuthorization.Create(
        Work,
        "scope-b",
        Api(),
        export));
}

static void DiRegistration()
{
    var services = new ServiceCollection();
    services.AddStatisticReconciliationActualCrossViewV3Owner();
    var owner = services.Single(value => value.ServiceType ==
        typeof(IStatisticReconciliationActualCrossViewV3Owner));
    Equal(ServiceLifetime.Scoped, owner.Lifetime);
    Equal(typeof(StatisticReconciliationActualCrossViewV3Owner),
        owner.ImplementationType);
    True(services.Any(value => value.ServiceType ==
        typeof(StatisticReconciliationActualCrossViewV3DirectParityProjector)));
}

static StatisticReconciliationActualApiAuthorizationContext Api()
{
    ImmutableArray<string> permissions = ["SYSTEM_ADMIN"];
    var sha = StatisticReconciliationActualApiObservationAdapter
        .AuthorizationSha("api-actor", Work, Scope, permissions, 1, 1);
    return new("api-actor", Work, Scope, permissions, 1, 1, sha, true);
}

static StatisticReconciliationActualCrossViewExportAuthorizationBinding
    ExportBinding(string resultKind)
{
    var capability = StatRunExportContract.CapabilityFor(resultKind);
    var authSha = Hash('b');
    var requestSha = Hash('c');
    var receipt = Hash('d');
    var ownerSha = Hash('e');
    var semantic = StatisticReconciliationActualCanonical.Hash(
        "P10_CROSS_VIEW_EXPORT_AUTHORIZATION_BINDING_V1",
        ExportId,
        Work,
        "ASSIGNMENT",
        Scope,
        resultKind,
        "export-actor",
        authSha,
        StatisticReconciliationActualCrossViewAuthorization.ExportPolicy,
        capability,
        "command-a",
        requestSha,
        receipt,
        ownerSha);
    return new(
        ExportId,
        Work,
        "ASSIGNMENT",
        Scope,
        resultKind,
        "export-actor",
        authSha,
        StatisticReconciliationActualCrossViewAuthorization.ExportPolicy,
        capability,
        "command-a",
        requestSha,
        receipt,
        ownerSha,
        semantic);
}

static void Throws(Action action)
{
    try
    {
        action();
    }
    catch (StatisticReconciliationActualObservationException)
    {
        return;
    }
    throw new InvalidOperationException("Expected fail-closed exception.");
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException(
            $"Expected {expected}, actual {actual}.");
}


static string Hash(char value) => new(value, 64);
