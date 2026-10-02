using System.Text.Json;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

// A staged artifact is not a publication. No API, scheduler or six-store Direct
// consumer may infer COMPLETED from its existence. Publication integration must
// bind the returned artifact hash into the owning job's complete generation hash.
internal sealed record NativeStatisticStageSource(string ReportId, string AssignmentId,
    int LifecycleRevision, string? ContributionPolicyJson, WorkReportNativeSourcePin Payload);

internal sealed record NativeStatisticGenerationArtifact(int Version, string WorkId, string PeriodInstanceKey,
    WorkReportDirectGenerationContext Generation, string DefinitionJson, string ConfigurationJson,
    NativeStatisticCalculationLimits Limits, IReadOnlyList<NativeStatisticStageSource> Sources,
    NativeStatisticCalculationResult Result);

internal sealed record NativeStatisticStageInputs(DynamicFormTemplate Template,
    IReadOnlyList<(WorkAssignmentReport Report, WorkReportPayloadSnapshot Payload)> AuthorizedSources);

internal static class NativeStatisticGenerationStage
{
    internal static async Task<NativeStatisticArtifactReceipt> StageAndVerifyAsync(NativeStatisticArtifactStore store,
        string workId, string periodInstanceKey, WorkReportDirectGenerationContext generation,
        NativeStatisticCalculationLimits calculationLimits, NativeStatisticStorageLimits storageLimits,
        Func<CancellationToken, Task<NativeStatisticStageInputs>> reloadAuthorizedSources, CancellationToken ct = default)
    {
        // The membership owner supplies a fresh complete authorized set twice.
        // This is a stage boundary only: the owning publisher still needs its
        // lease/CAS/transaction fence after this method, never a second scheduler.
        var inputs = await reloadAuthorizedSources(ct);
        var prepared = Prepare(inputs.Template, workId, periodInstanceKey, generation,
            inputs.AuthorizedSources, calculationLimits, ct);
        var receipt = await store.StageAsync(prepared, storageLimits, ct);
        var persisted = await store.ReadAsync(receipt, storageLimits, ct);
        if (StatConfigCanonicalJson.Canonicalize(persisted) != StatConfigCanonicalJson.Canonicalize(prepared))
            throw WorkReportDirectGenerationValidationException.RowConflict();
        var current = await reloadAuthorizedSources(ct);
        Revalidate(persisted, current.Template, current.AuthorizedSources, ct);
        return receipt;
    }

    internal static NativeStatisticGenerationArtifact Prepare(DynamicFormTemplate template,
        string workId, string periodInstanceKey, WorkReportDirectGenerationContext generation,
        IReadOnlyList<(WorkAssignmentReport Report, WorkReportPayloadSnapshot Payload)> authorizedSources,
        NativeStatisticCalculationLimits limits, CancellationToken ct = default)
    {
        // The existing membership owner must resolve permissions, period, lifecycle
        // and INCLUDE membership first. This internal boundary cannot query/add sources.
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workId) || string.IsNullOrWhiteSpace(periodInstanceKey)
            || authorizedSources.Count > limits.MaxReports)
            throw NativeStatisticCalculationError.Invalid("GENERATION_SCOPE_OR_SOURCE_LIMIT");
        var captured = generation.CaptureNativeSources(authorizedSources);
        var config = DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(template,
            captured.ConfigId, captured.ConfigVersionId, captured.ConfigVersionNo, captured.ConfigRevision, captured.ConfigHash);
        // V1 remains intact. Never invent selectors/options or silently omit an
        // enabled V1 target when the owning generation expects complete results.
        if (config.NativeTargets.Count != 0)
            throw NativeStatisticCalculationError.Invalid("GENERATION_ACTIVE_V1_ADAPTER_REQUIRED");
        var tables = DynamicFormNativeTableDefinition.ReadStored(template.NativeTablesVersion, template.TablesJson)!;
        if (template.Id != captured.DynamicFormTemplateId || template.FamilyId != captured.DynamicFormFamilyId
            || template.VersionNo != captured.DynamicFormVersionNo || template.PublishedSchemaHash != captured.DynamicFormSchemaHash)
            throw WorkReportDirectGenerationValidationException.PinConflict();
        var inputs = new List<NativeStatisticCalculationSource>(authorizedSources.Count);
        var sources = new List<NativeStatisticStageSource>(authorizedSources.Count);
        foreach (var (report, payload) in authorizedSources.OrderBy(s => s.Report.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (report.WorkId != workId || report.PeriodInstanceKey != periodInstanceKey
                || report.Status != WorkAssignmentReportStatus.Approved || !report.IsCurrent
                || report.IsActive != true || report.IsDeleted || report.InvalidatedByFlowEventId is not null)
                throw WorkReportDirectGenerationValidationException.SourceDrift();
            var input = WorkReportNativeStatisticInput.Read(template, report, payload, captured);
            if (!input.ContributionBinding.IsLockedFlowPolicy && report.CumulativeContributionMode != input.ContributionBinding.Mode)
                throw WorkReportDirectGenerationValidationException.SourceDrift();
            RequireContribution(input.ContributionBinding, report.CumulativeContributionPolicyJson);
            inputs.Add(NativeStatisticCalculationSource.Capture(report, payload));
            sources.Add(new(report.Id, report.WorkAssignmentId, report.LifecycleRevision,
                report.CumulativeContributionPolicyJson, captured.NativeSourcePins![report.Id]));
        }
        var plan = JsonSerializer.Serialize(new DynamicFormNativeStatisticPlanDto(2,
            config.NativePlan?.Targets.Select(t => t.Configuration).ToArray() ?? []), StatConfigCanonicalJson.StrictJsonOptions);
        var result = NativeTableStatisticCalculator.Calculate(plan, tables, template.SectionsJson,
            template.FieldsJson, template.BlocksJson, captured.DynamicFormSchemaHash,
            captured.NativeSourcePins!.Values.ToArray(), captured.NativeSourceOrderHash!, inputs, limits, ct);
        return new(2, workId, periodInstanceKey, captured,
            template.PublishedSchemaSnapshotJson ?? throw NativeStatisticCalculationError.Invalid("GENERATION_SCHEMA_SNAPSHOT_REQUIRED"),
            StatConfigCanonicalJson.Canonicalize(new { config.FieldSectionJson, config.TableSectionJson,
                config.NativeTargetSectionJson, config.NativePlanSectionJson, config.DependencyPins }), limits, sources, result);
    }

    internal static void Revalidate(NativeStatisticGenerationArtifact artifact, DynamicFormTemplate currentTemplate,
        IReadOnlyList<(WorkAssignmentReport Report, WorkReportPayloadSnapshot Payload)> currentAuthorizedSources,
        CancellationToken ct = default)
    {
        var current = Prepare(currentTemplate, artifact.WorkId, artifact.PeriodInstanceKey,
            artifact.Generation, currentAuthorizedSources, artifact.Limits, ct);
        // Recalculate from the exact authorized set, including policy, timestamps,
        // config/options, lifecycle and source values; never rebase a staged artifact.
        if (StatConfigCanonicalJson.Canonicalize(current) != StatConfigCanonicalJson.Canonicalize(artifact))
            throw WorkReportDirectGenerationValidationException.SourceDrift();
    }

    internal static void RequireContribution(WorkReportDirectContributionBinding binding, string? policyJson)
    {
        if (binding.Mode != "INCLUDE")
            throw NativeStatisticCalculationError.Invalid("GENERATION_EXCLUDED_SOURCE");
        // Native Flow/mapping is L7. The legacy policy uses Excel block/metric keys
        // and case-insensitive fallback matching; do not reuse it for native IDs.
        if (binding.IsLockedFlowPolicy)
            throw NativeStatisticCalculationError.Invalid("GENERATION_NATIVE_FLOW_CONSUMER_REQUIRED");
        if (string.IsNullOrWhiteSpace(policyJson)) return;
        using var doc = JsonDocument.Parse(policyJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count()
            || root.EnumerateObject().Any(p => p.Name is not ("defaultMode" or "rules"))
            || root.TryGetProperty("defaultMode", out var mode) && (mode.ValueKind != JsonValueKind.String || mode.GetString() != "INCLUDE")
            || root.TryGetProperty("rules", out var rules) && (rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() != 0))
            throw NativeStatisticCalculationError.Invalid("GENERATION_NATIVE_CONTRIBUTION_POLICY_UNSUPPORTED");
    }
}
