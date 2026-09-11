using System.Reflection;
using System.Security.Cryptography;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

internal static class ExportSidecarContractIntegration
{
    internal static async Task RunAsync()
    {
        var positiveRows = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["amount"] = 1.25m,
                ["active"] = true,
                ["occurredAtUtc"] = new DateTime(
                    2026, 8, 11, 1, 0, 0, DateTimeKind.Utc),
                ["note"] = string.Empty,
                ["optional"] = "present",
                ["payload"] = new Dictionary<string, object?> { ["x"] = 1 },
                ["totalRows"] = 2
            },
            new Dictionary<string, object?>
            {
                ["amount"] = 2m,
                ["active"] = false,
                ["occurredAtUtc"] = new DateTime(
                    2026, 8, 11, 2, 0, 0, DateTimeKind.Utc),
                ["note"] = "safe",
                ["payload"] = new Dictionary<string, object?> { ["x"] = 2 },
                ["totalRows"] = 2
            }
        };
        var positive = BuildTable(positiveRows);
        var json = Property<string?>(positive, "ColumnManifestJson");
        var sha = Property<string?>(positive, "ColumnManifestSha256");
        Require(json is not null && sha is not null, "POSITIVE_SIDECAR_REQUIRED");
        var parsed = StatRunExportColumnManifestContract.Parse(json, sha);
        Require(parsed.Manifest.Columns[0] == new StatRunExportColumnManifestEntry(
                0,
                "ordinal",
                StatRunExportColumnManifestContract.ValueTypes.Integer,
                StatRunExportColumnManifestContract.BlankPolicies.Forbidden,
                false),
            "ORDINAL_CONTRACT");
        Require(Column(parsed, "amount").ValueType ==
                StatRunExportColumnManifestContract.ValueTypes.Decimal,
            "NUMBER_IS_DECIMAL");
        Require(Column(parsed, "active").ValueType ==
                StatRunExportColumnManifestContract.ValueTypes.Boolean,
            "BOOLEAN_NATIVE");
        Require(Column(parsed, "occurredAtUtc").ValueType ==
                StatRunExportColumnManifestContract.ValueTypes.UtcInstant,
            "UTC_NATIVE");
        Require(Column(parsed, "note").BlankPolicy ==
                StatRunExportColumnManifestContract.BlankPolicies.Empty,
            "EMPTY_POLICY");
        Require(Column(parsed, "optional").BlankPolicy ==
                StatRunExportColumnManifestContract.BlankPolicies.Null,
            "NULL_POLICY");
        Require(Column(parsed, "payload").ValueType ==
                StatRunExportColumnManifestContract.ValueTypes.Json,
            "JSON_NATIVE");
        Require(Column(parsed, "totalRows").IsFullFilterTotal,
            "FROZEN_TOTAL_REGISTRY");
        Require(RenderCsvLength(positive) > 0, "POSITIVE_RENDER_COMPLETES");

        var mixed = BuildTable(
        [
            new Dictionary<string, object?> { ["value"] = 1m },
            new Dictionary<string, object?> { ["value"] = "one" }
        ]);
        Require(Property<string?>(mixed, "ColumnManifestJson") is null &&
                Property<string?>(mixed, "ColumnManifestSha256") is null,
            "MIXED_NATIVE_HAS_NO_SIDECAR");
        Require(RenderCsvLength(mixed) > 0, "MIXED_NATIVE_P9_RENDER_COMPLETES");

        var nullAndEmpty = BuildTable(
        [
            new Dictionary<string, object?> { ["value"] = null },
            new Dictionary<string, object?> { ["value"] = string.Empty },
            new Dictionary<string, object?> { ["value"] = "present" }
        ]);
        Require(Property<string?>(nullAndEmpty, "ColumnManifestJson") is null,
            "NULL_EMPTY_AMBIGUOUS_HAS_NO_SIDECAR");
        Require(RenderCsvLength(nullAndEmpty) > 0,
            "NULL_EMPTY_P9_RENDER_COMPLETES");

        var directField = BuildTable(
        [
            new FieldStatisticSummaryRow
            {
                WorkId = "work-direct",
                ScopeType = "ASSIGNMENT",
                ScopeId = "assignment-direct",
                DynamicFormTemplateId = "form-direct",
                FieldId = "field-amount",
                FieldKey = "amount",
                FieldLabel = "Amount",
                FieldType = "NUMBER",
                StatisticLabelCodes = ["amount"],
                ShowInTree = true,
                PeriodKey = "2026-08",
                PeriodInstanceKey = "2026-08",
                PeriodKind = "MONTH",
                ReportStatus = 2,
                ValueCount = 1,
                NumericValueCount = 1,
                Sum = 10m,
                Min = 10m,
                Max = 10m,
                Average = 10m,
                ReportCount = 1,
                UpdatedAtUtc = new DateTime(
                    2026, 8, 14, 1, 2, 3, DateTimeKind.Utc)
            }
        ], StatRunExportResultKinds.DirectField);
        var directJson = Property<string?>(
            directField,
            "ColumnManifestJson");
        var directSha = Property<string?>(
            directField,
            "ColumnManifestSha256");
        Require(directJson is not null && directSha is not null,
            "DIRECT_FIELD_DECLARED_SIDECAR_REQUIRED");
        var directParsed = StatRunExportColumnManifestContract.Parse(
            directJson,
            directSha);
        Require(Column(directParsed, "earliestDateUtc").ValueType ==
                    StatRunExportColumnManifestContract.ValueTypes.UtcInstant &&
                Column(directParsed, "earliestDateUtc").BlankPolicy ==
                    StatRunExportColumnManifestContract.BlankPolicies.Null,
            "DIRECT_FIELD_NULL_DATE_SCHEMA");
        Require(Column(directParsed, "bucketKey").ValueType ==
                    StatRunExportColumnManifestContract.ValueTypes.Text &&
                Column(directParsed, "bucketKey").BlankPolicy ==
                    StatRunExportColumnManifestContract.BlankPolicies.Null,
            "DIRECT_FIELD_NULL_BUCKET_SCHEMA");
        Require(Column(directParsed, "sum").ValueType ==
                StatRunExportColumnManifestContract.ValueTypes.Decimal,
            "DIRECT_FIELD_NUMERIC_SCHEMA");
        Require(StatRunExportColumnManifestContract.DeclaredValueType(
                    StatRunExportResultKinds.DirectField,
                    "latestDateUtc") ==
                StatRunExportColumnManifestContract.ValueTypes.UtcInstant &&
                StatRunExportColumnManifestContract.DeclaredValueType(
                    StatRunExportResultKinds.DirectField,
                    "unknown") is null &&
                StatRunExportColumnManifestContract.DeclaredValueType(
                    StatRunExportResultKinds.Basic,
                    "latestDateUtc") is null,
            "DIRECT_FIELD_SHARED_SCHEMA_EXACT");

        var untypedAllNull = BuildTable(
        [
            new Dictionary<string, object?> { ["value"] = null }
        ]);
        Require(Property<string?>(untypedAllNull, "ColumnManifestJson") is null,
            "UNTYPED_ALL_NULL_REMAINS_FAIL_CLOSED");
        RequireThrows(
            () => StatRunExportColumnManifestContract.Parse(null, null),
            "EXPORT_COLUMN_MANIFEST_REQUIRED");
        RequireThrows(
            () => StatRunExportColumnManifestContract.Parse(
                json!.Replace("DECIMAL", "TEXT", StringComparison.Ordinal),
                sha),
            "EXPORT_COLUMN_MANIFEST_DIGEST_MISMATCH");

        await ArtifactReadGuardControlsAsync().ConfigureAwait(false);
    }

    private static async Task ArtifactReadGuardControlsAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"p10-export-read-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var content = new byte[] { 1, 2, 3, 4 };
            var expectedHash = Convert.ToHexString(SHA256.HashData(content))
                .ToLowerInvariant();
            var validPath = Path.Combine(root, "valid.bin");
            await File.WriteAllBytesAsync(validPath, content).ConfigureAwait(false);
            var read = await StatRunExportService.ReadArtifactFileGuardedAsync(
                    validPath,
                    expectedHash,
                    content.LongLength,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Require(read.SequenceEqual(content), "BOUNDED_READ_POSITIVE");

            var oversizedPath = Path.Combine(root, "oversized.bin");
            await using (var oversized = new FileStream(
                             oversizedPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                oversized.SetLength(StatRunExportContract.MaxBytes + 1L);
            }
            await RequireAppReasonAsync(
                () => StatRunExportService.ReadArtifactFileGuardedAsync(
                    oversizedPath,
                    expectedHash,
                    content.LongLength,
                    CancellationToken.None),
                "EXPORT_ARTIFACT_INTEGRITY_FAILED");

            await RequireAppReasonAsync(
                () => StatRunExportService.ReadArtifactFileGuardedAsync(
                    validPath,
                    expectedHash,
                    StatRunExportContract.MaxBytes + 1L,
                    CancellationToken.None),
                "EXPORT_ARTIFACT_BOUNDS_INVALID");

            var lockedPath = Path.Combine(root, "locked.bin");
            await File.WriteAllBytesAsync(lockedPath, content).ConfigureAwait(false);
            await using var locked = new FileStream(
                lockedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            await RequireAppReasonAsync(
                () => StatRunExportService.ReadArtifactFileGuardedAsync(
                    lockedPath,
                    expectedHash,
                    content.LongLength,
                    CancellationToken.None),
                "EXPORT_ARTIFACT_READ_FAILED");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RequireAppReasonAsync(
        Func<Task> action,
        string expected)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (AppException exception)
        {
            var reason = exception.Details?
                .GetType()
                .GetProperty("reason")?
                .GetValue(exception.Details) as string;
            if (string.Equals(reason, expected, StringComparison.Ordinal))
                return;
            throw new InvalidOperationException(
                $"EXPECTED_{expected}_ACTUAL_{reason}");
        }
        throw new InvalidOperationException($"EXPECTED_{expected}");
    }

    private static object BuildTable(
        IReadOnlyList<object> rows,
        string resultKind = StatRunExportResultKinds.Basic)
    {
        var method = typeof(StatRunExportService).GetMethod(
                         "BuildTable",
                         BindingFlags.Static | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("BUILD_TABLE_MISSING");
        return method.Invoke(null, [resultKind, rows])
               ?? throw new InvalidOperationException("BUILD_TABLE_NULL");
    }

    private static long RenderCsvLength(object table)
    {
        var method = typeof(StatRunExportService).GetMethod(
                         "RenderCsv",
                         BindingFlags.Static | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("RENDER_CSV_MISSING");
        var rendering = method.Invoke(null, [table])
                        ?? throw new InvalidOperationException("RENDER_CSV_NULL");
        return Property<byte[]>(rendering, "Content").LongLength;
    }

    private static T Property<T>(object value, string name)
        => (T)(value.GetType().GetProperty(name)?.GetValue(value)
               ?? (default(T) is null
                   ? default(T)!
                   : throw new InvalidOperationException($"{name}_NULL")));

    private static StatRunExportColumnManifestEntry Column(
        StatRunExportColumnManifestSidecar sidecar,
        string name)
        => sidecar.Manifest.Columns.Single(column =>
            string.Equals(column.Name, name, StringComparison.Ordinal));

    private static void RequireThrows(Action action, string expected)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException exception)
            when (string.Equals(exception.Message, expected, StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException($"EXPECTED_{expected}");
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException(reason);
    }
}
