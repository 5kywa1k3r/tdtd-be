using System.Text.Json;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public sealed partial class WorkAssignmentAdvancedSummaryHierarchyService
{
    // Reuse legacy FIELD reducers against exactly the captured native sources.
    // Explicit targets only; no default field/method and no child-node rollup.
    internal static JsonElement CalculateCapturedFields(DynamicFormTemplate template, string sectionId,
        IReadOnlyList<WorkAssignmentAdvancedSummaryTargetPayload> targets,
        IReadOnlyList<WorkAssignmentReport> reports, IReadOnlyDictionary<string, WorkReportPayloadSnapshot> payloads)
    {
        var fields = LoadSectionFields(template, sectionId).ToDictionary(f => f.FieldId, StringComparer.Ordinal);
        var accumulators = targets.Select(target => fields.TryGetValue(target.FieldId!, out var field)
            ? new DayNodeTargetAccumulator(field, target.Operation!)
            : throw new InvalidOperationException("ADVANCED_NATIVE_LEGACY_FIELD_MISSING")).ToArray();
        foreach (var report in reports)
        {
            if (!TryGetValuesObject(payloads[report.Id].FieldValuesJson, out var values)) continue;
            foreach (var accumulator in accumulators)
                if (TryGetFieldValue(values, accumulator.Field, out var value) && !IsBlankJsonElement(value))
                    accumulator.Add(value, report.Id);
        }
        // Legacy JOIN retains its existing sampled meaning. Native CONCAT never
        // consumes this property; it keeps full typed chunks and source segments.
        return JsonSerializer.SerializeToElement(accumulators.Select(a => a.ToDto()).ToArray(), ValueJsonOptions);
    }
}
