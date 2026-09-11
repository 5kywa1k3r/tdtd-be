using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using MongoDB.Driver;
using MongoDB.Bson;
using tdtd_be.Models.Statistics;

namespace tdtd_be.IntegrationTests;

internal sealed record P11OwnedServerExport(string ExportId, string StorageKey, string Sha256, long Bytes);
internal sealed record P11ServerExportCleanupResult(string Status, int VerifiedFiles, int DeletedFiles,
    int RemainingFiles, bool RootAbsent, string InventoryPath, IReadOnlyList<string> Errors);

// A test-fixture resource owner. No continuation/vault input and no backend runtime hook.
internal static class P11ServerExportCleanup
{
    private static readonly Regex Storage = new("^[a-f0-9]{24}/[a-f0-9]{64}\\.(csv|xlsx)$", RegexOptions.CultureInvariant);
    private static readonly Regex Database = new("^tdtd_p(10|11)_[a-zA-Z0-9_]+$", RegexOptions.CultureInvariant);

    internal static async Task<P11ServerExportCleanupResult> CaptureAndDeleteAsync(
        HarnessPaths paths, IMongoDatabase database, string inventoryPath,
        bool backendStopped, CancellationToken ct)
    {
        if (!backendStopped) throw new InvalidOperationException("Export cleanup requires stopped backend.");
        var pins = new List<P11OwnedServerExport>();
        var root = Path.Combine(paths.WorkspaceRoot, "tdtd-be", ".build", "stat-run-exports", database.DatabaseNamespace.DatabaseName);
        if (!Directory.Exists(root))
            return DeleteVerified(paths.WorkspaceRoot, database.DatabaseNamespace.DatabaseName, pins, inventoryPath);
        foreach (var collection in new[] { "work_report_statistic_exports", "work_report_statistic_diff_exports" })
        {
            var rows = await database.GetCollection<BsonDocument>(collection)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Project(new BsonDocument { ["_id"] = 1, ["storageKey"] = 1, ["contentHash"] = 1, ["byteCount"] = 1 })
                .ToListAsync(ct);
            pins.AddRange(rows.Select(row => new P11OwnedServerExport(
                row["_id"].ToString()!, row["storageKey"].AsString,
                row["contentHash"].AsString, row["byteCount"].ToInt64())));
        }
        return DeleteVerified(paths.WorkspaceRoot, database.DatabaseNamespace.DatabaseName, pins, inventoryPath);
    }

    internal static P11ServerExportCleanupResult DeleteVerified(string workspace, string databaseName,
        IReadOnlyList<P11OwnedServerExport> pins, string inventoryPath)
    {
        workspace = Path.GetFullPath(workspace);
        if (!Database.IsMatch(databaseName)) throw new InvalidOperationException("Export cleanup database is outside fixture namespace.");
        var parent = Path.GetFullPath(Path.Combine(workspace, "tdtd-be", ".build", "stat-run-exports"));
        var root = Path.GetFullPath(Path.Combine(parent, databaseName));
        RequireUnder(workspace, root);
        if (!string.Equals(Path.GetDirectoryName(root), parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Export cleanup root escaped exact database directory.");
        RequireSafeAncestors(workspace, root);
        var byPath = new Dictionary<string, P11OwnedServerExport>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in pins)
        {
            if (!Storage.IsMatch(pin.StorageKey ?? "") || pin.StorageKey.Split('/')[0] != pin.ExportId ||
                Path.GetFileNameWithoutExtension(pin.StorageKey) != pin.Sha256 || pin.Bytes < 0)
                throw new InvalidOperationException("Export cleanup persisted owner pin is invalid.");
            var file = Path.GetFullPath(Path.Combine(root, pin.StorageKey.Replace('/', Path.DirectorySeparatorChar)));
            RequireUnder(root, file);
            if (!byPath.TryAdd(file, pin)) throw new InvalidOperationException("Export cleanup owner pin is duplicated.");
        }
        var files = new List<string>();
        var directories = new List<string>();
        if (Directory.Exists(root))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                RequireSafeAncestors(workspace, entry);
                if (!Directory.Exists(entry) || !byPath.Keys.Any(file =>
                        string.Equals(Path.GetDirectoryName(file), entry, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Export cleanup found unknown directory or root file.");
                directories.Add(entry);
                foreach (var file in Directory.EnumerateFileSystemEntries(entry))
                {
                    RequireSafeAncestors(workspace, file);
                    if (!File.Exists(file) || Directory.Exists(file) || !byPath.TryGetValue(file, out var pin))
                        throw new InvalidOperationException("Export cleanup found unowned file or nested directory.");
                    RequireRegularSingleLink(file);
                    if (new FileInfo(file).Length != pin.Bytes || Sha(File.ReadAllBytes(file)) != pin.Sha256)
                        throw new InvalidOperationException("Export cleanup content does not match persisted public owner pin.");
                    files.Add(file);
                }
            }
        }
        var prePath = inventoryPath + ".pre.json";
        WriteExclusive(prePath, new { schemaVersion = "P11_SERVER_EXPORT_CLEANUP_INVENTORY_V1",
            databaseName, root, ownerPins = pins, verifiedFiles = files, directories,
            guard = "EXACT_DB_ROOT_NO_LINKS_NO_UNKNOWN_FILES_PUBLIC_OWNER_HASH_MATCH",
            backendMustBeStopped = true, protectedContentRead = false });
        // Every guard runs for the entire inventory before the first deletion.
        foreach (var file in files) File.Delete(file);
        foreach (var directory in directories)
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any())
                throw new InvalidOperationException("Export cleanup directory changed after verification.");
            Directory.Delete(directory, recursive: false);
        }
        if (Directory.Exists(root))
        {
            if (Directory.EnumerateFileSystemEntries(root).Any())
                throw new InvalidOperationException("Export cleanup database directory changed after verification.");
            Directory.Delete(root, recursive: false);
        }
        var result = new P11ServerExportCleanupResult("PASS", files.Count, files.Count, 0,
            !Directory.Exists(root), inventoryPath, Array.Empty<string>());
        WriteExclusive(inventoryPath, new { schemaVersion = "P11_SERVER_EXPORT_CLEANUP_RECEIPT_V1",
            databaseName, root, result, ownerPins = pins, preInventoryPath = prePath,
            preInventorySha256 = Sha(File.ReadAllBytes(prePath)), deletedFiles = files,
            deletedEmptyDirectories = directories, downloadArtifactsTouched = false,
            protectedContentRead = false, protectedContentHashed = false });
        return result;
    }

    internal static void RequireSafeAncestors(string workspace, string target)
    {
        var current = Path.GetFullPath(target);
        RequireUnder(workspace, current);
        while (!string.Equals(current, Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0 ||
                 new FileInfo(current).LinkTarget is not null))
                throw new InvalidOperationException("Fixture path contains a reparse point or link.");
            current = Path.GetDirectoryName(current) ?? throw new InvalidOperationException("Fixture path has no parent.");
        }
    }
    internal static void RequireUnder(string root, string path)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture path escaped the intended workspace/root.");
    }
    private static void RequireRegularSingleLink(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException("Export cleanup target is not a regular file.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Single-link cleanup guard currently requires Windows file identity.");
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1)
            throw new InvalidOperationException("Export cleanup requires an exact single-link file.");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static void WriteExclusive(string path, object value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
    }
}
