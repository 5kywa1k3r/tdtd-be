using System.Globalization;
using System.Numerics;

namespace tdtd_be.Services.AggregateMapping;

// Exact rational arithmetic. Yud approved six fractional digits, midpoint-to-even,
// on 2026-09-28. Rounding happens only when serializing a result, never between nodes.
internal sealed record AggregateNumber
{
    internal BigInteger Numerator { get; }
    internal BigInteger Denominator { get; }
    internal AggregateNumber(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero) throw new AggregatePreviewException("AGG_DIVIDE_BY_ZERO");
        if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
        var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        Numerator = numerator / gcd; Denominator = denominator / gcd;
        if (Numerator.GetBitLength() > 4096 || Denominator.GetBitLength() > 4096)
            throw new AggregatePreviewException("AGG_NUMBER_OVERFLOW");
    }
    internal static AggregateNumber Parse(string text)
    {
        if (text.Length is < 1 or > 128) throw new AggregatePreviewException("AGG_NUMBER_INVALID");
        var negative = text[0] == '-';
        var body = negative ? text[1..] : text;
        var parts = body.Split('.');
        if (parts.Length is < 1 or > 2 || parts.Any(p => p.Length == 0 || p.Any(c => c is < '0' or > '9')))
            throw new AggregatePreviewException("AGG_NUMBER_INVALID");
        var numerator = BigInteger.Parse(string.Join("", parts), CultureInfo.InvariantCulture);
        return new(negative ? -numerator : numerator, BigInteger.Pow(10, parts.Length == 2 ? parts[1].Length : 0));
    }
    internal static AggregateNumber From(long value) => new(value, 1);
    internal static AggregateNumber ParseJson(string text)
    {
        var parts = text.Split(['e', 'E']);
        if (parts.Length == 1) return Parse(text);
        if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exponent)
            || exponent is < -128 or > 128) throw new AggregatePreviewException("AGG_NUMBER_OVERFLOW");
        var coefficient = Parse(parts[0]);
        var factor = new AggregateNumber(BigInteger.Pow(10, Math.Abs(exponent)), 1);
        return exponent >= 0 ? coefficient.Multiply(factor) : coefficient.Divide(factor);
    }
    internal AggregateNumber Add(AggregateNumber other) => new(Numerator * other.Denominator + other.Numerator * Denominator, Denominator * other.Denominator);
    internal AggregateNumber Subtract(AggregateNumber other) => new(Numerator * other.Denominator - other.Numerator * Denominator, Denominator * other.Denominator);
    internal AggregateNumber Multiply(AggregateNumber other) => new(Numerator * other.Numerator, Denominator * other.Denominator);
    internal AggregateNumber Divide(AggregateNumber other) => new(Numerator * other.Denominator, Denominator * other.Numerator);
    internal int Compare(AggregateNumber other) => (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);
    internal string ExactKey => Numerator.ToString(CultureInfo.InvariantCulture) + "/" + Denominator.ToString(CultureInfo.InvariantCulture);
    internal string ToWire()
    {
        var scaled = BigInteger.DivRem(BigInteger.Abs(Numerator) * 1_000_000, Denominator, out var remainder);
        var midpoint = (remainder * 2).CompareTo(Denominator);
        if (midpoint > 0 || (midpoint == 0 && !scaled.IsEven)) scaled++;
        var digits = scaled.ToString(CultureInfo.InvariantCulture).PadLeft(7, '0');
        var result = (digits[..^6] + "." + digits[^6..]).TrimEnd('0').TrimEnd('.');
        return Numerator.Sign < 0 && scaled != 0 ? "-" + result : result;
    }
}
