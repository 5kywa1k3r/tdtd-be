using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping.Persistence;

// One deterministic head per owner. Results exist only in the referenced mapping
// instance. Changing the target pin creates a generation, never edits a published form.
internal sealed record AggregateViewGeneration(string BindingId, AggregateFormPinDto Form, string? InstanceId, DateTimeOffset CreatedAt);
internal sealed record AggregateViewState(string Id, string WorkId, string OwnerKind, string OwnerId,
    AggregateViewGeneration Current, IReadOnlyList<AggregateViewGeneration> History, int PayloadRevision);
