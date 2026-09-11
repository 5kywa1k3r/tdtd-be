using System.Collections.Immutable;
using System.Reflection;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    private async Task<StatisticReconciliationActualAppendResult>
        CommitActualGenerationAsync(
            string reconciliationId,
            string workerId,
            string claimToken,
            CancellationToken cancellationToken)
    {
        var mongo = RequireMongo();
        var context = new MongoDbContext(
            Microsoft.Extensions.Options.Options.Create(new MongoOptions
        {
            ConnectionString = mongo.ConnectionString,
            Database = mongo.DatabaseName
        }));
        var run = await context.StatisticReconciliationRuns
            .Find(value => value.Id == reconciliationId)
            .SingleAsync(cancellationToken);
        HarnessAssert.True(
            run.CreatedAtUtc.Kind == DateTimeKind.Utc &&
            run.CreatedAtUtc.Ticks % TimeSpan.TicksPerMillisecond == 0,
            "P10 actual publication run timestamp is canonical UTC millisecond");
        var boundary = InvokeTrustedFinalizerFixture<
            StatisticReconciliationActualCoherentBoundary>(
            "BuildBoundary", run);
        var steps = InvokeTrustedFinalizerFixture<ImmutableArray<
            StatisticReconciliationActualCoherentCaptureStep>>(
            "BuildCaptureSteps", boundary);
        var coherent = await new
            StatisticReconciliationActualCoherentCaptureCoordinator()
            .CaptureAsync(
                run.Id,
                new P10RaceStableBoundaryReader(boundary),
                steps,
                cancellationToken);
        var publicationContext = InvokeTrustedFinalizerFixture<
            StatisticReconciliationActualPublicationContext>(
            "BuildPublicationContext", run, boundary, coherent);
        var noPublish = new P10NoopActualGenerationCas();
        var publisher = new StatisticReconciliationActualGenerationPublisher(
            new StatisticReconciliationActualObservationMongoBackend(context),
            noPublish);
        var actorFixture = Actor("admin");
        var actor = new MeResponse(
            actorFixture.Id,
            actorFixture.Username,
            actorFixture.Username,
            ["SYSTEM"],
            actorFixture.UnitId,
            null,
            null,
            null,
            [.. actorFixture.Roles],
            null,
            false,
            actorFixture.AccountKind);
        var generation = await publisher.PublishCoherentAsync(
            coherent,
            publicationContext,
            run.CreatedAtUtc,
            workerId,
            claimToken,
            actor,
            cancellationToken);
        HarnessAssert.Equal(
            1,
            noPublish.Calls,
            "P10 JOB-05 committed actual-generation staging calls");
        return generation;
    }

    private static T InvokeTrustedFinalizerFixture<T>(
        string name,
        params object[] arguments)
    {
        var method = typeof(P10TrustedFinalizerProbe)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(value => value.Name == name &&
                value.GetParameters().Length == arguments.Length);
        try
        {
            return (T)(method.Invoke(null, arguments) ??
                throw new InvalidOperationException(name + "_RETURNED_NULL"));
        }
        catch (TargetInvocationException error)
            when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }
}