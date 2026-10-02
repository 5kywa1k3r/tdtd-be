using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Services.StatisticsRun;

public sealed partial class StatRunDirectProjectionService
{
    private readonly IWorkReportPayloadReader? _nativePayloadReader;
    private sealed record NativeProjectionStage(WorkReportNativeStatisticPublication Publication,
        NativeStatisticGenerationArtifact Artifact, DynamicFormTemplate Template,
        IReadOnlyList<DirectMember> Members, string ActorUserId, string ActorUnitId);

    private static bool ValidateProjectionStatisticConfig(DynamicFormTemplate template)
    {
        if (template.NativeTablesVersion is not null)
        {
            // This owner consumes both sections. Legacy P804 callers still fail closed.
            _ = DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(template,
                template.StatisticConfigId!, template.StatisticConfigVersionId!, template.StatisticConfigVersionNo,
                template.StatisticConfigRevision, template.StatisticConfigHash!);
            return true;
        }
        var trusted = DynamicFormStatisticConfigCommandService.GetP804TrustedPersistedView(template);
        if (trusted is null) return false;
        ValidateLockedStatisticConfig(template, trusted);
        return true;
    }

    private static string BuildProjectionMembershipSignature(IReadOnlyCollection<DirectMember> members,
        DynamicFormTemplate template)
    {
        var legacy = BuildMembershipSignature(members);
        return template.NativeTablesVersion is null ? legacy : StatRunCanonicalJson.HashObject(new
        {
            version = "P9_DIRECT_NATIVE_MEMBERSHIP_V1", legacy,
            sourceOrder = WorkReportNativeSourcePin.Digest(members.Select(m => new WorkReportNativeSourcePin(
                m.Report.Id, m.Report.PayloadRevision, m.Report.PayloadHash!, m.Report.PayloadUpdatedAtUtc)))
        });
    }

    private async Task<NativeStatisticStageInputs> ReloadNativeProjectionInputsAsync(DirectSource expected,
        DynamicFormTemplate expectedTemplate, StatRunCandidateBinding binding, WorkReportDirectGenerationContext context,
        string actorUserId, bool expectSourceEffective, CancellationToken ct)
    {
        await RevalidateBeforePublishAsync(expected, expectedTemplate, binding, context,
            context.SourceMembershipSignature, expectSourceEffective, ct);
        var currentSource = await LoadSourceAsync(expected.Report.Id, expected.Entry.EntryKey, ct);
        _ = await ValidateTenantScopeAsync(currentSource, actorUserId, ct);
        var template = await LoadLockedTemplateAsync(currentSource.Report, currentSource.Assignment, ct);
        var members = await ResolveMembershipAsync(expected.Report.WorkId, expected.Report.PeriodInstanceKey, template, ct);
        if (BuildProjectionMembershipSignature(members, template) != context.SourceMembershipSignature)
            throw Fail("MEMBERSHIP_STALE");
        if (members.Count > NativeStatisticPublicationContract.CalculationLimits.MaxReports)
            throw Fail("NATIVE_SOURCE_LIMIT");
        var reader = _nativePayloadReader ?? throw Fail("NATIVE_PAYLOAD_READER_REQUIRED");
        var sources = new List<(WorkAssignmentReport, WorkReportPayloadSnapshot)>(members.Count);
        long payloadBytes = 0;
        foreach (var member in members)
        {
            // Reject unsupported native Flow/policy before loading full payloads.
            NativeStatisticGenerationStage.RequireContribution(context.SourceContributionBindings[member.Report.Id],
                member.Report.CumulativeContributionPolicyJson);
            if (member.Report.PayloadSizeBytes < 0
                || member.Report.PayloadSizeBytes > NativeStatisticPublicationContract.MaxSourcePayloadBytes - payloadBytes)
                throw Fail("NATIVE_SOURCE_BYTES_LIMIT");
            var payload = await reader.LoadReportPayloadAsync(member.Report, ct);
            payloadBytes += new[] { payload.Values1DJson, payload.FieldValuesJson, payload.TableValuesJson, payload.SummarySourceJson }
                .Sum(value => (long)System.Text.Encoding.UTF8.GetByteCount(value ?? string.Empty));
            if (payloadBytes > NativeStatisticPublicationContract.MaxSourcePayloadBytes)
                throw Fail("NATIVE_SOURCE_BYTES_LIMIT");
            sources.Add((member.Report, payload));
        }
        return new(template, sources);
    }

    private async Task<NativeProjectionStage> StageNativeProjectionAsync(DirectSource source,
        DynamicFormTemplate template, StatRunCandidateBinding binding, WorkReportDirectGenerationContext context,
        string actorUserId, bool expectSourceEffective, CancellationToken ct)
    {
        var store = new NativeStatisticArtifactStore(_ctx.Db);
        var receipt = await NativeStatisticGenerationStage.StageAndVerifyAsync(store,
            source.Report.WorkId, source.Report.PeriodInstanceKey, context,
            NativeStatisticPublicationContract.CalculationLimits, NativeStatisticPublicationContract.StorageLimits,
            token => ReloadNativeProjectionInputsAsync(source, template, binding, context, actorUserId, expectSourceEffective, token), ct);
        var artifact = await store.ReadAsync(receipt, NativeStatisticPublicationContract.StorageLimits, ct);
        var current = await ReloadNativeProjectionInputsAsync(source, template, binding, context, actorUserId, expectSourceEffective, ct);
        NativeStatisticGenerationStage.Revalidate(artifact, current.Template, current.AuthorizedSources, ct);
        var members = await ResolveMembershipAsync(source.Report.WorkId, source.Report.PeriodInstanceKey, current.Template, ct);
        if (BuildProjectionMembershipSignature(members, current.Template) != context.SourceMembershipSignature)
            throw Fail("MEMBERSHIP_STALE");
        var actorUnitId = await ValidateTenantScopeAsync(source, actorUserId, ct);
        return new(NativeStatisticPublicationContract.Create(receipt, artifact), artifact, current.Template,
            members, actorUserId, actorUnitId);
    }

    private async Task FenceNativeProjectionAsync(IClientSessionHandle session, NativeProjectionStage stage, CancellationToken ct)
    {
        var binding = _activation.RequireFoundation(StatRunCapabilities.DirectFieldTableLabel, StatRunRouteRegistry.LifecycleDirectProjector);
        if (!NativeBindingMatches(stage.Artifact.Generation, binding)) throw Fail("CANDIDATE_PIN_STALE");
        var receipt = stage.Publication;
        _ = await new NativeStatisticArtifactStore(_ctx.Db).ReadAsync(new(stage.Artifact.Generation.RunId,
            stage.Artifact.Generation.GenerationId, receipt.ArtifactHash, receipt.ManifestHash, receipt.Bytes, receipt.ChunkCount),
            NativeStatisticPublicationContract.StorageLimits, ct);
        // These writes live in the SAME transaction as lease CAS + work source fence
        // + supersession + COMPLETED. Concurrent definition, source or ACL writes
        // conflict with the fence; an expired lease rolls every fence back.
        await FenceDocumentAsync(session, _ctx.DynamicFormTemplates, stage.Template,
            ["_id", "isDeleted", "isActive", "isPublished", "familyId", "versionNo", "publishedSchemaHash",
             "publishedSchemaSnapshotJson", "nativeTablesVersion", "tablesJson", "sectionsJson", "fieldsJson", "blocksJson",
             "statisticConfigId", "statisticConfigVersionId", "statisticConfigVersionNo", "statisticConfigRevision",
             "statisticConfigHash", "statisticConfigStatus", "statisticConfigSnapshots", "statisticConfigSections",
             "statisticConfigDependencyPins", "statisticConfigPreviousVersionId"], ct);
        foreach (var member in stage.Members)
        {
            await FenceDocumentAsync(session, _ctx.WorkAssignmentReports, member.Report,
                ["_id", "isDeleted", "isCurrent", "isActive", "status", "invalidatedByFlowEventId", "workId",
                 "workAssignmentId", "workReportPeriodId", "payloadRevision", "payloadHash", "payloadStatus", "payloadUpdatedAtUtc",
                 "lifecycleRevision", "cumulativeContributionMode", "cumulativeContributionPolicyJson",
                 "dynamicFormTemplateId", "dynamicFormFamilyId", "dynamicFormVersionNo", "dynamicFormSchemaHash",
                 "periodKey", "periodInstanceKey", "periodKind", "periodStart", "periodEnd", "assigneeUserId", "approvedByUserId",
                 "lastLifecycleCommandId", "lastLifecycleCommandHash", "lastLifecycleCommandOperation", "lastLifecycleCommandRevision",
                 "lastLifecycleCommandPayloadRevision", "lastLifecycleCommandStatus", "lastLifecycleCommandIsActive"], ct);
            await FenceDocumentAsync(session, _ctx.WorkAssignments, member.Assignment,
                ["_id", "isDeleted", "isActive", "invalidatedByFlowEventId", "workId", "createdByUserId", "issuedByUnitId",
                 "targetUnitIds", "assignees", "rootAssignmentId", "flowInstanceId", "dynamicFormTemplateId",
                 "dynamicFormFamilyId", "dynamicFormVersionNo", "dynamicFormSchemaHash"], ct);
            await FenceDocumentAsync(session, _ctx.WorkReportPeriods, member.Period,
                ["_id", "isDeleted", "isActive", "status", "currentReportId", "sourceLifecycleReportId",
                 "sourceLifecycleRevision", "sourceLifecycleAppliedAtUtc", "assigneeUserId", "assigneeUnitId",
                 "workId", "workAssignmentId", "periodKey", "periodInstanceKey", "periodKind", "periodStart", "periodEnd"], ct);
        }
        foreach (var (id, unit) in stage.Members.Select(m => (m.Tenant.ApprovalActorUserId, m.Tenant.ApprovalActorUnitId))
                     .Append((stage.ActorUserId, stage.ActorUnitId)).Distinct())
        {
            var actorFilter = new BsonDocument { { "_id", ObjectId.Parse(id) },
                { "isDeleted", false }, { "unitId", ObjectId.Parse(unit) } };
            var actor = await _ctx.Users.UpdateOneAsync(session, actorFilter,
                Builders<AppUser>.Update.Inc("nativeStatisticPublicationFence", 1L), cancellationToken: ct);
            if (actor.MatchedCount != 1) throw Fail("NATIVE_ACTOR_FENCE_STALE");
        }
    }

    internal static async Task FenceDocumentAsync<T>(IClientSessionHandle session, IMongoCollection<T> collection,
        T expected, string[] fields, CancellationToken ct)
    {
        var bson = expected.ToBsonDocument();
        var filter = new BsonDocument(fields.Select(field => new BsonElement(field, bson.GetValue(field, BsonNull.Value))));
        var result = await collection.UpdateOneAsync(session, filter,
            Builders<T>.Update.Inc("nativeStatisticPublicationFence", 1L), cancellationToken: ct);
        if (result.MatchedCount != 1) throw Fail("NATIVE_PUBLICATION_SOURCE_FENCE_STALE");
    }

    // Result reader uses the same six-store + native + ledger/state verification
    // as replay. Calling this never claims that the publication is fresh/current.
    internal Task ValidateNativePublicationAsync(WorkReportStatisticRebuildJob job, CancellationToken ct)
        => ValidateCompletedPublicationAsync(job, requireCurrent: false, ct);

    private static bool NativeBindingMatches(WorkReportDirectGenerationContext pin, StatRunCandidateBinding binding)
        => binding.ChainId == pin.CandidateChainId && binding.CatalogVersion == pin.CatalogVersion
           && binding.CatalogRawSha256 == pin.CatalogRawSha256 && binding.CatalogSemanticSha256 == pin.CatalogSemanticSha256
           && binding.SchemaRawSha256 == pin.SchemaRawSha256 && binding.SchemaSemanticSha256 == pin.SchemaSemanticSha256
           && binding.StageLockSha256 == pin.StageLockSha256;

    internal async Task<bool> NativeCurrentPinsMatchAsync(WorkReportStatisticRebuildJob job, CancellationToken ct)
    {
        var binding = _activation.RequireFoundation(StatRunCapabilities.DirectFieldTableLabel, StatRunRouteRegistry.LifecycleDirectProjector);
        if (binding.ChainId != job.CandidateChainId || binding.CatalogVersion != job.CatalogVersion
            || binding.CatalogRawSha256 != job.CatalogRawSha256 || binding.CatalogSemanticSha256 != job.CatalogSemanticSha256
            || binding.SchemaRawSha256 != job.SchemaRawSha256 || binding.SchemaSemanticSha256 != job.SchemaSemanticSha256
            || binding.StageLockSha256 != job.StageLockSha256) return false;
        var template = await _ctx.DynamicFormTemplates.Find(t => t.Id == job.DynamicFormTemplateId && !t.IsDeleted && t.IsActive && t.IsPublished)
            .SingleOrDefaultAsync(ct);
        if (template is null || template.PublishedSchemaHash != job.DynamicFormSchemaHash
            || template.StatisticConfigId != job.ConfigId || template.StatisticConfigVersionId != job.ConfigVersionId
            || template.StatisticConfigVersionNo != job.ConfigVersionNo || template.StatisticConfigRevision != job.ConfigRevision
            || template.StatisticConfigHash != job.ConfigHash) return false;
        if (!ValidateProjectionStatisticConfig(template)) return false;
        var members = await ResolveMembershipAsync(job.WorkId, job.PeriodInstanceKey!, template, ct);
        return BuildProjectionMembershipSignature(members, template) == job.SourceMembershipSignature;
    }
}
