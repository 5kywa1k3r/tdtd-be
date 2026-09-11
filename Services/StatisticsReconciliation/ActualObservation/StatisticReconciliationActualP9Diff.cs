using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed class StatisticReconciliationActualP9DiffAdapter
{
    internal const int MaxRows = 100_000;
    internal const int MaxSourcePins = 100_000;
    internal const string P10DeltaNotComputed = "NOT_COMPUTED";
    private const string CandidatePrompt = "P9-06";
    private const int CandidateStage = 4;
    private const string CandidateCatalogRaw =
        "b26b24d1bdf9337d85c3ab01c700e357b8a56080f2e808332d1ba612bceb9c68";
    private const string CandidateCatalogSemantic =
        "b4de97a6975b94e4a4da4b7148844ba846af837d283f0e0065762aad10ffdb36";
    private const string CandidateStageLock =
        "e237f0e260f0ba2704ccb830a9b3aca1bceb7c88eaca52dc679f6b08b9126397";

    internal async Task<ActualP9DiffCapture> CaptureAsync(
        ActualP9DiffOwnerBoundary boundary,
        IStatisticReconciliationActualP9DiffOwnerReader reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(reader);
        var normalized = NormalizeBoundary(boundary);
        var owner = await reader.ReadResultAsync(normalized, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Fail("P9_DIFF_OWNER_RESULT_NOT_FOUND");
        RequireRootBoundary(normalized, owner);
        if (owner.Rows is null || owner.Rows.Count > MaxRows)
            throw Fail("P9_DIFF_ROWS_LIMIT");
        if (owner.SourcePins is null || owner.SourcePins.Count > MaxSourcePins)
            throw Fail("P9_DIFF_SOURCE_PINS_LIMIT");

        using var leftPeriod = StatisticReconciliationActualJson.ParseStrict(
            owner.LeftPeriodJson, "P9_DIFF_LEFT_PERIOD_JSON");
        using var rightPeriod = StatisticReconciliationActualJson.ParseStrict(
            owner.RightPeriodJson, "P9_DIFF_RIGHT_PERIOD_JSON");
        var leftPeriodCanonical = StatisticReconciliationActualJson.Canonicalize(
            leftPeriod.RootElement);
        var rightPeriodCanonical = StatisticReconciliationActualJson.Canonicalize(
            rightPeriod.RootElement);
        var leftPeriodShape = PeriodShape(leftPeriod.RootElement);
        var rightPeriodShape = PeriodShape(rightPeriod.RootElement);
        var periodsValid = leftPeriodShape.Valid && rightPeriodShape.Valid
                           && Eq(leftPeriodShape.Mode, rightPeriodShape.Mode);
        var ownerMetadataValid = OwnerMetadataValid(owner);

        var effectiveKind = owner.Direction == "RIGHT_TO_LEFT"
            ? owner.RightConceptKind : owner.LeftConceptKind;
        var effectiveKey = owner.Direction == "RIGHT_TO_LEFT"
            ? owner.RightConceptKey : owner.LeftConceptKey;
        var rowsCanonical = true;
        var typedRowsValid = true;
        var comparisonsConsistent = true;
        var declaredTypesMatch = true;
        var rowIds = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var rows = ImmutableArray.CreateBuilder<ActualP9DiffRowObservation>(owner.Rows.Count);
        string? priorRowKey = null;
        var expectedLeftType = owner.Direction == "RIGHT_TO_LEFT"
            ? owner.RightDataType : owner.LeftDataType;
        var expectedRightType = owner.Direction == "RIGHT_TO_LEFT"
            ? owner.LeftDataType : owner.RightDataType;
        for (var index = 0; index < owner.Rows.Count; index++)
        {
            var row = owner.Rows[index] ?? throw Fail("P9_DIFF_ROW_NULL");
            var key = Required(row.Key, "P9_DIFF_ROW_KEY");
            var conceptKind = Upper(row.ConceptKind, "P9_DIFF_ROW_CONCEPT_KIND");
            var conceptKey = Required(row.ConceptKey, "P9_DIFF_ROW_CONCEPT_KEY");
            var rowId = OwnerSha(row.RowId, "P9_DIFF_ROW_ID");
            var expectedRowId = RawSha256($"{effectiveKind}\n{effectiveKey}\n{key}");
            if (row.Ordinal != index || !rowIds.Add(rowId) || !keys.Add(key)
                || !Eq(rowId, expectedRowId)
                || !Eq(conceptKind, effectiveKind)
                || !Eq(conceptKey, effectiveKey)
                || !ProducerKeyValid(owner, key)
                || priorRowKey is not null
                   && StringComparer.Ordinal.Compare(priorRowKey, key) >= 0)
                rowsCanonical = false;
            priorRowKey = key;
            var left = Typed(
                row.Left, "LEFT", owner.MissingPolicy, owner.EmptyPolicy, conceptKind);
            var right = Typed(
                row.Right, "RIGHT", owner.MissingPolicy, owner.EmptyPolicy, conceptKind);
            var comparisonConsistent = ComparisonConsistent(row, left, right);
            var typesMatch = Eq(left.DataType, expectedLeftType)
                             && Eq(right.DataType, expectedRightType);
            typedRowsValid &= left.TypedShapeValid && right.TypedShapeValid;
            comparisonsConsistent &= comparisonConsistent;
            declaredTypesMatch &= typesMatch;
            var semantic = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_P9_DIFF_ROW_V1",
                rowId,
                StatisticReconciliationActualCanonical.Integer(row.Ordinal),
                key, conceptKind, conceptKey,
                left.SemanticSha256, right.SemanticSha256,
                StatisticReconciliationActualCanonical.Boolean(row.Equal),
                row.DifferenceKind,
                row.NumericDelta.HasValue
                    ? StatisticReconciliationActualCanonical.Number(row.NumericDelta.Value)
                    : null,
                StatisticReconciliationActualCanonical.Boolean(comparisonConsistent),
                StatisticReconciliationActualCanonical.Boolean(typesMatch),
                P10DeltaNotComputed);
            rows.Add(new ActualP9DiffRowObservation(
                rowId, row.Ordinal, key, conceptKind, conceptKey,
                left, right, row.Equal,
                Required(row.DifferenceKind, "P9_DIFF_DIFFERENCE_KIND"),
                row.NumericDelta, comparisonConsistent, typesMatch,
                P10DeltaNotComputed, null, semantic));
        }
        if (owner.LeftConceptKind == "FIELD")
        {
            var maximumFieldKeys = Eq(owner.LeftConceptKey, owner.RightConceptKey)
                ? 1 : 2;
            if (owner.Rows.Count > maximumFieldKeys)
                rowsCanonical = false;
        }

        var sourcePinsCanonical = true;
        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        var sourcePins = ImmutableArray.CreateBuilder<ActualP9DiffSourcePinObservation>(
            owner.SourcePins.Count);
        string? priorOwnerTuple = null;
        for (var index = 0; index < owner.SourcePins.Count; index++)
        {
            var pin = owner.SourcePins[index] ?? throw Fail("P9_DIFF_SOURCE_PIN_NULL");
            var side = Upper(pin.Side, "P9_DIFF_SOURCE_SIDE");
            if (side is not ("LEFT" or "RIGHT")
                || pin.SourcePayloadRevision < 1 || pin.SourceLifecycleRevision < 1)
                throw Fail("P9_DIFF_SOURCE_PIN_INVALID");
            var reportId = Required(pin.SourceReportId, "P9_DIFF_SOURCE_REPORT_ID");
            var generationId = OwnerSha(
                pin.DirectGenerationId, "P9_DIFF_DIRECT_GENERATION_ID");
            var key = $"{side}\u001f{reportId}\u001f{generationId}";
            var ownerTuple = $"{side}\u001f{reportId}";
            if (!sourceKeys.Add(key)
                || priorOwnerTuple is not null
                   && StringComparer.Ordinal.Compare(priorOwnerTuple, ownerTuple) > 0)
                sourcePinsCanonical = false;
            priorOwnerTuple = ownerTuple;
            var semantic = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_P9_DIFF_SOURCE_PIN_V1",
                StatisticReconciliationActualCanonical.Integer(index),
                side, reportId,
                StatisticReconciliationActualCanonical.Integer(pin.SourcePayloadRevision),
                OwnerSha(pin.SourcePayloadHash, "P9_DIFF_SOURCE_PAYLOAD_SHA256"),
                StatisticReconciliationActualCanonical.Integer(pin.SourceLifecycleRevision),
                Required(pin.DirectRunId, "P9_DIFF_DIRECT_RUN_ID"),
                generationId);
            sourcePins.Add(new ActualP9DiffSourcePinObservation(
                index, side, reportId, pin.SourcePayloadRevision,
                OwnerSha(pin.SourcePayloadHash, "P9_DIFF_SOURCE_PAYLOAD_SHA256"),
                pin.SourceLifecycleRevision,
                Required(pin.DirectRunId, "P9_DIFF_DIRECT_RUN_ID"),
                generationId,
                semantic));
        }
        sourcePinsCanonical &= (owner.Rows.Count == 0)
                               == (owner.SourcePins.Count == 0);

        var observedResultSha = ComputeP9ResultSha(owner);
        var storedResultSha = string.IsNullOrWhiteSpace(owner.ResultHash)
            ? null : owner.ResultHash;
        var totalsMatch = owner.TotalRowCount == owner.Rows.Count
                          && owner.EqualRowCount == owner.Rows.Count(x => x.Equal)
                          && owner.ChangedRowCount == owner.Rows.Count(x => !x.Equal)
                          && owner.TotalRowCount == owner.EqualRowCount + owner.ChangedRowCount;
        var status = Upper(owner.Status, "P9_DIFF_STATUS");
        var lifecycle = OwnerLifecycleSemantic(owner, status);
        var completedResult = !owner.IsDeleted
                              && status == P9StatisticDiffResultStatuses.Completed
                              && owner.IsCurrent && owner.IsFresh && !owner.IsDirty
                              && owner.CompletedAtUtc?.Kind == DateTimeKind.Utc
                              && owner.LeaseOwner is null && !owner.LeaseExpiresAtUtc.HasValue
                              && !owner.ExpiresAtUtc.HasValue
                              && string.IsNullOrWhiteSpace(owner.FailureCode)
                              && string.IsNullOrWhiteSpace(owner.FailureMessage);
        var usableResult = completedResult
                           && Eq(storedResultSha, observedResultSha) && totalsMatch
                           && rowsCanonical && sourcePinsCanonical
                           && ownerMetadataValid && periodsValid && typedRowsValid
                           && comparisonsConsistent && declaredTypesMatch;
        var state = new ActualP9DiffOwnerState(
            status,
            owner.IsCurrent,
            owner.IsFresh,
            owner.IsDirty,
            owner.IsDeleted,
            completedResult,
            Eq(storedResultSha, observedResultSha),
            totalsMatch,
            rowsCanonical,
            sourcePinsCanonical,
            ownerMetadataValid,
            periodsValid,
            typedRowsValid,
            comparisonsConsistent,
            declaredTypesMatch,
            usableResult,
            lifecycle);
        var rowArray = rows.MoveToImmutable();
        var sourceArray = sourcePins.MoveToImmutable();
        var captureSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_P9_TYPED_DIFF_CAPTURE_V1",
            BoundarySemantic(normalized),
            owner.LeftConceptKind, owner.LeftConceptKey, owner.LeftConceptCode,
            owner.LeftDataType, StatisticReconciliationActualJson.RawSha256(leftPeriodCanonical),
            owner.RightConceptKind, owner.RightConceptKey, owner.RightConceptCode,
            owner.RightDataType, StatisticReconciliationActualJson.RawSha256(rightPeriodCanonical),
            owner.Direction, owner.MissingPolicy, owner.EmptyPolicy, owner.TimeAxis,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_P9_DIFF_SOURCE_PINS_V1",
                sourceArray.Select(x => x.SemanticSha256)),
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_P9_DIFF_ROWS_V1",
                rowArray.Select(x => x.SemanticSha256)),
            storedResultSha,
            observedResultSha,
            lifecycle,
            StatisticReconciliationActualCanonical.Boolean(totalsMatch),
            StatisticReconciliationActualCanonical.Boolean(rowsCanonical),
            StatisticReconciliationActualCanonical.Boolean(sourcePinsCanonical),
            StatisticReconciliationActualCanonical.Boolean(ownerMetadataValid),
            StatisticReconciliationActualCanonical.Boolean(periodsValid),
            StatisticReconciliationActualCanonical.Boolean(typedRowsValid),
            StatisticReconciliationActualCanonical.Boolean(comparisonsConsistent),
            StatisticReconciliationActualCanonical.Boolean(declaredTypesMatch),
            P10DeltaNotComputed);

        return new ActualP9DiffCapture(
            normalized,
            Required(owner.LeftConceptKind, "P9_DIFF_LEFT_CONCEPT_KIND"),
            Required(owner.LeftConceptKey, "P9_DIFF_LEFT_CONCEPT_KEY"),
            Required(owner.LeftConceptCode, "P9_DIFF_LEFT_CONCEPT_CODE"),
            Required(owner.LeftDataType, "P9_DIFF_LEFT_DATA_TYPE"),
            leftPeriodCanonical,
            Required(owner.RightConceptKind, "P9_DIFF_RIGHT_CONCEPT_KIND"),
            Required(owner.RightConceptKey, "P9_DIFF_RIGHT_CONCEPT_KEY"),
            Required(owner.RightConceptCode, "P9_DIFF_RIGHT_CONCEPT_CODE"),
            Required(owner.RightDataType, "P9_DIFF_RIGHT_DATA_TYPE"),
            rightPeriodCanonical,
            Required(owner.Direction, "P9_DIFF_DIRECTION"),
            Required(owner.MissingPolicy, "P9_DIFF_MISSING_POLICY"),
            Required(owner.EmptyPolicy, "P9_DIFF_EMPTY_POLICY"),
            Required(owner.TimeAxis, "P9_DIFF_TIME_AXIS"),
            sourceArray,
            rowArray,
            storedResultSha,
            observedResultSha,
            state,
            P10DeltaNotComputed,
            null,
            captureSha);
    }

    private static ActualP9DiffOwnerBoundary NormalizeBoundary(
        ActualP9DiffOwnerBoundary value)
    {
        if (value.ConfigVersionNo < 1 || value.ConfigRevision < 1)
            throw Fail("P9_DIFF_BOUNDARY_REVISION_INVALID");
        if (value.CandidateStage != CandidateStage
            || !Eq(value.CandidatePromptId, CandidatePrompt)
            || !Eq(value.CandidateCatalogRawSha256, CandidateCatalogRaw)
            || !Eq(value.CandidateCatalogSemanticSha256, CandidateCatalogSemantic)
            || !Eq(value.CandidateStageLockSha256, CandidateStageLock))
            throw Fail("P9_DIFF_FROZEN_CANDIDATE_MISMATCH");
        return value with
        {
            ResultId = Required(value.ResultId, "P9_DIFF_RESULT_ID"),
            RunId = Required(value.RunId, "P9_DIFF_RUN_ID"),
            WorkId = Required(value.WorkId, "P9_DIFF_WORK_ID"),
            AssignmentId = Required(value.AssignmentId, "P9_DIFF_ASSIGNMENT_ID"),
            DynamicFormTemplateId = Required(value.DynamicFormTemplateId, "P9_DIFF_FORM_TEMPLATE_ID"),
            ConfigId = Required(value.ConfigId, "P9_DIFF_CONFIG_ID"),
            ConfigVersionId = Required(value.ConfigVersionId, "P9_DIFF_CONFIG_VERSION_ID"),
            ConfigSha256 = Sha(value.ConfigSha256, "P9_DIFF_CONFIG_SHA256"),
            DependencyPins = Pins(value.DependencyPins),
            CandidateChainId = Required(value.CandidateChainId, "P9_DIFF_CANDIDATE_CHAIN_ID"),
            CandidatePromptId = CandidatePrompt,
            CandidateCatalogRawSha256 = CandidateCatalogRaw,
            CandidateCatalogSemanticSha256 = CandidateCatalogSemantic,
            CandidateStageLockSha256 = CandidateStageLock
        };
    }

    private static void RequireRootBoundary(
        ActualP9DiffOwnerBoundary boundary,
        WorkReportStatisticDiffResult owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ValidateOwnerEnums(owner);
        if (!Eq(owner.Id, boundary.ResultId) || !Eq(owner.RunId, boundary.RunId)
            || !Eq(owner.WorkId, boundary.WorkId)
            || !Eq(owner.AssignmentId, boundary.AssignmentId)
            || !Eq(owner.DynamicFormTemplateId, boundary.DynamicFormTemplateId)
            || !Eq(owner.ConfigId, boundary.ConfigId)
            || !Eq(owner.ConfigVersionId, boundary.ConfigVersionId)
            || owner.ConfigVersionNo != boundary.ConfigVersionNo
            || owner.ConfigRevision != boundary.ConfigRevision
            || !Eq(owner.ConfigHash, boundary.ConfigSha256)
            || !owner.DependencyPins.SequenceEqual(boundary.DependencyPins, StringComparer.Ordinal)
            || !Eq(owner.CandidateChainId, boundary.CandidateChainId)
            || !Eq(owner.CandidatePromptId, boundary.CandidatePromptId)
            || owner.CandidateStage != boundary.CandidateStage
            || !Eq(owner.CandidateCatalogRawSha256, boundary.CandidateCatalogRawSha256)
            || !Eq(owner.CandidateCatalogSemanticSha256, boundary.CandidateCatalogSemanticSha256)
            || !Eq(owner.CandidateStageLockSha256, boundary.CandidateStageLockSha256))
            throw Fail("P9_DIFF_OWNER_BOUNDARY_MISMATCH");
    }

    private static bool OwnerMetadataValid(WorkReportStatisticDiffResult owner)
        => Eq(owner.LeftConceptKind, owner.RightConceptKind)
           && Eq(owner.LeftConceptCode, owner.RightConceptCode)
           && Eq(owner.LeftDataType, owner.RightDataType)
           && CanonicalConceptCode(owner.LeftConceptCode)
           && CanonicalConceptCode(owner.RightConceptCode)
           && (owner.MissingPolicy != "AS_ZERO"
               || owner.LeftDataType == "NUMBER");

    private static bool ProducerKeyValid(
        WorkReportStatisticDiffResult owner,
        string key)
        => owner.LeftConceptKind == "FIELD"
            ? Eq(key, $"FIELD:{owner.LeftConceptKey}")
              || Eq(key, $"FIELD:{owner.RightConceptKey}")
            : key.StartsWith("ROW:", StringComparison.Ordinal) && key.Length > 4;

    private static bool CanonicalConceptCode(string? value)
        => value is { Length: > 0 and <= 64 }
           && Eq(value, value.ToLowerInvariant())
           && char.IsAsciiLetterOrDigit(value[0])
           && value.All(character => char.IsAsciiLetterOrDigit(character)
                                     || character is '_' or '.' or '-');

    private static PeriodValidation PeriodShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new PeriodValidation(false, null);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "mode", "periodKey", "periodKeyFrom", "periodKeyTo"
        };
        var properties = root.EnumerateObject().Select(x => x.Name).ToArray();
        if (properties.Length != allowed.Count
            || properties.Any(property => !allowed.Contains(property)))
            return new PeriodValidation(false, null);
        if (!TryPeriodString(root, "mode", out var mode)
            || !TryPeriodString(root, "periodKey", out var key)
            || !TryPeriodString(root, "periodKeyFrom", out var from)
            || !TryPeriodString(root, "periodKeyTo", out var to))
            return new PeriodValidation(false, mode);
        var valid = mode switch
        {
            "EXACT" => CanonicalPeriodIdentity(key) && from is null && to is null,
            "RANGE" => key is null && CanonicalPeriodIdentity(from)
                       && CanonicalPeriodIdentity(to)
                       && StringComparer.Ordinal.Compare(from, to) <= 0,
            _ => false
        };
        return new PeriodValidation(valid, mode);
    }

    private static bool TryPeriodString(
        JsonElement root,
        string property,
        out string? value)
    {
        value = null;
        if (!root.TryGetProperty(property, out var element)
            || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString();
        return true;
    }

    private static bool CanonicalPeriodIdentity(string? value)
        => value is { Length: > 0 and <= 128 }
           && Eq(value, value.Trim())
           && !value.Any(char.IsControl);
    private static void ValidateOwnerEnums(WorkReportStatisticDiffResult owner)
    {
        if (owner.Status is not ("PENDING" or "RUNNING" or "COMPLETED" or "FAILED")
            || owner.Direction is not ("LEFT_TO_RIGHT" or "RIGHT_TO_LEFT")
            || owner.LeftConceptKind is not ("FIELD" or "TABLE_METRIC" or "ROW_LABEL")
            || owner.RightConceptKind is not ("FIELD" or "TABLE_METRIC" or "ROW_LABEL")
            || owner.LeftDataType is not ("NUMBER" or "DATE" or "BOOLEAN" or "CHOICE" or "TEXT")
            || owner.RightDataType is not ("NUMBER" or "DATE" or "BOOLEAN" or "CHOICE" or "TEXT")
            || owner.MissingPolicy is not ("REJECT" or "INCLUDE" or "AS_ZERO")
            || owner.EmptyPolicy is not ("REJECT" or "INCLUDE" or "AS_MISSING")
            || owner.TimeAxis != "UTC_GREGORIAN")
            throw Fail("P9_DIFF_OWNER_ENUM_INVALID");
    }

    private static ActualP9DiffTypedObservation Typed(
        P9StatisticDiffTypedValue? value,
        string side,
        string missingPolicy,
        string emptyPolicy,
        string conceptKind)
    {
        if (value is null)
            throw Fail($"P9_DIFF_{side}_VALUE_NULL");
        var state = Upper(value.State, $"P9_DIFF_{side}_STATE");
        var dataType = Upper(value.DataType, $"P9_DIFF_{side}_DATA_TYPE");
        var choices = value.ChoiceIds?.Select(x =>
                Required(x, $"P9_DIFF_{side}_CHOICE_ID"))
            .ToImmutableArray() ?? throw Fail($"P9_DIFF_{side}_CHOICES_NULL");
        var shape = TypedShape(
            value, state, dataType, choices,
            missingPolicy, emptyPolicy, conceptKind);
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_P9_DIFF_TYPED_VALUE_V1",
            state, dataType, value.CanonicalValue,
            value.NumericValue.HasValue
                ? StatisticReconciliationActualCanonical.Number(value.NumericValue.Value) : null,
            value.BooleanValue.HasValue
                ? StatisticReconciliationActualCanonical.Boolean(value.BooleanValue.Value) : null,
            value.DateValueUtc.HasValue
                ? value.DateValueUtc.Value.Kind == DateTimeKind.Utc
                    ? StatisticReconciliationActualCanonical.Instant(value.DateValueUtc.Value)
                    : "NOT_UTC"
                : null,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_P9_DIFF_CHOICES_V1", choices),
            StatisticReconciliationActualCanonical.Boolean(shape));
        return new ActualP9DiffTypedObservation(
            state, dataType, value.CanonicalValue,
            value.NumericValue, value.BooleanValue, value.DateValueUtc,
            choices, shape, semantic);
    }

    private static bool TypedShape(
        P9StatisticDiffTypedValue value,
        string state,
        string dataType,
        ImmutableArray<string> choices,
        string missingPolicy,
        string emptyPolicy,
        string conceptKind)
    {
        if (state is not ("MISSING" or "NULL" or "EMPTY" or "REDACTED" or "VALUE"))
            return false;
        if (dataType is not ("NUMBER" or "DATE" or "BOOLEAN" or "CHOICE" or "TEXT"))
            return false;
        if (state != "VALUE")
        {
            if (value.NumericValue.HasValue || value.BooleanValue.HasValue
                || value.DateValueUtc.HasValue || choices.Length != 0)
                return false;
            if (conceptKind == "ROW_LABEL")
                return state switch
                {
                    "REDACTED" => value.CanonicalValue is null,
                    "MISSING" => missingPolicy == "INCLUDE"
                                 && value.CanonicalValue is null,
                    _ => false
                };
            return state switch
            {
                "NULL" or "REDACTED" => value.CanonicalValue is null,
                "EMPTY" => emptyPolicy == "INCLUDE"
                           && dataType is "TEXT" or "CHOICE"
                           && value.CanonicalValue == string.Empty,
                "MISSING" => missingPolicy == "INCLUDE"
                             && (value.CanonicalValue is null
                                 || emptyPolicy == "AS_MISSING"
                                 && dataType is "TEXT" or "CHOICE"
                                 && value.CanonicalValue == string.Empty),
                _ => false
            };
        }
        if (conceptKind == "ROW_LABEL" && dataType != "NUMBER")
            return choices.Length > 0
                   && choices.SequenceEqual(
                       choices.Distinct(StringComparer.Ordinal)
                           .OrderBy(x => x, StringComparer.Ordinal),
                       StringComparer.Ordinal)
                   && value.CanonicalValue == string.Join("\u001f", choices)
                   && !value.NumericValue.HasValue && !value.BooleanValue.HasValue
                   && !value.DateValueUtc.HasValue;
        return dataType switch
        {
            "NUMBER" => value.NumericValue.HasValue
                        && value.CanonicalValue == value.NumericValue.Value
                            .ToString("G29", CultureInfo.InvariantCulture)
                        && !value.BooleanValue.HasValue && !value.DateValueUtc.HasValue
                        && choices.Length == 0,
            "BOOLEAN" => value.BooleanValue.HasValue
                         && value.CanonicalValue == (value.BooleanValue.Value ? "true" : "false")
                         && !value.NumericValue.HasValue && !value.DateValueUtc.HasValue
                         && choices.Length == 0,
            "DATE" => value.DateValueUtc?.Kind == DateTimeKind.Utc
                      && value.CanonicalValue == value.DateValueUtc.Value
                          .ToString("O", CultureInfo.InvariantCulture)
                      && !value.NumericValue.HasValue && !value.BooleanValue.HasValue
                      && choices.Length == 0,
            "CHOICE" => choices.Length > 0
                         && value.CanonicalValue == string.Join("\u001f", choices)
                        && choices.SequenceEqual(
                            choices.Distinct(StringComparer.Ordinal)
                                .OrderBy(x => x, StringComparer.Ordinal),
                            StringComparer.Ordinal)
                        && !value.NumericValue.HasValue && !value.BooleanValue.HasValue
                        && !value.DateValueUtc.HasValue,
            "TEXT" => !string.IsNullOrEmpty(value.CanonicalValue)
                      && !value.NumericValue.HasValue && !value.BooleanValue.HasValue
                      && !value.DateValueUtc.HasValue && choices.Length == 0,
            _ => false
        };
    }

    private static bool ComparisonConsistent(
        P9StatisticDiffResultRow row,
        ActualP9DiffTypedObservation left,
        ActualP9DiffTypedObservation right)
    {
        bool equal;
        string kind;
        decimal? delta;
        if (left.State == "REDACTED" || right.State == "REDACTED")
            (equal, kind, delta) = (true, "REDACTED", null);
        else if (left.State != right.State)
            (equal, kind, delta) = (false, "STATE_CHANGED", null);
        else if (left.State != "VALUE")
            (equal, kind, delta) = (true, "UNCHANGED", null);
        else if (left.DataType != right.DataType)
            return false;
        else
        {
            equal = Eq(left.CanonicalValue, right.CanonicalValue);
            kind = equal ? "UNCHANGED" : "VALUE_CHANGED";
            delta = left.DataType == "NUMBER"
                    && left.NumericValue.HasValue && right.NumericValue.HasValue
                ? left.NumericValue.Value - right.NumericValue.Value
                : null;
        }
        return row.Equal == equal && Eq(row.DifferenceKind, kind)
               && row.NumericDelta == delta;
    }

    private static string ComputeP9ResultSha(WorkReportStatisticDiffResult owner)
    {
        var element = JsonSerializer.SerializeToElement(new
        {
            owner.ConfigVersionId,
            owner.ConfigHash,
            owner.Direction,
            owner.MissingPolicy,
            owner.EmptyPolicy,
            rows = owner.Rows
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return StatisticReconciliationActualJson.CanonicalSha256(element);
    }

    private static string OwnerLifecycleSemantic(
        WorkReportStatisticDiffResult owner,
        string status)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_P9_DIFF_OWNER_LIFECYCLE_V1",
            owner.CommandId, owner.RequestHash, owner.ReceiptId,
            owner.RequestedByUserId, status, owner.JobId,
            StatisticReconciliationActualCanonical.Integer(owner.AttemptNo),
            owner.LeaseOwner, Instant(owner.LeaseExpiresAtUtc),
            StatisticReconciliationActualCanonical.Integer(owner.FenceToken),
            StatisticReconciliationActualCanonical.Integer(owner.TotalRowCount),
            StatisticReconciliationActualCanonical.Integer(owner.EqualRowCount),
            StatisticReconciliationActualCanonical.Integer(owner.ChangedRowCount),
            owner.ResultHash,
            StatisticReconciliationActualCanonical.Boolean(owner.IsCurrent),
            StatisticReconciliationActualCanonical.Boolean(owner.IsFresh),
            StatisticReconciliationActualCanonical.Boolean(owner.IsDirty),
            owner.FailureCode, owner.FailureMessage,
            Instant(owner.CompletedAtUtc), Instant(owner.ExpiresAtUtc),
            StatisticReconciliationActualCanonical.Boolean(owner.IsDeleted),
            StatisticReconciliationActualCanonical.Instant(owner.CreatedAtUtc),
            StatisticReconciliationActualCanonical.Instant(owner.UpdatedAtUtc),
            owner.CreatedByUserId, owner.UpdatedByUserId);

    private static string BoundarySemantic(ActualP9DiffOwnerBoundary value)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_P9_DIFF_BOUNDARY_V1",
            value.ResultId, value.RunId, value.WorkId, value.AssignmentId,
            value.DynamicFormTemplateId, value.ConfigId, value.ConfigVersionId,
            StatisticReconciliationActualCanonical.Integer(value.ConfigVersionNo),
            StatisticReconciliationActualCanonical.Integer(value.ConfigRevision),
            value.ConfigSha256,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_P9_DIFF_DEPENDENCIES_V1", value.DependencyPins),
            value.CandidateChainId, value.CandidatePromptId,
            StatisticReconciliationActualCanonical.Integer(value.CandidateStage),
            value.CandidateCatalogRawSha256, value.CandidateCatalogSemanticSha256,
            value.CandidateStageLockSha256);

    private static ImmutableArray<string> Pins(ImmutableArray<string> values)
    {
        if (values.IsDefault || values.Length > 10_000)
            throw Fail("P9_DIFF_DEPENDENCY_PINS_INVALID");
        var normalized = values.Select(x => Required(x, "P9_DIFF_DEPENDENCY_PIN"))
            .ToImmutableArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw Fail("P9_DIFF_DEPENDENCY_PINS_DUPLICATE");
        if (!normalized.SequenceEqual(normalized.OrderBy(x => x, StringComparer.Ordinal),
                StringComparer.Ordinal))
            throw Fail("P9_DIFF_DEPENDENCY_PINS_ORDER_INVALID");
        return normalized;
    }

    private static string RawSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);
    private static string Upper(string? value, string name)
    {
        var required = StatisticReconciliationActualCanonical.Required(value, name);
        if (!StringComparer.Ordinal.Equals(required, required.ToUpperInvariant()))
            throw Fail($"{name}_NON_CANONICAL_CASE" );
        return required;
    }
    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);
    private static string OwnerSha(string? value, string name)
    {
        var required = Required(value, name);
        if (!Eq(required, required.ToLowerInvariant()))
            throw Fail($"{name}_NON_CANONICAL_CASE" );
        return StatisticReconciliationActualCanonical.Sha256(required, name);
    }
    private static bool Eq(string? left, string? right)
        => StringComparer.Ordinal.Equals(left, right);
    private static string? Instant(DateTime? value)
        => value.HasValue
            ? StatisticReconciliationActualCanonical.Instant(
                value.Value.Kind == DateTimeKind.Utc
                    ? value.Value
                    : throw Fail("P9_DIFF_OWNER_TIME_NOT_UTC"))
            : null;
    private static StatisticReconciliationActualObservationException Fail(string reason)
        => new(reason);
    private sealed record PeriodValidation(bool Valid, string? Mode);
}
