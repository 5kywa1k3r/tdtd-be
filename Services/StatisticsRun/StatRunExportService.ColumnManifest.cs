using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsRun;

public sealed partial class StatRunExportService
{
    private static StatRunExportColumnManifestSidecar? BuildColumnManifest(
        string resultKind,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<ExportCell>> rows)
    {
        if (headers.Count == 0)
            return null;
        if (rows.Any(row => row.Count != headers.Count))
            return null;
        var directFieldSchema = string.Equals(
            resultKind,
            StatRunExportResultKinds.DirectField,
            StringComparison.Ordinal);

        var columns = new List<StatRunExportColumnManifestEntry>(headers.Count);
        for (var ordinal = 0; ordinal < headers.Count; ordinal++)
        {
            var name = headers[ordinal];
            if (ordinal == 0)
            {
                if (!string.Equals(name, "ordinal", StringComparison.Ordinal))
                    return null;
                columns.Add(new StatRunExportColumnManifestEntry(
                    ordinal,
                    name,
                    StatRunExportColumnManifestContract.ValueTypes.Integer,
                    StatRunExportColumnManifestContract.BlankPolicies.Forbidden,
                    false));
                continue;
            }

            string? valueType = null;
            var declaredValueType = directFieldSchema
                ? StatRunExportColumnManifestContract.DeclaredValueType(
                    resultKind,
                    name)
                : null;
            if (directFieldSchema && declaredValueType is null)
                return null;
            var sawNull = false;
            var sawEmpty = false;
            foreach (var row in rows)
            {
                var cell = row[ordinal];
                if (cell.Kind == ExportCellKinds.Null)
                {
                    sawNull = true;
                    continue;
                }
                if (cell.Kind == ExportCellKinds.Text &&
                    string.IsNullOrEmpty(cell.Text))
                {
                    sawEmpty = true;
                    continue;
                }

                var current = cell.Kind switch
                {
                    ExportCellKinds.Number =>
                        StatRunExportColumnManifestContract.ValueTypes.Decimal,
                    ExportCellKinds.Boolean =>
                        StatRunExportColumnManifestContract.ValueTypes.Boolean,
                    ExportCellKinds.Date =>
                        StatRunExportColumnManifestContract.ValueTypes.UtcInstant,
                    ExportCellKinds.Text =>
                        StatRunExportColumnManifestContract.ValueTypes.Text,
                    ExportCellKinds.Json =>
                        StatRunExportColumnManifestContract.ValueTypes.Json,
                    _ => null
                };
                if (current is null)
                    return null;
                if (declaredValueType is not null &&
                    !string.Equals(
                        current,
                        declaredValueType,
                        StringComparison.Ordinal))
                {
                    return null;
                }
                if (valueType is not null &&
                    !string.Equals(valueType, current, StringComparison.Ordinal))
                    return null;
                valueType = current;
            }

            valueType ??= declaredValueType;
            if (valueType is null)
                return null;
            if (sawNull && sawEmpty)
                return null;
            var blankPolicy = sawNull
                ? StatRunExportColumnManifestContract.BlankPolicies.Null
                : sawEmpty
                    ? StatRunExportColumnManifestContract.BlankPolicies.Empty
                    : StatRunExportColumnManifestContract.BlankPolicies.Forbidden;
            columns.Add(new StatRunExportColumnManifestEntry(
                ordinal,
                name,
                valueType,
                blankPolicy,
                IsFrozenFullFilterTotal(name)));
        }

        try
        {
            return StatRunExportColumnManifestContract.Create(columns);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsFrozenFullFilterTotal(string name)
        => name.Length > 5 &&
           name.StartsWith("total", StringComparison.Ordinal) &&
           name[5] is >= 'A' and <= 'Z';

}
