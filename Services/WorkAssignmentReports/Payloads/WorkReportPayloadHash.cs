using System.Security.Cryptography;
using System.Text;

namespace tdtd_be.Services.WorkAssignmentReports.Payloads;

internal readonly record struct WorkReportPayloadBlockHash(
    string BlockId,
    int BlockOrder,
    string PayloadHash);

internal static class WorkReportPayloadHash
{
    public static string Compute(
        string values1DJson,
        string? fieldValuesJson,
        string? tableRootJson,
        string? summarySourceJson,
        IEnumerable<WorkReportPayloadBlockHash> blocks)
    {
        var builder = new StringBuilder()
            .Append(values1DJson).Append('\n')
            .Append(fieldValuesJson).Append('\n')
            .Append(tableRootJson).Append('\n')
            .Append(summarySourceJson).Append('\n');

        foreach (var block in blocks.OrderBy(x => x.BlockOrder).ThenBy(x => x.BlockId, StringComparer.Ordinal))
            builder.Append(block.BlockId).Append(':').Append(block.PayloadHash).Append('\n');

        return Sha256Hex(builder.ToString());
    }

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
