using System.Text.RegularExpressions;
using System.Reflection;
using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.Services;

internal static class DynamicFormCoreMutationContractTests
{
    public static void Run()
    {
        DeleteRequiresClientRevisionThroughControllerAndCas();
        SearchTermsAreLiteralAndBounded();
        DeleteGuardsDraftDynamicFlowReferencesAndCatalogsConflict();
        DefaultWrapReuseIsProtectedByAUniquePartialIndex();
        PublishCasReloadIsIdempotentForTheWinningPublishedVersion();
        ForbiddenResponsesUseMetadataFreeDetails();
        StatisticUpdateCannotRenormalizeFormStructure();
        PublishedConsumersValidateSnapshotAgainstLiveStructure();
        FlowDraftWritersRequirePublishedFormVersions();
        PublishAndImportRevalidateEnumCatalogs();
        TypedSchemaKindsFailBeforeAllMutationWrites();
        MalformedObjectIdsFailBeforeMongoSerialization();
    }

    private static void DeleteRequiresClientRevisionThroughControllerAndCas()
    {
        var controller = ReadBackendSource("Controllers/DynamicFormController.cs");
        var service = ReadBackendSource("Services/DynamicFormService.cs");
        var deleteBody = Slice(
            service,
            "public async Task DeleteAsync(string id, int? expectedRevision, CancellationToken ct)",
            "private async Task<DynamicFormTemplate> LoadAsync");

        AssertRegex(
            service,
            @"Task\s+DeleteAsync\s*\(\s*string\s+id\s*,\s*int\?\s+expectedRevision\s*,\s*CancellationToken\s+ct\s*\)\s*;",
            "service interface must expose nullable client expectedRevision");
        AssertRegex(
            controller,
            @"\[FromQuery\]\s*int\?\s+expectedRevision",
            "DELETE controller must bind expectedRevision from the query string");
        AssertContains(
            controller,
            "_svc.DeleteAsync(id, expectedRevision, ct)",
            "DELETE controller must forward expectedRevision unchanged");
        AssertContains(
            deleteBody,
            "RequireExpectedRevision(expectedRevision, doc)",
            "DELETE service must reject missing and stale revisions before mutation");
        AssertContains(
            deleteBody,
            ".Set(x => x.Revision, requiredRevision + 1)",
            "DELETE soft-delete must advance the accepted revision");
        AssertContains(
            deleteBody,
            "BuildRevisionFilter(id, requiredRevision, requireDraft: true)",
            "DELETE write must remain a draft-only compare-and-set");
        AssertNotContains(
            deleteBody,
            "EffectiveRevision(doc)",
            "DELETE must never synthesize expectedRevision from the server document");
        AssertBefore(
            deleteBody,
            "RequireExpectedRevision(expectedRevision, doc)",
            "EnsureNotLinkedToRuntimeAsync(id, ct)",
            "DELETE must validate client revision before performing reference checks");
    }

    private static void SearchTermsAreLiteralAndBounded()
    {
        var service = ReadBackendSource("Services/DynamicFormService.cs");
        var searchBody = Slice(
            service,
            "public async Task<PagedResult<DynamicFormRow>> SearchAsync",
            "public async Task<DynamicFormDetail> GetByIdAsync");
        var regexBuilder = Slice(
            service,
            "private static BsonRegularExpression BuildLiteralSearchRegex",
            "private async Task InsertDynamicFormAsync");
        var sortBuilder = Slice(
            service,
            "private static SortDefinition<DynamicFormTemplate> BuildSort",
            "private static string NormalizeName");
        var maxMatch = Regex.Match(
            service,
            @"private const int MaxSearchTermLength\s*=\s*(?<value>\d+)\s*;");
        var maxOffsetMatch = Regex.Match(
            service,
            @"private const int MaxSearchOffset\s*=\s*(?<value>[\d_]+)\s*;");

        AssertTrue(maxMatch.Success, "Dynamic Form search must declare a maximum term length");
        var maxLength = int.Parse(maxMatch.Groups["value"].Value);
        AssertTrue(
            maxLength is > 0 and <= 512,
            $"Dynamic Form search limit must be bounded; observed {maxLength}");

        foreach (var field in new[] { "Code", "Name", "CreatedBy", "Q" })
        {
            AssertContains(
                searchBody,
                $"BuildLiteralSearchRegex(req.{field}",
                $"search field {field} must use the common literal builder");
        }

        AssertNotContains(
            searchBody,
            "new BsonRegularExpression(req.",
            "search requests must not reach Mongo as raw regular expressions");
        AssertContains(
            regexBuilder,
            "Regex.Escape(normalized)",
            "search terms must be escaped before creating a Mongo regex");
        AssertContains(
            regexBuilder,
            "normalized.Length > MaxSearchTermLength",
            "search terms must be rejected above the declared maximum");
        AssertContains(
            regexBuilder,
            "DYNAMIC_FORM_SEARCH_TERM_TOO_LONG",
            "bounded search rejection must keep its stable diagnostic reason");

        AssertTrue(maxOffsetMatch.Success, "Dynamic Form search must declare a maximum offset");
        var maxOffset = int.Parse(maxOffsetMatch.Groups["value"].Value.Replace("_", string.Empty));
        AssertTrue(
            maxOffset is > 0 and <= 1_000_000,
            $"Dynamic Form search offset must be bounded; observed {maxOffset}");
        AssertContains(
            searchBody,
            "var searchOffset = (long)page * pageSize",
            "search offset arithmetic must widen before multiplication");
        AssertContains(
            searchBody,
            "searchOffset > MaxSearchOffset",
            "deep-page search must fail before reaching Mongo");
        AssertContains(
            searchBody,
            "DYNAMIC_FORM_SEARCH_OFFSET_TOO_LARGE",
            "deep-page rejection must keep its stable diagnostic reason");
        AssertContains(
            searchBody,
            ".Skip((int)searchOffset)",
            "Mongo skip must use the already-validated widened offset");
        AssertBefore(
            searchBody,
            "searchOffset > MaxSearchOffset",
            "CountDocumentsAsync",
            "invalid deep pages must be rejected before any search query");

        AssertContains(
            sortBuilder,
            "if (!string.Equals(sortField, \"createdAtUtc\"",
            "createdAt must not be appended twice when it is already the primary sort");
        AssertContains(
            sortBuilder,
            "Sort.Descending(\"_id\")",
            "every search sort must end with a deterministic Mongo _id tie-breaker");
    }

    private static void DeleteGuardsDraftDynamicFlowReferencesAndCatalogsConflict()
    {
        var service = ReadBackendSource("Services/DynamicFormService.cs");
        var guard = Slice(
            service,
            "private async Task EnsureNotLinkedToRuntimeAsync",
            "private async Task<bool> ContainsDynamicFormTemplateIdAsync");
        var flowLookup = Slice(
            service,
            "private async Task<bool> ContainsDynamicFlowReferenceAsync",
            "private static FilterDefinition<BsonDocument> BuildObjectIdOrStringFilter");

        AssertContains(
            guard,
            "ContainsDynamicFlowReferenceAsync(templateId, ct)",
            "delete reference guard must query Dynamic Flow design-time documents");
        AssertContains(
            guard,
            "AppErrorCode.DYNAMIC_FORM_IN_USE_BY_FLOW",
            "Dynamic Flow references must fail with a stable conflict code");
        AssertContains(
            flowLookup,
            "_ctx.DynamicFlowTemplates.CollectionNamespace.CollectionName",
            "delete guard must search Dynamic Flow templates");
        AssertContains(
            flowLookup,
            "_ctx.DynamicFlowTemplateVersions.CollectionNamespace.CollectionName",
            "delete guard must search Dynamic Flow version snapshots");
        foreach (var field in new[]
                 {
                     "rootDynamicFormTemplateId",
                     "dynamicFormTemplateId",
                     "formTemplateId",
                     "sourceDynamicFormTemplateId",
                     "sourceFormTemplateId",
                     "targetDynamicFormTemplateId",
                     "targetFormTemplateId"
                 })
        {
            AssertContains(
                flowLookup,
                field,
                $"delete guard must recognize Dynamic Flow reference field {field}");
        }

        AssertContains(
            flowLookup,
            "Regex.Escape(templateId)",
            "payload lookup must quote the exact Dynamic Form id");
        AssertNotContains(
            flowLookup,
            "isPublished",
            "draft Dynamic Flow references must block form deletion too");

        AssertConflictDescriptor(AppErrorCode.DYNAMIC_FORM_IN_USE_BY_FLOW);
        AssertConflictDescriptor(AppErrorCode.DYNAMIC_FORM_PUBLISHED_SCHEMA_INTEGRITY_FAILED);
    }

    private static void DefaultWrapReuseIsProtectedByAUniquePartialIndex()
    {
        var model = ReadBackendSource("Models/DynamicForms/DynamicFormTemplate.cs");
        var service = ReadBackendSource("Services/DynamicFormService.cs");
        var indexes = ReadBackendSource("Data/Indexes/MongoIndexInitializer.cs");
        var wrapBody = Slice(
            service,
            "public async Task<DynamicFormDetail> WrapDynamicExcelAsync",
            "public async Task<DynamicFormDetail> ImportDynamicExcelBlockAsync");
        var updateBody = Slice(
            service,
            "public async Task<DynamicFormDetail> UpdateAsync",
            "public Task<DynamicFormStatisticConfigResult> GetStatisticsAsync");
        var indexBlock = Slice(
            indexes,
            "name: \"ux_dynamicForms_default_wrap_reuse\"",
            "name: \"ix_dynamicForms_createdAt_desc\"");

        AssertRegex(
            model,
            "\\[BsonElement\\(\"wrapReuseKey\"\\)\\]\\s*public\\s+string\\?\\s+WrapReuseKey",
            "Dynamic Form model must persist the default-wrap reuse identity");
        AssertContains(
            wrapBody,
            "ShouldReuseExistingWrap(req)",
            "only default wrap requests should opt into reuse");
        AssertContains(
            wrapBody,
            "BuildWrapReuseKey(me.Id, dynamicExcelId)",
            "reuse identity must include the actor and Dynamic Excel template");
        AssertContains(
            wrapBody,
            "WrapReuseKey = wrapReuseKey",
            "new default wrappers must persist their reuse identity");
        AssertContains(
            wrapBody,
            "catch (AppException ex) when",
            "concurrent duplicate insertion must enter a winner-reload path");
        AssertContains(
            wrapBody,
            "x.WrapReuseKey == wrapReuseKey",
            "concurrent duplicate insertion must reload by the exact reuse identity");
        AssertNotContains(
            wrapBody,
            "existing ??=",
            "default wrap reuse must not adopt an intentional custom wrapper with a null reuse key");
        AssertNotContains(
            wrapBody,
            "x.ExcelBlockDynamicExcelTemplateId == dynamicExcelId",
            "default wrap reuse must never fall back to the broad legacy wrapped-lineage lookup");
        AssertContains(
            wrapBody,
            "return await ToDetailAsync(winner, me, ct)",
            "concurrent default wrappers must return the single winning draft");

        AssertContains(
            updateBody,
            "!string.IsNullOrWhiteSpace(doc.WrapReuseKey)",
            "reactivating only wrapped drafts should map reuse-index collisions");
        AssertContains(
            updateBody,
            "req.IsActive",
            "deactivation and ordinary draft edits must not map unrelated duplicate keys as wrap conflicts");
        AssertContains(
            updateBody,
            "ex.WriteError?.Category == ServerErrorCategory.DuplicateKey",
            "reactivation must catch the atomic unique-index collision");
        AssertContains(
            updateBody,
            "AppErrorCode.DYNAMIC_FORM_WRAP_REUSE_CONFLICT",
            "reactivation must expose a stable conflict code instead of a generic 500");
        AssertContains(
            updateBody,
            "DYNAMIC_FORM_ACTIVE_WRAP_REUSE_EXISTS",
            "reactivation must expose a stable, metadata-free conflict reason");
        AssertBefore(
            updateBody,
            "BuildRevisionFilter(id, expectedRevision, requireDraft: true)",
            "AppErrorCode.DYNAMIC_FORM_WRAP_REUSE_CONFLICT",
            "reactivation must retain the exact draft revision CAS before mapping the duplicate-key result");

        AssertContains(indexBlock, "key: new BsonDocument(\"wrapReuseKey\", 1)", "reuse index key");
        AssertContains(indexBlock, "unique: true", "reuse index uniqueness");
        AssertContains(indexBlock, "{ \"isDeleted\", false }", "reuse index active document scope");
        AssertContains(indexBlock, "{ \"isActive\", true }", "reuse index active draft scope");
        AssertContains(indexBlock, "{ \"isPublished\", false }", "reuse index draft-only scope");
        AssertContains(
            indexBlock,
            "{ \"wrapReuseKey\", new BsonDocument(\"$type\", \"string\") }",
            "reuse index must exclude legacy null keys");

        AssertConflictDescriptor(AppErrorCode.DYNAMIC_FORM_WRAP_REUSE_CONFLICT);
    }

    private static void PublishCasReloadIsIdempotentForTheWinningPublishedVersion()
    {
        var service = ReadBackendSource("Services/DynamicFormService.cs");
        var publishBody = Slice(
            service,
            "public async Task<DynamicFormDetail> PublishAsync",
            "public async Task<DynamicFormDetail> CloneAsync");

        AssertContains(
            publishBody,
            "BuildRevisionFilter(id, expectedRevision, requireDraft: true)",
            "publish transition must remain an atomic draft CAS");
        AssertContains(
            publishBody,
            "if (res.MatchedCount == 0)",
            "publish must distinguish the losing CAS caller");
        AssertBefore(
            publishBody,
            "var current = await LoadAsync(id, ct)",
            "if (current.IsPublished)",
            "losing publish must reload before deciding idempotence");
        AssertBefore(
            publishBody,
            "RequireCanMutate(me, current)",
            "if (current.IsPublished)",
            "losing publish must re-check authorization on the reloaded document");
        AssertContains(
            publishBody,
            "return await ToDetailAsync(current, me, ct)",
            "losing concurrent publish must return the winning immutable version");
        AssertBefore(
            publishBody,
            "if (current.IsPublished)",
            "throw await ResolveRevisionFailureAsync(id, expectedRevision, ct)",
            "a non-published CAS loser must still receive a revision conflict");
    }

    private static void ForbiddenResponsesUseMetadataFreeDetails()
    {
        var service = ReadBackendSource("Services/DynamicFormService.cs");
        var helper = Slice(
            service,
            "private static object ForbiddenDetails",
            "private static FilterDefinition<DynamicFormTemplate> BuildVisibleFilter");
        var authorization = Slice(
            service,
            "private async Task RequireCanReadAsync",
            "private static string BuildStatisticConfigMonthKey");

        AssertContains(helper, "reason = \"DYNAMIC_FORM_ACCESS_FORBIDDEN\"", "generic forbidden reason");
        AssertContains(helper, "action", "generic forbidden action");
        foreach (var forbiddenMetadata in new[]
                 {
                     "dynamicFormTemplateId",
                     "doc.Code",
                     "doc.Name",
                     "doc.CreatedByUserId",
                     "actorUserId"
                 })
        {
            AssertNotContains(
                helper,
                forbiddenMetadata,
                $"forbidden details must not expose {forbiddenMetadata}");
        }

        foreach (var action in new[]
                 {
                     "READ_DYNAMIC_FORM",
                     "READ_VERSION_HISTORY",
                     "CLONE_DYNAMIC_FORM",
                     "MUTATE_DYNAMIC_FORM",
                     "UPDATE_DYNAMIC_FORM_STATISTICS"
                 })
        {
            AssertContains(
                authorization,
                $"ForbiddenDetails(\"{action}\")",
                $"forbidden path {action} must use metadata-free details");
        }

        AssertNotContains(
            authorization,
            "DynamicFormDetails(",
            "authorization failures must not use the metadata-bearing state-error payload");
    }

    private static void StatisticUpdateCannotRenormalizeFormStructure()
    {
        var controller = ReadBackendSource("Controllers/DynamicFormController.cs");
        var command = ReadBackendSource(
            "Services/DynamicForms/DynamicFormStatisticConfigCommandService.cs");
        var updateBody = Slice(
            command,
            "public async Task<DynamicFormStatisticConfigResult> PatchAsync",
            "private static NormalizedStatConfigCommand");

        AssertNotContains(
            command,
            "NormalizeBlocksForDynamicExcelTemplatesAsync",
            "statistics-only update must not refresh form structure from the current Dynamic Excel definition");
        AssertContains(
            controller,
            "[FromBody] JsonElement body",
            "statistics-only update must not bind a mutable full-document DTO");
        AssertContains(
            command,
            "new[] { \"isStatistic\", \"statisticLabelCodes\", \"statistic\" }",
            "statistics-only update must freeze the exact field-property allowlist");
        AssertBefore(
            updateBody,
            "NormalizeMutationSyntax(command.Payload)",
            "_transactions.ExecuteAsync",
            "strict payload normalization must finish before entering the write transaction");
        AssertBefore(
            updateBody,
            "ValidateAgainstTemplate(owner)",
            "PersistNextState(",
            "published structural integrity must be proven before changing statistic config");
    }

    private static void PublishedConsumersValidateSnapshotAgainstLiveStructure()
    {
        var service = ReadBackendSource("Services/DynamicFormService.cs");
        var statisticCommand = ReadBackendSource(
            "Services/DynamicForms/DynamicFormStatisticConfigCommandService.cs");
        var bindingPolicy = ReadBackendSource("Services/DynamicForms/DynamicFormBindingAccessPolicy.cs");
        var snapshotBuilder = ReadBackendSource("Services/DynamicForms/DynamicFormPublishedSchemaSnapshot.cs");
        var statistics = Slice(
            statisticCommand,
            "public async Task<DynamicFormStatisticConfigResult> PatchAsync",
            "private static NormalizedStatConfigCommand");
        var createVersion = Slice(
            service,
            "public async Task<DynamicFormDetail> CreateNextVersionAsync",
            "public async Task<DynamicFormDetail> CreateAsync");
        var clone = Slice(
            service,
            "public async Task<DynamicFormDetail> CloneAsync",
            "public async Task<DynamicFormDetail> WrapDynamicExcelAsync");

        AssertContains(
            snapshotBuilder,
            "ValidateAgainstTemplate",
            "published integrity must compare the immutable pair to the live template");
        AssertContains(
            bindingPolicy,
            "DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(form)",
            "assignment and Flow binding must reject live schema drift");
        AssertBefore(
            statistics,
            "ValidateAgainstTemplate(owner)",
            "PersistNextState(",
            "statistics PATCH must validate published live structure before changing owner state");
        AssertBefore(
            createVersion,
            "RequirePublishedSchemaIntegrity(source)",
            "var familyId = EffectiveFamilyId(source)",
            "successor creation must validate the published source before copying or reserving");
        AssertBefore(
            clone,
            "RequirePublishedSchemaIntegrity(source)",
            "var now = DateTime.UtcNow",
            "published clone must validate the source before copying");
        AssertContains(
            service,
            "return RequirePublishedSchemaIntegrity(doc)",
            "published detail/search/history and idempotent publish reads must fail closed on live drift");
    }

    private static void FlowDraftWritersRequirePublishedFormVersions()
    {
        var flowService = ReadBackendSource("Services/DynamicFlows/DynamicFlowTemplateService.cs");
        var definitionMutations = ReadBackendSource("Services/DynamicFlows/DynamicFlowTemplateDefinitionMutations.cs");
        var create = Slice(
            definitionMutations,
            "private async Task<DynamicFlowTemplateDto> CreateDefinitionAsync",
            "private async Task<DynamicFlowTemplateDto> UpdateDefinitionMetadataAsync");
        var update = Slice(
            definitionMutations,
            "private async Task<DynamicFlowTemplateDto> UpdateDefinitionMetadataAsync",
            "public async Task<DynamicFlowTemplateVersionDto> SaveDraftVersionAsync");
        var saveDraft = Slice(
            definitionMutations,
            "public async Task<DynamicFlowTemplateVersionDto> SaveDraftVersionAsync",
            "public async Task<DynamicFlowTemplateVersionDto> LockVersionAsync");
        var payloadLoader = Slice(
            flowService,
            "private async Task<Dictionary<string, DynamicFormTemplate>> LoadDynamicFormTemplatesForPayloadAsync",
            "private static IEnumerable<string?> CollectDynamicFormTemplateIds");
        var rootLoader = Slice(
            flowService,
            "private async Task<DynamicFormTemplate?> LoadDynamicFormTemplateOrNullAsync",
            "private static void ValidateLockablePayload");

        AssertContains(
            payloadLoader,
            "EnsureLockableDynamicFormVersions(rows)",
            "every payload reference writer must accept only immutable published Form versions");
        AssertContains(
            rootLoader,
            "EnsureLockableDynamicFormVersions(new[] { form })",
            "Flow root updates must accept only immutable published Form versions");
        AssertBefore(
            create,
            "PreparePayloadAsync",
            "InsertOneAsync(session, template",
            "Flow create must validate all Form versions before the first write");
        AssertBefore(
            saveDraft,
            "PreparePayloadAsync",
            "ReplaceOneAsync",
            "Flow draft save must validate all Form versions before a write");
        AssertBefore(
            update,
            "LoadDynamicFormTemplateOrNullAsync",
            "ReplaceOneAsync",
            "Flow root update must validate the Form version before a write");
    }

    private static void PublishAndImportRevalidateEnumCatalogs()
    {
        var service = ReadBackendSource("Services/DynamicFormService.cs");
        var publish = Slice(
            service,
            "public async Task<DynamicFormDetail> PublishAsync",
            "public async Task<DynamicFormDetail> CloneAsync");
        var import = Slice(
            service,
            "public async Task<DynamicFormDetail> ImportDynamicExcelBlockAsync",
            "public async Task DeleteAsync");

        foreach (var (body, context) in new[]
                 {
                     (publish, "publish"),
                     (import, "Dynamic Excel block import")
                 })
        {
            AssertContains(
                body,
                "_enumCatalogs.ValidateVisibleActiveCatalogsAsync",
                $"{context} must revalidate enum catalog visibility and active state");
            AssertContains(
                body,
                "ExtractEnumCatalogIds(doc.FieldsJson, excelBlockJson, blocksJson)",
                $"{context} must inspect the complete resulting Form schema");
            AssertBefore(
                body,
                "_enumCatalogs.ValidateVisibleActiveCatalogsAsync",
                "Builders<DynamicFormTemplate>.Update",
                $"{context} must reject inactive catalogs before writing");
        }
    }

    private static void TypedSchemaKindsFailBeforeAllMutationWrites()
    {
        var service = ReadBackendSource("Services/DynamicFormService.cs");
        var mutationBodies = new[]
        {
            (
                Name: "create version",
                Body: Slice(
                    service,
                    "public async Task<DynamicFormDetail> CreateNextVersionAsync",
                    "public async Task<DynamicFormDetail> CreateAsync"),
                FirstWrite: "ReserveNextVersionAsync(source"),
            (
                Name: "create",
                Body: Slice(
                    service,
                    "public async Task<DynamicFormDetail> CreateAsync",
                    "public async Task<DynamicFormDetail> UpdateAsync"),
                FirstWrite: "InsertDynamicFormAsync(doc"),
            (
                Name: "update",
                Body: Slice(
                    service,
                    "public async Task<DynamicFormDetail> UpdateAsync",
                    "public Task<DynamicFormStatisticConfigResult> GetStatisticsAsync"),
                FirstWrite: "_ctx.DynamicFormTemplates.UpdateOneAsync"),
            (
                Name: "publish",
                Body: Slice(
                    service,
                    "public async Task<DynamicFormDetail> PublishAsync",
                    "public async Task<DynamicFormDetail> CloneAsync"),
                FirstWrite: "_ctx.DynamicFormTemplates.UpdateOneAsync"),
            (
                Name: "clone",
                Body: Slice(
                    service,
                    "public async Task<DynamicFormDetail> CloneAsync",
                    "public async Task<DynamicFormDetail> WrapDynamicExcelAsync"),
                FirstWrite: "InsertDynamicFormAsync(clone"),
            (
                Name: "wrap",
                Body: Slice(
                    service,
                    "public async Task<DynamicFormDetail> WrapDynamicExcelAsync",
                    "public async Task<DynamicFormDetail> ImportDynamicExcelBlockAsync"),
                FirstWrite: "InsertDynamicFormAsync(doc"),
            (
                Name: "import",
                Body: Slice(
                    service,
                    "public async Task<DynamicFormDetail> ImportDynamicExcelBlockAsync",
                    "public async Task DeleteAsync"),
                FirstWrite: "_ctx.DynamicFormTemplates.UpdateOneAsync")
        };

        foreach (var mutation in mutationBodies)
        {
            AssertBefore(
                mutation.Body,
                "EnsureTypedSchemaProjection(",
                mutation.FirstWrite,
                $"{mutation.Name} must validate the typed response projection before its first write");
        }

        var createVersion = mutationBodies.Single(x => x.Name == "create version").Body;
        AssertBefore(
            createVersion,
            "RequirePublishedSchemaIntegrity(source)",
            "NormalizeBlocksJson(source.BlocksJson",
            "create version must validate the published raw schema before normalization");

        foreach (var (name, firstNormalization) in new[]
                 {
                     ("create", "NormalizeCode(req.Code)"),
                     ("update", "NormalizeLabelCodes(req.TagCodes)"),
                     ("publish", "NormalizeBlocksJson(doc.BlocksJson"),
                     ("clone", "NormalizeCode(req.Code)"),
                     ("import", "NormalizeDynamicExcelTemplateId(req.DynamicExcelTemplateId)")
                 })
        {
            var body = mutationBodies.Single(x => x.Name == name).Body;
            AssertBefore(
                body,
                "EnsureTypedSchemaProjection(",
                firstNormalization,
                $"{name} must validate the raw typed projection before normalization");
        }

        var validator = typeof(DynamicFormService).GetMethod(
            "EnsureTypedSchemaProjection",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing typed Dynamic Form schema projection validator.");
        const string sections = """[{"id":"main","title":"Main","order":0}]""";
        const string validField =
            """{"id":"value","sectionId":"main","key":"value","name":"Value","type":"number"}""";
        var cases = new List<(string Name, string Sections, string Fields, string Blocks)>
        {
            (
                "section order",
                """[{"id":"main","title":"Main","order":"TOP_SECRET_RAW_VALUE"}]""",
                "[]",
                "[]"),
            (
                "block tableMode",
                sections,
                "[]",
                """[{"blockId":"block-1","sectionId":"main","tableMode":42}]""")
        };
        foreach (var property in new[]
                 {
                     "colSpan",
                     "minHeight",
                     "order",
                     "canvasX",
                     "canvasY",
                     "canvasW",
                     "canvasH"
                 })
        {
            cases.Add((
                $"field {property}",
                sections,
                $"[{validField[..^1]},\"{property}\":\"TOP_SECRET_RAW_VALUE\"}}]",
                "[]"));
        }

        foreach (var item in cases)
        {
            var error = AssertThrowsAppException(() => validator.Invoke(
                null,
                new object?[] { item.Sections, item.Fields, null, item.Blocks }));
            AssertEqual(
                AppErrorCode.DYNAMIC_FORM_JSON_KIND_INVALID,
                error.Code,
                $"{item.Name} error code");
            AssertEqual(400, error.Descriptor.HttpStatus, $"{item.Name} HTTP status");
            var detailsJson = JsonSerializer.Serialize(error.Details);
            AssertContains(
                detailsJson,
                "DYNAMIC_FORM_TYPED_SCHEMA_KIND_INVALID",
                $"{item.Name} stable reason");
            AssertNotContains(
                detailsJson,
                "TOP_SECRET_RAW_VALUE",
                $"{item.Name} must not echo raw schema payload");
        }
    }

    private static void MalformedObjectIdsFailBeforeMongoSerialization()
    {
        var service = ReadBackendSource("Services/DynamicFormService.cs");
        var load = Slice(
            service,
            "private async Task<DynamicFormTemplate> LoadAsync",
            "private static BsonRegularExpression BuildLiteralSearchRegex");
        var wrap = Slice(
            service,
            "public async Task<DynamicFormDetail> WrapDynamicExcelAsync",
            "public async Task<DynamicFormDetail> ImportDynamicExcelBlockAsync");
        var import = Slice(
            service,
            "public async Task<DynamicFormDetail> ImportDynamicExcelBlockAsync",
            "public async Task DeleteAsync");
        var blockNormalizer = Slice(
            service,
            "private async Task<string> NormalizeBlocksForDynamicExcelTemplatesAsync",
            "private static void EnsureUniqueDynamicExcelTemplateBlocks");

        AssertBefore(
            load,
            "NormalizeDynamicFormTemplateId(id)",
            "_ctx.DynamicFormTemplates",
            "route Form ids must be validated before Mongo query rendering");
        AssertBefore(
            wrap,
            "NormalizeDynamicExcelTemplateId(req.DynamicExcelTemplateId)",
            "_ctx.DynamicExcelTemplates",
            "wrap Dynamic Excel id must be validated before Mongo query rendering");
        AssertBefore(
            import,
            "NormalizeDynamicExcelTemplateId(req.DynamicExcelTemplateId)",
            "_ctx.DynamicExcelTemplates",
            "import Dynamic Excel id must be validated before Mongo query rendering");
        AssertBefore(
            blockNormalizer,
            "NormalizeDynamicExcelTemplateId(rawId)",
            "_ctx.DynamicExcelTemplates",
            "schema block Dynamic Excel ids must be validated before Mongo query rendering");

        foreach (var (methodName, reason) in new[]
                 {
                     ("NormalizeDynamicFormTemplateId", "DYNAMIC_FORM_TEMPLATE_ID_INVALID"),
                     ("NormalizeDynamicExcelTemplateId", "DYNAMIC_EXCEL_TEMPLATE_ID_INVALID")
                 })
        {
            var method = typeof(DynamicFormService).GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Missing id normalizer {methodName}.");
            var error = AssertThrowsAppException(() => method.Invoke(null, new object?[] { "not-an-object-id" }));
            AssertEqual(AppErrorCode.COMMON_VALIDATION_FAILED, error.Code, $"{methodName} error code");
            AssertEqual(400, error.Descriptor.HttpStatus, $"{methodName} HTTP status");
            var detailsJson = JsonSerializer.Serialize(error.Details);
            AssertContains(detailsJson, reason, $"{methodName} stable reason");
            AssertNotContains(detailsJson, "not-an-object-id", $"{methodName} must not echo raw id");
        }
    }

    private static AppException AssertThrowsAppException(Action action)
    {
        try
        {
            action();
        }
        catch (TargetInvocationException error) when (error.InnerException is AppException appError)
        {
            return appError;
        }
        catch (AppException error)
        {
            return error;
        }

        throw new InvalidOperationException("Expected AppException was not thrown.");
    }

    private static void AssertConflictDescriptor(AppErrorCode code)
    {
        var descriptor = AppErrorCatalog.Get(code);
        AssertEqual(code, descriptor.Code, $"catalog code {code}");
        AssertEqual(409, descriptor.HttpStatus, $"catalog HTTP status {code}");
        AssertEqual("DYNAMIC_FORM", descriptor.Service, $"catalog service {code}");
    }

    private static string ReadBackendSource(string relativePath)
    {
        var root = FindBackendRoot();
        var path = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new InvalidOperationException($"Backend source file was not found: {path}");

        return File.ReadAllText(path);
    }

    private static string FindBackendRoot()
    {
        var seeds = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in seeds)
        {
            for (var directory = new DirectoryInfo(seed); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "tdtd-be.csproj")))
                    return directory.FullName;

                var nested = Path.Combine(directory.FullName, "tdtd-be");
                if (File.Exists(Path.Combine(nested, "tdtd-be.csproj")))
                    return nested;
            }
        }

        throw new InvalidOperationException("Could not locate the tdtd-be source root.");
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        if (startIndex < 0)
            throw new InvalidOperationException($"Source contract start anchor was not found: {start}");

        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        if (endIndex < 0)
            throw new InvalidOperationException($"Source contract end anchor was not found: {end}");

        return source[startIndex..endIndex];
    }

    private static void AssertBefore(
        string source,
        string first,
        string second,
        string context)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        if (firstIndex < 0 || secondIndex < 0 || firstIndex >= secondIndex)
        {
            throw new InvalidOperationException(
                $"{context}: expected '{first}' before '{second}'.");
        }
    }

    private static void AssertRegex(string source, string pattern, string context)
    {
        if (!Regex.IsMatch(source, pattern, RegexOptions.CultureInvariant))
            throw new InvalidOperationException($"{context}: source pattern was not found.");
    }

    private static void AssertContains(string source, string expected, string context)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context}: expected source fragment '{expected}'.");
    }

    private static void AssertNotContains(string source, string forbidden, string context)
    {
        if (source.Contains(forbidden, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context}: forbidden source fragment '{forbidden}'.");
    }

    private static void AssertEqual<T>(T expected, T actual, string context)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected '{expected}', got '{actual}'.");
    }

    private static void AssertTrue(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }
}
