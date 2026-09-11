using System.Security.Cryptography;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.EvidenceExport;

public sealed record StatisticReconciliationEvidenceCandidateBinding(
    string ChainId, string StageId, string StageLockSha256,
    string CatalogRawSha256, string CatalogSemanticSha256);

public interface IStatisticReconciliationEvidenceCandidateGate
{
    StatisticReconciliationEvidenceCandidateBinding Require(string routeId);
}

public sealed class StatisticReconciliationEvidenceCandidateGate(
    IStatisticReconciliationCandidateActivation activation,
    IConfiguration configuration,
    IHostEnvironment environment) : IStatisticReconciliationEvidenceCandidateGate
{
    public const string StageLockConfigurationKey =
        "StatisticReconciliationCandidate:EvidenceStageLockSha256";
    public const string CandidateRelativeRoot =
        ".p10-artifacts/catalog-candidate/p10_chain_20260810002129_9f56/P10-09";
    private static readonly string[] Promotions =
    [
        StatisticReconciliationCapabilities.SourceToResultReconciliation,
        StatisticReconciliationCapabilities.ExpectedActualDelta,
        StatisticReconciliationCapabilities.IndependentReviewSignoff,
        StatisticReconciliationCapabilities.ReconciliationEvidenceExport
    ];

    public StatisticReconciliationEvidenceCandidateBinding Require(string routeId)
    {
        try { return RequireCore(routeId); }
        catch (StatisticReconciliationEvidenceException) { throw; }
        catch (Exception error) when (error is IOException or JsonException or
            CryptographicException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException)
        {
            throw Invalid("candidate");
        }
    }

    private StatisticReconciliationEvidenceCandidateBinding RequireCore(string routeId)
    {
        if (!StatisticReconciliationRouteRegistry.TryResolve(routeId,
                out var capability) || capability !=
            StatisticReconciliationCapabilities.ReconciliationEvidenceExport)
            throw Invalid("route");
        var foundation = activation.RequireFoundation(
            StatisticReconciliationCapabilities.ReconciliationEvidenceExport,
            routeId);
        if (string.Equals(
                foundation.PromptId,
                StatisticReconciliationCapabilityActivation.PublishedPromptId,
                StringComparison.Ordinal))
        {
            if (foundation.ChainId !=
                    StatisticReconciliationCapabilityActivation.RequiredChainId ||
                foundation.Stage !=
                    StatisticReconciliationCapabilityActivation.PublishedStage ||
                foundation.StageId !=
                    StatisticReconciliationCapabilityActivation.PublishedStageId ||
                foundation.StageLockSha256 !=
                    StatisticReconciliationCapabilityActivation.PublishedStageLockRawSha256 ||
                foundation.CatalogRawSha256 !=
                    StatisticReconciliationCapabilityActivation.PublishedCatalogRawSha256 ||
                foundation.CatalogSemanticSha256 !=
                    StatisticReconciliationCapabilityActivation.PublishedCatalogSemanticSha256 ||
                !foundation.Promotions.SequenceEqual(Promotions, StringComparer.Ordinal))
            {
                throw Invalid("publishedBinding");
            }
            return new(
                foundation.ChainId,
                foundation.StageId,
                foundation.StageLockSha256,
                foundation.CatalogRawSha256,
                foundation.CatalogSemanticSha256);
        }
        var configuredSha = RequiredSha(configuration[StageLockConfigurationKey]);
        var workspace = Workspace(environment);
        var stagePath = Exact(workspace, $"{CandidateRelativeRoot}/stage-lock.json");
        var stageBytes = Read(stagePath);
        if (Hash(stageBytes) != configuredSha) throw Invalid("stagePin");
        using var stageDocument = JsonDocument.Parse(stageBytes);
        var stage = stageDocument.RootElement;
        if (String(stage, "schemaVersion") != "P10_CANDIDATE_STAGE_V1" ||
            String(stage, "version") != "1.7" ||
            String(stage, "stagePrompt") != "P10-09" ||
            Boolean(stage, "sealed") ||
            !Sequence(stage.GetProperty("promotionIds"), Promotions))
            throw Invalid("stage");
        var stageId = Text(String(stage, "stageId"));
        var catalog = ValidateReference(workspace, stage.GetProperty("catalog"),
            $"{CandidateRelativeRoot}/catalog.json", true);
        _ = ValidateReference(workspace, stage.GetProperty("schema"),
            $"{CandidateRelativeRoot}/schema.json", true);
        var evidence = ValidateReference(workspace, stage.GetProperty("evidence"),
            $"{CandidateRelativeRoot}/candidate-evidence.json", false);
        var generator = stage.GetProperty("generator");
        if (String(generator, "path") != "scripts/generate-p10-09-candidate.mjs" ||
            Hash(Read(Exact(workspace, String(generator, "path")))) !=
                RequiredSha(String(generator, "sha256")))
            throw Invalid("generator");
        ValidateCatalog(catalog);
        ValidateEvidence(evidence, stageId, workspace);
        if (foundation.ChainId != "p10_chain_20260810002129_9f56")
            throw Invalid("foundation");
        return new(foundation.ChainId, stageId, configuredSha,
            RequiredSha(String(stage.GetProperty("catalog"), "rawSha256")),
            RequiredSha(String(stage.GetProperty("catalog"), "semanticSha256")));
    }

    private static void ValidateCatalog(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var entries = document.RootElement.GetProperty("domains")
            .GetProperty("statisticsReconciliationCapabilities")
            .EnumerateArray().ToArray();
        if (entries.Length != 4) throw Invalid("catalogCount");
        foreach (var capability in Promotions)
        {
            var values = entries.Where(value => String(value, "id") == capability).ToArray();
            if (values.Length != 1 || String(values[0], "status") != "SUPPORTED")
                throw Invalid("catalogCapability");
        }
    }

    private static void ValidateEvidence(byte[] bytes, string stageId,
        string workspace)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (String(root, "schemaVersion") != "P10_CANDIDATE_EVIDENCE_V1" ||
            String(root, "promptId") != "P10-09" ||
            String(root, "stageId") != stageId ||
            !Sequence(root.GetProperty("promotionIds"), Promotions) ||
            Boolean(root, "published"))
            throw Invalid("evidence");
        var checkpoint = root.GetProperty("predecessorCheckpoint");
        var checkpointPath = String(checkpoint, "path");
        var checkpointBytes = Read(Exact(workspace, checkpointPath));
        if (Hash(checkpointBytes) != RequiredSha(String(checkpoint, "sha256")))
            throw Invalid("checkpointPin");
        using var checkpointDocument = JsonDocument.Parse(checkpointBytes);
        var value = checkpointDocument.RootElement;
        if (String(value, "schemaVersion") !=
                "P10_T29_EVIDENCE_EXPORT_CHECKPOINT_V1" ||
            String(value, "status") != "PASS" ||
            value.GetProperty("caseGroup").GetProperty("passed").GetInt32() != 18)
            throw Invalid("checkpoint");
    }

    private static byte[] ValidateReference(string workspace, JsonElement value,
        string path, bool semantic)
    {
        if (String(value, "path") != path) throw Invalid("referencePath");
        var bytes = Read(Exact(workspace, path));
        if (Hash(bytes) != RequiredSha(String(value, semantic ? "rawSha256" : "sha256")))
            throw Invalid("referenceSha");
        return bytes;
    }

    private static string Workspace(IHostEnvironment environment)
    {
        var content = Path.GetFullPath(environment.ContentRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.GetFileName(content).Equals("tdtd-be",
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(content, "tdtd-be.csproj")))
            throw Invalid("contentRoot");
        return Directory.GetParent(content)?.FullName ?? throw Invalid("workspace");
    }

    private static string Exact(string root, string relative)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var value = Path.GetFullPath(Path.Combine(root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!value.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw Invalid("path");
        RequireNoReparsePoints(normalizedRoot, value);
        return value;
    }

    internal static void RequireNoReparsePoints(string root, string path)
    {
        RejectReparsePoint(root);
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(
                     new[]
                     {
                         Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar
                     },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
                RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw Invalid("reparsePoint");
    }
    private static byte[] Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 1 or > 2 * 1024 * 1024 ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw Invalid("file");
        return File.ReadAllBytes(path);
    }
    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string RequiredSha(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
        ? value : throw Invalid("sha");
    private static string Text(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value == value.Trim() ? value : throw Invalid("text");
    private static string String(JsonElement value, string property) =>
        value.GetProperty(property).GetString() ?? throw Invalid(property);
    private static bool Boolean(JsonElement value, string property) =>
        value.GetProperty(property).GetBoolean();
    private static bool Sequence(JsonElement value, IReadOnlyList<string> expected)
        => value.ValueKind == JsonValueKind.Array &&
           value.EnumerateArray().Select(item => item.GetString()).SequenceEqual(expected);
    private static StatisticReconciliationEvidenceException Invalid(string detail)
        => new(StatisticReconciliationEvidenceFailureCodes.PermissionDenied, detail);
}
