using System.Security.Cryptography;
using System.Text;

namespace tdtd_be.Services.StatisticsReconciliation.IndependentReview;

public static class StatisticReconciliationIndependentReviewCanonical
{
    public static string Hash(params object?[] fields)
    {
        using var stream = new MemoryStream();
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field switch
            {
                null => "~",
                bool value => value ? "1" : "0",
                DateTime value => value.ToUniversalTime().ToString("O"),
                _ => Convert.ToString(field, System.Globalization.CultureInfo.InvariantCulture) ?? "~"
            });
            stream.Write(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(bytes.Length)));
            stream.Write(bytes);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    public static string ReviewRecordHash(StatisticReconciliationEightColumnRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var fields = new List<object?> { "P10_REVIEW_EIGHT_COLUMNS_V1" };
        foreach (var column in record.Columns)
        {
            RequireText(column.Value, column.Key);
            fields.Add(column.Key);
            fields.Add(column.Value);
        }
        if (record.Columns.Count != 8)
            throw Invalid("exactEightColumns");
        return Hash(fields.ToArray());
    }

    public static string CommandHash(StatisticReconciliationReviewCommand command)
        => Hash("P10_REVIEW_COMMAND_V1", command.CommandId, command.ReconciliationId,
            command.GenerationId, command.GenerationSha256, command.SemanticVerdictSha256,
            command.Gate, command.Decision, command.ExpectedStateRevision);

    public static string SupersessionCommandHash(StatisticReconciliationReviewSupersessionCommand command)
        => Hash("P10_REVIEW_SUPERSESSION_COMMAND_V1", command.CommandId,
            command.ReconciliationId, command.PreviousGenerationId, command.NewGenerationId,
            command.NewGenerationSha256, command.ExpectedStateRevision);

    public static void RequireSha(string? value, string field)
    {
        if (value is null || value.Length != 64 || value.Any(ch =>
                !char.IsAsciiHexDigit(ch) || char.IsLetter(ch) && char.IsUpper(ch)))
            throw Invalid(field);
    }

    public static void RequireText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Invalid(field);
    }

    public static StatisticReconciliationIndependentReviewException Invalid(string detail)
        => new(StatisticReconciliationIndependentReviewFailureCodes.EvidenceInvalid, detail);
}
