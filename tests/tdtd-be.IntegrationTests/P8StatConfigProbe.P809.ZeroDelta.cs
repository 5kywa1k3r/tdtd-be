namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP809ZeroDeltaCasesAsync(CancellationToken ct)
    {
        await RunP809ZeroDeltaCaseAsync(
            "P8-BND-009",
            ["P9_PROJECTION"],
            P809SixResultCollections,
            "All six field/table/label value and aggregate collections retained exact document-set hashes.",
            "stores=6;entry=P9_PROJECTION;delta=0",
            ct);
        await RunP809ZeroDeltaCaseAsync(
            "P8-BND-010",
            ["P9_RESULT"],
            P809BasicSnapshotCollections,
            "The Basic summary snapshot collection retained its exact document-set hash.",
            "stores=basicSnapshot;entry=P9_RESULT;delta=0",
            ct);
        await RunP809ZeroDeltaCaseAsync(
            "P8-BND-011",
            ["P9_RESULT"],
            P809AdvancedAndDiffOutputCollections,
            "Advanced DAY/MONTH/YEAR hierarchy nodes plus Diff result/export stores retained exact hashes.",
            "stores=advanced3+diff2;entry=P9_RESULT;delta=0",
            ct);
        await RunP809ZeroDeltaCaseAsync(
            "P8-BND-012",
            ["P9_RUN", "P9_PROJECTION", "P9_RESULT", "P9_EXPORT", "P10_RECONCILE"],
            P809ReportProjectionAggregateExportReconcileCollections
                .Concat(P809JobOutboxAndSideEffectCollections)
                .ToArray(),
            "Every report/projection/aggregate/export/reconcile/job/outbox/side-effect store retained its exact hash across all five barriers.",
            $"stores={P809ReportProjectionAggregateExportReconcileCollections.Length + P809JobOutboxAndSideEffectCollections.Length};entries=5;delta=0",
            ct);
    }

    private async Task RunP809ZeroDeltaCaseAsync(
        string caseId,
        IReadOnlyList<string> entries,
        IReadOnlyCollection<string> category,
        string detail,
        string fingerprint,
        CancellationToken ct)
    {
        var commandIds = entries
            .Select((entry, index) =>
                $"p809-zero-{caseId[^3..]}-{index + 1}-{entry.ToLowerInvariant().Replace('_', '-')}")
            .ToArray();
        await RunEvidenceCaseAsync(
            caseId,
            "system_admin",
            commandIds,
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var before = await CaptureP809ZeroWriteInventoryAsync(ct);
                for (var index = 0; index < entries.Count; index++)
                {
                    await RequireP809BarrierAsync(
                        caseId,
                        entries[index],
                        commandIds[index],
                        ct);
                }
                var after = await CaptureP809ZeroWriteInventoryAsync(ct);
                RequireP809ZeroWrite(caseId, before, after, category);
                return new CaseObservation(detail, fingerprint);
            },
            ct);
    }
}
