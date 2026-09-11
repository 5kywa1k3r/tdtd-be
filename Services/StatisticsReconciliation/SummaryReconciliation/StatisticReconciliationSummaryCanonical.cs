using System.Security.Cryptography;
using System.Text;

namespace tdtd_be.Services.StatisticsReconciliation.SummaryReconciliation;

internal static class StatisticReconciliationSummaryCanonical
{
    internal static string Hash(string domain, params string?[] values)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(sha, domain);
        foreach (var value in values)
            Add(sha, value ?? "~");
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    internal static string HashSequence(string domain, IEnumerable<string> values)
        => Hash(domain, values.ToArray());

    internal static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() ||
            !value.IsNormalized(NormalizationForm.FormC) || value.Length > 4096 ||
            value.Any(char.IsControl))
            throw Fail($"{name}.required");
        return value;
    }

    internal static string Sha(string? value, string name)
    {
        var required = Required(value, name);
        if (required.Length != 64 || required.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Fail($"{name}.sha256");
        return required;
    }

    internal static string? Optional(string? value, string name)
        => value is null ? null : Required(value, name);

    internal static string I(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static StatisticReconciliationSummaryException Fail(string detail, string? code = null)
        => new(code ?? StatisticReconciliationSummaryFailureCodes.Invalid, detail);

    private static void Add(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(bytes.Length));
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
