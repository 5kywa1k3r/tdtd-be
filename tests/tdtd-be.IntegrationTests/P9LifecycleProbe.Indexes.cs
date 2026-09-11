using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private static readonly IReadOnlyDictionary<string, string>
        LifecycleUniqueIndexes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["work_report_field_stat_values"] =
                "ux_workReportFieldStatValues_report_field_source_active_generation",
            ["work_report_field_stat_aggregates"] =
                "ux_workReportFieldStatAggregates_scope_field_bucket_period_status_active_generation",
            ["work_report_table_stat_values"] =
                "ux_workReportTableStatValues_report_metric_source_active_generation",
            ["work_report_table_stat_aggregates"] =
                "ux_workReportTableStatAggregates_scope_metric_period_status_active_generation",
            ["work_report_label_stat_values"] =
                "ux_workReportLabelStatValues_report_block_row_label_active_generation",
            ["work_report_label_stat_aggregates"] =
                "ux_workReportLabelStatAggregates_scope_label_period_status_active_generation"
        };

    private static readonly IReadOnlyDictionary<string, string[]>
        LifecycleUniqueKeyRequirements =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["work_report_field_stat_values"] =
                [
                    "directProjection.generationId", "workAssignmentReportId",
                    "directProjection.sourcePayloadRevision",
                    "directProjection.sourceLifecycleRevision", "periodInstanceKey",
                    "fieldId", "sourceKey", "bucketKey", "valueKind"
                ],
                ["work_report_table_stat_values"] =
                [
                    "directProjection.generationId", "workAssignmentReportId",
                    "directProjection.sourcePayloadRevision",
                    "directProjection.sourceLifecycleRevision", "periodInstanceKey",
                    "blockId", "metricKey", "sourceKey", "bucketKey"
                ],
                ["work_report_label_stat_values"] =
                [
                    "directProjection.generationId", "workAssignmentReportId",
                    "directProjection.sourcePayloadRevision",
                    "directProjection.sourceLifecycleRevision", "periodInstanceKey",
                    "blockId", "sheetId", "rowKey", "labelCode", "source"
                ],
                ["work_report_field_stat_aggregates"] =
                [
                    "directProjection.generationId", "directProjection.configVersionId",
                    "directProjection.configRevision", "directProjection.configHash",
                    "directProjection.sourceMembershipSignature", "workId", "scopeType",
                    "scopeId", "dynamicFormTemplateId", "fieldId", "bucketKey",
                    "periodInstanceKey", "reportStatus"
                ],
                ["work_report_table_stat_aggregates"] =
                [
                    "directProjection.generationId", "directProjection.configVersionId",
                    "directProjection.configRevision", "directProjection.configHash",
                    "directProjection.sourceMembershipSignature", "workId", "scopeType",
                    "scopeId", "dynamicFormTemplateId", "dynamicExcelTemplateId", "blockId",
                    "tableMode", "metricKey", "dataType", "bucketKey", "periodInstanceKey",
                    "reportStatus"
                ],
                ["work_report_label_stat_aggregates"] =
                [
                    "directProjection.generationId", "directProjection.configVersionId",
                    "directProjection.configRevision", "directProjection.configHash",
                    "directProjection.sourceMembershipSignature", "workId", "scopeType",
                    "scopeId", "dynamicFormTemplateId", "dynamicExcelTemplateId", "blockId",
                    "labelCode", "periodInstanceKey", "reportStatus"
                ]
            };

    private static readonly IReadOnlyDictionary<string, string>
        LifecycleExplainIndexes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["work_report_field_stat_values"] =
                "ix_workReportFieldStatValues_work_period_field_status_generation",
            ["work_report_field_stat_aggregates"] =
                "ix_workReportFieldStatAggregates_field_read_generation",
            ["work_report_table_stat_values"] =
                "ix_workReportTableStatValues_work_period_metric_status_generation",
            ["work_report_table_stat_aggregates"] =
                "ix_workReportTableStatAggregates_metric_read_generation",
            ["work_report_label_stat_values"] =
                "ix_workReportLabelStatValues_work_period_label_status_generation",
            ["work_report_label_stat_aggregates"] =
                "ix_workReportLabelStatAggregates_label_read_generation"
        };

    private async Task CaptureLifecycleOwnerIndexEvidenceAsync(
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_lfcGenerationId))
            return;

        var database = RequireDatabase();
        var ownerNames = LifecycleDirectCollections
            .Append(LifecycleJobCollection)
            .Append("work_assignments")
            .Append("work_report_periods")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var owners = new List<P9LifecycleIndexOwnerEvidence>();
        foreach (var name in ownerNames)
        {
            var indexes = await LoadIndexInventoryAsync(name, ct);
            owners.Add(new P9LifecycleIndexOwnerEvidence(name, indexes));
        }

        var requiredVerified = true;
        foreach (var pair in LifecycleUniqueIndexes)
        {
            var index = RequiredIndex(owners, pair.Key, pair.Value);
            requiredVerified &= index.Unique;
            requiredVerified &= index.KeyNames.FirstOrDefault() ==
                                "directProjection.generationId";
            requiredVerified &= index.KeyNames.SequenceEqual(
                LifecycleUniqueKeyRequirements[pair.Key],
                StringComparer.Ordinal);
            requiredVerified &= index.PartialFilterJson.Contains(
                "directProjection.generationId",
                StringComparison.Ordinal);
        }

        var lifecycleJobUnique = RequiredIndex(
            owners,
            LifecycleJobCollection,
            "ux_workReportStatisticRebuildJobs_source_lifecycle_event");
        requiredVerified &= lifecycleJobUnique.Unique &&
                            lifecycleJobUnique.KeyNames.SequenceEqual(
                                ["sourceReportId", "sourceLifecycleEventKey", "directProjectionIdentityKey"],
                                StringComparer.Ordinal);
        var currentPublicationUnique = RequiredIndex(
            owners,
            LifecycleJobCollection,
            "ux_workReportStatisticRebuildJobs_current_direct_publication");
        requiredVerified &= currentPublicationUnique.Unique &&
                            currentPublicationUnique.KeyNames.SequenceEqual(
                                [
                                    "runKind",
                                    "workId",
                                    "periodInstanceKey",
                                    "periodKind",
                                    "dynamicFormFamilyId",
                                    "dynamicFormTemplateId",
                                    "dynamicFormVersionNo",
                                    "dynamicFormSchemaHash",
                                    "candidateChainId",
                                    "candidatePromptId",
                                    "isCurrentPublication"
                                ],
                                StringComparer.Ordinal) &&
                            currentPublicationUnique.PartialFilterJson.Contains(
                                "LIFECYCLE_DIRECT_PROJECTION",
                                StringComparison.Ordinal) &&
                            currentPublicationUnique.PartialFilterJson.Contains(
                                "isCurrentPublication",
                                StringComparison.Ordinal);
        requiredVerified &= HasIndex(
            owners,
            "work_assignments",
            "ix_workAssignments_flowParticipant_createdBy_access") &&
                            HasIndex(
                                owners,
                                "work_assignments",
                                "ix_workAssignments_flowParticipant_watcher_access") &&
                            HasIndex(
                                owners,
                                "work_assignments",
                                "ix_workAssignments_flowParticipant_assignee_access") &&
                            HasIndex(
                                owners,
                                "work_report_periods",
                                "ix_work_report_period_lifecycle_source") &&
                            HasIndex(
                                owners,
                                "work_report_label_stat_values",
                                "ix_workReportLabelStatValues_flow_period_label_generation");

        var explains = new List<P9LifecycleExplainEvidence>();
        foreach (var pair in LifecycleExplainIndexes)
        {
            var explain = await ExplainGenerationQueryAsync(
                pair.Key,
                pair.Value,
                ct);
            explains.Add(explain);
        }

        var ownerClasses = new[]
        {
            "WorkReportFieldStatValue",
            "WorkReportFieldStatAggregate",
            "WorkReportTableStatValue",
            "WorkReportTableStatAggregate",
            "WorkReportLabelStatValue",
            "WorkReportLabelStatAggregate"
        };
        var queryIndexesVerified = LifecycleExplainIndexes.All(pair =>
            HasIndex(owners, pair.Key, pair.Value));
        var explainsVerified = explains.All(item => item.Passed);
        var allSixOwnersPinned = LifecycleDirectCollections.All(name =>
            _lfcPublishedDirect is not null &&
            _lfcPublishedDirect.TryGetValue(name, out var state) &&
            state.Count > 0);
        var job = await LoadLifecycleJobAsync(ct);
        var work = await database.GetCollection<BsonDocument>("works")
            .Find(new BsonDocument("_id", ObjectId.Parse(Fixture().WorkId)))
            .SingleAsync(ct);
        var publicationScopeKey = BsonString(job, "publicationScopeKey");
        var currentPublicationCount = await database
            .GetCollection<BsonDocument>(LifecycleJobCollection)
            .CountDocumentsAsync(
                new BsonDocument
                {
                    ["runKind"] = "LIFECYCLE_DIRECT_PROJECTION",
                    ["publicationScopeKey"] = publicationScopeKey,
                    ["isCurrentPublication"] = true,
                    ["isDeleted"] = false
                },
                cancellationToken: ct);
        var jobSourceRevision = BsonLong(job, "directSourceRevision");
        var workSourceRevision = BsonLong(work, "directSourceRevision");
        var jobPublicationRevision = BsonLong(job, "directPublicationRevision");
        var workPublicationRevision = BsonLong(work, "directPublicationRevision");
        var sourceRevisionFenceVerified =
            jobSourceRevision > 0 &&
            jobSourceRevision <= workSourceRevision &&
            jobPublicationRevision > 0 &&
            jobPublicationRevision == workPublicationRevision;
        var legacyIsolationVerified = await AssertLegacyGenerationIsolationAsync(ct);
        var currentPublicationVerified =
            BsonBool(job, "isCurrentPublication") &&
            string.Equals(
                BsonString(job, "status"),
                "COMPLETED",
                StringComparison.Ordinal) &&
            currentPublicationCount == 1;
        var generationAtomicityVerified =
            _lfcPublishedDirect is not null &&
            _lfcPublishedDirect[LifecycleJobCollection].Count == 1 &&
            currentPublicationVerified &&
            sourceRevisionFenceVerified &&
            legacyIsolationVerified &&
            IsCanonicalSha(_lfcGenerationId) &&
            IsCanonicalSha(_lfcGenerationHash);
        var ownerPassed = requiredVerified &&
                          queryIndexesVerified &&
                          explainsVerified &&
                          allSixOwnersPinned &&
                          generationAtomicityVerified;
        _lfcOwnerIndexEvidence = new P9LifecycleOwnerIndexEvidence(
            "P9_LFC_OWNER_INDEX_EVIDENCE_V1",
            ChainId,
            LifecyclePromptId,
            LifecycleGroupId,
            _runKey,
            _lfcGenerationId,
            ownerClasses,
            owners,
            explains,
            allSixOwnersPinned,
            requiredVerified,
            queryIndexesVerified,
            explainsVerified,
            generationAtomicityVerified,
            currentPublicationVerified,
            sourceRevisionFenceVerified,
            legacyIsolationVerified,
            ownerPassed,
            DateTime.UtcNow);
    }

    private async Task<bool> AssertLegacyGenerationIsolationAsync(
        CancellationToken ct)
    {
        var database = RequireDatabase();
        foreach (var collection in LifecycleDirectCollections)
        {
            var owner = database.GetCollection<BsonDocument>(collection);
            var generationFilter = new BsonDocument(
                "directProjection.generationId",
                _lfcGenerationId);
            var legacyFilter = new BsonDocument(
                "directProjection",
                BsonNull.Value);
            var generationRows = await owner.CountDocumentsAsync(
                generationFilter,
                cancellationToken: ct);
            var generationRowsVisibleToLegacy = await owner.CountDocumentsAsync(
                new BsonDocument("$and", new BsonArray
                {
                    generationFilter,
                    legacyFilter
                }),
                cancellationToken: ct);
            HarnessAssert.True(
                generationRows > 0,
                $"Generation isolation found no staged rows in {collection}");
            HarnessAssert.Equal(
                0L,
                generationRowsVisibleToLegacy,
                $"Legacy Direct slice admitted staged generation rows in {collection}");
        }

        return true;
    }

    private async Task<IReadOnlyList<P9LifecycleIndexEvidence>>
        LoadIndexInventoryAsync(string collection, CancellationToken ct)
    {
        using var cursor = await RequireDatabase()
            .GetCollection<BsonDocument>(collection)
            .Indexes.ListAsync(ct);
        var documents = await cursor.ToListAsync(ct);
        return documents
            .Select(document =>
            {
                var keys = document.GetValue("key", new BsonDocument()).AsBsonDocument;
                return new P9LifecycleIndexEvidence(
                    BsonString(document, "name"),
                    document.GetValue("unique", false).ToBoolean(),
                    keys.Names.ToArray(),
                    keys.ToJson(),
                    document.GetValue(
                        "partialFilterExpression",
                        new BsonDocument()).ToJson());
            })
            .OrderBy(index => index.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<P9LifecycleExplainEvidence>
        ExplainGenerationQueryAsync(
            string collection,
            string hint,
            CancellationToken ct)
    {
        var filter = new BsonDocument
        {
            ["directProjection.generationId"] = _lfcGenerationId,
            ["workId"] = ObjectId.Parse(Fixture().WorkId),
            ["periodInstanceKey"] = Fixture().PeriodInstanceKey
        };
        var command = new BsonDocument
        {
            ["explain"] = new BsonDocument
            {
                ["find"] = collection,
                ["filter"] = filter,
                ["hint"] = hint,
                ["limit"] = 200
            },
            ["verbosity"] = "executionStats"
        };
        var result = await RequireDatabase().RunCommandAsync<BsonDocument>(
            command,
            cancellationToken: ct);
        var execution = result.GetValue(
            "executionStats",
            new BsonDocument()).AsBsonDocument;
        var returned = execution.GetValue("nReturned", 0).ToInt64();
        var examined = execution.GetValue("totalDocsExamined", 0).ToInt64();
        var budget = Math.Max(100L, 5L * returned);
        var winning = result.GetValue(
                "queryPlanner",
                new BsonDocument()).AsBsonDocument
            .GetValue("winningPlan", new BsonDocument()).AsBsonDocument;
        var stages = FindBsonStrings(winning, "stage").ToArray();
        var indexes = FindBsonStrings(winning, "indexName").ToArray();
        var passed = indexes.Contains(hint, StringComparer.Ordinal) &&
                     stages.Contains("IXSCAN", StringComparer.Ordinal) &&
                     !stages.Contains("COLLSCAN", StringComparer.Ordinal) &&
                     !stages.Contains("SORT", StringComparer.Ordinal) &&
                     examined <= budget;
        return new P9LifecycleExplainEvidence(
            collection,
            hint,
            returned,
            examined,
            budget,
            stages,
            indexes,
            passed,
            winning.ToJson());
    }

    private static IEnumerable<string> FindBsonStrings(
        BsonValue value,
        string field)
    {
        if (value.IsBsonDocument)
        {
            foreach (var element in value.AsBsonDocument.Elements)
            {
                if (string.Equals(element.Name, field, StringComparison.Ordinal) &&
                    element.Value.IsString)
                {
                    yield return element.Value.AsString;
                }
                foreach (var nested in FindBsonStrings(element.Value, field))
                    yield return nested;
            }
        }
        else if (value.IsBsonArray)
        {
            foreach (var item in value.AsBsonArray)
            {
                foreach (var nested in FindBsonStrings(item, field))
                    yield return nested;
            }
        }
    }

    private static P9LifecycleIndexEvidence RequiredIndex(
        IReadOnlyCollection<P9LifecycleIndexOwnerEvidence> owners,
        string owner,
        string index)
        => owners.Single(item => item.Collection == owner).Indexes
               .SingleOrDefault(item => item.Name == index)
           ?? throw new InvalidOperationException(
               $"Required index '{index}' is absent from '{owner}'.");

    private static bool HasIndex(
        IReadOnlyCollection<P9LifecycleIndexOwnerEvidence> owners,
        string owner,
        string index)
        => owners.Single(item => item.Collection == owner).Indexes
            .Any(item => item.Name == index);
}

internal sealed record P9LifecycleOwnerIndexEvidence(
    string SchemaVersion,
    string ChainId,
    string PromptId,
    string GroupId,
    string RunKey,
    string GenerationId,
    IReadOnlyList<string> Owners,
    IReadOnlyList<P9LifecycleIndexOwnerEvidence> Inventory,
    IReadOnlyList<P9LifecycleExplainEvidence> Explains,
    bool AllSixOwnersPinned,
    bool UniqueIndexesVerified,
    bool QueryIndexesVerified,
    bool ExplainIndexesVerified,
    bool GenerationAtomicityVerified,
    bool CurrentPublicationVerified,
    bool SourceRevisionFenceVerified,
    bool LegacyIsolationVerified,
    bool Passed,
    DateTime CapturedAtUtc);

internal sealed record P9LifecycleIndexOwnerEvidence(
    string Collection,
    IReadOnlyList<P9LifecycleIndexEvidence> Indexes);

internal sealed record P9LifecycleIndexEvidence(
    string Name,
    bool Unique,
    IReadOnlyList<string> KeyNames,
    string KeyJson,
    string PartialFilterJson);

internal sealed record P9LifecycleExplainEvidence(
    string Collection,
    string Hint,
    long ReturnedRows,
    long ExaminedDocuments,
    long ExaminedBudget,
    IReadOnlyList<string> Stages,
    IReadOnlyList<string> IndexNames,
    bool Passed,
    string WinningPlanJson);
