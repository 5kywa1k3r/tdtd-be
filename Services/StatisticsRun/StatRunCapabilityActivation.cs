using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Common.Capabilities;
using tdtd_be.Common.Errors;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsRun;

public static class StatRunCapabilities
{
    public const string DirectFieldTableLabel = "DIRECT_FIELD_TABLE_LABEL";
    public const string BasicSummary = "BASIC_SUMMARY";
    public const string AdvancedSummary = "ADVANCED_SUMMARY";
    public const string Diff = "DIFF";
    public const string FlowScopes = "FLOW_SCOPES";

    public static readonly IReadOnlySet<string> All =
        new[] { DirectFieldTableLabel, BasicSummary, AdvancedSummary, Diff, FlowScopes }
            .ToFrozenSet(StringComparer.Ordinal);
}

public static class StatRunRouteRegistry
{
    public const string LifecycleDirectProjector = "P9_LFC_DIRECT_PROJECTOR";
    public const string DirectFieldResult = "P9_DIRECT_FIELD_RESULT";
    public const string DirectTableResult = "P9_DIRECT_TABLE_RESULT";
    public const string DirectLabelResult = "P9_DIRECT_LABEL_RESULT";
    public const string DirectTextResult = "P9_DIRECT_TEXT_RESULT";
    public const string BasicResult = "P9_BASIC_RESULT";
    public const string AdvancedBuild = "P9_ADVANCED_BUILD";
    public const string AdvancedResult = "P9_ADVANCED_RESULT";
    public const string DiffExecute = "P9_DIFF_EXECUTE";
    public const string DiffResult = "P9_DIFF_RESULT";
    public const string FlowContribution = "P9_FLOW_CONTRIBUTION";
    public const string DirectExport = "P9_DIRECT_EXPORT";
    public const string BasicExport = "P9_BASIC_EXPORT";
    public const string AdvancedExport = "P9_ADVANCED_EXPORT";
    public const string DiffExport = "P9_DIFF_EXPORT";
    public const string FlowExport = "P9_FLOW_EXPORT";

    private static readonly IReadOnlyDictionary<string, (string CapabilityId, int MinimumStage)> Routes =
        new Dictionary<string, (string, int)>(StringComparer.Ordinal)
        {
            ["P9_CORE_DIRECT_JOB"] = (StatRunCapabilities.DirectFieldTableLabel, 0),
            [LifecycleDirectProjector] = (StatRunCapabilities.DirectFieldTableLabel, 0),
            ["P9_CORE_BASIC_JOB"] = (StatRunCapabilities.BasicSummary, 0),
            ["P9_CORE_ADVANCED_JOB"] = (StatRunCapabilities.AdvancedSummary, 0),
            ["P9_CORE_DIFF_JOB"] = (StatRunCapabilities.Diff, 0),
            ["P9_CORE_FLOW_SCOPES_JOB"] = (StatRunCapabilities.FlowScopes, 0),
            [DirectFieldResult] = (StatRunCapabilities.DirectFieldTableLabel, 1),
            [DirectTableResult] = (StatRunCapabilities.DirectFieldTableLabel, 1),
            [DirectLabelResult] = (StatRunCapabilities.DirectFieldTableLabel, 1),
            [DirectTextResult] = (StatRunCapabilities.DirectFieldTableLabel, 1),
            [BasicResult] = (StatRunCapabilities.BasicSummary, 2),
            [AdvancedBuild] = (StatRunCapabilities.AdvancedSummary, 3),
            [AdvancedResult] = (StatRunCapabilities.AdvancedSummary, 3),
            [DiffExecute] = (StatRunCapabilities.Diff, 4),
            [DiffResult] = (StatRunCapabilities.Diff, 4),
            [FlowContribution] = (StatRunCapabilities.FlowScopes, 5),
            [DirectExport] = (StatRunCapabilities.DirectFieldTableLabel, 7),
            [BasicExport] = (StatRunCapabilities.BasicSummary, 7),
            [AdvancedExport] = (StatRunCapabilities.AdvancedSummary, 7),
            [DiffExport] = (StatRunCapabilities.Diff, 7),
            [FlowExport] = (StatRunCapabilities.FlowScopes, 7),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string CoreJob(string capabilityId)
        => capabilityId switch
        {
            StatRunCapabilities.DirectFieldTableLabel => "P9_CORE_DIRECT_JOB",
            StatRunCapabilities.BasicSummary => "P9_CORE_BASIC_JOB",
            StatRunCapabilities.AdvancedSummary => "P9_CORE_ADVANCED_JOB",
            StatRunCapabilities.Diff => "P9_CORE_DIFF_JOB",
            StatRunCapabilities.FlowScopes => "P9_CORE_FLOW_SCOPES_JOB",
            _ => "P9_CORE_UNKNOWN_JOB"
        };

    public static bool TryResolve(string? routeId, out string capabilityId)
    {
        if (Routes.TryGetValue(Normalize(routeId), out var route))
        {
            capabilityId = route.CapabilityId;
            return true;
        }

        capabilityId = string.Empty;
        return false;
    }

    public static int MinimumStage(string? routeId)
        => Routes.TryGetValue(Normalize(routeId), out var route)
            ? route.MinimumStage
            : int.MaxValue;

    public static IReadOnlyDictionary<string, string> Snapshot()
        => Routes.ToFrozenDictionary(
            item => item.Key,
            item => item.Value.CapabilityId,
            StringComparer.Ordinal);

    private static string Normalize(string? value)
        => value?.Trim().ToUpperInvariant() ?? string.Empty;
}

public sealed record StatRunCandidateBinding(
    string ChainId,
    string PromptId,
    int Stage,
    string CatalogVersion,
    string CatalogRawSha256,
    string CatalogSemanticSha256,
    string SchemaRawSha256,
    string SchemaSemanticSha256,
    string StageLockSha256,
    string DatabaseName,
    IReadOnlyList<string> Promotions);

public sealed record StatRunCandidateEvaluation(
    bool Enabled,
    string? Reason,
    string RouteId,
    string CapabilityId,
    StatRunCandidateBinding? Binding);

public interface IStatRunCandidateActivation
{
    StatRunCandidateBinding RequireFoundation(string capabilityId, string routeId);
    StatRunCandidateEvaluation EvaluateFoundation(string capabilityId, string routeId);
    StatRunCandidateBinding RequireCapability(string capabilityId, string routeId);
    StatRunCandidateEvaluation EvaluateCapability(string capabilityId, string routeId);
}

/// <summary>
/// P9 candidate execution boundary. It deliberately requires a complete,
/// hash-bound stage bundle in a Testing process and a chain-owned database.
/// A boolean, request flag, or catalog version alone can never activate it.
/// </summary>
public sealed class StatRunCapabilityActivation : IStatRunCandidateActivation
{
    public const string RequiredChainId = "p9_chain_20260804012144_7185";
    public const string RequiredPromptId = "P9-01";
    public const string LifecycleRequiredPromptId = "P9-02";
    public const string RequiredCatalogVersion = "1.6";
    public const string RequiredCurrentCatalogVersion = "1.5";
    public const string RequiredCurrentCatalogSemanticSha256 =
        "e3c335617721bbf8cd09c62c5e76377f23f3d024b20c848bf66c8c539f0c9d2f";
    public const string RequiredCurrentRawSha256 =
        "d5e77af2cf8a4d4d959c8d642b82fa0bccf5b7b3c8369c6122c5875c01e4c535";
    public const string RequiredSourceCatalogRawSha256 =
        "5b6e3fb828a6130bce2464dd98b1e8e66fd540335b0997a19187c799a969f5b5";
    public const string RequiredSourceSchemaRawSha256 =
        "d6eba41c05a1ff4d65d8c497fa5cd56b53d6d99ae3145475553bab55722cffa4";
    public const string RequiredSourceSchemaSemanticSha256 =
        "9bab8219c4c097977de2417885206caacec9bca5a188a588d0d1a96063b84477";
    public const string RequiredParentLockRawSha256 =
        "9e580106ccee20cd6ff7975dda652833123ef709024bea74087826c34986e42a";
    public const string RequiredCandidateCatalogRawSha256 =
        "3739b331995975b34d84c0b66e43011cf84da5c8181fd2b68b5f76e89ce8dbfa";
    public const string RequiredCandidateCatalogSemanticSha256 =
        "0f0b900db1150969496bf42aa143e2527ce65bcecc6d674062eb86f2204d7265";
    public const string RequiredCandidateSchemaRawSha256 =
        "603304c9798805c972370494d3939bdc7da324939ba9ab98a82800241f1b6940";
    public const string RequiredCandidateSchemaSemanticSha256 =
        "da0c80f265845f24aaf282e0a0369272273b1986520dae07171cda85b28b3fed";
    public const string RequiredStageLockRawSha256 =
        "273ad1f24be203be3fd18d9f1032d0b1ed780212b9792b533be9d60f44ddf66a";
    public const string RequiredGeneratorRawSha256 =
        "262578d0134f0b1f9facf6ac7b5942ba36eb9b0224f8b987f27160723dd46a1b";
    public const string LifecycleRequiredStageLockRawSha256 =
        "b71bcbdc369ca7b147dc0aff95515b5c7297eb6cc34185177372d9484df5b565";
    public const string LifecycleRequiredGeneratorRawSha256 =
        "613066d10cbe0a21483556877c9f1c1d2a6d76910390b465ec7b0f3718f4c433";
    public const string PublishedPromptId = "P9-12";
    public const string PublishedCatalogRawSha256 =
        "a790be94e4598208de08af39f8782a267ce434db2c20242a811991429932233c";
    public const string PublishedCatalogSemanticSha256 =
        "39cdb98dda168f5901f48a94640fe5d50943c5bd78ed32d5e05e8b719b23d13b";
    public const string PublishedSchemaRawSha256 =
        "603304c9798805c972370494d3939bdc7da324939ba9ab98a82800241f1b6940";
    public const string PublishedSchemaSemanticSha256 =
        "da0c80f265845f24aaf282e0a0369272273b1986520dae07171cda85b28b3fed";
    public const string PublishedCurrentRawSha256 =
        "b1ecff835b16316798a27cf9285956b91f2fc16d2c32c2cf27e11e4fb5b2ea26";
    public const string PublishedLockRawSha256 =
        "be02e58b97584e7638f591f7a8b37e6cb51bda8ca1ae100ebf1eb4ef9dc8bb6f";
    public const string PublishedSealStageLockRawSha256 =
        "9c1acc91c5c1d2683da51c074e08bdac264936bf7921c93cb024c1bbe4c0c396";
    private static readonly IReadOnlyList<string> PublishedPromotions =
        Array.AsReadOnly(new[]
        {
            StatRunCapabilities.DirectFieldTableLabel,
            StatRunCapabilities.BasicSummary,
            StatRunCapabilities.AdvancedSummary,
            StatRunCapabilities.Diff,
            StatRunCapabilities.FlowScopes
        });

    private const string CurrentRelativePath =
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json";
    private const string ParentLockRelativePath =
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json";
    private const string SourceCatalogRelativePath =
        "docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_5.json";
    private const string SourceSchemaRelativePath =
        "docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_5.schema.json";
    private const string GeneratorRelativePath =
        "scripts/generate-p9-stage0-candidate.mjs";
    private const string CandidateDirectoryRelativePath =
        ".p9-artifacts/catalog-candidate/p9_chain_20260804012144_7185/P9-01";
    private const string LifecycleGeneratorRelativePath =
        "scripts/generate-p9-02-stage0-candidate.mjs";
    private const string LifecycleParentStageLockRelativePath =
        ".p9-artifacts/catalog-candidate/p9_chain_20260804012144_7185/P9-01/stage-lock.json";
    private const string LifecycleCandidateDirectoryRelativePath =
        ".p9-artifacts/catalog-candidate/p9_chain_20260804012144_7185/P9-02";

    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly MongoOptions _mongo;

    public StatRunCapabilityActivation(
        IConfiguration configuration,
        IHostEnvironment environment,
        IOptions<MongoOptions> mongo)
    {
        _configuration = configuration;
        _environment = environment;
        _mongo = mongo.Value;
    }

    public StatRunCandidateBinding RequireFoundation(
        string capabilityId,
        string routeId)
        => RequireCapability(capabilityId, routeId);

    public StatRunCandidateBinding RequireCapability(
        string capabilityId,
        string routeId)
    {
        var evaluation = EvaluateCapability(capabilityId, routeId);
        if (evaluation.Enabled && evaluation.Binding is not null)
            return evaluation.Binding;

        if (string.Equals(
                evaluation.Reason,
                "CANDIDATE_DISABLED",
                StringComparison.Ordinal) &&
            string.Equals(
                DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                RequiredCurrentCatalogVersion,
                StringComparison.Ordinal) &&
            P8RollbackBarrierEntry(evaluation.RouteId) is { } rollbackEntry)
        {
            StatConfigPhaseBarrier.Reject(
                rollbackEntry,
                string.Equals(
                    evaluation.RouteId,
                    StatRunRouteRegistry.BasicResult,
                    StringComparison.Ordinal)
                    ? "BASIC_SUMMARY_RESULT"
                    : evaluation.RouteId);
        }

        var error = string.Equals(
            evaluation.Reason,
            "ROUTE_NOT_PROVEN",
            StringComparison.Ordinal)
            ? AppErrorCode.STAT_RUN_ROUTE_NOT_PROVEN
            : string.Equals(
                evaluation.Reason,
                "CAPABILITY_CONFLICT",
                StringComparison.Ordinal)
                ? AppErrorCode.STAT_RUN_CAPABILITY_CONFLICT
                : AppErrorCode.STAT_RUN_CANDIDATE_INVALID;
        throw new AppException(
            error,
            new
            {
                reason = evaluation.Reason ?? "CANDIDATE_INVALID",
                routeId = evaluation.RouteId,
                capabilityId = evaluation.CapabilityId,
                requiredChainId = RequiredChainId,
                requiredCatalogVersion = RequiredCatalogVersion,
                currentPhase = StatConfigPhaseBarrier.CurrentPhase,
                writes = 0
            });
    }

    public StatRunCandidateEvaluation EvaluateFoundation(
        string capabilityId,
        string routeId)
        => EvaluateCapability(capabilityId, routeId);

    public StatRunCandidateEvaluation EvaluateCapability(
        string capabilityId,
        string routeId)
    {
        capabilityId = Normalize(capabilityId);
        routeId = Normalize(routeId);
        if (!StatRunCapabilities.All.Contains(capabilityId))
            return Disabled("CAPABILITY_CONFLICT", routeId, capabilityId);
        if (!StatRunRouteRegistry.TryResolve(routeId, out var mappedCapability) ||
            !string.Equals(mappedCapability, capabilityId, StringComparison.Ordinal))
        {
            return Disabled("ROUTE_NOT_PROVEN", routeId, capabilityId);
        }

        try
        {
            var minimumStage = StatRunRouteRegistry.MinimumStage(routeId);
            var candidateEnabled =
                _configuration.GetValue<bool?>("StatRunCandidate:Enabled") == true;
            if (!candidateEnabled &&
                StatisticReconciliationCapabilityActivation
                    .IsKnownGeneratedCatalogMetadata(
                        DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                        DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
                        DynamicFormFlowCapabilityCatalogMetadata.SchemaSha256) &&
                HasKnownP10PublicationState())
            {
                return new StatRunCandidateEvaluation(
                    true,
                    null,
                    routeId,
                    capabilityId,
                    ValidatePublishedBundle(capabilityId, minimumStage));
            }
            var configuredStageLock = _configuration["StatRunCandidate:StageLockPath"];
            var isFeatureCandidate =
                minimumStage > 0 ||
                (!string.IsNullOrWhiteSpace(configuredStageLock) &&
                 Path.GetFileName(
                     Path.GetDirectoryName(Path.GetFullPath(configuredStageLock))) is
                     "P9-03" or "P9-04" or "P9-05" or "P9-06" or "P9-07" or "P9-08" or
                     "P9-09" or "P9-10" or "P9-11");
            return new StatRunCandidateEvaluation(
                true,
                null,
                routeId,
                capabilityId,
                isFeatureCandidate
                    ? ValidateFeatureCandidateBundle(capabilityId, minimumStage)
                    : string.Equals(
                    routeId,
                    StatRunRouteRegistry.LifecycleDirectProjector,
                    StringComparison.Ordinal)
                    ? ValidateLifecycleCandidateBundle()
                    : ValidateCandidateBundle());
        }
        catch (CandidateValidationException exception)
        {
            return Disabled(exception.Reason, routeId, capabilityId);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            JsonException or
            CryptographicException or
            ArgumentException or
            NotSupportedException or
            InvalidOperationException)
        {
            return Disabled("CANDIDATE_INVALID", routeId, capabilityId);
        }
    }

    private bool HasKnownP10PublicationState()
    {
        var workspaceRoot = ResolveWorkspaceRoot();
        var contractRoot = WorkspacePath(
            workspaceRoot,
            "tdtd-be/Contracts/DynamicFormFlow");
        var currentBytes = RequirePinnedFile(
            Path.Combine(
                contractRoot,
                "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json"),
            null,
            "CURRENT_CATALOG_MISMATCH");
        var lockBytes = RequirePinnedFile(
            Path.Combine(
                contractRoot,
                "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json"),
            null,
            "CATALOG_LOCK_MISMATCH");
        return StatisticReconciliationCapabilityActivation
            .ClassifyCatalogPublicationState(
                Hash(currentBytes),
                Hash(lockBytes)) is
            StatisticReconciliationCatalogPublicationState.PrePublish or
            StatisticReconciliationCatalogPublicationState.RolledBack or
            StatisticReconciliationCatalogPublicationState.Restored;
    }

    private StatRunCandidateBinding ValidatePublishedBundle(
        string requestedCapabilityId,
        int minimumStage)
    {
        Require(StatConfigPhaseBarrier.CurrentPhase == 8, "BROAD_PHASE_CHANGED");
        Require(
            StatisticReconciliationCapabilityActivation
                .IsKnownGeneratedCatalogMetadata(
                    DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                    DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
                    DynamicFormFlowCapabilityCatalogMetadata.SchemaSha256),
            "CURRENT_CATALOG_MISMATCH");

        var workspaceRoot = ResolveWorkspaceRoot();
        var contractRoot = WorkspacePath(
            workspaceRoot,
            "tdtd-be/Contracts/DynamicFormFlow");
        var currentPath = Path.Combine(
            contractRoot,
            "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json");
        var lockPath = Path.Combine(
            contractRoot,
            "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json");
        var catalogPath = Path.Combine(
            contractRoot,
            "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.json");
        var schemaPath = Path.Combine(
            contractRoot,
            "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.schema.json");
        var currentBytes = RequirePinnedFile(
            currentPath,
            null,
            "CURRENT_CATALOG_MISMATCH");
        var lockBytes = RequirePinnedFile(
            lockPath,
            null,
            "CATALOG_LOCK_MISMATCH");
        var publicationState = StatisticReconciliationCapabilityActivation
            .ClassifyCatalogPublicationState(
                Hash(currentBytes),
                Hash(lockBytes));
        Require(
            publicationState is
                StatisticReconciliationCatalogPublicationState.PrePublish or
                StatisticReconciliationCatalogPublicationState.RolledBack or
                StatisticReconciliationCatalogPublicationState.Restored,
            "P10_CATALOG_STATE_INVALID");
        var catalogBytes = RequirePinnedFile(
            catalogPath,
            PublishedCatalogRawSha256,
            "PUBLISHED_CATALOG_MISMATCH");
        var schemaBytes = RequirePinnedFile(
            schemaPath,
            PublishedSchemaRawSha256,
            "PUBLISHED_SCHEMA_MISMATCH");

        using var current = JsonDocument.Parse(currentBytes);
        using var catalog = JsonDocument.Parse(catalogBytes);
        using var schema = JsonDocument.Parse(schemaBytes);
        using var catalogLock = JsonDocument.Parse(lockBytes);
        var restored = publicationState ==
            StatisticReconciliationCatalogPublicationState.Restored;
        Require(
            ReadString(current.RootElement, "catalogVersion") ==
                (restored
                    ? StatisticReconciliationCapabilityActivation.RequiredCatalogVersion
                    : RequiredCatalogVersion) &&
            ReadString(current.RootElement, "catalogSha256") ==
                (restored
                    ? StatisticReconciliationCapabilityActivation
                        .PublishedCatalogSemanticSha256
                    : PublishedCatalogSemanticSha256) &&
            ReadString(current.RootElement, "schemaSha256") ==
                (restored
                    ? StatisticReconciliationCapabilityActivation
                        .PublishedSchemaSemanticSha256
                    : PublishedSchemaSemanticSha256),
            "CURRENT_CATALOG_MISMATCH");
        Require(SemanticHash(catalog.RootElement) == PublishedCatalogSemanticSha256,
            "PUBLISHED_CATALOG_MISMATCH");
        Require(SemanticHash(schema.RootElement) == PublishedSchemaSemanticSha256,
            "PUBLISHED_SCHEMA_MISMATCH");
        ValidateCandidateSchema(schema.RootElement);
        ValidateFeatureCatalog(catalog.RootElement, 9, requestedCapabilityId, minimumStage);

        var entries = catalogLock.RootElement.GetProperty("publishedCatalogs")
            .EnumerateArray()
            .Where(item => ReadString(item, "catalogVersion") == RequiredCatalogVersion)
            .ToArray();
        Require(entries.Length == 1, "CATALOG_LOCK_MISMATCH");
        var entry = entries[0];
        Require(
            ReadString(entry, "catalogFile") ==
                "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.json" &&
            ReadString(entry, "schemaFile") ==
                "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.schema.json" &&
            ReadString(entry, "catalogSha256") == PublishedCatalogSemanticSha256 &&
            ReadString(entry, "schemaSha256") == PublishedSchemaSemanticSha256,
            "CATALOG_LOCK_MISMATCH");
        var v17Entries = catalogLock.RootElement.GetProperty("publishedCatalogs")
            .EnumerateArray()
            .Where(item => ReadString(item, "catalogVersion") ==
                StatisticReconciliationCapabilityActivation.RequiredCatalogVersion)
            .ToArray();
        if (publicationState ==
            StatisticReconciliationCatalogPublicationState.PrePublish)
        {
            Require(v17Entries.Length == 0, "CATALOG_LOCK_MISMATCH");
        }
        else
        {
            Require(v17Entries.Length == 1, "CATALOG_LOCK_MISMATCH");
            var v17 = v17Entries[0];
            Require(
                ReadString(v17, "catalogFile") ==
                    "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_7.json" &&
                ReadString(v17, "schemaFile") ==
                    "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_7.schema.json" &&
                ReadString(v17, "catalogSha256") ==
                    StatisticReconciliationCapabilityActivation
                        .PublishedCatalogSemanticSha256 &&
                ReadString(v17, "schemaSha256") ==
                    StatisticReconciliationCapabilityActivation
                        .PublishedSchemaSemanticSha256,
                "CATALOG_LOCK_MISMATCH");
        }

        return new StatRunCandidateBinding(
            RequiredChainId,
            PublishedPromptId,
            9,
            RequiredCatalogVersion,
            PublishedCatalogRawSha256,
            PublishedCatalogSemanticSha256,
            PublishedSchemaRawSha256,
            PublishedSchemaSemanticSha256,
            PublishedSealStageLockRawSha256,
            _mongo.Database,
            PublishedPromotions);
    }

    private StatRunCandidateBinding ValidateCandidateBundle()
    {
        Require(
            _configuration.GetValue<bool?>("StatRunCandidate:Enabled") == true,
            "CANDIDATE_DISABLED");
        Require(_environment.IsEnvironment("Testing"), "ENVIRONMENT_MISMATCH");
        Require(StatConfigPhaseBarrier.CurrentPhase == 8, "BROAD_PHASE_CHANGED");
        Require(
            string.Equals(
                DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                RequiredCurrentCatalogVersion,
                StringComparison.Ordinal) &&
            string.Equals(
                DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
                RequiredCurrentCatalogSemanticSha256,
                StringComparison.Ordinal),
            "CURRENT_CATALOG_MISMATCH");

        var chainId = Required("StatRunCandidate:ChainId");
        Require(string.Equals(chainId, RequiredChainId, StringComparison.Ordinal), "CHAIN_MISMATCH");
        var expectedDatabase = Required("StatRunCandidate:ExpectedDatabase");
        var expectedDatabasePrefix = Required("StatRunCandidate:ExpectedDatabasePrefix");
        Require(
            string.Equals(expectedDatabasePrefix, "tdtd_p9_", StringComparison.Ordinal) &&
            expectedDatabase.StartsWith(expectedDatabasePrefix, StringComparison.Ordinal) &&
            IsSafeDatabaseSuffix(expectedDatabase[expectedDatabasePrefix.Length..]) &&
            string.Equals(expectedDatabase, _mongo.Database, StringComparison.Ordinal),
            "DATABASE_MISMATCH");
        ValidateMongoOwner();

        var workspaceRoot = ResolveWorkspaceRoot();
        ValidatePinnedLineage(workspaceRoot);
        var candidateDirectory = WorkspacePath(workspaceRoot, CandidateDirectoryRelativePath);
        ValidateCandidateDirectory(candidateDirectory);
        var catalogPath = ResolveRequiredExactPath(
            "StatRunCandidate:CatalogPath",
            Path.Combine(candidateDirectory, "catalog.json"));
        var schemaPath = ResolveRequiredExactPath(
            "StatRunCandidate:SchemaPath",
            Path.Combine(candidateDirectory, "schema.json"));
        var stageLockPath = ResolveRequiredExactPath(
            "StatRunCandidate:StageLockPath",
            Path.Combine(candidateDirectory, "stage-lock.json"));
        var configuredCatalogRaw = RequiredSha("StatRunCandidate:CatalogRawSha256");
        var configuredCatalogSemantic = RequiredSha("StatRunCandidate:CatalogSemanticSha256");
        var configuredSchemaRaw = RequiredSha("StatRunCandidate:SchemaRawSha256");
        var configuredSchemaSemantic = RequiredSha("StatRunCandidate:SchemaSemanticSha256");
        var configuredStageLockRaw = RequiredSha("StatRunCandidate:StageLockSha256");
        Require(
            configuredCatalogRaw == RequiredCandidateCatalogRawSha256 &&
            configuredCatalogSemantic == RequiredCandidateCatalogSemanticSha256 &&
            configuredSchemaRaw == RequiredCandidateSchemaRawSha256 &&
            configuredSchemaSemantic == RequiredCandidateSchemaSemanticSha256 &&
            configuredStageLockRaw == RequiredStageLockRawSha256,
            "CANDIDATE_PIN_MISMATCH");

        var catalogBytes = File.ReadAllBytes(catalogPath);
        var schemaBytes = File.ReadAllBytes(schemaPath);
        var stageLockBytes = File.ReadAllBytes(stageLockPath);
        Require(Hash(catalogBytes) == configuredCatalogRaw, "CATALOG_RAW_HASH_MISMATCH");
        Require(Hash(schemaBytes) == configuredSchemaRaw, "SCHEMA_RAW_HASH_MISMATCH");
        Require(Hash(stageLockBytes) == configuredStageLockRaw, "STAGE_LOCK_HASH_MISMATCH");

        using var catalog = JsonDocument.Parse(catalogBytes);
        using var schema = JsonDocument.Parse(schemaBytes);
        using var stageLock = JsonDocument.Parse(stageLockBytes);
        Require(SemanticHash(catalog.RootElement) == configuredCatalogSemantic,
            "CATALOG_SEMANTIC_HASH_MISMATCH");
        Require(SemanticHash(schema.RootElement) == configuredSchemaSemantic,
            "SCHEMA_SEMANTIC_HASH_MISMATCH");

        ValidateStageLock(
            stageLock.RootElement,
            configuredCatalogRaw,
            configuredCatalogSemantic,
            configuredSchemaRaw,
            configuredSchemaSemantic);
        ValidateCandidateCatalog(catalog.RootElement);
        ValidateCandidateSchema(schema.RootElement);

        return new StatRunCandidateBinding(
            RequiredChainId,
            RequiredPromptId,
            0,
            RequiredCatalogVersion,
            configuredCatalogRaw,
            configuredCatalogSemantic,
            configuredSchemaRaw,
            configuredSchemaSemantic,
            configuredStageLockRaw,
            expectedDatabase,
            Array.Empty<string>());
    }

    private StatRunCandidateBinding ValidateLifecycleCandidateBundle()
    {
        Require(
            _configuration.GetValue<bool?>("StatRunCandidate:Enabled") == true,
            "CANDIDATE_DISABLED");
        Require(_environment.IsEnvironment("Testing"), "ENVIRONMENT_MISMATCH");
        Require(StatConfigPhaseBarrier.CurrentPhase == 8, "BROAD_PHASE_CHANGED");
        Require(
            string.Equals(
                DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                RequiredCurrentCatalogVersion,
                StringComparison.Ordinal) &&
            string.Equals(
                DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
                RequiredCurrentCatalogSemanticSha256,
                StringComparison.Ordinal),
            "CURRENT_CATALOG_MISMATCH");

        var chainId = Required("StatRunCandidate:ChainId");
        Require(string.Equals(chainId, RequiredChainId, StringComparison.Ordinal), "CHAIN_MISMATCH");
        var expectedDatabase = Required("StatRunCandidate:ExpectedDatabase");
        var expectedDatabasePrefix = Required("StatRunCandidate:ExpectedDatabasePrefix");
        Require(
            string.Equals(expectedDatabasePrefix, "tdtd_p9_", StringComparison.Ordinal) &&
            expectedDatabase.StartsWith(expectedDatabasePrefix, StringComparison.Ordinal) &&
            IsSafeDatabaseSuffix(expectedDatabase[expectedDatabasePrefix.Length..]) &&
            string.Equals(expectedDatabase, _mongo.Database, StringComparison.Ordinal),
            "DATABASE_MISMATCH");
        ValidateMongoOwner();

        var workspaceRoot = ResolveWorkspaceRoot();
        ValidatePinnedLineage(workspaceRoot);
        RequirePinnedFile(
            WorkspacePath(workspaceRoot, LifecycleParentStageLockRelativePath),
            RequiredStageLockRawSha256,
            "PARENT_STAGE_LOCK_RAW_HASH_MISMATCH");
        RequirePinnedFile(
            WorkspacePath(workspaceRoot, LifecycleGeneratorRelativePath),
            LifecycleRequiredGeneratorRawSha256,
            "GENERATOR_RAW_HASH_MISMATCH");

        var candidateDirectory = WorkspacePath(
            workspaceRoot,
            LifecycleCandidateDirectoryRelativePath);
        ValidateCandidateDirectory(candidateDirectory);
        var catalogPath = ResolveRequiredExactPath(
            "StatRunCandidate:CatalogPath",
            Path.Combine(candidateDirectory, "catalog.json"));
        var schemaPath = ResolveRequiredExactPath(
            "StatRunCandidate:SchemaPath",
            Path.Combine(candidateDirectory, "schema.json"));
        var stageLockPath = ResolveRequiredExactPath(
            "StatRunCandidate:StageLockPath",
            Path.Combine(candidateDirectory, "stage-lock.json"));
        var configuredCatalogRaw = RequiredSha("StatRunCandidate:CatalogRawSha256");
        var configuredCatalogSemantic = RequiredSha("StatRunCandidate:CatalogSemanticSha256");
        var configuredSchemaRaw = RequiredSha("StatRunCandidate:SchemaRawSha256");
        var configuredSchemaSemantic = RequiredSha("StatRunCandidate:SchemaSemanticSha256");
        var configuredStageLockRaw = RequiredSha("StatRunCandidate:StageLockSha256");
        Require(
            configuredCatalogRaw == RequiredCandidateCatalogRawSha256 &&
            configuredCatalogSemantic == RequiredCandidateCatalogSemanticSha256 &&
            configuredSchemaRaw == RequiredCandidateSchemaRawSha256 &&
            configuredSchemaSemantic == RequiredCandidateSchemaSemanticSha256 &&
            configuredStageLockRaw == LifecycleRequiredStageLockRawSha256,
            "CANDIDATE_PIN_MISMATCH");

        var catalogBytes = File.ReadAllBytes(catalogPath);
        var schemaBytes = File.ReadAllBytes(schemaPath);
        var stageLockBytes = File.ReadAllBytes(stageLockPath);
        Require(Hash(catalogBytes) == configuredCatalogRaw, "CATALOG_RAW_HASH_MISMATCH");
        Require(Hash(schemaBytes) == configuredSchemaRaw, "SCHEMA_RAW_HASH_MISMATCH");
        Require(Hash(stageLockBytes) == configuredStageLockRaw, "STAGE_LOCK_HASH_MISMATCH");

        using var catalog = JsonDocument.Parse(catalogBytes);
        using var schema = JsonDocument.Parse(schemaBytes);
        using var stageLock = JsonDocument.Parse(stageLockBytes);
        Require(SemanticHash(catalog.RootElement) == configuredCatalogSemantic,
            "CATALOG_SEMANTIC_HASH_MISMATCH");
        Require(SemanticHash(schema.RootElement) == configuredSchemaSemantic,
            "SCHEMA_SEMANTIC_HASH_MISMATCH");

        ValidateLifecycleStageLock(
            stageLock.RootElement,
            configuredCatalogRaw,
            configuredCatalogSemantic,
            configuredSchemaRaw,
            configuredSchemaSemantic);
        ValidateCandidateCatalog(catalog.RootElement);
        ValidateCandidateSchema(schema.RootElement);

        return new StatRunCandidateBinding(
            RequiredChainId,
            LifecycleRequiredPromptId,
            0,
            RequiredCatalogVersion,
            configuredCatalogRaw,
            configuredCatalogSemantic,
            configuredSchemaRaw,
            configuredSchemaSemantic,
            configuredStageLockRaw,
            expectedDatabase,
            Array.Empty<string>());
    }

    private StatRunCandidateBinding ValidateFeatureCandidateBundle(
        string requestedCapabilityId,
        int minimumStage)
    {
        Require(
            _configuration.GetValue<bool?>("StatRunCandidate:Enabled") == true,
            "CANDIDATE_DISABLED");
        Require(_environment.IsEnvironment("Testing"), "ENVIRONMENT_MISMATCH");
        Require(StatConfigPhaseBarrier.CurrentPhase == 8, "BROAD_PHASE_CHANGED");
        Require(
            string.Equals(
                DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                RequiredCurrentCatalogVersion,
                StringComparison.Ordinal) &&
            string.Equals(
                DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
                RequiredCurrentCatalogSemanticSha256,
                StringComparison.Ordinal),
            "CURRENT_CATALOG_MISMATCH");

        var chainId = Required("StatRunCandidate:ChainId");
        Require(string.Equals(chainId, RequiredChainId, StringComparison.Ordinal), "CHAIN_MISMATCH");
        var expectedDatabase = Required("StatRunCandidate:ExpectedDatabase");
        var expectedDatabasePrefix = Required("StatRunCandidate:ExpectedDatabasePrefix");
        Require(
            string.Equals(expectedDatabasePrefix, "tdtd_p9_", StringComparison.Ordinal) &&
            expectedDatabase.StartsWith(expectedDatabasePrefix, StringComparison.Ordinal) &&
            IsSafeDatabaseSuffix(expectedDatabase[expectedDatabasePrefix.Length..]) &&
            string.Equals(expectedDatabase, _mongo.Database, StringComparison.Ordinal),
            "DATABASE_MISMATCH");
        ValidateMongoOwner();

        var workspaceRoot = ResolveWorkspaceRoot();
        ValidatePinnedLineage(workspaceRoot);
        var stageLockPath = Path.GetFullPath(Required("StatRunCandidate:StageLockPath"));
        RequirePinnedFile(stageLockPath, null, "CANDIDATE_FILE_MISSING");
        var candidateDirectory = Path.GetDirectoryName(stageLockPath);
        Require(!string.IsNullOrWhiteSpace(candidateDirectory), "CANDIDATE_PATH_MISMATCH");
        var promptId = Path.GetFileName(candidateDirectory);
        var expectedStage = promptId switch
        {
            "P9-03" => 1,
            "P9-04" => 2,
            "P9-05" => 3,
            "P9-06" => 4,
            "P9-07" => 5,
            "P9-08" => 6,
            "P9-09" => 7,
            "P9-10" => 8,
            "P9-11" => 9,
            _ => -1
        };
        Require(expectedStage >= 1, "STAGE_LOCK_PROMPT_INVALID");
        var expectedCandidateDirectory = WorkspacePath(
            workspaceRoot,
            $".p9-artifacts/catalog-candidate/{RequiredChainId}/{promptId}");
        Require(
            string.Equals(
                Path.GetFullPath(candidateDirectory!),
                expectedCandidateDirectory,
                StringComparison.OrdinalIgnoreCase),
            "CANDIDATE_PATH_MISMATCH");
        ValidateCandidateDirectory(expectedCandidateDirectory);

        var catalogPath = ResolveRequiredExactPath(
            "StatRunCandidate:CatalogPath",
            Path.Combine(expectedCandidateDirectory, "catalog.json"));
        var schemaPath = ResolveRequiredExactPath(
            "StatRunCandidate:SchemaPath",
            Path.Combine(expectedCandidateDirectory, "schema.json"));
        stageLockPath = ResolveRequiredExactPath(
            "StatRunCandidate:StageLockPath",
            Path.Combine(expectedCandidateDirectory, "stage-lock.json"));
        var configuredCatalogRaw = RequiredSha("StatRunCandidate:CatalogRawSha256");
        var configuredCatalogSemantic = RequiredSha("StatRunCandidate:CatalogSemanticSha256");
        var configuredSchemaRaw = RequiredSha("StatRunCandidate:SchemaRawSha256");
        var configuredSchemaSemantic = RequiredSha("StatRunCandidate:SchemaSemanticSha256");
        var configuredStageLockRaw = RequiredSha("StatRunCandidate:StageLockSha256");

        var catalogBytes = File.ReadAllBytes(catalogPath);
        var schemaBytes = File.ReadAllBytes(schemaPath);
        var stageLockBytes = File.ReadAllBytes(stageLockPath);
        Require(Hash(catalogBytes) == configuredCatalogRaw, "CATALOG_RAW_HASH_MISMATCH");
        Require(Hash(schemaBytes) == configuredSchemaRaw, "SCHEMA_RAW_HASH_MISMATCH");
        Require(Hash(stageLockBytes) == configuredStageLockRaw, "STAGE_LOCK_HASH_MISMATCH");

        using var catalog = JsonDocument.Parse(catalogBytes);
        using var schema = JsonDocument.Parse(schemaBytes);
        using var stageLock = JsonDocument.Parse(stageLockBytes);
        Require(
            SemanticHash(catalog.RootElement) == configuredCatalogSemantic,
            "CATALOG_SEMANTIC_HASH_MISMATCH");
        Require(
            SemanticHash(schema.RootElement) == configuredSchemaSemantic,
            "SCHEMA_SEMANTIC_HASH_MISMATCH");
        ValidateCandidateSchema(schema.RootElement);
        ValidateFeatureStageLock(
            workspaceRoot,
            stageLock.RootElement,
            promptId,
            expectedStage,
            configuredCatalogRaw,
            configuredCatalogSemantic,
            configuredSchemaRaw,
            configuredSchemaSemantic);
        ValidateFeatureCatalog(
            catalog.RootElement,
            expectedStage,
            requestedCapabilityId,
            minimumStage);
        Require(expectedStage >= minimumStage, "CANDIDATE_STAGE_TOO_LOW");

        var promotions = stageLock.RootElement
            .GetProperty("promotions")
            .EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
        return new StatRunCandidateBinding(
            RequiredChainId,
            promptId,
            expectedStage,
            RequiredCatalogVersion,
            configuredCatalogRaw,
            configuredCatalogSemantic,
            configuredSchemaRaw,
            configuredSchemaSemantic,
            configuredStageLockRaw,
            expectedDatabase,
            promotions);
    }

    private static void ValidateFeatureStageLock(
        string workspaceRoot,
        JsonElement root,
        string promptId,
        int stage,
        string catalogRaw,
        string catalogSemantic,
        string schemaRaw,
        string schemaSemantic)
    {
        Require(ReadString(root, "schemaVersion") == "P9_CANDIDATE_STAGE_V1", "STAGE_LOCK_SCHEMA_INVALID");
        Require(ReadString(root, "packId") == "FULL-P9-STAT-RUN", "STAGE_LOCK_PACK_INVALID");
        Require(ReadString(root, "chainId") == RequiredChainId, "STAGE_LOCK_CHAIN_INVALID");
        Require(ReadString(root, "promptId") == promptId, "STAGE_LOCK_PROMPT_INVALID");
        Require(ReadInt(root, "stage") == stage, "STAGE_LOCK_STAGE_INVALID");
        Require(ReadString(root, "catalogVersion") == RequiredCatalogVersion, "STAGE_LOCK_VERSION_INVALID");

        var candidateRoot = $".p9-artifacts/catalog-candidate/{RequiredChainId}/{promptId}";
        var catalogPin = RequiredObject(root, "catalog");
        Require(
            ReadString(catalogPin, "path") == $"{candidateRoot}/catalog.json" &&
            ReadString(catalogPin, "rawSha256") == catalogRaw &&
            ReadString(catalogPin, "semanticSha256") == catalogSemantic,
            "STAGE_CATALOG_PIN_INVALID");
        var schemaPin = RequiredObject(root, "schema");
        Require(
            ReadString(schemaPin, "path") == $"{candidateRoot}/schema.json" &&
            ReadString(schemaPin, "rawSha256") == schemaRaw &&
            ReadString(schemaPin, "semanticSha256") == schemaSemantic,
            "STAGE_SCHEMA_PIN_INVALID");

        var parentPromptId = $"P9-{stage + 1:00}";
        var parentPath =
            $".p9-artifacts/catalog-candidate/{RequiredChainId}/{parentPromptId}/stage-lock.json";
        var parentPin = RequiredObject(root, "parentStageLock");
        Require(
            ReadString(parentPin, "path") == parentPath,
            "PARENT_STAGE_LOCK_PIN_INVALID");
        RequirePinnedFile(
            WorkspacePath(workspaceRoot, parentPath),
            ReadString(parentPin, "sha256"),
            "PARENT_STAGE_LOCK_PIN_INVALID");

        var generator = RequiredObject(root, "generator");
        Require(
            ReadString(generator, "path") == "scripts/generate-p9-candidate-stage.mjs",
            "GENERATOR_PIN_INVALID");
        RequirePinnedRepositoryFileOrHistoricalArchive(
            workspaceRoot,
            "scripts/generate-p9-candidate-stage.mjs",
            ReadString(generator, "sha256"),
            "GENERATOR_PIN_INVALID");

        var expectedPromotions = stage switch
        {
            1 => new[] { StatRunCapabilities.DirectFieldTableLabel },
            2 => new[] { StatRunCapabilities.BasicSummary, StatRunCapabilities.FlowScopes },
            3 => new[] { StatRunCapabilities.AdvancedSummary },
            4 => new[] { StatRunCapabilities.Diff },
            5 => Array.Empty<string>(),
            6 => Array.Empty<string>(),
            7 => Array.Empty<string>(),
            8 => Array.Empty<string>(),
            9 => Array.Empty<string>(),
            _ => throw new CandidateValidationException("STAGE_LOCK_STAGE_INVALID")
        };
        Require(
            root.TryGetProperty("promotions", out var promotions) &&
            promotions.ValueKind == JsonValueKind.Array &&
            promotions.EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .SequenceEqual(expectedPromotions, StringComparer.Ordinal),
            "STAGE_PROMOTIONS_INVALID");

        Require(
            root.TryGetProperty("capabilityStates", out var states) &&
            states.ValueKind == JsonValueKind.Array &&
            states.GetArrayLength() == StatRunCapabilities.All.Count,
            "STAGE_CAPABILITY_STATES_INVALID");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in states.EnumerateArray())
        {
            var id = ReadString(item, "id");
            Require(StatRunCapabilities.All.Contains(id) && seen.Add(id), "STAGE_CAPABILITY_ID_INVALID");
            Require(
                ReadString(item, "status") ==
                (FeatureCapabilitySupported(id, stage) ? "SUPPORTED" : "PATCH_REQUIRED") &&
                ReadString(item, "targetPhase") == "P9",
                "STAGE_CAPABILITY_STATE_INVALID");
        }
        var profile = RequiredObject(root, "profileBarrier");
        Require(
            ReadString(profile, "id") == "FLOW_STATISTIC_PROFILE" &&
            ReadString(profile, "status") == "INTENTIONAL_BLOCK" &&
            profile.TryGetProperty("targetPhase", out var targetPhase) &&
            targetPhase.ValueKind == JsonValueKind.Null,
            "PROFILE_BARRIER_INVALID");
        if (stage == 9)
        {
            var seal = RequiredObject(root, "seal");
            Require(
                ReadString(seal, "status") == "SEALED" &&
                ReadString(seal, "catalogRawSha256") == catalogRaw &&
                ReadString(seal, "catalogSemanticSha256") == catalogSemantic &&
                ReadString(seal, "schemaRawSha256") == schemaRaw &&
                ReadString(seal, "schemaSemanticSha256") == schemaSemantic &&
                ReadString(seal, "productionCurrentVersion") == "1.5" &&
                ReadString(seal, "publishOwnerPrompt") == "P9-12",
                "STAGE_SEAL_INVALID");
        }
    }

    private static void ValidateFeatureCatalog(
        JsonElement root,
        int stage,
        string requestedCapabilityId,
        int minimumStage)
    {
        Require(ReadString(root, "catalogVersion") == RequiredCatalogVersion, "CATALOG_VERSION_INVALID");
        Require(
            ReadString(root, "$schema") ==
            (stage == 9
                ? "./DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.schema.json"
                : "./schema.json"),
            "CATALOG_SCHEMA_POINTER_INVALID");
        var domains = RequiredObject(root, "domains");
        Require(
            domains.TryGetProperty("statisticsCapabilities", out var capabilities) &&
            capabilities.ValueKind == JsonValueKind.Array,
            "CATALOG_STATISTICS_CAPABILITIES_INVALID");
        var selected = capabilities.EnumerateArray()
            .Where(item => StatRunCapabilities.All.Contains(ReadString(item, "id")))
            .ToArray();
        Require(selected.Length == StatRunCapabilities.All.Count, "CATALOG_P9_CAPABILITY_COUNT_INVALID");
        foreach (var capability in selected)
        {
            var id = ReadString(capability, "id");
            Require(
                ReadString(capability, "status") ==
                (FeatureCapabilitySupported(id, stage) ? "SUPPORTED" : "PATCH_REQUIRED") &&
                ReadString(capability, "targetPhase") == "P9",
                "CATALOG_P9_CAPABILITY_STATE_INVALID");
        }
        if (minimumStage > 0)
        {
            Require(
                selected.Any(item =>
                    ReadString(item, "id") == requestedCapabilityId &&
                    ReadString(item, "status") == "SUPPORTED"),
                "CAPABILITY_NOT_PROMOTED");
        }
        var profile = capabilities.EnumerateArray().SingleOrDefault(item =>
            ReadString(item, "id") == "FLOW_STATISTIC_PROFILE");
        Require(
            profile.ValueKind == JsonValueKind.Object &&
            ReadString(profile, "status") == "INTENTIONAL_BLOCK" &&
            profile.TryGetProperty("targetPhase", out var targetPhase) &&
            targetPhase.ValueKind == JsonValueKind.Null,
            "CATALOG_PROFILE_BARRIER_INVALID");
    }

    private static bool FeatureCapabilitySupported(string capabilityId, int stage)
        => capabilityId switch
        {
            StatRunCapabilities.DirectFieldTableLabel => stage >= 1,
            StatRunCapabilities.BasicSummary or StatRunCapabilities.FlowScopes => stage >= 2,
            StatRunCapabilities.AdvancedSummary => stage >= 3,
            StatRunCapabilities.Diff => stage >= 4,
            _ => false
        };

    private static void ValidateStageLock(
        JsonElement root,
        string catalogRaw,
        string catalogSemantic,
        string schemaRaw,
        string schemaSemantic)
    {
        Require(ReadString(root, "schemaVersion") == "P9_CANDIDATE_STAGE_V1", "STAGE_LOCK_SCHEMA_INVALID");
        Require(ReadString(root, "packId") == "FULL-P9-STAT-RUN", "STAGE_LOCK_PACK_INVALID");
        Require(ReadString(root, "chainId") == RequiredChainId, "STAGE_LOCK_CHAIN_INVALID");
        Require(ReadString(root, "promptId") == RequiredPromptId, "STAGE_LOCK_PROMPT_INVALID");
        Require(ReadInt(root, "stage") == 0, "STAGE_LOCK_STAGE_INVALID");
        Require(ReadString(root, "catalogVersion") == RequiredCatalogVersion, "STAGE_LOCK_VERSION_INVALID");

        var parentCurrent = RequiredObject(root, "parentCurrent");
        Require(ReadString(parentCurrent, "path") == CurrentRelativePath &&
                ReadString(parentCurrent, "sha256") == RequiredCurrentRawSha256,
            "PARENT_CURRENT_PIN_INVALID");
        var parent = RequiredObject(root, "parentLock");
        Require(ReadString(parent, "path") == ParentLockRelativePath &&
                ReadString(parent, "sha256") == RequiredParentLockRawSha256,
            "PARENT_LOCK_PIN_INVALID");

        var sourceCatalog = RequiredObject(root, "sourceCatalog");
        Require(ReadString(sourceCatalog, "path") == SourceCatalogRelativePath &&
                ReadString(sourceCatalog, "rawSha256") == RequiredSourceCatalogRawSha256,
            "SOURCE_CATALOG_HASH_INVALID");
        Require(ReadString(sourceCatalog, "semanticSha256") == RequiredCurrentCatalogSemanticSha256,
            "SOURCE_CATALOG_SEMANTIC_HASH_INVALID");
        var sourceSchema = RequiredObject(root, "sourceSchema");
        Require(ReadString(sourceSchema, "path") == SourceSchemaRelativePath &&
                ReadString(sourceSchema, "rawSha256") == RequiredSourceSchemaRawSha256,
            "SOURCE_SCHEMA_HASH_INVALID");
        Require(ReadString(sourceSchema, "semanticSha256") == RequiredSourceSchemaSemanticSha256,
            "SOURCE_SCHEMA_SEMANTIC_HASH_INVALID");
        var generator = RequiredObject(root, "generator");
        Require(ReadString(generator, "path") == GeneratorRelativePath &&
                ReadString(generator, "sha256") == RequiredGeneratorRawSha256,
            "GENERATOR_PIN_INVALID");

        var lockCatalog = RequiredObject(root, "catalog");
        Require(
            ReadString(lockCatalog, "path") ==
            $"{CandidateDirectoryRelativePath}/catalog.json",
            "STAGE_CATALOG_PATH_INVALID");
        Require(ReadString(lockCatalog, "rawSha256") == catalogRaw, "STAGE_CATALOG_RAW_INVALID");
        Require(ReadString(lockCatalog, "semanticSha256") == catalogSemantic,
            "STAGE_CATALOG_SEMANTIC_INVALID");
        var lockSchema = RequiredObject(root, "schema");
        Require(
            ReadString(lockSchema, "path") ==
            $"{CandidateDirectoryRelativePath}/schema.json",
            "STAGE_SCHEMA_PATH_INVALID");
        Require(ReadString(lockSchema, "rawSha256") == schemaRaw, "STAGE_SCHEMA_RAW_INVALID");
        Require(ReadString(lockSchema, "semanticSha256") == schemaSemantic,
            "STAGE_SCHEMA_SEMANTIC_INVALID");

        Require(root.TryGetProperty("promotions", out var promotions) &&
                promotions.ValueKind == JsonValueKind.Array &&
                promotions.GetArrayLength() == 0,
            "STAGE_PROMOTIONS_INVALID");
        Require(root.TryGetProperty("capabilityStates", out var states) &&
                states.ValueKind == JsonValueKind.Array &&
                states.GetArrayLength() == StatRunCapabilities.All.Count,
            "STAGE_CAPABILITY_STATES_INVALID");
        var stateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in states.EnumerateArray())
        {
            var stateId = ReadString(state, "id");
            Require(StatRunCapabilities.All.Contains(stateId) && stateIds.Add(stateId),
                "STAGE_CAPABILITY_ID_INVALID");
            Require(ReadString(state, "status") == "PATCH_REQUIRED" &&
                    ReadString(state, "targetPhase") == "P9",
                "STAGE_CAPABILITY_STATE_INVALID");
        }
        var profile = RequiredObject(root, "profileBarrier");
        Require(ReadString(profile, "id") == "FLOW_STATISTIC_PROFILE" &&
                ReadString(profile, "status") == "INTENTIONAL_BLOCK" &&
                profile.TryGetProperty("targetPhase", out var targetPhase) &&
                targetPhase.ValueKind == JsonValueKind.Null,
            "PROFILE_BARRIER_INVALID");
    }

    private static void ValidateLifecycleStageLock(
        JsonElement root,
        string catalogRaw,
        string catalogSemantic,
        string schemaRaw,
        string schemaSemantic)
    {
        Require(ReadString(root, "schemaVersion") == "P9_CANDIDATE_STAGE_V1", "STAGE_LOCK_SCHEMA_INVALID");
        Require(ReadString(root, "packId") == "FULL-P9-STAT-RUN", "STAGE_LOCK_PACK_INVALID");
        Require(ReadString(root, "chainId") == RequiredChainId, "STAGE_LOCK_CHAIN_INVALID");
        Require(ReadString(root, "promptId") == LifecycleRequiredPromptId, "STAGE_LOCK_PROMPT_INVALID");
        Require(ReadInt(root, "stage") == 0, "STAGE_LOCK_STAGE_INVALID");
        Require(ReadString(root, "catalogVersion") == RequiredCatalogVersion, "STAGE_LOCK_VERSION_INVALID");

        var parentStage = RequiredObject(root, "parentStageLock");
        Require(
            ReadString(parentStage, "path") == LifecycleParentStageLockRelativePath &&
            ReadString(parentStage, "sha256") == RequiredStageLockRawSha256,
            "PARENT_STAGE_LOCK_PIN_INVALID");
        var parentCurrent = RequiredObject(root, "parentCurrent");
        Require(ReadString(parentCurrent, "path") == CurrentRelativePath &&
                ReadString(parentCurrent, "sha256") == RequiredCurrentRawSha256,
            "PARENT_CURRENT_PIN_INVALID");
        var parent = RequiredObject(root, "parentLock");
        Require(ReadString(parent, "path") == ParentLockRelativePath &&
                ReadString(parent, "sha256") == RequiredParentLockRawSha256,
            "PARENT_LOCK_PIN_INVALID");

        var sourceCatalog = RequiredObject(root, "sourceCatalog");
        Require(ReadString(sourceCatalog, "path") == SourceCatalogRelativePath &&
                ReadString(sourceCatalog, "rawSha256") == RequiredSourceCatalogRawSha256 &&
                ReadString(sourceCatalog, "semanticSha256") == RequiredCurrentCatalogSemanticSha256,
            "SOURCE_CATALOG_HASH_INVALID");
        var sourceSchema = RequiredObject(root, "sourceSchema");
        Require(ReadString(sourceSchema, "path") == SourceSchemaRelativePath &&
                ReadString(sourceSchema, "rawSha256") == RequiredSourceSchemaRawSha256 &&
                ReadString(sourceSchema, "semanticSha256") == RequiredSourceSchemaSemanticSha256,
            "SOURCE_SCHEMA_HASH_INVALID");
        var generator = RequiredObject(root, "generator");
        Require(ReadString(generator, "path") == LifecycleGeneratorRelativePath &&
                ReadString(generator, "sha256") == LifecycleRequiredGeneratorRawSha256,
            "GENERATOR_PIN_INVALID");

        var lockCatalog = RequiredObject(root, "catalog");
        Require(
            ReadString(lockCatalog, "path") ==
            $"{LifecycleCandidateDirectoryRelativePath}/catalog.json" &&
            ReadString(lockCatalog, "rawSha256") == catalogRaw &&
            ReadString(lockCatalog, "semanticSha256") == catalogSemantic,
            "STAGE_CATALOG_PIN_INVALID");
        var lockSchema = RequiredObject(root, "schema");
        Require(
            ReadString(lockSchema, "path") ==
            $"{LifecycleCandidateDirectoryRelativePath}/schema.json" &&
            ReadString(lockSchema, "rawSha256") == schemaRaw &&
            ReadString(lockSchema, "semanticSha256") == schemaSemantic,
            "STAGE_SCHEMA_PIN_INVALID");

        Require(root.TryGetProperty("promotions", out var promotions) &&
                promotions.ValueKind == JsonValueKind.Array &&
                promotions.GetArrayLength() == 0,
            "STAGE_PROMOTIONS_INVALID");
        Require(root.TryGetProperty("capabilityStates", out var states) &&
                states.ValueKind == JsonValueKind.Array &&
                states.GetArrayLength() == StatRunCapabilities.All.Count,
            "STAGE_CAPABILITY_STATES_INVALID");
        var stateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in states.EnumerateArray())
        {
            var stateId = ReadString(state, "id");
            Require(StatRunCapabilities.All.Contains(stateId) && stateIds.Add(stateId),
                "STAGE_CAPABILITY_ID_INVALID");
            Require(ReadString(state, "status") == "PATCH_REQUIRED" &&
                    ReadString(state, "targetPhase") == "P9",
                "STAGE_CAPABILITY_STATE_INVALID");
        }
        var profile = RequiredObject(root, "profileBarrier");
        Require(ReadString(profile, "id") == "FLOW_STATISTIC_PROFILE" &&
                ReadString(profile, "status") == "INTENTIONAL_BLOCK" &&
                profile.TryGetProperty("targetPhase", out var targetPhase) &&
                targetPhase.ValueKind == JsonValueKind.Null,
            "PROFILE_BARRIER_INVALID");
    }

    private static void ValidateCandidateCatalog(JsonElement root)
    {
        Require(ReadString(root, "catalogVersion") == RequiredCatalogVersion, "CATALOG_VERSION_INVALID");
        Require(ReadString(root, "$schema") == "./schema.json", "CATALOG_SCHEMA_POINTER_INVALID");
        var domains = RequiredObject(root, "domains");
        Require(domains.TryGetProperty("statisticsCapabilities", out var capabilities) &&
                capabilities.ValueKind == JsonValueKind.Array,
            "CATALOG_STATISTICS_CAPABILITIES_INVALID");
        var selected = capabilities.EnumerateArray()
            .Where(item => StatRunCapabilities.All.Contains(ReadString(item, "id")))
            .ToArray();
        Require(selected.Length == StatRunCapabilities.All.Count, "CATALOG_P9_CAPABILITY_COUNT_INVALID");
        foreach (var capability in selected)
        {
            Require(ReadString(capability, "status") == "PATCH_REQUIRED" &&
                    ReadString(capability, "targetPhase") == "P9",
                "CATALOG_P9_CAPABILITY_STATE_INVALID");
        }
        var profile = capabilities.EnumerateArray().SingleOrDefault(item =>
            ReadString(item, "id") == "FLOW_STATISTIC_PROFILE");
        Require(profile.ValueKind == JsonValueKind.Object &&
                ReadString(profile, "status") == "INTENTIONAL_BLOCK" &&
                profile.TryGetProperty("targetPhase", out var targetPhase) &&
                targetPhase.ValueKind == JsonValueKind.Null,
            "CATALOG_PROFILE_BARRIER_INVALID");
    }

    private static void ValidateCandidateSchema(JsonElement root)
    {
        Require(ReadString(root, "title") == "Dynamic Form Flow Capability Catalog v1.6",
            "SCHEMA_TITLE_INVALID");
        var properties = RequiredObject(root, "properties");
        var version = RequiredObject(properties, "catalogVersion");
        Require(ReadString(version, "const") == RequiredCatalogVersion, "SCHEMA_VERSION_INVALID");
    }

    private static string SemanticHash(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
            WriteCanonical(writer, root);
        return Hash(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new CandidateValidationException("JSON_TOKEN_INVALID");
        }
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        Require(parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object,
            $"{name.ToUpperInvariant()}_INVALID");
        return value;
    }

    private static string ReadString(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object &&
           parent.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int? ReadInt(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object &&
           parent.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var number)
            ? number
            : null;

    private static void RequirePathEquals(JsonElement parent, string name, string fullPath)
    {
        var configured = ReadString(parent, name).Replace((char)92, '/').TrimStart('/');
        Require(!string.IsNullOrWhiteSpace(configured), "STAGE_PATH_INVALID");
        var actual = Path.GetFullPath(fullPath).Replace((char)92, '/');
        Require(
            actual.Equals(configured, StringComparison.OrdinalIgnoreCase) ||
            actual.EndsWith($"/{configured}", StringComparison.OrdinalIgnoreCase),
            "STAGE_PATH_MISMATCH");
    }

    private static void RequirePathSuffix(JsonElement parent, string name, string suffix)
    {
        var value = ReadString(parent, name).Replace('\\', '/');
        Require(value.EndsWith(suffix, StringComparison.Ordinal), "PARENT_LOCK_PATH_INVALID");
    }

    private string Required(string key)
    {
        var value = _configuration[key]?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new CandidateValidationException("CANDIDATE_BINDING_MISSING");
        return value;
    }

    private string RequiredSha(string key)
    {
        var value = Required(key);
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new CandidateValidationException("CANDIDATE_HASH_INVALID");
        }
        return value;
    }

    private string ResolveRequiredExactPath(string key, string expectedPath)
    {
        var configuredPath = Path.GetFullPath(Required(key));
        expectedPath = Path.GetFullPath(expectedPath);
        Require(
            string.Equals(configuredPath, expectedPath, StringComparison.OrdinalIgnoreCase),
            "CANDIDATE_PATH_MISMATCH");
        RequirePinnedFile(expectedPath, null, "CANDIDATE_FILE_MISSING");
        return expectedPath;
    }

    private string ResolveWorkspaceRoot()
    {
        var contentRoot = Path.GetFullPath(_environment.ContentRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Require(
            string.Equals(Path.GetFileName(contentRoot), "tdtd-be", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(contentRoot, "tdtd-be.csproj")),
            "CONTENT_ROOT_MISMATCH");
        var workspaceRoot = Directory.GetParent(contentRoot)?.FullName;
        Require(!string.IsNullOrWhiteSpace(workspaceRoot), "WORKSPACE_ROOT_MISSING");
        return Path.GetFullPath(workspaceRoot!);
    }

    private static string WorkspacePath(string workspaceRoot, string relativePath)
        => Path.GetFullPath(Path.Combine(
            workspaceRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static void ValidateCandidateDirectory(string candidateDirectory)
    {
        RequireNoReparsePath(candidateDirectory);
        Require(Directory.Exists(candidateDirectory), "CANDIDATE_DIRECTORY_MISSING");
        var entries = Directory.EnumerateFileSystemEntries(candidateDirectory).ToArray();
        var expected = new HashSet<string>(
            ["catalog.json", "schema.json", "stage-lock.json"],
            StringComparer.Ordinal);
        Require(entries.Length == expected.Count, "CANDIDATE_DIRECTORY_DRIFT");
        foreach (var entry in entries)
        {
            Require(
                expected.Remove(Path.GetFileName(entry)) &&
                File.Exists(entry) &&
                (File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0,
                "CANDIDATE_DIRECTORY_DRIFT");
        }
        Require(expected.Count == 0, "CANDIDATE_DIRECTORY_DRIFT");
    }

    private static void ValidatePinnedLineage(string workspaceRoot)
    {
        RequirePinnedFile(
            WorkspacePath(workspaceRoot, CurrentRelativePath),
            RequiredCurrentRawSha256,
            "CURRENT_RAW_HASH_MISMATCH");
        RequirePinnedFile(
            WorkspacePath(workspaceRoot, ParentLockRelativePath),
            RequiredParentLockRawSha256,
            "PARENT_LOCK_RAW_HASH_MISMATCH");
        var sourceCatalogBytes = RequirePinnedFile(
            WorkspacePath(workspaceRoot, SourceCatalogRelativePath),
            RequiredSourceCatalogRawSha256,
            "SOURCE_CATALOG_RAW_HASH_MISMATCH");
        var sourceSchemaBytes = RequirePinnedFile(
            WorkspacePath(workspaceRoot, SourceSchemaRelativePath),
            RequiredSourceSchemaRawSha256,
            "SOURCE_SCHEMA_RAW_HASH_MISMATCH");
        RequirePinnedFile(
            WorkspacePath(workspaceRoot, GeneratorRelativePath),
            RequiredGeneratorRawSha256,
            "GENERATOR_RAW_HASH_MISMATCH");

        using var sourceCatalog = JsonDocument.Parse(sourceCatalogBytes);
        using var sourceSchema = JsonDocument.Parse(sourceSchemaBytes);
        Require(
            SemanticHash(sourceCatalog.RootElement) == RequiredCurrentCatalogSemanticSha256,
            "SOURCE_CATALOG_SEMANTIC_HASH_MISMATCH");
        Require(
            SemanticHash(sourceSchema.RootElement) == RequiredSourceSchemaSemanticSha256,
            "SOURCE_SCHEMA_SEMANTIC_HASH_MISMATCH");
    }

    private static byte[] RequirePinnedFile(
        string path,
        string? expectedRawSha256,
        string reason)
    {
        RequireNoReparsePath(path);
        Require(File.Exists(path), reason);
        var bytes = File.ReadAllBytes(path);
        if (expectedRawSha256 is not null)
            Require(Hash(bytes) == expectedRawSha256, reason);
        return bytes;
    }

    private static void RequirePinnedRepositoryFileOrHistoricalArchive(
        string workspaceRoot,
        string relativePath,
        string expectedRawSha256,
        string reason)
    {
        Require(
            expectedRawSha256.Length == 64 &&
            expectedRawSha256.All(character =>
                character is (>= '0' and <= '9') or (>= 'a' and <= 'f')),
            reason);

        var currentPath = WorkspacePath(workspaceRoot, relativePath);
        if (File.Exists(currentPath))
        {
            RequireNoReparsePath(currentPath);
            if (Hash(File.ReadAllBytes(currentPath)) == expectedRawSha256)
                return;
        }

        var archivePath = WorkspacePath(
            workspaceRoot,
            $".p9-artifacts/historical-sha256/{expectedRawSha256}.blob");
        RequirePinnedFile(archivePath, expectedRawSha256, reason);
    }

    private static void RequireNoReparsePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        Require(!string.IsNullOrWhiteSpace(root), "PINNED_PATH_ROOT_INVALID");
        var current = root!;
        var relative = Path.GetRelativePath(root!, fullPath);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            Require(
                File.Exists(current) || Directory.Exists(current),
                "PINNED_PATH_MISSING");
            Require(
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0,
                "PINNED_PATH_REPARSE_POINT");
        }
    }

    private void ValidateMongoOwner()
    {
        try
        {
            var builder = new MongoUrlBuilder(_mongo.ConnectionString);
            var servers = builder.Servers.ToArray();
            var suffix = _mongo.Database["tdtd_p9_".Length..];
            Require(
                servers.Length == 1 &&
                servers[0].Port > 0 &&
                servers[0].Host is "127.0.0.1" or "localhost" or "::1" &&
                string.Equals(builder.ReplicaSetName, $"p9rs_{suffix}", StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(builder.Username),
                "DATABASE_OWNER_MISMATCH");
        }
        catch (CandidateValidationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new CandidateValidationException("DATABASE_OWNER_MISMATCH");
        }
    }

    private static bool IsSafeDatabaseSuffix(string value)
        => value.Length is >= 3 and <= 48 &&
           value.All(character =>
               character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    private static string? P8RollbackBarrierEntry(string routeId)
        => routeId switch
        {
            StatRunRouteRegistry.AdvancedBuild => StatConfigPhaseBarrierEntries.P9Run,
            StatRunRouteRegistry.DirectFieldResult or
            StatRunRouteRegistry.DirectTableResult or
            StatRunRouteRegistry.DirectLabelResult or
            StatRunRouteRegistry.DirectTextResult or
            StatRunRouteRegistry.BasicResult or
            StatRunRouteRegistry.AdvancedResult => StatConfigPhaseBarrierEntries.P9Result,
            _ => null
        };

    private static StatRunCandidateEvaluation Disabled(
        string reason,
        string routeId,
        string capabilityId)
        => new(false, reason, routeId, capabilityId, null);

    private static string Normalize(string? value)
        => value?.Trim().ToUpperInvariant() ?? string.Empty;

    private static string Hash(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new CandidateValidationException(reason);
    }

    private sealed class CandidateValidationException : Exception
    {
        public CandidateValidationException(string reason) : base(reason)
            => Reason = reason;

        public string Reason { get; }
    }
}
