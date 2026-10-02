using MongoDB.Driver;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.DynamicForms;

// Basic and Advanced resolve the same explicit operations against the trusted
// locked Form. This selector does not normalize references or invent defaults.
internal static class DynamicFormNativeStatisticSelection
{
    internal static DynamicFormNativeStatisticPlanDto Select(
        DynamicFormStatisticConfigCommandService.NativeStatisticInputView? config,
        IReadOnlyList<(string? TableId, string? TargetId, string? OperationId)> references,
        string pathRoot, string reasonPrefix, Func<string, string, Exception> error)
    {
        var selected = new List<DynamicFormNativeStatisticPlanTargetDto>();
        for (var index = 0; index < references.Count; index++)
        {
            var item = references[index];
            var path = $"{pathRoot}[{index}]";
            var matches = config?.NativePlan?.Targets.Where(target =>
                target.Configuration.TableId == item.TableId && target.Configuration.TargetId == item.TargetId).ToArray() ?? [];
            if (matches.Length != 1)
            {
                var legacy = config?.NativeTargets.Any(target => target.TableId == item.TableId && target.TargetId == item.TargetId) == true;
                throw error(path, reasonPrefix + (legacy ? "V1_EXPLICIT_UPGRADE_REQUIRED" : "TARGET_NOT_FOUND"));
            }
            var target = matches[0].Configuration;
            var operations = target.Operations!.Where(op => op.OperationId == item.OperationId).ToArray();
            if (operations.Length != 1) throw error(path + ".operationId", reasonPrefix + "OPERATION_NOT_FOUND");
            var existing = selected.FindIndex(t => t.TableId == item.TableId && t.TargetId == item.TargetId);
            if (existing < 0) selected.Add(target with { Operations = operations });
            else selected[existing] = selected[existing] with { Operations = selected[existing].Operations!.Concat(operations).ToArray() };
        }
        return new(2, selected);
    }

    internal static async Task<IReadOnlyList<string>> DependencyPinsAsync(IClientSessionHandle session,
        IMongoCollection<LabelCatalogItem> labels, DynamicFormStatisticConfigCommandService.NativeStatisticInputView config,
        DynamicFormNativeStatisticPlanDto plan, MeResponse me, string planPinPrefix,
        Func<Exception> labelNotVisible, CancellationToken ct)
    {
        var selected = config.NativePlan!.Targets.Where(target => plan.Targets!.Any(t =>
            t.TableId == target.Configuration.TableId && t.TargetId == target.Configuration.TargetId)).ToArray();
        var snapshots = selected.SelectMany(t => t.LabelSnapshots).ToArray();
        var owners = snapshots.Length == 0 ? [] : await labels.Find(session,
            Builders<LabelCatalogItem>.Filter.In(label => label.Id, snapshots.Select(s => s.LabelId).Distinct()) &
            Builders<LabelCatalogItem>.Filter.Eq(label => label.IsDeleted, false) &
            DynamicFormStatisticConfigCommandService.GetP804LabelVisibilityFilter(me)).ToListAsync(ct);
        var pins = new List<string>();
        foreach (var snapshot in snapshots)
        {
            var owner = owners.SingleOrDefault(label => label.Id == snapshot.LabelId);
            if (owner is null) throw labelNotVisible();
            LabelConfigCommandService.ValidateTrustedStatisticSnapshot(owner, snapshot);
            pins.Add($"LABEL:{snapshot.LabelId}:{snapshot.VersionId}:{snapshot.VersionNo}:{snapshot.ConfigHash}");
        }
        pins.AddRange(config.DependencyPins);
        pins.Add(planPinPrefix + StatConfigCanonicalJson.HashObject(new { plan, selected }));
        return pins;
    }
}
