using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationRollbackProbe
{
    private static readonly string[] TrustedRollbackSourcePaths =
    [
        "docs/features/p10-reconcile/FULL_P10_RECONCILE_PROMPT_MANIFEST.json",
        "scripts/generate-p10-11-candidate.mjs",
        "scripts/p10-closeout-gate.mjs",
        "tdtd-be/Common/Capabilities/DynamicFormFlowCapabilityCatalog.g.cs",
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json",
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json",
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.json",
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.schema.json",
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_7.json",
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_7.schema.json",
        "tdtd-be/Controllers/StatisticReconciliationActualCaptureOperationsController.cs",
        "tdtd-be/Controllers/StatisticReconciliationController.cs",
        "tdtd-be/Controllers/StatisticReconciliationEvidenceController.cs",
        "tdtd-be/Controllers/StatisticReconciliationIndependentReviewController.cs",
        "tdtd-be/Controllers/StatisticReconciliationPendingFinalizationController.cs",
        "tdtd-be/Controllers/StatisticReconciliationRecheckController.cs",
        "tdtd-be/Services/StatisticsConfiguration/StatConfigPhaseBarrier.cs",
        "tdtd-be/Services/StatisticsReconciliation/ActualObservation/StatisticReconciliationActualTrustedCaptureWorker.cs",
        "tdtd-be/Services/StatisticsReconciliation/ActualObservation/StatisticReconciliationActualWorkerCaptureActivationGate.cs",
        "tdtd-be/Services/StatisticsReconciliation/EvidenceExport/StatisticReconciliationEvidenceCandidateGate.cs",
        "tdtd-be/Services/StatisticsReconciliation/EvidenceExport/StatisticReconciliationEvidenceExportService.cs",
        "tdtd-be/Services/StatisticsReconciliation/IndependentReview/StatisticReconciliationCurrentReviewValidator.cs",
        "tdtd-be/Services/StatisticsReconciliation/IndependentReview/StatisticReconciliationIndependentReviewCandidateGate.cs",
        "tdtd-be/Services/StatisticsReconciliation/IndependentReview/StatisticReconciliationIndependentReviewOwner.cs",
        "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationCapabilityActivation.cs",
        "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.cs",
        "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.Helpers.cs",
        "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.Recheck.cs",
        "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.RecheckRemediation.cs",
        "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.Worker.cs",
        "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationTrustedFinalizer.cs",
        "tdtd-be/Services/StatisticsRun/StatRunCapabilityActivation.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10ReconciliationCoreProbe.Fixture.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10ReconciliationRollbackProbe.Closeout.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10ReconciliationRollbackProbe.Inventory.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10ReconciliationRollbackProbe.Routes.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10ReconciliationRollbackProbe.UnknownInventory.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10ReconciliationRollbackProbe.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/Program.cs",
        "tdtd-fe/src/api/reconciliationApi.ts",
        "tdtd-fe/src/generated/dynamicFormFlowCapabilityCatalog.generated.ts",
        "tdtd-fe/src/pages/works/reconciliation/ReconciliationPages.tsx",
        "tdtd-fe/src/routes/appRoutes.tsx"
    ];
    private P10RollbackTrustedSourceFingerprint CaptureTrustedSourceFingerprint()
    {
        var files = TrustedRollbackSourcePaths
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(relative =>
            {
                var absolute = Path.Combine(
                    _paths.WorkspaceRoot,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                HarnessAssert.True(File.Exists(absolute),
                    "P10-12 trusted rollback source missing " + relative);
                return new P10RollbackTrustedSourceFile(relative, HashFile(absolute));
            })
            .ToArray();
        var canonical = string.Join(
            "\n",
            files.Select(value => $"{value.Path}\n{value.Sha256}"));
        return new(
            "P10_TRUSTED_SOURCE_FINGERPRINT_V1",
            files.Length,
            files,
            HashBytes(Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task<P10RollbackFutureBarrierEvidence>
        ProveFutureNamespacesBlockedAsync(CancellationToken ct)
    {
        var manifestPath = Path.Combine(
            _paths.WorkspaceRoot,
            "docs",
            "features",
            "p10-reconcile",
            "FULL_P10_RECONCILE_PROMPT_MANIFEST.json");
        using var manifest = JsonDocument.Parse(
            await File.ReadAllBytesAsync(manifestPath, ct));
        var barrier = manifest.RootElement.GetProperty("barrier");
        var manifestBarrierExact =
            barrier.GetProperty("entry").GetString() == "P10_RECONCILE" &&
            barrier.GetProperty("currentPhase").GetInt32() == 8 &&
            barrier.GetProperty("targetPhase").GetInt32() == 10;
        HarnessAssert.True(manifestBarrierExact,
            "P10-12 manifest phase barrier drift");

        var names = await (await Database().ListCollectionNamesAsync(
                cancellationToken: ct)).ToListAsync(ct);
        var successorNamespaces = names
            .Where(value => Regex.IsMatch(
                value,
                @"(^|[_\-.])p(?:11|12)([_\-.]|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        HarnessAssert.Equal(0, successorNamespaces.Length,
            "P10-12 direct successor namespace count");

        var currentPath = Path.Combine(
            _paths.BackendRoot,
            "Contracts",
            "DynamicFormFlow",
            "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json");
        using var current = JsonDocument.Parse(
            await File.ReadAllBytesAsync(currentPath, ct));
        var catalogVersion = current.RootElement
            .GetProperty("catalogVersion")
            .GetString();
        var expectedVersion = _mode == RollbackMode ? "1.6" : "1.7";
        HarnessAssert.Equal(expectedVersion, catalogVersion,
            $"P10-12 {_mode} profile catalog version");
        var catalogFile = catalogVersion switch
        {
            "1.6" => "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.json",
            "1.7" => "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_7.json",
            _ => throw new InvalidOperationException(
                "P10_CLOSEOUT_PROFILE_CATALOG_VERSION_INVALID")
        };
        var catalogPath = Path.Combine(
            _paths.BackendRoot,
            "Contracts",
            "DynamicFormFlow",
            catalogFile);
        await using var catalogStream = new FileStream(
            catalogPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        HarnessAssert.True(catalogStream.Length is > 0 and <= 2 * 1024 * 1024,
            "P10-12 profile catalog bounded length");
        var catalogBytes = new byte[checked((int)catalogStream.Length)];
        await catalogStream.ReadExactlyAsync(catalogBytes, ct);
        HarnessAssert.Equal(-1, catalogStream.ReadByte(),
            "P10-12 profile catalog bounded read");
        using var catalog = JsonDocument.Parse(catalogBytes);
        var profileBlocked = ContainsProfileBarrier(catalog.RootElement);
        HarnessAssert.True(profileBlocked,
            "P10-12 FLOW_STATISTIC_PROFILE barrier missing");
        return new(
            RelativeWorkspace(manifestPath),
            HashFile(manifestPath),
            8,
            10,
            "P10_RECONCILE",
            manifestBarrierExact,
            successorNamespaces,
            successorNamespaces.Length == 0,
            profileBlocked,
            manifestBarrierExact && successorNamespaces.Length == 0);
    }

    private static bool ContainsProfileBarrier(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            string? id = null;
            string? surface = null;
            foreach (var property in value.EnumerateObject())
            {
                if (property.NameEquals("id") &&
                    property.Value.ValueKind == JsonValueKind.String)
                    id = property.Value.GetString();
                if (property.NameEquals("uiSurface") &&
                    property.Value.ValueKind == JsonValueKind.String)
                    surface = property.Value.GetString();
            }
            if (id == "FLOW_STATISTIC_PROFILE_BARRIER" &&
                surface == "FLOW_STATISTIC_PROFILE_DISABLED")
                return true;
            return value.EnumerateObject()
                .Any(property => ContainsProfileBarrier(property.Value));
        }
        return value.ValueKind == JsonValueKind.Array &&
               value.EnumerateArray().Any(ContainsProfileBarrier);
    }

    private static object[] CloseoutRouteRows(
        IEnumerable<P10RollbackLogicalRouteEvidence> rows)
        => rows.Select(value => (object)new
        {
            routeId = value.RouteId,
            status = value.Blocked ? "BLOCKED" : "ENABLED",
            reason = JsonSerializer.SerializeToElement<string?>(value.EvaluationReason),
            writes = value.Writes,
            beforeSha256 = value.BeforeInventorySha256,
            afterSha256 = value.AfterInventorySha256
        }).ToArray();

    private static object[] CloseoutStoreRows(
        P10RollbackInventorySnapshot? before,
        P10RollbackInventorySnapshot? after)
    {
        if (before is null || after is null) return [];
        var afterByName = after.Collections.ToDictionary(
            value => value.Collection,
            StringComparer.Ordinal);
        return before.Collections.Select(value =>
        {
            var right = afterByName[value.Collection];
            return (object)new
            {
                name = value.Collection,
                before = new
                {
                    exists = value.Exists,
                    count = value.Count,
                    sha256 = value.DocumentSetSha256
                },
                after = new
                {
                    exists = right.Exists,
                    count = right.Count,
                    sha256 = right.DocumentSetSha256
                },
                writes = 0
            };
        }).ToArray();
    }

    private static string CompactStringArraySha(IEnumerable<string> values)
        => HashBytes(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            values.ToArray())));

    private P10RollbackArtifactPin Pin(string path, string purpose)
    {
        var info = new FileInfo(path);
        return new(
            RelativeWorkspace(path),
            HashFile(path),
            info.Length,
            purpose);
    }
}

internal sealed record P10RollbackTrustedSourceFile(
    string Path,
    string Sha256);

internal sealed record P10RollbackTrustedSourceFingerprint(
    string SchemaVersion,
    int FileCount,
    IReadOnlyList<P10RollbackTrustedSourceFile> Files,
    string Sha256);

internal sealed record P10RollbackFutureBarrierEvidence(
    string ManifestPath,
    string ManifestSha256,
    int CurrentPhase,
    int TargetPhase,
    string Entry,
    bool ManifestBarrierExact,
    IReadOnlyList<string> DirectSuccessorNamespaces,
    bool DirectSuccessorNamespacesAbsent,
    bool ProfileBlocked,
    bool Passed);

internal sealed record P10RollbackArtifactPin(
    string Path,
    string Sha256,
    long Bytes,
    string Purpose);
