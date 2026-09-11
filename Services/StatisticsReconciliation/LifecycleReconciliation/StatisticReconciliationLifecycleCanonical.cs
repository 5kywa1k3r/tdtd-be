using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;

internal static class StatisticReconciliationLifecycleCanonical
{
    internal static string Hash(params string?[] values)
    {
        using var stream = new MemoryStream();
        foreach (var value in values)
        {
            if (value is null)
            {
                stream.Write(BitConverter.GetBytes(-1));
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(BitConverter.GetBytes(bytes.Length));
            stream.Write(bytes);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
    }

    internal static string HashSequence(IEnumerable<string> values)
        => Hash(values.Order(StringComparer.Ordinal).ToArray());

    internal static string Required(string? value, string path, int max = 512)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > max)
            Fail(StatisticReconciliationLifecycleFailureCodes.EvidenceInvalid,
                $"{path}_REQUIRED");
        return normalized;
    }

    internal static string? Optional(string? value, string path, int max = 512)
    {
        if (value is null)
            return null;
        var normalized = value.Trim();
        if (normalized.Length == 0 || normalized.Length > max)
            Fail(StatisticReconciliationLifecycleFailureCodes.EvidenceInvalid,
                $"{path}_INVALID");
        return normalized;
    }

    internal static string Sha(string? value, string path)
    {
        var normalized = Required(value, path, 64).ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(ch =>
                !char.IsAsciiHexDigit(ch) || char.IsUpper(ch)))
            Fail(StatisticReconciliationLifecycleFailureCodes.EvidenceInvalid,
                $"{path}_SHA256_INVALID");
        return normalized;
    }

    internal static string Decimal(string? value, string path)
    {
        var normalized = Required(value, path, 256);
        if (!decimal.TryParse(normalized, NumberStyles.Number,
                CultureInfo.InvariantCulture, out var number))
            Fail(StatisticReconciliationLifecycleFailureCodes.EvidenceInvalid,
                $"{path}_DECIMAL_INVALID");
        return Format(number);
    }

    internal static decimal ParseDecimal(string value, string path)
    {
        var normalized = Decimal(value, path);
        return decimal.Parse(normalized, NumberStyles.Number,
            CultureInfo.InvariantCulture);
    }

    internal static string Format(decimal value)
        => value == 0m ? "0" : value.ToString("0.#############################",
            CultureInfo.InvariantCulture);

    internal static bool Same(string? left, string? right)
        => StringComparer.Ordinal.Equals(left, right);

    internal static void Fail(string code, string detail)
        => throw new StatisticReconciliationLifecycleException(code, detail);
}
