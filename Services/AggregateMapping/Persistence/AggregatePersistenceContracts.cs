using System.Text.Json;
using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping.Persistence;

// Application-owned records, never model-bound as authority from HTTP.
internal sealed record AggregateConfigHead(string Id, string WorkId, string AssignmentId, string BindingId,
    string OwnerUserId, AggregateFormPinDto TargetForm, long HeadRevision);
internal sealed record AggregateConfigVersion(string ConfigId, long Revision, AggregateRecipeDto Recipe,
    string RecipeHash, string Actor, DateTimeOffset CreatedAt);
internal sealed record AggregateInstanceState(string Id, AggregatePeriodContextDto Context, string ConfigId,
    long ConfigRevision, long Revision, long Generation, string State, AggregateInstanceOverrideDto? Overrides,
    AggregateInstanceSelectionDto Selection, IReadOnlyList<string> UnlinkedMembers, AggregateRawDraftDto? RawDraft,
    AggregatePreviewEnvelope? Applied, string? SubmissionId, string AuthorityUserId,
    long AppliedGeneration = 0, string? AppliedInputStamp = null, string? ComputationProfile = null);
internal sealed record AggregateDependencyState(string InstanceId, string TargetReportId, long Generation,
    IReadOnlyList<string> Keys, IReadOnlyList<AggregateSourcePinDto> Reports, IReadOnlyList<AggregateCoverageSlotDto> Slots);
internal sealed record AggregateLockState(string Kind, string Identity, IReadOnlyList<AggregateLockOwnerDto> Owners,
    string WorkId = "", IReadOnlyList<string>? Keys = null);
internal sealed record AggregateFrozenState(string SubmissionId, string InstanceId, string TargetReportId,
    long SubmissionLifecycleRevision, AggregateInstanceState Instance, AggregatePreviewEnvelope Preview, DateTimeOffset FrozenAt);
internal sealed record AggregateDeclarationState(string Kind, string Identity, string WorkId, string AssignmentId,
    AggregateDataWindowDeclarationDto Declaration);
internal sealed record AggregateReceipt(string RequestHash, string Actor, string Operation, JsonElement Result);
internal sealed record AggregateRefreshIntent(string InstanceId, long Generation, string EventId, string AuthorityUserId,
    string State, string? ErrorCode, string? DiffReference, string? Lease = null,
    DateTimeOffset? LeaseUntil = null, DateTimeOffset? DispatchUntil = null, int Attempt = 0,
    AggregatePreviewProgress? Progress = null, string? InputStamp = null, DateTimeOffset? RetryNotBefore = null);
internal sealed record AggregateStored<T>(long Version, T Value);
internal sealed record AggregateQuery(string WorkId, string? Target = null, string? DependencyKey = null);
internal sealed record AggregateCommitAuthority(string Actor, string SessionKey, AggregateReadContext Read,
    IReadOnlyList<AggregateAuthorityPin> Pins);
internal sealed record AggregateAuthorityPin(string Collection, string Id, string Fingerprint);
internal sealed record AggregateCommandContext(string CommandId, string Operation, string ContextId,
    string Actor, string SessionKey, DateTimeOffset Now);
internal sealed record AggregateCommandResult(string Id, long Revision, long PayloadRevision, long LifecycleRevision,
    string State, bool Replayed = false);
internal sealed record AggregateConfigImpactPlan(string ConfigId, long HeadRevision, AggregateRecipeDto Recipe,
    IReadOnlyList<AggregateInstanceState> Instances, IReadOnlyList<AggregateMigrationConflictDto> Conflicts,
    IReadOnlyDictionary<string, AggregatePreviewEnvelope> Previews,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyDictionary<string, AggregateCommitAuthority> Authorities);
internal sealed record AggregateConfirmation(string Operation, string Actor, string SessionKey, string ContextId,
    string RequestHash, string EvidenceHash, DateTimeOffset ExpiresAt);
internal sealed record AggregateTargetWrite(AggregateInstanceState Instance, AggregateRecipeDto Recipe,
    AggregatePreviewEnvelope Preview, IReadOnlyList<string> Members);
internal sealed record AggregateNativeContentBinding(string ReportId, string TableId, string SchemaHash, AggregateContentReference Reference);

internal static class AggregateCollections
{
    internal const string Configs = "aggregate_configs", Versions = "aggregate_config_revisions",
        Instances = "aggregate_mapping_instances", Dependencies = "aggregate_dependencies",
        Locks = "aggregate_source_lock_owners", Frozen = "aggregate_submission_snapshots",
        Declarations = "aggregate_data_windows", Receipts = "aggregate_command_receipts",
        Targets = "aggregate_target_bindings", Refresh = "aggregate_refresh_intents", NativeContent = "aggregate_native_content_bindings",
        ContentSnapshots = "aggregate_content_snapshot_intents", Views = "aggregate_views";
    internal static readonly string[] All = [Configs, Versions, Instances, Dependencies, Locks, Frozen,
        Declarations, Receipts, Targets, Refresh, NativeContent, ContentSnapshots, Views];
}

internal interface IAggregateTransaction
{
    Task<AggregateStored<T>?> GetAsync<T>(string collection, string id, CancellationToken ct);
    Task<IReadOnlyList<AggregateStored<T>>> QueryAsync<T>(string collection, AggregateQuery query, CancellationToken ct);
    // expectedVersion=0 means insert only. Delete also requires an exact version.
    Task PutAsync<T>(string collection, string id, long expectedVersion, T value, string workId,
        string? target, IReadOnlyList<string> keys, CancellationToken ct);
    Task DeleteAsync(string collection, string id, long expectedVersion, CancellationToken ct);
    // All implementations must use the SAME transaction as aggregate records and receipt.
    Task FenceAsync(AggregateCommitAuthority authority, IReadOnlyList<AggregateSourcePinDto> sources,
        IReadOnlyList<AggregateCoverageSlotDto> slots, CancellationToken ct);
    Task<long> WriteTargetAsync(AggregateCommitAuthority authority, AggregateTargetWrite write, CancellationToken ct);
}
internal interface IAggregateTransactionStore
{
    Task<T> ExecuteAsync<T>(Func<IAggregateTransaction, CancellationToken, Task<T>> action, CancellationToken ct);
}
internal interface IAggregateCommandReader
{
    Task<AggregateCommitAuthority> AuthorizeStatusAsync(AggregatePeriodContextDto context, string actor, string sessionKey, CancellationToken ct)
        => AuthorizeAsync(context, actor, sessionKey, ct);
    Task ValidateSavedResultAsync(AggregateCommitAuthority authority, AggregatePreviewEnvelope applied, CancellationToken ct)
        => Task.CompletedTask;
    Task<AggregateCommitAuthority> AuthorizeAsync(AggregatePeriodContextDto context, string actor, string sessionKey, CancellationToken ct);
    Task<AggregatePreviewEnvelope> PreviewAsync(AggregateInstanceState instance, AggregateRecipeDto effectiveRecipe,
        AggregateCommitAuthority authority, CancellationToken ct);
    Task<string> InputStampAsync(AggregateInstanceState instance, AggregateRecipeDto recipe,
        AggregateCommitAuthority authority, CancellationToken ct)
        => throw new AggregatePreviewException("AGG_ASYNC_READER_REQUIRED");
}
