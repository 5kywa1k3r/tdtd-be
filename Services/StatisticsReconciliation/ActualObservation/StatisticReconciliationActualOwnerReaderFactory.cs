using System.Collections.Immutable;
using tdtd_be.Data;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record StatisticReconciliationActualOwnerReaderBinding(
    string Layer,
    string Contract,
    string AuthoritativeOwner,
    string ExactPin,
    string ReadOperation,
    bool BoundByFactory,
    string? ExternalDependency);

internal sealed record StatisticReconciliationActualMongoOwnerReaderBundle(
    IStatisticReconciliationActualSourceOwnerReader Source,
    IStatisticReconciliationActualDirectProjectionOwnerReader DirectProjection,
    IStatisticReconciliationActualAggregateOwnerReader Aggregate,
    IStatisticReconciliationActualBasicOwnerReader Basic,
    IStatisticReconciliationActualAdvancedOwnerReader Advanced,
    IStatisticReconciliationActualP9DiffOwnerReader Diff,
    ImmutableArray<StatisticReconciliationActualOwnerReaderBinding> MappingReport);

internal static class StatisticReconciliationActualOwnerReaderFactory
{
    internal static StatisticReconciliationActualMongoOwnerReaderBundle Create(
        MongoDbContext context,
        IStatRunDirectProjectionReadOwner sourceOwner)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sourceOwner);
        return new StatisticReconciliationActualMongoOwnerReaderBundle(
            new StatisticReconciliationActualSourceMongoOwnerReader(sourceOwner),
            new StatisticReconciliationActualDirectProjectionMongoOwnerReader(context),
            new StatisticReconciliationActualAggregateMongoOwnerReader(context),
            new StatisticReconciliationActualBasicMongoOwnerReader(context),
            new StatisticReconciliationActualAdvancedMongoOwnerReader(context),
            new StatisticReconciliationActualP9DiffMongoOwnerReader(context),
            Describe());
    }

    internal static ImmutableArray<StatisticReconciliationActualOwnerReaderBinding>
        Describe()
        =>
        [
            new(
                "SOURCE_MEMBERSHIP",
                nameof(IStatisticReconciliationActualSourceOwnerReader),
                "StatRunDirectProjectionService.ResolveMembershipAsync",
                "runId+generationId+generationHash+membershipSignature+directSourceRevision",
                "P9 read-only included-set owner; exclusions are observed as absence",
                true,
                "The P9 owner exposes admitted members, not a raw excluded-candidate decision ledger"),
            new(
                "DIRECT_PROJECTION",
                nameof(IStatisticReconciliationActualDirectProjectionOwnerReader),
                "work_report_{field,table,label}_stat_values",
                "P9 completed current run + exact runId/generationId",
                "Mongo Find/Sort only; stored generation row-count fence",
                true,
                null),
            new(
                "AGGREGATE",
                nameof(IStatisticReconciliationActualAggregateOwnerReader),
                "work_report_{field,table,label}_stat_aggregates",
                "publicationScopeKey+publicationRevision+generationId+three store digests",
                "Mongo Find/Sort only; adapter recomputes pinned store digests",
                true,
                null),
            new(
                "BASIC",
                nameof(IStatisticReconciliationActualBasicOwnerReader),
                "work_assignment_basic_summary_snapshots",
                "ownerSnapshotId",
                "Mongo Find/Sort only; adapter reveals dirty/deleted/boundary drift",
                true,
                null),
            new(
                "ADVANCED",
                nameof(IStatisticReconciliationActualAdvancedOwnerReader),
                "work_assignment_advanced_summary_{day,month,year}_nodes",
                "exact day/month/year node-id sets",
                "Mongo Find/Sort only; adapter reveals dirty/deleted/boundary drift",
                true,
                null),
            new(
                "DIFF",
                nameof(IStatisticReconciliationActualP9DiffOwnerReader),
                StatisticReconciliationActualP9DiffMongoOwnerReader.CollectionName,
                "resultId",
                "Mongo Find/Sort only; P9 typed Diff remains distinct from P10 delta",
                true,
                null),
            new(
                "API",
                nameof(IStatisticReconciliationActualApiOwnerReader),
                "authoritative P9 Kestrel response",
                "request/filter/auth/etag/generation",
                "StatisticReconciliationActualHttpApiOwnerReader: service-auth HTTP; authorization before page",
                false,
                "Bound separately by AddStatisticReconciliationActualCapture through its typed HttpClient/options; fails closed when the API-owner base URI or service token is unconfigured"),
            new(
                "EXPORT",
                "StatisticReconciliationActualExportParser",
                "immutable StatRunExportArtifact bytes",
                "artifact digest+manifest+media type",
                "StatRunExportService.P10ReadOwner: read-only metadata, bytes, and column-manifest resolver",
                false,
                "Bound separately by AddStatisticReconciliationActualCapture through IStatisticReconciliationActualExportOwnerReader; requires an unambiguous sidecar and never mutates download counters"),
            new(
                "COHERENT_BOUNDARY",
                nameof(IStatisticReconciliationActualCoherentBoundaryReader),
                "source/config/catalog/runtime/result/export boundary owners",
                "same pre/post boundary semantic hash",
                "StatisticReconciliationActualMongoBoundaryReaderFactory: multi-owner projection-only boundary reader",
                false,
                "Bound separately by AddStatisticReconciliationActualCapture; capture requires the same pre/post boundary semantic hash")
        ];
}
