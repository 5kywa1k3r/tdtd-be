using System.Runtime.InteropServices;
using System.Text.Json;

namespace tdtd_be.IntegrationTests;

internal static class P11ServerExportCleanupSelftest
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    internal static int Run(string[] args)
    {
        var index = Array.IndexOf(args, "--output-directory");
        if (index < 0 || index + 1 >= args.Length) throw new InvalidOperationException("Cleanup selftest requires output directory.");
        var workspace = Directory.GetCurrentDirectory();
        var output = Path.GetFullPath(args[index + 1]);
        P11ServerExportCleanup.RequireUnder(Path.Combine(workspace, ".p11-artifacts", "offline-validation", "diagnostic-process-v1"), output);
        P11ServerExportCleanup.RequireSafeAncestors(workspace, output);
        Directory.CreateDirectory(output);
        var historicalRun = Path.Combine(workspace, ".p11-artifacts", "runs", "p11_chain_20260821084552_5ed1", "P11-04", "p1104_20260907054249_d225c968");
        var evidencePath = Path.Combine(historicalRun, "P11-04.production-browser.json");
        if (P11ServerExportCleanup.Sha(File.ReadAllBytes(evidencePath)) != "6bc786423bb2c960f9b0999be06074e06b8461188d33e13d35eb6ec75df5cb7b")
            throw new InvalidOperationException("Historical public cleanup regression evidence changed.");
        using var evidence = JsonDocument.Parse(File.ReadAllBytes(evidencePath));
        var originals = evidence.RootElement.GetProperty("downloads").EnumerateArray().ToArray();
        if (originals.Length != 3) throw new InvalidOperationException("Expected exactly three historical public downloads.");
        var seeds = new List<(P11OwnedServerExport Pin, byte[] Bytes, string Download)>();
        foreach (var item in originals)
        {
            var source = Path.GetFullPath(Path.Combine(workspace, item.GetProperty("path").GetString()!));
            P11ServerExportCleanup.RequireUnder(Path.Combine(historicalRun, "downloads"), source);
            P11ServerExportCleanup.RequireSafeAncestors(workspace, source);
            var bytes = File.ReadAllBytes(source);
            var hash = item.GetProperty("sha256").GetString()!;
            if (bytes.Length != item.GetProperty("byteCount").GetInt64() || P11ServerExportCleanup.Sha(bytes) != hash)
                throw new InvalidOperationException("Public retained download pin mismatch.");
            var id = item.GetProperty("exportId").GetString()!;
            var extension = item.GetProperty("format").GetString()!.ToLowerInvariant();
            seeds.Add((new P11OwnedServerExport(id, id + "/" + hash + "." + extension, hash, bytes.Length), bytes, source));
        }
        var cases = new List<object>();
        foreach (var name in new[] { "THREE_039_RESIDUES", "UNKNOWN_FILE", "HASH_MISMATCH", "HARDLINK", "PATH_ESCAPE", "NO_FILES" })
        {
            var db = "tdtd_p11_diagcleanup_" + Guid.NewGuid().ToString("N");
            var root = Path.Combine(workspace, "tdtd-be", ".build", "stat-run-exports", db);
            var pins = seeds.Select(x => x.Pin).ToArray();
            var files = pins.Select(x => Path.Combine(root, x.StorageKey.Replace('/', Path.DirectorySeparatorChar))).ToArray();
            if (name != "NO_FILES")
            {
                for (var i = 0; i < files.Length; i++)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(files[i])!);
                    File.WriteAllBytes(files[i], seeds[i].Bytes);
                }
            }
            var badFile = Path.Combine(root, pins[0].ExportId, "unexpected.csv");
            var link = Path.Combine(output, "link-" + db);
            if (name == "UNKNOWN_FILE") File.WriteAllText(badFile, "synthetic unexpected owned fixture file");
            if (name == "HASH_MISMATCH") File.AppendAllText(files[0], "synthetic drift");
            if (name == "HARDLINK" && !CreateHardLink(link, files[0], 0)) throw new InvalidOperationException("Cannot create isolated hardlink test.");
            var input = name == "PATH_ESCAPE" ? pins.Select((p, i) => i == 0 ? p with { StorageKey = "../outside.csv" } : p).ToArray() : pins;
            var rejected = false;
            try { P11ServerExportCleanup.DeleteVerified(workspace, db, input, Path.Combine(output, name + ".json")); }
            catch (InvalidOperationException) when (name is "UNKNOWN_FILE" or "HASH_MISMATCH" or "HARDLINK" or "PATH_ESCAPE")
            { rejected = true; }
            if (name is "UNKNOWN_FILE" or "HASH_MISMATCH" or "HARDLINK" or "PATH_ESCAPE")
            {
                if (!rejected || files.Any(file => !File.Exists(file)))
                    throw new InvalidOperationException("Unsafe inventory was not rejected before all deletions: " + name);
                if (name == "UNKNOWN_FILE") File.Delete(badFile);
                if (name == "HARDLINK") File.Delete(link);
                if (name == "HASH_MISMATCH") File.WriteAllBytes(files[0], seeds[0].Bytes);
                P11ServerExportCleanup.DeleteVerified(workspace, db, pins, Path.Combine(output, name + "-cleanup.json"));
            }
            if (Directory.Exists(root)) throw new InvalidOperationException("Selftest export resource remains: " + name);
            foreach (var seed in seeds)
                if (P11ServerExportCleanup.Sha(File.ReadAllBytes(seed.Download)) != seed.Pin.Sha256)
                    throw new InvalidOperationException("Retained download changed.");
            cases.Add(new { id = name, status = "PASS", rejectedBeforeAnyDeletion = rejected,
                rootAbsent = true, retainedPublicDownloadsUnchanged = true });
            Console.WriteLine("PASS P11-SERVER-EXPORT-CLEANUP-" + name);
        }
        P11ServerExportCleanup.WriteExclusive(Path.Combine(output, "result.json"),
            new { schemaVersion = "P11_SERVER_EXPORT_CLEANUP_SELFTEST_V1", status = "PASS", cases,
                protectedContentRead = false, historicalPublicDownloadsRetained = true, liveLaunch = false });
        return 0;
    }
}
