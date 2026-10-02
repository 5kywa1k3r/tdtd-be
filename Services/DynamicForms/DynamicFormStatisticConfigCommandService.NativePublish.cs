using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.DynamicForms;

public sealed partial class DynamicFormStatisticConfigCommandService
{
    // Called only after DynamicFormService has applied the common authoring and
    // visibility checks. Freeze schema + existing explicit config in one CAS;
    // this operation neither activates a catalog nor dispatches a statistic job.
    public async Task PublishNativeAsync(string id, int expectedRevision,
        DynamicFormPublishedSchemaSnapshot snapshot, CancellationToken ct)
    {
        var me = _me.RequireMe();
        id = NormalizeOwnerId(id);
        await _transactions.ExecuteAsync(async (session, token) =>
        {
            var owner = await _ctx.DynamicFormTemplates.Find(session, BuildOwnerFilter(id, me)).FirstOrDefaultAsync(token)
                ?? throw OwnerNotFound(id);
            if (!DynamicFormNativeTableDefinition.IsNative(owner)) throw IntegrityConflict(id, "NATIVE_OWNER_REQUIRED");
            if (owner.IsPublished)
            {
                _ = DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(owner);
                _ = ReadNativeStatisticInputView(owner, owner.StatisticConfigId!, owner.StatisticConfigVersionId!,
                    owner.StatisticConfigVersionNo, owner.StatisticConfigRevision, owner.StatisticConfigHash!);
                return true;
            }
            if (owner.Revision != expectedRevision) throw IntegrityConflict(id, "PUBLISH_REVISION_CHANGED");
            DynamicFormNativeTableDefinition.ValidateStored(owner);
            var actual = DynamicFormPublishedSchemaSnapshotBuilder.Build(owner);
            if (actual.Sha256 != snapshot.Sha256 || actual.Json != snapshot.Json)
                throw IntegrityConflict(id, "PUBLISH_SCHEMA_CHANGED");
            var state = await LoadStateAsync(session, owner, me, token);
            var tables = NativeTables(owner);
            var labelsNow = await BuildNativePlanSectionAsync(session, owner, tables, me, token);
            if (labelsNow != state.NativePlanSectionJson)
                throw IntegrityConflict(id, "PUBLISH_LABEL_PINS_CHANGED");
            state = state with { Status = StatConfigStatuses.Locked };
            var now = UtcNowAtMillisecondPrecision();
            if (state.IsVirtual)
            {
                // Publication is an explicit write: materialize the complete
                // already-validated state, without inventing a statistic method.
                owner.StatisticConfigId = state.ConfigId;
                owner.StatisticConfigVersionId = state.VersionId;
                owner.StatisticConfigPreviousVersionId = state.PreviousVersionId;
                owner.StatisticConfigVersionNo = state.VersionNo;
                owner.StatisticConfigRevision = state.Revision;
                owner.StatisticConfigHash = state.ConfigHash;
                owner.StatisticConfigDependencyPins = state.DependencyPins.ToList();
                owner.StatisticConfigSections = new DynamicFormStatisticConfigSections {
                    FieldSectionJson = state.FieldSectionJson, TableSectionJson = state.TableSectionJson,
                    NativeTargetSectionJson = state.NativeTargetSectionJson, NativePlanSectionJson = state.NativePlanSectionJson
                };
                owner.StatisticConfigSnapshots = [CreateSnapshot(state, now, me.Id)];
                owner.StatisticConfigUpdatedAtUtc = now;
                owner.StatisticConfigUpdatedByUserId = me.Id;
            }
            else
                owner.StatisticConfigSnapshots!.Single(s => s.VersionId == state.VersionId).Status = StatConfigStatuses.Locked;
            owner.StatisticConfigStatus = StatConfigStatuses.Locked;
            owner.IsPublished = true; owner.IsActive = true;
            owner.PublishedSchemaSnapshotJson = snapshot.Json; owner.PublishedSchemaHash = snapshot.Sha256;
            owner.PublishedAtUtc = now; owner.PublishedByUserId = me.Id;
            owner.UpdatedAtUtc = now; owner.UpdatedByUserId = me.Id; owner.Revision = checked(expectedRevision + 1);
            _ = ReadNativeStatisticInputView(owner, state.ConfigId, state.VersionId, state.VersionNo, state.Revision, state.ConfigHash);
            if (owner.ToBson().LongLength > 15L * 1024 * 1024)
                throw Schema("$.nativeStatistics", "NATIVE_PUBLISH_STORAGE_BUDGET_EXCEEDED");
            var changed = await _ctx.DynamicFormTemplates.ReplaceOneAsync(session,
                BuildOwnerFilter(id, me) & Builders<DynamicFormTemplate>.Filter.Eq(t => t.Revision, expectedRevision)
                    & Builders<DynamicFormTemplate>.Filter.Eq(t => t.IsPublished, false), owner, cancellationToken: token);
            if (changed.ModifiedCount != 1) throw IntegrityConflict(id, "PUBLISH_REVISION_CHANGED");
            return true;
        }, ct);
    }
}
