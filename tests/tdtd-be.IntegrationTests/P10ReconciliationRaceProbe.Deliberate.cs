using MongoDB.Driver;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationRaceProbe
{
    private async Task<int> ExecuteDeliberateWrongLedgerAsync(
        CancellationToken ct)
    {
        var cleanupErrors = new List<string>();
        var counter = new P10WrongLedgerCountingBackend();
        var generation = BuildExpectedGeneration();
        var first = generation.Atoms[0];
        var wrong = generation with
        {
            Atoms =
            [
                first with { CanonicalValue = first.CanonicalValue + "-wrong" },
                .. generation.Atoms.Skip(1)
            ]
        };
        string? rejectionReason = null;
        string? rejectionDetail = null;
        var rejected = false;
        try
        {
            var validator = new StatisticReconciliationExpectedObservationStore(
                counter,
                new StatisticReconciliationExpectedMetricIdentityCompiler());
            _ = await validator.AppendGenerationAsync(wrong, FixedUtc, ct);
        }
        catch (StatisticReconciliationExpectedObservationException error)
            when (error.ReasonCode ==
                  StatisticReconciliationExpectedObservationFailureReasons
                      .GenerationInvalid &&
                  string.Equals(error.Message,
                      "Atom semantic hash mismatch.",
                      StringComparison.Ordinal))
        {
            rejected = true;
            rejectionReason = error.ReasonCode;
            rejectionDetail = error.Message;
        }

        var preIo = counter.Reads == 0 && counter.ContentWrites == 0 &&
                    counter.CommitWrites == 0;
        var recovered = false;
        var replayed = false;
        long cleanupDryRun = -1;
        long cleanupApplied = -1;
        long cleanupSecondDryRun = -1;
        try
        {
            if (!rejected || !preIo)
                throw new InvalidOperationException(
                    "P10_RACE_WRONG_LEDGER_NOT_REJECTED_PRE_IO");
            await StartInfrastructureAsync(includeBackend: false, ct);
            var store = new StatisticReconciliationExpectedObservationStore(
                new StatisticReconciliationExpectedObservationMongoBackend(
                    Context()),
                new StatisticReconciliationExpectedMetricIdentityCompiler());
            var appended = await store.AppendGenerationAsync(
                generation, FixedUtc, ct);
            var replay = await store.AppendGenerationAsync(
                generation, FixedUtc, ct);
            recovered = !appended.ExactReplay && appended.DocumentCount > 0;
            replayed = replay.ExactReplay &&
                       replay.ManifestSha256 == appended.ManifestSha256;

            var filter = Builders<StatisticReconciliationObservation>.Filter.And(
                Builders<StatisticReconciliationObservation>.Filter.Eq(
                    value => value.ReconciliationId,
                    generation.ContextPin.ReconciliationId),
                Builders<StatisticReconciliationObservation>.Filter.Eq(
                    value => value.GenerationId,
                    generation.GenerationId));
            cleanupDryRun = await Context().StatisticReconciliationObservations
                .CountDocumentsAsync(filter, cancellationToken: ct);
            cleanupApplied = (await Context().StatisticReconciliationObservations
                .DeleteManyAsync(filter, ct)).DeletedCount;
            cleanupSecondDryRun = await Context().StatisticReconciliationObservations
                .CountDocumentsAsync(filter, cancellationToken: ct);
        }
        catch (Exception error)
        {
            cleanupErrors.Add("control:" + error.Message);
        }
        finally
        {
            await StopAndCleanAsync(cleanupErrors);
        }

        var cleanupComplete = cleanupErrors.Count == 0 &&
            cleanupDryRun > 0 && cleanupApplied == cleanupDryRun &&
            cleanupSecondDryRun == 0 &&
            _mongo is
            {
                DatabaseDropVerified: true,
                ProcessStopVerified: true,
                PortReleaseVerified: true,
                DataDirectoryRemovalVerified: true
            };
        var control = new
        {
            schemaVersion = "P10_RACE_WRONG_LEDGER_CONTROL_V1",
            rootId = _rootId,
            fixedUtc = FixedUtc,
            productDetector = nameof(
                StatisticReconciliationExpectedObservationStore),
            rejectionReason,
            rejectionDetail,
            rejectedBeforeIo = rejected && preIo,
            backend = new
            {
                reads = counter.Reads,
                contentWrites = counter.ContentWrites,
                commitWrites = counter.CommitWrites
            },
            recovery = new
            {
                validGenerationAppended = recovered,
                exactReplay = replayed
            },
            cleanup = new
            {
                dryRunCount = cleanupDryRun,
                appliedCount = cleanupApplied,
                secondDryRunCount = cleanupSecondDryRun,
                complete = cleanupComplete
            },
            errors = cleanupErrors
        };
        var controlPath = Path.Combine(
            _artifactRoot, "P10-RACE.wrong-ledger-control.json");
        await WriteNewJsonAsync(controlPath, control, CancellationToken.None);
        if (!rejected || !preIo || !recovered || !replayed || !cleanupComplete)
        {
            Console.Error.WriteLine(
                "P10_RACE_DELIBERATE_WRONG_LEDGER_CONTROL_FAILED " +
                $"reason={rejectionReason ?? "none"} reads={counter.Reads} " +
                $"writes={counter.ContentWrites + counter.CommitWrites} " +
                $"recovered={recovered} replayed={replayed} cleanup={cleanupComplete}");
            return 1;
        }

        Console.Error.WriteLine(
            $"P10_RACE_WRONG_LEDGER_CONTROL errorCode={rejectionReason} " +
            $"detailSha256={Sha(rejectionDetail!)} reads=0 writes=0 recovered=true " +
            "replayed=true secondDryRun=0 cleanup=true " +
            $"control={RelativeWorkspace(controlPath)}");
        Console.Error.WriteLine(
            "P10_RACE_DELIBERATE_WRONG_LEDGER_FAILED " +
            "reason=EXPECTED_SEMANTIC_HASH_MISMATCH exitCode=42 " +
            "cleanup=true recovered=true");
        return 42;
    }
}

internal sealed class P10WrongLedgerCountingBackend
    : IStatisticReconciliationExpectedObservationBackend
{
    internal int Reads { get; private set; }
    internal int ContentWrites { get; private set; }
    internal int CommitWrites { get; private set; }

    public Task<IReadOnlyList<StatisticReconciliationExpectedStoredObservation>>
        ReadGenerationAsync(string reconciliationId, string generationId,
            CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult<IReadOnlyList<
            StatisticReconciliationExpectedStoredObservation>>([]);
    }

    public Task AppendContentAsync(
        IReadOnlyList<StatisticReconciliationObservation> observations,
        CancellationToken cancellationToken)
    {
        ContentWrites += observations.Count;
        return Task.CompletedTask;
    }

    public Task AppendCommitAsync(
        StatisticReconciliationObservation observation,
        CancellationToken cancellationToken)
    {
        CommitWrites++;
        return Task.CompletedTask;
    }
}
