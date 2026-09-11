using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation.EvidenceExport;

public static class StatisticReconciliationEvidenceCanonical
{
    public const int MaximumRows = 10_000;
    public const int MaximumContentBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static StatisticReconciliationEvidenceExport Compile(
        StatisticReconciliationEvidenceSnapshot snapshot,
        StatisticReconciliationEvidenceCompileCommand command)
    {
        RequireSnapshot(snapshot);
        var commandId = Token(command.CommandId, "commandId", 128);
        var format = Upper(command.Format, "format", 16);
        if (!StatisticReconciliationEvidenceFormats.All.Contains(format))
            throw Invalid("format");
        var actorId = ObjectId(command.ActorId, "actorId");
        var permission = Sha(command.PermissionSnapshotSha256,
            "permissionSnapshotSha256");
        if (command.IncludeOperatorDetail && !command.CanViewOperatorDetail)
            throw Denied("operatorDetail");
        var requestedAt = Utc(command.RequestedAtUtc, "requestedAtUtc");
        if (command.Retention < TimeSpan.FromHours(1) ||
            command.Retention > TimeSpan.FromDays(90))
            throw Invalid("retention");
        var expiresAt = requestedAt.Add(command.Retention);
        var detail = command.IncludeOperatorDetail
            ? StatisticReconciliationEvidenceDetailLevels.Operator
            : StatisticReconciliationEvidenceDetailLevels.Redacted;
        var rows = NormalizeRows(snapshot.Rows, command.IncludeOperatorDetail);
        if (rows.Length > MaximumRows)
            throw new StatisticReconciliationEvidenceException(
                StatisticReconciliationEvidenceFailureCodes.ScopeLimit, "rows");

        var rowManifest = HashSequence("P10_EVIDENCE_ROW_MANIFEST_V1",
            rows.Select(RowSemantic));
        var manifestBytes = ManifestBytes(snapshot, commandId, format, detail,
            permission, requestedAt, expiresAt, rows.Length, rowManifest);
        var manifestJson = Utf8.GetString(manifestBytes);
        var manifestSha = HashBytes(manifestBytes);
        var content = format == StatisticReconciliationEvidenceFormats.Json
            ? JsonContent(manifestJson, rows)
            : CsvContent(rows);
        if (content.LongLength > MaximumContentBytes)
            throw new StatisticReconciliationEvidenceException(
                StatisticReconciliationEvidenceFailureCodes.ScopeLimit, "bytes");
        var contentSha = HashBytes(content);
        var extension = format == StatisticReconciliationEvidenceFormats.Json
            ? "json" : "csv";
        var fileName = $"reconciliation-{SafeToken(snapshot.ReconciliationId)}-" +
            $"{SafeToken(snapshot.GenerationId)}-{contentSha[..12]}.{extension}";
        var id = Hash("P10_EVIDENCE_ARTIFACT_ID_V1", snapshot.WorkId,
            snapshot.ScopeAssignmentId, snapshot.ReconciliationId, commandId,
            format, detail, manifestSha, contentSha);
        var documentSha = Hash("P10_EVIDENCE_DOCUMENT_V1", id, commandId,
            snapshot.WorkId, snapshot.ScopeAssignmentId, snapshot.ReconciliationId,
            snapshot.GenerationId, snapshot.GenerationSha256,
            snapshot.SemanticVerdictSha256, snapshot.ReviewSignatureSha256,
            snapshot.FinalApprovalSha256, permission, actorId, format, detail,
            fileName, manifestSha, contentSha,
            content.LongLength.ToString(CultureInfo.InvariantCulture),
            requestedAt.ToString("O", CultureInfo.InvariantCulture),
            expiresAt.ToString("O", CultureInfo.InvariantCulture));
        return new StatisticReconciliationEvidenceExport
        {
            Id = id,
            CommandId = commandId,
            WorkId = snapshot.WorkId,
            ScopeAssignmentId = snapshot.ScopeAssignmentId,
            ReconciliationId = snapshot.ReconciliationId,
            GenerationId = snapshot.GenerationId,
            GenerationSha256 = snapshot.GenerationSha256,
            SemanticVerdictSha256 = snapshot.SemanticVerdictSha256,
            ReviewSignatureSha256 = snapshot.ReviewSignatureSha256,
            FinalApprovalSha256 = snapshot.FinalApprovalSha256,
            PermissionSnapshotSha256 = permission,
            CreatedByActorId = actorId,
            Format = format,
            DetailLevel = detail,
            FileName = fileName,
            ContentType = format == StatisticReconciliationEvidenceFormats.Json
                ? "application/json; charset=utf-8" : "text/csv; charset=utf-8",
            ManifestJson = manifestJson,
            ManifestSha256 = manifestSha,
            ContentSha256 = contentSha,
            ContentLength = content.LongLength,
            Content = content,
            CreatedAtUtc = requestedAt,
            ExpiresAtUtc = expiresAt,
            DocumentSha256 = documentSha
        };
    }

    public static void RequireStored(StatisticReconciliationEvidenceExport value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.SchemaVersion != "P10_RECONCILIATION_EVIDENCE_EXPORT_V1" ||
            HashBytes(Utf8.GetBytes(value.ManifestJson)) != value.ManifestSha256 ||
            HashBytes(value.Content) != value.ContentSha256 ||
            value.Content.LongLength != value.ContentLength ||
            value.FileName != Path.GetFileName(value.FileName) ||
            value.CreatedAtUtc.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            value.ExpiresAtUtc.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            value.ExpiresAtUtc <= value.CreatedAtUtc)
            throw Drift("stored");
        var expected = Hash("P10_EVIDENCE_DOCUMENT_V1", value.Id,
            value.CommandId, value.WorkId, value.ScopeAssignmentId,
            value.ReconciliationId, value.GenerationId,
            value.GenerationSha256, value.SemanticVerdictSha256,
            value.ReviewSignatureSha256, value.FinalApprovalSha256,
            value.PermissionSnapshotSha256, value.CreatedByActorId,
            value.Format, value.DetailLevel, value.FileName,
            value.ManifestSha256, value.ContentSha256,
            value.ContentLength.ToString(CultureInfo.InvariantCulture),
            Utc(value.CreatedAtUtc, "createdAtUtc").ToString("O", CultureInfo.InvariantCulture),
            Utc(value.ExpiresAtUtc, "expiresAtUtc").ToString("O", CultureInfo.InvariantCulture));
        if (expected != value.DocumentSha256)
            throw Drift("document");
    }

    public static string Hash(string domain, params object?[] values)
        => HashBytes(Utf8.GetBytes(string.Join("\n", new[] { domain }.Concat(
            values.Select(value => Convert.ToString(value,
                CultureInfo.InvariantCulture) ?? "<NULL>")))));

    public static string HashSequence(string domain, IEnumerable<string> values)
    {
        var material = values.ToArray();
        return Hash(domain, material.Length,
            string.Join("\n", material.Select((value, index) => $"{index}:{value}")));
    }

    public static string HashBytes(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static ImmutableArray<StatisticReconciliationEvidenceRow> NormalizeRows(
        ImmutableArray<StatisticReconciliationEvidenceRow> rows, bool detail)
    {
        if (rows.IsDefault)
            throw Invalid("rows");
        return rows.Select(row => new StatisticReconciliationEvidenceRow(
                Token(row.Identity, "identity", 512),
                Token(row.Config, "config", 512),
                Cell(row.Expected, "expected"), Cell(row.Actual, "actual"),
                Cell(row.Delta, "delta"),
                Token(row.Freshness, "freshness", 512),
                Token(row.Permission, "permission", 512),
                Token(row.Verdict, "verdict", 128),
                detail ? row.SourceStableIds.Select(value =>
                        Token(value, "sourceStableId", 512))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                    .ToImmutableArray() : []))
            .OrderBy(value => value.Identity, StringComparer.Ordinal)
            .ThenBy(value => value.Config, StringComparer.Ordinal)
            .ThenBy(value => value.Expected.CanonicalJson, StringComparer.Ordinal)
            .ThenBy(value => value.Actual.CanonicalJson, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static StatisticReconciliationEvidenceCell Cell(
        StatisticReconciliationEvidenceCell cell, string field)
    {
        ArgumentNullException.ThrowIfNull(cell);
        var canonical = Token(cell.CanonicalJson, field, 64 * 1024);
        try { using var _ = JsonDocument.Parse(canonical); }
        catch (JsonException) { throw Invalid(field); }
        return new(Upper(cell.ValueType, field, 64),
            Upper(cell.ValueState, field, 64), canonical);
    }

    private static byte[] ManifestBytes(StatisticReconciliationEvidenceSnapshot s,
        string commandId, string format, string detail, string permission,
        DateTime createdAt, DateTime expiresAt, int rowCount, string rowManifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               { Indented = false, Encoder = JavaScriptEncoder.Default }))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", "P10_EVIDENCE_MANIFEST_V1");
            writer.WriteString("commandId", commandId);
            writer.WriteString("workId", s.WorkId);
            writer.WriteString("scopeAssignmentId", s.ScopeAssignmentId);
            writer.WriteString("reconciliationId", s.ReconciliationId);
            writer.WriteString("generationId", s.GenerationId);
            writer.WriteString("generationSha256", s.GenerationSha256);
            writer.WriteString("semanticVerdictSha256", s.SemanticVerdictSha256);
            writer.WriteString("reviewSignatureSha256", s.ReviewSignatureSha256);
            writer.WriteString("finalApprovalSha256", s.FinalApprovalSha256);
            writer.WriteString("permissionSnapshotSha256", permission);
            writer.WriteString("format", format);
            writer.WriteString("detailLevel", detail);
            writer.WriteNumber("rowCount", rowCount);
            writer.WriteString("rowManifestSha256", rowManifest);
            writer.WriteString("snapshotAtUtc", Utc(s.SnapshotAtUtc, "snapshotAtUtc"));
            writer.WriteString("createdAtUtc", createdAt);
            writer.WriteString("expiresAtUtc", expiresAt);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static byte[] JsonContent(string manifestJson,
        ImmutableArray<StatisticReconciliationEvidenceRow> rows)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.Default });
        writer.WriteStartObject();
        writer.WritePropertyName("manifest");
        using (var manifest = JsonDocument.Parse(manifestJson))
            manifest.RootElement.WriteTo(writer);
        writer.WriteStartArray("rows");
        foreach (var row in rows)
        {
            writer.WriteStartObject();
            writer.WriteString("identity", row.Identity);
            writer.WriteString("config", row.Config);
            WriteCell(writer, "expected", row.Expected);
            WriteCell(writer, "actual", row.Actual);
            WriteCell(writer, "delta", row.Delta);
            writer.WriteString("freshness", row.Freshness);
            writer.WriteString("permission", row.Permission);
            writer.WriteString("verdict", row.Verdict);
            writer.WriteStartArray("sourceStableIds");
            foreach (var value in row.SourceStableIds) writer.WriteStringValue(value);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] CsvContent(ImmutableArray<StatisticReconciliationEvidenceRow> rows)
    {
        var result = new StringBuilder();
        result.AppendLine("Identity,Config,Expected,Actual,Delta,Freshness,Permission,Verdict");
        foreach (var row in rows)
        {
            var values = new[] { row.Identity, row.Config,
                row.Expected.CanonicalJson, row.Actual.CanonicalJson,
                row.Delta.CanonicalJson, row.Freshness, row.Permission, row.Verdict };
            result.AppendLine(string.Join(',', values.Select(Csv)));
        }
        return Utf8.GetBytes(result.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static void WriteCell(Utf8JsonWriter writer, string name,
        StatisticReconciliationEvidenceCell cell)
    {
        writer.WriteStartObject(name);
        writer.WriteString("valueType", cell.ValueType);
        writer.WriteString("valueState", cell.ValueState);
        writer.WritePropertyName("value");
        using var value = JsonDocument.Parse(cell.CanonicalJson);
        value.RootElement.WriteTo(writer);
        writer.WriteEndObject();
    }

    private static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    private static string RowSemantic(StatisticReconciliationEvidenceRow row)
        => Hash("P10_EVIDENCE_ROW_V1", row.Identity, row.Config,
            CellSemantic(row.Expected), CellSemantic(row.Actual),
            CellSemantic(row.Delta), row.Freshness, row.Permission, row.Verdict,
            HashSequence("P10_EVIDENCE_ROW_SOURCE_SET_V1", row.SourceStableIds));
    private static string CellSemantic(StatisticReconciliationEvidenceCell cell)
        => Hash("P10_EVIDENCE_CELL_V1", cell.ValueType, cell.ValueState,
            cell.CanonicalJson);

    private static void RequireSnapshot(StatisticReconciliationEvidenceSnapshot value)
    {
        ObjectId(value.WorkId, "workId"); ObjectId(value.ScopeAssignmentId, "scopeAssignmentId");
        ObjectId(value.ReconciliationId, "reconciliationId");
        Token(value.GenerationId, "generationId", 256);
        Sha(value.GenerationSha256, "generationSha256");
        Sha(value.SemanticVerdictSha256, "semanticVerdictSha256");
        Sha(value.ReviewSignatureSha256, "reviewSignatureSha256");
        Sha(value.FinalApprovalSha256, "finalApprovalSha256");
        Utc(value.SnapshotAtUtc, "snapshotAtUtc");
    }

    private static string ObjectId(string? value, string field)
        => value is { Length: 24 } && value.All(Uri.IsHexDigit)
            ? value.ToLowerInvariant() : throw Invalid(field);
    private static string Sha(string? value, string field)
        => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value : throw Invalid(field);
    private static string Token(string? value, string field, int max)
        => value is not null && value.Length is > 0 && value.Length <= max && value == value.Trim()
            ? value : throw Invalid(field);
    private static string Upper(string? value, string field, int max)
    {
        var token = Token(value, field, max);
        return token == token.ToUpperInvariant() ? token : throw Invalid(field);
    }
    private static DateTime Utc(DateTime value, string field)
    {
        if (value == default || value.Kind != DateTimeKind.Utc)
            throw Invalid(field);
        return value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMillisecond));
    }
    private static string SafeToken(string value)
        => new(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    private static StatisticReconciliationEvidenceException Invalid(string detail)
        => new(StatisticReconciliationEvidenceFailureCodes.InvalidRequest, detail);
    private static StatisticReconciliationEvidenceException Denied(string detail)
        => new(StatisticReconciliationEvidenceFailureCodes.PermissionDenied, detail);
    private static StatisticReconciliationEvidenceException Drift(string detail)
        => new(StatisticReconciliationEvidenceFailureCodes.Drift, detail);
}
