using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public sealed partial class WorkReportStatisticDiffService
{
    private async Task<P9DiffComputation> BuildP9DiffResultAsync(
        WorkAssignment assignment,
        WorkReportStatisticDiffConfigPayload payload,
        int limit,
        CancellationToken ct)
    {
        var scanLimit = Math.Clamp(limit * 100, MinProjectionScanLimit, MaxProjectionScanLimit);
        var left = await LoadP9DiffSideAsync(
            assignment, payload.Left!, "LEFT", scanLimit, ct);
        var right = await LoadP9DiffSideAsync(
            assignment, payload.Right!, "RIGHT", scanLimit, ct);
        var leftFacts = left.Facts;
        var rightFacts = right.Facts;
        var leftSelector = payload.Left!.Selector!;
        var rightSelector = payload.Right!.Selector!;
        if (string.Equals(payload.Direction,
                WorkReportStatisticDiffConfigContract.RightToLeft,
                StringComparison.Ordinal))
        {
            (leftFacts, rightFacts) = (rightFacts, leftFacts);
            (leftSelector, rightSelector) = (rightSelector, leftSelector);
        }

        var keys = leftFacts.Keys.Concat(rightFacts.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (keys.Length > limit)
            keys = keys.Take(limit).ToArray();
        var rows = new List<P9StatisticDiffResultRow>(keys.Length);
        for (var ordinal = 0; ordinal < keys.Length; ordinal++)
        {
            var key = keys[ordinal];
            var leftValue = leftFacts.TryGetValue(key, out var l)
                ? CloneP9DiffValue(l)
                : P9DiffMissing(leftSelector.DataType!);
            var rightValue = rightFacts.TryGetValue(key, out var r)
                ? CloneP9DiffValue(r)
                : P9DiffMissing(rightSelector.DataType!);
            ApplyP9DiffPolicies(
                leftValue,
                rightValue,
                payload.MissingPolicy!,
                payload.EmptyPolicy!);
            var comparison = CompareP9DiffValues(leftValue, rightValue);
            rows.Add(new P9StatisticDiffResultRow
            {
                RowId = P9DiffSha256(
                    $"{leftSelector.ConceptKind}\n{leftSelector.ConceptKey}\n{key}"),
                Ordinal = ordinal,
                Key = key,
                ConceptKind = leftSelector.ConceptKind!,
                ConceptKey = leftSelector.ConceptKey!,
                Left = leftValue,
                Right = rightValue,
                Equal = comparison.Equal,
                DifferenceKind = comparison.DifferenceKind,
                NumericDelta = comparison.NumericDelta
            });
        }
        return new P9DiffComputation(
            rows,
            left.SourcePins.Concat(right.SourcePins)
                .GroupBy(pin => $"{pin.Side}:{pin.SourceReportId}:{pin.DirectGenerationId}",
                    StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(pin => pin.Side, StringComparer.Ordinal)
                .ThenBy(pin => pin.SourceReportId, StringComparer.Ordinal)
                .ToArray());
    }

    private async Task<P9DiffSideData> LoadP9DiffSideAsync(
        WorkAssignment assignment,
        WorkReportStatisticDiffSidePayload side,
        string sideName,
        int scanLimit,
        CancellationToken ct)
    {
        var selector = side.Selector!;
        var sourceScope = WorkAssignmentSummarySourceScope.Normalize(
            assignment,
            side.SourceScope!.Mode,
            side.SourceScope.FlowInstanceId,
            side.SourceScope.FlowStepId,
            side.SourceScope.FlowBranchId,
            side.SourceScope.FlowEffectiveStatus);
        var assignments = await WorkAssignmentSummarySourceScope.LoadAssignmentsAsync(
            _ctx.WorkAssignments,
            assignment,
            assignment.DynamicFormTemplateId!,
            Array.Empty<string>(),
            sourceScope,
            ct);
        var assignmentIds = assignments.Select(item => ObjectId.Parse(item.Id)).ToArray();
        if (assignmentIds.Length == 0)
            return P9DiffSideData.Empty;

        var collectionName = selector.ConceptKind switch
        {
            WorkReportStatisticDiffConfigContract.Field =>
                "work_report_field_stat_values",
            WorkReportStatisticDiffConfigContract.TableMetric =>
                "work_report_table_stat_values",
            WorkReportStatisticDiffConfigContract.RowLabel =>
                "work_report_label_stat_values",
            _ => throw P9DiffValidation("DIFF_CONCEPT_KIND_UNSUPPORTED")
        };
        var filter = new BsonDocument
        {
            ["workId"] = ObjectId.Parse(assignment.WorkId),
            ["workAssignmentId"] = new BsonDocument("$in", new BsonArray(assignmentIds)),
            ["assignmentIsActive"] = true,
            ["reportIsActive"] = true,
            ["reportStatus"] = (int)WorkAssignmentReportStatus.Approved,
            ["isDeleted"] = false,
            ["directProjection"] = new BsonDocument("$type", "object")
        };
        ApplyP9DiffPeriodFilter(filter, side.Period!);
        ApplyP9DiffSelectorFilter(filter, selector);
        var documents = await _ctx.Db.GetCollection<BsonDocument>(collectionName)
            .Find(filter)
            .Sort(new BsonDocument
            {
                ["rowKey"] = 1,
                ["sourceKey"] = 1,
                ["workAssignmentReportId"] = 1,
                ["_id"] = 1
            })
            .Limit(scanLimit + 1)
            .ToListAsync(ct);
        if (documents.Count > scanLimit)
            throw P9DiffValidation("DIFF_SOURCE_SCAN_LIMIT_EXCEEDED");
        var pins = await ValidateP9DiffSourcePinsAsync(
            documents, sideName, ct);
        var facts = selector.ConceptKind switch
        {
            WorkReportStatisticDiffConfigContract.Field =>
                BuildP9DiffFieldFacts(documents, selector),
            WorkReportStatisticDiffConfigContract.TableMetric =>
                BuildP9DiffTableFacts(documents, selector),
            WorkReportStatisticDiffConfigContract.RowLabel =>
                BuildP9DiffLabelFacts(documents, selector),
            _ => new Dictionary<string, P9StatisticDiffTypedValue>(StringComparer.Ordinal)
        };
        return new P9DiffSideData(facts, pins);
    }

    private static void ApplyP9DiffPeriodFilter(
        BsonDocument filter,
        WorkReportStatisticDiffPeriodPayload period)
    {
        if (string.Equals(period.Mode,
                WorkReportStatisticDiffConfigContract.Exact,
                StringComparison.Ordinal))
        {
            filter["periodKey"] = period.PeriodKey!;
            return;
        }
        filter["periodKey"] = new BsonDocument
        {
            ["$gte"] = period.PeriodKeyFrom!,
            ["$lte"] = period.PeriodKeyTo!
        };
    }

    private static void ApplyP9DiffSelectorFilter(
        BsonDocument filter,
        WorkReportStatisticDiffSelectorPayload selector)
    {
        if (selector.ConceptKind == WorkReportStatisticDiffConfigContract.Field)
        {
            filter["conceptCode"] = selector.ConceptCode!;
            filter["fieldId"] = selector.ConceptKey!;
            return;
        }
        var separator = selector.ConceptKey!.IndexOf(':');
        if (separator <= 0 || separator == selector.ConceptKey.Length - 1)
            throw P9DiffValidation("DIFF_CONCEPT_KEY_INVALID");
        filter["blockId"] = selector.ConceptKey[..separator];
        var tail = selector.ConceptKey[(separator + 1)..];
        if (selector.ConceptKind == WorkReportStatisticDiffConfigContract.TableMetric)
        {
            filter["conceptCode"] = selector.ConceptCode!;
            filter["metricKey"] = tail;
        }
        else
            filter["labelCode"] = tail;
    }

    private async Task<IReadOnlyList<P9StatisticDiffSourcePin>>
        ValidateP9DiffSourcePinsAsync(
            IReadOnlyList<BsonDocument> documents,
            string sideName,
            CancellationToken ct)
    {
        if (documents.Count == 0)
            return Array.Empty<P9StatisticDiffSourcePin>();
        var rawPins = documents.Select(document => document["directProjection"].AsBsonDocument)
            .ToArray();
        var reportIds = rawPins.Select(pin => pin["sourceReportId"].AsObjectId)
            .Distinct().ToArray();
        var runIds = rawPins.Select(pin => pin["runId"].AsObjectId)
            .Distinct().ToArray();
        var reports = await _ctx.Db.GetCollection<BsonDocument>("work_assignment_report")
            .Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(reportIds))))
            .ToListAsync(ct);
        var jobs = await _ctx.Db.GetCollection<BsonDocument>("work_report_statistic_rebuild_jobs")
            .Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(runIds))))
            .ToListAsync(ct);
        var reportMap = reports.ToDictionary(item => item["_id"].AsObjectId);
        var jobMap = jobs.ToDictionary(item => item["_id"].AsObjectId);
        var result = new List<P9StatisticDiffSourcePin>();
        foreach (var pin in rawPins)
        {
            var reportId = pin["sourceReportId"].AsObjectId;
            var runId = pin["runId"].AsObjectId;
            if (!reportMap.TryGetValue(reportId, out var report) ||
                report.GetValue("status", -1).ToInt32() !=
                    (int)WorkAssignmentReportStatus.Approved ||
                !report.GetValue("isCurrent", false).ToBoolean() ||
                !report.GetValue("isActive", false).ToBoolean() ||
                report.GetValue("isDeleted", true).ToBoolean() ||
                report.GetValue("payloadRevision", -1).ToInt32() !=
                    pin["sourcePayloadRevision"].ToInt32() ||
                !string.Equals(report.GetValue("payloadHash", "").AsString,
                    pin["sourcePayloadHash"].AsString, StringComparison.Ordinal) ||
                report.GetValue("lifecycleRevision", -1).ToInt32() !=
                    pin["sourceLifecycleRevision"].ToInt32())
                throw P9DiffValidation("DIFF_SOURCE_LIFECYCLE_STALE");
            if (!jobMap.TryGetValue(runId, out var job) ||
                !string.Equals(job.GetValue("status", "").AsString,
                    WorkReportStatisticRebuildJobStatuses.Completed,
                    StringComparison.Ordinal) ||
                !job.GetValue("isCurrentPublication", false).ToBoolean() ||
                !string.Equals(job.GetValue("generationId", "").AsString,
                    pin["generationId"].AsString, StringComparison.Ordinal))
                throw P9DiffValidation("DIFF_DIRECT_GENERATION_NOT_PUBLISHED");
            result.Add(new P9StatisticDiffSourcePin
            {
                Side = sideName,
                SourceReportId = reportId.ToString(),
                SourcePayloadRevision = pin["sourcePayloadRevision"].ToInt32(),
                SourcePayloadHash = pin["sourcePayloadHash"].AsString,
                SourceLifecycleRevision = pin["sourceLifecycleRevision"].ToInt32(),
                DirectRunId = runId.ToString(),
                DirectGenerationId = pin["generationId"].AsString
            });
        }
        return result;
    }

    private static IReadOnlyDictionary<string, P9StatisticDiffTypedValue>
        BuildP9DiffFieldFacts(
            IReadOnlyList<BsonDocument> documents,
            WorkReportStatisticDiffSelectorPayload selector)
    {
        if (documents.Count == 0)
            return new Dictionary<string, P9StatisticDiffTypedValue>(StringComparer.Ordinal);
        return new Dictionary<string, P9StatisticDiffTypedValue>(StringComparer.Ordinal)
        {
            [$"FIELD:{selector.ConceptKey}"] =
                AggregateP9DiffValues(documents, selector.DataType!, isLabel: false)
        };
    }

    private static IReadOnlyDictionary<string, P9StatisticDiffTypedValue>
        BuildP9DiffTableFacts(
            IReadOnlyList<BsonDocument> documents,
            WorkReportStatisticDiffSelectorPayload selector)
        => documents.GroupBy(
                document => document.GetValue("rowKey", "").AsString,
                StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => $"ROW:{group.Key}",
                group => AggregateP9DiffValues(
                    group.ToArray(), selector.DataType!, isLabel: false),
                StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, P9StatisticDiffTypedValue>
        BuildP9DiffLabelFacts(
            IReadOnlyList<BsonDocument> documents,
            WorkReportStatisticDiffSelectorPayload selector)
        => documents.GroupBy(
                document => document.GetValue("rowKey", "").AsString,
                StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => $"ROW:{group.Key}",
                group => AggregateP9DiffValues(
                    group.ToArray(), selector.DataType!, isLabel: true),
                StringComparer.Ordinal);

    private static P9StatisticDiffTypedValue AggregateP9DiffValues(
        IReadOnlyList<BsonDocument> documents,
        string dataType,
        bool isLabel)
    {
        if (documents.Any(document => document.GetValue("redacted", false).ToBoolean()))
            return new P9StatisticDiffTypedValue
            {
                State = P9StatisticDiffValueStates.Redacted,
                DataType = dataType
            };
        if (isLabel)
        {
            if (dataType == WorkReportStatisticDiffConfigContract.Number)
                return P9DiffNumber(documents.Count);
            var labels = documents.Select(document => document["labelCode"].AsString)
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            return new P9StatisticDiffTypedValue
            {
                State = P9StatisticDiffValueStates.Value,
                DataType = dataType,
                CanonicalValue = string.Join("\u001f", labels),
                ChoiceIds = labels
            };
        }
        if (documents.Any(document =>
                string.Equals(document.GetValue("valueKind", "").AsString,
                    "NULL", StringComparison.Ordinal)))
            return new P9StatisticDiffTypedValue
            {
                State = P9StatisticDiffValueStates.Null,
                DataType = dataType
            };
        return dataType switch
        {
            WorkReportStatisticDiffConfigContract.Number =>
                P9DiffNumber(documents.Sum(ReadP9DiffDecimal)),
            WorkReportStatisticDiffConfigContract.Boolean =>
                P9DiffBoolean(documents.Select(document =>
                        document.GetValue("booleanValue", BsonNull.Value))
                    .Where(value => !value.IsBsonNull)
                    .Select(value => value.ToBoolean())
                    .Distinct().SingleOrDefault()),
            WorkReportStatisticDiffConfigContract.Date =>
                P9DiffDate(documents.Select(ReadP9DiffDate)
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value)
                    .OrderBy(value => value)
                    .FirstOrDefault()),
            WorkReportStatisticDiffConfigContract.Choice =>
                P9DiffChoice(documents.Select(document =>
                        document.GetValue("bucketKey", "").AsString)
                    .Where(value => !string.IsNullOrWhiteSpace(value))),
            _ => P9DiffText(documents.Select(document =>
                    document.GetValue("textValue", "").AsString))
        };
    }

    private static decimal ReadP9DiffDecimal(BsonDocument document)
    {
        var value = document.GetValue("numericValue",
            document.GetValue("value", BsonNull.Value));
        return value.IsBsonNull ? 0m : value.ToDecimal();
    }

    private static DateTime? ReadP9DiffDate(BsonDocument document)
    {
        var value = document.GetValue("dateValueUtc",
            document.GetValue("dateValue", BsonNull.Value));
        return value.IsBsonNull ? null : value.ToUniversalTime();
    }

    private static P9StatisticDiffTypedValue P9DiffNumber(decimal value)
        => new()
        {
            State = P9StatisticDiffValueStates.Value,
            DataType = WorkReportStatisticDiffConfigContract.Number,
            NumericValue = value,
            CanonicalValue = value.ToString("G29", CultureInfo.InvariantCulture)
        };

    private static P9StatisticDiffTypedValue P9DiffBoolean(bool value)
        => new()
        {
            State = P9StatisticDiffValueStates.Value,
            DataType = WorkReportStatisticDiffConfigContract.Boolean,
            BooleanValue = value,
            CanonicalValue = value ? "true" : "false"
        };

    private static P9StatisticDiffTypedValue P9DiffDate(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return new P9StatisticDiffTypedValue
        {
            State = P9StatisticDiffValueStates.Value,
            DataType = WorkReportStatisticDiffConfigContract.Date,
            DateValueUtc = utc,
            CanonicalValue = utc.ToString("O", CultureInfo.InvariantCulture)
        };
    }

    private static P9StatisticDiffTypedValue P9DiffChoice(IEnumerable<string> values)
    {
        var choices = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        return new P9StatisticDiffTypedValue
        {
            State = choices.Count == 0
                ? P9StatisticDiffValueStates.Empty
                : P9StatisticDiffValueStates.Value,
            DataType = WorkReportStatisticDiffConfigContract.Choice,
            CanonicalValue = choices.Count == 0 ? string.Empty : string.Join("\u001f", choices),
            ChoiceIds = choices
        };
    }

    private static P9StatisticDiffTypedValue P9DiffText(IEnumerable<string> values)
    {
        var text = string.Join("\u001f", values.OrderBy(value => value, StringComparer.Ordinal));
        return new P9StatisticDiffTypedValue
        {
            State = text.Length == 0
                ? P9StatisticDiffValueStates.Empty
                : P9StatisticDiffValueStates.Value,
            DataType = WorkReportStatisticDiffConfigContract.Text,
            CanonicalValue = text
        };
    }

    private static P9StatisticDiffTypedValue P9DiffMissing(string dataType)
        => new()
        {
            State = P9StatisticDiffValueStates.Missing,
            DataType = dataType
        };

    private static void ApplyP9DiffPolicies(
        P9StatisticDiffTypedValue left,
        P9StatisticDiffTypedValue right,
        string missingPolicy,
        string emptyPolicy)
    {
        foreach (var value in new[] { left, right })
        {
            if (value.State == P9StatisticDiffValueStates.Empty)
            {
                if (emptyPolicy == WorkReportStatisticDiffConfigContract.Reject)
                    throw P9DiffValidation("DIFF_EMPTY_VALUE_REJECTED");
                if (emptyPolicy == WorkReportStatisticDiffConfigContract.AsMissing)
                    value.State = P9StatisticDiffValueStates.Missing;
            }
            if (value.State != P9StatisticDiffValueStates.Missing)
                continue;
            if (missingPolicy == WorkReportStatisticDiffConfigContract.Reject)
                throw P9DiffValidation("DIFF_MISSING_VALUE_REJECTED");
            if (missingPolicy == WorkReportStatisticDiffConfigContract.AsZero)
            {
                if (value.DataType != WorkReportStatisticDiffConfigContract.Number)
                    throw P9DiffValidation("DIFF_AS_ZERO_NON_NUMERIC");
                var zero = P9DiffNumber(0m);
                value.State = zero.State;
                value.CanonicalValue = zero.CanonicalValue;
                value.NumericValue = zero.NumericValue;
            }
        }
    }

    public static P9DiffTypedComparison CompareP9DiffValues(
        P9StatisticDiffTypedValue left,
        P9StatisticDiffTypedValue right)
    {
        if (left.State == P9StatisticDiffValueStates.Redacted ||
            right.State == P9StatisticDiffValueStates.Redacted)
            return new P9DiffTypedComparison(true, "REDACTED", null);
        if (!string.Equals(left.State, right.State, StringComparison.Ordinal))
            return new P9DiffTypedComparison(false, "STATE_CHANGED", null);
        if (left.State != P9StatisticDiffValueStates.Value)
            return new P9DiffTypedComparison(true, "UNCHANGED", null);
        if (!string.Equals(left.DataType, right.DataType, StringComparison.Ordinal))
            throw P9DiffValidation("DIFF_DATA_TYPE_INCOMPATIBLE");
        var equal = string.Equals(
            left.CanonicalValue, right.CanonicalValue, StringComparison.Ordinal);
        var delta = left.DataType == WorkReportStatisticDiffConfigContract.Number &&
                    left.NumericValue.HasValue && right.NumericValue.HasValue
            ? left.NumericValue.Value - right.NumericValue.Value
            : (decimal?)null;
        return new P9DiffTypedComparison(
            equal,
            equal ? "UNCHANGED" : "VALUE_CHANGED",
            delta);
    }

    private static P9StatisticDiffTypedValue CloneP9DiffValue(
        P9StatisticDiffTypedValue value)
        => new()
        {
            State = value.State,
            DataType = value.DataType,
            CanonicalValue = value.CanonicalValue,
            NumericValue = value.NumericValue,
            BooleanValue = value.BooleanValue,
            DateValueUtc = value.DateValueUtc,
            ChoiceIds = value.ChoiceIds.ToList()
        };
}

internal sealed record P9DiffSideData(
    IReadOnlyDictionary<string, P9StatisticDiffTypedValue> Facts,
    IReadOnlyList<P9StatisticDiffSourcePin> SourcePins)
{
    public static readonly P9DiffSideData Empty = new(
        new Dictionary<string, P9StatisticDiffTypedValue>(StringComparer.Ordinal),
        Array.Empty<P9StatisticDiffSourcePin>());
}

public sealed record P9DiffTypedComparison(
    bool Equal,
    string DifferenceKind,
    decimal? NumericDelta);
