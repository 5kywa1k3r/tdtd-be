using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class RuntimeSourceAudit
{
    public static void Run(string repoRoot)
    {
        var beRoot = Path.GetFullPath(Path.Combine(repoRoot, "tdtd-be"));
        var pdb = Path.Combine(beRoot, "bin/Debug/net8.0/tdtd-be.pdb");
        using var input = File.OpenRead(pdb);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(input);
        var reader = provider.GetMetadataReader();
        using var dll = File.OpenRead(Path.Combine(beRoot, "bin/Debug/net8.0/tdtd-be.dll"));
        using var pe = new PEReader(dll);
        var debugEntry = pe.ReadDebugDirectory().Single(x => x.Type == DebugDirectoryEntryType.CodeView);
        var pdbMatchesAssembly = pe.ReadCodeViewDebugDirectoryData(debugEntry).Guid
            == new Guid(reader.DebugMetadataHeader!.Id.Take(16).ToArray());
        if (!pdbMatchesAssembly) throw new Exception("Runtime sibling PDB does not match its assembly");
        var mismatches = new List<string>();
        var liveDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checkedCount = 0;
        foreach (var handle in reader.Documents)
        {
            var document = reader.GetDocument(handle);
            var path = Path.GetFullPath(reader.GetString(document.Name));
            if (!path.StartsWith(beRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) || path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)) continue;
            liveDocuments.Add(path);
            if (!File.Exists(path)) { mismatches.Add(Path.GetRelativePath(beRoot, path) + " (missing)"); continue; }
            var expected = reader.GetBlobBytes(document.Hash);
            var content = File.ReadAllBytes(path);
            var current = expected.Length == 32 ? SHA256.HashData(content) : SHA1.HashData(content);
            checkedCount++;
            if (!expected.SequenceEqual(current)) mismatches.Add(Path.GetRelativePath(beRoot, path));
        }
        var currentPdb = Path.Combine(repoRoot, "outputs/recipient-scope-20261006/test-artifacts/bin/tdtd-be/debug/tdtd-be.pdb");
        using var currentInput = File.OpenRead(currentPdb);
        using var currentProvider = MetadataReaderProvider.FromPortablePdbStream(currentInput);
        var currentReader = currentProvider.GetMetadataReader();
        var addedDocuments = currentReader.Documents.Select(handle => Path.GetFullPath(currentReader.GetString(currentReader.GetDocument(handle).Name)))
            .Where(path => path.StartsWith(beRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) && !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
                && !liveDocuments.Contains(path)).Select(path => Path.GetRelativePath(beRoot, path)).ToList();
        var json = new { pdbMatchesAssembly, checkedCount, mismatches, addedDocuments, note = "Current source compared to sibling portable PDB document checksums. No runtime mutation." };
        File.WriteAllText(Path.Combine(repoRoot, "outputs/recipient-scope-20261006/runtime-source-audit.json"),
            JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Runtime sibling PDB: {checkedCount} source checksums read, {mismatches.Count} differing/missing source files. No runtime mutation.");
    }
}
