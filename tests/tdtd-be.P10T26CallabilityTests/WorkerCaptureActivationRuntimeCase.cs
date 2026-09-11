using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using tdtd_be.Common.Errors;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class WorkerCaptureActivationRuntimeCase
{
    internal static void CurrentV16RollbackStopsBeforeOwnerReadAppendAndCas()
    {
        Require(
            StatisticReconciliationCapabilityActivation
                .RequiredCurrentCatalogVersion == "1.6",
            "CURRENT_ROLLBACK_VERSION");

        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            $"tdtd-p10-t26-rollback-{Guid.NewGuid():N}");
        var contentRoot = Path.Combine(fixtureRoot, "tdtd-be");
        try
        {
            var catalogDirectory = Path.Combine(
                contentRoot,
                "Contracts",
                "DynamicFormFlow");
            Directory.CreateDirectory(catalogDirectory);
            File.WriteAllText(
                Path.Combine(contentRoot, "tdtd-be.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
            File.WriteAllBytes(
                Path.Combine(
                    catalogDirectory,
                    "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json"),
                System.Text.Encoding.UTF8.GetBytes(CurrentV16Pointer));
            File.Copy(
                Path.Combine(
                    FindRepositoryRoot(),
                    "tdtd-be",
                    "Contracts",
                    "DynamicFormFlow",
                    "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json"),
                Path.Combine(
                    catalogDirectory,
                    "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json"));

            var activation = new StatisticReconciliationCapabilityActivation(
                new ConfigurationBuilder().Build(),
                new RollbackHostEnvironment(contentRoot),
                Options.Create(new MongoOptions
                {
                    ConnectionString = "mongodb://127.0.0.1:27017",
                    Database = "tdtd_p10_worker_capture_rollback"
                }));
            var evaluation = activation.EvaluateFoundation(
                StatisticReconciliationCapabilities.SourceToResultReconciliation,
                StatisticReconciliationRouteRegistry.WorkerCapture);
            Require(!evaluation.Enabled, "ROLLBACK_MUST_DISABLE_WORKER_CAPTURE");
            Require(evaluation.Binding is null, "ROLLBACK_BINDING_MUST_BE_ABSENT");
            Require(evaluation.Reason == "CATALOG_STATE_ROLLED_BACK",
                "ROLLBACK_REASON");
            Require(evaluation.RouteId ==
                    StatisticReconciliationRouteRegistry.WorkerCapture,
                "ROLLBACK_ROUTE");
            Require(evaluation.CapabilityId ==
                    StatisticReconciliationCapabilities
                        .SourceToResultReconciliation,
                "ROLLBACK_CAPABILITY");

            var ownerReads = 0;
            var p10AppendCalls = 0;
            var casCalls = 0;
            AppException? blocked = null;
            try
            {
                _ = StatisticReconciliationActualWorkerCaptureActivationGate
                    .ExecuteAsync(
                        activation,
                        _ =>
                        {
                            ownerReads++;
                            p10AppendCalls++;
                            casCalls++;
                            return Task.FromResult(true);
                        },
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (AppException error)
            {
                blocked = error;
            }

            if (blocked is null)
                throw new InvalidOperationException("ROLLBACK_MUST_FAIL_CLOSED");
            Require(blocked.Code ==
                    AppErrorCode
                        .DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE,
                "ROLLBACK_ERROR_CODE");
            Require(ownerReads == 0, "ROLLBACK_OWNER_READ_ZERO");
            Require(p10AppendCalls == 0, "ROLLBACK_P10_APPEND_ZERO");
            Require(casCalls == 0, "ROLLBACK_CAS_ZERO");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private const string CurrentV16Pointer =
        "{\n" +
        "  \"pointerVersion\": 1,\n" +
        "  \"catalogVersion\": \"1.6\",\n" +
        "  \"catalogSha256\": \"39cdb98dda168f5901f48a94640fe5d50943c5bd78ed32d5e05e8b719b23d13b\",\n" +
        "  \"schemaSha256\": \"da0c80f265845f24aaf282e0a0369272273b1986520dae07171cda85b28b3fed\"\n" +
        "}\n";

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(
                        directory.FullName,
                        "tdtd-be",
                        "tdtd-be.csproj")))
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }
        }
        throw new InvalidOperationException("REPOSITORY_ROOT_NOT_FOUND");
    }

    private static void Require(bool condition, string code)
    {
        if (!condition)
            throw new InvalidOperationException(code);
    }

    private sealed class RollbackHostEnvironment(string contentRootPath)
        : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } =
            "tdtd-be.P10T26CallabilityTests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}

internal sealed class WorkerCaptureEnabledTestActivation
    : IStatisticReconciliationCandidateActivation
{
    internal int RequireFoundationCalls { get; private set; }

    public StatisticReconciliationCandidateBinding RequireFoundation(
        string capabilityId,
        string routeId)
    {
        if (capabilityId !=
                StatisticReconciliationCapabilities.SourceToResultReconciliation ||
            routeId != StatisticReconciliationRouteRegistry.WorkerCapture)
        {
            throw new InvalidOperationException(
                "ENDPOINT_ACTIVATION_ROUTE_OR_CAPABILITY");
        }

        RequireFoundationCalls++;
        return null!;
    }

    public StatisticReconciliationCandidateEvaluation EvaluateFoundation(
        string capabilityId,
        string routeId)
        => throw new NotSupportedException();

    public StatisticReconciliationCandidateBinding RequireCapability(
        string capabilityId,
        string routeId)
        => throw new NotSupportedException();

    public StatisticReconciliationCandidateEvaluation EvaluateCapability(
        string capabilityId,
        string routeId)
        => throw new NotSupportedException();
}
