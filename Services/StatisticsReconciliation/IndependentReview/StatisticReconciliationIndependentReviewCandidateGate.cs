using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tdtd_be.Services.StatisticsReconciliation.IndependentReview;

public sealed record StatisticReconciliationIndependentReviewCandidateBinding(
    string ChainId,
    string StageId,
    string StageLockSha256,
    string CatalogRawSha256,
    string CatalogSemanticSha256);

public interface IStatisticReconciliationIndependentReviewCandidateGate
{
    StatisticReconciliationIndependentReviewCandidateBinding Require(string routeId);
}

/// <summary>
/// Extends the immutable P10 foundation activation with a separately pinned,
/// unsealed P10-08 candidate. The stage-lock SHA is a deployment input; the
/// stage cannot self-attest its own catalog or evidence bytes.
/// </summary>
public sealed class StatisticReconciliationIndependentReviewCandidateGate(
    IStatisticReconciliationCandidateActivation activation,
    IConfiguration configuration,
    IHostEnvironment environment)
    : IStatisticReconciliationIndependentReviewCandidateGate
{
    public const string StageLockConfigurationKey =
        "StatisticReconciliationCandidate:ReviewStageLockSha256";
    public const string CandidateRelativeRoot =
        ".p10-artifacts/catalog-candidate/p10_chain_20260810002129_9f56/P10-08";
    public const string ParentStageRelativePath =
        ".p10-artifacts/catalog-candidate/p10_chain_20260810002129_9f56/P10-04/stage-lock.json";
    public const string ParentStageSha256 =
        "df9a567dc0e7a1e292373d2512b97b698328e6d52865a4f63f3bea56f93d42e0";

    private const int MaximumArtifactBytes = 2 * 1024 * 1024;
    private static readonly string[] Promotions =
    [
        StatisticReconciliationCapabilities.SourceToResultReconciliation,
        StatisticReconciliationCapabilities.ExpectedActualDelta,
        StatisticReconciliationCapabilities.IndependentReviewSignoff
    ];

    public StatisticReconciliationIndependentReviewCandidateBinding Require(string routeId)
    {
        try
        {
            return RequireCore(routeId);
        }
        catch (StatisticReconciliationIndependentReviewException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or JsonException or CryptographicException or
            ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw Invalid("candidateInvalid");
        }
    }

    private StatisticReconciliationIndependentReviewCandidateBinding RequireCore(
        string routeId)
    {
        var foundation = activation.RequireFoundation(
            StatisticReconciliationCapabilities.IndependentReviewSignoff,
            routeId);
        if (string.Equals(
                foundation.PromptId,
                StatisticReconciliationCapabilityActivation.PublishedPromptId,
                StringComparison.Ordinal))
        {
            Require(
                foundation.ChainId ==
                    StatisticReconciliationCapabilityActivation.RequiredChainId &&
                foundation.Stage ==
                    StatisticReconciliationCapabilityActivation.PublishedStage &&
                foundation.StageId ==
                    StatisticReconciliationCapabilityActivation.PublishedStageId &&
                foundation.StageLockSha256 ==
                    StatisticReconciliationCapabilityActivation.PublishedStageLockRawSha256 &&
                foundation.CatalogRawSha256 ==
                    StatisticReconciliationCapabilityActivation.PublishedCatalogRawSha256 &&
                foundation.CatalogSemanticSha256 ==
                    StatisticReconciliationCapabilityActivation.PublishedCatalogSemanticSha256 &&
                foundation.Promotions.Count == Promotions.Length + 1 &&
                foundation.Promotions.SequenceEqual(
                    Promotions.Append(
                        StatisticReconciliationCapabilities.ReconciliationEvidenceExport),
                    StringComparer.Ordinal),
                "publishedBinding");
            return new(
                foundation.ChainId,
                foundation.StageId,
                foundation.StageLockSha256,
                foundation.CatalogRawSha256,
                foundation.CatalogSemanticSha256);
        }
        var configuredStageSha = RequiredSha(
            configuration[StageLockConfigurationKey], "stageLockPin");
        var workspaceRoot = ResolveWorkspaceRoot(environment);
        var candidateRoot = ExactPath(workspaceRoot, CandidateRelativeRoot);
        RequireDirectory(candidateRoot);

        var stagePath = ExactPath(workspaceRoot,
            $"{CandidateRelativeRoot}/stage-lock.json");
        var catalogPath = ExactPath(workspaceRoot,
            $"{CandidateRelativeRoot}/catalog.json");
        var schemaPath = ExactPath(workspaceRoot,
            $"{CandidateRelativeRoot}/schema.json");
        var evidencePath = ExactPath(workspaceRoot,
            $"{CandidateRelativeRoot}/candidate-evidence.json");
        var stageBytes = ReadBounded(stagePath);
        Require(Hash(stageBytes) == configuredStageSha, "stageLockPin");

        using var stage = JsonDocument.Parse(stageBytes);
        var root = stage.RootElement;
        Require(String(root, "schemaVersion") == "P10_CANDIDATE_STAGE_V1" &&
                String(root, "version") == "1.7" &&
                String(root, "stagePrompt") == "P10-08" &&
                root.GetProperty("sealed").ValueKind == JsonValueKind.False,
            "stageIdentity");
        var stageId = RequiredText(String(root, "stageId"), "stageId");
        Require(ExactStrings(root.GetProperty("promotionIds"), Promotions),
            "promotions");

        var parent = root.GetProperty("parentStage");
        Require(String(parent, "path") == ParentStageRelativePath &&
                String(parent, "sha256") == ParentStageSha256,
            "parentStage");
        var parentBytes = ReadBounded(ExactPath(workspaceRoot,
            ParentStageRelativePath));
        Require(Hash(parentBytes) == ParentStageSha256, "parentStage");
        using var parentStage = JsonDocument.Parse(parentBytes);
        Require(String(parent, "stageId") ==
                String(parentStage.RootElement, "stageId"), "parentStageId");

        var catalogBytes = ValidateReferencedArtifact(root.GetProperty("catalog"),
            $"{CandidateRelativeRoot}/catalog.json", catalogPath, requireSemantic: true);
        var schemaBytes = ValidateReferencedArtifact(root.GetProperty("schema"),
            $"{CandidateRelativeRoot}/schema.json", schemaPath, requireSemantic: true);
        var evidenceBytes = ValidateEvidenceReference(root.GetProperty("evidence"),
            $"{CandidateRelativeRoot}/candidate-evidence.json", evidencePath);
        ValidateGenerator(root.GetProperty("generator"), workspaceRoot);
        ValidateCatalog(catalogBytes, parentStage.RootElement, workspaceRoot);
        ValidateParentSchema(parentStage.RootElement, schemaBytes, workspaceRoot);
        ValidateEvidence(evidenceBytes, stageId, workspaceRoot);

        var catalog = root.GetProperty("catalog");
        Require(foundation.ChainId ==
                StatisticReconciliationCapabilityActivation.RequiredChainId,
            "foundationChain");
        return new(foundation.ChainId, stageId, configuredStageSha,
            RequiredSha(String(catalog, "rawSha256"), "catalogRaw"),
            RequiredSha(String(catalog, "semanticSha256"), "catalogSemantic"));
    }

    private static byte[] ValidateReferencedArtifact(JsonElement reference,
        string expectedRelativePath, string path, bool requireSemantic)
    {
        Require(String(reference, "path") == expectedRelativePath,
            "artifactPath");
        var bytes = ReadBounded(path);
        Require(Hash(bytes) == RequiredSha(String(reference, "rawSha256"),
            "artifactRaw"), "artifactRaw");
        if (requireSemantic)
        {
            using var document = JsonDocument.Parse(bytes);
            Require(SemanticHash(document.RootElement) == RequiredSha(
                String(reference, "semanticSha256"), "artifactSemantic"),
                "artifactSemantic");
        }
        return bytes;
    }

    private static byte[] ValidateEvidenceReference(JsonElement reference,
        string expectedRelativePath, string path)
    {
        Require(String(reference, "path") == expectedRelativePath,
            "evidencePath");
        var bytes = ReadBounded(path);
        Require(Hash(bytes) == RequiredSha(String(reference, "sha256"),
            "evidenceSha"), "evidenceSha");
        return bytes;
    }

    private static void ValidateGenerator(JsonElement generator,
        string workspaceRoot)
    {
        const string relative = "scripts/generate-p10-08-candidate.mjs";
        Require(String(generator, "path") == relative, "generatorPath");
        Require(Hash(ReadBounded(ExactPath(workspaceRoot, relative))) ==
                RequiredSha(String(generator, "sha256"), "generatorSha"),
            "generatorSha");
    }

    private static void ValidateCatalog(byte[] bytes, JsonElement parentStage,
        string workspaceRoot)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Require(String(root, "catalogVersion") == "1.7", "catalogVersion");
        var entries = root.GetProperty("domains")
            .GetProperty("statisticsReconciliationCapabilities")
            .EnumerateArray().ToArray();
        Require(entries.Length == 4, "catalogCapabilities");
        var expected = new[]
        {
            (StatisticReconciliationCapabilities.SourceToResultReconciliation,
                "SUPPORTED"),
            (StatisticReconciliationCapabilities.ExpectedActualDelta, "SUPPORTED"),
            (StatisticReconciliationCapabilities.IndependentReviewSignoff,
                "SUPPORTED"),
            (StatisticReconciliationCapabilities.ReconciliationEvidenceExport,
                "PATCH_REQUIRED")
        };
        for (var index = 0; index < expected.Length; index++)
            Require(String(entries[index], "id") == expected[index].Item1 &&
                    String(entries[index], "status") == expected[index].Item2,
                "catalogCapabilities");

        var parentReference = parentStage.GetProperty("catalog");
        var parentPath = RequiredText(String(parentReference, "path"),
            "parentCatalogPath");
        var parentBytes = ReadBounded(ExactPath(workspaceRoot, parentPath));
        Require(Hash(parentBytes) == RequiredSha(
                String(parentReference, "rawSha256"), "parentCatalogSha"),
            "parentCatalogSha");
        var candidateNode = JsonNode.Parse(bytes) ?? throw Invalid("catalogNode");
        var targetEntries = candidateNode["domains"]?
            ["statisticsReconciliationCapabilities"]?.AsArray() ??
            throw Invalid("catalogNode");
        var target = targetEntries.SingleOrDefault(item =>
            string.Equals(item?["id"]?.GetValue<string>(),
                StatisticReconciliationCapabilities.IndependentReviewSignoff,
                StringComparison.Ordinal)) ?? throw Invalid("catalogTarget");
        target["status"] = "PATCH_REQUIRED";
        using var normalized = JsonDocument.Parse(candidateNode.ToJsonString());
        using var parentDocument = JsonDocument.Parse(parentBytes);
        Require(SemanticHash(normalized.RootElement) ==
                SemanticHash(parentDocument.RootElement),
            "catalogNonTargetDrift");
    }

    private static void ValidateParentSchema(JsonElement parentStage,
        byte[] schemaBytes, string workspaceRoot)
    {
        var parentSchema = parentStage.GetProperty("schema");
        var parentPath = RequiredText(String(parentSchema, "path"),
            "parentSchemaPath");
        var parentBytes = ReadBounded(ExactPath(workspaceRoot, parentPath));
        Require(Hash(parentBytes) ==
                RequiredSha(String(parentSchema, "rawSha256"), "parentSchemaSha") &&
                Hash(parentBytes) == Hash(schemaBytes), "schemaDrift");
    }

    private static void ValidateEvidence(byte[] bytes, string stageId,
        string workspaceRoot)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Require(String(root, "schemaVersion") == "P10_CANDIDATE_EVIDENCE_V1" &&
                String(root, "chainId") ==
                StatisticReconciliationCapabilityActivation.RequiredChainId &&
                String(root, "promptId") == "P10-08" &&
                String(root, "stageId") == stageId &&
                String(root, "executionStoppedBefore") == "P10-T29" &&
                root.GetProperty("sealed").ValueKind == JsonValueKind.False,
            "evidenceIdentity");
        Require(ExactStrings(root.GetProperty("promotionIds"), Promotions),
            "evidencePromotions");
        var requirements = root.GetProperty("requirements");
        var caseGate = root.GetProperty("caseGate");
        var reviewGate = root.GetProperty("reviewGate");
        Require(requirements.GetProperty("expected").GetInt32() == 2 &&
                requirements.GetProperty("passed").GetInt32() == 2 &&
                ExactStrings(requirements.GetProperty("ids"),
                    ["P10-REC-027", "P10-REC-028"]) &&
                String(caseGate, "groupId") == "P10-REVIEW" &&
                caseGate.GetProperty("expected").GetInt32() == 20 &&
                caseGate.GetProperty("passed").GetInt32() == 20 &&
                reviewGate.GetProperty("exactGates").GetInt32() == 5 &&
                reviewGate.GetProperty("exactColumns").GetInt32() == 8 &&
                reviewGate.GetProperty("productionCallable").GetBoolean() &&
                reviewGate.GetProperty("directP9Writes").GetInt32() == 0,
            "evidenceReviewGate");

        var checkpoint = root.GetProperty("predecessorCheckpoint");
        var checkpointPath = RequiredText(String(checkpoint, "path"),
            "checkpointPath");
        Require(checkpointPath.StartsWith(
                    ".p10-artifacts/checkpoints/p10_chain_20260810002129_9f56/P10-08/P10-T28/verify_",
                    StringComparison.Ordinal) &&
                checkpointPath.EndsWith("/P10-T28.verifier-output.json",
                    StringComparison.Ordinal), "checkpointPath");
        var checkpointBytes = ValidatePin(checkpoint, workspaceRoot);
        using var checkpointDocument = JsonDocument.Parse(checkpointBytes);
        var checkpointRoot = checkpointDocument.RootElement;
        Require(String(checkpointRoot, "schemaVersion") ==
                "P10_T27_T28_INDEPENDENT_REVIEW_CHECKPOINT_V2" &&
                String(checkpointRoot, "status") == "PASS" &&
                String(checkpointRoot, "promptId") == "P10-08" &&
                String(checkpointRoot, "taskRange") == "P10-T27..P10-T28" &&
                String(checkpointRoot, "executionStoppedBefore") == "P10-T29" &&
                String(checkpointRoot, "nextPrompt") == "P10-09" &&
                checkpointRoot.GetProperty("scope")
                    .GetProperty("cumulativeCaseCount").GetInt32() == 208 &&
                checkpointRoot.GetProperty("caseGroup")
                    .GetProperty("passed").GetInt32() == 20,
            "checkpointContract");

        var checkpointHandoff = checkpointRoot.GetProperty("predecessors")
            .GetProperty("handoff");
        var expectedHandoffPath = RequiredText(
            String(checkpointHandoff, "path"), "predecessorHandoffPath");
        var expectedHandoffSha = RequiredSha(
            String(checkpointHandoff, "sha256"), "predecessorHandoffSha");
        Require(expectedHandoffPath ==
                ".p10-artifacts/handoffs/p10_chain_20260810002129_9f56/P10-07.attempt-002.json",
            "predecessorHandoffPath");
        ValidateExactPin(root.GetProperty("predecessorHandoff"), workspaceRoot,
            expectedHandoffPath, expectedHandoffSha);
        ValidateExactPin(root.GetProperty("current"), workspaceRoot,
            "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json",
            StatisticReconciliationCapabilityActivation.RequiredCurrentRawSha256);
        ValidateExactPin(root.GetProperty("lock"), workspaceRoot,
            "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json",
            StatisticReconciliationCapabilityActivation.RequiredLockRawSha256);
        ValidateExactPin(root.GetProperty("manifest"), workspaceRoot,
            "docs/features/p10-reconcile/FULL_P10_RECONCILE_PROMPT_MANIFEST.json",
            StatisticReconciliationCapabilityActivation.RequiredManifestRawSha256);
        ValidateExactPin(root.GetProperty("prompt"), workspaceRoot,
            "docs/features/p10-reconcile/prompts/P10_08_INDEPENDENT_REVIEW_SIGNOFF_AND_SUPERSESSION_PROMPT.md",
            "94e7a67f7d524eeb7c70c802637321a563be70ae72469dad6888d558d6bd0b2f");
    }

    private static byte[] ValidatePin(JsonElement pin, string workspaceRoot)
    {
        var relative = RequiredText(String(pin, "path"), "pinPath");
        var expected = RequiredSha(String(pin, "sha256"), "pinSha");
        var bytes = ReadBounded(ExactPath(workspaceRoot, relative));
        Require(Hash(bytes) == expected, "pinSha");
        return bytes;
    }

    private static void ValidateExactPin(JsonElement pin, string workspaceRoot,
        string expectedPath, string expectedSha)
    {
        Require(String(pin, "path") == expectedPath &&
                String(pin, "sha256") == expectedSha, "exactPin");
        ValidatePin(pin, workspaceRoot);
    }
    private static string ResolveWorkspaceRoot(IHostEnvironment value)
    {
        var contentRoot = Path.GetFullPath(value.ContentRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Require(Path.GetFileName(contentRoot).Equals("tdtd-be",
                    StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(contentRoot, "tdtd-be.csproj")),
            "contentRoot");
        return Directory.GetParent(contentRoot)?.FullName ??
               throw Invalid("workspaceRoot");
    }

    private static string ExactPath(string root, string relative)
    {
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, normalized));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        Require(path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
            "pathEscape");
        return path;
    }

    private static void RequireDirectory(string path)
    {
        RequireNoReparse(path);
        Require(Directory.Exists(path), "candidateDirectory");
        var expected = new HashSet<string>(
            ["catalog.json", "schema.json", "candidate-evidence.json", "stage-lock.json"],
            StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            Require(File.Exists(entry) && expected.Remove(Path.GetFileName(entry)),
                "candidateDirectory");
        Require(expected.Count == 0, "candidateDirectory");
    }

    private static byte[] ReadBounded(string path)
    {
        RequireNoReparse(path);
        var info = new FileInfo(path);
        Require(info.Exists && info.Length is > 0 and <= MaximumArtifactBytes,
            "artifactSize");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.SequentialScan);
        var bytes = new byte[checked((int)info.Length)];
        stream.ReadExactly(bytes);
        Require(stream.ReadByte() == -1, "artifactRace");
        return bytes;
    }

    private static void RequireNoReparse(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw Invalid("pathRoot");
        var current = root;
        foreach (var part in Path.GetRelativePath(root, full).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            Require(File.Exists(current) || Directory.Exists(current), "pathMissing");
            Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0,
                "pathReparse");
        }
    }

    private static bool ExactStrings(JsonElement value,
        IReadOnlyList<string> expected)
        => value.ValueKind == JsonValueKind.Array &&
           value.GetArrayLength() == expected.Count &&
           value.EnumerateArray().Select(item => item.GetString())
               .SequenceEqual(expected, StringComparer.Ordinal);

    private static string? String(JsonElement value, string property)
        => value.TryGetProperty(property, out var result) &&
           result.ValueKind == JsonValueKind.String
            ? result.GetString()
            : null;

    private static string RequiredText(string? value, string detail)
    {
        Require(!string.IsNullOrWhiteSpace(value), detail);
        return value!;
    }

    private static string RequiredSha(string? value, string detail)
    {
        Require(value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'), detail);
        return value!;
    }

    private static string Hash(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string SemanticHash(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
            WriteCanonical(writer, value);
        return Hash(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static void Require(bool condition, string detail)
    {
        if (!condition)
            throw Invalid(detail);
    }

    private static StatisticReconciliationIndependentReviewException Invalid(
        string detail) => new(
        StatisticReconciliationIndependentReviewFailureCodes.TargetNotSignable,
        $"candidate:{detail}");
}
