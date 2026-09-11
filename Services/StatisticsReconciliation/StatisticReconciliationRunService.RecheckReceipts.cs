using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed partial class StatisticReconciliationRunService
{
    private const int MaxRecheckBeginReceipts = 64;

    private static StatisticReconciliationRecheckBeginReceipt
        BuildRecheckBeginReceipt(
            StatisticReconciliationRun run,
            string actorUserId,
            string commandId,
            long expectedStateRevision,
            string expectedStateHash,
            string requestHash,
            string markerId,
            long acceptedStateRevision,
            DateTime acceptedAtUtc)
    {
        var receipt = new StatisticReconciliationRecheckBeginReceipt
        {
            ReceiptId = StatisticReconciliationCanonicalJson.HashObject(new
            {
                schema = "P10_RECHECK_BEGIN_RECEIPT_ID_V1",
                reconciliationId = run.Id,
                actorUserId,
                commandId
            }),
            ActorUserId = actorUserId,
            CommandId = commandId,
            ExpectedStateRevision = expectedStateRevision,
            ExpectedStateHash = expectedStateHash,
            RequestHash = requestHash,
            MarkerId = markerId,
            AcceptedStateRevision = acceptedStateRevision,
            AcceptedAtUtc = acceptedAtUtc
        };
        receipt.ReceiptHash = BuildRecheckBeginReceiptHash(receipt);
        return receipt;
    }

    private static string BuildRecheckBeginReceiptHash(
        StatisticReconciliationRecheckBeginReceipt receipt)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_RECHECK_BEGIN_RECEIPT_V1",
            receipt.ReceiptId,
            receipt.ActorUserId,
            receipt.CommandId,
            receipt.ExpectedStateRevision,
            receipt.ExpectedStateHash,
            receipt.RequestHash,
            receipt.MarkerId,
            receipt.AcceptedStateRevision,
            acceptedAtUtc = FormatUtc(receipt.AcceptedAtUtc)
        });

    private static string BuildRecheckBeginReceiptHistoryHash(
        IEnumerable<StatisticReconciliationRecheckBeginReceipt> receipts)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_RECHECK_BEGIN_HISTORY_V1",
            receipts = receipts.Select(receipt => new
            {
                receipt.ReceiptHash,
                computedReceiptHash = BuildRecheckBeginReceiptHash(receipt)
            }).ToArray()
        });

    private static StatisticReconciliationRecheckBeginReceipt?
        FindRecheckBeginReceipt(
            StatisticReconciliationRun run,
            string actorUserId,
            string commandId)
        => (run.RecheckBeginReceipts ?? []).SingleOrDefault(receipt =>
            receipt.ActorUserId == actorUserId &&
            receipt.CommandId == commandId);

    private static bool HasValidRecheckBeginReceiptHistory(
        StatisticReconciliationRun run)
    {
        var receipts = run.RecheckBeginReceipts ?? [];
        if (receipts.Count == 0)
            return run.RecheckBeginReceiptHistoryHash is null;
        return receipts.Count <= MaxRecheckBeginReceipts &&
            receipts.All(receipt =>
                StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                    receipt.ReceiptId) &&
                !string.IsNullOrWhiteSpace(receipt.ActorUserId) &&
                !string.IsNullOrWhiteSpace(receipt.CommandId) &&
                receipt.ExpectedStateRevision >= 1 &&
                receipt.AcceptedStateRevision ==
                    checked(receipt.ExpectedStateRevision + 1) &&
                receipt.AcceptedAtUtc.Kind == DateTimeKind.Utc &&
                StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                    receipt.ExpectedStateHash) &&
                StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                    receipt.RequestHash) &&
                StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                    receipt.MarkerId) &&
                StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                    receipt.ReceiptHash) &&
                receipt.ReceiptHash == BuildRecheckBeginReceiptHash(receipt)) &&
            receipts.GroupBy(receipt => receipt.ReceiptId,
                    StringComparer.Ordinal)
                .All(group => group.Count() == 1) &&
            receipts.GroupBy(receipt =>
                    $"{receipt.ActorUserId}\n{receipt.CommandId}",
                    StringComparer.Ordinal)
                .All(group => group.Count() == 1) &&
            receipts.Zip(receipts.Skip(1), (left, right) =>
                    left.AcceptedAtUtc <= right.AcceptedAtUtc)
                .All(ordered => ordered) &&
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                run.RecheckBeginReceiptHistoryHash) &&
            run.RecheckBeginReceiptHistoryHash ==
                BuildRecheckBeginReceiptHistoryHash(receipts);
    }

    private static bool HasValidCurrentRecheckCaptureBinding(
        StatisticReconciliationRun run)
    {
        var binding = run.CurrentGenerationRecheckCaptureBinding;
        if (binding is null)
            return true;
        if (run.CurrentGenerationId is null ||
            run.CurrentGenerationHash is null ||
            run.GenerationPublishRevision < 1)
            return false;
        try
        {
            Recheck.StatisticReconciliationRecheckCaptureBindingCanonical
                .RequireValid(binding);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
