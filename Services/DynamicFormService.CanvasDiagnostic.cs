using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;

namespace tdtd_be.Services;

public sealed partial class DynamicFormService
{
    // Deliberate allowlist: schema/statistic/version data only, not arbitrary
    // future owner fields. Presence is recorded before any typed deserialization.
    private static readonly string[] DiagnosticStorageFields =
    [
        "code", "name", "description", "tagCodes", "schemaVersion", "versionNo", "familyId",
        "previousVersionId", "clonedFromVersionId", "lineageStatus", "revision", "isActive", "isPublished",
        "sectionsJson", "fieldsJson", "blocksJson", "excelBlockJson", "nativeTablesVersion", "tablesJson",
        "excelBlockDynamicExcelTemplateId", "publishedSchemaSnapshotJson", "publishedSchemaHash", "publishedAtUtc",
        "statisticConfigId", "statisticConfigVersionId", "statisticConfigPreviousVersionId", "statisticConfigVersionNo",
        "statisticConfigRevision", "statisticConfigStatus", "statisticConfigHash", "statisticConfigDependencyPins",
        "statisticConfigSections", "statisticConfigSnapshots", "nativeStatisticPublicationFence"
    ];

    public async Task<DynamicFormStorageDiagnostic> GetStorageDiagnosticAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var normalizedId = NormalizeDynamicFormTemplateId(id);
        var collection = _ctx.Db.GetCollection<BsonDocument>(_ctx.DynamicFormTemplates.CollectionNamespace.CollectionName);
        // Reuse the driver's physical document; typed defaults would erase the
        // difference between absent, null and an explicitly stored empty array.
        var stored = await collection.Find(new BsonDocument("_id", ObjectId.Parse(normalizedId)))
            .FirstOrDefaultAsync(ct) ?? throw DynamicFormNotFound(normalizedId);
        RequireStorageDiagnosticAccess(stored, normalizedId, me);
        var settings = new JsonWriterSettings { OutputMode = JsonOutputMode.CanonicalExtendedJson };
        var fields = DiagnosticStorageFields.ToDictionary(name => name, name => stored.TryGetValue(name, out var value)
            ? new DynamicFormStoredValue(true, value.BsonType.ToString(), value.ToJson(settings))
            : new DynamicFormStoredValue(false, null, null), StringComparer.Ordinal);
        return new DynamicFormStorageDiagnostic("dynamic-form-storage-diagnostic", 1, normalizedId,
            Convert.ToHexString(SHA256.HashData(stored.ToBson())).ToLowerInvariant(),
            ReadOnly: true, MigrationSupported: false, ReferenceScanComplete: false, fields);
    }

    private static void RequireStorageDiagnosticAccess(BsonDocument stored, string id, MeResponse me)
    {
        if (stored.TryGetValue("isDeleted", out var deleted) && (!deleted.IsBoolean || deleted.AsBoolean))
            throw DynamicFormNotFound(id);
        var creator = stored.GetValue("createdByUserId", BsonNull.Value);
        // Same owner/admin history policy for both raw and reference evidence.
        RequireCanReadVersionHistory(me, new DynamicFormTemplate
        {
            Id = id,
            CreatedByUserId = creator.IsObjectId ? creator.AsObjectId.ToString() : creator.IsString ? creator.AsString : null
        });
    }
}
