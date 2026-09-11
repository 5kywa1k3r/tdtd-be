using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualCanonical
{
    internal static string Required(string? value, string name, int maxLength = 1024)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
            throw new StatisticReconciliationActualObservationException($"{name}_REQUIRED");
        if (!StringComparer.Ordinal.Equals(value, normalized))
            throw new StatisticReconciliationActualObservationException($"{name}_NON_CANONICAL_WHITESPACE");
        if (normalized.Length > maxLength)
            throw new StatisticReconciliationActualObservationException($"{name}_TOO_LONG");
        if (normalized.Any(char.IsControl))
            throw new StatisticReconciliationActualObservationException($"{name}_CONTROL_CHARACTER");
        return normalized;
    }

    internal static string? Optional(string? value, string name, int maxLength = 1024)
        => string.IsNullOrWhiteSpace(value) ? null : Required(value, name, maxLength);

    internal static string Sha256(string? value, string name)
    {
        var normalized = Required(value, name, 64).ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(x => !Uri.IsHexDigit(x)))
            throw new StatisticReconciliationActualObservationException($"{name}_INVALID");
        return normalized;
    }

    internal static string Upper(string? value, string name)
        => Required(value, name).ToUpperInvariant();

    internal static DateTime? Utc(DateTime? value, string name)
    {
        if (!value.HasValue)
            return null;
        if (value.Value.Kind != DateTimeKind.Utc)
            throw new StatisticReconciliationActualObservationException($"{name}_NOT_UTC");
        return value.Value;
    }

    internal static string Hash(string domain, params string?[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Required(domain, nameof(domain)));
        foreach (var value in values)
            Append(hash, value ?? string.Empty);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static string HashSequence(string domain, IEnumerable<string> values)
        => Hash(domain, values.ToArray());

    internal static string Number(decimal value)
        => value.ToString("0.#############################", CultureInfo.InvariantCulture);

    internal static string Boolean(bool value) => value ? "true" : "false";

    internal static string Instant(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new StatisticReconciliationActualObservationException("DATE_VALUE_NOT_UTC");
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    internal static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture));
        hash.AppendData(length);
        hash.AppendData(new byte[] { (byte)':'});
        hash.AppendData(bytes);
        hash.AppendData(new byte[] { (byte)'\n'});
    }
}

internal sealed class StatisticReconciliationActualObservationException : InvalidOperationException
{
    internal StatisticReconciliationActualObservationException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    internal string Reason { get; }
}
