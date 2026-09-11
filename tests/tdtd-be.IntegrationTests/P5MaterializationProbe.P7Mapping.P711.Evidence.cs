using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

namespace tdtd_be.IntegrationTests;

internal static partial class P5MaterializationProbe
{
    private const string P711ApplyAuditAction =
        "APPLY_DYNAMIC_FLOW_MAPPING";
    private const string P711RerunAuditAction =
        "RERUN_DYNAMIC_FLOW_MAPPING";

    private static readonly HashSet<string> P711KnownTamperCommandIds =
        new(StringComparer.Ordinal)
        {
            "p708-tamper-forge-apply",
            "p708-tamper-remove-apply"
        };

    private static async Task<P711MongoGlobalSummary>
        CaptureP711MongoGlobalSummaryAsync(
            IMongoDatabase database,
            CancellationToken ct)
    {
        var allReceipts = await database
            .GetCollection<DynamicFlowMappingApplyReceipt>(
                "dynamic_flow_mapping_apply_receipts")
            .Find(FilterDefinition<DynamicFlowMappingApplyReceipt>.Empty)
            .ToListAsync(ct);
        var allProvenance = await database
            .GetCollection<DynamicFlowMappingProvenanceRecord>(
                "dynamic_flow_mapping_provenance")
            .Find(FilterDefinition<DynamicFlowMappingProvenanceRecord>.Empty)
            .ToListAsync(ct);
        var allEvents = await database
            .GetCollection<DynamicFlowMappingEvent>(
                "dynamic_flow_mapping_events")
            .Find(FilterDefinition<DynamicFlowMappingEvent>.Empty)
            .ToListAsync(ct);
        var allOutbox = await database
            .GetCollection<DynamicFlowMappingOutboxItem>(
                "dynamic_flow_mapping_outbox")
            .Find(FilterDefinition<DynamicFlowMappingOutboxItem>.Empty)
            .ToListAsync(ct);
        var allReports = await database
            .GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .Find(FilterDefinition<WorkAssignmentReport>.Empty)
            .ToListAsync(ct);
        var allPayloads = await database
            .GetCollection<WorkReportPayload>(
                "work_report_payloads")
            .Find(FilterDefinition<WorkReportPayload>.Empty)
            .ToListAsync(ct);
        var allSections = await database
            .GetCollection<WorkAssignmentReportSection>(
                "work_assignment_report_sections")
            .Find(FilterDefinition<WorkAssignmentReportSection>.Empty)
            .ToListAsync(ct);
        var allLogs = await database
            .GetCollection<WorkAssignmentReportLog>(
                "work_assignment_report_logs")
            .Find(FilterDefinition<WorkAssignmentReportLog>.Empty)
            .ToListAsync(ct);
        var allTemplates = await database
            .GetCollection<DynamicFormTemplate>(
                "dynamic_form_templates")
            .Find(FilterDefinition<DynamicFormTemplate>.Empty)
            .ToListAsync(ct);

        var excludedReceipts = allReceipts
            .Where(item =>
                P711KnownTamperCommandIds.Contains(item.CommandId))
            .ToArray();
        var excludedKnownTamperFixtureCount =
            excludedReceipts.Length;
        var excludedReceiptIds = excludedReceipts
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var excludedProvenanceIds = excludedReceipts
            .Select(item => item.ProvenanceId)
            .ToHashSet(StringComparer.Ordinal);
        var excludedEventIds = excludedReceipts
            .Select(item => item.EventId)
            .ToHashSet(StringComparer.Ordinal);
        var excludedOutboxIds = excludedReceipts
            .Select(item => item.OutboxIntentId)
            .ToHashSet(StringComparer.Ordinal);
        var excludedPrimaryEvents = allEvents
            .Where(item =>
                excludedReceiptIds.Contains(item.ReceiptId) &&
                excludedEventIds.Contains(item.Id))
            .ToArray();
        var excludedPrimaryEventKeys = excludedPrimaryEvents
            .Select(item => item.EventKey)
            .ToHashSet(StringComparer.Ordinal);
        var excludedCurrentSurfaceTargetIds = excludedReceipts
            .Select(item => item.TargetReportId)
            .ToHashSet(StringComparer.Ordinal);
        var excludedMappingAudits = allLogs
            .Where(item =>
                P711IsMappingAuditAction(item.Action) &&
                item.LifecycleEventKey is not null &&
                excludedPrimaryEventKeys.Contains(
                    item.LifecycleEventKey))
            .ToArray();
        var excludedCurrentSurfaceTargetCount =
            excludedCurrentSurfaceTargetIds.Count;
        var knownTamperScopeValid =
            excludedKnownTamperFixtureCount == 2 &&
            excludedReceipts
                .Select(item => item.CommandId)
                .Distinct(StringComparer.Ordinal)
                .Count() == 2 &&
            excludedPrimaryEvents.Length == 2 &&
            excludedPrimaryEventKeys.Count == 2 &&
            excludedMappingAudits.Length == 2 &&
            excludedCurrentSurfaceTargetCount == 2 &&
            P711KnownTamperCommandIds.All(commandId =>
                excludedReceipts.Count(item =>
                    string.Equals(
                        item.CommandId,
                        commandId,
                        StringComparison.Ordinal)) == 1);

        var receipts = allReceipts
            .Where(item => !excludedReceiptIds.Contains(item.Id))
            .ToList();
        var provenance = allProvenance
            .Where(item =>
                !excludedReceiptIds.Contains(item.ReceiptId) &&
                !excludedProvenanceIds.Contains(item.Id))
            .ToList();
        var events = allEvents
            .Where(item =>
                !excludedReceiptIds.Contains(item.ReceiptId) &&
                !excludedEventIds.Contains(item.Id))
            .ToList();
        var outbox = allOutbox
            .Where(item =>
                !excludedReceiptIds.Contains(item.ReceiptId) &&
                !excludedOutboxIds.Contains(item.Id))
            .ToList();
        var mappingAudits = allLogs
            .Where(item =>
                P711IsMappingAuditAction(item.Action) &&
                (item.LifecycleEventKey is null ||
                 !excludedPrimaryEventKeys.Contains(
                     item.LifecycleEventKey)))
            .ToList();

        var currentSurfaceTargetIds = receipts
            .Select(item => item.TargetReportId)
            .Where(item =>
                !excludedCurrentSurfaceTargetIds.Contains(item))
            .ToHashSet(StringComparer.Ordinal);
        var surfaceReports = allReports
            .Where(item =>
                currentSurfaceTargetIds.Contains(item.Id))
            .ToList();
        var surfacePayloads = allPayloads
            .Where(item =>
                currentSurfaceTargetIds.Contains(item.ReportId) &&
                !item.IsDeleted)
            .ToList();
        var surfaceSections = allSections
            .Where(item =>
                currentSurfaceTargetIds.Contains(
                    item.WorkAssignmentReportId) &&
                !item.IsDeleted)
            .ToList();

        static int DuplicateGroupCount(
            IEnumerable<string> values)
            => values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .GroupBy(value => value, StringComparer.Ordinal)
                .Count(group => group.Count() > 1);

        var duplicateCount =
            DuplicateGroupCount(receipts.Select(item =>
                $"{item.TargetReportId}\n{item.CommandId}")) +
            DuplicateGroupCount(provenance.Select(item =>
                item.ReceiptId)) +
            DuplicateGroupCount(events.Select(item =>
                item.EventKey)) +
            DuplicateGroupCount(outbox.Select(item =>
                item.DedupeKey)) +
            DuplicateGroupCount(mappingAudits.Select(item =>
                item.LifecycleEventKey ?? string.Empty)) +
            DuplicateGroupCount(surfaceReports.Select(item =>
                item.Id)) +
            DuplicateGroupCount(surfacePayloads.Select(item =>
                item.ReportId)) +
            DuplicateGroupCount(surfaceSections.Select(item =>
                $"{item.WorkAssignmentReportId}\n{item.SectionId}")) +
            events
                .Where(item =>
                    string.Equals(
                        item.EventType,
                        DynamicFlowMappingEventTypes
                            .ProvenanceInvalidated,
                        StringComparison.Ordinal))
                .GroupBy(
                    item => item.ProvenanceId,
                    StringComparer.Ordinal)
                .Count(group => group.Count() > 1) +
            outbox
                .Where(item =>
                    string.Equals(
                        item.Operation,
                        DynamicFlowMappingLifecycleContract
                            .RebuildIntentOperation,
                        StringComparison.Ordinal))
                .GroupBy(
                    item => item.ProvenanceId,
                    StringComparer.Ordinal)
                .Count(group => group.Count() > 1);

        static Dictionary<string, T> ById<T>(
            IEnumerable<T> values,
            Func<T, string> key)
            => values
                .Where(item =>
                    !string.IsNullOrWhiteSpace(key(item)))
                .GroupBy(key, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.Ordinal);

        var receiptById = ById(receipts, item => item.Id);
        var provenanceById = ById(provenance, item => item.Id);
        var eventById = ById(events, item => item.Id);
        var outboxById = ById(outbox, item => item.Id);
        var primaryEventByKey = events
            .Where(item =>
                item.EventType is
                    DynamicFlowMappingEventTypes.ApplyCommitted or
                    DynamicFlowMappingEventTypes.RerunCommitted)
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.EventKey))
            .GroupBy(
                item => item.EventKey,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        var auditsByEventKey = mappingAudits
            .Where(item =>
                !string.IsNullOrWhiteSpace(
                    item.LifecycleEventKey))
            .GroupBy(
                item => item.LifecycleEventKey!,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);

        var partialWriteSetCount = 0;
        var primaryHashesValid = true;
        foreach (var receipt in receipts)
        {
            var hasProvenance = provenanceById.TryGetValue(
                receipt.ProvenanceId,
                out var receiptProvenance);
            var hasEvent = eventById.TryGetValue(
                receipt.EventId,
                out var receiptEvent);
            var hasOutbox = outboxById.TryGetValue(
                receipt.OutboxIntentId,
                out var receiptOutbox);
            var auditMatches =
                hasEvent &&
                auditsByEventKey.TryGetValue(
                    receiptEvent!.EventKey,
                    out var matchedAudits)
                    ? matchedAudits
                    : Array.Empty<WorkAssignmentReportLog>();
            var complete =
                hasProvenance &&
                hasEvent &&
                hasOutbox &&
                auditMatches.Length == 1 &&
                string.Equals(
                    receiptProvenance!.ReceiptId,
                    receipt.Id,
                    StringComparison.Ordinal) &&
                string.Equals(
                    receiptEvent!.ReceiptId,
                    receipt.Id,
                    StringComparison.Ordinal) &&
                string.Equals(
                    receiptOutbox!.ReceiptId,
                    receipt.Id,
                    StringComparison.Ordinal);
            if (!complete)
            {
                partialWriteSetCount++;
                primaryHashesValid = false;
                continue;
            }

            var valid =
                P711PrimaryWriteSetHashesValid(
                    receipt,
                    receiptProvenance!,
                    receiptEvent!,
                    receiptOutbox!) &&
                P711MappingAuditValid(
                    auditMatches[0],
                    receipt,
                    receiptProvenance!,
                    receiptEvent!,
                    receiptOutbox!);
            if (!valid)
            {
                partialWriteSetCount++;
                primaryHashesValid = false;
            }
        }

        var orphanCount = provenance.Count(item =>
                !receiptById.TryGetValue(
                    item.ReceiptId,
                    out var parent) ||
                !string.Equals(
                    parent.ProvenanceId,
                    item.Id,
                    StringComparison.Ordinal)) +
            events.Count(item =>
                !receiptById.ContainsKey(item.ReceiptId) ||
                !provenanceById.ContainsKey(item.ProvenanceId) ||
                !P711EventOwnsProductionIdentity(item, receiptById)) +
            outbox.Count(item =>
                !receiptById.ContainsKey(item.ReceiptId) ||
                !provenanceById.ContainsKey(item.ProvenanceId) ||
                !eventById.TryGetValue(item.EventId, out var itemEvent) ||
                !P711OutboxOwnsProductionIdentity(
                    item,
                    itemEvent,
                    receiptById)) +
            mappingAudits.Count(item =>
                string.IsNullOrWhiteSpace(
                    item.LifecycleEventKey) ||
                !primaryEventByKey.ContainsKey(
                    item.LifecycleEventKey));
        var eventHashesValid = events.All(item =>
            receiptById.TryGetValue(item.ReceiptId, out var receipt) &&
            provenanceById.TryGetValue(
                item.ProvenanceId,
                out var itemProvenance) &&
            P711EventHashesValid(
                item,
                receipt,
                itemProvenance));
        var outboxHashesValid = outbox.All(item =>
            receiptById.TryGetValue(item.ReceiptId, out var receipt) &&
            provenanceById.TryGetValue(
                item.ProvenanceId,
                out var itemProvenance) &&
            eventById.TryGetValue(item.EventId, out var itemEvent) &&
            P711OutboxHashesValid(
                item,
                itemEvent,
                receipt,
                itemProvenance));
        var lineageValid = P711LineageValid(
            receipts,
            provenance);
        if (!lineageValid)
            partialWriteSetCount++;

        var currentSurfaceHashesValid = true;
        var currentSurfaceTargetCount = 0;
        var headerSelectedReceiptCount = 0;
        foreach (var targetReportId in currentSurfaceTargetIds)
        {
            var reports = surfaceReports
                .Where(item =>
                    string.Equals(
                        item.Id,
                        targetReportId,
                        StringComparison.Ordinal))
                .ToArray();
            if (reports.Length != 1)
            {
                partialWriteSetCount++;
                currentSurfaceHashesValid = false;
                if (reports.Length == 0)
                    orphanCount++;
                continue;
            }

            currentSurfaceTargetCount++;
            var report = reports[0];
            var targetReceipts = receipts
                .Where(item =>
                    string.Equals(
                        item.TargetReportId,
                        targetReportId,
                        StringComparison.Ordinal))
                .ToArray();
            var selectedReceipts = targetReceipts
                .Where(item =>
                    string.Equals(
                        item.Id,
                        report.DynamicFlowMappingReceiptId,
                        StringComparison.Ordinal))
                .ToArray();
            if (selectedReceipts.Length != 1 ||
                !provenanceById.TryGetValue(
                    report.DynamicFlowMappingProvenanceId ??
                    string.Empty,
                    out var selectedProvenance) ||
                !outboxById.TryGetValue(
                    selectedReceipts.FirstOrDefault()
                        ?.OutboxIntentId ?? string.Empty,
                    out var selectedOutbox))
            {
                partialWriteSetCount++;
                currentSurfaceHashesValid = false;
                orphanCount++;
                continue;
            }

            headerSelectedReceiptCount++;
            var selectedReceipt = selectedReceipts[0];
            var payloads = surfacePayloads
                .Where(item =>
                    string.Equals(
                        item.ReportId,
                        report.Id,
                        StringComparison.Ordinal))
                .ToArray();
            var sections = surfaceSections
                .Where(item =>
                    string.Equals(
                        item.WorkAssignmentReportId,
                        report.Id,
                        StringComparison.Ordinal))
                .ToArray();
            var templates = string.IsNullOrWhiteSpace(
                    report.DynamicFormTemplateId)
                ? Array.Empty<DynamicFormTemplate>()
                : allTemplates
                    .Where(item =>
                        string.Equals(
                            item.Id,
                            report.DynamicFormTemplateId,
                            StringComparison.Ordinal) &&
                        item.IsPublished &&
                        !item.IsDeleted)
                    .ToArray();

            IReadOnlySet<string>? expectedSectionIds = null;
            if (string.IsNullOrWhiteSpace(
                    report.DynamicFormTemplateId))
            {
                expectedSectionIds =
                    new HashSet<string>(StringComparer.Ordinal);
            }
            else if (templates.Length == 1)
            {
                try
                {
                    expectedSectionIds =
                        DynamicFormSectionSnapshotBuilder
                            .Build(templates[0])
                            .Sections
                            .Select(item => item.SectionId)
                            .ToHashSet(StringComparer.Ordinal);
                }
                catch (InvalidOperationException)
                {
                    expectedSectionIds = null;
                }
            }

            var currentValid =
                payloads.Length == 1 &&
                expectedSectionIds is not null &&
                P711CurrentSurfaceValid(
                    report,
                    payloads[0],
                    sections,
                    expectedSectionIds,
                    selectedReceipt,
                    selectedProvenance,
                    selectedOutbox);
            var historicalValid = targetReceipts
                .Where(item =>
                    !string.Equals(
                        item.Id,
                        selectedReceipt.Id,
                        StringComparison.Ordinal))
                .All(item =>
                    provenanceById.TryGetValue(
                        item.ProvenanceId,
                        out var historicalProvenance) &&
                    string.Equals(
                        historicalProvenance.State,
                        DynamicFlowMappingProvenanceStates
                            .Superseded,
                        StringComparison.Ordinal) &&
                    report.PayloadRevision >=
                    item.ResultPayloadRevision &&
                    (report.PayloadRevision !=
                     item.ResultPayloadRevision ||
                     string.Equals(
                         report.PayloadHash,
                         item.ResultPayloadHash,
                         StringComparison.Ordinal)));
            if (!currentValid || !historicalValid)
            {
                partialWriteSetCount++;
                currentSurfaceHashesValid = false;
            }
        }

        orphanCount += surfacePayloads.Count(item =>
            !surfaceReports.Any(report =>
                string.Equals(
                    report.Id,
                    item.ReportId,
                    StringComparison.Ordinal)));
        orphanCount += surfaceSections.Count(item =>
            !surfaceReports.Any(report =>
                string.Equals(
                    report.Id,
                    item.WorkAssignmentReportId,
                    StringComparison.Ordinal)));

        var checkpointCount = outbox.Sum(item =>
            item.ProjectorCheckpoints?.Count ?? 0);
        var completedCheckpointCount = outbox.Sum(item =>
            item.ProjectorCheckpoints?.Count(checkpoint =>
                string.Equals(
                    checkpoint.State,
                    DynamicFlowMappingProjectorCheckpointStates
                        .Completed,
                    StringComparison.Ordinal)) ?? 0);
        var applyIntentCount = outbox.Count(item =>
            item.Operation is
                DynamicFlowMappingLifecycleContract
                    .ApplyInitialOperation or
                DynamicFlowMappingLifecycleContract
                    .ApplyRerunOperation);
        var rebuildIntentCount = outbox.Count(item =>
            string.Equals(
                item.Operation,
                DynamicFlowMappingLifecycleContract
                    .RebuildIntentOperation,
                StringComparison.Ordinal));
        var historicalReceiptCount = provenance.Count(item =>
            string.Equals(
                item.State,
                DynamicFlowMappingProvenanceStates.Superseded,
                StringComparison.Ordinal));
        var invalidatedReceiptCount = provenance.Count(item =>
            string.Equals(
                item.State,
                DynamicFlowMappingProvenanceStates.Invalidated,
                StringComparison.Ordinal));
        var hashesValid =
            primaryHashesValid &&
            eventHashesValid &&
            outboxHashesValid &&
            lineageValid &&
            currentSurfaceHashesValid;

        var leasedOutboxCount = outbox.Count(item =>
            !string.IsNullOrWhiteSpace(item.LeaseId) ||
            !string.IsNullOrWhiteSpace(item.LeaseOwner) ||
            item.LeaseAcquiredAtUtc is not null ||
            item.LeaseUntilUtc is not null);
        var convergedIntentCount = outbox.Count(item =>
            string.Equals(
                item.State,
                DynamicFlowMappingOutboxStates.Reconciled,
                StringComparison.Ordinal));
        var deferredCounts =
            await CountP709DeferredExecutionRowsAsync(
                database,
                ct);
        var p8WriteCount = checked((int)(
            deferredCounts.FieldValues +
            deferredCounts.FieldAggregates +
            deferredCounts.TableValues +
            deferredCounts.TableAggregates +
            deferredCounts.LabelValues +
            deferredCounts.LabelAggregates +
            deferredCounts.StatisticRebuildJobs));
        var p9WriteCount = checked((int)(
            deferredCounts.BasicSummarySnapshots +
            deferredCounts.AdvancedSummaryDayNodes +
            deferredCounts.AdvancedSummaryMonthNodes +
            deferredCounts.AdvancedSummaryYearNodes +
            deferredCounts.SummaryTokenLedgers));

        if (!knownTamperScopeValid ||
            outbox.Count == 0 ||
            mappingAudits.Count != receipts.Count ||
            currentSurfaceTargetCount !=
            currentSurfaceTargetIds.Count ||
            headerSelectedReceiptCount !=
            currentSurfaceTargetIds.Count ||
            duplicateCount != 0 ||
            orphanCount != 0 ||
            partialWriteSetCount != 0 ||
            convergedIntentCount != outbox.Count ||
            leasedOutboxCount != 0 ||
            !hashesValid ||
            p8WriteCount != 0 ||
            p9WriteCount != 0)
        {
            throw new InvalidOperationException(
                "P7-11 global direct-Mongo convergence failed: " +
                $"excludedKnownTamper={excludedKnownTamperFixtureCount}; " +
                $"excludedSurfaceTargets={excludedCurrentSurfaceTargetCount}; " +
                $"tamperScopeValid={knownTamperScopeValid}; " +
                $"receipts={receipts.Count}; provenance={provenance.Count}; " +
                $"events={events.Count}; outbox={outbox.Count}; " +
                $"audits={mappingAudits.Count}; reports={surfaceReports.Count}; " +
                $"payloads={surfacePayloads.Count}; sections={surfaceSections.Count}; " +
                $"surfaceTargets={currentSurfaceTargetCount}; " +
                $"headerSelected={headerSelectedReceiptCount}; " +
                $"historical={historicalReceiptCount}; " +
                $"invalidated={invalidatedReceiptCount}; " +
                $"applyIntents={applyIntentCount}; rebuildIntents={rebuildIntentCount}; " +
                $"checkpoints={checkpointCount}; completedCheckpoints={completedCheckpointCount}; " +
                $"converged={convergedIntentCount}; leased={leasedOutboxCount}; " +
                $"duplicates={duplicateCount}; orphans={orphanCount}; " +
                $"partial={partialWriteSetCount}; hashesValid={hashesValid}; " +
                $"p8={p8WriteCount}; p9={p9WriteCount}.");
        }

        return new P711MongoGlobalSummary(
            "P7-11-MONGO-GLOBAL-SUMMARY",
            excludedKnownTamperFixtureCount,
            excludedCurrentSurfaceTargetCount,
            receipts.Count,
            provenance.Count,
            events.Count,
            outbox.Count,
            mappingAudits.Count,
            currentSurfaceTargetCount,
            surfaceReports.Count,
            surfacePayloads.Count,
            surfaceSections.Count,
            headerSelectedReceiptCount,
            historicalReceiptCount,
            invalidatedReceiptCount,
            applyIntentCount,
            rebuildIntentCount,
            checkpointCount,
            completedCheckpointCount,
            convergedIntentCount,
            leasedOutboxCount,
            duplicateCount,
            orphanCount,
            partialWriteSetCount,
            hashesValid,
            p8WriteCount,
            p9WriteCount);
    }

    private static bool P711PrimaryWriteSetHashesValid(
        DynamicFlowMappingApplyReceipt receipt,
        DynamicFlowMappingProvenanceRecord provenance,
        DynamicFlowMappingEvent mappingEvent,
        DynamicFlowMappingOutboxItem outbox)
    {
        var expectedWriteSetHash =
            DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(
                new
                {
                    receiptId = receipt.Id,
                    provenanceId = provenance.Id,
                    eventId = mappingEvent.Id,
                    eventKey = mappingEvent.EventKey,
                    eventPayloadHash = mappingEvent.PayloadHash,
                    outboxId = outbox.Id,
                    outboxDedupeKey = outbox.DedupeKey,
                    intentHash = outbox.IntentHash,
                    targetReportId = receipt.TargetReportId,
                    resultPayloadRevision =
                        receipt.ResultPayloadRevision,
                    resultPayloadHash =
                        receipt.ResultPayloadHash,
                    resultLifecycleRevision =
                        receipt.ResultLifecycleRevision,
                    provenanceHash = provenance.ProvenanceHash
                });
        var expectedOperation = mappingEvent.EventType switch
        {
            DynamicFlowMappingEventTypes.ApplyCommitted =>
                DynamicFlowMappingLifecycleContract
                    .ApplyInitialOperation,
            DynamicFlowMappingEventTypes.RerunCommitted =>
                DynamicFlowMappingLifecycleContract
                    .ApplyRerunOperation,
            _ => null
        };

        return
            expectedOperation is not null &&
            P711ReceiptHashesHaveValidShape(receipt) &&
            P711ProvenanceHashesHaveValidShape(provenance) &&
            string.Equals(
                receipt.State,
                DynamicFlowMappingApplyStates.Reconciled,
                StringComparison.Ordinal) &&
            receipt.ReconciledAtUtc is not null &&
            string.Equals(
                receipt.Id,
                provenance.ReceiptId,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ProvenanceId,
                provenance.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ProvenanceHash,
                provenance.ProvenanceHash,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.EventId,
                mappingEvent.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.OutboxIntentId,
                outbox.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                expectedOperation,
                outbox.Operation,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.WorkId,
                provenance.WorkId,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.TargetAssignmentId,
                provenance.TargetAssignmentId,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.TargetReportId,
                provenance.TargetReportId,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.CommandId,
                provenance.CommandId,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.CommandId,
                mappingEvent.CommandId,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.CommandId,
                mappingEvent.CorrelationId,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.CommandId,
                outbox.CommandId,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ActorUserId,
                mappingEvent.ActorUserId,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.RequestHash,
                P711BsonString(
                    provenance.ProvenanceSnapshot,
                    "requestHash"),
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.AuthorizationSnapshotHash,
                P711BsonString(
                    provenance.ProvenanceSnapshot,
                    "authorizationSnapshotHash"),
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ResultSnapshotHash,
                DynamicFlowMappingLifecycleContract
                    .ComputeDocumentHash(receipt.ResultSnapshot),
                StringComparison.Ordinal) &&
            string.Equals(
                provenance.ResultSnapshotHash,
                DynamicFlowMappingLifecycleContract
                    .ComputeDocumentHash(provenance.ResultSnapshot),
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ResultSnapshotHash,
                provenance.ResultSnapshotHash,
                StringComparison.Ordinal) &&
            string.Equals(
                provenance.ProvenanceHash,
                DynamicFlowMappingLifecycleContract
                    .ComputeDocumentHash(
                        provenance.ProvenanceSnapshot),
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.WriteSetHash,
                expectedWriteSetHash,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.SourceSignature,
                provenance.SourceSignature,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ResultSemanticHash,
                provenance.ResultSemanticHash,
                StringComparison.Ordinal) &&
            receipt.ResultPayloadRevision ==
                provenance.TargetPayloadRevision &&
            string.Equals(
                receipt.ResultPayloadHash,
                provenance.TargetPayloadHash,
                StringComparison.Ordinal) &&
            receipt.ResultLifecycleRevision ==
                provenance.TargetLifecycleRevision &&
            string.Equals(
                DynamicFlowMappingLifecycleContract
                    .ComputeCanonicalHash(receipt.RuntimePin),
                DynamicFlowMappingLifecycleContract
                    .ComputeCanonicalHash(provenance.RuntimePin),
                StringComparison.Ordinal) &&
            string.Equals(
                DynamicFlowMappingLifecycleContract
                    .ComputeCanonicalHash(receipt.SourcePins),
                DynamicFlowMappingLifecycleContract
                    .ComputeCanonicalHash(provenance.SourcePins),
                StringComparison.Ordinal) &&
            P711ResultAndProvenanceSnapshotsValid(
                receipt,
                provenance);
    }

    private static bool P711EventHashesValid(
        DynamicFlowMappingEvent mappingEvent,
        DynamicFlowMappingApplyReceipt receipt,
        DynamicFlowMappingProvenanceRecord provenance)
    {
        var isInvalidation = string.Equals(
            mappingEvent.EventType,
            DynamicFlowMappingEventTypes.ProvenanceInvalidated,
            StringComparison.Ordinal);
        string? expectedEventKey;
        if (isInvalidation)
        {
            var invalidationReason =
                P711BsonString(mappingEvent.Payload, "reason");
            var invalidatedByEventId =
                P711BsonString(
                    mappingEvent.Payload,
                    "invalidatedByEventId");
            if (string.IsNullOrWhiteSpace(invalidationReason) ||
                string.IsNullOrWhiteSpace(invalidatedByEventId) ||
                !string.Equals(
                    invalidatedByEventId,
                    mappingEvent.CommandId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    provenance.InvalidatedByEventId,
                    mappingEvent.CommandId,
                    StringComparison.Ordinal) ||
                (string.Equals(
                     provenance.State,
                     DynamicFlowMappingProvenanceStates.Invalidated,
                     StringComparison.Ordinal) &&
                 !string.Equals(
                     provenance.InvalidationReason,
                     invalidationReason,
                     StringComparison.Ordinal)))
            {
                return false;
            }

            expectedEventKey =
                DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(
                    new
                    {
                        schemaVersion =
                            "P7-MAP-INVALIDATION-1",
                        provenanceId = provenance.Id,
                        receiptId = provenance.ReceiptId,
                        targetReportId =
                            provenance.TargetReportId,
                        invalidatedByEventId,
                        reason = invalidationReason
                    });
            if (!string.Equals(
                    mappingEvent.Id,
                    DynamicFlowMappingLifecycleContract
                        .ComputeStableObjectId(
                            $"event\n{expectedEventKey}"),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        else
        {
            if (mappingEvent.EventType is not
                DynamicFlowMappingEventTypes.ApplyCommitted and not
                DynamicFlowMappingEventTypes.RerunCommitted)
            {
                return false;
            }
            expectedEventKey =
                DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(
                    new
                    {
                        eventType = mappingEvent.EventType,
                        targetReportId =
                            mappingEvent.TargetReportId,
                        commandId = mappingEvent.CommandId,
                        receiptId = mappingEvent.ReceiptId,
                        resultPayloadRevision =
                            mappingEvent.TargetPayloadRevision,
                        payloadHash =
                            mappingEvent.TargetPayloadHash,
                        provenanceHash =
                            mappingEvent.ProvenanceHash
                    });
        }

        return
            P711EventHashesHaveValidShape(mappingEvent) &&
            string.Equals(
                mappingEvent.EventKey,
                expectedEventKey,
                StringComparison.Ordinal) &&
            string.Equals(
                mappingEvent.PayloadHash,
                DynamicFlowMappingLifecycleContract
                    .ComputeDocumentHash(mappingEvent.Payload),
                StringComparison.Ordinal) &&
            string.Equals(
                mappingEvent.ReceiptId,
                receipt.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                mappingEvent.ProvenanceId,
                provenance.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                mappingEvent.TargetReportId,
                receipt.TargetReportId,
                StringComparison.Ordinal) &&
            string.Equals(
                mappingEvent.TargetAssignmentId,
                receipt.TargetAssignmentId,
                StringComparison.Ordinal) &&
            mappingEvent.TargetPayloadRevision ==
                provenance.TargetPayloadRevision &&
            string.Equals(
                mappingEvent.TargetPayloadHash,
                provenance.TargetPayloadHash,
                StringComparison.Ordinal) &&
            mappingEvent.TargetLifecycleRevision ==
                provenance.TargetLifecycleRevision &&
            string.Equals(
                mappingEvent.SourceSignature,
                provenance.SourceSignature,
                StringComparison.Ordinal) &&
            string.Equals(
                mappingEvent.ResultSemanticHash,
                provenance.ResultSemanticHash,
                StringComparison.Ordinal) &&
            string.Equals(
                mappingEvent.ProvenanceHash,
                provenance.ProvenanceHash,
                StringComparison.Ordinal) &&
            string.Equals(
                DynamicFlowMappingLifecycleContract
                    .ComputeCanonicalHash(mappingEvent.RuntimePin),
                DynamicFlowMappingLifecycleContract
                    .ComputeCanonicalHash(provenance.RuntimePin),
                StringComparison.Ordinal);
    }

    private static bool P711OutboxHashesValid(
        DynamicFlowMappingOutboxItem outbox,
        DynamicFlowMappingEvent mappingEvent,
        DynamicFlowMappingApplyReceipt receipt,
        DynamicFlowMappingProvenanceRecord provenance)
    {
        var intent = outbox.Intent;
        var isInvalidation = string.Equals(
            mappingEvent.EventType,
            DynamicFlowMappingEventTypes.ProvenanceInvalidated,
            StringComparison.Ordinal);
        var expectedIntentHash =
            DynamicFlowMappingLifecycleContract.ComputeDocumentHash(
                intent.ToBsonDocument());
        var expectedDedupeKey = isInvalidation
            ? DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(
                new
                {
                    operation = outbox.Operation,
                    eventKey = mappingEvent.EventKey
                })
            : DynamicFlowMappingLifecycleContract.ComputeCanonicalHash(
                new
                {
                    operation = outbox.Operation,
                    eventKey = mappingEvent.EventKey,
                    intentHash = outbox.IntentHash
                });

        var invalidationReason =
            P711BsonString(mappingEvent.Payload, "reason");
        var expectedOperation = mappingEvent.EventType switch
        {
            DynamicFlowMappingEventTypes.ApplyCommitted =>
                DynamicFlowMappingLifecycleContract
                    .ApplyInitialOperation,
            DynamicFlowMappingEventTypes.RerunCommitted =>
                DynamicFlowMappingLifecycleContract
                    .ApplyRerunOperation,
            DynamicFlowMappingEventTypes.ProvenanceInvalidated =>
                DynamicFlowMappingLifecycleContract
                    .RebuildIntentOperation,
            _ => null
        };
        var invalidationShapeValid = isInvalidation
            ? string.Equals(
                  outbox.Operation,
                  DynamicFlowMappingLifecycleContract
                      .RebuildIntentOperation,
                  StringComparison.Ordinal) &&
              string.Equals(
                  intent.SchemaVersion,
                  "P7-MAP-INVALIDATE-1",
                  StringComparison.Ordinal) &&
              intent.RebuildOnly &&
              string.Equals(
                  intent.InvalidatedByEventId,
                  mappingEvent.CommandId,
                  StringComparison.Ordinal) &&
              string.Equals(
                  intent.InvalidationReason,
                  invalidationReason,
                  StringComparison.Ordinal) &&
              intent.InvalidatedProvenanceIds is
                  [var invalidatedProvenanceId] &&
              string.Equals(
                  invalidatedProvenanceId,
                  provenance.Id,
                  StringComparison.Ordinal) &&
              string.Equals(
                  outbox.Id,
                  DynamicFlowMappingLifecycleContract
                      .ComputeStableObjectId(
                          $"outbox\n{mappingEvent.EventKey}"),
                  StringComparison.Ordinal)
            : expectedOperation is not null &&
              string.Equals(
                  outbox.Operation,
                  expectedOperation,
                  StringComparison.Ordinal) &&
              !intent.RebuildOnly &&
              string.Equals(
                  intent.SchemaVersion,
                  DynamicFlowMappingPersistenceSchema
                      .ReconcileIntentVersion,
                  StringComparison.Ordinal) &&
              intent.InvalidatedByEventId is null &&
              intent.InvalidationReason is null;

        return
            invalidationShapeValid &&
            P711OutboxHashesHaveValidShape(outbox) &&
            string.Equals(
                outbox.State,
                DynamicFlowMappingOutboxStates.Reconciled,
                StringComparison.Ordinal) &&
            outbox.ReconciledAtUtc is not null &&
            string.Equals(
                outbox.IntentHash,
                expectedIntentHash,
                StringComparison.Ordinal) &&
            string.Equals(
                outbox.DedupeKey,
                expectedDedupeKey,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(mappingEvent.Payload, "outboxId"),
                outbox.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(mappingEvent.Payload, "intentHash"),
                outbox.IntentHash,
                StringComparison.Ordinal) &&
            string.Equals(
                outbox.ReceiptId,
                receipt.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                outbox.EventId,
                mappingEvent.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                outbox.ProvenanceId,
                provenance.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                outbox.TargetReportId,
                receipt.TargetReportId,
                StringComparison.Ordinal) &&
            string.Equals(
                outbox.CommandId,
                mappingEvent.CommandId,
                StringComparison.Ordinal) &&
            string.Equals(
                intent.ReceiptId,
                receipt.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                intent.EventId,
                mappingEvent.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                intent.ProvenanceId,
                provenance.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                intent.TargetReportId,
                receipt.TargetReportId,
                StringComparison.Ordinal) &&
            string.Equals(
                intent.TargetAssignmentId,
                receipt.TargetAssignmentId,
                StringComparison.Ordinal) &&
            string.Equals(
                intent.CommandId,
                mappingEvent.CommandId,
                StringComparison.Ordinal) &&
            intent.TargetPayloadRevision ==
                provenance.TargetPayloadRevision &&
            string.Equals(
                intent.TargetPayloadHash,
                provenance.TargetPayloadHash,
                StringComparison.Ordinal) &&
            intent.TargetLifecycleRevision ==
                provenance.TargetLifecycleRevision &&
            string.Equals(
                intent.SourceSignature,
                provenance.SourceSignature,
                StringComparison.Ordinal) &&
            string.Equals(
                intent.ResultSemanticHash,
                provenance.ResultSemanticHash,
                StringComparison.Ordinal) &&
            string.Equals(
                intent.MappingRuleSetHash,
                provenance.MappingRuleSetHash,
                StringComparison.Ordinal) &&
            string.Equals(
                intent.ProvenanceHash,
                provenance.ProvenanceHash,
                StringComparison.Ordinal) &&
            string.Equals(
                intent.ProjectionSnapshotHash,
                DynamicFlowMappingLifecycleContract
                    .ComputeDocumentHash(
                        intent.ProjectionSnapshot),
                StringComparison.Ordinal) &&
            string.Equals(
                intent.AuditSnapshotHash,
                DynamicFlowMappingLifecycleContract
                    .ComputeDocumentHash(intent.AuditSnapshot),
                StringComparison.Ordinal) &&
            string.Equals(
                DynamicFlowMappingLifecycleContract
                    .ComputeCanonicalHash(intent.RuntimePin),
                DynamicFlowMappingLifecycleContract
                    .ComputeCanonicalHash(provenance.RuntimePin),
                StringComparison.Ordinal) &&
            !intent.P8ExecutionEnabled &&
            !intent.P9ExecutionEnabled &&
            P711EventPayloadLinksValid(
                mappingEvent,
                outbox,
                receipt,
                provenance) &&
            P711IntentSnapshotLinksValid(
                outbox,
                mappingEvent,
                receipt,
                provenance) &&
            P711ProjectorPlanValid(outbox, mappingEvent);
    }

    private static bool P711CurrentSurfaceValid(
        WorkAssignmentReport report,
        WorkReportPayload payload,
        IReadOnlyCollection<WorkAssignmentReportSection> sections,
        IReadOnlySet<string> expectedSectionIds,
        DynamicFlowMappingApplyReceipt receipt,
        DynamicFlowMappingProvenanceRecord provenance,
        DynamicFlowMappingOutboxItem outbox)
    {
        var sectionsById = sections
            .GroupBy(
                item => item.SectionId,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        var exactSectionSet =
            sections.Count == expectedSectionIds.Count &&
            sectionsById.Count == expectedSectionIds.Count &&
            expectedSectionIds.All(sectionId =>
                sectionsById.TryGetValue(
                    sectionId,
                    out var matches) &&
                matches.Length == 1);

        return
            provenance.State is
                DynamicFlowMappingProvenanceStates.Current or
                DynamicFlowMappingProvenanceStates.Invalidated &&
            !string.Equals(
                provenance.State,
                DynamicFlowMappingProvenanceStates.Superseded,
                StringComparison.Ordinal) &&
            string.Equals(
                report.Id,
                receipt.TargetReportId,
                StringComparison.Ordinal) &&
            string.Equals(
                report.WorkId,
                receipt.WorkId,
                StringComparison.Ordinal) &&
            string.Equals(
                report.WorkAssignmentId,
                receipt.TargetAssignmentId,
                StringComparison.Ordinal) &&
            string.Equals(
                report.DynamicFlowMappingReceiptId,
                receipt.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                report.DynamicFlowMappingProvenanceId,
                provenance.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                report.DynamicFlowMappingProvenanceHash,
                provenance.ProvenanceHash,
                StringComparison.Ordinal) &&
            report.DynamicFlowMappingResultPayloadRevision ==
                receipt.ResultPayloadRevision &&
            string.Equals(
                report.DynamicFlowMappingResultPayloadHash,
                receipt.ResultPayloadHash,
                StringComparison.Ordinal) &&
            receipt.ResultPayloadRevision ==
                provenance.TargetPayloadRevision &&
            receipt.ResultPayloadRevision ==
                outbox.Intent.TargetPayloadRevision &&
            string.Equals(
                receipt.ResultPayloadHash,
                provenance.TargetPayloadHash,
                StringComparison.Ordinal) &&
            string.Equals(
                receipt.ResultPayloadHash,
                outbox.Intent.TargetPayloadHash,
                StringComparison.Ordinal) &&
            report.PayloadRevision >=
                receipt.ResultPayloadRevision &&
            (report.PayloadRevision !=
             receipt.ResultPayloadRevision ||
             string.Equals(
                 report.PayloadHash,
                 receipt.ResultPayloadHash,
                 StringComparison.Ordinal)) &&
            string.Equals(
                payload.ReportId,
                report.Id,
                StringComparison.Ordinal) &&
            payload.PayloadRevision ==
                report.PayloadRevision &&
            string.Equals(
                payload.PayloadHash,
                report.PayloadHash,
                StringComparison.Ordinal) &&
            string.Equals(
                payload.Status,
                WorkReportPayloadStatus.Ready,
                StringComparison.Ordinal) &&
            !payload.IsDeleted &&
            exactSectionSet &&
            sections.All(section =>
                string.Equals(
                    section.WorkAssignmentReportId,
                    report.Id,
                    StringComparison.Ordinal) &&
                string.Equals(
                    section.WorkId,
                    report.WorkId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    section.WorkAssignmentId,
                    report.WorkAssignmentId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    section.WorkReportPeriodId,
                    report.WorkReportPeriodId,
                    StringComparison.Ordinal) &&
                section.SourcePayloadRevision ==
                    report.PayloadRevision &&
                string.Equals(
                    section.SourcePayloadHash,
                    report.PayloadHash,
                    StringComparison.Ordinal) &&
                section.SourceLifecycleRevision ==
                    report.LifecycleRevision &&
                string.Equals(
                    section.DynamicFlowMappingReceiptId,
                    receipt.Id,
                    StringComparison.Ordinal) &&
                string.Equals(
                    section.DynamicFlowMappingProvenanceId,
                    provenance.Id,
                    StringComparison.Ordinal) &&
                string.Equals(
                    section.DynamicFlowMappingProvenanceHash,
                    provenance.ProvenanceHash,
                    StringComparison.Ordinal) &&
                section.DynamicFlowMappingResultPayloadRevision ==
                    receipt.ResultPayloadRevision &&
                string.Equals(
                    section.DynamicFlowMappingResultPayloadHash,
                    receipt.ResultPayloadHash,
                    StringComparison.Ordinal) &&
                !section.IsDeleted &&
                IsP711LowerSha(section.PayloadHash));
    }

    private static bool P711MappingAuditValid(
        WorkAssignmentReportLog audit,
        DynamicFlowMappingApplyReceipt receipt,
        DynamicFlowMappingProvenanceRecord provenance,
        DynamicFlowMappingEvent mappingEvent,
        DynamicFlowMappingOutboxItem outbox)
    {
        if (!P711TryParseDocument(
                audit.SnapshotJson,
                out var snapshot))
        {
            return false;
        }

        var expectedAction = mappingEvent.EventType switch
        {
            DynamicFlowMappingEventTypes.ApplyCommitted =>
                P711ApplyAuditAction,
            DynamicFlowMappingEventTypes.RerunCommitted =>
                P711RerunAuditAction,
            _ => null
        };
        var workReportPeriodId =
            P711BsonString(
                outbox.Intent.ProjectionSnapshot,
                "workReportPeriodId");
        return
            expectedAction is not null &&
            !audit.IsDeleted &&
            string.Equals(
                audit.WorkId,
                receipt.WorkId,
                StringComparison.Ordinal) &&
            string.Equals(
                audit.WorkAssignmentId,
                receipt.TargetAssignmentId,
                StringComparison.Ordinal) &&
            string.Equals(
                audit.WorkReportPeriodId,
                workReportPeriodId,
                StringComparison.Ordinal) &&
            string.Equals(
                audit.WorkAssignmentReportId,
                receipt.TargetReportId,
                StringComparison.Ordinal) &&
            string.Equals(
                audit.Action,
                expectedAction,
                StringComparison.Ordinal) &&
            string.Equals(
                audit.ToStatus,
                "Draft",
                StringComparison.Ordinal) &&
            string.Equals(
                audit.ActionByUserId,
                receipt.ActorUserId,
                StringComparison.Ordinal) &&
            string.Equals(
                audit.Reason,
                mappingEvent.EventType,
                StringComparison.Ordinal) &&
            string.Equals(
                audit.LifecycleEventKey,
                mappingEvent.EventKey,
                StringComparison.Ordinal) &&
            string.Equals(
                DynamicFlowMappingLifecycleContract
                    .ComputeDocumentHash(snapshot),
                outbox.Intent.AuditSnapshotHash,
                StringComparison.Ordinal) &&
            string.Equals(
                DynamicFlowMappingLifecycleContract
                    .ComputeDocumentHash(snapshot),
                DynamicFlowMappingLifecycleContract
                    .ComputeDocumentHash(
                        outbox.Intent.AuditSnapshot),
                StringComparison.Ordinal) &&
            P711AuditSnapshotLinksValid(
                snapshot,
                receipt,
                provenance,
                mappingEvent);
    }

    private static bool P711AuditSnapshotLinksValid(
        BsonDocument snapshot,
        DynamicFlowMappingApplyReceipt receipt,
        DynamicFlowMappingProvenanceRecord provenance,
        DynamicFlowMappingEvent mappingEvent)
    {
        var expectedSourceFactHashes = provenance.SourcePins
            .Select(item => item.SourceFactHash)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        return
            string.Equals(
                P711BsonString(snapshot, "schemaVersion"),
                "P7-MAP-AUDIT-1",
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(snapshot, "receiptId"),
                receipt.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(snapshot, "provenanceId"),
                provenance.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(snapshot, "eventId"),
                mappingEvent.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(snapshot, "eventKey"),
                mappingEvent.EventKey,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(snapshot, "targetReportId"),
                receipt.TargetReportId,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(snapshot, "commandId"),
                receipt.CommandId,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(snapshot, "requestHash"),
                receipt.RequestHash,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(snapshot, "sourceSignature"),
                receipt.SourceSignature,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(snapshot, "resultSemanticHash"),
                receipt.ResultSemanticHash,
                StringComparison.Ordinal) &&
            P711BsonInt32(snapshot, "resultPayloadRevision") ==
                receipt.ResultPayloadRevision &&
            string.Equals(
                P711BsonString(snapshot, "resultPayloadHash"),
                receipt.ResultPayloadHash,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(snapshot, "provenanceHash"),
                provenance.ProvenanceHash,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(
                    snapshot,
                    "authorizationSnapshotHash"),
                receipt.AuthorizationSnapshotHash,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(
                    snapshot,
                    "runtimeRuleSetHash"),
                provenance.MappingRuleSetHash,
                StringComparison.Ordinal) &&
            P711BsonStringArray(snapshot, "sourceFactHashes")
                .SequenceEqual(
                    expectedSourceFactHashes,
                    StringComparer.Ordinal);
    }

    private static bool P711ResultAndProvenanceSnapshotsValid(
        DynamicFlowMappingApplyReceipt receipt,
        DynamicFlowMappingProvenanceRecord provenance)
    {
        var result = receipt.ResultSnapshot;
        var provenanceSnapshot = provenance.ProvenanceSnapshot;
        return
            string.Equals(
                DynamicFlowMappingLifecycleContract
                    .ComputeDocumentHash(result),
                DynamicFlowMappingLifecycleContract
                    .ComputeDocumentHash(
                        provenance.ResultSnapshot),
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(result, "receiptId"),
                receipt.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(result, "provenanceId"),
                provenance.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(result, "targetReportId"),
                receipt.TargetReportId,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(result, "targetAssignmentId"),
                receipt.TargetAssignmentId,
                StringComparison.Ordinal) &&
            P711BsonInt32(result, "resultPayloadRevision") ==
                receipt.ResultPayloadRevision &&
            string.Equals(
                P711BsonString(result, "resultPayloadHash"),
                receipt.ResultPayloadHash,
                StringComparison.Ordinal) &&
            P711BsonInt32(result, "resultLifecycleRevision") ==
                receipt.ResultLifecycleRevision &&
            string.Equals(
                P711BsonString(result, "sourceSignature"),
                receipt.SourceSignature,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(result, "resultSemanticHash"),
                receipt.ResultSemanticHash,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(provenanceSnapshot, "receiptId"),
                receipt.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(
                    provenanceSnapshot,
                    "provenanceId"),
                provenance.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(provenanceSnapshot, "commandId"),
                receipt.CommandId,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(
                    provenanceSnapshot,
                    "targetReportId"),
                receipt.TargetReportId,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(
                    provenanceSnapshot,
                    "targetAssignmentId"),
                receipt.TargetAssignmentId,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(
                    provenanceSnapshot,
                    "resultSnapshotHash"),
                receipt.ResultSnapshotHash,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(
                    result,
                    "supersedesProvenanceId"),
                provenance.SupersedesProvenanceId,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(
                    provenanceSnapshot,
                    "supersedesProvenanceId"),
                provenance.SupersedesProvenanceId,
                StringComparison.Ordinal);
    }

    private static bool P711EventPayloadLinksValid(
        DynamicFlowMappingEvent mappingEvent,
        DynamicFlowMappingOutboxItem outbox,
        DynamicFlowMappingApplyReceipt receipt,
        DynamicFlowMappingProvenanceRecord provenance)
    {
        if (string.Equals(
                mappingEvent.EventType,
                DynamicFlowMappingEventTypes.ProvenanceInvalidated,
                StringComparison.Ordinal))
        {
            return
                string.Equals(
                    P711BsonString(
                        mappingEvent.Payload,
                        "schemaVersion"),
                    "P7-MAP-INVALIDATION-EVENT-1",
                    StringComparison.Ordinal) &&
                string.Equals(
                    P711BsonString(
                        mappingEvent.Payload,
                        "outboxId"),
                    outbox.Id,
                    StringComparison.Ordinal) &&
                string.Equals(
                    P711BsonString(
                        mappingEvent.Payload,
                        "invalidatedByEventId"),
                    outbox.Intent.InvalidatedByEventId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    P711BsonString(
                        mappingEvent.Payload,
                        "reason"),
                    outbox.Intent.InvalidationReason,
                    StringComparison.Ordinal) &&
                string.Equals(
                    P711BsonString(
                        mappingEvent.Payload,
                        "intentHash"),
                    outbox.IntentHash,
                    StringComparison.Ordinal);
        }

        return
            string.Equals(
                P711BsonString(
                    mappingEvent.Payload,
                    "schemaVersion"),
                "P7-MAP-EVENT-1",
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(mappingEvent.Payload, "receiptId"),
                receipt.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(
                    mappingEvent.Payload,
                    "provenanceId"),
                provenance.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(mappingEvent.Payload, "outboxId"),
                outbox.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(
                    mappingEvent.Payload,
                    "supersedesProvenanceId"),
                provenance.SupersedesProvenanceId,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(
                    mappingEvent.Payload,
                    "resultSnapshotHash"),
                receipt.ResultSnapshotHash,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(
                    mappingEvent.Payload,
                    "provenanceHash"),
                provenance.ProvenanceHash,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(
                    mappingEvent.Payload,
                    "projectionSnapshotHash"),
                outbox.Intent.ProjectionSnapshotHash,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(
                    mappingEvent.Payload,
                    "auditSnapshotHash"),
                outbox.Intent.AuditSnapshotHash,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(mappingEvent.Payload, "intentHash"),
                outbox.IntentHash,
                StringComparison.Ordinal);
    }

    private static bool P711IntentSnapshotLinksValid(
        DynamicFlowMappingOutboxItem outbox,
        DynamicFlowMappingEvent mappingEvent,
        DynamicFlowMappingApplyReceipt receipt,
        DynamicFlowMappingProvenanceRecord provenance)
    {
        var intent = outbox.Intent;
        var isInvalidation = string.Equals(
            mappingEvent.EventType,
            DynamicFlowMappingEventTypes.ProvenanceInvalidated,
            StringComparison.Ordinal);
        if (isInvalidation)
        {
            var expectedBusinessKeys = new[]
            {
                $"report:{receipt.TargetReportId}",
                $"source-report:{intent.SourceReportId}",
                $"provenance:{provenance.Id}"
            };
            return
                !string.IsNullOrWhiteSpace(intent.SourceReportId) &&
                intent.ProjectionBusinessKeys.SequenceEqual(
                    expectedBusinessKeys,
                    StringComparer.Ordinal) &&
                string.Equals(
                    P711BsonString(
                        intent.ProjectionSnapshot,
                        "targetReportId"),
                    receipt.TargetReportId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    P711BsonString(
                        intent.ProjectionSnapshot,
                        "invalidatedProvenanceId"),
                    provenance.Id,
                    StringComparison.Ordinal) &&
                string.Equals(
                    P711BsonString(
                        intent.ProjectionSnapshot,
                        "sourceReportId"),
                    intent.SourceReportId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    P711BsonString(
                        intent.ProjectionSnapshot,
                        "reason"),
                    intent.InvalidationReason,
                    StringComparison.Ordinal) &&
                string.Equals(
                    P711BsonString(
                        intent.AuditSnapshot,
                        "receiptId"),
                    receipt.Id,
                    StringComparison.Ordinal) &&
                string.Equals(
                    P711BsonString(
                        intent.AuditSnapshot,
                        "provenanceId"),
                    provenance.Id,
                    StringComparison.Ordinal) &&
                string.Equals(
                    P711BsonString(
                        intent.AuditSnapshot,
                        "provenanceHash"),
                    provenance.ProvenanceHash,
                    StringComparison.Ordinal) &&
                string.Equals(
                    P711BsonString(
                        intent.AuditSnapshot,
                        "sourceReportId"),
                    intent.SourceReportId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    P711BsonString(
                        intent.AuditSnapshot,
                        "reason"),
                    intent.InvalidationReason,
                    StringComparison.Ordinal);
        }

        var periodId = P711BsonString(
            intent.ProjectionSnapshot,
            "workReportPeriodId");
        if (string.IsNullOrWhiteSpace(periodId))
            return false;
        var expectedApplyBusinessKeys = new[]
            {
                $"report:{receipt.TargetReportId}",
                $"assignment:{receipt.TargetAssignmentId}",
                $"period:{periodId}"
            }
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        return
            intent.ProjectionBusinessKeys.SequenceEqual(
                expectedApplyBusinessKeys,
                StringComparer.Ordinal) &&
            string.Equals(
                P711BsonString(
                    intent.ProjectionSnapshot,
                    "targetReportId"),
                receipt.TargetReportId,
                StringComparison.Ordinal) &&
            string.Equals(
                P711BsonString(
                    intent.ProjectionSnapshot,
                    "targetAssignmentId"),
                receipt.TargetAssignmentId,
                StringComparison.Ordinal) &&
            P711BsonInt32(
                intent.ProjectionSnapshot,
                "payloadRevision") ==
                receipt.ResultPayloadRevision &&
            string.Equals(
                P711BsonString(
                    intent.ProjectionSnapshot,
                    "payloadHash"),
                receipt.ResultPayloadHash,
                StringComparison.Ordinal) &&
            P711BsonInt32(
                intent.ProjectionSnapshot,
                "lifecycleRevision") ==
                receipt.ResultLifecycleRevision &&
            P711AuditSnapshotLinksValid(
                intent.AuditSnapshot,
                receipt,
                provenance,
                mappingEvent);
    }

    private static bool P711ProjectorPlanValid(
        DynamicFlowMappingOutboxItem outbox,
        DynamicFlowMappingEvent mappingEvent)
    {
        var checkpoints = outbox.ProjectorCheckpoints ??
                          new List<
                              DynamicFlowMappingProjectorCheckpoint>();
        var isRebuild = string.Equals(
            outbox.Operation,
            DynamicFlowMappingLifecycleContract
                .RebuildIntentOperation,
            StringComparison.Ordinal);
        if (isRebuild)
        {
            return
                string.Equals(
                    mappingEvent.EventType,
                    DynamicFlowMappingEventTypes
                        .ProvenanceInvalidated,
                    StringComparison.Ordinal) &&
                checkpoints.Count == 0 &&
                outbox.AttemptCount == 0 &&
                outbox.RepairEpoch == 0 &&
                outbox.LastAttemptedAtUtc is null &&
                outbox.LastErrorCode is null &&
                outbox.LastErrorSnapshotHash is null &&
                outbox.ReconciledAtUtc == outbox.CreatedAtUtc &&
                outbox.UpdatedAtUtc == outbox.CreatedAtUtc &&
                outbox.NextAttemptAtUtc == outbox.CreatedAtUtc;
        }

        if (outbox.Operation is not
                DynamicFlowMappingLifecycleContract
                    .ApplyInitialOperation and not
                DynamicFlowMappingLifecycleContract
                    .ApplyRerunOperation)
        {
            return false;
        }
        var periodId = P711BsonString(
            outbox.Intent.ProjectionSnapshot,
            "workReportPeriodId");
        if (string.IsNullOrWhiteSpace(periodId))
            return false;

        if (outbox.AttemptCount < 1 ||
            outbox.RepairEpoch < 1 ||
            outbox.LastErrorCode is not null ||
            outbox.LastErrorSnapshotHash is not null)
        {
            return false;
        }

        var expected = DynamicFlowMappingProjectorContract.BuildPlan(
            outbox.Id,
            outbox.IntentHash,
            outbox.Intent.TargetAssignmentId,
            periodId);
        if (checkpoints.Count != expected.Count ||
            checkpoints
                .GroupBy(
                    item => item.Projector,
                    StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
        {
            return false;
        }

        var expectedByProjector = expected.ToDictionary(
            item => item.Projector,
            StringComparer.Ordinal);
        return checkpoints.All(checkpoint =>
            expectedByProjector.TryGetValue(
                checkpoint.Projector,
                out var expectedCheckpoint) &&
            string.Equals(
                checkpoint.BusinessKey,
                expectedCheckpoint.BusinessKey,
                StringComparison.Ordinal) &&
            string.Equals(
                checkpoint.IdempotencyKey,
                expectedCheckpoint.IdempotencyKey,
                StringComparison.Ordinal) &&
            IsP711LowerSha(checkpoint.IdempotencyKey) &&
            checkpoint.AttemptCount >= 1 &&
            string.Equals(
                checkpoint.State,
                DynamicFlowMappingProjectorCheckpointStates
                    .Completed,
                StringComparison.Ordinal) &&
            checkpoint.ActiveRepairEpoch is null &&
            checkpoint.StartedAtUtc is not null &&
            checkpoint.CompletedAtUtc is not null &&
            checkpoint.CompletedRepairEpoch is > 0 &&
            checkpoint.CompletedRepairEpoch <=
                outbox.RepairEpoch &&
            IsP711LowerSha(checkpoint.CompletionHash) &&
            string.Equals(
                checkpoint.CompletionHash,
                DynamicFlowMappingProjectorContract
                    .ComputeCompletionHash(
                        outbox.IntentHash,
                        checkpoint,
                        checkpoint.CompletedRepairEpoch.Value),
                StringComparison.Ordinal));
    }

    private static bool P711LineageValid(
        IReadOnlyCollection<DynamicFlowMappingApplyReceipt> receipts,
        IReadOnlyCollection<DynamicFlowMappingProvenanceRecord> provenance)
    {
        var byId = provenance
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        if (receipts.Count != provenance.Count)
            return false;

        foreach (var item in provenance)
        {
            if (item.State is not
                    DynamicFlowMappingProvenanceStates.Current and not
                    DynamicFlowMappingProvenanceStates.Invalidated and not
                    DynamicFlowMappingProvenanceStates.Superseded)
            {
                return false;
            }

            if (string.Equals(
                    item.State,
                    DynamicFlowMappingProvenanceStates.Superseded,
                    StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(
                        item.SupersededByProvenanceId) ||
                    !string.Equals(
                        item.InvalidationReason,
                        DynamicFlowMappingLifecycleContract
                            .SupersededByRerunReason,
                        StringComparison.Ordinal) ||
                    item.InvalidatedAtUtc is null ||
                    !byId.TryGetValue(
                        item.SupersededByProvenanceId,
                        out var successor) ||
                    !string.Equals(
                        successor.TargetReportId,
                        item.TargetReportId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        successor.SupersedesProvenanceId,
                        item.Id,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else if (item.SupersededByProvenanceId is not null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(
                    item.SupersedesProvenanceId))
            {
                if (!byId.TryGetValue(
                        item.SupersedesProvenanceId,
                        out var predecessor) ||
                    !string.Equals(
                        predecessor.TargetReportId,
                        item.TargetReportId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        predecessor.State,
                        DynamicFlowMappingProvenanceStates
                            .Superseded,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        predecessor.SupersededByProvenanceId,
                        item.Id,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return provenance
            .GroupBy(
                item => item.TargetReportId,
                StringComparer.Ordinal)
            .All(group =>
                group.Count(item =>
                    !string.Equals(
                        item.State,
                        DynamicFlowMappingProvenanceStates
                            .Superseded,
                        StringComparison.Ordinal)) == 1);
    }

    private static bool P711EventOwnsProductionIdentity(
        DynamicFlowMappingEvent mappingEvent,
        IReadOnlyDictionary<
            string,
            DynamicFlowMappingApplyReceipt> receiptById)
    {
        if (!receiptById.TryGetValue(
                mappingEvent.ReceiptId,
                out var receipt))
        {
            return false;
        }
        return mappingEvent.EventType switch
        {
            DynamicFlowMappingEventTypes.ApplyCommitted or
                DynamicFlowMappingEventTypes.RerunCommitted =>
                string.Equals(
                    receipt.EventId,
                    mappingEvent.Id,
                    StringComparison.Ordinal),
            DynamicFlowMappingEventTypes.ProvenanceInvalidated =>
                string.Equals(
                    mappingEvent.CommandId,
                    P711BsonString(
                        mappingEvent.Payload,
                        "invalidatedByEventId"),
                    StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool P711OutboxOwnsProductionIdentity(
        DynamicFlowMappingOutboxItem outbox,
        DynamicFlowMappingEvent mappingEvent,
        IReadOnlyDictionary<
            string,
            DynamicFlowMappingApplyReceipt> receiptById)
    {
        if (!receiptById.TryGetValue(
                outbox.ReceiptId,
                out var receipt))
        {
            return false;
        }
        return outbox.Operation switch
        {
            DynamicFlowMappingLifecycleContract.ApplyInitialOperation or
                DynamicFlowMappingLifecycleContract.ApplyRerunOperation =>
                string.Equals(
                    receipt.OutboxIntentId,
                    outbox.Id,
                    StringComparison.Ordinal) &&
                mappingEvent.EventType is
                    DynamicFlowMappingEventTypes.ApplyCommitted or
                    DynamicFlowMappingEventTypes.RerunCommitted,
            DynamicFlowMappingLifecycleContract.RebuildIntentOperation =>
                string.Equals(
                    mappingEvent.EventType,
                    DynamicFlowMappingEventTypes
                        .ProvenanceInvalidated,
                    StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool P711ReceiptHashesHaveValidShape(
        DynamicFlowMappingApplyReceipt receipt)
        => IsP711LowerSha(receipt.RequestHash) &&
           IsP711LowerSha(receipt.PreviewTokenHash) &&
           IsP711LowerSha(receipt.SourceSignature) &&
           IsP711LowerSha(receipt.ResultSemanticHash) &&
           IsP711LowerSha(receipt.AuthorizationSnapshotHash) &&
           IsP711LowerSha(receipt.ExpectedTargetPayloadHash) &&
           IsP711LowerSha(receipt.ResultSnapshotHash) &&
           IsP711LowerSha(receipt.WriteSetHash) &&
           IsP711LowerSha(receipt.ResultPayloadHash) &&
           IsP711LowerSha(receipt.ProvenanceHash) &&
           P711RuntimePinHashesHaveValidShape(receipt.RuntimePin) &&
           receipt.SourcePins.All(
               P711SourcePinHashesValid);

    private static bool P711ProvenanceHashesHaveValidShape(
        DynamicFlowMappingProvenanceRecord provenance)
        => IsP711LowerSha(provenance.TargetPayloadHash) &&
           IsP711LowerSha(provenance.SourceSignature) &&
           IsP711LowerSha(provenance.ResultSemanticHash) &&
           IsP711LowerSha(provenance.MappingRuleSetHash) &&
           IsP711LowerSha(provenance.ResultSnapshotHash) &&
           IsP711LowerSha(provenance.ProvenanceHash) &&
           P711RuntimePinHashesHaveValidShape(provenance.RuntimePin) &&
           provenance.SourcePins.All(
               P711SourcePinHashesValid);

    private static bool P711EventHashesHaveValidShape(
        DynamicFlowMappingEvent mappingEvent)
        => IsP711LowerSha(mappingEvent.EventKey) &&
           IsP711LowerSha(mappingEvent.TargetPayloadHash) &&
           IsP711LowerSha(mappingEvent.SourceSignature) &&
           IsP711LowerSha(mappingEvent.ResultSemanticHash) &&
           IsP711LowerSha(mappingEvent.ProvenanceHash) &&
           IsP711LowerSha(mappingEvent.PayloadHash) &&
           P711RuntimePinHashesHaveValidShape(mappingEvent.RuntimePin);

    private static bool P711OutboxHashesHaveValidShape(
        DynamicFlowMappingOutboxItem outbox)
        => IsP711LowerSha(outbox.DedupeKey) &&
           IsP711LowerSha(outbox.IntentHash) &&
           IsP711LowerSha(outbox.Intent.TargetPayloadHash) &&
           IsP711LowerSha(outbox.Intent.SourceSignature) &&
           IsP711LowerSha(outbox.Intent.ResultSemanticHash) &&
           IsP711LowerSha(outbox.Intent.MappingRuleSetHash) &&
           IsP711LowerSha(outbox.Intent.ProvenanceHash) &&
           IsP711LowerSha(
               outbox.Intent.ProjectionSnapshotHash) &&
           IsP711LowerSha(outbox.Intent.AuditSnapshotHash) &&
           P711RuntimePinHashesHaveValidShape(
               outbox.Intent.RuntimePin);

    private static bool P711RuntimePinHashesHaveValidShape(
        DynamicFlowMappingRuntimePin pin)
        => IsP711LowerSha(pin.FlowPayloadHash) &&
           IsP711LowerSha(pin.CatalogSemanticHash) &&
           IsP711LowerSha(pin.MappingRuleSetHash) &&
           IsP711LowerSha(pin.FunctionRegistryHash) &&
           IsP711LowerSha(pin.FormSchemaHash) &&
           IsP711LowerSha(pin.FormSnapshotHash);

    private static bool P711SourcePinHashesValid(
        DynamicFlowMappingSourcePin pin)
        => IsP711LowerSha(pin.SourceFormSchemaHash) &&
           IsP711LowerSha(pin.SourcePayloadHash) &&
           IsP711LowerSha(pin.SourceFactHash) &&
           string.Equals(
               pin.SourceFactHash,
               DynamicFlowMappingLifecycleContract
                   .ComputeCanonicalHash(
                       new
                       {
                           sourceReportId =
                               pin.SourceReportId,
                           sourceAssignmentId =
                               pin.SourceAssignmentId,
                           sourceFlowInstanceId =
                               pin.SourceFlowInstanceId,
                           sourceExecutionEpoch =
                               pin.SourceExecutionEpoch,
                           sourceStepInstanceId =
                               pin.SourceStepInstanceId,
                           sourceStepId = pin.SourceStepId,
                           sourceBranchId =
                               pin.SourceBranchId,
                           sourceAttemptNo =
                               pin.SourceAttemptNo,
                           sourceFormFamilyId =
                               pin.SourceFormFamilyId,
                           sourceFormVersionId =
                               pin.SourceFormVersionId,
                           sourceFormVersionNo =
                               pin.SourceFormVersionNo,
                           sourceFormSchemaHash =
                               pin.SourceFormSchemaHash,
                           sourcePayloadRevision =
                               pin.SourcePayloadRevision,
                           sourcePayloadHash =
                               pin.SourcePayloadHash,
                           sourceLifecycleRevision =
                               pin.SourceLifecycleRevision,
                           sourceLifecycleStatus =
                               pin.SourceLifecycleStatus,
                           sourcePeriodInstanceKey =
                               pin.SourcePeriodInstanceKey
                       }),
               StringComparison.Ordinal);

    private static bool P711IsMappingAuditAction(string? action)
        => action is
            P711ApplyAuditAction or
            P711RerunAuditAction;

    private static bool P711TryParseDocument(
        string? json,
        out BsonDocument document)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                document = BsonDocument.Parse(json);
                return true;
            }
            catch (FormatException)
            {
                // Fail closed below.
            }
        }

        document = new BsonDocument();
        return false;
    }

    private static string? P711BsonString(
        BsonDocument document,
        string name)
        => document.TryGetValue(name, out var value) &&
           value.IsString
            ? value.AsString
            : null;

    private static int? P711BsonInt32(
        BsonDocument document,
        string name)
    {
        if (!document.TryGetValue(name, out var value))
            return null;
        return value.BsonType switch
        {
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64
                when value.AsInt64 is >= int.MinValue and <= int.MaxValue =>
                checked((int)value.AsInt64),
            _ => null
        };
    }

    private static IReadOnlyList<string> P711BsonStringArray(
        BsonDocument document,
        string name)
        => document.TryGetValue(name, out var value) &&
           value.IsBsonArray &&
           value.AsBsonArray.All(item => item.IsString)
            ? value.AsBsonArray
                .Select(item => item.AsString)
                .ToArray()
            : Array.Empty<string>();

    private static bool IsP711LowerSha(string? value)
        => DynamicFlowMappingLifecycleContract
            .IsLowerSha256(value);

    private sealed record P711MongoGlobalSummary(
        string CaseId,
        int ExcludedKnownTamperFixtureCount,
        int ExcludedCurrentSurfaceTargetCount,
        int ReceiptCount,
        int ProvenanceCount,
        int EventCount,
        int CommittedIntentCount,
        int MappingAuditCount,
        int CurrentSurfaceTargetCount,
        int ReportCount,
        int PayloadCount,
        int SectionCount,
        int HeaderSelectedReceiptCount,
        int HistoricalReceiptCount,
        int InvalidatedReceiptCount,
        int ApplyIntentCount,
        int RebuildIntentCount,
        int ProjectorCheckpointCount,
        int CompletedProjectorCheckpointCount,
        int ConvergedIntentCount,
        int LeasedOutboxCount,
        int DuplicateCount,
        int OrphanCount,
        int PartialWriteSetCount,
        bool ExactHashes,
        int P8WriteCount,
        int P9WriteCount);
}

