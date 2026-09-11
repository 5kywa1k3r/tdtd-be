using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

/// <summary>
/// A bounded, projection-only description of one authoritative P5-P9 owner
/// slice. The descriptor is assembled by the trusted reconciliation worker;
/// this reader never accepts a client-supplied collection or filter.
/// </summary>
internal sealed record StatisticReconciliationActualMongoBoundarySlice(
    string Domain,
    string Collection,
    string OwnerKey,
    string GenerationKey,
    long Revision,
    BsonDocument Filter,
    ImmutableArray<string> ProjectionFields);

internal sealed record StatisticReconciliationActualMongoBoundaryDescriptor(
    string ReconciliationId,
    string WorkId,
    string ScopeAssignmentId,
    string SourceSetSha256,
    string ConfigurationBundleSha256,
    string FilterSha256,
    string AuthorizationSnapshotSha256,
    ImmutableArray<StatisticReconciliationActualMongoBoundarySlice> Slices);

internal interface IStatisticReconciliationActualCoherentBoundaryReaderFactory
{
    IStatisticReconciliationActualCoherentBoundaryReader Create(
        StatisticReconciliationActualMongoBoundaryDescriptor descriptor);
}

internal sealed class StatisticReconciliationActualMongoBoundaryReaderFactory(
    MongoDbContext context)
    : IStatisticReconciliationActualCoherentBoundaryReaderFactory
{
    public IStatisticReconciliationActualCoherentBoundaryReader Create(
        StatisticReconciliationActualMongoBoundaryDescriptor descriptor)
        => new StatisticReconciliationActualMongoBoundaryReader(
            context,
            descriptor ?? throw new ArgumentNullException(nameof(descriptor)));
}

/// <summary>
/// Re-reads exact Mongo owner slices and derives a deterministic coherent
/// boundary. Only find/project/sort operations are exposed; there is no write,
/// refresh, rebuild, reset, export-generation, or freshness-update path.
/// </summary>
internal sealed class StatisticReconciliationActualMongoBoundaryReader(
    MongoDbContext context,
    StatisticReconciliationActualMongoBoundaryDescriptor descriptor)
    : IStatisticReconciliationActualCoherentBoundaryReader
{
    internal const int MaxSlices = 64;
    internal const int MaxProjectionFields = 64;
    internal const int MaxFilterBytes = 64 * 1024;
    internal const int MaxFilterDepth = 16;
    internal const int MaxFilterArrayElements = 4096;
    internal const int MaxDocumentsPerSlice = 100_000;
    internal const int MaxTotalDocuments = 500_000;

    private static readonly Regex CollectionToken = new(
        "^[a-z][a-z0-9_]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ProjectionPath = new(
        "^[A-Za-z_][A-Za-z0-9_.]{0,255}$",
        RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ForbiddenFilterOperators =
        new(StringComparer.Ordinal)
        {
            "$where",
            "$regex",
            "$expr"
        };
    private static readonly IReadOnlyDictionary<string, HashSet<string>>
        AllowedCollectionsByDomain =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                [StatisticReconciliationActualBoundaryDomains.Source] =
                    new(StringComparer.Ordinal)
                    {
                        "works",
                        "work_assignments",
                        "work_assignment_report",
                        "work_assignment_report_sections",
                        "work_report_payloads",
                        "work_report_table_values",
                        "work_report_periods",
                        "dynamic_flow_mapping_provenance",
                        "dynamic_flow_templates",
                        "dynamic_flow_template_versions"
                    },
                [StatisticReconciliationActualBoundaryDomains.Configuration] =
                    new(StringComparer.Ordinal)
                    {
                        "dynamic_form_templates",
                        "dynamic_form_sections",
                        "dynamic_flow_templates",
                        "dynamic_flow_template_versions",
                        "work_assignment_aggregate_configs",
                        "work_assignment_basic_summary_configs",
                        "work_assignment_advanced_summary_configs",
                        "work_report_statistic_diff_configs"
                    },
                [StatisticReconciliationActualBoundaryDomains.Catalog] =
                    new(StringComparer.Ordinal)
                    {
                        "labels",
                        "label_enum_catalogs",
                        "label_enum_option_read_models",
                        "dynamic_form_templates",
                        "dynamic_form_sections",
                        "dynamic_flow_templates",
                        "dynamic_flow_template_versions"
                    },
                [StatisticReconciliationActualBoundaryDomains.Runtime] =
                    new(StringComparer.Ordinal)
                    {
                        "dynamic_flow_instances",
                        "dynamic_flow_step_instances",
                        "dynamic_flow_gateway_instances",
                        "dynamic_flow_gateway_contributions",
                        "dynamic_flow_participant_snapshots",
                        "dynamic_flow_execution_epochs",
                        "dynamic_flow_periodic_schedules",
                        "dynamic_flow_periodic_occurrences",
                        "dynamic_flow_events",
                        "dynamic_flow_runtime_events",
                        "dynamic_flow_mapping_events",
                        "dynamic_flow_mapping_provenance"
                    },
                [StatisticReconciliationActualBoundaryDomains.Result] =
                    new(StringComparer.Ordinal)
                    {
                        "work_report_statistic_rebuild_jobs",
                        "work_report_field_stat_values",
                        "work_report_table_stat_values",
                        "work_report_label_stat_values",
                        "work_report_field_stat_aggregates",
                        "work_report_table_stat_aggregates",
                        "work_report_label_stat_aggregates",
                        "work_assignment_basic_summary_snapshots",
                        "work_assignment_advanced_summary_day_nodes",
                        "work_assignment_advanced_summary_month_nodes",
                        "work_assignment_advanced_summary_year_nodes",
                        "work_report_statistic_diff_results"
                    },
                [StatisticReconciliationActualBoundaryDomains.Export] =
                    new(StringComparer.Ordinal)
                    {
                        "work_report_statistic_exports",
                        "work_report_statistic_diff_exports"
                    }
            };

    private readonly object _authorizationGate = new();
    private string? _initialRunAuthorizationSha256;

    public async Task<StatisticReconciliationActualCoherentBoundary> ReadAsync(
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var slices = NormalizeSlices(descriptor.Slices);
        ArgumentNullException.ThrowIfNull(context);

        // The coherent authorization pin is owned by the persisted run. The
        // descriptor is only an expected value, never the source of truth.
        var run = await context.StatisticReconciliationRuns
            .Find(value =>
                value.Id == descriptor.ReconciliationId &&
                value.WorkId == descriptor.WorkId &&
                value.ScopeAssignmentId == descriptor.ScopeAssignmentId &&
                !value.IsDeleted)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw Fail("BOUNDARY_RUN_NOT_FOUND");
        var storedAuthorizationSha256 = ValidateRunAuthorization(run);

        var pins = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualBoundaryPin>(slices.Length);
        var totalDocuments = 0;
        foreach (var slice in slices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var collection = context.Db.GetCollection<BsonDocument>(
                slice.Collection);
            var projection = new BsonDocument("_id", 1);
            foreach (var field in slice.ProjectionFields)
                projection[field] = 1;

            var documents = await collection
                .Find(slice.Filter)
                .Project<BsonDocument>(projection)
                .Sort(new BsonDocument("_id", 1))
                .Limit(ReadLimit(totalDocuments))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (documents.Count > MaxDocumentsPerSlice)
                throw Fail("BOUNDARY_SLICE_LIMIT");
            if (documents.Count > MaxTotalDocuments - totalDocuments)
                throw Fail("BOUNDARY_TOTAL_DOCUMENT_LIMIT");
            totalDocuments += documents.Count;

            var rowsSha256 = StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_MONGO_BOUNDARY_ROWS_V1",
                documents.Select(HashBson));
            var sliceSha256 = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_MONGO_BOUNDARY_SLICE_V1",
                slice.Domain,
                slice.Collection,
                slice.OwnerKey,
                slice.GenerationKey,
                StatisticReconciliationActualCanonical.Integer(slice.Revision),
                HashBson(slice.Filter),
                StatisticReconciliationActualCanonical.HashSequence(
                    "P10_ACTUAL_MONGO_BOUNDARY_PROJECTION_V1",
                    slice.ProjectionFields),
                StatisticReconciliationActualCanonical.Integer(documents.Count),
                rowsSha256);
            pins.Add(new StatisticReconciliationActualBoundaryPin(
                slice.Domain,
                $"{slice.Collection}:{slice.OwnerKey}",
                slice.GenerationKey,
                slice.Revision,
                sliceSha256));
        }

        return StatisticReconciliationActualCoherentCaptureCoordinator
            .CreateBoundary(
                descriptor.ReconciliationId,
                descriptor.WorkId,
                descriptor.ScopeAssignmentId,
                descriptor.SourceSetSha256,
                descriptor.ConfigurationBundleSha256,
                descriptor.FilterSha256,
                storedAuthorizationSha256,
                pins.MoveToImmutable());
    }

    internal string ValidateRunAuthorization(StatisticReconciliationRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var runAuthorizationSha256 = RunAuthorizationSha256(run);
        var storedAuthorizationSha256 =
            StatisticReconciliationActualCanonical.Sha256(
                run.AuthorizationSnapshotHash,
                "BOUNDARY_RUN_AUTHORIZATION_SHA256");
        if (!StringComparer.Ordinal.Equals(
                runAuthorizationSha256,
                storedAuthorizationSha256))
        {
            throw Fail("BOUNDARY_RUN_AUTHORIZATION_TAMPERED");
        }

        lock (_authorizationGate)
        {
            if (_initialRunAuthorizationSha256 is null)
            {
                var expectedAuthorizationSha256 =
                    StatisticReconciliationActualCanonical.Sha256(
                        descriptor.AuthorizationSnapshotSha256,
                        "BOUNDARY_EXPECTED_AUTHORIZATION_SHA256");
                if (!StringComparer.Ordinal.Equals(
                        expectedAuthorizationSha256,
                        storedAuthorizationSha256))
                {
                    throw Fail("BOUNDARY_EXPECTED_AUTHORIZATION_MISMATCH");
                }
                _initialRunAuthorizationSha256 = storedAuthorizationSha256;
            }
        }

        // A later persisted-owner authorization change is returned to the
        // coordinator so the pre/post boundary comparison becomes STALE.
        return storedAuthorizationSha256;
    }

    internal static string RunAuthorizationSha256(
        StatisticReconciliationRun run)
    {
        var permissions = run.PermissionCodes ?? [];
        if (permissions.Count == 0 ||
            !permissions.SequenceEqual(
                permissions.OrderBy(value => value, StringComparer.Ordinal),
                StringComparer.Ordinal) ||
            permissions.Distinct(StringComparer.Ordinal).Count() !=
            permissions.Count)
            throw Fail("BOUNDARY_RUN_PERMISSION_CODES_INVALID");
        return StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_RECONCILIATION_AUTHORIZATION_V1",
            actorUserId = run.ActorUserId,
            tenantUnitId = run.TenantUnitId,
            workId = run.WorkId,
            scopeAssignmentId = run.ScopeAssignmentId,
            permissionCodes = permissions,
            policy = "AUTHORIZED_SCOPE_SERVER_DERIVED_V1"
        });
    }

    internal static int ReadLimit(int totalDocuments)
    {
        if (totalDocuments < 0 || totalDocuments > MaxTotalDocuments)
            throw Fail("BOUNDARY_TOTAL_DOCUMENT_COUNT_INVALID");
        return Math.Min(
                   MaxDocumentsPerSlice,
                   MaxTotalDocuments - totalDocuments) +
               1;
    }

    internal static ImmutableArray<StatisticReconciliationActualMongoBoundarySlice>
        NormalizeSlices(
            ImmutableArray<StatisticReconciliationActualMongoBoundarySlice> slices)
    {
        if (slices.IsDefaultOrEmpty)
            throw Fail("BOUNDARY_SLICES_REQUIRED");
        if (slices.Length > MaxSlices)
            throw Fail("BOUNDARY_SLICE_COUNT_LIMIT");
        return slices.Select(Normalize).ToImmutableArray();
    }

    internal static StatisticReconciliationActualMongoBoundarySlice Normalize(
        StatisticReconciliationActualMongoBoundarySlice value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var domain = StatisticReconciliationActualCanonical.Upper(
            value.Domain,
            "BOUNDARY_SLICE_DOMAIN");
        if (!StatisticReconciliationActualBoundaryDomains.Required.Contains(
                domain,
                StringComparer.Ordinal))
            throw Fail("BOUNDARY_SLICE_DOMAIN_INVALID");
        var collection = StatisticReconciliationActualCanonical.Required(
            value.Collection,
            "BOUNDARY_SLICE_COLLECTION");
        if (!CollectionToken.IsMatch(collection))
            throw Fail("BOUNDARY_SLICE_COLLECTION_INVALID");
        if (!AllowedCollectionsByDomain.TryGetValue(domain, out var allowed) ||
            !allowed.Contains(collection))
        {
            throw Fail("BOUNDARY_SLICE_COLLECTION_NOT_ALLOWED");
        }
        var ownerKey = StatisticReconciliationActualCanonical.Required(
            value.OwnerKey,
            "BOUNDARY_SLICE_OWNER_KEY");
        var generationKey = StatisticReconciliationActualCanonical.Required(
            value.GenerationKey,
            "BOUNDARY_SLICE_GENERATION_KEY");
        if (value.Revision < 0 || value.Filter is null ||
            value.ProjectionFields.IsDefaultOrEmpty)
            throw Fail("BOUNDARY_SLICE_SHAPE_INVALID");
        if (value.ProjectionFields.Length > MaxProjectionFields)
            throw Fail("BOUNDARY_SLICE_PROJECTION_LIMIT");
        var filter = NormalizeFilter(value.Filter);
        var fields = value.ProjectionFields
            .Select(field => StatisticReconciliationActualCanonical.Required(
                field,
                "BOUNDARY_SLICE_PROJECTION"))
            .OrderBy(field => field, StringComparer.Ordinal)
            .ToImmutableArray();
        if (fields.Any(field => !ProjectionPath.IsMatch(field) || field == "_id") ||
            fields.Distinct(StringComparer.Ordinal).Count() != fields.Length)
            throw Fail("BOUNDARY_SLICE_PROJECTION_INVALID");
        return value with
        {
            Domain = domain,
            Collection = collection,
            OwnerKey = ownerKey,
            GenerationKey = generationKey,
            Filter = filter,
            ProjectionFields = fields
        };
    }

    internal static BsonDocument NormalizeFilter(BsonDocument value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateFilterValue(value, 1);
        byte[] serialized;
        try
        {
            serialized = value.ToBson();
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException &&
            exception is not StackOverflowException)
        {
            throw Fail("BOUNDARY_FILTER_SERIALIZATION_INVALID");
        }
        if (serialized.Length > MaxFilterBytes)
            throw Fail("BOUNDARY_FILTER_SIZE_LIMIT");
        return (BsonDocument)value.DeepClone();
    }

    private static void ValidateFilterValue(BsonValue value, int depth)
    {
        switch (value)
        {
            case BsonDocument document:
                if (depth > MaxFilterDepth)
                    throw Fail("BOUNDARY_FILTER_DEPTH_LIMIT");
                foreach (var element in document.Elements)
                {
                    if (ForbiddenFilterOperators.Contains(element.Name))
                        throw Fail("BOUNDARY_FILTER_OPERATOR_FORBIDDEN");
                    ValidateFilterValue(element.Value, depth + 1);
                }
                break;
            case BsonArray array:
                if (depth > MaxFilterDepth)
                    throw Fail("BOUNDARY_FILTER_DEPTH_LIMIT");
                if (array.Count > MaxFilterArrayElements)
                    throw Fail("BOUNDARY_FILTER_ARRAY_LIMIT");
                foreach (var item in array)
                    ValidateFilterValue(item, depth + 1);
                break;
            case BsonRegularExpression:
            case BsonJavaScriptWithScope:
            case BsonJavaScript:
                throw Fail("BOUNDARY_FILTER_OPERATOR_FORBIDDEN");
        }
    }

    private static string HashBson(BsonValue value)
    {
        var canonical = CanonicalBson(value);
        return Convert.ToHexString(SHA256.HashData(canonical.ToBson()))
            .ToLowerInvariant();
    }

    private static BsonValue CanonicalBson(BsonValue value)
        => value switch
        {
            BsonDocument document => new BsonDocument(document.Elements
                .OrderBy(element => element.Name, StringComparer.Ordinal)
                .Select(element => new BsonElement(
                    element.Name,
                    CanonicalBson(element.Value)))),
            BsonArray array => new BsonArray(array.Select(CanonicalBson)),
            _ => value.DeepClone()
        };

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"ACTUAL_MONGO_{reason}");
}
