using System.Text;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private Task WriteP808CaseContractAsync(CancellationToken ct)
        => EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-ops-case-contract.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                exactCaseCount = P808OwnedCaseContracts.Length,
                exactCaseIds = P808OwnedCaseContracts
                    .Select(item => item.CaseId)
                    .ToArray(),
                contracts = P808OwnedCaseContracts
            },
            ct);

    private async Task WriteP808SourceFreezeAsync(CancellationToken ct)
    {
        var integrationDirectory = Path.Combine(
            _paths.BackendRoot,
            "tests",
            "tdtd-be.IntegrationTests");
        var statisticsConfigurationServicesDirectory = Path.Combine(
            _paths.BackendRoot,
            "Services",
            "StatisticsConfiguration");
        var sourcePaths = new[]
            {
                "Program.cs",
                Path.Combine(
                    "Common",
                    "Errors",
                    "AppErrorCode.cs"),
                Path.Combine(
                    "Common",
                    "Errors",
                    "AppErrorCatalog.cs"),
                Path.Combine(
                    "Data",
                    "Infrastructure",
                    "MongoOptions.cs"),
                Path.Combine("Data", "MongoDbContext.cs"),
                Path.Combine(
                    "Controllers",
                    "StatConfigReadinessController.cs"),
                Path.Combine(
                    "Controllers",
                    "StatConfigReadinessAdminController.cs"),
                Path.Combine(
                    "DTOs",
                    "StatisticsConfiguration",
                    "StatConfigOperationsDtos.cs"),
                Path.Combine(
                    "Models",
                    "StatisticsConfiguration",
                    "StatConfigValidationJob.cs"),
                Path.Combine(
                    "Models",
                    "StatisticsConfiguration",
                    "StatConfigCommandReceipt.cs"),
                Path.Combine(
                    "Services",
                    "StatisticsConfiguration",
                    "IStatConfigOperationsService.cs"),
                Path.Combine(
                    "Services",
                    "StatisticsConfiguration",
                    "StatConfigCanonicalJson.cs"),
                Path.Combine(
                    "Services",
                    "StatisticsConfiguration",
                    "StatConfigTransactionRunner.cs"),
                Path.Combine(
                    "Services",
                    "StatisticsConfiguration",
                    "StatConfigOperationsService.cs"),
                Path.Combine(
                    "Data",
                    "Indexes",
                    "MongoIndexInitializer.cs"),
                Path.Combine(
                    "tests",
                    "tdtd-be.IntegrationTests",
                    "BackendServerLease.cs"),
                Path.Combine(
                    "tests",
                    "tdtd-be.IntegrationTests",
                    "P8StatConfigProbe.cs")
            }
            .Select(relative => Path.Combine(_paths.BackendRoot, relative))
            .Concat(Directory.EnumerateFiles(
                statisticsConfigurationServicesDirectory,
                "StatConfigOperationsService*.cs",
                SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(
                integrationDirectory,
                "P8StatConfigProbe.P808*.cs",
                SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        HarnessAssert.True(
            sourcePaths.All(File.Exists),
            "P8-08 source-freeze set contains a missing source file.");
        var rows = new List<object>(sourcePaths.Length);
        var canonical = new StringBuilder();
        foreach (var path in sourcePaths)
        {
            var relativePath = Path.GetRelativePath(
                    _paths.BackendRoot,
                    path)
                .Replace('\\', '/');
            var sha256 = Sha256(await File.ReadAllBytesAsync(path, ct));
            rows.Add(new { relativePath, sha256 });
            canonical.Append(relativePath).Append(':').Append(sha256)
                .Append('\n');
        }

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-ops-source-freeze.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                frozenBeforeFirstOwnedMutation = true,
                sourceSetSha256 = Sha256(
                    Encoding.UTF8.GetBytes(canonical.ToString())),
                sourceFiles = rows,
                faultConfiguration = new
                {
                    environment = "Testing",
                    maxActiveJobsPerActor = 1,
                    commandIdPrefix = P808FaultCommandPrefix,
                    points = P808OperationsFaultPoints,
                    oneShotIdentity = "commandId + faultPoint"
                }
            },
            ct);
    }
}

