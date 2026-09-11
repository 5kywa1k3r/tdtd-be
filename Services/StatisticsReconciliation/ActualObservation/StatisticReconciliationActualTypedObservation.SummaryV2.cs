using System.Collections.Immutable;
using System.Globalization;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static partial class
    StatisticReconciliationActualTypedObservationCanonical
{
    private static readonly ImmutableHashSet<string> SummaryStateKinds =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "MISSING", "NULL", "EMPTY");

    private static readonly ImmutableHashSet<string> SummaryV2AtomKinds =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "REPORT_COUNT", "ROW_COUNT", "COUNT", "NUMERIC_VALUE_COUNT",
            "SUM", "MIN", "MAX", "MEAN", "BUCKET", "DATE",
            "FULL_DATE", "PERIOD", "BOOLEAN", "ENUM", "STRING_LIST",
            "TEXT", "MISSING", "NULL", "EMPTY",
            "TRANSITION_KIND", "ADDED_COUNT", "REMOVED_COUNT",
            "CHANGED_COUNT", "UNCHANGED_COUNT", "DIFFERENCE");

    internal static bool RequiresSummaryV2(
        StatisticReconciliationActualTypedObservation value)
        => SummaryStateKinds.Contains(value.AtomKind) ||
           value.ValueState != "VALUE" ||
           value.AtomKind == "STRING_LIST" ||
           IsNonNumericMinMax(value.AtomKind, value.ValueType) ||
           value.TransitionLeg != "NONE" ||
           value.TransitionKind is not null ||
           value.CollectionSemantics is not null;

    internal static StatisticReconciliationActualTypedObservation
        CreateSummaryCompatible(
            int ordinal,
            string layer,
            string ownerId,
            string ownerVersionSha256,
            string family,
            string kind,
            string metricId,
            string periodKey,
            string atomKind,
            string valueType,
            string valueState,
            string canonicalValue,
            int decimalScale = 0,
            long occurrenceCount = 1,
            long reportCount = 1,
            long rowCount = 0,
            long numericValueCount = 0,
            string? fieldId = null,
            string? tableId = null,
            string? rowId = null,
            string? labelId = null,
            string? basicScope = null,
            string? basicScopeId = null,
            string? flowBranchId = null,
            string? flowStepId = null,
            string? advancedGrain = null,
            string? diffKind = null,
            string transitionLeg = "NONE",
            string? transitionKind = null,
            string? collectionSemantics = null)
    {
        atomKind = Upper(atomKind, "SUMMARY_TYPED_ATOM_KIND");
        valueType = Upper(valueType, "SUMMARY_TYPED_VALUE_TYPE");
        valueState = Upper(valueState, "SUMMARY_TYPED_VALUE_STATE");
        return SummaryStateKinds.Contains(atomKind) ||
               valueState != "VALUE" || atomKind == "STRING_LIST" ||
               IsNonNumericMinMax(atomKind, valueType) ||
               transitionLeg != "NONE" || transitionKind is not null ||
               collectionSemantics is not null
            ? CreateSummaryV2(
                ordinal, layer, ownerId, ownerVersionSha256, family, kind,
                metricId, periodKey, atomKind, valueType, valueState,
                canonicalValue, decimalScale, occurrenceCount, reportCount,
                rowCount, numericValueCount, fieldId, tableId, rowId, labelId,
                basicScope, basicScopeId, flowBranchId, flowStepId,
                advancedGrain, diffKind, transitionLeg, transitionKind,
                collectionSemantics)
            : Create(
                ordinal, layer, ownerId, ownerVersionSha256, family, kind,
                metricId, periodKey, atomKind, valueType, canonicalValue,
                decimalScale, occurrenceCount, reportCount, rowCount,
                numericValueCount, valueState, fieldId, tableId, rowId,
                labelId, basicScope, basicScopeId, flowBranchId, flowStepId,
                advancedGrain, diffKind, transitionLeg, transitionKind,
                collectionSemantics);
    }

    internal static StatisticReconciliationActualTypedObservation
        Reordinalize(
            StatisticReconciliationActualTypedObservation value,
            int ordinal)
        => CreateSummaryCompatible(
            ordinal,
            value.Layer,
            value.OwnerId,
            value.OwnerVersionSha256,
            value.Family,
            value.Kind,
            value.MetricId,
            value.PeriodKey,
            value.AtomKind,
            value.ValueType,
            value.ValueState,
            value.CanonicalValue,
            value.DecimalScale,
            value.OccurrenceCount,
            value.ReportCount,
            value.RowCount,
            value.NumericValueCount,
            value.FieldId,
            value.TableId,
            value.RowId,
            value.LabelId,
            value.BasicScope,
            value.BasicScopeId,
            value.FlowBranchId,
            value.FlowStepId,
            value.AdvancedGrain,
            value.DiffKind,
            value.TransitionLeg,
            value.TransitionKind,
            value.CollectionSemantics);

    internal static StatisticReconciliationActualTypedObservation
        CreateSummaryV2(
            int ordinal,
            string layer,
            string ownerId,
            string ownerVersionSha256,
            string family,
            string kind,
            string metricId,
            string periodKey,
            string atomKind,
            string valueType,
            string valueState,
            string canonicalValue,
            int decimalScale,
            long occurrenceCount,
            long reportCount,
            long rowCount,
            long numericValueCount,
            string? fieldId,
            string? tableId,
            string? rowId,
            string? labelId,
            string? basicScope,
            string? basicScopeId,
            string? flowBranchId,
            string? flowStepId,
            string? advancedGrain,
            string? diffKind,
            string transitionLeg = "NONE",
            string? transitionKind = null,
            string? collectionSemantics = null)
    {
        if (ordinal < 0)
            throw Fail("SUMMARY_TYPED_ORDINAL_INVALID");
        layer = Upper(layer, "SUMMARY_TYPED_LAYER");
        if (!StatisticReconciliationActualCoherentLayers.RequiredOrder.Contains(
                layer, StringComparer.Ordinal))
            throw Fail("SUMMARY_TYPED_LAYER_INVALID");
        ownerId = Required(ownerId, "SUMMARY_TYPED_OWNER_ID");
        ownerVersionSha256 = Sha(
            ownerVersionSha256, "SUMMARY_TYPED_OWNER_VERSION_SHA256");
        family = Upper(family, "SUMMARY_TYPED_FAMILY");
        kind = Upper(kind, "SUMMARY_TYPED_KIND");
        metricId = Required(metricId, "SUMMARY_TYPED_METRIC_ID");
        periodKey = Required(periodKey, "SUMMARY_TYPED_PERIOD_KEY");
        atomKind = Upper(atomKind, "SUMMARY_TYPED_ATOM_KIND");
        valueType = Upper(valueType, "SUMMARY_TYPED_VALUE_TYPE");
        valueState = Upper(valueState, "SUMMARY_TYPED_VALUE_STATE");
        if (!SummaryV2AtomKinds.Contains(atomKind) ||
            !AllowedValueTypes.Contains(valueType))
            throw Fail("SUMMARY_TYPED_VOCABULARY_INVALID");
        var stateAtom = SummaryStateKinds.Contains(atomKind);
        var missingDifference = atomKind == "DIFFERENCE" &&
            valueState == "MISSING";
        var missingAggregate = (atomKind is "SUM" or "MIN" or "MAX" or
            "MEAN") && valueState == "MISSING";
        if ((stateAtom && valueState != atomKind) ||
            (!stateAtom && !missingDifference && !missingAggregate &&
             valueState != "VALUE"))
            throw Fail("SUMMARY_TYPED_VALUE_STATE_INVALID");
        if (missingDifference || missingAggregate)
        {
            if (canonicalValue.Length != 0 || decimalScale != 0 ||
                occurrenceCount != 0)
                throw Fail("SUMMARY_TYPED_MISSING_AGGREGATE_INVALID");
        }
        else
        {
            canonicalValue = StatisticReconciliationActualCanonical.Required(
                canonicalValue, "SUMMARY_TYPED_CANONICAL_VALUE", 65536);
        }
        fieldId = Optional(fieldId, "SUMMARY_TYPED_FIELD_ID");
        tableId = Optional(tableId, "SUMMARY_TYPED_TABLE_ID");
        rowId = Optional(rowId, "SUMMARY_TYPED_ROW_ID");
        labelId = Optional(labelId, "SUMMARY_TYPED_LABEL_ID");
        basicScope = OptionalUpper(
            basicScope, "SUMMARY_TYPED_BASIC_SCOPE");
        basicScopeId = Optional(
            basicScopeId, "SUMMARY_TYPED_BASIC_SCOPE_ID");
        flowBranchId = Optional(
            flowBranchId, "SUMMARY_TYPED_FLOW_BRANCH_ID");
        flowStepId = Optional(flowStepId, "SUMMARY_TYPED_FLOW_STEP_ID");
        advancedGrain = OptionalUpper(
            advancedGrain, "SUMMARY_TYPED_ADVANCED_GRAIN");
        diffKind = OptionalUpper(diffKind, "SUMMARY_TYPED_DIFF_KIND");
        transitionLeg = Upper(
            transitionLeg, "SUMMARY_TYPED_TRANSITION_LEG");
        transitionKind = OptionalUpper(
            transitionKind, "SUMMARY_TYPED_TRANSITION_KIND");
        collectionSemantics = OptionalUpper(
            collectionSemantics, "SUMMARY_TYPED_COLLECTION_SEMANTICS");
        var transitioned = transitionLeg != "NONE" ||
            transitionKind is not null;
        if ((valueType == "STRING_LIST") !=
                (collectionSemantics is not null) ||
            collectionSemantics is not (null or "ORDERED" or "UNORDERED"))
            throw Fail("SUMMARY_TYPED_COLLECTION_SEMANTICS_INVALID");
        if (valueType == "STRING_LIST" && valueState == "VALUE" &&
            !CanonicalStringList(
                collectionSemantics == "UNORDERED"
                    ? "STRING_LIST_UNORDERED"
                    : "STRING_LIST_ORDERED",
                canonicalValue))
            throw Fail("SUMMARY_TYPED_STRING_LIST_CANONICAL_INVALID");
        var version3 = transitioned || collectionSemantics is not null ||
            missingAggregate || IsNonNumericMinMax(atomKind, valueType);
        if (transitioned && (family != "DIFF" ||
            transitionLeg is not ("BEFORE" or "AFTER" or "DELTA" or
                "CHANGE_STATE") ||
            transitionKind is not ("ADDED" or "REMOVED" or "CHANGED")))
            throw Fail("SUMMARY_TYPED_TRANSITION_SHAPE_INVALID");
        if (!transitioned && (transitionLeg != "NONE" ||
            transitionKind is not null))
            throw Fail("SUMMARY_TYPED_TRANSITION_PARTIAL");
        if (transitioned)
        {
            ValidateTransitionShape(
                transitionLeg, atomKind, valueType, valueState);
            if (transitionLeg == "CHANGE_STATE" &&
                atomKind == "TRANSITION_KIND" &&
                canonicalValue != transitionKind)
                throw Fail("SUMMARY_TYPED_TRANSITION_KIND_CANONICAL_MISMATCH");
        }
        if (decimalScale is < 0 or > 28 || reportCount < 0 || rowCount < 0 ||
            numericValueCount < 0 || occurrenceCount < 0 ||
            (!stateAtom && !missingDifference && !missingAggregate &&
             occurrenceCount == 0))
            throw Fail("SUMMARY_TYPED_COUNTS_OR_SCALE_INVALID");

        ValidateSummaryV2Channel(
            atomKind, valueType, valueState, canonicalValue, decimalScale);
        ValidateIdentityShape(
            family, kind, fieldId, tableId, rowId, labelId, basicScope,
            basicScopeId, flowBranchId, flowStepId, advancedGrain, diffKind);

        var identitySha256 = version3
            ? StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_SUMMARY_TYPED_IDENTITY_V3",
                family, kind, metricId, periodKey,
                fieldId ?? "~", tableId ?? "~", rowId ?? "~",
                labelId ?? "~", basicScope ?? "~",
                basicScopeId ?? "~", advancedGrain ?? "~",
                diffKind ?? "~", transitionLeg, transitionKind ?? "~",
                collectionSemantics ?? "~")
            : StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_TYPED_IDENTITY_V1",
                family, kind, metricId, periodKey,
                fieldId ?? "~", tableId ?? "~", rowId ?? "~",
                labelId ?? "~", basicScope ?? "~",
                basicScopeId ?? "~", advancedGrain ?? "~",
                diffKind ?? "~");
        var valueIdentitySha256 = version3
            ? StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_SUMMARY_TYPED_VALUE_IDENTITY_V3",
                identitySha256, transitionLeg, transitionKind ?? "~",
                collectionSemantics ?? "~", atomKind, valueType,
                valueState, canonicalValue,
                StatisticReconciliationActualCanonical.Integer(decimalScale))
            : StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_SUMMARY_TYPED_VALUE_IDENTITY_V2",
                identitySha256, atomKind, valueType, valueState,
                canonicalValue,
                StatisticReconciliationActualCanonical.Integer(decimalScale));
        var provenanceSha256 = version3
            ? StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_SUMMARY_TYPED_PROVENANCE_V3",
                StatisticReconciliationActualCanonical.Integer(ordinal),
                layer, ownerId, ownerVersionSha256, identitySha256,
                transitionLeg, transitionKind ?? "~",
                collectionSemantics ?? "~", valueIdentitySha256)
            : StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_SUMMARY_TYPED_PROVENANCE_V2",
                StatisticReconciliationActualCanonical.Integer(ordinal),
                layer, ownerId, ownerVersionSha256, identitySha256,
                valueIdentitySha256);
        var atomSemanticSha256 = version3
            ? StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_SUMMARY_TYPED_ATOM_V3",
                identitySha256, transitionLeg, transitionKind ?? "~",
                collectionSemantics ?? "~", atomKind, valueType,
                valueState, canonicalValue,
                StatisticReconciliationActualCanonical.Integer(decimalScale),
                StatisticReconciliationActualCanonical.Integer(occurrenceCount),
                StatisticReconciliationActualCanonical.Integer(reportCount),
                StatisticReconciliationActualCanonical.Integer(rowCount),
                StatisticReconciliationActualCanonical.Integer(numericValueCount),
                valueIdentitySha256, provenanceSha256)
            : StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_SUMMARY_TYPED_ATOM_V2",
                identitySha256, atomKind, valueType, valueState,
                canonicalValue,
                StatisticReconciliationActualCanonical.Integer(decimalScale),
                StatisticReconciliationActualCanonical.Integer(occurrenceCount),
                StatisticReconciliationActualCanonical.Integer(reportCount),
                StatisticReconciliationActualCanonical.Integer(rowCount),
                StatisticReconciliationActualCanonical.Integer(numericValueCount),
                valueIdentitySha256, provenanceSha256);
        return new StatisticReconciliationActualTypedObservation(
            ordinal, layer, ownerId, ownerVersionSha256, provenanceSha256,
            identitySha256, family, kind, metricId, fieldId, tableId, rowId,
            labelId, basicScope, basicScopeId, flowBranchId, flowStepId,
            advancedGrain, diffKind, periodKey, atomKind, valueType, valueState,
            canonicalValue, decimalScale, occurrenceCount, reportCount,
            rowCount, numericValueCount, valueIdentitySha256,
            atomSemanticSha256, transitionLeg, transitionKind,
            collectionSemantics);
    }

    private static void ValidateSummaryV2Channel(
        string atomKind,
        string valueType,
        string valueState,
        string canonicalValue,
        int decimalScale)
    {
        if (atomKind == "TRANSITION_KIND")
        {
            if (valueType != "TEXT" || valueState != "VALUE" ||
                decimalScale != 0 || canonicalValue is not (
                    "ADDED" or "REMOVED" or "CHANGED"))
                throw Fail("SUMMARY_TYPED_TRANSITION_KIND_INVALID");
            return;
        }
        if (atomKind == "DIFFERENCE")
        {
            if (valueType != "NUMBER")
                throw Fail("SUMMARY_TYPED_DIFFERENCE_TYPE_INVALID");
            if (valueState == "MISSING")
            {
                if (canonicalValue.Length != 0 || decimalScale != 0)
                    throw Fail("SUMMARY_TYPED_DIFFERENCE_MISSING_INVALID");
                return;
            }
            ValidateCanonicalNumber(atomKind, canonicalValue, decimalScale);
            return;
        }
        if (IsSummaryCount(atomKind))
        {
            if (valueType != "NUMBER" || decimalScale != 0 || !long.TryParse(
                    canonicalValue,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var count) || count < 0)
                throw Fail("SUMMARY_TYPED_COUNT_INVALID");
            return;
        }
        if (SummaryStateKinds.Contains(atomKind))
        {
            if (decimalScale != 0 || !long.TryParse(
                    canonicalValue,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var count) || count < 0)
                throw Fail("SUMMARY_TYPED_STATE_COUNT_INVALID");
            return;
        }
        if (atomKind is "SUM" or "MIN" or "MAX" or "MEAN")
        {
            if (valueState == "MISSING")
            {
                if (canonicalValue.Length != 0 || decimalScale != 0 ||
                    (atomKind is "SUM" or "MEAN") && valueType != "NUMBER")
                    throw Fail("SUMMARY_TYPED_MISSING_AGGREGATE_INVALID");
                return;
            }
            if (valueType == "NUMBER")
            {
                ValidateCanonicalNumber(atomKind, canonicalValue, decimalScale);
                return;
            }
            if (atomKind is not ("MIN" or "MAX") || decimalScale != 0 ||
                !CanonicalNonNumeric(valueType, canonicalValue))
                throw Fail("SUMMARY_TYPED_NON_NUMERIC_AGGREGATE_INVALID");
            return;
        }
        if (decimalScale != 0)
            throw Fail("SUMMARY_TYPED_NON_NUMERIC_SCALE_INVALID");
        var valid = atomKind switch
        {
            "BOOLEAN" => valueType == "BOOLEAN" &&
                canonicalValue is "true" or "false",
            "DATE" => valueType == "DATE" &&
                CanonicalPeriodLike(canonicalValue, fullDate: false),
            "FULL_DATE" => valueType == "FULL_DATE" &&
                CanonicalPeriodLike(canonicalValue, fullDate: true),
            "PERIOD" => valueType == "PERIOD" &&
                CanonicalPeriodLike(canonicalValue, fullDate: false),
            "BUCKET" => valueType == "BUCKET" &&
                CanonicalBucket(canonicalValue),
            "STRING_LIST" => valueType == "STRING_LIST",
            "ENUM" => valueType == "ENUM",
            "TEXT" => valueType == "TEXT",
            _ => false
        };
        if (!valid)
            throw Fail("SUMMARY_TYPED_CANONICAL_VALUE_INVALID");
    }

    private static bool IsNonNumericMinMax(
        string atomKind,
        string valueType)
        => (atomKind is "MIN" or "MAX") && valueType != "NUMBER";

    private static bool CanonicalNonNumeric(
        string valueType,
        string canonicalValue)
        => valueType switch
        {
            "BOOLEAN" => canonicalValue is "true" or "false",
            "DATE" => CanonicalPeriodLike(canonicalValue, fullDate: false),
            "FULL_DATE" => CanonicalPeriodLike(canonicalValue, fullDate: true),
            "PERIOD" => CanonicalPeriodLike(canonicalValue, fullDate: false),
            "BUCKET" => CanonicalBucket(canonicalValue),
            "STRING_LIST" or "ENUM" or "TEXT" => canonicalValue.Length > 0,
            _ => false
        };

    private static void ValidateTransitionShape(
        string transitionLeg,
        string atomKind,
        string valueType,
        string valueState)
    {
        var valid = transitionLeg switch
        {
            "BEFORE" or "AFTER" => atomKind is not (
                "TRANSITION_KIND" or "ADDED_COUNT" or "REMOVED_COUNT" or
                "CHANGED_COUNT" or "UNCHANGED_COUNT" or "DIFFERENCE"),
            "CHANGE_STATE" => atomKind is "TRANSITION_KIND" or
                "ADDED_COUNT" or "REMOVED_COUNT" or "CHANGED_COUNT" or
                "UNCHANGED_COUNT",
            "DELTA" => atomKind == "DIFFERENCE" &&
                valueType == "NUMBER" && valueState is "VALUE" or "MISSING",
            _ => false
        };
        if (!valid)
            throw Fail("SUMMARY_TYPED_TRANSITION_ATOM_INVALID");
    }

    private static bool IsSummaryCount(string atomKind)
        => atomKind is "REPORT_COUNT" or "ROW_COUNT" or "COUNT" or
            "NUMERIC_VALUE_COUNT" or "ADDED_COUNT" or "REMOVED_COUNT" or
            "CHANGED_COUNT" or "UNCHANGED_COUNT";
}