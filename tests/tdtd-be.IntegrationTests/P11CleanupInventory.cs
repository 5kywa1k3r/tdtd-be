using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal static class P11CleanupInventory
{
    internal sealed record Evidence(
        string SchemaVersion,
        string DatabaseName,
        bool DatabaseNameGuarded,
        string DataDirectoryLeaf,
        string DataDirectoryRelativePath,
        bool DataDirectoryGuarded,
        IReadOnlyList<string> PlannedCollections,
        int PlannedCollectionCount,
        string PlannedCollectionSetSha256,
        int? ContinuationCollectionCount,
        string? ContinuationCollectionSetSha256,
        bool? ContinuationCollectionSetMatches,
        bool? ContinuationCollectionsPreserved,
        IReadOnlyList<string> AllowedJourneyNewCollections,
        IReadOnlyList<string> JourneyOwnedNewCollections,
        IReadOnlyList<string> UnknownJourneyCollections,
        IReadOnlyList<string> MissingContinuationCollections,
        IReadOnlyList<string> MissingAllowedJourneyNewCollections,
        int? UnknownCollectionDelta,
        int DataFileCount,
        long DataFileBytes,
        string DataFileMetadataSha256,
        bool DryRunPassed,
        DateTime CapturedAtUtc)
    {
        // Missing lazy collections are incomplete journey coverage, not leaked resources.
        // DryRunPassed above retains the exact full-journey topology contract.
        internal bool PhysicalDryRunPassed => P11ResultCloseoutContract.PhysicalCollectionScopeVerified(
            DatabaseNameGuarded, DataDirectoryGuarded, ContinuationCollectionCount,
            ContinuationCollectionsPreserved, AllowedJourneyNewCollections.Count,
            UnknownJourneyCollections.Count, MissingContinuationCollections.Count);
    }

    internal sealed record SecondDryRunEvidence(
        string SchemaVersion,
        string DatabaseNameSha256,
        string DataDirectoryRelativePath,
        bool DatabaseAbsentAfterApply,
        string DatabaseAbsenceProbe,
        int BackendProcessId,
        int BackendPort,
        int MongoProcessId,
        int MongoPort,
        bool BackendProcessStopped,
        bool BackendPortBindable,
        bool MongoProcessStopped,
        bool MongoPortBindable,
        bool DataDirectoryAbsent,
        bool RuntimeSecretManifestAbsent,
        IReadOnlyList<string> ProbedOwnedResources,
        int ProbedOwnedResourceCount,
        string ProbedOwnedResourceSetSha256,
        IReadOnlyList<string> RemainingOwnedResources,
        int RemainingOwnedResourceCount,
        bool SecondDryRunEmpty,
        DateTime CapturedAtUtc);

    internal static async Task<Evidence> CaptureAsync(
        MongoReplicaSetLease mongo,
        HarnessPaths paths,
        string iterationRoot,
        string? continuationManifestPath,
        IReadOnlyCollection<string>? allowedJourneyNewCollections,
        CancellationToken ct)
    {
        var databaseNameGuarded =
            mongo.DatabaseName.StartsWith("tdtd_p11_", StringComparison.Ordinal) &&
            mongo.DatabaseName.Length > "tdtd_p11_".Length &&
            mongo.DatabaseName.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

        var integrationRoot = Path.GetFullPath(paths.IntegrationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var dataDirectory = Path.GetFullPath(mongo.DataDirectory);
        var expectedDataDirectory = Path.GetFullPath(Path.Combine(iterationRoot, "mongo-data"));
        var dataDirectoryGuarded =
            dataDirectory.StartsWith(integrationRoot, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(dataDirectory, expectedDataDirectory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(dataDirectory), "mongo-data", StringComparison.Ordinal);
        var dataDirectoryRelativePath = Path.GetRelativePath(paths.IntegrationRoot, dataDirectory)
            .Replace(Path.DirectorySeparatorChar, '/');

        if (!databaseNameGuarded || !dataDirectoryGuarded)
            throw new InvalidOperationException("P11 cleanup dry-run rejected an unguarded owned resource.");

        var collectionInfos = (await mongo.Client
                .GetDatabase(mongo.DatabaseName)
                .ListCollectionsAsync(cancellationToken: ct))
            .ToList(ct);
        var plannedCollections = collectionInfos
            .Where(item => item.TryGetValue("name", out var name) &&
                           name.IsString &&
                           !name.AsString.StartsWith("system.", StringComparison.Ordinal))
            .Select(item => item["name"].AsString)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (plannedCollections.Distinct(StringComparer.Ordinal).Count() != plannedCollections.Length)
            throw new InvalidOperationException("P11 cleanup inventory contains duplicate Mongo collection names.");

        var allowedNewCollections = (allowedJourneyNewCollections ?? Array.Empty<string>())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (allowedNewCollections.Any(name =>
                string.IsNullOrWhiteSpace(name) ||
                name.StartsWith("system.", StringComparison.Ordinal)) ||
            allowedNewCollections.Distinct(StringComparer.Ordinal).Count() != allowedNewCollections.Length)
        {
            throw new InvalidOperationException("P11 cleanup allowed-new collection inventory is invalid.");
        }

        var plannedSetSha256 = SetSha256(plannedCollections);
        int? continuationCollectionCount = null;
        string? continuationSetSha256 = null;
        bool? continuationSetMatches = null;
        bool? continuationCollectionsPreserved = null;
        var journeyOwnedNewCollections = Array.Empty<string>();
        var unknownJourneyCollections = Array.Empty<string>();
        var missingContinuationCollections = Array.Empty<string>();
        var missingAllowedJourneyNewCollections = Array.Empty<string>();
        int? unknownCollectionDelta = null;
        if (!string.IsNullOrWhiteSpace(continuationManifestPath))
        {
            var expected = await ReadManifestCollectionNamesAsync(continuationManifestPath, ct);
            continuationCollectionCount = expected.Length;
            continuationSetSha256 = SetSha256(expected);
            continuationSetMatches = plannedCollections.SequenceEqual(expected, StringComparer.Ordinal);
            missingContinuationCollections = expected
                .Except(plannedCollections, StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            var actualNewCollections = plannedCollections
                .Except(expected, StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            journeyOwnedNewCollections = actualNewCollections
                .Intersect(allowedNewCollections, StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            unknownJourneyCollections = actualNewCollections
                .Except(allowedNewCollections, StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            missingAllowedJourneyNewCollections = allowedNewCollections
                .Except(actualNewCollections, StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            continuationCollectionsPreserved = missingContinuationCollections.Length == 0;
            unknownCollectionDelta = missingContinuationCollections.Length +
                unknownJourneyCollections.Length + missingAllowedJourneyNewCollections.Length;
        }

        var files = Directory.Exists(dataDirectory)
            ? Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Select(file => new DataFile(
                    Path.GetRelativePath(dataDirectory, file.FullName)
                        .Replace(Path.DirectorySeparatorChar, '/'),
                    file.Length))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<DataFile>();
        var fileMetadata = string.Concat(files.Select(file =>
            $"{file.RelativePath}\0{file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n"));

        var evidence = new Evidence(
            "P11_CLEANUP_INVENTORY_V1",
            mongo.DatabaseName,
            databaseNameGuarded,
            Path.GetFileName(dataDirectory),
            dataDirectoryRelativePath,
            dataDirectoryGuarded,
            plannedCollections,
            plannedCollections.Length,
            plannedSetSha256,
            continuationCollectionCount,
            continuationSetSha256,
            continuationSetMatches,
            continuationCollectionsPreserved,
            allowedNewCollections,
            journeyOwnedNewCollections,
            unknownJourneyCollections,
            missingContinuationCollections,
            missingAllowedJourneyNewCollections,
            unknownCollectionDelta,
            files.Length,
            files.Sum(file => (long)file.Length),
            Sha256Utf8(fileMetadata),
            string.IsNullOrWhiteSpace(continuationManifestPath)
                ? allowedNewCollections.Length == 0
                : unknownCollectionDelta == 0,
            DateTime.UtcNow);

        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "mongo-cleanup-inventory.json"),
            new
            {
                evidence.SchemaVersion,
                evidence.DatabaseName,
                evidence.DatabaseNameGuarded,
                evidence.DataDirectoryLeaf,
                evidence.DataDirectoryRelativePath,
                evidence.DataDirectoryGuarded,
                evidence.PlannedCollections,
                evidence.PlannedCollectionCount,
                evidence.PlannedCollectionSetSha256,
                evidence.ContinuationCollectionCount,
                evidence.ContinuationCollectionSetSha256,
                evidence.ContinuationCollectionSetMatches,
                evidence.ContinuationCollectionsPreserved,
                evidence.AllowedJourneyNewCollections,
                evidence.JourneyOwnedNewCollections,
                evidence.UnknownJourneyCollections,
                evidence.MissingContinuationCollections,
                evidence.MissingAllowedJourneyNewCollections,
                evidence.UnknownCollectionDelta,
                evidence.DataFileCount,
                evidence.DataFileBytes,
                evidence.DataFileMetadataSha256,
                exactOwnedResourceApply = new
                {
                    databaseName = mongo.DatabaseName,
                    dataDirectoryLeaf = Path.GetFileName(dataDirectory),
                    collectionCount = plannedCollections.Length
                },
                evidence.DryRunPassed,
                evidence.PhysicalDryRunPassed,
                evidence.CapturedAtUtc
            },
            ct);
        return evidence;
    }

    internal static async Task<SecondDryRunEvidence> CaptureSecondDryRunAsync(
        MongoReplicaSetLease mongo,
        BackendServerLease backend,
        HarnessPaths paths,
        string iterationRoot,
        string runtimeSecretPath,
        CancellationToken ct)
    {
        var expectedDataDirectory = Path.GetFullPath(Path.Combine(iterationRoot, "mongo-data"));
        var actualDataDirectory = Path.GetFullPath(mongo.DataDirectory);
        if (!string.Equals(expectedDataDirectory, actualDataDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("P11 second dry-run rejected a non-owned Mongo data directory.");

        var probed = new[]
        {
            "DATABASE", "BACKEND_PROCESS", "BACKEND_PORT", "MONGO_PROCESS",
            "MONGO_PORT", "MONGO_DATA_DIRECTORY", "RUNTIME_SECRET_MANIFEST"
        };
        var databaseAbsent = mongo.DatabaseDropVerified;
        var backendProcessStopped = IsProcessStopped(backend.ProcessId);
        var backendPortBindable = IsPortBindable(backend.Port);
        var mongoProcessStopped = IsProcessStopped(mongo.ProcessId);
        var mongoPortBindable = IsPortBindable(mongo.Port);
        var dataDirectoryAbsent = !Directory.Exists(actualDataDirectory);
        var runtimeSecretAbsent = !File.Exists(runtimeSecretPath);
        var remaining = new List<string>();
        if (!databaseAbsent) remaining.Add("DATABASE");
        if (!backendProcessStopped) remaining.Add("BACKEND_PROCESS");
        if (!backendPortBindable) remaining.Add("BACKEND_PORT");
        if (!mongoProcessStopped) remaining.Add("MONGO_PROCESS");
        if (!mongoPortBindable) remaining.Add("MONGO_PORT");
        if (!dataDirectoryAbsent) remaining.Add("MONGO_DATA_DIRECTORY");
        if (!runtimeSecretAbsent) remaining.Add("RUNTIME_SECRET_MANIFEST");

        var evidence = new SecondDryRunEvidence(
            "P11_CLEANUP_SECOND_DRY_RUN_V1",
            Sha256Utf8(mongo.DatabaseName),
            Path.GetRelativePath(paths.IntegrationRoot, actualDataDirectory)
                .Replace(Path.DirectorySeparatorChar, '/'),
            databaseAbsent,
            "POST_DROP_LIST_DATABASE_NAMES_BEFORE_MONGO_STOP",
            backend.ProcessId,
            backend.Port,
            mongo.ProcessId,
            mongo.Port,
            backendProcessStopped,
            backendPortBindable,
            mongoProcessStopped,
            mongoPortBindable,
            dataDirectoryAbsent,
            runtimeSecretAbsent,
            probed,
            probed.Length,
            SetSha256(probed),
            remaining,
            remaining.Count,
            remaining.Count == 0,
            DateTime.UtcNow);
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "cleanup-second-dry-run.json"),
            evidence,
            ct);
        return evidence;
    }

    private static bool IsProcessStopped(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPortBindable(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static async Task<string[]> ReadManifestCollectionNamesAsync(
        string manifestPath,
        CancellationToken ct)
    {
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("P11 continuation manifest is missing during cleanup inventory.", manifestPath);
        await using var stream = File.OpenRead(manifestPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("collections", out var collections) ||
            collections.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("P11 continuation manifest lacks its collection inventory.");
        }

        var names = collections.EnumerateArray()
            .Select(item => item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null)
            .Select(name => name ?? throw new InvalidOperationException(
                "P11 continuation manifest contains an invalid collection name."))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidOperationException("P11 continuation manifest contains duplicate collection names.");
        return names;
    }

    private static string SetSha256(IEnumerable<string> values)
        => Sha256Utf8(string.Concat(values.Select(value => value + "\n")));

    private static string Sha256Utf8(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record DataFile(string RelativePath, long Length);
}
