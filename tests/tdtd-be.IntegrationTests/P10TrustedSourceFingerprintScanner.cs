using System.Security.Cryptography;
using System.Text;

namespace tdtd_be.IntegrationTests;

internal static class P10TrustedSourceFingerprintScanner
{
    internal const string SchemaVersion =
        "P10_TRUSTED_SOURCE_FINGERPRINT_DOTNET_ORDINAL_V1";

    private static readonly string[] SourceRoots =
    [
        "scripts",
        "tdtd-be",
        "tdtd-fe",
        "docs/features/p10-reconcile"
    ];

    private static readonly HashSet<string> ExcludedSegments =
        new(StringComparer.Ordinal)
        {
            "bin",
            "obj",
            "node_modules",
            "dist",
            "coverage",
            "artifacts",
            ".build",
            ".runtime",
            ".p10-artifacts",
            "App_Data",
            ".tmp",
            ".vs"
        };

    private static readonly HashSet<string> IncludedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".csproj",
            ".json",
            ".js",
            ".mjs",
            ".ps1",
            ".ts",
            ".tsx",
            ".md"
        };

    internal static P10CloseoutSourceFingerprint Capture(
        string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new InvalidOperationException(
                "P10 trusted-source workspace root is required.");
        }

        var root = Path.GetFullPath(workspaceRoot);
        RequireRealDirectory(root, "workspace root");

        var relativeFiles = new List<string>();
        foreach (var sourceRoot in SourceRoots)
        {
            RequireRealDirectoryPathComponents(root, sourceRoot);
            VisitDirectory(root, sourceRoot, relativeFiles);
        }

        var orderedPaths = relativeFiles
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (orderedPaths.Distinct(StringComparer.Ordinal).Count() !=
            orderedPaths.Length)
        {
            throw new InvalidOperationException(
                "P10 trusted-source scan produced duplicate paths.");
        }

        var files = orderedPaths
            .Select(path => CaptureFile(root, path))
            .ToArray();
        var fingerprint = new P10CloseoutSourceFingerprint(
            SchemaVersion,
            files.Length,
            files,
            SemanticSha256(files));
        if (!IsWellFormed(fingerprint))
        {
            throw new InvalidOperationException(
                "P10 trusted-source fingerprint is not canonical.");
        }

        return fingerprint;
    }

    internal static bool AreEqual(
        P10CloseoutSourceFingerprint? left,
        P10CloseoutSourceFingerprint? right)
        => left is not null &&
            right is not null &&
            IsWellFormed(left) &&
            IsWellFormed(right) &&
            left.SchemaVersion == right.SchemaVersion &&
            left.FileCount == right.FileCount &&
            left.Sha256 == right.Sha256 &&
            left.Files.SequenceEqual(right.Files);

    internal static bool IsWellFormed(
        P10CloseoutSourceFingerprint value)
    {
        if (value.SchemaVersion != SchemaVersion ||
            value.FileCount != value.Files.Count ||
            value.FileCount <= 0 ||
            !IsSha256(value.Sha256))
        {
            return false;
        }

        string? previous = null;
        foreach (var file in value.Files)
        {
            if (!IsCanonicalRelativePath(file.Path) ||
                !IsSha256(file.Sha256) ||
                file.Bytes < 0 ||
                previous is not null &&
                    StringComparer.Ordinal.Compare(previous, file.Path) >= 0)
            {
                return false;
            }

            previous = file.Path;
        }

        return value.Sha256 == SemanticSha256(value.Files);
    }

    internal static bool IsExcludedRelativePath(string relative)
    {
        if (!IsCanonicalRelativePath(relative))
        {
            throw new InvalidOperationException(
                $"P10 trusted-source path is not canonical: {relative}");
        }

        foreach (var segment in relative.Split('/'))
        {
            if (ExcludedSegments.Contains(segment) ||
                IsNumberedArtifactSegment(segment))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasIncludedExtension(string relative)
        => IncludedExtensions.Contains(Path.GetExtension(relative));

    private static void VisitDirectory(
        string root,
        string currentRelative,
        ICollection<string> files)
    {
        var current = ResolveUnderRoot(root, currentRelative);
        RequireRealDirectory(current, currentRelative);
        var entries = Directory.EnumerateFileSystemEntries(current)
            .OrderBy(
                entry => Path.GetFileName(entry),
                StringComparer.Ordinal)
            .ToArray();

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrEmpty(name))
            {
                throw new InvalidOperationException(
                    $"P10 trusted-source entry has no name: {entry}");
            }

            var childRelative = currentRelative + "/" + name;
            if (IsExcludedRelativePath(childRelative))
            {
                continue;
            }

            var expected = ResolveUnderRoot(root, childRelative);
            if (!string.Equals(
                    Path.GetFullPath(entry),
                    expected,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"P10 trusted-source entry escaped its root: {childRelative}");
            }

            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"P10 trusted-source reparse entry is forbidden: {childRelative}");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                VisitDirectory(root, childRelative, files);
                continue;
            }

            if (HasIncludedExtension(childRelative))
            {
                files.Add(childRelative);
            }
        }
    }

    private static P10CloseoutSourceFilePin CaptureFile(
        string root,
        string relative)
    {
        var fullPath = ResolveUnderRoot(root, relative);
        var before = File.GetAttributes(fullPath);
        if ((before & (FileAttributes.Directory |
                FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(
                $"P10 trusted-source file is not a real regular file: {relative}");
        }

        var bytes = File.ReadAllBytes(fullPath);
        var after = File.GetAttributes(fullPath);
        if ((after & (FileAttributes.Directory |
                FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(
                $"P10 trusted-source file changed type while read: {relative}");
        }

        return new P10CloseoutSourceFilePin(
            relative,
            RawSha256(bytes),
            bytes.LongLength);
    }

    private static string ResolveUnderRoot(
        string root,
        string relative)
    {
        if (!IsCanonicalRelativePath(relative))
        {
            throw new InvalidOperationException(
                $"P10 trusted-source path is not canonical: {relative}");
        }

        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"P10 trusted-source path escaped the workspace: {relative}");
        }

        return fullPath;
    }

    private static void RequireRealDirectory(
        string fullPath,
        string context)
    {
        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException(
                $"P10 trusted-source directory is missing: {context}");
        }

        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"P10 trusted-source directory must be real: {context}");
        }
    }

    private static void RequireRealDirectoryPathComponents(
        string root,
        string relative)
    {
        var currentRelative = string.Empty;
        foreach (var segment in relative.Split('/'))
        {
            currentRelative = currentRelative.Length == 0
                ? segment
                : currentRelative + "/" + segment;
            RequireRealDirectory(
                ResolveUnderRoot(root, currentRelative),
                currentRelative);
        }
    }

    private static bool IsCanonicalRelativePath(string value)
        => !string.IsNullOrWhiteSpace(value) &&
            !Path.IsPathRooted(value) &&
            !value.StartsWith("/", StringComparison.Ordinal) &&
            !value.EndsWith("/", StringComparison.Ordinal) &&
            !value.Contains('\\') &&
            value.Split('/').All(segment =>
                segment.Length > 0 && segment != "." && segment != "..");

    private static bool IsNumberedArtifactSegment(string segment)
        => segment.Length == 13 &&
            segment[0] == '.' &&
            segment[1] == 'p' &&
            segment[2] is >= '1' and <= '9' &&
            segment[3..] == "-artifacts";

    private static string SemanticSha256(
        IReadOnlyList<P10CloseoutSourceFilePin> files)
        => RawSha256(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            files.Select(file => $"{file.Path}\n{file.Sha256}"))));

    private static string RawSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed record P10CloseoutSourceFilePin(
    string Path,
    string Sha256,
    long Bytes);

internal sealed record P10CloseoutSourceFingerprint(
    string SchemaVersion,
    int FileCount,
    IReadOnlyList<P10CloseoutSourceFilePin> Files,
    string Sha256);
