using MongoDB.Driver;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignments.BasicSummary;

public sealed partial class WorkAssignmentBasicSummaryService
{
    private static IReadOnlyList<WorkAssignmentBasicSummaryNativeTargetPayload>? NormalizeP804NativeTargets(
        IReadOnlyList<WorkAssignmentBasicSummaryNativeTargetPayload>? input)
    {
        if (input is null) return null;
        if (input.Count > 1000)
            throw P804Schema("$.payload.nativeTargets", "NATIVE_TARGET_LIMIT");
        var seen = new HashSet<(string, string, string)>();
        var result = new List<WorkAssignmentBasicSummaryNativeTargetPayload>(input.Count);
        for (var i = 0; i < input.Count; i++)
        {
            var path = $"$.payload.nativeTargets[{i}]";
            var item = input[i] ?? throw P804Schema(path, "NATIVE_TARGET_REQUIRED");
            var table = P804RequiredIdentity(item.TableId, path + ".tableId", "TABLE_ID_REQUIRED");
            var target = P804RequiredIdentity(item.TargetId, path + ".targetId", "TARGET_ID_REQUIRED");
            var operation = P804RequiredIdentity(item.OperationId, path + ".operationId", "OPERATION_ID_REQUIRED");
            if (!seen.Add((table, target, operation)))
                throw P804Schema(path, "DUPLICATE_NATIVE_OPERATION");
            result.Add(new(table, target, operation));
        }
        return result;
    }

    private static DynamicFormStatisticConfigCommandService.NativeStatisticInputView P804ReadNativeConfig(
        DynamicFormTemplate template)
    {
        try
        {
            return DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(template,
                template.StatisticConfigId!, template.StatisticConfigVersionId!, template.StatisticConfigVersionNo,
                template.StatisticConfigRevision, template.StatisticConfigHash!);
        }
        catch (InvalidOperationException)
        {
            throw P804Integrity("BASIC_SUMMARY_DYNAMIC_FORM_PUBLISHED_SCHEMA_INVALID");
        }
    }

    // Shared by config validation and the raw-source Basic consumer.
    // Resolving references never invents v1 grouping/order/CONCAT options.
    internal static DynamicFormNativeStatisticPlanDto ResolveNativeBasicPlan(DynamicFormTemplate template,
        IReadOnlyList<WorkAssignmentBasicSummaryNativeTargetPayload> references)
        => P804SelectNativePlan(P804ReadNativeConfig(template), NormalizeP804NativeTargets(references)!);

    private static DynamicFormNativeStatisticPlanDto P804SelectNativePlan(
        DynamicFormStatisticConfigCommandService.NativeStatisticInputView? config,
        IReadOnlyList<WorkAssignmentBasicSummaryNativeTargetPayload> references)
        => DynamicFormNativeStatisticSelection.Select(config,
            references.Select(r => (r.TableId, r.TargetId, r.OperationId)).ToArray(),
            "$.payload.nativeTargets", "BASIC_NATIVE_", P804Schema);

    private async Task<IReadOnlyList<string>> P804ResolveNativeDependencyPinsAsync(IClientSessionHandle session,
        DynamicFormStatisticConfigCommandService.NativeStatisticInputView? config,
        IReadOnlyList<WorkAssignmentBasicSummaryNativeTargetPayload>? references, MeResponse me, CancellationToken ct)
    {
        if (references is not { Count: > 0 }) return [];
        var plan = P804SelectNativePlan(config, references);
        return await DynamicFormNativeStatisticSelection.DependencyPinsAsync(session, _ctx.Labels, config!, plan, me,
            "NATIVE_BASIC_PLAN:", () => P804Schema("$.payload.nativeTargets", "BASIC_NATIVE_LABEL_NOT_VISIBLE"), ct);
    }
}
