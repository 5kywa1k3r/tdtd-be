using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tdtd_be.IntegrationTests;

internal sealed record HarnessPaths(
    string WorkspaceRoot,
    string BackendRoot,
    string IntegrationRoot,
    string RunRoot)
{
    public static HarnessPaths Create(string runKey)
    {
        var backendRoot = FindBackendRoot();
        var workspaceRoot = Directory.GetParent(backendRoot)?.FullName
            ?? throw new InvalidOperationException("Cannot resolve workspace root.");
        var configuredArtifactRoot = Environment.GetEnvironmentVariable("TDTD_TEST_ARTIFACT_ROOT");
        var integrationRoot = string.IsNullOrWhiteSpace(configuredArtifactRoot)
            ? Path.Combine(workspaceRoot, ".p1-artifacts", "backend-integration")
            : Path.GetFullPath(configuredArtifactRoot);
        var runRoot = Path.Combine(integrationRoot, runKey);
        Directory.CreateDirectory(runRoot);
        return new HarnessPaths(workspaceRoot, backendRoot, integrationRoot, runRoot);
    }

    public static HarnessPaths CreateP8(
        string runKey,
        string chainId,
        string promptId)
    {
        var backendRoot = FindBackendRoot();
        var workspaceRoot = Directory.GetParent(backendRoot)?.FullName
            ?? throw new InvalidOperationException("Cannot resolve workspace root.");
        var configuredArtifactRoot = Environment.GetEnvironmentVariable(
            "TDTD_P8_ARTIFACT_ROOT");
        var p8Root = string.IsNullOrWhiteSpace(configuredArtifactRoot)
            ? Path.Combine(workspaceRoot, ".p8-artifacts", "test-runs")
            : Path.GetFullPath(configuredArtifactRoot);
        var integrationRoot = Path.Combine(
            p8Root,
            RequireSafePathSegment(chainId, nameof(chainId)),
            RequireSafePathSegment(promptId, nameof(promptId)));
        var runRoot = Path.Combine(
            integrationRoot,
            RequireSafePathSegment(runKey, nameof(runKey)));
        Directory.CreateDirectory(runRoot);
        return new HarnessPaths(
            workspaceRoot,
            backendRoot,
            integrationRoot,
            runRoot);
    }

    public static HarnessPaths CreateP9(
        string runKey,
        string chainId,
        string promptId)
    {
        var backendRoot = FindBackendRoot();
        var workspaceRoot = Directory.GetParent(backendRoot)?.FullName
            ?? throw new InvalidOperationException("Cannot resolve workspace root.");
        var configuredArtifactRoot = Environment.GetEnvironmentVariable(
            "TDTD_P9_ARTIFACT_ROOT");
        var p9Root = string.IsNullOrWhiteSpace(configuredArtifactRoot)
            ? Path.Combine(workspaceRoot, ".p9-artifacts", "runs")
            : Path.GetFullPath(configuredArtifactRoot);
        var integrationRoot = Path.Combine(
            p9Root,
            RequireSafePathSegment(chainId, nameof(chainId)),
            RequireSafePathSegment(promptId, nameof(promptId)));
        var runRoot = Path.Combine(
            integrationRoot,
            RequireSafePathSegment(runKey, nameof(runKey)));
        Directory.CreateDirectory(runRoot);
        return new HarnessPaths(
            workspaceRoot,
            backendRoot,
            integrationRoot,
            runRoot);
    }

    public static HarnessPaths CreateP10(
        string runKey,
        string chainId,
        string promptId)
    {
        var backendRoot = FindBackendRoot();
        var workspaceRoot = Directory.GetParent(backendRoot)?.FullName
            ?? throw new InvalidOperationException("Cannot resolve workspace root.");
        var configuredArtifactRoot = Environment.GetEnvironmentVariable(
            "TDTD_P10_ARTIFACT_ROOT");
        var p10Root = string.IsNullOrWhiteSpace(configuredArtifactRoot)
            ? Path.Combine(workspaceRoot, ".p10-artifacts", "runs")
            : Path.GetFullPath(configuredArtifactRoot);
        var integrationRoot = Path.Combine(
            p10Root,
            RequireSafePathSegment(chainId, nameof(chainId)),
            RequireSafePathSegment(promptId, nameof(promptId)));
        var runRoot = Path.Combine(
            integrationRoot,
            RequireSafePathSegment(runKey, nameof(runKey)));
        Directory.CreateDirectory(runRoot);
        return new HarnessPaths(
            workspaceRoot,
            backendRoot,
            integrationRoot,
            runRoot);
    }

    public static HarnessPaths CreateP11(
        string runKey,
        string chainId,
        string promptId)
    {
        var backendRoot = FindBackendRoot();
        var workspaceRoot = Directory.GetParent(backendRoot)?.FullName
            ?? throw new InvalidOperationException("Cannot resolve workspace root.");
        var configuredArtifactRoot = Environment.GetEnvironmentVariable(
            "TDTD_P11_ARTIFACT_ROOT");
        var p11Root = string.IsNullOrWhiteSpace(configuredArtifactRoot)
            ? Path.Combine(workspaceRoot, ".p11-artifacts", "runs")
            : Path.GetFullPath(configuredArtifactRoot);
        var integrationRoot = Path.Combine(
            p11Root,
            RequireSafePathSegment(chainId, nameof(chainId)),
            RequireSafePathSegment(promptId, nameof(promptId)));
        var runRoot = Path.Combine(
            integrationRoot,
            RequireSafePathSegment(runKey, nameof(runKey)));
        Directory.CreateDirectory(runRoot);
        return new HarnessPaths(
            workspaceRoot,
            backendRoot,
            integrationRoot,
            runRoot);
    }

    public string IterationRoot(int iteration)
    {
        var path = Path.Combine(RunRoot, $"iteration-{iteration:00}");
        Directory.CreateDirectory(path);
        return path;
    }

    public string ResolveBackendDll()
    {
        var configured = Environment.GetEnvironmentVariable("TDTD_TEST_BACKEND_DLL");
        if (!string.IsNullOrWhiteSpace(configured))
            return RequireBackendDll(configured);

        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var preferred = Path.Combine(BackendRoot, "bin", configuration, "net8.0", "tdtd-be.dll");
        if (File.Exists(preferred) && File.Exists(Path.ChangeExtension(preferred, ".runtimeconfig.json")))
            return preferred;

        var candidates = Directory.Exists(Path.Combine(BackendRoot, "bin"))
            ? Directory.EnumerateFiles(Path.Combine(BackendRoot, "bin"), "tdtd-be.dll", SearchOption.AllDirectories)
                .Where(x => File.Exists(Path.ChangeExtension(x, ".runtimeconfig.json")))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray()
            : [];
        if (candidates.Length > 0)
            return candidates[0];

        throw new FileNotFoundException(
            "tdtd-be.dll with runtimeconfig was not found. Build tdtd-be before running the integration harness.");
    }

    public static string ResolveMongodPath()
    {
        foreach (var variable in new[] { "TDTD_TEST_MONGOD_PATH", "TDTD_MONGOD_PATH" })
        {
            var configured = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return Path.GetFullPath(configured);
        }

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var version in new[] { "7.0", "6.0" })
            {
                var candidate = Path.Combine(programFiles, "MongoDB", "Server", version, "bin", "mongod.exe");
                if (File.Exists(candidate))
                    return candidate;
            }

            var serverRoot = Path.Combine(programFiles, "MongoDB", "Server");
            if (Directory.Exists(serverRoot))
            {
                var candidate = Directory.EnumerateFiles(serverRoot, "mongod.exe", SearchOption.AllDirectories)
                    .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (candidate is not null)
                    return candidate;
            }
        }

        var executableName = OperatingSystem.IsWindows() ? "mongod.exe" : "mongod";
        foreach (var segment in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(segment, executableName);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "mongod was not found. Set TDTD_TEST_MONGOD_PATH to a MongoDB 6/7 server executable.");
    }

    private static string FindBackendRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                var direct = Path.Combine(current.FullName, "tdtd-be.csproj");
                if (File.Exists(direct))
                    return current.FullName;

                var nested = Path.Combine(current.FullName, "tdtd-be", "tdtd-be.csproj");
                if (File.Exists(nested))
                    return Path.Combine(current.FullName, "tdtd-be");

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate tdtd-be.csproj.");
    }

    private static string RequireSafePathSegment(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                "Artifact path segment is invalid.",
                name);
        }

        return value;
    }

    private static string RequireBackendDll(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("Configured backend DLL does not exist.", path);
        if (!File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")))
            throw new FileNotFoundException("Configured backend runtimeconfig does not exist.", Path.ChangeExtension(path, ".runtimeconfig.json"));
        return path;
    }
}

internal static class EvidenceCsv
{
    public static async Task WriteCasesAsync(
        string path,
        IEnumerable<(int Iteration, HarnessCaseResult Case)> rows,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("iteration,caseId,verdict,durationMs,fingerprint,detail");
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Join(",",
                row.Iteration.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Escape(row.Case.CaseId),
                Escape(row.Case.Verdict.ToString()),
                row.Case.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Escape(row.Case.Fingerprint),
                Escape(row.Case.Detail)));
        }
        await writer.FlushAsync(ct);
    }

    private static string Escape(string value)
        => $"\"{value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ")}\"";
}

internal static class EvidenceJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task WriteAsync(string path, object value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, Options, ct);
        await stream.FlushAsync(ct);
    }

    public static object EnvironmentSnapshot(
        string runKey,
        string mongodPath,
        string backendDll,
        int iterations)
        => new
        {
            runKey,
            generatedAtUtc = DateTime.UtcNow,
            operatingSystem = RuntimeInformation.OSDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            dotnetVersion = Environment.Version.ToString(),
            machineName = Environment.MachineName,
            mongodPath,
            backendDll,
            iterations,
            isolation = new
            {
                mongoProcessPerIteration = true,
                mongoReplicaSet = true,
                databasePerIteration = true,
                hangfireServerEnabled = false,
                hangfireDashboardEnabled = false,
                hangfireRecurringRegistrationEnabled = false,
                redisEnabled = false
            }
        };
}
