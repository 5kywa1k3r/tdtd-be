using System.Globalization;
using System.Text;
using System.Text.Json;

namespace tdtd_be.Services;

/// <summary>Official baseline shipped with the application; reading never seeds the database.</summary>
public static class SystemLocalityCatalog
{
    public sealed record Locality(string Code, string Name, string ProvinceCode, string Kind);
    public sealed record Dataset(string DatasetId, string ProvinceCode, string ProvinceName,
        string EffectiveFrom, string VerifiedOn, string SourceDocument, string SourceUrl,
        string SourceSha256, IReadOnlyList<Locality> Rows);

    private static readonly Lazy<Dataset> Baseline = new(() =>
    {
        using var stream = typeof(SystemLocalityCatalog).Assembly.GetManifestResourceStream(
            "tdtd_be.Data.Catalogs.thanh-hoa-localities-2025.json")
            ?? throw new InvalidOperationException("Missing Thanh Hoa locality baseline.");
        var data = JsonSerializer.Deserialize<Dataset>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Invalid locality baseline.");
        if (data.Rows.Count != 166 || data.Rows.Select(x => x.Code).Distinct().Count() != 166 ||
            data.Rows.Any(x => x.ProvinceCode != "38" || x.Code.Length != 5))
            throw new InvalidOperationException("Invalid Thanh Hoa locality codes.");
        return data with { Rows = Array.AsReadOnly(data.Rows.ToArray()) };
    });

    public static Dataset Official => Baseline.Value;

    public static string SearchKey(string? value)
    {
        var decomposed = (value ?? "").Trim().ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        return new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }
}
