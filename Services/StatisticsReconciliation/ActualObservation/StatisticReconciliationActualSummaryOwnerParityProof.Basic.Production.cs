using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static partial class StatisticReconciliationActualSummaryOwnerParity
{
    private static readonly ImmutableHashSet<string> BasicSnapshotProperties =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "v", "meta", "fields", "tb", "warnings");

    private static readonly ImmutableHashSet<string> BasicCompactBlockProperties =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "b", "m", "w", "n", "op", "vc", "rc", "s", "mi", "ma",
            "ov", "items");

    private static readonly ImmutableHashSet<string> BasicCompactOverrideProperties =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "i", "mk", "rk", "ck", "op", "l");

    private static readonly ImmutableHashSet<string> BasicNullRunsProperties =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "values1DCompressed", "values1DCompression", "values1DLength",
            "values1D", "values1DCompressedIndexes",
            "values1DCompressedCounts");

    private static readonly ImmutableHashSet<string> BasicTableModes =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "FIXED_GRID", "APPEND_ROWS", "APPEND_COLUMNS", "MATRIX");

    private static readonly ImmutableHashSet<string> BasicNumericOperations =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "COUNT", "SUM", "MIN", "MAX", "MEAN");

    private static BasicOwnerView ReadBasicOwnerView(
        ActualBasicResultObservation owner)
    {
        using var request = StatisticReconciliationActualJson.ParseStrict(
            owner.RequestJson, "SUMMARY_PARITY_BASIC_REQUEST");
        using var snapshot = StatisticReconciliationActualJson.ParseStrict(
            owner.SnapshotJson, "SUMMARY_PARITY_BASIC_SNAPSHOT");
        if (StatisticReconciliationActualJson.RawSha256(owner.RequestJson) !=
                owner.RequestRawSha256 ||
            StatisticReconciliationActualJson.CanonicalSha256(
                request.RootElement) != owner.RequestCanonicalSha256 ||
            StatisticReconciliationActualJson.RawSha256(owner.SnapshotJson) !=
                owner.SnapshotRawSha256 ||
            StatisticReconciliationActualJson.CanonicalSha256(
                snapshot.RootElement) != owner.SnapshotCanonicalSha256 ||
            snapshot.RootElement.ValueKind != JsonValueKind.Object)
            throw Fail("BASIC_OWNER_JSON_HASH_MISMATCH");

        RequireExactProperties(snapshot.RootElement, BasicSnapshotProperties,
            "BASIC_COMPACT_ROOT_SCHEMA_INVALID");
        if (!snapshot.RootElement.TryGetProperty("v", out var version) ||
            !version.TryGetInt32(out var versionNumber) || versionNumber != 9 ||
            !snapshot.RootElement.TryGetProperty("meta", out var meta) ||
            meta.ValueKind != JsonValueKind.Object ||
            !snapshot.RootElement.TryGetProperty("warnings", out var warnings) ||
            warnings.ValueKind != JsonValueKind.Array ||
            warnings.EnumerateArray().Any(value =>
                value.ValueKind != JsonValueKind.String))
            throw Fail("BASIC_COMPACT_ROOT_SCHEMA_INVALID");

        var fields = ReadArray(snapshot.RootElement, "fields").ToArray();
        var blocks = ReadArray(snapshot.RootElement, "tb").ToArray();
        var fieldObservations = fields.Select((value, index) =>
            Item("FIELD", index, value)).ToArray();
        var blockObservations = blocks.Select((value, index) =>
            Item("TABLE_BLOCK", index, value)).ToArray();
        if (!fieldObservations.Concat(blockObservations)
                .SequenceEqual(owner.ResultItems))
            throw Fail("BASIC_OWNER_ITEMS_SNAPSHOT_MISMATCH");

        var logical = new List<BasicItem>(fields.Length + blocks.Length);
        logical.AddRange(fieldObservations.Select(ParseBasicItem));
        for (var index = 0; index < blocks.Length; index++)
            logical.AddRange(InflateBasicCompactBlock(
                blocks[index], blockObservations[index]));
        if (logical.Count > StatisticReconciliationActualBasicAdapter.MaxResultItems)
            throw Fail("BASIC_LOGICAL_ITEMS_LIMIT");

        return new BasicOwnerView(
            ReadBasicPeriodSelection(request.RootElement),
            ReadBasicMaxTextChars(request.RootElement),
            logical.ToArray());
    }

    private static BasicPeriodSelection ReadBasicPeriodSelection(
        JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object)
            throw Fail("BASIC_REQUEST_OBJECT_REQUIRED");
        var mode = StrictOptionalString(request, "periodScopeMode");
        var key = StrictOptionalString(request, "periodKey");
        var from = StrictOptionalString(request, "periodKeyFrom");
        var to = StrictOptionalString(request, "periodKeyTo");
        if (mode is null)
        {
            if (key is null || from is not null || to is not null)
                throw Fail("BASIC_PERIOD_SELECTION_INVALID");
            return new BasicPeriodSelection("SINGLE_PERIOD", key, null, null);
        }
        if (mode != mode.ToUpperInvariant())
            throw Fail("BASIC_PERIOD_SELECTION_NON_CANONICAL");
        switch (mode)
        {
            case "SINGLE_PERIOD" when key is not null && from is null && to is null:
                return new BasicPeriodSelection(mode, key, null, null);
            case "PERIOD_RANGE" when key is null && from is not null && to is not null:
                if (StringComparer.Ordinal.Compare(from, to) > 0)
                    throw Fail("BASIC_PERIOD_RANGE_REVERSED");
                return new BasicPeriodSelection(mode, null, from, to);
            case "ALL_PERIODS" when key is null && from is null && to is null:
                return new BasicPeriodSelection(mode, null, null, null);
            default:
                throw Fail("BASIC_PERIOD_SELECTION_INVALID");
        }
    }

    private static int ReadBasicMaxTextChars(JsonElement request)
    {
        if (!request.TryGetProperty("maxTextChars", out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return 12_000;
        if (!value.TryGetInt32(out var found) || found is < 1_000 or > 100_000)
            throw Fail("BASIC_MAX_TEXT_CHARS_INVALID");
        return found;
    }

    private static IEnumerable<BasicItem> InflateBasicCompactBlock(
        JsonElement block,
        ActualBasicPayloadItemObservation blockObservation)
    {
        if (block.ValueKind != JsonValueKind.Object)
            throw Fail("BASIC_COMPACT_TABLE_BLOCK_OBJECT_REQUIRED");
        RequireExactProperties(block, BasicCompactBlockProperties,
            "BASIC_COMPACT_TABLE_BLOCK_SCHEMA_INVALID");
        var blockId = RequiredCompactString(block, "b",
            "BASIC_COMPACT_TABLE_BLOCK_ID_INVALID");
        var tableMode = RequiredCompactString(block, "m",
            "BASIC_COMPACT_TABLE_MODE_INVALID");
        if (!BasicTableModes.Contains(tableMode))
            throw Fail("BASIC_COMPACT_TABLE_MODE_INVALID");

        var hasItems = block.TryGetProperty("items", out var itemArray) &&
            itemArray.ValueKind != JsonValueKind.Null;
        if (hasItems)
        {
            if (itemArray.ValueKind != JsonValueKind.Array ||
                itemArray.GetArrayLength() == 0 ||
                HasNonNullProperty(block, "vc") ||
                HasNonNullProperty(block, "rc") ||
                HasNonNullProperty(block, "s") ||
                HasNonNullProperty(block, "mi") ||
                HasNonNullProperty(block, "ma") ||
                HasNonNullProperty(block, "ov"))
                throw Fail("BASIC_COMPACT_TABLE_ITEMS_MIXED");
            var ordinal = 0;
            foreach (var item in itemArray.EnumerateArray())
            {
                var parsed = ParseBasicItem(NestedBasicTableItem(
                    blockObservation, ordinal++, item));
                if (parsed.Observation.Section != "TABLE" ||
                    parsed.Dto.TargetKind != "TABLE" ||
                    parsed.Dto.BlockId != blockId ||
                    parsed.Dto.TableMode != tableMode)
                    throw Fail("BASIC_COMPACT_TABLE_ITEM_BOUNDARY_MISMATCH");
                yield return parsed;
            }
            yield break;
        }

        var length = RequiredCompactInteger(block, "n",
            "BASIC_COMPACT_TABLE_LENGTH_INVALID");
        var width = RequiredCompactInteger(block, "w",
            "BASIC_COMPACT_TABLE_WIDTH_INVALID");
        var operation = RequiredCompactString(block, "op",
            "BASIC_COMPACT_TABLE_OPERATION_INVALID");
        if (length is < 1 or > StatisticReconciliationActualBasicAdapter.MaxResultItems ||
            width is < 1 or > StatisticReconciliationActualBasicAdapter.MaxResultItems ||
            !BasicNumericOperations.Contains(operation))
            throw Fail("BASIC_COMPACT_TABLE_VECTOR_HEADER_INVALID");

        var valueCounts = ReadStrictDecimalVector(block, "vc", length);
        var reportCounts = ReadStrictDecimalVector(block, "rc", length);
        var sums = ReadStrictDecimalVector(block, "s", length);
        var mins = ReadStrictDecimalVector(block, "mi", length);
        var maxes = ReadStrictDecimalVector(block, "ma", length);
        var overrides = ReadBasicCompactOverrides(block, length);
        var found = 0;
        for (var index = 0; index < length; index++)
        {
            var valueCount = StrictVectorCount(valueCounts[index],
                "BASIC_COMPACT_VALUE_COUNT_INVALID");
            var reportCount = StrictVectorCount(reportCounts[index],
                "BASIC_COMPACT_REPORT_COUNT_INVALID");
            var sum = sums[index];
            var min = mins[index];
            var max = maxes[index];
            if (valueCount == 0 && reportCount == 0 &&
                sum is null && min is null && max is null)
                continue;
            if (valueCount <= 0 || reportCount <= 0 ||
                sum is null || min is null || max is null || min > max)
                throw Fail("BASIC_COMPACT_TABLE_VECTOR_INCONSISTENT");

            var rowKey = $"row_{(index / width) + 1}";
            var columnKey = $"col_{(index % width) + 1}";
            var metricKey =
                $"table:{blockId}.row:{rowKey}.column:{columnKey}";
            overrides.TryGetValue(index, out var metricOverride);
            metricKey = metricOverride?.MetricKey ?? metricKey;
            rowKey = metricOverride?.RowKey ?? rowKey;
            columnKey = metricOverride?.ColumnKey ?? columnKey;
            var itemOperation = metricOverride?.Operation ?? operation;
            var label = metricOverride?.Label ?? metricKey;
            var mean = sum.Value / valueCount;
            var dto = new WorkAssignmentBasicSummaryItemDto
            {
                TargetKind = "TABLE",
                TargetKey = $"table:{blockId}:{metricKey}",
                BlockId = blockId,
                TableMode = tableMode,
                MetricKey = metricKey,
                RowKey = rowKey,
                ColumnKey = columnKey,
                Index = index,
                Label = label,
                DataType = "NUMBER",
                Operation = itemOperation,
                Value = itemOperation switch
                {
                    "COUNT" => valueCount,
                    "SUM" => sum,
                    "MIN" => min,
                    "MAX" => max,
                    "MEAN" => mean,
                    _ => throw Fail("BASIC_COMPACT_TABLE_OPERATION_INVALID")
                },
                ValueCount = valueCount,
                ReportCount = reportCount,
                Sum = sum,
                Min = min,
                Max = max,
                Mean = mean,
                Buckets = []
            };
            yield return ParseBasicItem(InflatedBasicTableItem(
                blockObservation, index, dto));
            found++;
        }
        if (found == 0)
            throw Fail("BASIC_COMPACT_TABLE_EMPTY");
    }

    private static Dictionary<int, BasicCompactOverride> ReadBasicCompactOverrides(
        JsonElement block,
        int length)
    {
        var output = new Dictionary<int, BasicCompactOverride>();
        if (!block.TryGetProperty("ov", out var values) ||
            values.ValueKind == JsonValueKind.Null)
            return output;
        if (values.ValueKind != JsonValueKind.Array)
            throw Fail("BASIC_COMPACT_OVERRIDE_ARRAY_INVALID");
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw Fail("BASIC_COMPACT_OVERRIDE_OBJECT_REQUIRED");
            RequireExactProperties(value, BasicCompactOverrideProperties,
                "BASIC_COMPACT_OVERRIDE_SCHEMA_INVALID");
            var index = RequiredCompactInteger(value, "i",
                "BASIC_COMPACT_OVERRIDE_INDEX_INVALID");
            if (index < 0 || index >= length || output.ContainsKey(index))
                throw Fail("BASIC_COMPACT_OVERRIDE_INDEX_INVALID");
            var metricKey = StrictOptionalString(value, "mk");
            var rowKey = StrictOptionalString(value, "rk");
            var columnKey = StrictOptionalString(value, "ck");
            var operation = StrictOptionalString(value, "op");
            var label = StrictOptionalString(value, "l");
            if (metricKey is null && rowKey is null && columnKey is null &&
                operation is null && label is null ||
                operation is not null && !BasicNumericOperations.Contains(operation))
                throw Fail("BASIC_COMPACT_OVERRIDE_EMPTY_OR_INVALID");
            output[index] = new BasicCompactOverride(
                metricKey, rowKey, columnKey, operation, label);
        }
        return output;
    }

    private static decimal?[] ReadStrictDecimalVector(
        JsonElement owner,
        string property,
        int requiredLength)
    {
        if (!owner.TryGetProperty(property, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return new decimal?[requiredLength];
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() != requiredLength)
                throw Fail("BASIC_COMPACT_VECTOR_LENGTH_MISMATCH");
            return value.EnumerateArray().Select(ReadVectorDecimal).ToArray();
        }
        if (value.ValueKind != JsonValueKind.Object)
            throw Fail("BASIC_COMPACT_VECTOR_SHAPE_INVALID");

        RequireExactProperties(value, BasicNullRunsProperties,
            "BASIC_NULL_RUNS_SCHEMA_INVALID");
        if (!value.TryGetProperty("values1DCompressed", out var compressed) ||
            compressed.ValueKind != JsonValueKind.True ||
            !value.TryGetProperty("values1DCompression", out var kind) ||
            kind.ValueKind != JsonValueKind.String ||
            kind.GetString() != "NULL_RUNS" ||
            RequiredCompactInteger(value, "values1DLength",
                "BASIC_NULL_RUNS_LENGTH_INVALID") != requiredLength ||
            requiredLength < 251 ||
            !value.TryGetProperty("values1D", out var compactValues) ||
            compactValues.ValueKind != JsonValueKind.Array)
            throw Fail("BASIC_NULL_RUNS_HEADER_INVALID");
        var packed = compactValues.EnumerateArray()
            .Select(ReadVectorDecimal).ToArray();
        var indexes = ReadStrictIntegerArray(value,
            "values1DCompressedIndexes");
        var counts = ReadStrictIntegerArray(value,
            "values1DCompressedCounts");
        if (indexes.Length == 0 || indexes.Length != counts.Length)
            throw Fail("BASIC_NULL_RUNS_CARDINALITY_INVALID");

        var priorEnd = 0;
        var removedBefore = 0;
        var runs = new List<BasicNullRun>(indexes.Length);
        for (var index = 0; index < indexes.Length; index++)
        {
            var start = indexes[index];
            var count = counts[index];
            var end = checked(start + count);
            var packedIndex = start - removedBefore;
            if (start < priorEnd || start < 0 || start >= requiredLength ||
                count < 8 || end > requiredLength || packedIndex < 0 ||
                packedIndex >= packed.Length ||
                packed[packedIndex] is not null and not 0m)
                throw Fail("BASIC_NULL_RUNS_RANGE_INVALID");
            removedBefore = checked(removedBefore + count - 1);
            runs.Add(new BasicNullRun(start, end, packedIndex, removedBefore));
            priorEnd = end;
        }
        if (requiredLength - removedBefore != packed.Length)
            throw Fail("BASIC_NULL_RUNS_PACKED_LENGTH_INVALID");

        var output = new decimal?[requiredLength];
        var packedCursor = 0;
        var outputCursor = 0;
        foreach (var run in runs)
        {
            while (outputCursor < run.Start)
                output[outputCursor++] = packed[packedCursor++];
            var repeated = packed[packedCursor++];
            while (outputCursor < run.EndExclusive)
                output[outputCursor++] = repeated;
        }
        while (outputCursor < requiredLength)
            output[outputCursor++] = packed[packedCursor++];
        if (packedCursor != packed.Length)
            throw Fail("BASIC_NULL_RUNS_CURSOR_INVALID");
        return output;
    }

    private static int[] ReadStrictIntegerArray(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Array)
            throw Fail("BASIC_NULL_RUNS_INTEGER_ARRAY_INVALID");
        var output = new int[value.GetArrayLength()];
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (!item.TryGetInt32(out output[index++]))
                throw Fail("BASIC_NULL_RUNS_INTEGER_ARRAY_INVALID");
        }
        return output;
    }

    private static decimal? ReadVectorDecimal(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDecimal(out var number))
            throw Fail("BASIC_COMPACT_VECTOR_VALUE_INVALID");
        return number;
    }

    private static int StrictVectorCount(decimal? value, string reason)
    {
        if (value is null)
            return 0;
        if (value < 0 || value > int.MaxValue || decimal.Truncate(value.Value) != value)
            throw Fail(reason);
        return decimal.ToInt32(value.Value);
    }

    private static ActualBasicPayloadItemObservation NestedBasicTableItem(
        ActualBasicPayloadItemObservation block,
        int ordinal,
        JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            throw Fail("BASIC_COMPACT_TABLE_ITEM_OBJECT_REQUIRED");
        var canonical = StatisticReconciliationActualJson.Canonicalize(item);
        var identity = FirstString(item, "targetKey", "metricKey") ??
            throw Fail("BASIC_COMPACT_TABLE_ITEM_IDENTITY_REQUIRED");
        return new ActualBasicPayloadItemObservation(
            "TABLE", ordinal, identity, canonical,
            Hash("P10_ACTUAL_SUMMARY_OWNER_BASIC_NESTED_TABLE_ITEM_V1",
                block.SemanticSha256, I(ordinal), identity,
                StatisticReconciliationActualJson.RawSha256(canonical)));
    }

    private static ActualBasicPayloadItemObservation InflatedBasicTableItem(
        ActualBasicPayloadItemObservation block,
        int ordinal,
        WorkAssignmentBasicSummaryItemDto dto)
    {
        var element = JsonSerializer.SerializeToElement(dto, WebJson);
        var canonical = StatisticReconciliationActualJson.Canonicalize(element);
        return new ActualBasicPayloadItemObservation(
            "TABLE", ordinal, dto.TargetKey, canonical,
            Hash("P10_ACTUAL_SUMMARY_OWNER_BASIC_INFLATED_TABLE_ITEM_V1",
                block.SemanticSha256, I(ordinal), dto.TargetKey,
                StatisticReconciliationActualJson.RawSha256(canonical)));
    }

    private static void RequireBasicRawSourceIdentity(
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        ImmutableArray<StatisticReconciliationActualRawPayloadEnvelope> sources,
        ActualBasicResultObservation owner)
    {
        var reportIds = sources.Select(value => value.ReportId).ToArray();
        if (reportIds.Distinct(StringComparer.Ordinal).Count() != reportIds.Length ||
            !CanonicalBasicIdSet(reportIds).SequenceEqual(
                CanonicalBasicIdSet(owner.SourceReportIds),
                StringComparer.Ordinal))
            throw Fail("BASIC_SOURCE_REPORT_IDS_MISMATCH");
        var reportCounts = atoms.Where(value =>
                value.TransitionLeg == "NONE" &&
                value.AtomKind == "REPORT_COUNT")
            .Select(value => value.ReportCount).Distinct().ToArray();
        if (reportCounts.Length != 1 || reportCounts[0] != sources.Length ||
            reportCounts[0] != owner.SourceReportIds.Length)
            throw Fail("BASIC_SOURCE_CARDINALITY_MISMATCH");
    }

    private static IEnumerable<string> CanonicalBasicIdSet(
        IEnumerable<string> values)
        => values.Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal);

    private static void ValidateBasicDescriptorBoundary(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        BasicPeriodSelection period,
        ActualBasicResultObservation owner)
    {
        if (descriptor.ExpandArray && descriptor.ValueType == "STRING_LIST" ||
            descriptor.CollectionSemantics is not null &&
            descriptor.ValueType != "STRING_LIST" ||
            descriptor.BasicScope != owner.Boundary.SourceScopeMode ||
            descriptor.BasicScopeId != BasicScopeId(owner.Boundary))
            throw Fail("BASIC_DESCRIPTOR_SHAPE_UNPROVABLE");
        if (period.Mode == "PERIOD_RANGE")
            throw Fail("BASIC_PERIOD_RANGE_IDENTITY_UNPROVABLE");
        if (period.Mode == "ALL_PERIODS")
            throw Fail("BASIC_ALL_PERIODS_IDENTITY_UNPROVABLE");
        if (period.Key is null ||
            !PeriodMatches(descriptor.PeriodKey, period.Key, null))
            throw Fail("BASIC_PERIOD_IDENTITY_MISMATCH");
    }

    private static bool BasicItemMatchesDescriptor(
        BasicItem item,
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor)
        => descriptor.Kind switch
        {
            "FIELD" => item.Observation.Section == "FIELD" &&
                item.Dto.TargetKind == "FIELD" &&
                item.Dto.FieldId == descriptor.FieldId &&
                (item.Dto.TargetKey == descriptor.MetricId ||
                 item.Dto.FieldKey == descriptor.MetricId),
            "TABLE" => item.Observation.Section == "TABLE" &&
                item.Dto.TargetKind == "TABLE" &&
                item.Dto.BlockId == descriptor.TableId &&
                (item.Dto.TargetKey == descriptor.MetricId ||
                 item.Dto.MetricKey == descriptor.MetricId),
            _ => false
        };

    private static string ProveBasicLogicalItem(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> partition,
        BasicItem item,
        ImmutableArray<StatisticReconciliationActualRawPayloadEnvelope> sources,
        ActualBasicResultObservation owner,
        int maxTextChars)
    {
        ValidateBasicItemCoordinates(descriptor, item);
        var count = Integer(partition, "COUNT");
        var reportCount = BasicContributingReportCount(descriptor, sources);
        if (count > int.MaxValue || reportCount > int.MaxValue ||
            item.Dto.ValueCount != count || item.Dto.ReportCount != reportCount)
            throw Fail("BASIC_OWNER_COUNT_MISMATCH");

        switch (descriptor.ValueType)
        {
            case "NUMBER":
                ProveBasicNumber(descriptor, partition, item, count);
                break;
            case "DATE":
            case "FULL_DATE":
                ProveBasicDate(descriptor, partition, item, count);
                break;
            case "BOOLEAN":
                ProveBasicBoolean(descriptor, partition, item, count);
                break;
            case "BUCKET":
            case "ENUM":
                ProveBasicSelection(descriptor, partition, item, count);
                break;
            case "TEXT":
                ProveBasicText(descriptor, partition, item, count, sources,
                    owner.SourceReportIds, maxTextChars);
                break;
            default:
                throw Fail("BASIC_OWNER_VALUE_TYPE_UNPROVABLE");
        }

        var rawManifest = HashSequence(
            "P10_ACTUAL_SUMMARY_OWNER_BASIC_RAW_RELATION_V1",
            partition.Select(value => value.AtomSemanticSha256));
        return Hash("P10_ACTUAL_SUMMARY_OWNER_BASIC_RELATION_V1",
            descriptor.SemanticSha256, rawManifest,
            item.Observation.SemanticSha256,
            StatisticReconciliationActualJson.RawSha256(
                item.Observation.CanonicalJson));
    }

    private static void ValidateBasicItemCoordinates(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        BasicItem item)
    {
        var dto = item.Dto;
        if (descriptor.Kind == "FIELD")
        {
            if (dto.TargetKind != "FIELD" || dto.FieldId != descriptor.FieldId ||
                dto.FieldKey is null || dto.BlockId is not null ||
                dto.TableMode is not null || dto.MetricKey is not null ||
                dto.RowKey is not null || dto.ColumnKey is not null ||
                dto.Index is not null)
                throw Fail("BASIC_OWNER_FIELD_SHAPE_UNPROVABLE");
            return;
        }
        if (descriptor.Kind != "TABLE" || dto.TargetKind != "TABLE" ||
            dto.FieldId is not null || dto.FieldKey is not null ||
            dto.BlockId != descriptor.TableId ||
            !BasicTableModes.Contains(dto.TableMode ?? string.Empty) ||
            string.IsNullOrEmpty(dto.MetricKey) || string.IsNullOrEmpty(dto.RowKey) ||
            string.IsNullOrEmpty(dto.ColumnKey) || dto.Index is null or < 0)
            throw Fail("BASIC_OWNER_TABLE_SHAPE_UNPROVABLE");
    }

    private static void ProveBasicNumber(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        BasicItem item,
        long count)
    {
        if (item.Dto.DataType != "NUMBER" ||
            !BasicNumericOperations.Contains(item.Dto.Operation) ||
            item.Dto.Operation != "COUNT" &&
            !descriptor.Operations.Contains(item.Dto.Operation,
                StringComparer.Ordinal) ||
            Integer(atoms, "NUMERIC_VALUE_COUNT") != count ||
            item.Dto.TrueCount is not null || item.Dto.FalseCount is not null ||
            item.Dto.MinDateUtc is not null || item.Dto.MaxDateUtc is not null ||
            item.Dto.Text is not null || item.Dto.TextCharCount is not null ||
            item.Dto.TextTruncated || item.Dto.Buckets.Count != 0)
            throw Fail("BASIC_OWNER_NUMBER_SHAPE_UNPROVABLE");
        var sum = OptionalBasicDecimal(atoms, descriptor, "SUM");
        var min = OptionalBasicDecimal(atoms, descriptor, "MIN");
        var max = OptionalBasicDecimal(atoms, descriptor, "MAX");
        var mean = OptionalBasicDecimal(atoms, descriptor, "MEAN");
        if (sum.IsApplicable && item.Dto.Sum != sum.Value ||
            min.IsApplicable && item.Dto.Min != min.Value ||
            max.IsApplicable && item.Dto.Max != max.Value ||
            mean.IsApplicable && item.Dto.Mean != mean.Value)
            throw Fail("BASIC_OWNER_NUMBER_METADATA_MISMATCH");
        decimal? selected = item.Dto.Operation switch
        {
            "COUNT" => count,
            "SUM" => sum.Value,
            "MIN" => min.Value,
            "MAX" => max.Value,
            "MEAN" => mean.Value,
            _ => null
        };
        if (!BasicJsonDecimalEquals(item.ValueElement, selected))
            throw Fail("BASIC_OWNER_VALUE_MISMATCH");
    }

    private static void ProveBasicDate(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        BasicItem item,
        long count)
    {
        if (item.Dto.DataType is not ("DATE" or "FULL_DATE") ||
            item.Dto.Operation is not ("COUNT" or "MIN_DATE" or "MAX_DATE") ||
            item.Dto.Operation == "MIN_DATE" &&
                !descriptor.Operations.Contains("MIN", StringComparer.Ordinal) ||
            item.Dto.Operation == "MAX_DATE" &&
                !descriptor.Operations.Contains("MAX", StringComparer.Ordinal) ||
            !BasicNonNumericZeroMetadata(item.Dto, count) ||
            item.Dto.TrueCount is not null || item.Dto.FalseCount is not null ||
            item.Dto.Text is not null || item.Dto.TextCharCount is not null ||
            item.Dto.TextTruncated || item.Dto.Buckets.Count != 0)
            throw Fail("BASIC_OWNER_DATE_SHAPE_UNPROVABLE");
        var minimum = OptionalBasicTypedValue(atoms, descriptor, "MIN");
        var maximum = OptionalBasicTypedValue(atoms, descriptor, "MAX");
        if (minimum.IsApplicable &&
                !BasicDateMatches(minimum.Value, item.Dto.MinDateUtc) ||
            maximum.IsApplicable &&
                !BasicDateMatches(maximum.Value, item.Dto.MaxDateUtc))
            throw Fail("BASIC_OWNER_DATE_METADATA_MISMATCH");
        if (item.Dto.Operation == "COUNT")
        {
            if (!BasicJsonDecimalEquals(item.ValueElement, count))
                throw Fail("BASIC_OWNER_VALUE_MISMATCH");
            return;
        }
        var expected = item.Dto.Operation == "MIN_DATE"
            ? item.Dto.MinDateUtc : item.Dto.MaxDateUtc;
        if (!BasicJsonDateEquals(item.ValueElement, expected))
            throw Fail("BASIC_OWNER_VALUE_MISMATCH");
    }

    private static void ProveBasicBoolean(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        BasicItem item,
        long count)
    {
        if (item.Dto.DataType != "BOOLEAN" ||
            item.Dto.Operation is not ("COUNT" or "TRUE_COUNT" or "FALSE_COUNT") ||
            item.Dto.Operation is "TRUE_COUNT" or "FALSE_COUNT" &&
                !descriptor.Operations.Contains("VALUES", StringComparer.Ordinal) ||
            !BasicNonNumericZeroMetadata(item.Dto, count) ||
            item.Dto.MinDateUtc is not null || item.Dto.MaxDateUtc is not null ||
            item.Dto.Text is not null || item.Dto.TextCharCount is not null ||
            item.Dto.TextTruncated || item.Dto.Buckets.Count != 0)
            throw Fail("BASIC_OWNER_BOOLEAN_SHAPE_UNPROVABLE");
        var trueCount = BasicTypedOccurrence(atoms, "BOOLEAN", "true",
            "BOOLEAN");
        var falseCount = BasicTypedOccurrence(atoms, "BOOLEAN", "false",
            "BOOLEAN");
        if ((item.Dto.TrueCount ?? 0) != trueCount ||
            (item.Dto.FalseCount ?? 0) != falseCount ||
            trueCount + falseCount != count)
            throw Fail("BASIC_OWNER_BOOLEAN_COUNT_MISMATCH");
        var selected = item.Dto.Operation switch
        {
            "COUNT" => count,
            "TRUE_COUNT" => trueCount,
            "FALSE_COUNT" => falseCount,
            _ => 0
        };
        if (!BasicJsonDecimalEquals(item.ValueElement, selected))
            throw Fail("BASIC_OWNER_VALUE_MISMATCH");
    }

    private static void ProveBasicSelection(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        BasicItem item,
        long count)
    {
        if (item.Dto.DataType is not ("SINGLE_SELECT" or "MULTI_SELECT") ||
            item.Dto.Operation is not ("COUNT" or "BUCKET_COUNT") ||
            item.Dto.Operation == "BUCKET_COUNT" &&
                !descriptor.Operations.Contains("VALUES", StringComparer.Ordinal) ||
            !BasicNonNumericZeroMetadata(item.Dto, count) ||
            item.Dto.TrueCount is not null || item.Dto.FalseCount is not null ||
            item.Dto.MinDateUtc is not null || item.Dto.MaxDateUtc is not null ||
            item.Dto.Text is not null || item.Dto.TextCharCount is not null ||
            item.Dto.TextTruncated)
            throw Fail("BASIC_OWNER_SELECTION_SHAPE_UNPROVABLE");
        var atomKind = descriptor.ValueType == "BUCKET" ? "BUCKET" : "ENUM";
        var expected = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var atom in atoms.Where(value => value.AtomKind == atomKind &&
                     value.TransitionLeg == "NONE"))
        {
            var key = BasicBucketKey(atom, descriptor.ValueType);
            if (!expected.TryAdd(key, atom.OccurrenceCount))
                throw Fail("BASIC_RAW_BUCKET_KEY_COLLISION");
        }
        if (expected.Values.Sum() != count ||
            item.Dto.Buckets.Any(value => string.IsNullOrEmpty(value.Key) ||
                string.IsNullOrEmpty(value.Label) || value.Count <= 0) ||
            item.Dto.Buckets.Select(value => value.Key)
                .Distinct(StringComparer.Ordinal).Count() != item.Dto.Buckets.Count ||
            item.Dto.Buckets.Any(value =>
                !expected.TryGetValue(value.Key, out var amount) ||
                amount != value.Count) ||
            expected.Count != item.Dto.Buckets.Count ||
            !item.Dto.Buckets.SequenceEqual(item.Dto.Buckets
                .OrderByDescending(value => value.Count)
                .ThenBy(value => value.Label, StringComparer.Ordinal)))
            throw Fail("BASIC_OWNER_SELECTION_BUCKET_MISMATCH");
        if (item.Dto.Operation == "COUNT")
        {
            if (!BasicJsonDecimalEquals(item.ValueElement, count))
                throw Fail("BASIC_OWNER_VALUE_MISMATCH");
            return;
        }
        var expectedJson = StatisticReconciliationActualJson.Canonicalize(
            JsonSerializer.SerializeToElement(item.Dto.Buckets, WebJson));
        if (item.ValueElement is null ||
            StatisticReconciliationActualJson.Canonicalize(
                item.ValueElement.Value) != expectedJson)
            throw Fail("BASIC_OWNER_VALUE_MISMATCH");
    }

    private static void ProveBasicText(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        BasicItem item,
        long count,
        ImmutableArray<StatisticReconciliationActualRawPayloadEnvelope> sources,
        ImmutableArray<string> sourceReportIds,
        int maxTextChars)
    {
        if (item.Dto.DataType is not ("TEXT" or "STRING_LIST") ||
            item.Dto.Operation is not ("COUNT" or "JOIN") ||
            item.Dto.Operation == "JOIN" &&
                !descriptor.Operations.Contains("VALUES", StringComparer.Ordinal) ||
            !BasicNonNumericZeroMetadata(item.Dto, count) ||
            item.Dto.TrueCount is not null || item.Dto.FalseCount is not null ||
            item.Dto.MinDateUtc is not null || item.Dto.MaxDateUtc is not null ||
            item.Dto.Buckets.Count != 0)
            throw Fail("BASIC_OWNER_TEXT_SHAPE_UNPROVABLE");
        var expectedAtomCount = atoms.Where(value => value.AtomKind == "TEXT" &&
                value.TransitionLeg == "NONE")
            .Sum(value => value.OccurrenceCount);
        var text = BuildBasicExpectedText(
            descriptor, sources, sourceReportIds, maxTextChars);
        if (expectedAtomCount != count || text.ValueCount != count ||
            item.Dto.Text != text.Text ||
            (item.Dto.TextCharCount ?? 0) != text.TextCharCount ||
            item.Dto.TextTruncated != text.Truncated)
            throw Fail("BASIC_OWNER_TEXT_MISMATCH");
        if (item.Dto.Operation == "COUNT")
        {
            if (!BasicJsonDecimalEquals(item.ValueElement, count))
                throw Fail("BASIC_OWNER_VALUE_MISMATCH");
        }
        else if (!BasicJsonStringEquals(item.ValueElement, text.Text))
            throw Fail("BASIC_OWNER_VALUE_MISMATCH");
    }

    private static long BasicContributingReportCount(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        ImmutableArray<StatisticReconciliationActualRawPayloadEnvelope> sources)
    {
        long count = 0;
        foreach (var source in sources)
        {
            using var document = StatisticReconciliationActualJson.ParseStrict(
                source.CanonicalPayloadJson, "BASIC_RAW_SOURCE_PAYLOAD");
            if (!TryResolveBasicPointer(document.RootElement,
                    descriptor.JsonPointer, out var value))
                continue;
            if (descriptor.ExpandArray && value.ValueKind == JsonValueKind.Array)
            {
                if (value.EnumerateArray().Any(BasicRawValuePresent))
                    count++;
            }
            else if (BasicRawValuePresent(value))
                count++;
        }
        return count;
    }

    private static bool BasicRawValuePresent(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => value.GetString()!.Length != 0,
            JsonValueKind.Array => value.GetArrayLength() != 0,
            _ => true
        };

    private static BasicExpectedText BuildBasicExpectedText(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        ImmutableArray<StatisticReconciliationActualRawPayloadEnvelope> sources,
        ImmutableArray<string> sourceReportIds,
        int maximum)
    {
        var byReport = sources.ToDictionary(value => value.ReportId,
            StringComparer.Ordinal);
        var output = new StringBuilder();
        long valueCount = 0;
        long characterCount = 0;
        var truncated = false;
        foreach (var reportId in sourceReportIds)
        {
            var source = byReport[reportId];
            using var document = StatisticReconciliationActualJson.ParseStrict(
                source.CanonicalPayloadJson, "BASIC_RAW_TEXT_PAYLOAD");
            if (!TryResolveBasicPointer(document.RootElement,
                    descriptor.JsonPointer, out var value))
                continue;
            IEnumerable<JsonElement> values = descriptor.ExpandArray &&
                value.ValueKind == JsonValueKind.Array
                    ? value.EnumerateArray().Select(item => item.Clone())
                    : [value.Clone()];
            foreach (var candidate in values)
            {
                if (candidate.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(candidate.GetString()))
                    continue;
                var text = candidate.GetString()!.Trim();
                valueCount++;
                characterCount = checked(characterCount + text.Length);
                if (truncated)
                    continue;
                var segment = (output.Length == 0 ? string.Empty :
                    Environment.NewLine) + text;
                var remaining = maximum - output.Length;
                if (remaining <= 0)
                {
                    truncated = true;
                    continue;
                }
                if (segment.Length > remaining)
                {
                    output.Append(segment.AsSpan(0, remaining));
                    truncated = true;
                }
                else
                    output.Append(segment);
            }
        }
        if (characterCount > int.MaxValue || valueCount > int.MaxValue)
            throw Fail("BASIC_OWNER_TEXT_CARDINALITY_INVALID");
        return new BasicExpectedText(valueCount,
            output.Length == 0 ? null : output.ToString(),
            (int)characterCount, truncated);
    }

    private static bool TryResolveBasicPointer(
        JsonElement root,
        string pointer,
        out JsonElement value)
    {
        value = root;
        if (pointer.Length == 0)
            return true;
        foreach (var raw in pointer[1..].Split('/'))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty(segment, out value))
                    return false;
                continue;
            }
            if (value.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var index) &&
                index >= 0 && (segment == "0" || !segment.StartsWith('0')) &&
                index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }
            return false;
        }
        return true;
    }

    private static BasicOptionalDecimal OptionalBasicDecimal(
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string operation)
        => descriptor.Operations.Contains(operation, StringComparer.Ordinal)
            ? new BasicOptionalDecimal(true, Decimal(atoms, operation))
            : new BasicOptionalDecimal(false, null);

    private static BasicOptionalString OptionalBasicTypedValue(
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string operation)
    {
        if (!descriptor.Operations.Contains(operation, StringComparer.Ordinal))
            return new BasicOptionalString(false, null);
        var atom = One(atoms, operation);
        if (atom.ValueState == "MISSING" && atom.CanonicalValue.Length == 0 &&
            atom.OccurrenceCount == 0)
            return new BasicOptionalString(true, null);
        if (atom.ValueType != descriptor.ValueType || atom.ValueState != "VALUE" ||
            atom.OccurrenceCount != 1)
            throw Fail("BASIC_RAW_TYPED_ATOM_INVALID");
        return new BasicOptionalString(true, atom.CanonicalValue);
    }

    private static long BasicTypedOccurrence(
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        string atomKind,
        string canonical,
        string valueType)
    {
        var found = atoms.Where(value => value.AtomKind == atomKind &&
            value.TransitionLeg == "NONE" &&
            value.CanonicalValue == canonical).ToArray();
        if (found.Length == 0)
            return 0;
        if (found.Length != 1 || found[0].ValueType != valueType ||
            found[0].ValueState != "VALUE" || found[0].OccurrenceCount <= 0)
            throw Fail("BASIC_RAW_TYPED_ATOM_INVALID");
        return found[0].OccurrenceCount;
    }

    private static string BasicBucketKey(
        StatisticReconciliationActualRawSummaryAtom atom,
        string valueType)
    {
        if (atom.ValueType != valueType || atom.ValueState != "VALUE" ||
            atom.OccurrenceCount <= 0)
            throw Fail("BASIC_RAW_BUCKET_ATOM_INVALID");
        if (valueType == "ENUM")
            return atom.CanonicalValue;
        if (atom.CanonicalValue.StartsWith("S:", StringComparison.Ordinal) ||
            atom.CanonicalValue.StartsWith("N:", StringComparison.Ordinal) ||
            atom.CanonicalValue.StartsWith("B:", StringComparison.Ordinal))
            return atom.CanonicalValue[2..];
        throw Fail("BASIC_RAW_BUCKET_ATOM_INVALID");
    }

    private static bool BasicNonNumericZeroMetadata(
        WorkAssignmentBasicSummaryItemDto dto,
        long count)
        => dto.Min is null && dto.Max is null &&
           dto.Sum == (count > 0 ? 0m : null) &&
           dto.Mean == (count > 0 ? 0m : null);

    private static bool BasicJsonDecimalEquals(JsonElement? value, decimal? expected)
    {
        if (expected is null)
            return value is null || value.Value.ValueKind == JsonValueKind.Null;
        return value is { ValueKind: JsonValueKind.Number } &&
            value.Value.TryGetDecimal(out var found) && found == expected.Value;
    }

    private static bool BasicJsonDateEquals(JsonElement? value, DateTime? expected)
    {
        if (expected is null)
            return value is null || value.Value.ValueKind == JsonValueKind.Null;
        return value is { ValueKind: JsonValueKind.String } &&
            DateTime.TryParse(value.Value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var found) &&
            found.ToUniversalTime() == expected.Value.ToUniversalTime();
    }

    private static bool BasicJsonStringEquals(JsonElement? value, string? expected)
        => expected is null
            ? value is null || value.Value.ValueKind == JsonValueKind.Null
            : value is { ValueKind: JsonValueKind.String } &&
              value.Value.GetString() == expected;

    private static bool BasicDateMatches(string? canonical, DateTime? actual)
    {
        if (canonical is null)
            return actual is null;
        if (actual is null || actual.Value.Kind != DateTimeKind.Utc)
            return false;
        var expected = canonical.StartsWith("YEAR:", StringComparison.Ordinal)
            ? canonical[5..] + "-01-01"
            : canonical.StartsWith("MONTH:", StringComparison.Ordinal)
                ? canonical[6..] + "-01"
                : canonical.StartsWith("DAY:", StringComparison.Ordinal)
                    ? canonical[4..]
                    : null;
        return expected is not null &&
            DateTime.TryParseExact(expected, "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var date) && actual.Value == date;
    }

    private static void RequireExactProperties(
        JsonElement value,
        ImmutableHashSet<string> allowed,
        string reason)
    {
        if (value.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
            throw Fail(reason);
    }

    private static string RequiredCompactString(
        JsonElement value,
        string property,
        string reason)
        => StrictOptionalString(value, property) ?? throw Fail(reason);

    private static int RequiredCompactInteger(
        JsonElement value,
        string property,
        string reason)
        => value.TryGetProperty(property, out var found) &&
           found.ValueKind == JsonValueKind.Number && found.TryGetInt32(out var number)
            ? number : throw Fail(reason);

    private static string? StrictOptionalString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var found) ||
            found.ValueKind == JsonValueKind.Null)
            return null;
        if (found.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(found.GetString()) ||
            found.GetString() != found.GetString()!.Trim())
            throw Fail($"BASIC_{property.ToUpperInvariant()}_STRING_INVALID");
        return found.GetString();
    }

    private static bool HasNonNullProperty(JsonElement value, string property)
        => value.TryGetProperty(property, out var found) &&
           found.ValueKind != JsonValueKind.Null;

    private sealed record BasicOwnerView(
        BasicPeriodSelection Period,
        int MaxTextChars,
        BasicItem[] Items);

    private sealed record BasicPeriodSelection(
        string Mode,
        string? Key,
        string? From,
        string? To);

    private sealed record BasicCompactOverride(
        string? MetricKey,
        string? RowKey,
        string? ColumnKey,
        string? Operation,
        string? Label);

    private sealed record BasicNullRun(
        int Start,
        int EndExclusive,
        int PackedIndex,
        int RemovedThrough);

    private sealed record BasicOptionalDecimal(bool IsApplicable, decimal? Value);
    private sealed record BasicOptionalString(bool IsApplicable, string? Value);
    private sealed record BasicExpectedText(
        long ValueCount,
        string? Text,
        int TextCharCount,
        bool Truncated);
}
