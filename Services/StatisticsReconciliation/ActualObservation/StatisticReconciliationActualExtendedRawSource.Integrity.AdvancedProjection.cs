using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static partial class
    StatisticReconciliationActualExtendedRawSourceIntegrity
{
    private static ImmutableArray<
        StatisticReconciliationActualExtendedAdvancedDescriptorProjection>
        RequireAdvancedProjections(
            ImmutableArray<StatisticReconciliationActualExtendedAdvancedDescriptorProjection>
                values,
            ImmutableArray<StatisticReconciliationActualRawSummaryAtom> atoms)
    {
        if (values.IsDefault || !values.SequenceEqual(values.OrderBy(
                value => value.IdentitySha256, StringComparer.Ordinal)) ||
            values.Select(value => value.IdentitySha256)
                .Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw Fail("EXTENDED_ADVANCED_PROJECTION_ORDER_INVALID");
        var descriptors = atoms
            .GroupBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        foreach (var value in values)
        {
            var identity = Sha(
                value.IdentitySha256,
                "EXTENDED_ADVANCED_PROJECTION_IDENTITY");
            var descriptorSemantic = Sha(
                value.DescriptorSemanticSha256,
                "EXTENDED_ADVANCED_PROJECTION_DESCRIPTOR");
            if (!descriptors.TryGetValue(identity, out var descriptor) ||
                descriptor.DescriptorSemanticSha256 != descriptorSemantic ||
                descriptor.FieldId != value.FieldId)
                throw Fail("EXTENDED_ADVANCED_PROJECTION_BIJECTION_INVALID");
            var fieldId = Required(
                value.FieldId, "EXTENDED_ADVANCED_PROJECTION_FIELD_ID");
            var fieldKey = Required(
                value.FieldKey, "EXTENDED_ADVANCED_PROJECTION_FIELD_KEY");
            var rawType = Required(
                value.RawFieldType, "EXTENDED_ADVANCED_PROJECTION_RAW_TYPE");
            var prefix = fieldKey + ":";
            if (!descriptor.MetricId.StartsWith(
                    prefix, StringComparison.Ordinal))
                throw Fail("EXTENDED_ADVANCED_PROJECTION_METRIC_INVALID");
            var operation = Required(
                descriptor.MetricId[prefix.Length..],
                "EXTENDED_ADVANCED_PROJECTION_OPERATION");
            var actualType = rawType switch
            {
                "number" => "NUMBER",
                "date" or "fullDate" => "DATE",
                "boolean" => "BOOLEAN",
                "singleSelect" or "multiSelect" or "stringList" or
                    "richText" => "CHOICE",
                "shortText" or "longText" => "TEXT",
                _ => throw Fail(
                    "EXTENDED_ADVANCED_PROJECTION_RAW_TYPE_INVALID")
            };
            if (value.Options.IsDefault ||
                !value.Options.SequenceEqual(value.Options.OrderBy(
                    option => option.Code, StringComparer.Ordinal)) ||
                value.Options.Select(option => option.Code)
                    .Distinct(StringComparer.Ordinal).Count() !=
                    value.Options.Length)
                throw Fail("EXTENDED_ADVANCED_OPTION_ORDER_INVALID");
            foreach (var option in value.Options)
            {
                var code = Required(
                    option.Code, "EXTENDED_ADVANCED_OPTION_CODE");
                var label = Required(
                    option.Label, "EXTENDED_ADVANCED_OPTION_LABEL");
                if (Sha(
                        option.OptionSemanticSha256,
                        "EXTENDED_ADVANCED_OPTION_SEMANTIC") !=
                    StatisticReconciliationActualCanonical.Hash(
                        "P10_ACTUAL_EXTENDED_ADVANCED_OPTION_V1",
                        code,
                        label))
                    throw Fail("EXTENDED_ADVANCED_OPTION_SEMANTIC_INVALID");
            }
            var optionManifest = Hs(
                "P10_ACTUAL_EXTENDED_ADVANCED_OPTION_MANIFEST_V1",
                value.Options.Select(option =>
                    option.OptionSemanticSha256));
            var fieldPin = Required(
                value.FieldDependencyPin,
                "EXTENDED_ADVANCED_PROJECTION_FIELD_PIN");
            if (Sha(
                    value.OptionManifestSha256,
                    "EXTENDED_ADVANCED_OPTION_MANIFEST") != optionManifest ||
                Sha(
                    value.ProjectionSemanticSha256,
                    "EXTENDED_ADVANCED_PROJECTION_SEMANTIC") !=
                StatisticReconciliationActualCanonical.Hash(
                    "P10_ACTUAL_EXTENDED_ADVANCED_DESCRIPTOR_PROJECTION_V1",
                    descriptorSemantic,
                    identity,
                    fieldId,
                    fieldKey,
                    rawType,
                    actualType,
                    operation,
                    optionManifest,
                    fieldPin))
                throw Fail("EXTENDED_ADVANCED_PROJECTION_SEMANTIC_INVALID");
        }
        return values;
    }
}
