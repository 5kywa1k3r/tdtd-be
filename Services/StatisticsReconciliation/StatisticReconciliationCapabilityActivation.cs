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

namespace tdtd_be.Services.StatisticsReconciliation;

public static class StatisticReconciliationCapabilities
{
    public const string SourceToResultReconciliation =
        "SOURCE_TO_RESULT_RECONCILIATION";
    public const string ExpectedActualDelta = "EXPECTED_ACTUAL_DELTA";
    public const string IndependentReviewSignoff = "INDEPENDENT_REVIEW_SIGNOFF";
    public const string ReconciliationEvidenceExport =
        "RECONCILIATION_EVIDENCE_EXPORT";

    public static readonly IReadOnlySet<string> All = new[]
    {
        SourceToResultReconciliation,
        ExpectedActualDelta,
        IndependentReviewSignoff,
        ReconciliationEvidenceExport
    }.ToFrozenSet(StringComparer.Ordinal);
}

public static class StatisticReconciliationRouteRegistry
{
    public const string Create = "P10_RECONCILIATION_CREATE";
    public const string List = "P10_RECONCILIATION_LIST";
    public const string Read = "P10_RECONCILIATION_READ";
    public const string Cancel = "P10_RECONCILIATION_CANCEL";
    public const string WorkerClaim = "P10_RECONCILIATION_WORKER_CLAIM";
    public const string WorkerHeartbeat = "P10_RECONCILIATION_WORKER_HEARTBEAT";
    public const string WorkerRetry = "P10_RECONCILIATION_WORKER_RETRY";
    public const string WorkerPublish = "P10_RECONCILIATION_WORKER_PUBLISH";
    public const string WorkerFinalize = "P10_RECONCILIATION_WORKER_FINALIZE";
    public const string RecheckBegin = "P10_RECONCILIATION_RECHECK_BEGIN";
    public const string RecheckClaim = "P10_RECONCILIATION_RECHECK_CLAIM";
    public const string RecheckRemediationAuthorize =
        "P10_RECONCILIATION_RECHECK_REMEDIATION_AUTHORIZE";
    public const string RecheckFinalize = "P10_RECONCILIATION_RECHECK_FINALIZE";
    public const string WorkerCapture = "P10_RECONCILIATION_WORKER_CAPTURE";
    public const string ReviewSubmit = "P10_RECONCILIATION_REVIEW_SUBMIT";
    public const string ReviewRead = "P10_RECONCILIATION_REVIEW_READ";
    public const string ReviewSupersede = "P10_RECONCILIATION_REVIEW_SUPERSEDE";
    public const string EvidenceCreate = "P10_RECONCILIATION_EVIDENCE_CREATE";
    public const string EvidenceRead = "P10_RECONCILIATION_EVIDENCE_READ";

    private static readonly IReadOnlyDictionary<string, string> Routes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Create] = StatisticReconciliationCapabilities.SourceToResultReconciliation,
            [List] = StatisticReconciliationCapabilities.SourceToResultReconciliation,
            [Read] = StatisticReconciliationCapabilities.SourceToResultReconciliation,
            [Cancel] = StatisticReconciliationCapabilities.SourceToResultReconciliation,
            [WorkerClaim] = StatisticReconciliationCapabilities.SourceToResultReconciliation,
            [WorkerHeartbeat] = StatisticReconciliationCapabilities.SourceToResultReconciliation,
            [WorkerRetry] = StatisticReconciliationCapabilities.SourceToResultReconciliation,
            [WorkerPublish] = StatisticReconciliationCapabilities.SourceToResultReconciliation,
            [WorkerFinalize] = StatisticReconciliationCapabilities.ExpectedActualDelta,
            [RecheckBegin] = StatisticReconciliationCapabilities.SourceToResultReconciliation,
            [RecheckClaim] = StatisticReconciliationCapabilities.SourceToResultReconciliation,
            [RecheckRemediationAuthorize] =
                StatisticReconciliationCapabilities.SourceToResultReconciliation,
            [RecheckFinalize] = StatisticReconciliationCapabilities.ExpectedActualDelta,
            [WorkerCapture] = StatisticReconciliationCapabilities.SourceToResultReconciliation,
            [ReviewSubmit] = StatisticReconciliationCapabilities.IndependentReviewSignoff,
            [ReviewRead] = StatisticReconciliationCapabilities.IndependentReviewSignoff,
            [ReviewSupersede] = StatisticReconciliationCapabilities.IndependentReviewSignoff,
            [EvidenceCreate] = StatisticReconciliationCapabilities.ReconciliationEvidenceExport,
            [EvidenceRead] = StatisticReconciliationCapabilities.ReconciliationEvidenceExport
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static bool TryResolve(string? routeId, out string capabilityId)
        => Routes.TryGetValue(Normalize(routeId), out capabilityId!);

    public static IReadOnlyDictionary<string, string> Snapshot()
        => Routes.ToFrozenDictionary(StringComparer.Ordinal);

    private static string Normalize(string? value)
        => value?.Trim().ToUpperInvariant() ?? string.Empty;
}

public sealed record StatisticReconciliationCandidateBinding(
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
    IReadOnlyList<string> Promotions,
    string StageId,
    string EvidenceSha256,
    string GeneratorSha256);

public sealed record StatisticReconciliationCandidateEvaluation(
    bool Enabled,
    string? Reason,
    string RouteId,
    string CapabilityId,
    StatisticReconciliationCandidateBinding? Binding);

internal enum StatisticReconciliationCatalogPublicationState
{
    Unknown,
    PrePublish,
    RolledBack,
    Restored
}

public interface IStatisticReconciliationCandidateActivation
{
    StatisticReconciliationCandidateBinding RequireFoundation(
        string capabilityId,
        string routeId);

    StatisticReconciliationCandidateEvaluation EvaluateFoundation(
        string capabilityId,
        string routeId);

    StatisticReconciliationCandidateBinding RequireCapability(
        string capabilityId,
        string routeId);

    StatisticReconciliationCandidateEvaluation EvaluateCapability(
        string capabilityId,
        string routeId);
}

/// <summary>
/// P10 execution boundary. Before publication, a foundation route is available
/// only in an exact Testing process bound to the active P10 chain, immutable
/// stage-0 bundle and an owned isolated Mongo database. After P10-12, the exact
/// restored v1.7 CURRENT/LOCK pair activates only the immutable sealed P10-11
/// bundle. The exact CURRENT-only v1.6 rollback and every unknown mixed state
/// remain fail-closed.
/// </summary>
public sealed class StatisticReconciliationCapabilityActivation
    : IStatisticReconciliationCandidateActivation
{
    public const string RequiredChainId = "p10_chain_20260810002129_9f56";
    public const string RequiredPromptId = "P10-01";
    public const int RequiredStage = 0;
    public const string RequiredCatalogVersion = "1.7";
    public const string RequiredCurrentCatalogVersion = "1.6";
    public const string RequiredStageId =
        "p10_stage_002c7594778edc8e8b9d404b";
    public const string RequiredCatalogRawSha256 =
        "0d87a49cffd7b3ce24c48ff91836d11345ea51d03395ea86b8476edbb0a896da";
    public const string RequiredCatalogSemanticSha256 =
        "2ad26a8f328900356fb7ba3a35c13d08e82beffe335567ddff34db5b8bb60ac9";
    public const string RequiredSchemaRawSha256 =
        "16e796d421a8f3a6675afc32be96c6fea001ae7ed6b431dfe4655cabb98cc3b7";
    public const string RequiredSchemaSemanticSha256 =
        "5842baf176bf1eec453b718d07e50da55a417aa9036f4f097ccfbb6fc58c1978";
    public const string RequiredEvidenceRawSha256 =
        "294966494a347704c628f2aa6cdbe53e2cd369a110bec95bef46cd8773a1f71b";
    public const string RequiredStageLockRawSha256 =
        "e92fa88a24cab4d536e9f5c0a800de15cac8cae6356513b1a8f6bec3ba6aafed";
    public const string RequiredGeneratorRawSha256 =
        "046d535a4839b19e298e69b419fa05976a5aa1ffbcf21d28711a236f23cb87c8";

    public const string RequiredSourceCatalogRawSha256 =
        "a790be94e4598208de08af39f8782a267ce434db2c20242a811991429932233c";
    public const string RequiredSourceCatalogSemanticSha256 =
        "39cdb98dda168f5901f48a94640fe5d50943c5bd78ed32d5e05e8b719b23d13b";
    public const string RequiredSourceSchemaRawSha256 =
        "603304c9798805c972370494d3939bdc7da324939ba9ab98a82800241f1b6940";
    public const string RequiredSourceSchemaSemanticSha256 =
        "da0c80f265845f24aaf282e0a0369272273b1986520dae07171cda85b28b3fed";
    public const string RequiredCurrentRawSha256 =
        "b1ecff835b16316798a27cf9285956b91f2fc16d2c32c2cf27e11e4fb5b2ea26";
    public const string RequiredLockRawSha256 =
        "be02e58b97584e7638f591f7a8b37e6cb51bda8ca1ae100ebf1eb4ef9dc8bb6f";
    public const string RequiredManifestRawSha256 =
        "aebb7005acacbdc9fa0bedb056c285a4ad51ddee1e2ee998ab4f81d05d959ca2";
    public const string RequiredPreviousHandoffRawSha256 =
        "fbc6f82271128fd07c7b0cf8406f8ba2e735d719ade70bbf00c568bdedd2160e";
    public const string RequiredBaselineRawSha256 =
        "e5a0a1ebaa375a5e5bef28f319c3cec3e8fcf9c6f224362d3dfb21ed325ec2e5";

    public const string PublishedPromptId = "P10-12";
    public const string PublishedStagePromptId = "P10-11";
    public const int PublishedStage = 4;
    public const string PublishedStageId =
        "p10_stage_f18796f1ca034d89ab5a2b16";
    public const string PublishedCatalogRawSha256 =
        "072831d879352c76ca9e5af5f9cc2a20e13c632653fed466131f5f7a359204c4";
    public const string PublishedCatalogSemanticSha256 =
        "ccb28afafc068ac1b720c046a25276a35d9d828b14f9cc9c9bc690077ca204c1";
    public const string PublishedSchemaRawSha256 =
        "16e796d421a8f3a6675afc32be96c6fea001ae7ed6b431dfe4655cabb98cc3b7";
    public const string PublishedSchemaSemanticSha256 =
        "5842baf176bf1eec453b718d07e50da55a417aa9036f4f097ccfbb6fc58c1978";
    public const string PublishedEvidenceRawSha256 =
        "f982787e73fc36c9b3cf9fd5b908166fc0e8c3b3faea85dded614ac02c5a057e";
    public const string PublishedStageLockRawSha256 =
        "a7e80506b5f8838c02e892cbb7b206cc6d0e84241cff240bd88a7c268f316e2d";
    public const string PublishedGeneratorRawSha256 =
        "db0e2562272553e9fc866af31eba7954b0c69f0aa6eb4af5e2595b088b59ca76";
    public const string PublishedCurrentRawSha256 =
        "9899ca9a7495e9d903f520d185dee3098b45ad17a7a3da05caa96b8d31a392d9";
    public const string PublishedLockRawSha256 =
        "cec0b6893a109a697e2f7fa31017c1165c47039b15db85247a8eb9729a044ae0";

    private const string ConfigurationPrefix =
        "StatisticReconciliationCandidate";
    private const string CurrentRelativePath =
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json";
    private const string LockRelativePath =
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json";
    private const string SourceCatalogRelativePath =
        "docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.json";
    private const string SourceSchemaRelativePath =
        "docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.schema.json";
    private const string ManifestRelativePath =
        "docs/features/p10-reconcile/FULL_P10_RECONCILE_PROMPT_MANIFEST.json";
    private const string PreviousHandoffRelativePath =
        ".p10-artifacts/handoffs/p10_chain_20260810002129_9f56/P10-00.attempt-001.json";
    private const string BaselineRelativePath =
        ".p10-artifacts/baseline/p10_chain_20260810002129_9f56/P10-00.baseline.json";
    private const string GeneratorRelativePath =
        "scripts/generate-p10-stage0-candidate.mjs";
    private const string CandidateDirectoryRelativePath =
        ".p10-artifacts/catalog-candidate/p10_chain_20260810002129_9f56/P10-01";
    private const string PublishedCatalogRelativePath =
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_7.json";
    private const string PublishedSchemaRelativePath =
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_7.schema.json";
    private const string PublishedCandidateDirectoryRelativePath =
        ".p10-artifacts/catalog-candidate/p10_chain_20260810002129_9f56/P10-11";
    private const string PublishedGeneratorRelativePath =
        "scripts/generate-p10-11-candidate.mjs";
    private const string PublishedParentStageRelativePath =
        ".p10-artifacts/catalog-candidate/p10_chain_20260810002129_9f56/P10-09/stage-lock.json";
    private const string PublishedParentStageId =
        "p10_stage_73de0c7e57a94dee52954288";
    private const string PublishedParentStageRawSha256 =
        "39246720948d682e85e1ca5aee1204b6da115ef974f8ad57c245d81d50bbe2b1";

    private static readonly string[] ExpectedCapabilityIds =
    [
        StatisticReconciliationCapabilities.SourceToResultReconciliation,
        StatisticReconciliationCapabilities.ExpectedActualDelta,
        StatisticReconciliationCapabilities.IndependentReviewSignoff,
        StatisticReconciliationCapabilities.ReconciliationEvidenceExport
    ];

    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly MongoOptions _mongo;

    public StatisticReconciliationCapabilityActivation(
        IConfiguration configuration,
        IHostEnvironment environment,
        IOptions<MongoOptions> mongo)
    {
        _configuration = configuration;
        _environment = environment;
        _mongo = mongo.Value;
    }

    public StatisticReconciliationCandidateBinding RequireFoundation(
        string capabilityId,
        string routeId)
        => Require(EvaluateFoundation(capabilityId, routeId));

    public StatisticReconciliationCandidateEvaluation EvaluateFoundation(
        string capabilityId,
        string routeId)
        => Evaluate(capabilityId, routeId, requirePromotion: false);

    public StatisticReconciliationCandidateBinding RequireCapability(
        string capabilityId,
        string routeId)
        => Require(EvaluateCapability(capabilityId, routeId));

    public StatisticReconciliationCandidateEvaluation EvaluateCapability(
        string capabilityId,
        string routeId)
        => Evaluate(capabilityId, routeId, requirePromotion: true);

    private StatisticReconciliationCandidateEvaluation Evaluate(
        string capabilityId,
        string routeId,
        bool requirePromotion)
    {
        capabilityId = Normalize(capabilityId);
        routeId = Normalize(routeId);
        if (!StatisticReconciliationCapabilities.All.Contains(capabilityId))
            return Disabled("CAPABILITY_CONFLICT", routeId, capabilityId);
        if (!StatisticReconciliationRouteRegistry.TryResolve(
                routeId,
                out var mappedCapability) ||
            !string.Equals(mappedCapability, capabilityId, StringComparison.Ordinal))
        {
            return Disabled("ROUTE_NOT_PROVEN", routeId, capabilityId);
        }

        if (ShouldUsePrePublishDisabledCompatibilityFallback(
                _environment.IsEnvironment("Testing"),
                HasValidWorkspaceCatalogRoot(_environment.ContentRootPath),
                _configuration.GetValue<bool?>(Key("Enabled")),
                DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
                DynamicFormFlowCapabilityCatalogMetadata.SchemaSha256))
        {
            return Disabled("CANDIDATE_DISABLED", routeId, capabilityId);
        }
        try
        {
            var binding = ValidateActivationBundle();
            if (requirePromotion && !binding.Promotions.Contains(
                    capabilityId,
                    StringComparer.Ordinal))
            {
                return Disabled(
                    "CAPABILITY_NOT_PROMOTED",
                    routeId,
                    capabilityId);
            }
            return new StatisticReconciliationCandidateEvaluation(
                true,
                null,
                routeId,
                capabilityId,
                binding);
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

    private StatisticReconciliationCandidateBinding ValidateActivationBundle()
    {
        var workspaceRoot = ResolveWorkspaceRoot();
        var currentBytes = RequirePinnedFile(
            WorkspacePath(workspaceRoot, CurrentRelativePath),
            null,
            "CURRENT_CATALOG_MISSING");
        var lockBytes = RequirePinnedFile(
            WorkspacePath(workspaceRoot, LockRelativePath),
            null,
            "CATALOG_LOCK_MISSING");
        var publicationState = ClassifyCatalogPublicationState(
            Hash(currentBytes),
            Hash(lockBytes));
        return publicationState switch
        {
            StatisticReconciliationCatalogPublicationState.PrePublish =>
                ValidateCandidateBundle(),
            StatisticReconciliationCatalogPublicationState.Restored =>
                ValidatePublishedBundle(workspaceRoot, currentBytes, lockBytes),
            StatisticReconciliationCatalogPublicationState.RolledBack =>
                throw new CandidateValidationException("CATALOG_STATE_ROLLED_BACK"),
            _ => throw new CandidateValidationException("CATALOG_STATE_INVALID")
        };
    }

    internal static StatisticReconciliationCatalogPublicationState
        ClassifyCatalogPublicationState(
            string? currentRawSha256,
            string? lockRawSha256)
    {
        if (string.Equals(currentRawSha256, RequiredCurrentRawSha256,
                StringComparison.Ordinal) &&
            string.Equals(lockRawSha256, RequiredLockRawSha256,
                StringComparison.Ordinal))
        {
            return StatisticReconciliationCatalogPublicationState.PrePublish;
        }
        if (string.Equals(currentRawSha256, RequiredCurrentRawSha256,
                StringComparison.Ordinal) &&
            string.Equals(lockRawSha256, PublishedLockRawSha256,
                StringComparison.Ordinal))
        {
            return StatisticReconciliationCatalogPublicationState.RolledBack;
        }
        if (string.Equals(currentRawSha256, PublishedCurrentRawSha256,
                StringComparison.Ordinal) &&
            string.Equals(lockRawSha256, PublishedLockRawSha256,
                StringComparison.Ordinal))
        {
            return StatisticReconciliationCatalogPublicationState.Restored;
        }
        return StatisticReconciliationCatalogPublicationState.Unknown;
    }

    internal static bool IsExactPrePublishGeneratedCatalogMetadata(
        string? version,
        string? catalogSemanticSha256,
        string? schemaSemanticSha256)
        => string.Equals(version, RequiredCurrentCatalogVersion,
                StringComparison.Ordinal) &&
            string.Equals(catalogSemanticSha256,
                RequiredSourceCatalogSemanticSha256,
                StringComparison.Ordinal) &&
            string.Equals(schemaSemanticSha256,
                RequiredSourceSchemaSemanticSha256,
                StringComparison.Ordinal);

    internal static bool IsKnownGeneratedCatalogMetadata(
        string? version,
        string? catalogSemanticSha256,
        string? schemaSemanticSha256)
        => IsExactPrePublishGeneratedCatalogMetadata(
               version,
               catalogSemanticSha256,
               schemaSemanticSha256) ||
           (string.Equals(version, RequiredCatalogVersion,
                StringComparison.Ordinal) &&
            string.Equals(catalogSemanticSha256,
                PublishedCatalogSemanticSha256,
                StringComparison.Ordinal) &&
            string.Equals(schemaSemanticSha256,
                PublishedSchemaSemanticSha256,
                StringComparison.Ordinal));

    internal static bool ShouldUsePrePublishDisabledCompatibilityFallback(
        bool isTestingEnvironment,
        bool hasValidWorkspaceCatalogRoot,
        bool? candidateEnabled,
        string? version,
        string? catalogSemanticSha256,
        string? schemaSemanticSha256)
        => !isTestingEnvironment &&
           !hasValidWorkspaceCatalogRoot &&
           candidateEnabled != true &&
           IsExactPrePublishGeneratedCatalogMetadata(
               version,
               catalogSemanticSha256,
               schemaSemanticSha256);

    internal static bool HasValidWorkspaceCatalogRoot(string? contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(contentRootPath)) return false;
        var contentRoot = contentRootPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return string.Equals(
                   Path.GetFileName(contentRoot),
                   "tdtd-be",
                   StringComparison.OrdinalIgnoreCase) &&
               File.Exists(Path.Combine(contentRoot, "tdtd-be.csproj")) &&
               File.Exists(Path.Combine(
                   contentRoot,
                   "Contracts",
                   "DynamicFormFlow",
                   "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json")) &&
               File.Exists(Path.Combine(
                   contentRoot,
                   "Contracts",
                   "DynamicFormFlow",
                   "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json"));
    }

    private StatisticReconciliationCandidateBinding ValidatePublishedBundle(
        string workspaceRoot,
        byte[] currentBytes,
        byte[] lockBytes)
    {
        Require(StatConfigPhaseBarrier.CurrentPhase == 8, "BROAD_PHASE_CHANGED");
        Require(
            string.Equals(
                DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                RequiredCatalogVersion,
                StringComparison.Ordinal) &&
            string.Equals(
                DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
                PublishedCatalogSemanticSha256,
                StringComparison.Ordinal) &&
            string.Equals(
                DynamicFormFlowCapabilityCatalogMetadata.SchemaSha256,
                PublishedSchemaSemanticSha256,
                StringComparison.Ordinal),
            "CURRENT_CATALOG_MISMATCH");

        var catalogBytes = RequirePinnedFile(
            WorkspacePath(workspaceRoot, PublishedCatalogRelativePath),
            PublishedCatalogRawSha256,
            "PUBLISHED_CATALOG_MISMATCH");
        var schemaBytes = RequirePinnedFile(
            WorkspacePath(workspaceRoot, PublishedSchemaRelativePath),
            PublishedSchemaRawSha256,
            "PUBLISHED_SCHEMA_MISMATCH");
        var candidateDirectory = WorkspacePath(
            workspaceRoot,
            PublishedCandidateDirectoryRelativePath);
        ValidateCandidateDirectory(candidateDirectory);
        var candidateCatalogBytes = RequirePinnedFile(
            Path.Combine(candidateDirectory, "catalog.json"),
            PublishedCatalogRawSha256,
            "SEALED_CATALOG_MISMATCH");
        var candidateSchemaBytes = RequirePinnedFile(
            Path.Combine(candidateDirectory, "schema.json"),
            PublishedSchemaRawSha256,
            "SEALED_SCHEMA_MISMATCH");
        var evidenceBytes = RequirePinnedFile(
            Path.Combine(candidateDirectory, "candidate-evidence.json"),
            PublishedEvidenceRawSha256,
            "SEALED_EVIDENCE_MISMATCH");
        var stageBytes = RequirePinnedFile(
            Path.Combine(candidateDirectory, "stage-lock.json"),
            PublishedStageLockRawSha256,
            "SEALED_STAGE_MISMATCH");
        RequirePinnedFile(
            WorkspacePath(workspaceRoot, PublishedGeneratorRelativePath),
            PublishedGeneratorRawSha256,
            "SEALED_GENERATOR_MISMATCH");
        RequirePinnedFile(
            WorkspacePath(workspaceRoot, PublishedParentStageRelativePath),
            PublishedParentStageRawSha256,
            "SEALED_PARENT_STAGE_MISMATCH");
        Require(
            catalogBytes.AsSpan().SequenceEqual(candidateCatalogBytes) &&
            schemaBytes.AsSpan().SequenceEqual(candidateSchemaBytes),
            "PUBLISHED_SEAL_BYTE_MISMATCH");

        using var current = JsonDocument.Parse(currentBytes);
        using var catalogLock = JsonDocument.Parse(lockBytes);
        using var catalog = JsonDocument.Parse(catalogBytes);
        using var schema = JsonDocument.Parse(schemaBytes);
        using var evidence = JsonDocument.Parse(evidenceBytes);
        using var stage = JsonDocument.Parse(stageBytes);
        RequireExactProperties(
            current.RootElement,
            ["pointerVersion", "catalogVersion", "catalogSha256", "schemaSha256"],
            "CURRENT_SHAPE_INVALID");
        Require(
            current.RootElement.GetProperty("pointerVersion").GetInt32() == 1 &&
            ReadString(current.RootElement, "catalogVersion") == RequiredCatalogVersion &&
            ReadString(current.RootElement, "catalogSha256") ==
                PublishedCatalogSemanticSha256 &&
            ReadString(current.RootElement, "schemaSha256") ==
                PublishedSchemaSemanticSha256,
            "CURRENT_CATALOG_MISMATCH");
        Require(
            SemanticHash(catalog.RootElement) == PublishedCatalogSemanticSha256,
            "PUBLISHED_CATALOG_MISMATCH");
        Require(
            SemanticHash(schema.RootElement) == PublishedSchemaSemanticSha256,
            "PUBLISHED_SCHEMA_MISMATCH");
        ValidatePublishedCatalog(catalog.RootElement);
        ValidateCandidateSchema(schema.RootElement);
        ValidatePublishedLock(catalogLock.RootElement);
        ValidatePublishedStage(stage.RootElement);
        ValidatePublishedEvidence(evidence.RootElement);

        return new StatisticReconciliationCandidateBinding(
            RequiredChainId,
            PublishedPromptId,
            PublishedStage,
            RequiredCatalogVersion,
            PublishedCatalogRawSha256,
            PublishedCatalogSemanticSha256,
            PublishedSchemaRawSha256,
            PublishedSchemaSemanticSha256,
            PublishedStageLockRawSha256,
            _mongo.Database,
            Array.AsReadOnly(ExpectedCapabilityIds.ToArray()),
            PublishedStageId,
            PublishedEvidenceRawSha256,
            PublishedGeneratorRawSha256);
    }

    private StatisticReconciliationCandidateBinding Require(
        StatisticReconciliationCandidateEvaluation evaluation)
    {
        if (evaluation.Enabled && evaluation.Binding is not null)
            return evaluation.Binding;

        if (string.Equals(
                evaluation.Reason,
                "CANDIDATE_DISABLED",
                StringComparison.Ordinal) ||
            string.Equals(
                evaluation.Reason,
                "CATALOG_STATE_ROLLED_BACK",
                StringComparison.Ordinal))
        {
            StatConfigPhaseBarrier.Reject(
                StatConfigPhaseBarrierEntries.P10Reconcile,
                evaluation.RouteId);
        }

        var error = evaluation.Reason switch
        {
            "ROUTE_NOT_PROVEN" =>
                AppErrorCode.STAT_RECONCILIATION_ROUTE_NOT_PROVEN,
            "CAPABILITY_CONFLICT" or "CAPABILITY_NOT_PROMOTED" =>
                AppErrorCode.STAT_RECONCILIATION_CAPABILITY_CONFLICT,
            _ => AppErrorCode.STAT_RECONCILIATION_CANDIDATE_INVALID
        };
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

    private StatisticReconciliationCandidateBinding ValidateCandidateBundle()
    {
        Require(
            _configuration.GetValue<bool?>(Key("Enabled")) == true,
            "CANDIDATE_DISABLED");
        Require(_environment.IsEnvironment("Testing"), "ENVIRONMENT_MISMATCH");
        Require(StatConfigPhaseBarrier.CurrentPhase == 8, "BROAD_PHASE_CHANGED");
        Require(
            IsExactPrePublishGeneratedCatalogMetadata(
                DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
                DynamicFormFlowCapabilityCatalogMetadata.SchemaSha256),
            "CURRENT_CATALOG_MISMATCH");

        var chainId = Required(Key("ChainId"));
        Require(
            string.Equals(chainId, RequiredChainId, StringComparison.Ordinal),
            "CHAIN_MISMATCH");
        var expectedDatabase = Required(Key("ExpectedDatabase"));
        var expectedDatabasePrefix = Required(Key("ExpectedDatabasePrefix"));
        Require(
            string.Equals(expectedDatabasePrefix, "tdtd_p10_", StringComparison.Ordinal) &&
            expectedDatabase.StartsWith(expectedDatabasePrefix, StringComparison.Ordinal) &&
            IsSafeDatabaseSuffix(expectedDatabase[expectedDatabasePrefix.Length..]) &&
            string.Equals(expectedDatabase, _mongo.Database, StringComparison.Ordinal),
            "DATABASE_MISMATCH");
        ValidateMongoOwner(expectedDatabasePrefix);

        var workspaceRoot = ResolveWorkspaceRoot();
        ValidatePinnedLineage(workspaceRoot);
        var candidateDirectory = WorkspacePath(
            workspaceRoot,
            CandidateDirectoryRelativePath);
        ValidateCandidateDirectory(candidateDirectory);

        var catalogPath = ResolveRequiredExactPath(
            Key("CatalogPath"),
            Path.Combine(candidateDirectory, "catalog.json"));
        var schemaPath = ResolveRequiredExactPath(
            Key("SchemaPath"),
            Path.Combine(candidateDirectory, "schema.json"));
        var evidencePath = ResolveRequiredExactPath(
            Key("EvidencePath"),
            Path.Combine(candidateDirectory, "candidate-evidence.json"));
        var stageLockPath = ResolveRequiredExactPath(
            Key("StageLockPath"),
            Path.Combine(candidateDirectory, "stage-lock.json"));

        var configuredCatalogRaw = RequiredSha(Key("CatalogRawSha256"));
        var configuredCatalogSemantic = RequiredSha(Key("CatalogSemanticSha256"));
        var configuredSchemaRaw = RequiredSha(Key("SchemaRawSha256"));
        var configuredSchemaSemantic = RequiredSha(Key("SchemaSemanticSha256"));
        var configuredEvidenceRaw = RequiredSha(Key("EvidenceSha256"));
        var configuredStageLockRaw = RequiredSha(Key("StageLockSha256"));
        Require(
            configuredCatalogRaw == RequiredCatalogRawSha256 &&
            configuredCatalogSemantic == RequiredCatalogSemanticSha256 &&
            configuredSchemaRaw == RequiredSchemaRawSha256 &&
            configuredSchemaSemantic == RequiredSchemaSemanticSha256 &&
            configuredEvidenceRaw == RequiredEvidenceRawSha256 &&
            configuredStageLockRaw == RequiredStageLockRawSha256,
            "CANDIDATE_PIN_MISMATCH");

        var catalogBytes = RequirePinnedFile(
            catalogPath,
            configuredCatalogRaw,
            "CATALOG_RAW_HASH_MISMATCH");
        var schemaBytes = RequirePinnedFile(
            schemaPath,
            configuredSchemaRaw,
            "SCHEMA_RAW_HASH_MISMATCH");
        var evidenceBytes = RequirePinnedFile(
            evidencePath,
            configuredEvidenceRaw,
            "EVIDENCE_RAW_HASH_MISMATCH");
        var stageLockBytes = RequirePinnedFile(
            stageLockPath,
            configuredStageLockRaw,
            "STAGE_LOCK_HASH_MISMATCH");

        using var catalog = JsonDocument.Parse(catalogBytes);
        using var schema = JsonDocument.Parse(schemaBytes);
        using var evidence = JsonDocument.Parse(evidenceBytes);
        using var stageLock = JsonDocument.Parse(stageLockBytes);
        Require(
            SemanticHash(catalog.RootElement) == configuredCatalogSemantic,
            "CATALOG_SEMANTIC_HASH_MISMATCH");
        Require(
            SemanticHash(schema.RootElement) == configuredSchemaSemantic,
            "SCHEMA_SEMANTIC_HASH_MISMATCH");

        ValidateCandidateCatalog(catalog.RootElement);
        ValidateCandidateSchema(schema.RootElement);
        ValidateCandidateEvidence(evidence.RootElement);
        ValidateStageLock(stageLock.RootElement);

        return new StatisticReconciliationCandidateBinding(
            RequiredChainId,
            RequiredPromptId,
            RequiredStage,
            RequiredCatalogVersion,
            configuredCatalogRaw,
            configuredCatalogSemantic,
            configuredSchemaRaw,
            configuredSchemaSemantic,
            configuredStageLockRaw,
            expectedDatabase,
            Array.Empty<string>(),
            RequiredStageId,
            configuredEvidenceRaw,
            RequiredGeneratorRawSha256);
    }

    private static void ValidatePublishedCatalog(JsonElement root)
    {
        Require(
            ReadString(root, "$schema") ==
                "./DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_7.schema.json" &&
            ReadString(root, "catalogVersion") == RequiredCatalogVersion &&
            ReadString(root, "approvedAt") == "2026-08-10",
            "PUBLISHED_CATALOG_IDENTITY_INVALID");
        var capabilities = RequiredObject(root, "domains")
            .GetProperty("statisticsReconciliationCapabilities")
            .EnumerateArray()
            .ToArray();
        Require(capabilities.Length == ExpectedCapabilityIds.Length,
            "PUBLISHED_CAPABILITY_COUNT_INVALID");
        for (var index = 0; index < capabilities.Length; index++)
        {
            RequireExactProperties(
                capabilities[index],
                ["id", "name", "status", "targetPhase", "testPrefix", "uiSurface"],
                "PUBLISHED_CAPABILITY_SHAPE_INVALID");
            Require(
                ReadString(capabilities[index], "id") ==
                    ExpectedCapabilityIds[index] &&
                ReadString(capabilities[index], "status") == "SUPPORTED" &&
                ReadString(capabilities[index], "targetPhase") == "P10",
                "PUBLISHED_CAPABILITY_STATE_INVALID");
        }
        var statistics = RequiredArray(RequiredObject(root, "domains"),
            "statisticsCapabilities");
        var profile = statistics.EnumerateArray().SingleOrDefault(item =>
            ReadString(item, "id") == "FLOW_STATISTIC_PROFILE");
        Require(
            profile.ValueKind == JsonValueKind.Object &&
            ReadString(profile, "status") == "INTENTIONAL_BLOCK" &&
            profile.TryGetProperty("targetPhase", out var targetPhase) &&
            targetPhase.ValueKind == JsonValueKind.Null,
            "PUBLISHED_PROFILE_BARRIER_INVALID");
    }

    private static void ValidatePublishedLock(JsonElement root)
    {
        RequireExactProperties(
            root,
            ["lockVersion", "publishedCatalogs"],
            "PUBLISHED_LOCK_SHAPE_INVALID");
        Require(root.GetProperty("lockVersion").GetInt32() == 1,
            "PUBLISHED_LOCK_VERSION_INVALID");
        var entries = RequiredArray(root, "publishedCatalogs")
            .EnumerateArray()
            .ToArray();
        Require(entries.Length == 8, "PUBLISHED_LOCK_COUNT_INVALID");
        Require(
            entries.Select(entry => ReadString(entry, "catalogVersion"))
                .SequenceEqual(
                    new[] { "1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "1.7" },
                    StringComparer.Ordinal),
            "PUBLISHED_LOCK_ORDER_INVALID");
        var published = entries[^1];
        Require(
            ReadString(published, "catalogFile") ==
                "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_7.json" &&
            ReadString(published, "schemaFile") ==
                "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_7.schema.json" &&
            ReadString(published, "catalogSha256") ==
                PublishedCatalogSemanticSha256 &&
            ReadString(published, "schemaSha256") ==
                PublishedSchemaSemanticSha256,
            "PUBLISHED_LOCK_ENTRY_INVALID");
    }

    private static void ValidatePublishedStage(JsonElement root)
    {
        RequireExactProperties(
            root,
            [
                "schemaVersion", "version", "stageId", "stagePrompt", "catalog",
                "schema", "generator", "parentStage", "promotionIds", "sealed", "evidence"
            ],
            "PUBLISHED_STAGE_SHAPE_INVALID");
        Require(
            ReadString(root, "schemaVersion") == "P10_CANDIDATE_STAGE_V1" &&
            ReadString(root, "version") == RequiredCatalogVersion &&
            ReadString(root, "stageId") == PublishedStageId &&
            ReadString(root, "stagePrompt") == PublishedStagePromptId &&
            ReadBoolean(root, "sealed") == true,
            "PUBLISHED_STAGE_IDENTITY_INVALID");
        RequireReference(
            RequiredObject(root, "catalog"),
            $"{PublishedCandidateDirectoryRelativePath}/catalog.json",
            PublishedCatalogRawSha256,
            PublishedCatalogSemanticSha256,
            "PUBLISHED_STAGE_CATALOG_INVALID");
        RequireReference(
            RequiredObject(root, "schema"),
            $"{PublishedCandidateDirectoryRelativePath}/schema.json",
            PublishedSchemaRawSha256,
            PublishedSchemaSemanticSha256,
            "PUBLISHED_STAGE_SCHEMA_INVALID");
        RequirePathSha(
            RequiredObject(root, "generator"),
            PublishedGeneratorRelativePath,
            PublishedGeneratorRawSha256,
            "PUBLISHED_STAGE_GENERATOR_INVALID");
        var parent = RequiredObject(root, "parentStage");
        Require(
            ReadString(parent, "stageId") == PublishedParentStageId &&
            ReadString(parent, "path") == PublishedParentStageRelativePath &&
            ReadString(parent, "sha256") == PublishedParentStageRawSha256,
            "PUBLISHED_STAGE_PARENT_INVALID");
        Require(
            RequiredArray(root, "promotionIds").EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .SequenceEqual(ExpectedCapabilityIds, StringComparer.Ordinal),
            "PUBLISHED_STAGE_PROMOTIONS_INVALID");
        RequirePathSha(
            RequiredObject(root, "evidence"),
            $"{PublishedCandidateDirectoryRelativePath}/candidate-evidence.json",
            PublishedEvidenceRawSha256,
            "PUBLISHED_STAGE_EVIDENCE_INVALID");
    }

    private static void ValidatePublishedEvidence(JsonElement root)
    {
        Require(
            ReadString(root, "schemaVersion") == "P10_CANDIDATE_EVIDENCE_V1" &&
            ReadString(root, "packId") == "FULL-P10-RECONCILE" &&
            ReadString(root, "packRevision") == "P10-R1" &&
            ReadString(root, "chainId") == RequiredChainId &&
            ReadString(root, "promptId") == PublishedStagePromptId &&
            ReadString(root, "stageId") == PublishedStageId &&
            ReadString(root, "version") == RequiredCatalogVersion,
            "PUBLISHED_EVIDENCE_IDENTITY_INVALID");
        var seal = RequiredObject(root, "seal");
        Require(
            ReadBoolean(seal, "sealed") == true &&
            ReadString(seal, "sealedAtPrompt") == PublishedStagePromptId &&
            ReadBoolean(seal, "catalogAndSchemaByteCopiedFromParent") == true &&
            ReadBoolean(seal, "published") == false &&
            ReadString(seal, "publishOwnerPrompt") == PublishedPromptId &&
            seal.GetProperty("exactPromotionCount").GetInt32() ==
                ExpectedCapabilityIds.Length &&
            RequiredArray(seal, "promotionIds").EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .SequenceEqual(ExpectedCapabilityIds, StringComparer.Ordinal),
            "PUBLISHED_EVIDENCE_SEAL_INVALID");
        RequireReference(
            RequiredObject(root, "sourceCatalog"),
            ".p10-artifacts/catalog-candidate/p10_chain_20260810002129_9f56/P10-09/catalog.json",
            PublishedCatalogRawSha256,
            PublishedCatalogSemanticSha256,
            "PUBLISHED_EVIDENCE_SOURCE_CATALOG_INVALID");
        RequireReference(
            RequiredObject(root, "sourceSchema"),
            ".p10-artifacts/catalog-candidate/p10_chain_20260810002129_9f56/P10-09/schema.json",
            PublishedSchemaRawSha256,
            PublishedSchemaSemanticSha256,
            "PUBLISHED_EVIDENCE_SOURCE_SCHEMA_INVALID");
        var parent = RequiredObject(root, "parentStage");
        Require(
            ReadString(parent, "stageId") == PublishedParentStageId &&
            ReadString(parent, "path") == PublishedParentStageRelativePath &&
            ReadString(parent, "sha256") == PublishedParentStageRawSha256,
            "PUBLISHED_EVIDENCE_PARENT_INVALID");
        RequirePathSha(
            RequiredObject(root, "current"),
            CurrentRelativePath,
            RequiredCurrentRawSha256,
            "PUBLISHED_EVIDENCE_CURRENT_INVALID");
        RequirePathSha(
            RequiredObject(root, "lock"),
            LockRelativePath,
            RequiredLockRawSha256,
            "PUBLISHED_EVIDENCE_LOCK_INVALID");
        RequireReference(
            RequiredObject(root, "catalog"),
            $"{PublishedCandidateDirectoryRelativePath}/catalog.json",
            PublishedCatalogRawSha256,
            PublishedCatalogSemanticSha256,
            "PUBLISHED_EVIDENCE_CATALOG_INVALID");
        RequireReference(
            RequiredObject(root, "schema"),
            $"{PublishedCandidateDirectoryRelativePath}/schema.json",
            PublishedSchemaRawSha256,
            PublishedSchemaSemanticSha256,
            "PUBLISHED_EVIDENCE_SCHEMA_INVALID");
        Require(
            RequiredArray(root, "promotionIds").EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .SequenceEqual(ExpectedCapabilityIds, StringComparer.Ordinal) &&
            ReadBoolean(root, "currentRemainsV16") == true &&
            ReadBoolean(root, "lockRemainsAppendOnlyThroughV16") == true &&
            ReadBoolean(root, "sealed") == true &&
            ReadBoolean(root, "published") == false &&
            ReadString(root, "publishOwnerPrompt") == PublishedPromptId,
            "PUBLISHED_EVIDENCE_STATE_INVALID");
    }

    private static void ValidateCandidateCatalog(JsonElement root)
    {
        Require(
            ReadString(root, "$schema") ==
                "./DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_7.schema.json" &&
            ReadString(root, "catalogVersion") == RequiredCatalogVersion &&
            ReadString(root, "approvedAt") == "2026-08-10",
            "CATALOG_IDENTITY_INVALID");
        var domains = RequiredObject(root, "domains");
        Require(
            domains.TryGetProperty(
                "statisticsReconciliationCapabilities",
                out var capabilities) &&
            capabilities.ValueKind == JsonValueKind.Array,
            "CATALOG_RECONCILIATION_CAPABILITIES_INVALID");
        var entries = capabilities.EnumerateArray().ToArray();
        Require(entries.Length == ExpectedCapabilityIds.Length,
            "CATALOG_RECONCILIATION_CAPABILITY_COUNT_INVALID");
        var expectedNames = new[]
        {
            "Source-to-result statistics reconciliation",
            "Expected-versus-actual typed delta",
            "Independent reconciliation review sign-off",
            "Deterministic reconciliation evidence export"
        };
        var expectedPrefixes = new[]
        {
            "P10-RECONCILE", "P10-DELTA", "P10-REVIEW", "P10-EVIDENCE"
        };
        var expectedSurfaces = new[]
        {
            "STAT_RECONCILIATION",
            "STAT_RECONCILIATION",
            "STAT_RECONCILIATION_REVIEW",
            "STAT_RECONCILIATION_EVIDENCE"
        };
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            RequireExactProperties(
                entry,
                ["id", "name", "status", "targetPhase", "testPrefix", "uiSurface"],
                "CATALOG_RECONCILIATION_CAPABILITY_SHAPE_INVALID");
            Require(
                ReadString(entry, "id") == ExpectedCapabilityIds[index] &&
                ReadString(entry, "name") == expectedNames[index] &&
                ReadString(entry, "status") == "PATCH_REQUIRED" &&
                ReadString(entry, "targetPhase") == "P10" &&
                ReadString(entry, "testPrefix") == expectedPrefixes[index] &&
                ReadString(entry, "uiSurface") == expectedSurfaces[index],
                "CATALOG_RECONCILIATION_CAPABILITY_STATE_INVALID");
        }

        var statistics = RequiredArray(domains, "statisticsCapabilities");
        var profile = statistics.EnumerateArray().SingleOrDefault(item =>
            ReadString(item, "id") == "FLOW_STATISTIC_PROFILE");
        Require(
            profile.ValueKind == JsonValueKind.Object &&
            ReadString(profile, "status") == "INTENTIONAL_BLOCK" &&
            profile.TryGetProperty("targetPhase", out var targetPhase) &&
            targetPhase.ValueKind == JsonValueKind.Null,
            "CATALOG_PROFILE_BARRIER_INVALID");
    }

    private static void ValidateCandidateSchema(JsonElement root)
    {
        Require(
            ReadString(root, "title") ==
                "Dynamic Form Flow Capability Catalog v1.7",
            "SCHEMA_TITLE_INVALID");
        var properties = RequiredObject(root, "properties");
        var version = RequiredObject(properties, "catalogVersion");
        Require(
            ReadString(version, "const") == RequiredCatalogVersion,
            "SCHEMA_VERSION_INVALID");
        var domains = RequiredObject(properties, "domains");
        var required = RequiredArray(domains, "required")
            .EnumerateArray()
            .Select(value => value.GetString() ?? string.Empty)
            .ToArray();
        Require(
            required.Count(value => value ==
                "statisticsReconciliationCapabilities") == 1 &&
            required[^1] == "statisticsReconciliationCapabilities",
            "SCHEMA_RECONCILIATION_REQUIRED_INVALID");
        var domainProperties = RequiredObject(domains, "properties");
        var reconciliation = RequiredObject(
            domainProperties,
            "statisticsReconciliationCapabilities");
        RequireExactProperties(
            reconciliation,
            ["$ref"],
            "SCHEMA_RECONCILIATION_DOMAIN_SHAPE_INVALID");
        Require(
            ReadString(reconciliation, "$ref") == "#/$defs/capabilityArray",
            "SCHEMA_RECONCILIATION_DOMAIN_REF_INVALID");
    }

    private static void ValidateCandidateEvidence(JsonElement root)
    {
        Require(
            ReadString(root, "schemaVersion") == "P10_CANDIDATE_EVIDENCE_V1" &&
            ReadString(root, "packId") == "FULL-P10-RECONCILE" &&
            ReadString(root, "chainId") == RequiredChainId &&
            ReadString(root, "promptId") == RequiredPromptId &&
            ReadString(root, "stageId") == RequiredStageId &&
            ReadString(root, "version") == RequiredCatalogVersion,
            "CANDIDATE_EVIDENCE_IDENTITY_INVALID");
        RequireReference(
            RequiredObject(root, "sourceCatalog"),
            SourceCatalogRelativePath,
            RequiredSourceCatalogRawSha256,
            RequiredSourceCatalogSemanticSha256,
            "EVIDENCE_SOURCE_CATALOG_INVALID");
        RequireReference(
            RequiredObject(root, "sourceSchema"),
            SourceSchemaRelativePath,
            RequiredSourceSchemaRawSha256,
            RequiredSourceSchemaSemanticSha256,
            "EVIDENCE_SOURCE_SCHEMA_INVALID");
        RequirePathSha(
            RequiredObject(root, "current"),
            CurrentRelativePath,
            RequiredCurrentRawSha256,
            "EVIDENCE_CURRENT_INVALID");
        RequirePathSha(
            RequiredObject(root, "lock"),
            LockRelativePath,
            RequiredLockRawSha256,
            "EVIDENCE_LOCK_INVALID");
        RequirePathSha(
            RequiredObject(root, "manifest"),
            ManifestRelativePath,
            RequiredManifestRawSha256,
            "EVIDENCE_MANIFEST_INVALID");
        RequirePathSha(
            RequiredObject(root, "previousHandoff"),
            PreviousHandoffRelativePath,
            RequiredPreviousHandoffRawSha256,
            "EVIDENCE_PREDECESSOR_INVALID");
        RequirePathSha(
            RequiredObject(root, "baseline"),
            BaselineRelativePath,
            RequiredBaselineRawSha256,
            "EVIDENCE_BASELINE_INVALID");
        RequirePathSha(
            RequiredObject(root, "generator"),
            GeneratorRelativePath,
            RequiredGeneratorRawSha256,
            "EVIDENCE_GENERATOR_INVALID");
        RequireReference(
            RequiredObject(root, "catalog"),
            $"{CandidateDirectoryRelativePath}/catalog.json",
            RequiredCatalogRawSha256,
            RequiredCatalogSemanticSha256,
            "EVIDENCE_CATALOG_INVALID");
        RequireReference(
            RequiredObject(root, "schema"),
            $"{CandidateDirectoryRelativePath}/schema.json",
            RequiredSchemaRawSha256,
            RequiredSchemaSemanticSha256,
            "EVIDENCE_SCHEMA_INVALID");
        Require(
            RequiredArray(root, "promotionIds").GetArrayLength() == 0 &&
            ReadBoolean(root, "currentRemainsV16") == true &&
            ReadBoolean(root, "lockRemainsAppendOnlyThroughV16") == true &&
            ReadBoolean(root, "published") == false,
            "EVIDENCE_PUBLICATION_STATE_INVALID");
    }

    private static void ValidateStageLock(JsonElement root)
    {
        RequireExactProperties(
            root,
            [
                "schemaVersion", "version", "stageId", "stagePrompt", "catalog",
                "schema", "generator", "parentStage", "promotionIds", "sealed", "evidence"
            ],
            "STAGE_LOCK_SHAPE_INVALID");
        Require(
            ReadString(root, "schemaVersion") == "P10_CANDIDATE_STAGE_V1" &&
            ReadString(root, "version") == RequiredCatalogVersion &&
            ReadString(root, "stageId") == RequiredStageId &&
            ReadString(root, "stagePrompt") == RequiredPromptId,
            "STAGE_LOCK_IDENTITY_INVALID");
        RequireReference(
            RequiredObject(root, "catalog"),
            $"{CandidateDirectoryRelativePath}/catalog.json",
            RequiredCatalogRawSha256,
            RequiredCatalogSemanticSha256,
            "STAGE_CATALOG_PIN_INVALID");
        RequireReference(
            RequiredObject(root, "schema"),
            $"{CandidateDirectoryRelativePath}/schema.json",
            RequiredSchemaRawSha256,
            RequiredSchemaSemanticSha256,
            "STAGE_SCHEMA_PIN_INVALID");
        RequirePathSha(
            RequiredObject(root, "generator"),
            GeneratorRelativePath,
            RequiredGeneratorRawSha256,
            "STAGE_GENERATOR_PIN_INVALID");
        Require(
            root.TryGetProperty("parentStage", out var parentStage) &&
            parentStage.ValueKind == JsonValueKind.Null &&
            RequiredArray(root, "promotionIds").GetArrayLength() == 0 &&
            ReadBoolean(root, "sealed") == false,
            "STAGE_BASE_STATE_INVALID");
        RequirePathSha(
            RequiredObject(root, "evidence"),
            $"{CandidateDirectoryRelativePath}/candidate-evidence.json",
            RequiredEvidenceRawSha256,
            "STAGE_EVIDENCE_PIN_INVALID");
    }

    private static void ValidatePinnedLineage(string workspaceRoot)
    {
        var currentBytes = RequirePinnedFile(
            WorkspacePath(workspaceRoot, CurrentRelativePath),
            RequiredCurrentRawSha256,
            "CURRENT_RAW_HASH_MISMATCH");
        var lockBytes = RequirePinnedFile(
            WorkspacePath(workspaceRoot, LockRelativePath),
            RequiredLockRawSha256,
            "LOCK_RAW_HASH_MISMATCH");
        var sourceCatalogBytes = RequirePinnedFile(
            WorkspacePath(workspaceRoot, SourceCatalogRelativePath),
            RequiredSourceCatalogRawSha256,
            "SOURCE_CATALOG_RAW_HASH_MISMATCH");
        var sourceSchemaBytes = RequirePinnedFile(
            WorkspacePath(workspaceRoot, SourceSchemaRelativePath),
            RequiredSourceSchemaRawSha256,
            "SOURCE_SCHEMA_RAW_HASH_MISMATCH");
        RequirePinnedFile(
            WorkspacePath(workspaceRoot, ManifestRelativePath),
            RequiredManifestRawSha256,
            "MANIFEST_RAW_HASH_MISMATCH");
        RequirePinnedFile(
            WorkspacePath(workspaceRoot, PreviousHandoffRelativePath),
            RequiredPreviousHandoffRawSha256,
            "PREVIOUS_HANDOFF_RAW_HASH_MISMATCH");
        RequirePinnedFile(
            WorkspacePath(workspaceRoot, BaselineRelativePath),
            RequiredBaselineRawSha256,
            "BASELINE_RAW_HASH_MISMATCH");
        RequirePinnedFile(
            WorkspacePath(workspaceRoot, GeneratorRelativePath),
            RequiredGeneratorRawSha256,
            "GENERATOR_RAW_HASH_MISMATCH");

        using var current = JsonDocument.Parse(currentBytes);
        using var catalogLock = JsonDocument.Parse(lockBytes);
        using var sourceCatalog = JsonDocument.Parse(sourceCatalogBytes);
        using var sourceSchema = JsonDocument.Parse(sourceSchemaBytes);
        Require(
            ReadString(current.RootElement, "catalogVersion") ==
                RequiredCurrentCatalogVersion &&
            ReadString(current.RootElement, "catalogSha256") ==
                RequiredSourceCatalogSemanticSha256 &&
            ReadString(current.RootElement, "schemaSha256") ==
                RequiredSourceSchemaSemanticSha256,
            "CURRENT_CATALOG_MISMATCH");
        Require(
            SemanticHash(sourceCatalog.RootElement) ==
                RequiredSourceCatalogSemanticSha256 &&
            SemanticHash(sourceSchema.RootElement) ==
                RequiredSourceSchemaSemanticSha256,
            "SOURCE_SEMANTIC_HASH_MISMATCH");
        var v17Count = catalogLock.RootElement
            .GetProperty("publishedCatalogs")
            .EnumerateArray()
            .Count(entry => ReadString(entry, "catalogVersion") ==
                RequiredCatalogVersion);
        Require(v17Count == 0, "V17_ALREADY_PUBLISHED");
    }

    private void ValidateMongoOwner(string expectedPrefix)
    {
        try
        {
            var builder = new MongoUrlBuilder(_mongo.ConnectionString);
            var servers = builder.Servers.ToArray();
            var suffix = _mongo.Database[expectedPrefix.Length..];
            Require(
                servers.Length == 1 &&
                servers[0].Port > 0 &&
                servers[0].Host is "127.0.0.1" or "localhost" or "::1" &&
                string.Equals(
                    builder.ReplicaSetName,
                    $"p10rs_{suffix}",
                    StringComparison.Ordinal) &&
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

    private string ResolveWorkspaceRoot()
    {
        var contentRoot = Path.GetFullPath(_environment.ContentRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Require(
            string.Equals(
                Path.GetFileName(contentRoot),
                "tdtd-be",
                StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(contentRoot, "tdtd-be.csproj")),
            "CONTENT_ROOT_MISMATCH");
        var workspaceRoot = Directory.GetParent(contentRoot)?.FullName;
        Require(!string.IsNullOrWhiteSpace(workspaceRoot), "WORKSPACE_ROOT_MISSING");
        return Path.GetFullPath(workspaceRoot!);
    }

    private string ResolveRequiredExactPath(string key, string expectedPath)
    {
        var configuredPath = Path.GetFullPath(Required(key));
        expectedPath = Path.GetFullPath(expectedPath);
        Require(
            string.Equals(
                configuredPath,
                expectedPath,
                StringComparison.OrdinalIgnoreCase),
            "CANDIDATE_PATH_MISMATCH");
        RequirePinnedFile(expectedPath, null, "CANDIDATE_FILE_MISSING");
        return expectedPath;
    }

    private static void ValidateCandidateDirectory(string candidateDirectory)
    {
        RequireNoReparsePath(candidateDirectory);
        Require(Directory.Exists(candidateDirectory), "CANDIDATE_DIRECTORY_MISSING");
        var entries = Directory.EnumerateFileSystemEntries(candidateDirectory).ToArray();
        var expected = new HashSet<string>(
            ["catalog.json", "schema.json", "candidate-evidence.json", "stage-lock.json"],
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

    private static string SemanticHash(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
        {
            WriteCanonical(writer, root);
        }
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

    private static void RequireReference(
        JsonElement value,
        string path,
        string rawSha256,
        string semanticSha256,
        string reason)
    {
        Require(
            ReadString(value, "path") == path &&
            ReadString(value, "rawSha256") == rawSha256 &&
            ReadString(value, "semanticSha256") == semanticSha256,
            reason);
    }

    private static void RequirePathSha(
        JsonElement value,
        string path,
        string sha256,
        string reason)
    {
        Require(
            ReadString(value, "path") == path &&
            ReadString(value, "sha256") == sha256,
            reason);
    }

    private static void RequireExactProperties(
        JsonElement value,
        IReadOnlyList<string> expected,
        string reason)
    {
        Require(value.ValueKind == JsonValueKind.Object, reason);
        var actual = value.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var sortedExpected = expected.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Require(actual.SequenceEqual(sortedExpected, StringComparer.Ordinal), reason);
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        Require(
            parent.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Object,
            $"{name.ToUpperInvariant()}_INVALID");
        return value;
    }

    private static JsonElement RequiredArray(JsonElement parent, string name)
    {
        Require(
            parent.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Array,
            $"{name.ToUpperInvariant()}_INVALID");
        return value;
    }

    private static string ReadString(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object &&
           parent.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool? ReadBoolean(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object &&
           parent.TryGetProperty(name, out var value) &&
           value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

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

    private static string Key(string suffix)
        => $"{ConfigurationPrefix}:{suffix}";

    private static string WorkspacePath(string workspaceRoot, string relativePath)
        => Path.GetFullPath(Path.Combine(
            workspaceRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static bool IsSafeDatabaseSuffix(string value)
        => value.Length is >= 3 and <= 48 &&
           value.All(character =>
               character is (>= 'a' and <= 'z') or
               (>= '0' and <= '9') or '_');

    private static StatisticReconciliationCandidateEvaluation Disabled(
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
