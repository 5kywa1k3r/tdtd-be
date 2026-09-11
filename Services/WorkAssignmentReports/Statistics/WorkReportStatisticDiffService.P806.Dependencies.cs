using MongoDB.Driver;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public sealed partial class WorkReportStatisticDiffService
{
    private static void P806ValidateScopeOwner(
        WorkReportStatisticDiffSourceScopePayload scope,
        WorkAssignment assignment,
        string path)
    {
        if (scope.Mode is
            WorkReportStatisticDiffConfigContract.DirectChildrenOrSelf or
            WorkReportStatisticDiffConfigContract.DirectChildren or
            WorkReportStatisticDiffConfigContract.Self)
        {
            return;
        }
        if (!string.Equals(
                scope.FlowInstanceId,
                assignment.FlowInstanceId,
                StringComparison.Ordinal))
        {
            throw P806Schema(
                $"{path}.flowInstanceId",
                "DIFF_SOURCE_SCOPE_FOREIGN");
        }
        if (scope.Mode == WorkReportStatisticDiffConfigContract.FlowStep &&
            !string.Equals(
                scope.FlowStepId,
                assignment.FlowStepId,
                StringComparison.Ordinal))
        {
            throw P806Schema(
                $"{path}.flowStepId",
                "DIFF_SOURCE_SCOPE_FOREIGN");
        }
        if (scope.Mode == WorkReportStatisticDiffConfigContract.FlowBranch &&
            !string.Equals(
                scope.FlowBranchId,
                assignment.FlowBranchId,
                StringComparison.Ordinal))
        {
            throw P806Schema(
                $"{path}.flowBranchId",
                "DIFF_SOURCE_SCOPE_FOREIGN");
        }
        if (scope.FlowEffectiveStatus is not null &&
            scope.FlowEffectiveStatus !=
                WorkReportStatisticDiffConfigContract.Any &&
            !string.Equals(
                scope.FlowEffectiveStatus,
                assignment.FlowEffectiveStatus,
                StringComparison.Ordinal))
        {
            throw P806Schema(
                $"{path}.flowEffectiveStatus",
                "DIFF_SOURCE_SCOPE_STATUS_MISMATCH");
        }
        if (scope.Mode == WorkReportStatisticDiffConfigContract.FlowFinal &&
            assignment.IsFlowFinalNode != true)
        {
            throw P806Schema(
                $"{path}.mode",
                "DIFF_SOURCE_SCOPE_NOT_FINAL");
        }
    }

    private async Task P806ValidateSelectedLabelsAsync(
        IClientSessionHandle session,
        MeResponse me,
        WorkReportStatisticDiffConfigPayload payload,
        IReadOnlyList<DynamicFormStatisticFieldConfigDto> fields,
        IReadOnlyList<DynamicFormStatisticTableConfigDto> tables,
        CancellationToken ct)
    {
        var selections = new[]
        {
            P806ResolveSelectedLabelSnapshot(
                payload.Left!.Selector!,
                "$.payload.left.selector",
                fields,
                tables),
            P806ResolveSelectedLabelSnapshot(
                payload.Right!.Selector!,
                "$.payload.right.selector",
                fields,
                tables)
        };
        var ids = selections
            .Select(item => item.Snapshot.LabelId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var filter =
            Builders<LabelCatalogItem>.Filter.In(item => item.Id, ids) &
            Builders<LabelCatalogItem>.Filter.Eq(item => item.IsDeleted, false) &
            DynamicFormStatisticConfigCommandService
                .GetP804LabelVisibilityFilter(me);
        var owners = await _ctx.Labels
            .Find(session, filter)
            .ToListAsync(ct);
        foreach (var selection in selections)
        {
            var matches = owners
                .Where(item => item.Id == selection.Snapshot.LabelId)
                .ToList();
            var valid = matches.Count == 1 &&
                P806LiveLabelMatches(matches[0], selection.Snapshot);
            if (valid)
                continue;
            var rowLabel = selection.ConceptKind ==
                WorkReportStatisticDiffConfigContract.RowLabel;
            throw P806Schema(
                rowLabel
                    ? $"{selection.Path}.conceptKey"
                    : $"{selection.Path}.conceptCode",
                rowLabel
                    ? "DIFF_ROW_LABEL_NOT_ALLOWED"
                    : "DIFF_SELECTOR_LABEL_FORBIDDEN_OR_STALE");
        }
    }

    private static P806LabelSelection P806ResolveSelectedLabelSnapshot(
        WorkReportStatisticDiffSelectorPayload selector,
        string path,
        IReadOnlyList<DynamicFormStatisticFieldConfigDto> fields,
        IReadOnlyList<DynamicFormStatisticTableConfigDto> tables)
    {
        DynamicFormStatisticLabelSnapshotDto? snapshot = null;
        if (selector.ConceptKind == WorkReportStatisticDiffConfigContract.Field)
        {
            var field = fields.Single(item => item.FieldId == selector.ConceptKey);
            var matches = field.LabelSnapshots
                .Where(item => item.Code == selector.ConceptCode)
                .ToList();
            if (matches.Count == 1)
                snapshot = matches[0];
        }
        else
        {
            var separator = selector.ConceptKey!.IndexOf(':');
            var blockId = selector.ConceptKey[..separator];
            var localKey = selector.ConceptKey[(separator + 1)..];
            var table = tables.Single(item => item.BlockId == blockId);
            if (selector.ConceptKind == WorkReportStatisticDiffConfigContract.TableMetric)
            {
                var matches = table.MetricLabelTargets
                    .Where(item =>
                        item.MetricKey == localKey &&
                        item.StatisticLabelCode == selector.ConceptCode)
                    .ToList();
                if (matches.Count == 1)
                    snapshot = matches[0].LabelSnapshot;
            }
            else
            {
                var matches = table.RowLabelSnapshots
                    .Where(item => item.Code == localKey)
                    .ToList();
                if (matches.Count == 1)
                    snapshot = matches[0];
            }
        }
        if (snapshot is null)
        {
            throw P806Schema(
                $"{path}.conceptKey",
                "DIFF_SELECTOR_LABEL_FORBIDDEN_OR_STALE");
        }
        return new P806LabelSelection(
            selector.ConceptKind!,
            path,
            snapshot);
    }

    private static bool P806LiveLabelMatches(
        LabelCatalogItem owner,
        DynamicFormStatisticLabelSnapshotDto snapshot)
        => owner.IsActive &&
           !owner.IsDeleted &&
           string.Equals(owner.Id, snapshot.LabelId, StringComparison.Ordinal) &&
           string.Equals(owner.Code, snapshot.Code, StringComparison.Ordinal) &&
           string.Equals(owner.DataType, snapshot.DataType, StringComparison.Ordinal) &&
           string.Equals(owner.Usage, snapshot.Usage, StringComparison.Ordinal) &&
           string.Equals(owner.ScopeType, snapshot.ScopeType, StringComparison.Ordinal) &&
           string.Equals(owner.ScopeId, snapshot.ScopeId, StringComparison.Ordinal) &&
           string.Equals(owner.VersionId, snapshot.VersionId, StringComparison.Ordinal) &&
           owner.VersionNo == snapshot.VersionNo &&
           string.Equals(owner.ConfigHash, snapshot.ConfigHash, StringComparison.Ordinal) &&
           snapshot.IsActive;

    private sealed record P806LabelSelection(
        string ConceptKind,
        string Path,
        DynamicFormStatisticLabelSnapshotDto Snapshot);
}
