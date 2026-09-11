using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualBoundaryDomains
{
    internal const string Source = "SOURCE";
    internal const string Configuration = "CONFIGURATION";
    internal const string Catalog = "CATALOG";
    internal const string Runtime = "RUNTIME";
    internal const string Result = "RESULT";
    internal const string Export = "EXPORT";

    internal static readonly ImmutableArray<string> Required =
    [
        Source,
        Configuration,
        Catalog,
        Runtime,
        Result,
        Export
    ];
}

internal static class StatisticReconciliationActualCoherentLayers
{
    internal const string SourceMembership = "SOURCE_MEMBERSHIP";
    internal const string DirectProjection = "DIRECT_PROJECTION";
    internal const string Aggregate = "AGGREGATE";
    internal const string Basic = "BASIC";
    internal const string Advanced = "ADVANCED";
    internal const string Diff = "DIFF";
    internal const string Api = "API";
    internal const string Export = "EXPORT";

    internal static readonly ImmutableArray<string> RequiredOrder =
    [
        SourceMembership,
        DirectProjection,
        Aggregate,
        Basic,
        Advanced,
        Diff,
        Api,
        Export
    ];
}

internal static class StatisticReconciliationActualCoherentCaptureStates
{
    internal const string Ready = "READY";
    internal const string Stale = "STALE";
}

internal sealed record StatisticReconciliationActualBoundaryPin(
    string Domain,
    string OwnerKey,
    string GenerationKey,
    long Revision,
    string SemanticSha256);

internal sealed record StatisticReconciliationActualCoherentBoundary(
    string ReconciliationId,
    string WorkId,
    string ScopeAssignmentId,
    string SourceSetSha256,
    string ConfigurationBundleSha256,
    string FilterSha256,
    string AuthorizationSnapshotSha256,
    string SourcePinSetSha256,
    string ConfigurationPinSetSha256,
    string CatalogPinSetSha256,
    string RuntimePinSetSha256,
    string ResultPinSetSha256,
    string ExportPinSetSha256,
    ImmutableArray<StatisticReconciliationActualBoundaryPin> Pins,
    string BoundarySemanticSha256);

internal interface IStatisticReconciliationActualCoherentBoundaryReader
{
    Task<StatisticReconciliationActualCoherentBoundary> ReadAsync(
        CancellationToken cancellationToken);
}

internal sealed record StatisticReconciliationActualLayerCapture(
    string Layer,
    string BoundarySemanticSha256,
    string CaptureSemanticSha256,
    long ObservationCount,
    string CaptureState,
    string? CaptureReason,
    string OwnerId,
    string OwnerVersionSha256,
    ImmutableArray<StatisticReconciliationActualTypedObservation>
        TypedObservations = default);

internal sealed record StatisticReconciliationActualCoherentCaptureStep(
    string Layer,
    Func<
        StatisticReconciliationActualCoherentBoundary,
        CancellationToken,
        Task<StatisticReconciliationActualLayerCapture>> CaptureAsync);

internal sealed record StatisticReconciliationActualCoherentLayerObservation(
    int Ordinal,
    string Layer,
    string BoundarySemanticSha256,
    string CaptureSemanticSha256,
    long ObservationCount,
    string CaptureState,
    string? CaptureReason,
    bool BoundaryMatches,
    string LayerSemanticSha256,
    string OwnerId,
    string OwnerVersionSha256,
    string TypedObservationManifestSha256,
    ImmutableArray<StatisticReconciliationActualTypedObservation>
        TypedObservations);

/// <summary>
/// Immutable T24 result consumed by the T25 append-only observation owner.
/// This type grants no persistence or publication by itself. The T25 owner may
/// consume it only when ReadyForAppendOnlyPersistence is true; T25 owns the
/// append-only write followed by the one-CAS publication boundary.
/// </summary>
internal sealed record StatisticReconciliationActualCoherentGeneration(
    string ReconciliationId,
    string GenerationId,
    string GenerationSemanticSha256,
    StatisticReconciliationActualCoherentBoundary CommonBoundary,
    StatisticReconciliationActualCoherentBoundary FinalBoundary,
    ImmutableArray<StatisticReconciliationActualCoherentLayerObservation>
        OrderedLayerObservations,
    string LayerManifestSha256,
    string CaptureState,
    string? StaleReason,
    bool BoundaryStable,
    bool Complete,
    bool ReadyForAppendOnlyPersistence,
    bool PublicationForbidden,
    bool MatchAndSignOffForbidden);

internal sealed class StatisticReconciliationActualCoherentCaptureCoordinator
{
    internal async Task<StatisticReconciliationActualCoherentGeneration>
        CaptureAsync(
            string reconciliationId,
            IStatisticReconciliationActualCoherentBoundaryReader boundaryReader,
            ImmutableArray<StatisticReconciliationActualCoherentCaptureStep> steps,
            CancellationToken cancellationToken = default,
            StatisticReconciliationActualCoherentBoundary? pinnedInitialBoundary = null,
            Func<CancellationToken, Task<string?>>? finalGuardAsync = null)
    {
        reconciliationId = StatisticReconciliationActualCanonical.Required(
            reconciliationId,
            "COHERENT_RECONCILIATION_ID");
        ArgumentNullException.ThrowIfNull(boundaryReader);
        var normalizedSteps = NormalizeSteps(steps);

        cancellationToken.ThrowIfCancellationRequested();
        var commonBoundary = NormalizeBoundary(
            pinnedInitialBoundary ??
            await boundaryReader.ReadAsync(cancellationToken));
        if (!StringComparer.Ordinal.Equals(
                commonBoundary.ReconciliationId,
                reconciliationId))
        {
            throw Fail("COHERENT_RECONCILIATION_BINDING_MISMATCH");
        }

        var observations = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualCoherentLayerObservation>(
            StatisticReconciliationActualCoherentLayers.RequiredOrder.Length);
        var typedBudget =
            new StatisticReconciliationActualTypedObservationCanonical
                .GenerationBudget();
        string? staleReason = null;

        for (var ordinal = 0; ordinal < normalizedSteps.Length; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = normalizedSteps[ordinal];
            var capture = await step.CaptureAsync(
                commonBoundary,
                cancellationToken);
            var observation = ObserveLayer(
                ordinal,
                step.Layer,
                commonBoundary.BoundarySemanticSha256,
                capture,
                typedBudget);
            observations.Add(observation);

            if (!observation.BoundaryMatches)
            {
                staleReason = $"LAYER_BOUNDARY_MISMATCH:{step.Layer}";
                break;
            }
            if (observation.CaptureState ==
                StatisticReconciliationActualCoherentCaptureStates.Stale)
            {
                staleReason = $"LAYER_STALE:{step.Layer}:{observation.CaptureReason}";
                break;
            }
        }

        // This is deliberately a second authoritative read after all layer
        // readers and immediately before this result can become persistence-
        // eligible. T25/T26 must not replace it with client-provided pins.
        cancellationToken.ThrowIfCancellationRequested();
        var finalBoundary = NormalizeBoundary(
            await boundaryReader.ReadAsync(cancellationToken));
        if (!StringComparer.Ordinal.Equals(
                finalBoundary.ReconciliationId,
                reconciliationId))
        {
            throw Fail("COHERENT_FINAL_RECONCILIATION_BINDING_MISMATCH");
        }

        var boundaryStable = StringComparer.Ordinal.Equals(
            commonBoundary.BoundarySemanticSha256,
            finalBoundary.BoundarySemanticSha256);
        if (!boundaryStable)
            staleReason = "BOUNDARY_DRIFT";
        if (finalGuardAsync is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var finalGuardReason = await finalGuardAsync(cancellationToken)
                .ConfigureAwait(false);
            if (finalGuardReason is not null)
            {
                staleReason ??=
                    $"FINAL_GUARD:{RequiredUpper(finalGuardReason, "FINAL_GUARD_REASON")}";
            }
        }

        var ordered = observations.ToImmutable();
        var complete = ordered.Length ==
                       StatisticReconciliationActualCoherentLayers.RequiredOrder.Length;
        if (staleReason is null && !complete)
            throw Fail("COHERENT_LAYER_SET_INCOMPLETE");

        var state = staleReason is null
            ? StatisticReconciliationActualCoherentCaptureStates.Ready
            : StatisticReconciliationActualCoherentCaptureStates.Stale;
        var layerManifestSha256 = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_COHERENT_LAYER_MANIFEST_V2",
            ordered.Select(item => item.LayerSemanticSha256));
        var generationSemanticSha256 = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_COHERENT_GENERATION_V2",
            reconciliationId,
            commonBoundary.BoundarySemanticSha256,
            finalBoundary.BoundarySemanticSha256,
            layerManifestSha256,
            state,
            staleReason,
            StatisticReconciliationActualCanonical.Boolean(boundaryStable),
            StatisticReconciliationActualCanonical.Boolean(complete));
        var generationId = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_COHERENT_GENERATION_ID_V2",
            reconciliationId,
            generationSemanticSha256);
        var hasComparableTypedObservation = ordered.Any(item =>
            !item.TypedObservations.IsDefaultOrEmpty);
        var readyForPersistence = state ==
                                  StatisticReconciliationActualCoherentCaptureStates.Ready &&
                                  boundaryStable &&
                                  complete &&
                                  hasComparableTypedObservation &&
                                  ordered.All(item =>
                                      item.BoundaryMatches &&
                                      item.CaptureState ==
                                      StatisticReconciliationActualCoherentCaptureStates.Ready);

        return new StatisticReconciliationActualCoherentGeneration(
            reconciliationId,
            generationId,
            generationSemanticSha256,
            commonBoundary,
            finalBoundary,
            ordered,
            layerManifestSha256,
            state,
            staleReason,
            boundaryStable,
            complete,
            readyForPersistence,
            PublicationForbidden: !readyForPersistence,
            MatchAndSignOffForbidden: true);
    }

    internal static StatisticReconciliationActualCoherentBoundary CreateBoundary(
        string reconciliationId,
        string workId,
        string scopeAssignmentId,
        string sourceSetSha256,
        string configurationBundleSha256,
        string filterSha256,
        string authorizationSnapshotSha256,
        IEnumerable<StatisticReconciliationActualBoundaryPin> pins)
    {
        reconciliationId = StatisticReconciliationActualCanonical.Required(
            reconciliationId,
            "COHERENT_RECONCILIATION_ID");
        workId = StatisticReconciliationActualCanonical.Required(
            workId,
            "COHERENT_WORK_ID");
        scopeAssignmentId = StatisticReconciliationActualCanonical.Required(
            scopeAssignmentId,
            "COHERENT_SCOPE_ASSIGNMENT_ID");
        sourceSetSha256 = StatisticReconciliationActualCanonical.Sha256(
            sourceSetSha256,
            "COHERENT_SOURCE_SET_SHA256");
        configurationBundleSha256 = StatisticReconciliationActualCanonical.Sha256(
            configurationBundleSha256,
            "COHERENT_CONFIGURATION_BUNDLE_SHA256");
        filterSha256 = StatisticReconciliationActualCanonical.Sha256(
            filterSha256,
            "COHERENT_FILTER_SHA256");
        authorizationSnapshotSha256 = StatisticReconciliationActualCanonical.Sha256(
            authorizationSnapshotSha256,
            "COHERENT_AUTHORIZATION_SNAPSHOT_SHA256");
        ArgumentNullException.ThrowIfNull(pins);

        var normalizedPins = pins
            .Select(NormalizePin)
            .OrderBy(item => item.Domain, StringComparer.Ordinal)
            .ThenBy(item => item.OwnerKey, StringComparer.Ordinal)
            .ToImmutableArray();
        if (normalizedPins.Length == 0 || normalizedPins.Length > 4096)
            throw Fail("COHERENT_BOUNDARY_PINS_INVALID");
        for (var index = 1; index < normalizedPins.Length; index++)
        {
            var prior = normalizedPins[index - 1];
            var current = normalizedPins[index];
            if (StringComparer.Ordinal.Equals(prior.Domain, current.Domain) &&
                StringComparer.Ordinal.Equals(prior.OwnerKey, current.OwnerKey))
            {
                throw Fail("COHERENT_BOUNDARY_PIN_DUPLICATE");
            }
        }

        var actualDomains = normalizedPins
            .Select(item => item.Domain)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var requiredDomains = StatisticReconciliationActualBoundaryDomains.Required
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        if (!actualDomains.SequenceEqual(requiredDomains, StringComparer.Ordinal))
            throw Fail("COHERENT_BOUNDARY_DOMAINS_INVALID");

        foreach (var group in normalizedPins.GroupBy(
                     item => item.Domain,
                     StringComparer.Ordinal))
        {
            if (group.Select(item => item.GenerationKey)
                .Distinct(StringComparer.Ordinal)
                .Skip(1)
                .Any())
            {
                throw Fail($"COHERENT_{group.Key}_GENERATION_MIXED");
            }
        }

        string PinSet(string domain) => StatisticReconciliationActualCanonical.HashSequence(
            $"P10_ACTUAL_COHERENT_{domain}_PIN_SET_V1",
            normalizedPins
                .Where(item => item.Domain == domain)
                .Select(PinSha));

        var sourcePins = PinSet(StatisticReconciliationActualBoundaryDomains.Source);
        var configurationPins = PinSet(
            StatisticReconciliationActualBoundaryDomains.Configuration);
        var catalogPins = PinSet(StatisticReconciliationActualBoundaryDomains.Catalog);
        var runtimePins = PinSet(StatisticReconciliationActualBoundaryDomains.Runtime);
        var resultPins = PinSet(StatisticReconciliationActualBoundaryDomains.Result);
        var exportPins = PinSet(StatisticReconciliationActualBoundaryDomains.Export);
        var boundarySha256 = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_COHERENT_BOUNDARY_V1",
            reconciliationId,
            workId,
            scopeAssignmentId,
            sourceSetSha256,
            configurationBundleSha256,
            filterSha256,
            authorizationSnapshotSha256,
            sourcePins,
            configurationPins,
            catalogPins,
            runtimePins,
            resultPins,
            exportPins,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_COHERENT_ALL_PINS_V1",
                normalizedPins.Select(PinSha)));

        return new StatisticReconciliationActualCoherentBoundary(
            reconciliationId,
            workId,
            scopeAssignmentId,
            sourceSetSha256,
            configurationBundleSha256,
            filterSha256,
            authorizationSnapshotSha256,
            sourcePins,
            configurationPins,
            catalogPins,
            runtimePins,
            resultPins,
            exportPins,
            normalizedPins,
            boundarySha256);
    }

    private static ImmutableArray<StatisticReconciliationActualCoherentCaptureStep>
        NormalizeSteps(
            ImmutableArray<StatisticReconciliationActualCoherentCaptureStep> steps)
    {
        if (steps.IsDefault ||
            steps.Length !=
            StatisticReconciliationActualCoherentLayers.RequiredOrder.Length)
        {
            throw Fail("COHERENT_LAYER_SET_INVALID");
        }

        var normalized = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualCoherentCaptureStep>(steps.Length);
        for (var index = 0; index < steps.Length; index++)
        {
            var step = steps[index] ?? throw Fail("COHERENT_LAYER_STEP_REQUIRED");
            var layer = RequiredUpper(step.Layer, "COHERENT_LAYER");
            if (!StringComparer.Ordinal.Equals(
                    layer,
                    StatisticReconciliationActualCoherentLayers.RequiredOrder[index]))
            {
                throw Fail("COHERENT_LAYER_ORDER_INVALID");
            }
            if (step.CaptureAsync is null)
                throw Fail("COHERENT_LAYER_CAPTURE_REQUIRED");
            normalized.Add(step with { Layer = layer });
        }
        return normalized.MoveToImmutable();
    }

    private static StatisticReconciliationActualCoherentBoundary NormalizeBoundary(
        StatisticReconciliationActualCoherentBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        var normalized = CreateBoundary(
            boundary.ReconciliationId,
            boundary.WorkId,
            boundary.ScopeAssignmentId,
            boundary.SourceSetSha256,
            boundary.ConfigurationBundleSha256,
            boundary.FilterSha256,
            boundary.AuthorizationSnapshotSha256,
            boundary.Pins);
        var exact =
            normalized.ReconciliationId == boundary.ReconciliationId &&
            normalized.WorkId == boundary.WorkId &&
            normalized.ScopeAssignmentId == boundary.ScopeAssignmentId &&
            normalized.SourceSetSha256 == boundary.SourceSetSha256 &&
            normalized.ConfigurationBundleSha256 ==
            boundary.ConfigurationBundleSha256 &&
            normalized.FilterSha256 == boundary.FilterSha256 &&
            normalized.AuthorizationSnapshotSha256 ==
            boundary.AuthorizationSnapshotSha256 &&
            normalized.SourcePinSetSha256 == boundary.SourcePinSetSha256 &&
            normalized.ConfigurationPinSetSha256 ==
            boundary.ConfigurationPinSetSha256 &&
            normalized.CatalogPinSetSha256 == boundary.CatalogPinSetSha256 &&
            normalized.RuntimePinSetSha256 == boundary.RuntimePinSetSha256 &&
            normalized.ResultPinSetSha256 == boundary.ResultPinSetSha256 &&
            normalized.ExportPinSetSha256 == boundary.ExportPinSetSha256 &&
            normalized.BoundarySemanticSha256 ==
            boundary.BoundarySemanticSha256 &&
            normalized.Pins.SequenceEqual(boundary.Pins);
        if (!exact)
            throw Fail("COHERENT_BOUNDARY_SEMANTIC_MISMATCH");
        return normalized;
    }

    private static StatisticReconciliationActualCoherentLayerObservation ObserveLayer(
        int ordinal,
        string expectedLayer,
        string expectedBoundarySha256,
        StatisticReconciliationActualLayerCapture capture,
        StatisticReconciliationActualTypedObservationCanonical.GenerationBudget
            typedBudget)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var layer = RequiredUpper(capture.Layer, "COHERENT_CAPTURE_LAYER");
        if (!StringComparer.Ordinal.Equals(layer, expectedLayer))
            throw Fail("COHERENT_CAPTURE_LAYER_MISMATCH");
        var boundarySha256 = StatisticReconciliationActualCanonical.Sha256(
            capture.BoundarySemanticSha256,
            "COHERENT_CAPTURE_BOUNDARY_SHA256");
        var captureSha256 = StatisticReconciliationActualCanonical.Sha256(
            capture.CaptureSemanticSha256,
            "COHERENT_CAPTURE_SEMANTIC_SHA256");
        if (capture.ObservationCount < 0)
            throw Fail("COHERENT_CAPTURE_COUNT_INVALID");
        var typed = StatisticReconciliationActualTypedObservationCanonical
            .NormalizeSet(
                capture.TypedObservations.IsDefault
                    ? []
                    : capture.TypedObservations,
                typedBudget);
        var ownerId = StatisticReconciliationActualCanonical.Required(
            capture.OwnerId,
            "COHERENT_CAPTURE_OWNER_ID");
        var ownerVersionSha256 = StatisticReconciliationActualCanonical.Sha256(
            capture.OwnerVersionSha256,
            "COHERENT_CAPTURE_OWNER_VERSION_SHA256");
        if (typed.Any(item => item.Layer != layer))
            throw Fail("COHERENT_CAPTURE_TYPED_LAYER_MISMATCH");        var typedManifestSha256 =
            StatisticReconciliationActualTypedObservationCanonical
                .ManifestSha256(typed);
        var state = RequiredUpper(capture.CaptureState, "COHERENT_CAPTURE_STATE");
        if (state is not (
                StatisticReconciliationActualCoherentCaptureStates.Ready or
                StatisticReconciliationActualCoherentCaptureStates.Stale))
        {
            throw Fail("COHERENT_CAPTURE_STATE_INVALID");
        }
        var reason = StatisticReconciliationActualCanonical.Optional(
            capture.CaptureReason,
            "COHERENT_CAPTURE_REASON");
        if ((state == StatisticReconciliationActualCoherentCaptureStates.Ready) !=
            (reason is null))
        {
            throw Fail("COHERENT_CAPTURE_REASON_INVALID");
        }

        var boundaryMatches = StringComparer.Ordinal.Equals(
            boundarySha256,
            expectedBoundarySha256);
        var semanticSha256 = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_COHERENT_LAYER_V2",
            StatisticReconciliationActualCanonical.Integer(ordinal),
            layer,
            expectedBoundarySha256,
            boundarySha256,
            captureSha256,
            StatisticReconciliationActualCanonical.Integer(
                capture.ObservationCount),
            StatisticReconciliationActualCanonical.Integer(typed.Length),
            state,
            reason,
            StatisticReconciliationActualCanonical.Boolean(boundaryMatches),
            ownerId,
            ownerVersionSha256,
            typedManifestSha256);
        return new StatisticReconciliationActualCoherentLayerObservation(
            ordinal,
            layer,
            boundarySha256,
            captureSha256,
            capture.ObservationCount,
            state,
            reason,
            boundaryMatches,
            semanticSha256,
            ownerId,
            ownerVersionSha256,
            typedManifestSha256,
            typed);
    }

    private static StatisticReconciliationActualBoundaryPin NormalizePin(
        StatisticReconciliationActualBoundaryPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        var domain = RequiredUpper(pin.Domain, "COHERENT_PIN_DOMAIN");
        if (!StatisticReconciliationActualBoundaryDomains.Required.Contains(
                domain,
                StringComparer.Ordinal))
        {
            throw Fail("COHERENT_PIN_DOMAIN_INVALID");
        }
        if (pin.Revision < 0)
            throw Fail("COHERENT_PIN_REVISION_INVALID");
        return pin with
        {
            Domain = domain,
            OwnerKey = StatisticReconciliationActualCanonical.Required(
                pin.OwnerKey,
                "COHERENT_PIN_OWNER_KEY"),
            GenerationKey = StatisticReconciliationActualCanonical.Required(
                pin.GenerationKey,
                "COHERENT_PIN_GENERATION_KEY"),
            SemanticSha256 = StatisticReconciliationActualCanonical.Sha256(
                pin.SemanticSha256,
                "COHERENT_PIN_SEMANTIC_SHA256")
        };
    }

    private static string PinSha(StatisticReconciliationActualBoundaryPin pin)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_COHERENT_PIN_V1",
            pin.Domain,
            pin.OwnerKey,
            pin.GenerationKey,
            StatisticReconciliationActualCanonical.Integer(pin.Revision),
            pin.SemanticSha256);

    private static string RequiredUpper(string? value, string name)
    {
        var normalized = StatisticReconciliationActualCanonical.Required(value, name);
        if (!StringComparer.Ordinal.Equals(normalized, normalized.ToUpperInvariant()))
            throw Fail($"{name}_NON_CANONICAL");
        return normalized;
    }

    private static StatisticReconciliationActualObservationException Fail(string reason)
        => new(reason);
}
