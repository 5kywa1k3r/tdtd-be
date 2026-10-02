namespace tdtd_be.DTOs.DynamicForms;

// No identity or metadata of another account's form is exposed.
public sealed record DynamicFormCodeAvailability(
    bool Available,
    string? ExistingOwnedFormId,
    bool CanCorrectRejectedCreate);
