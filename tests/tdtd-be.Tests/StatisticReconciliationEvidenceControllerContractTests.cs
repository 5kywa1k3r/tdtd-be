using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.Controllers;
using tdtd_be.DTOs.Auth;
using tdtd_be.Services.StatisticsReconciliation.EvidenceExport;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;

internal static class StatisticReconciliationEvidenceControllerContractTests
{
    private const string WorkId = "507f1f77bcf86cd799439011";
    private const string AssignmentId = "507f1f77bcf86cd799439012";
    private const string ReconciliationId = "507f1f77bcf86cd799439013";
    private const string ActorId = "507f1f77bcf86cd799439014";
    private const string UnitId = "507f1f77bcf86cd799439015";
    private const string ExportId =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static void Run()
    {
        RoutesAreAuthorizedProductionReadOwners();
        ListAndReadDelegateOnlyAfterCurrentScopeAuthorization();
        PermissionRemovalBlocksDownloadBeforeArtifactLookup();
    }

    private static void RoutesAreAuthorizedProductionReadOwners()
    {
        var controller = typeof(StatisticReconciliationEvidenceController);
        Require(controller.GetCustomAttribute<AuthorizeAttribute>() is not null,
            "evidence controller must require authenticated production access");
        Route(controller, nameof(StatisticReconciliationEvidenceController.ListAsync),
            "{reconciliationId}/evidence-exports");
        Route(controller, nameof(StatisticReconciliationEvidenceController.ReadAsync),
            "{reconciliationId}/evidence-exports/{exportId}");
        Route(controller, nameof(StatisticReconciliationEvidenceController.DownloadAsync),
            "{reconciliationId}/evidence-exports/{exportId}/download");
    }

    private static void ListAndReadDelegateOnlyAfterCurrentScopeAuthorization()
    {
        var owner = new CapturingOwner(CurrentAuthorization());
        var controller = Controller(owner);

        var list = controller.ListAsync(WorkId, AssignmentId,
                ReconciliationId, 1, 25, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(list is OkObjectResult && owner.ListCalls == 1,
            "authorized evidence list must delegate exactly once");

        var read = controller.ReadAsync(WorkId, AssignmentId,
                ReconciliationId, ExportId, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(read is OkObjectResult && owner.ReadCalls == 1,
            "authorized evidence readback must delegate exactly once");
        Require(owner.AuthorizeCalls == 2,
            "list and readback must independently re-authorize assignment scope");
    }

    private static void PermissionRemovalBlocksDownloadBeforeArtifactLookup()
    {
        var owner = new CapturingOwner(CurrentAuthorization());
        var controller = Controller(owner);

        var first = controller.DownloadAsync(WorkId, AssignmentId,
                ReconciliationId, ExportId, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(first is FileContentResult && owner.DownloadCalls == 1,
            "authorized evidence download must use the owner once");

        // Revocation is represented only by the existing Work/Assignment
        // permission owner returning no current scope authorization. No
        // evidence-specific revoke command exists.
        owner.Revoked = true;
        var denied = controller.DownloadAsync(WorkId, AssignmentId,
                ReconciliationId, ExportId, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(denied is EmptyResult &&
                controller.Response.StatusCode == StatusCodes.Status404NotFound,
            "permission removal must preserve opaque hidden/missing parity");
        Require(owner.AuthorizeCalls == 2 && owner.DownloadCalls == 1,
            "revoked download must stop before artifact lookup/readback");
    }

    private static StatisticReconciliationEvidenceController Controller(
        CapturingOwner owner)
    {
        var context = new DefaultHttpContext();
        context.Items[MeAccessor.MeItemKey] = new MeResponse(ActorId,
            "reviewer", "Reviewer", [], UnitId, null, null, null,
            ["ADMIN"], null, false);
        var accessor = new HttpContextAccessor { HttpContext = context };
        return new StatisticReconciliationEvidenceController(
            new MeAccessor(accessor), owner)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static StatisticReconciliationReviewScopeAuthorization
        CurrentAuthorization()
    {
        var constructor = typeof(StatisticReconciliationReviewScopeAuthorization)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        return (StatisticReconciliationReviewScopeAuthorization)
            constructor.Invoke([
                ActorId, UnitId, WorkId, AssignmentId,
                new[] { "STAT_RECONCILIATION_REVIEW" },
                new string('b', 64), 1, 1, true
            ]);
    }

    private static StatisticReconciliationEvidenceArtifactDto Artifact()
        => new(ExportId, ReconciliationId, new string('c', 64), "JSON",
            "REDACTED", "evidence.json", "application/json",
            new string('d', 64), new string('e', 64), 2,
            DateTime.UnixEpoch, DateTime.UnixEpoch.AddDays(30))
        {
            Links =
            [
                new("READBACK", "/api/evidence/readback", "GET"),
                new("DOWNLOAD", "/api/evidence/download", "GET")
            ]
        };

    private static void Route(Type controller, string methodName,
        string expected)
    {
        var method = controller.GetMethods().Single(value =>
            value.Name == methodName);
        var route = method.GetCustomAttribute<HttpGetAttribute>()?.Template;
        Require(route == expected,
            $"{methodName} route drift: expected {expected}, actual {route}");
        Require(route is not null &&
                !route.Contains("/api/testing/", StringComparison.Ordinal),
            $"{methodName} must not use a testing route");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CapturingOwner(
        StatisticReconciliationReviewScopeAuthorization authorization)
        : IStatisticReconciliationEvidenceOwner
    {
        public bool Revoked { get; set; }
        public int AuthorizeCalls { get; private set; }
        public int ListCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int DownloadCalls { get; private set; }

        public Task<StatisticReconciliationReviewScopeAuthorization?>
            AuthorizeScopeAsync(string? workId, string? scopeAssignmentId,
                MeResponse actor, CancellationToken ct = default)
        {
            AuthorizeCalls++;
            return Task.FromResult<
                StatisticReconciliationReviewScopeAuthorization?>(
                    Revoked ? null : authorization);
        }

        public Task<StatisticReconciliationEvidenceArtifactDto> CreateAsync(
            StatisticReconciliationReviewScopeAuthorization scope,
            string reconciliationId,
            StatisticReconciliationEvidenceCompileCommand command,
            CancellationToken ct = default)
            => Task.FromResult(Artifact());

        public Task<StatisticReconciliationEvidenceArtifactPageDto> ListAsync(
            StatisticReconciliationReviewScopeAuthorization scope,
            string reconciliationId, int page, int pageSize,
            CancellationToken ct = default)
        {
            ListCalls++;
            return Task.FromResult(
                new StatisticReconciliationEvidenceArtifactPageDto(
                    [Artifact()], 1, page, pageSize));
        }

        public Task<StatisticReconciliationEvidenceArtifactDto> ReadAsync(
            StatisticReconciliationReviewScopeAuthorization scope,
            string reconciliationId, string exportId,
            CancellationToken ct = default)
        {
            ReadCalls++;
            return Task.FromResult(Artifact());
        }

        public Task<StatisticReconciliationEvidenceDownload> DownloadAsync(
            StatisticReconciliationReviewScopeAuthorization scope,
            string reconciliationId, string exportId,
            CancellationToken ct = default)
        {
            DownloadCalls++;
            return Task.FromResult(new StatisticReconciliationEvidenceDownload(
                Artifact(), [0x7b, 0x7d]));
        }
    }
}
