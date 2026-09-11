using System.Security.Cryptography;
using MongoDB.Driver;
using tdtd_be.Models.Statistics;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    public const string EffectiveMappingDirectRegressionCommandLineSwitch =
        "--p9-reconciled-mapping-direct-regression";

    public static async Task<int> RunEffectiveMappingDirectRegressionAsync()
    {
        var runKey =
            $"p9_reconciled_mapping_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.CreateP10(
            runKey,
            ChainId,
            "P9-RECONCILED-MAPPING-REGRESSION");
        return await new P10ReconciliationCoreProbe(paths, runKey)
            .ExecuteEffectiveMappingDirectRegressionAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteEffectiveMappingDirectRegressionAsync(
        CancellationToken ct)
    {
        var cleanupErrors = new List<string>();
        Exception? failure = null;
        P10ProductionDirectFixturePins? publication = null;
        try
        {
            var jwtSigningKey = Convert.ToBase64String(
                RandomNumberGenerator.GetBytes(48));
            RememberSecret(jwtSigningKey);
            _mongo = await MongoReplicaSetLease.StartP10Async(
                _paths,
                _iterationRoot,
                _runKey,
                1,
                ct);
            _database = _mongo.Client.GetDatabase(_mongo.DatabaseName);
            _backend = await BackendServerLease.StartAsync(
                _paths,
                Path.Combine(_iterationRoot, "production-bootstrap"),
                _runKey,
                _mongo,
                ct,
                CloseoutBackendOptions(jwtSigningKey));
            _api = new ApiHarnessClient(_backend.BaseUri);

            await BootstrapAndSeedFixtureAsync(ct);
            await RestartCloseoutBackendWithActualApiOwnerAsync(
                jwtSigningKey,
                ct);
            await AwaitInfrastructureAsync(ct);

            publication = await PrepareProductionDirectLifecycleFixtureAsync(
                ct,
                pinMappingToEffectiveVersion: true,
                markMappingReceiptReconciled: true);
            var receipt = await RequireDatabase()
                .GetCollection<tdtd_be.Models.DynamicFlowMappingApplyReceipt>(
                    "dynamic_flow_mapping_apply_receipts")
                .Find(item => item.Id == publication.MappingReceiptId)
                .SingleAsync(ct);
            HarnessAssert.Equal(
                tdtd_be.Models.DynamicFlowMappingApplyStates.Reconciled,
                receipt.State,
                "reconciled mapping regression terminal receipt state");
            HarnessAssert.True(
                receipt.ReconciledAtUtc.HasValue,
                "reconciled mapping regression reconciled receipt timestamp");

            var job = await RequireDatabase()
                .GetCollection<WorkReportStatisticRebuildJob>(
                    P10ProductionLifecycleJobCollection)
                .Find(item => item.Id == publication.P9RunId && !item.IsDeleted)
                .SingleAsync(ct);
            var source = job.FlowContributionSources.Single();
            HarnessAssert.Equal(
                publication.FlowTemplateVersionId,
                source.MappingFlowVersionId,
                "reconciled mapping regression RuntimePin flow version");
            HarnessAssert.True(
                !string.Equals(
                    publication.FlowContributionOriginVersionId,
                    source.MappingFlowVersionId,
                    StringComparison.Ordinal),
                "reconciled mapping regression must distinguish V2 from origin V1");
            HarnessAssert.Equal(
                WorkReportStatisticRebuildJobStatuses.Completed,
                job.Status,
                "reconciled mapping regression publication status");
        }
        catch (Exception exception)
        {
            failure = exception;
            Console.Error.WriteLine(exception);
        }
        finally
        {
            await StopBackendAsync(cleanupErrors);
            await CleanupMongoAsync(cleanupErrors);
        }

        var cleanupSucceeded = cleanupErrors.Count == 0 &&
            _backend is { StopVerified: true, PortReleaseVerified: true } &&
            _mongo is
            {
                DatabaseDropVerified: true,
                ProcessStopVerified: true,
                PortReleaseVerified: true,
                DataDirectoryRemovalVerified: true
            };
        if (cleanupErrors.Count > 0)
            Console.Error.WriteLine("cleanup=" + string.Join(" | ", cleanupErrors));

        var passed = failure is null && cleanupSucceeded && publication is not null;
        Console.WriteLine(
            passed
                ? $"PASS P9 reconciled-mapping direct regression; run={publication!.P9RunId}; effective={publication.FlowTemplateVersionId}; origin={publication.FlowContributionOriginVersionId}; artifacts={_paths.RunRoot}"
                : $"FAIL P9 reconciled-mapping direct regression; error={failure?.GetType().Name}:{failure?.Message}; cleanup={cleanupSucceeded}; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }
}