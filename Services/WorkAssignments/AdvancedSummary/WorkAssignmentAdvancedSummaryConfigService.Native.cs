using MongoDB.Driver;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public sealed partial class WorkAssignmentAdvancedSummaryConfigService
{
    private const string NativeReferencePath = "$.payload.sections[0].nativeTargets";

    private static IReadOnlyList<WorkAssignmentAdvancedSummaryNativeTargetPayload>? NormalizeP805NativeTargets(
        IReadOnlyList<WorkAssignmentAdvancedSummaryNativeTargetPayload>? input)
    {
        if (input is null) return null; // Preserve legacy canonical payload/hash.
        if (input.Count > WorkAssignmentAdvancedSummaryConfigContract.MaxNonCumulativeTargets)
            throw P805Schema(NativeReferencePath, "TARGET_LIMIT_EXCEEDED");
        var seen = new HashSet<(string, string, string)>();
        var result = new List<WorkAssignmentAdvancedSummaryNativeTargetPayload>(input.Count);
        for (var index = 0; index < input.Count; index++)
        {
            var path = $"{NativeReferencePath}[{index}]";
            var item = input[index] ?? throw P805Schema(path, "NATIVE_TARGET_REQUIRED");
            var table = P805RequiredIdentity(item.TableId, path + ".tableId", "TABLE_ID_REQUIRED");
            var target = P805RequiredIdentity(item.TargetId, path + ".targetId", "TARGET_ID_REQUIRED");
            var operation = P805RequiredIdentity(item.OperationId, path + ".operationId", "OPERATION_ID_REQUIRED");
            if (!seen.Add((table, target, operation))) throw P805Schema(path, "DUPLICATE_NATIVE_OPERATION");
            result.Add(new(table, target, operation));
        }
        return result;
    }

    internal static DynamicFormNativeStatisticPlanDto ResolveNativeAdvancedPlan(DynamicFormTemplate template,
        string sectionId, IReadOnlyList<WorkAssignmentAdvancedSummaryNativeTargetPayload> references)
    {
        var normalized = NormalizeP805NativeTargets(references)!;
        var section = DynamicFormSectionSnapshotBuilder.GetRequiredSection(template, sectionId);
        RequireNativeSectionReferences(section, normalized);
        return SelectNativeAdvancedPlan(ReadAdvancedNativeConfig(template), normalized);
    }

    private static DynamicFormStatisticConfigCommandService.NativeStatisticInputView ReadAdvancedNativeConfig(DynamicFormTemplate template)
    {
        try
        {
            return DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(template,
                template.StatisticConfigId!, template.StatisticConfigVersionId!, template.StatisticConfigVersionNo,
                template.StatisticConfigRevision, template.StatisticConfigHash!);
        }
        catch (InvalidOperationException) { throw P805Integrity("ADVANCED_SUMMARY_PUBLISHED_SCHEMA_INVALID"); }
    }

    private static DynamicFormNativeStatisticPlanDto SelectNativeAdvancedPlan(
        DynamicFormStatisticConfigCommandService.NativeStatisticInputView config,
        IReadOnlyList<WorkAssignmentAdvancedSummaryNativeTargetPayload> references)
        => DynamicFormNativeStatisticSelection.Select(config,
            references.Select(r => (r.TableId, r.TargetId, r.OperationId)).ToArray(),
            NativeReferencePath, "ADVANCED_NATIVE_", P805Schema);

    private static void RequireNativeSectionReferences(DynamicFormSectionSnapshot section,
        IReadOnlyList<WorkAssignmentAdvancedSummaryNativeTargetPayload> references)
    {
        var tableIds = section.NativeTableIds.ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < references.Count; index++)
            if (!tableIds.Contains(references[index].TableId!))
                throw P805Schema($"{NativeReferencePath}[{index}].tableId", "ADVANCED_NATIVE_TABLE_OUTSIDE_SECTION");
    }

    private async Task<IReadOnlyList<string>> P805NativeDependencyPinsAsync(IClientSessionHandle session,
        P805OwnerContext owner, WorkAssignmentAdvancedSummaryConfigPayload payload, CancellationToken ct)
    {
        var references = payload.Sections![0].NativeTargets;
        if (references is not { Count: > 0 }) return [];
        if (P805FlowModes.Contains(payload.SourceScope!.Mode!))
            throw P805Schema("$.payload.sourceScope", "ADVANCED_NATIVE_FLOW_CONSUMER_REQUIRED");
        RequireNativeSectionReferences(owner.Section, references);
        var config = ReadAdvancedNativeConfig(owner.Template);
        var plan = SelectNativeAdvancedPlan(config, references);
        return await DynamicFormNativeStatisticSelection.DependencyPinsAsync(session, _ctx.Labels, config, plan, owner.Actor,
            "NATIVE_ADVANCED_PLAN:", () => P805Schema(NativeReferencePath, "ADVANCED_NATIVE_LABEL_NOT_VISIBLE"), ct);
    }
}
