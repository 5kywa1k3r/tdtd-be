using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class StatisticReconciliationActualCrossViewParityV2
{
    private static void RequireFamilyOwnerBinding(
        StatisticReconciliationActualCrossViewParityV2Plan value,
        string family,
        string? apiOwnerResultId,
        string? apiGenerationId,
        string? apiGenerationSha256,
        string exportResultId,
        string exportResultSha256)
    {
        if (family == StatisticReconciliationActualCrossViewFamilies.Direct)
        {
            var publicationSha = Sha(
                value.DirectPublicationGenerationSha256);
            if (value.DirectSourceRevision is null or < 0 ||
                value.DirectPublicationRevision is null or < 0 ||
                (!Eq(exportResultId, apiOwnerResultId) &&
                 !Eq(exportResultId, apiGenerationId)) ||
                !Eq(exportResultSha256, publicationSha))
            {
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .ResultMismatch);
            }

            var publicationPin = H(
                "P10_ACTUAL_API_DIRECT_PUBLICATION_PIN_V1",
                apiOwnerResultId,
                apiGenerationId,
                publicationSha,
                I(value.DirectSourceRevision.Value),
                I(value.DirectPublicationRevision.Value));
            var composite = HS(
                "P10_ACTUAL_API_DIRECT_GENERATION_V1",
                [publicationPin]);
            if (!Eq(apiGenerationSha256, composite))
            {
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .ResultMismatch);
            }
            return;
        }

        if (value.DirectPublicationGenerationSha256 is not null ||
            value.DirectSourceRevision is not null ||
            value.DirectPublicationRevision is not null)
        {
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .PlanInvalid);
        }

        if (family is StatisticReconciliationActualCrossViewFamilies.Basic or
            StatisticReconciliationActualCrossViewFamilies.Flow)
        {
            if (!Eq(apiOwnerResultId, exportResultId) ||
                !Eq(apiGenerationId, exportResultId))
            {
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .ResultMismatch);
            }
            return;
        }

        if (family == StatisticReconciliationActualCrossViewFamilies.Diff &&
            (!Eq(apiOwnerResultId, exportResultId) ||
             !Eq(apiGenerationId, exportResultId) ||
             !Eq(apiGenerationSha256, exportResultSha256)))
        {
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .ResultMismatch);
        }
    }

    private static void RequireExactProtocolFilterSemantics(
        NormalizedPlan plan)
    {
        if (!plan.ApiRequired)
            return;

        using var document = StatisticReconciliationActualJson.ParseStrict(
            plan.CanonicalApiFilterJson!, "CROSS_VIEW_API_FILTER_EXACT");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .FilterMismatch);

        if (plan.Family is StatisticReconciliationActualCrossViewFamilies.Basic
            or StatisticReconciliationActualCrossViewFamilies.Flow)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal)
                { "q", "periodKey", "unitId", "assigneeUserId" };
            foreach (var property in root.EnumerateObject())
            {
                if (!allowed.Contains(property.Name) ||
                    property.Value.ValueKind is not (
                        JsonValueKind.String or JsonValueKind.Null))
                {
                    throw Fail(
                        StatisticReconciliationActualCrossViewParityV2Failures
                            .FilterMismatch);
                }
                if (property.Value.ValueKind == JsonValueKind.String)
                    _ = OptionalString(root, property.Name,
                        StatisticReconciliationActualCrossViewParityV2Failures
                            .FilterMismatch);
            }
            return;
        }

        if (plan.Family == StatisticReconciliationActualCrossViewFamilies.Diff)
        {
            if (root.EnumerateObject().Any())
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .FilterMismatch);
            return;
        }

        var allowedDirect = plan.ApiSurface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField =>
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "periodInstanceKey", "fieldId", "fieldKey",
                    "statisticLabelCode", "fieldType", "bucketKey",
                    "showInTree", "showInDetail", "periodKey",
                    "periodKeyFrom", "periodKeyTo", "reportStatus"
                },
            StatisticReconciliationActualApiSurfaces.DirectTable =>
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "periodInstanceKey", "dynamicExcelTemplateId", "blockId",
                    "tableMode", "metricKey", "metricLabelCode", "dataType",
                    "bucketKey", "periodKey", "reportStatus"
                },
            StatisticReconciliationActualApiSurfaces.DirectLabel =>
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "periodInstanceKey", "dynamicExcelTemplateId", "labelCode",
                    "periodKey", "reportStatus"
                },
            _ => throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .FamilySurfaceInvalid)
        };
        foreach (var property in root.EnumerateObject())
        {
            if (!allowedDirect.Contains(property.Name))
            {
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .FilterMismatch);
            }
            if (property.Value.ValueKind == JsonValueKind.Null)
                continue;
            if (property.Name is "showInTree" or "showInDetail")
            {
                if (property.Value.ValueKind is not (
                    JsonValueKind.True or JsonValueKind.False))
                    throw Fail(
                        StatisticReconciliationActualCrossViewParityV2Failures
                            .FilterMismatch);
                continue;
            }
            if (property.Name == "reportStatus")
            {
                if (property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetInt32(out _))
                    throw Fail(
                        StatisticReconciliationActualCrossViewParityV2Failures
                            .FilterMismatch);
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.String)
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .FilterMismatch);
            _ = OptionalString(root, property.Name,
                StatisticReconciliationActualCrossViewParityV2Failures
                    .FilterMismatch);
        }
    }
}
