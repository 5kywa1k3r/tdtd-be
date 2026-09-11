using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal static class P11DownloadedFileSelectionSelftest
{
    public const string CommandLineSwitch = "--p11-download-selection-selftest";

    public static int Run()
    {
        const string csvId = "csv-export";
        const string xlsxId = "xlsx-export";
        var downloads = new JsonArray
        {
            Download("P9_RESULT_EXPORT", "CSV", csvId),
            Download("P9_RESULT_EXPORT", "XLSX", xlsxId),
            Download("P9_RESULT_EXPORT", "XLSX", "capture-plan-export", "RECONCILIATION-CAPTURE-PLAN"),
            Download("P10_EVIDENCE_EXPORT", "CSV", "evidence-export")
        };

        var selected = P11ResultBrowserFixture.SelectP9ResultDownloads(
            downloads, csvId, xlsxId);
        Require(selected.Csv["exportId"]!.GetValue<string>() == csvId,
            "CSV selection drifted from its exact export identity.");
        Require(selected.Xlsx["exportId"]!.GetValue<string>() == xlsxId,
            "XLSX selection was confused by the capture-plan XLSX.");

        Reject(new JsonArray
        {
            Download("P9_RESULT_EXPORT", "CSV", csvId),
            Download("P9_RESULT_EXPORT", "XLSX", "wrong-xlsx")
        }, csvId, xlsxId, "missing exact XLSX export");
        Reject(new JsonArray
        {
            Download("P9_RESULT_EXPORT", "CSV", csvId),
            Download("P9_RESULT_EXPORT", "XLSX", xlsxId),
            Download("P9_RESULT_EXPORT", "XLSX", xlsxId)
        }, csvId, xlsxId, "ambiguous exact XLSX export");
        Reject(new JsonArray
        {
            Download("P9_RESULT_EXPORT", "XLSX", csvId),
            Download("P9_RESULT_EXPORT", "CSV", xlsxId)
        }, csvId, xlsxId, "format-swapped export identities");
        Reject(downloads, csvId, csvId, "reused CSV/XLSX export identity");

        Console.WriteLine("{\"verdict\":\"PASS\",\"checks\":6,\"noiseXlsxAccepted\":true,\"selectionBoundByExportId\":true}");
        return 0;
    }

    private static JsonObject Download(
        string kind,
        string format,
        string exportId,
        string? label = null)
        => new()
        {
            ["kind"] = kind,
            ["format"] = format,
            ["exportId"] = exportId,
            ["label"] = label
        };

    private static void Reject(
        JsonArray downloads,
        string csvId,
        string xlsxId,
        string purpose)
    {
        try
        {
            P11ResultBrowserFixture.SelectP9ResultDownloads(downloads, csvId, xlsxId);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException($"Self-test failed to reject {purpose}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
