using System.Text.Json;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

/// <summary>
/// Validated source material for the native adapter, not observations or a
/// computed result. D2 selects typed cells; L5b/L5c own reducers and execution.
/// No database calls, default values, migration or contribution fallback.
/// </summary>
internal sealed record WorkReportNativeStatisticInput(
    WorkReportDirectProjectionPin SourcePin,
    DynamicFormStatisticConfigCommandService.NativeStatisticInputView Configuration,
    IReadOnlyList<DynamicFormNativeTableDto> Tables,
    JsonElement NativeValues,
    WorkReportDirectContributionBinding ContributionBinding,
    string? ContributionPolicyJson)
{
    internal static WorkReportNativeStatisticInput Read(
        DynamicFormTemplate template,
        WorkAssignmentReport report,
        WorkReportPayloadSnapshot payload,
        WorkReportDirectGenerationContext generation)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(generation);

        // The caller owns membership, permissions, period and lifecycle checks.
        // This intake requires an explicit captured member; it cannot add one
        // using the generation context's legacy contribution fallback.
        if (!generation.SourceContributionBindings.TryGetValue(report.Id, out var binding) || binding is null)
            throw WorkReportDirectGenerationValidationException.SourceDrift();
        if (template.Id != generation.DynamicFormTemplateId
            || template.FamilyId != generation.DynamicFormFamilyId
            || template.VersionNo != generation.DynamicFormVersionNo
            || template.PublishedSchemaHash != generation.DynamicFormSchemaHash
            || report.DynamicFormTemplateId != template.Id
            || report.DynamicFormFamilyId != template.FamilyId
            || report.DynamicFormVersionNo != template.VersionNo
            || report.DynamicFormSchemaHash != template.PublishedSchemaHash)
            throw WorkReportDirectGenerationValidationException.PinConflict();

        var configuration = DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(
            template, generation.ConfigId, generation.ConfigVersionId, generation.ConfigVersionNo,
            generation.ConfigRevision, generation.ConfigHash);
        WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, payload);
        DynamicFormNativeTableValues.Validate(template, report.DynamicFormSchemaHash,
            payload.TableValuesJson, submitting: true);

        var definitions = DynamicFormNativeTableDefinition.ReadStored(template.NativeTablesVersion, template.TablesJson)!;
        using var document = JsonDocument.Parse(payload.TableValuesJson!);
        // An empty trusted plan still validates the complete pinned envelope,
        // but has no cells to consume. Keep paged content opaque in that case.
        var hasNativeTargets = configuration.NativeTargets.Count != 0
            || configuration.NativePlan?.Targets.Count > 0;
        if (hasNativeTargets && document.RootElement.GetProperty("nativeTables").GetProperty("tables")
            .EnumerateArray().Any(table => table.TryGetProperty("contentRef", out _)))
            throw new InvalidOperationException("CONTENT_TABLE_REQUIRES_PAGED_READER");
        var requiresTimestamp = configuration.NativeTargets.Any(target => target.Statistic.AggregateOps?.Contains("LATEST", StringComparer.Ordinal) == true)
            || configuration.NativePlan?.Targets.Any(target => target.Configuration.Order?.Reports == "sourceUpdatedAtAsc") == true;
        return new WorkReportNativeStatisticInput(generation.CreateNativePin(report, payload, requiresTimestamp), configuration,
            definitions, document.RootElement.GetProperty("nativeTables").Clone(), binding,
            report.CumulativeContributionPolicyJson);
    }
}
