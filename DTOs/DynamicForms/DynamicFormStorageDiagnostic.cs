namespace tdtd_be.DTOs.DynamicForms;

// ValueJson is canonical Extended JSON of one stored BSON value. A stored JSON
// string stays a string, even if its contents are malformed or have duplicate keys.
public sealed record DynamicFormStoredValue(bool Present, string? BsonType, string? ValueJson);

public sealed record DynamicFormStorageDiagnostic(
    string ContractId,
    int Version,
    string OwnerId,
    string StorageSha256,
    bool ReadOnly,
    bool MigrationSupported,
    bool ReferenceScanComplete,
    IReadOnlyDictionary<string, DynamicFormStoredValue> Fields);
