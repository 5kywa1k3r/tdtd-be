using System.Text.Json;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed partial class StatisticReconciliationRunService
{
    private const string CapturePlanPreflightSchemaVersion =
        "P10_CAPTURE_PLAN_PREFLIGHT_V1";
    private const string DirectFieldApiSurface = "DIRECT_FIELD";

    public async Task<StatisticReconciliationCapturePlanPreflightResponse>
        PreflightCapturePlanAsync(
            string workId,
            string scopeAssignmentId,
            JsonElement body,
            MeResponse actor,
            CancellationToken ct = default)
    {
        // Scope authorization must precede every hidden P8/P9/export lookup.
        var scope = await AuthorizeScopeAsync(
            workId,
            scopeAssignmentId,
            actor,
            ct);
        var request = StatisticReconciliationCanonicalJson
            .DeserializeStrict<StatisticReconciliationCapturePlanPreflightRequest>(
                body);
        var normalized = NormalizeCapturePlanPreflight(request, actor);
        var p9 = await LoadP9PublicationAsync(
            normalized.Create,
            scope,
            actor,
            ct);
        var captureRequest = BuildDirectOnlyCapturePlanRequest(
            p9,
            normalized.ExportId);
        var plan = await ResolveActualCapturePlanAsync(
            captureRequest,
            p9,
            scope,
            actor,
            normalized.Create.ConceptKey,
            normalized.Create.FilterHash,
            normalized.Create.CanonicalFilterJson ?? string.Empty,
            ct);
        if (plan is null)
            throw RequestInvalid("capturePlan", "SERVER_PLAN_REQUIRED");

        RequireDirectOnlyPlan(plan, p9, normalized.ExportId);
        var now = MongoUtcNow();
        var expiresAtUtc = now.Add(
            StatisticReconciliationCapturePlanToken.Lifetime);
        var payload = new StatisticReconciliationCapturePlanTokenPayload(
            StatisticReconciliationCapturePlanToken.SchemaVersion,
            actor.Id,
            NormalizeTenantId(actor.UnitId),
            scope.WorkId,
            scope.Id,
            normalized.Create.P9ResultKind,
            normalized.Create.P9ResultId,
            normalized.Create.P9RunId,
            normalized.Create.ConceptKey,
            normalized.Create.Grain,
            normalized.Create.CanonicalFilterJson ?? string.Empty,
            normalized.Create.FilterHash,
            normalized.ExportId,
            plan.P8ConfigurationOwnerId!,
            plan.P8ConfigurationBundleSha256!,
            plan.Basic.Disposition,
            plan.Advanced.Disposition,
            plan.Diff.Disposition,
            plan.Api.Surface,
            plan.Api.OwnerResultId!,
            plan.PlanSha256,
            plan.ActualConfigurationBundleSha256,
            now,
            expiresAtUtc);
        var token = StatisticReconciliationCapturePlanToken.Issue(
            payload,
            _capturePlanTokenSigningKey);
        return new StatisticReconciliationCapturePlanPreflightResponse
        {
            SchemaVersion = CapturePlanPreflightSchemaVersion,
            CapturePlanToken = token,
            PlanSha256 = plan.PlanSha256,
            ExpiresAtUtc = expiresAtUtc
        };
    }

    private static NormalizedCapturePlanPreflight
        NormalizeCapturePlanPreflight(
            StatisticReconciliationCapturePlanPreflightRequest request,
            MeResponse actor)
    {
        if (request is null)
            throw RequestInvalid("body", "BODY_REQUIRED");
        var p9ResultKind = RequiredToken(
            request.P9ResultKind,
            "p9ResultKind",
            64).ToUpperInvariant();
        var p9ResultId = RequiredText(
            request.P9ResultId,
            "p9ResultId",
            64);
        var p9RunId = RequiredText(
            request.P9RunId,
            "p9RunId",
            64);
        var conceptKey = RequiredToken(
            request.ConceptKey,
            "conceptKey",
            160);
        var grain = RequiredToken(
            request.Grain,
            "grain",
            64).ToUpperInvariant();
        if (request.Filter is not { } filter ||
            filter.ValueKind != JsonValueKind.Object)
        {
            throw RequestInvalid(
                "filter",
                "OBJECT_REQUIRED_WITH_CAPTURE_PLAN_PREFLIGHT");
        }
        var canonicalFilterJson =
            StatisticReconciliationCanonicalJson.Canonicalize(filter);
        var filterHash = StatisticReconciliationCanonicalJson.HashText(
            canonicalFilterJson);
        var exportId = RequiredText(request.ExportId, "exportId", 128);
        if (!ValidActualExportId(exportId))
            throw HiddenSourceFailure(actor, "ACTUAL_EXPORT_ID_INVALID");

        return new NormalizedCapturePlanPreflight(
            new NormalizedCreate(
                CapturePlanPreflightSchemaVersion,
                p9ResultKind,
                p9ResultId,
                p9RunId,
                conceptKey,
                grain,
                filterHash,
                canonicalFilterJson,
                null),
            exportId);
    }

    private static NormalizedActualCapturePlanRequest
        BuildDirectOnlyCapturePlanRequest(
            WorkReportStatisticRebuildJob p9,
            string exportId)
        => new(
            StatisticReconciliationActualCapturePlanIntegrity
                .BoundaryRegistryVersion,
            string.Empty,
            string.Empty,
            string.Empty,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            string.Empty,
            string.Empty,
            DirectFieldApiSurface,
            p9.Id,
            exportId,
            StatisticReconciliationActualSummaryTargetDispositions
                .NotApplicable,
            StatisticReconciliationActualSummaryTargetDispositions
                .NotApplicable,
            StatisticReconciliationActualSummaryTargetDispositions
                .NotApplicable);

    private StatisticReconciliationCapturePlanTokenPayload
        ValidateCapturePlanToken(
            string token,
            DateTime nowUtc,
            bool allowExpiredReplay)
    {
        try
        {
            return allowExpiredReplay
                ? StatisticReconciliationCapturePlanToken.ValidateForReplay(
                    token,
                    _capturePlanTokenSigningKey,
                    nowUtc)
                : StatisticReconciliationCapturePlanToken.Validate(
                    token,
                    _capturePlanTokenSigningKey,
                    nowUtc);
        }
        catch (InvalidOperationException)
        {
            throw RequestInvalid(
                "capturePlanToken",
                "CAPTURE_PLAN_TOKEN_INVALID");
        }
    }

    private static void RequireCapturePlanTokenRequestBinding(
        StatisticReconciliationCapturePlanTokenPayload payload,
        NormalizedCreate request,
        WorkAssignment scope,
        WorkReportStatisticRebuildJob p9,
        MeResponse actor)
    {
        var p8Bundle = BuildP8ConfigBundleHash(p9);
        var notApplicable =
            StatisticReconciliationActualSummaryTargetDispositions.NotApplicable;
        if (!StringComparer.Ordinal.Equals(payload.ActorUserId, actor.Id) ||
            !StringComparer.Ordinal.Equals(
                payload.TenantUnitId,
                NormalizeTenantId(actor.UnitId)) ||
            !StringComparer.Ordinal.Equals(payload.WorkId, scope.WorkId) ||
            !StringComparer.Ordinal.Equals(
                payload.ScopeAssignmentId,
                scope.Id) ||
            !StringComparer.Ordinal.Equals(
                payload.P9ResultKind,
                request.P9ResultKind) ||
            !StringComparer.Ordinal.Equals(
                payload.P9ResultId,
                request.P9ResultId) ||
            !StringComparer.Ordinal.Equals(
                payload.P9RunId,
                request.P9RunId) ||
            !StringComparer.Ordinal.Equals(
                payload.ConceptKey,
                request.ConceptKey) ||
            !StringComparer.Ordinal.Equals(payload.Grain, request.Grain) ||
            !StringComparer.Ordinal.Equals(
                payload.CanonicalFilterJson,
                request.CanonicalFilterJson) ||
            !StringComparer.Ordinal.Equals(
                payload.FilterSha256,
                request.FilterHash) ||
            !ValidActualExportId(payload.ExportId) ||
            !StringComparer.Ordinal.Equals(
                payload.P8ConfigurationOwnerId,
                p9.DynamicFormTemplateId) ||
            !StringComparer.Ordinal.Equals(
                payload.P8ConfigurationBundleSha256,
                p8Bundle) ||
            !StringComparer.Ordinal.Equals(
                payload.BasicDisposition,
                notApplicable) ||
            !StringComparer.Ordinal.Equals(
                payload.AdvancedDisposition,
                notApplicable) ||
            !StringComparer.Ordinal.Equals(
                payload.DiffDisposition,
                notApplicable) ||
            !StringComparer.Ordinal.Equals(
                payload.ApiSurface,
                DirectFieldApiSurface) ||
            !StringComparer.Ordinal.Equals(
                payload.ApiOwnerResultId,
                p9.Id) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                payload.PlanSha256) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                payload.ActualConfigurationBundleSha256))
        {
            throw RequestInvalid(
                "capturePlanToken",
                "CAPTURE_PLAN_TOKEN_BINDING_INVALID");
        }
    }

    private static void RequireDirectOnlyPlan(
        StatisticReconciliationActualCapturePlan plan,
        WorkReportStatisticRebuildJob p9,
        string exportId)
    {
        StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
            plan,
            plan.PlanSha256);
        var notApplicable =
            StatisticReconciliationActualSummaryTargetDispositions.NotApplicable;
        if (!StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan) ||
            !StringComparer.Ordinal.Equals(
                plan.P8ConfigurationOwnerId,
                p9.DynamicFormTemplateId) ||
            !StringComparer.Ordinal.Equals(
                plan.P8ConfigurationBundleSha256,
                BuildP8ConfigBundleHash(p9)) ||
            !StringComparer.Ordinal.Equals(
                plan.Basic.Disposition,
                notApplicable) ||
            !StringComparer.Ordinal.Equals(
                plan.Advanced.Disposition,
                notApplicable) ||
            !StringComparer.Ordinal.Equals(
                plan.Diff.Disposition,
                notApplicable) ||
            !StringComparer.Ordinal.Equals(
                plan.Api.Surface,
                DirectFieldApiSurface) ||
            !StringComparer.Ordinal.Equals(plan.Api.OwnerResultId, p9.Id) ||
            !StringComparer.Ordinal.Equals(plan.Export.ExportId, exportId))
        {
            throw RequestInvalid(
                "capturePlan",
                "SERVER_DIRECT_ONLY_PLAN_INVALID");
        }
    }

    private static void RequireCapturePlanTokenResolvedBinding(
        StatisticReconciliationCapturePlanTokenPayload payload,
        StatisticReconciliationActualCapturePlan plan)
    {
        if (!StringComparer.Ordinal.Equals(
                payload.PlanSha256,
                plan.PlanSha256) ||
            !StringComparer.Ordinal.Equals(
                payload.ActualConfigurationBundleSha256,
                plan.ActualConfigurationBundleSha256) ||
            !StringComparer.Ordinal.Equals(
                payload.P8ConfigurationOwnerId,
                plan.P8ConfigurationOwnerId) ||
            !StringComparer.Ordinal.Equals(
                payload.P8ConfigurationBundleSha256,
                plan.P8ConfigurationBundleSha256) ||
            !StringComparer.Ordinal.Equals(
                payload.ExportId,
                plan.Export.ExportId))
        {
            throw RequestInvalid(
                "capturePlanToken",
                "CAPTURE_PLAN_TOKEN_STATE_DRIFT");
        }
    }

    private static void RequireCapturePlanTokenReplayBinding(
        StatisticReconciliationCapturePlanTokenPayload payload,
        StatisticReconciliationRun existing)
    {
        if (existing.ActualCapturePlan is null ||
            !StatisticReconciliationActualCapturePlanIntegrity.IsV4(
                existing.ActualCapturePlan) ||
            !StringComparer.Ordinal.Equals(
                payload.PlanSha256,
                existing.ActualCapturePlanSha256) ||
            !StringComparer.Ordinal.Equals(
                payload.ActualConfigurationBundleSha256,
                existing.ActualConfigurationBundleSha256) ||
            !StringComparer.Ordinal.Equals(
                payload.P8ConfigurationOwnerId,
                existing.ActualCapturePlan.P8ConfigurationOwnerId) ||
            !StringComparer.Ordinal.Equals(
                payload.P8ConfigurationBundleSha256,
                existing.ActualCapturePlan.P8ConfigurationBundleSha256) ||
            !StringComparer.Ordinal.Equals(
                payload.ExportId,
                existing.ActualCapturePlan.Export.ExportId))
        {
            throw ReplayMismatch("CAPTURE_PLAN_TOKEN_REPLAY_BINDING_MISMATCH");
        }
    }

    private sealed record NormalizedCapturePlanPreflight(
        NormalizedCreate Create,
        string ExportId);
}