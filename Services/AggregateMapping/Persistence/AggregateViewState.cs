using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping.Persistence;

// One deterministic head per owner. Results exist only in the referenced mapping
// instance. Changing the target pin creates a generation, never edits a published form.
internal sealed record AggregateViewGeneration(string BindingId, AggregateFormPinDto Form, string? InstanceId, DateTimeOffset CreatedAt);
internal sealed record AggregateViewState(string Id, string WorkId, string OwnerKind, string OwnerId,
    AggregateViewGeneration Current, IReadOnlyList<AggregateViewGeneration> History, int PayloadRevision);

internal static class AggregateViewRetention
{
    internal const int MaxVersions = 3; // Includes the current generation.
    internal static AggregateViewGeneration[] HistoryAfterBind(AggregateViewState? previous)
        => previous == null ? [] : previous.History.Append(previous.Current).TakeLast(MaxVersions - 1).ToArray();
    internal static AggregateViewGeneration[] DiscardedAfterBind(AggregateViewState? previous)
        => previous == null ? [] : previous.History.Append(previous.Current).Except(HistoryAfterBind(previous)).ToArray();
}
