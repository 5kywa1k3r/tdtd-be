using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.DynamicForms;

public interface IDynamicFormStatisticConfigCommandService
{
    Task PublishNativeAsync(string id, int expectedRevision, DynamicFormPublishedSchemaSnapshot snapshot, CancellationToken ct);
    Task<DynamicFormStatisticConfigResult> GetAsync(
        string id,
        CancellationToken ct);

    Task<DynamicFormStatisticConfigResult> PatchAsync(
        string id,
        JsonElement body,
        CancellationToken ct);
}

/// <summary>
/// P8-02/P8-03 command boundary for field and table statistic metadata on the canonical
/// DynamicFormTemplate owner. It never calls a materializer, queue, outbox or
/// result collection.
/// </summary>
public sealed partial class DynamicFormStatisticConfigCommandService :
    IDynamicFormStatisticConfigCommandService
{
    private const string CommandKind = "UPDATE_DYNAMIC_FORM_STATISTICS";
    private static readonly HashSet<string> StatisticProperties =
        new(
            new[] { "isStatistic", "statisticLabelCodes", "statistic", "showOnOverview" },
            StringComparer.Ordinal);

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly IStatConfigTransactionRunner _transactions;

    public DynamicFormStatisticConfigCommandService(
        MongoDbContext ctx,
        MeAccessor me,
        IStatConfigTransactionRunner transactions)
    {
        _ctx = ctx;
        _me = me;
        _transactions = transactions;
    }

    public async Task<DynamicFormStatisticConfigResult> GetAsync(
        string id,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        id = NormalizeOwnerId(id);
        var owner = await _ctx.DynamicFormTemplates
            .Find(BuildOwnerFilter(id, me))
            .FirstOrDefaultAsync(ct);
        if (owner is null)
            throw OwnerNotFound(id);
        if (owner.IsPublished)
        {
            _ = DynamicFormPublishedSchemaSnapshotBuilder
                .ValidateAgainstTemplate(owner);
        }

        var state = await LoadStateAsync(
            session: null,
            owner,
            me,
            ct);
        return ToResult(owner, state, me, receiptId: null);
    }

    public async Task<DynamicFormStatisticConfigResult> PatchAsync(
        string id,
        JsonElement body,
        CancellationToken ct)
    {
        using var isolation =
            StatConfigIsolationGuard.EnterConfigurationMutation(
                StatConfigOwnerKinds.DynamicForm);
        var me = _me.RequireMe();
        id = NormalizeOwnerId(id);
        var authorizedOwnerExists =
            await _ctx.DynamicFormTemplates
                .Find(BuildOwnerFilter(id, me))
                .Project(owner => owner.Id)
                .AnyAsync(ct);
        if (!authorizedOwnerExists)
            throw OwnerNotFound(id);

        var command = NormalizeCommand(body);
        // Form field/native Table calculations are now authored by Aggregate Canvas.
        // Keep P8 reads, history and the separate legacy Excel table command intact.
        if (command.Payload.Fields is not null || command.Payload.NativeTargets is not null
            || command.Payload.NativeStatistics is not null)
            throw Schema("$.payload", "FORM_CALCULATION_AUTHORING_MOVED_TO_AGGREGATE_CANVAS");
        var mutations = NormalizeMutationSyntax(command.Payload);
        object normalizedPayload = mutations.NativeStatistics is not null
            ? new { nativeStatistics = mutations.NativeStatistics }
            : mutations.NativeTargets is not null
            ? new { nativeTargets = mutations.NativeTargets }
            : mutations.Fields is not null
            ? new { fields = mutations.Fields }
            : new { tables = mutations.Tables };
        command = command with
        {
            RequestHash = StatConfigCanonicalJson.HashObject(new
            {
                commandKind = CommandKind,
                expectedRevision = command.ExpectedRevision,
                expectedConfigHash = command.ExpectedConfigHash,
                payload = normalizedPayload
            })
        };
        var receiptId = ReceiptId(id, command.CommandId);

        try
        {
            return await _transactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    var owner = await _ctx.DynamicFormTemplates
                        .Find(session, BuildOwnerFilter(id, me))
                        .FirstOrDefaultAsync(transactionCt);
                    if (owner is null)
                        throw OwnerNotFound(id);
                    if (owner.IsPublished)
                    {
                        _ = DynamicFormPublishedSchemaSnapshotBuilder
                            .ValidateAgainstTemplate(owner);
                    }

                    var replay = await LoadReplayAsync(
                        session,
                        receiptId,
                        command.RequestHash,
                        transactionCt);
                    if (replay is not null)
                        return replay;

                    var current = await LoadStateAsync(
                        session,
                        owner,
                        me,
                        transactionCt);
                    EnsureCas(current, command);
                    var next = mutations.NativeStatistics is not null
                        ? await ApplyNativeStatisticsAsync(session, owner, current, mutations.NativeStatistics, me, transactionCt)
                        : mutations.NativeTargets is not null
                        ? await ApplyNativeMutationsAsync(session, owner, current, mutations.NativeTargets, me, transactionCt)
                        : await ApplyMutationsAsync(
                        session,
                        owner,
                        current,
                        mutations,
                        me,
                        transactionCt);

                    next = CompleteNativeState(owner, next,
                        next.NativeTargetSectionJson ?? current.NativeTargetSectionJson,
                        next.NativeTablesJson ?? current.NativeTablesJson,
                        mutations.NativeStatistics is not null ? next.NativePlanSectionJson : current.NativePlanSectionJson);

                    if (owner.IsPublished && DynamicFormNativeTableDefinition.IsNative(owner)
                        && DeserializeNativeSection(next.NativeTargetSectionJson)?.Count > 0)
                        throw Schema("$.nativeStatistics", "NATIVE_PUBLISHED_ACTIVE_V1_REQUIRES_EXPLICIT_UPGRADE");

                    var priorOwnerRevision = owner.Revision;
                    var hadPersistedIdentity =
                        !string.IsNullOrWhiteSpace(owner.StatisticConfigHash);
                    var mutationNow =
                        UtcNowAtMillisecondPrecision();
                    PersistNextState(
                        owner,
                        current,
                        next,
                        me.Id,
                        mutationNow);

                    // Keep complete history or fail the transaction; never trim old
                    // versions/separators to fit the Mongo document limit.
                    var enforceNativeHistoryBudget = mutations.NativeStatistics is not null
                        || owner.StatisticConfigSnapshots?.Any(s => s.Sections?.NativePlanSectionJson is not null) == true;
                    if (enforceNativeHistoryBudget && owner.ToBson().LongLength > 15L * 1024 * 1024)
                        throw Schema("$.nativeStatistics", "NATIVE_STATISTIC_HISTORY_STORAGE_BUDGET_EXCEEDED");

                    var replacement = await _ctx.DynamicFormTemplates
                        .ReplaceOneAsync(
                            session,
                            BuildOwnerCasFilter(
                                id,
                                priorOwnerRevision,
                                current,
                                hadPersistedIdentity),
                            owner,
                            cancellationToken: transactionCt);
                    if (replacement.ModifiedCount != 1)
                        throw CasConflict(command, current);

                    var result = ToResult(
                        owner,
                        next,
                        me,
                        receiptId);
                    var receipt = CreateReceipt(
                            receiptId,
                            command,
                            next,
                            result,
                            me.Id,
                            mutationNow);
                    if (enforceNativeHistoryBudget && receipt.ToBson().LongLength > 15L * 1024 * 1024)
                        throw Schema("$.nativeStatistics", "NATIVE_STATISTIC_RECEIPT_STORAGE_BUDGET_EXCEEDED");
                    await _ctx.StatConfigCommandReceipts.InsertOneAsync(
                        session,
                        receipt,
                        cancellationToken: transactionCt);
                    return result;
                },
                ct);
        }
        catch (MongoWriteException ex) when (
            ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var receipt = await _ctx.StatConfigCommandReceipts
                .Find(item => item.Id == receiptId)
                .FirstOrDefaultAsync(ct);
            if (receipt is not null)
                return RestoreReplay(receipt, command.RequestHash);
            throw;
        }
        catch (AppException ex) when (
            ex.Code == AppErrorCode.STAT_CONFIG_CAS_CONFLICT)
        {
            var receipt = await _ctx.StatConfigCommandReceipts
                .Find(item => item.Id == receiptId)
                .FirstOrDefaultAsync(ct);
            if (receipt is not null)
                return RestoreReplay(receipt, command.RequestHash);
            throw;
        }
    }

    private static NormalizedStatConfigCommand<
        DynamicFormStatisticConfigPayload> NormalizeCommand(
        JsonElement body)
    {
        try
        {
            var envelope = StatConfigCanonicalJson.DeserializeStrict<
                StatConfigMutationEnvelope<
                    DynamicFormStatisticConfigPayload>>(body);
            if (envelope.Payload?.NativeStatistics is not null)
            {
                body.GetProperty("payload").GetProperty("nativeStatistics").TryGetProperty("tables", out var nativeTables);
                DynamicFormNativeStatisticState.ValidateWirePlans(
                    nativeTables, metadata: true);
            }
            return StatConfigCanonicalJson.NormalizeCommand(
                envelope,
                CommandKind);
        }
        catch (AppException ex) when (
            ex.Code == AppErrorCode.STAT_CONFIG_SCHEMA_INVALID)
        {
            throw new AppException(
                AppErrorCode.DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID,
                ex.Details);
        }
    }

    private static IReadOnlyList<NormalizedMutation>
        NormalizeFieldMutationSyntax(
            DynamicFormStatisticConfigPayload payload)
    {
        if (payload.Fields is null)
            throw Schema("$.payload.fields", "FIELDS_REQUIRED");
        if (payload.Fields.Count == 0)
            throw Schema("$.payload.fields", "FIELD_MUTATION_REQUIRED");

        var seenFields = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<NormalizedMutation>(
            payload.Fields.Count);
        for (var index = 0; index < payload.Fields.Count; index++)
        {
            var path = $"$.payload.fields[{index}]";
            var input = payload.Fields[index];
            var fieldId = input.FieldId?.Trim();
            if (string.IsNullOrWhiteSpace(fieldId))
                throw Schema($"{path}.fieldId", "FIELD_ID_REQUIRED");
            if (!seenFields.Add(fieldId))
                throw Schema($"{path}.fieldId", "DUPLICATE_FIELD_ID");
            if (!input.IsStatistic.HasValue)
                throw Schema(
                    $"{path}.isStatistic",
                    "IS_STATISTIC_REQUIRED");

            var labelCodes = NormalizeLabelCodes(
                input.StatisticLabelCodes,
                $"{path}.statisticLabelCodes");
            if (!input.IsStatistic.Value)
            {
                if (input.Statistic is not null)
                {
                    throw Schema(
                        $"{path}.statistic",
                        "DISABLED_STATISTIC_CONFIG_FORBIDDEN");
                }
                if (labelCodes.Count > 0)
                {
                    throw Schema(
                        $"{path}.statisticLabelCodes",
                        "DISABLED_STATISTIC_LABELS_FORBIDDEN");
                }
                normalized.Add(
                    new NormalizedMutation(
                        fieldId,
                        false,
                        null,
                        Array.Empty<string>(),
                        index));
                continue;
            }

            var statistic = input.Statistic ??
                            throw Schema(
                                $"{path}.statistic",
                                "STATISTIC_CONFIG_REQUIRED");
            if (statistic.AggregateOps is null ||
                statistic.AggregateOps.Count == 0)
            {
                throw Schema(
                    $"{path}.statistic.aggregateOps",
                    "AGGREGATE_OPS_REQUIRED");
            }
            if (!statistic.ShowInDetail.HasValue)
            {
                throw Schema(
                    $"{path}.statistic.showInDetail",
                    "SHOW_IN_DETAIL_REQUIRED");
            }
            if (!statistic.ShowInTree.HasValue)
            {
                throw Schema(
                    $"{path}.statistic.showInTree",
                    "SHOW_IN_TREE_REQUIRED");
            }

            var operations = new List<string>(
                statistic.AggregateOps.Count);
            var seenOperations =
                new HashSet<string>(StringComparer.Ordinal);
            for (var operationIndex = 0;
                 operationIndex < statistic.AggregateOps.Count;
                 operationIndex++)
            {
                var operation =
                    statistic.AggregateOps[operationIndex]?.Trim()
                        .ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(operation))
                {
                    throw OperationError(
                        $"{path}.statistic.aggregateOps[{operationIndex}]",
                        "OPERATION_REQUIRED",
                        null,
                        null);
                }
                if (!seenOperations.Add(operation))
                {
                    throw Schema(
                        $"{path}.statistic.aggregateOps[{operationIndex}]",
                        "DUPLICATE_OPERATION");
                }
                operations.Add(operation);
            }
            var bucketMode =
                statistic.BucketMode?.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(bucketMode))
            {
                throw BucketError(
                    $"{path}.statistic.bucketMode",
                    "BUCKET_MODE_REQUIRED",
                    null,
                    null);
            }

            normalized.Add(
                new NormalizedMutation(
                    fieldId,
                    true,
                    new DynamicFormStatisticSettingsPayload(
                        operations,
                        bucketMode,
                        statistic.ShowInDetail.Value,
                        statistic.ShowInTree.Value),
                    labelCodes,
                    index));
        }
        return normalized;
    }

    private async Task<ConfigState> ApplyFieldMutationsAsync(
        IClientSessionHandle session,
        DynamicFormTemplate owner,
        ConfigState current,
        IReadOnlyList<NormalizedMutation> mutations,
        MeResponse me,
        CancellationToken ct)
    {
        var fieldArray = ParseFieldArray(owner.FieldsJson);
        var schemaFields = BuildSchemaFieldMap(fieldArray);
        var nextFields = current.Fields.ToDictionary(
            field => field.FieldId,
            field => field,
            StringComparer.Ordinal);

        foreach (var mutation in mutations)
        {
            var path = $"$.payload.fields[{mutation.InputIndex}]";
            if (!schemaFields.TryGetValue(
                    mutation.FieldId,
                    out var schemaField))
            {
                throw Schema(
                    $"{path}.fieldId",
                    "FIELD_NOT_FOUND");
            }

            if (!mutation.IsStatistic)
            {
                nextFields.Remove(mutation.FieldId);
                schemaField.Node["isStatistic"] = false;
                schemaField.Node["statisticLabelCodes"] =
                    new JsonArray();
                schemaField.Node.Remove("statistic");
                continue;
            }

            var fieldType = MapFieldType(
                schemaField.Type,
                $"{path}.fieldId");
            ValidateStatisticSettings(
                fieldType,
                mutation.Statistic!,
                path);
            var labels = await ResolveLabelsAsync(
                session,
                mutation.StatisticLabelCodes,
                fieldType,
                me,
                path,
                ct);
            var persisted = new PersistedFieldConfig(
                mutation.FieldId,
                fieldType,
                true,
                mutation.Statistic,
                mutation.StatisticLabelCodes,
                labels,
                ComputeStructureHash(schemaField.Node));
            nextFields[mutation.FieldId] = persisted;

            schemaField.Node["isStatistic"] = true;
            schemaField.Node["statisticLabelCodes"] =
                JsonSerializer.SerializeToNode(
                    mutation.StatisticLabelCodes,
                    StatConfigCanonicalJson.StrictJsonOptions);
            schemaField.Node["statistic"] =
                JsonSerializer.SerializeToNode(
                    mutation.Statistic,
                    StatConfigCanonicalJson.StrictJsonOptions);
        }

        if (nextFields.Count >
            DynamicFormStatisticOperationContract.MaximumTargets)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode
                    .DYNAMIC_FORM_STATISTIC_TARGET_LIMIT_EXCEEDED,
                new
                {
                    path = "$.payload.fields",
                    reason = "FIELD_STATISTIC_TARGET_LIMIT_30",
                    limit =
                        DynamicFormStatisticOperationContract
                            .MaximumTargets,
                    actual = nextFields.Count
                });
        }
        var orderedFields = nextFields.Values
            .OrderBy(field => field.FieldId, StringComparer.Ordinal)
            .ToList();
        EnsureUniqueLabelTargets(orderedFields);
        EnsureUniqueStatisticLabelTargets(
            orderedFields,
            current.Tables);
        var fieldSectionJson =
            StatConfigCanonicalJson.Canonicalize(orderedFields);
        var tableSectionJson = current.TableSectionJson;
        var dependencyPins = BuildDependencyPins(
            orderedFields,
            current.Tables);
        var status = CurrentStatus(owner);
        var configHash = ComputeConfigHash(
            owner.Id,
            fieldSectionJson,
            tableSectionJson,
            dependencyPins);
        var fieldsJson = CanonicalNode(fieldArray);
        return new ConfigState(
            current.ConfigId,
            ObjectId.GenerateNewId().ToString(),
            current.VersionId,
            current.VersionNo + 1,
            current.Revision + 1,
            status,
            configHash,
            dependencyPins,
            fieldSectionJson,
            tableSectionJson,
            orderedFields,
            current.Tables,
            fieldsJson,
            current.BlocksJson,
            IsVirtual: false);
    }

    private async Task<ConfigState> LoadStateAsync(
        IClientSessionHandle? session,
        DynamicFormTemplate owner,
        MeResponse me,
        CancellationToken ct)
    {
        var nativeSectionJson = await ReadNativeSectionAsync(session, owner, me, ct);
        var nativePlanSectionJson = await ReadNativePlanSectionAsync(session, owner, me, ct);
        var fieldArray = ParseFieldArray(owner.FieldsJson);
        var schemaFields = BuildSchemaFieldMap(fieldArray);
        var hasPersistedIdentity =
            !string.IsNullOrWhiteSpace(owner.StatisticConfigId) ||
            !string.IsNullOrWhiteSpace(
                owner.StatisticConfigVersionId) ||
            owner.StatisticConfigRevision > 0 ||
            !string.IsNullOrWhiteSpace(owner.StatisticConfigHash);

        if (!hasPersistedIdentity)
        {
            var initialFields = await DeriveInitialFieldsAsync(
                session,
                schemaFields,
                me,
                ct);
            var initialTables = await DeriveInitialTablesAsync(
                session,
                owner,
                me,
                ct);
            EnsureUniqueStatisticLabelTargets(
                initialFields,
                initialTables);
            ValidateNativeTargetLimits(initialFields, initialTables, nativeSectionJson, nativePlanSectionJson);
            var initialFieldSectionJson =
                StatConfigCanonicalJson.Canonicalize(initialFields);
            var initialTableSectionJson =
                StatConfigCanonicalJson.Canonicalize(initialTables);
            var initialDependencyPins =
                NativeDependencyPins(BuildDependencyPins(initialFields, initialTables), nativeSectionJson, nativePlanSectionJson);
            return new ConfigState(
                owner.Id,
                owner.Id,
                null,
                1,
                1,
                CurrentStatus(owner),
                ComputeConfigHash(
                    owner.Id,
                    initialFieldSectionJson,
                    initialTableSectionJson,
                    initialDependencyPins, nativeSectionJson, nativePlanSectionJson),
                initialDependencyPins,
                initialFieldSectionJson,
                initialTableSectionJson,
                initialFields,
                initialTables,
                owner.FieldsJson,
                owner.BlocksJson,
                IsVirtual: true) { NativeTargetSectionJson = nativeSectionJson, NativePlanSectionJson = nativePlanSectionJson, NativeTablesJson = owner.TablesJson };
        }

        if (string.IsNullOrWhiteSpace(owner.StatisticConfigId) ||
            string.IsNullOrWhiteSpace(
                owner.StatisticConfigVersionId) ||
            owner.StatisticConfigVersionNo < 1 ||
            owner.StatisticConfigRevision < 1 ||
            string.IsNullOrWhiteSpace(owner.StatisticConfigHash))
        {
            throw IntegrityConflict(owner.Id, "CONFIG_IDENTITY");
        }

        var expectedStatus = CurrentStatus(owner);
        if (!string.Equals(
                owner.StatisticConfigStatus,
                expectedStatus,
                StringComparison.Ordinal))
        {
            throw IntegrityConflict(owner.Id, "CONFIG_STATUS");
        }

        var currentSnapshots =
            (owner.StatisticConfigSnapshots ?? new())
            .Where(snapshot =>
                string.Equals(
                    snapshot.VersionId,
                    owner.StatisticConfigVersionId,
                    StringComparison.Ordinal) &&
                snapshot.VersionNo ==
                owner.StatisticConfigVersionNo)
            .ToList();
        if (currentSnapshots.Count != 1 ||
            currentSnapshots[0].Revision !=
            owner.StatisticConfigRevision ||
            !string.Equals(
                currentSnapshots[0].ConfigHash,
                owner.StatisticConfigHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                currentSnapshots[0].Status,
                owner.StatisticConfigStatus,
                StringComparison.Ordinal))
        {
            throw IntegrityConflict(
                owner.Id,
                "CURRENT_SNAPSHOT");
        }

        var sections = owner.StatisticConfigSections ??
                       new DynamicFormStatisticConfigSections();
        var fieldSectionJson = CanonicalSectionJson(
            sections.FieldSectionJson,
            "$.fieldConfig");
        var tableSectionJson = CanonicalSectionJson(
            sections.TableSectionJson,
            "$.tableConfig");
        var fields = DeserializeFieldSection(fieldSectionJson);
        var tables = DeserializeTableSection(tableSectionJson);
        ValidatePersistedFieldStructure(fields, schemaFields, owner.Id);
        ValidatePersistedTableStructure(tables, owner);
        EnsureUniqueStatisticLabelTargets(fields, tables);
        ValidateNativeTargetLimits(fields, tables, nativeSectionJson, nativePlanSectionJson);
        var dependencyPins = NativeDependencyPins(BuildDependencyPins(fields, tables), nativeSectionJson, nativePlanSectionJson);
        if (!(owner.StatisticConfigDependencyPins ??
              new List<string>()).SequenceEqual(
                dependencyPins,
                StringComparer.Ordinal))
        {
            throw IntegrityConflict(owner.Id, "DEPENDENCY_PINS");
        }

        var recomputed = ComputeConfigHash(
            owner.Id,
            fieldSectionJson,
            tableSectionJson,
            dependencyPins, nativeSectionJson, nativePlanSectionJson);
        if (!string.Equals(
                owner.StatisticConfigHash,
                recomputed,
                StringComparison.Ordinal))
        {
            throw IntegrityConflict(owner.Id, "CONFIG_HASH");
        }

        return new ConfigState(
            owner.StatisticConfigId,
            owner.StatisticConfigVersionId,
            owner.StatisticConfigPreviousVersionId,
            owner.StatisticConfigVersionNo,
            owner.StatisticConfigRevision,
            owner.StatisticConfigStatus!,
            recomputed,
            dependencyPins,
            fieldSectionJson,
            tableSectionJson,
            fields,
            tables,
            owner.FieldsJson,
            owner.BlocksJson,
            IsVirtual: false) { NativeTargetSectionJson = nativeSectionJson, NativePlanSectionJson = nativePlanSectionJson, NativeTablesJson = owner.TablesJson };
    }

    private async Task<List<PersistedFieldConfig>>
        DeriveInitialFieldsAsync(
            IClientSessionHandle? session,
            IReadOnlyDictionary<string, SchemaField> schemaFields,
            MeResponse me,
            CancellationToken ct)
    {
        var result = new List<PersistedFieldConfig>();
        foreach (var schemaField in schemaFields.Values
                     .OrderBy(
                         field => field.Id,
                         StringComparer.Ordinal))
        {
            if (!ReadBoolean(
                    schemaField.Node,
                    "isStatistic"))
            {
                continue;
            }

            var path =
                $"$.schema.fields[{schemaField.Index}].statistic";
            var fieldType = MapFieldType(
                schemaField.Type,
                path);
            if (schemaField.Node["statistic"] is not JsonObject statistic)
                throw Schema(path, "STATISTIC_CONFIG_REQUIRED");
            DynamicFormStatisticSettingsPayload settings;
            try
            {
                settings = JsonSerializer.Deserialize<
                               DynamicFormStatisticSettingsPayload>(
                               statistic.ToJsonString(),
                               StatConfigCanonicalJson
                                   .StrictJsonOptions) ??
                           throw new JsonException();
            }
            catch (JsonException)
            {
                throw Schema(path, "LEGACY_STATISTIC_SCHEMA_INVALID");
            }

            var normalized = NormalizeExistingSettings(
                settings,
                fieldType,
                path);
            var labelCodes = NormalizeLabelCodes(
                ReadStringArray(
                    schemaField.Node["statisticLabelCodes"]),
                $"$.schema.fields[{schemaField.Index}].statisticLabelCodes");
            var labels = await ResolveLabelsAsync(
                session,
                labelCodes,
                fieldType,
                me,
                $"$.schema.fields[{schemaField.Index}]",
                ct);
            result.Add(
                new PersistedFieldConfig(
                    schemaField.Id,
                    fieldType,
                    true,
                    normalized,
                    labelCodes,
                    labels,
                    ComputeStructureHash(schemaField.Node)));
        }

        if (result.Count >
            DynamicFormStatisticOperationContract.MaximumTargets)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode
                    .DYNAMIC_FORM_STATISTIC_TARGET_LIMIT_EXCEEDED,
                new
                {
                    path = "$.schema.fields",
                    reason = "FIELD_STATISTIC_TARGET_LIMIT_30",
                    limit =
                        DynamicFormStatisticOperationContract
                            .MaximumTargets,
                    actual = result.Count
                });
        }
        return result;
    }

    private async Task<List<
        DynamicFormStatisticLabelSnapshotDto>> ResolveLabelsAsync(
        IClientSessionHandle? session,
        IReadOnlyList<string> codes,
        string fieldType,
        MeResponse me,
        string fieldPath,
        CancellationToken ct)
    {
        if (codes.Count == 0)
            return new List<DynamicFormStatisticLabelSnapshotDto>();

        var filter =
            Builders<LabelCatalogItem>.Filter.In(
                label => label.Code,
                codes) &
            Builders<LabelCatalogItem>.Filter.Eq(
                label => label.IsDeleted,
                false) &
            BuildLabelVisibilityFilter(me);
        List<LabelCatalogItem> labels;
        if (session is null)
        {
            labels = await _ctx.Labels.Find(filter).ToListAsync(ct);
        }
        else
        {
            labels = await _ctx.Labels
                .Find(session, filter)
                .ToListAsync(ct);
        }

        var expectedDataType = ExpectedLabelDataType(fieldType);
        var result = new List<
            DynamicFormStatisticLabelSnapshotDto>(codes.Count);
        for (var index = 0; index < codes.Count; index++)
        {
            var code = codes[index];
            var path =
                $"{fieldPath}.statisticLabelCodes[{index}]";
            var matches = labels
                .Where(label =>
                    string.Equals(
                        label.Code,
                        code,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0 ||
                matches.All(label => !label.IsActive))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode
                        .DYNAMIC_FORM_LABEL_NOT_FOUND_OR_INACTIVE,
                    new
                    {
                        path,
                        reason =
                            "FIELD_STATISTIC_LABEL_NOT_FOUND_OR_INACTIVE",
                        code
                    });
            }
            matches = matches.Where(label => label.IsActive).ToList();
            if (matches.Count != 1)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode
                        .DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                    new
                    {
                        path,
                        reason =
                            "FIELD_STATISTIC_LABEL_AMBIGUOUS",
                        code
                    });
            }

            var label = matches[0];
            if (!string.Equals(
                    label.Usage,
                    LabelUsages.Statistic,
                    StringComparison.Ordinal))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode
                        .DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                    new
                    {
                        path,
                        reason =
                            "FIELD_STATISTIC_LABEL_USAGE_INCOMPATIBLE",
                        code,
                        expectedUsage = LabelUsages.Statistic,
                        actualUsage = label.Usage
                    });
            }
            if (!string.Equals(
                    label.DataType,
                    expectedDataType,
                    StringComparison.Ordinal))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode
                        .DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                    new
                    {
                        path,
                        reason =
                            "FIELD_STATISTIC_LABEL_TYPE_INCOMPATIBLE",
                        code,
                        expectedDataType,
                        actualDataType = label.DataType
                    });
            }
            if (string.IsNullOrWhiteSpace(label.VersionId) ||
                string.IsNullOrWhiteSpace(label.ConfigHash) ||
                label.VersionNo < 1)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode
                        .DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                    new
                    {
                        path,
                        reason =
                            "FIELD_STATISTIC_LABEL_IDENTITY_INVALID",
                        code
                    });
            }

            result.Add(
                new DynamicFormStatisticLabelSnapshotDto(
                    label.Id,
                    label.Code,
                    label.DataType,
                    label.Usage,
                    label.ScopeType,
                    label.ScopeId,
                    label.IsActive,
                    label.VersionNo,
                    label.VersionId,
                    label.ConfigHash));
        }
        return result;
    }

    private static void ValidateStatisticSettings(
        string fieldType,
        DynamicFormStatisticSettingsPayload settings,
        string fieldPath)
    {
        if (!DynamicFormStatisticOperationContract
                .IsSupportedFieldType(fieldType))
        {
            throw Schema(
                $"{fieldPath}.fieldId",
                "FIELD_TYPE_STATISTIC_FORBIDDEN");
        }

        for (var index = 0;
             index < settings.AggregateOps!.Count;
             index++)
        {
            var operation = settings.AggregateOps[index];
            if (!DynamicFormStatisticOperationContract
                    .IsOperationAllowed(fieldType, operation))
            {
                throw OperationError(
                    $"{fieldPath}.statistic.aggregateOps[{index}]",
                    "FIELD_STATISTIC_OPERATION_INCOMPATIBLE",
                    fieldType,
                    operation);
            }
        }

        if (!DynamicFormStatisticOperationContract
                .IsBucketModeAllowed(
                    fieldType,
                    settings.BucketMode!))
        {
            throw BucketError(
                $"{fieldPath}.statistic.bucketMode",
                "FIELD_STATISTIC_BUCKET_MODE_INCOMPATIBLE",
                fieldType,
                settings.BucketMode);
        }
    }

    private static DynamicFormStatisticSettingsPayload
        NormalizeExistingSettings(
            DynamicFormStatisticSettingsPayload settings,
            string fieldType,
            string path)
    {
        if (settings.AggregateOps is null ||
            settings.AggregateOps.Count == 0)
        {
            throw Schema(
                $"{path}.aggregateOps",
                "AGGREGATE_OPS_REQUIRED");
        }
        if (!settings.ShowInDetail.HasValue)
        {
            throw Schema(
                $"{path}.showInDetail",
                "SHOW_IN_DETAIL_REQUIRED");
        }
        if (!settings.ShowInTree.HasValue)
        {
            throw Schema(
                $"{path}.showInTree",
                "SHOW_IN_TREE_REQUIRED");
        }

        var operations = settings.AggregateOps
            .Select(value => value?.Trim().ToUpperInvariant())
            .ToList();
        if (operations.Any(string.IsNullOrWhiteSpace) ||
            operations.Distinct(StringComparer.Ordinal).Count() !=
            operations.Count)
        {
            throw Schema(
                $"{path}.aggregateOps",
                "AGGREGATE_OPS_INVALID");
        }
        var bucketMode =
            settings.BucketMode?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(bucketMode))
            throw BucketError(
                $"{path}.bucketMode",
                "BUCKET_MODE_REQUIRED",
                fieldType,
                null);

        var normalized =
            new DynamicFormStatisticSettingsPayload(
                operations!,
                bucketMode,
                settings.ShowInDetail.Value,
                settings.ShowInTree.Value);
        ValidateStatisticSettings(
            fieldType,
            normalized,
            path.EndsWith(
                ".statistic",
                StringComparison.Ordinal)
                ? path[..^".statistic".Length]
                : path);
        return normalized;
    }

    private static List<string> NormalizeLabelCodes(
        IReadOnlyList<string>? values,
        string path)
    {
        if (values is null)
            return new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var code = LabelTaxonomyContract.NormalizeCode(
                values[index],
                $"{path}[{index}]");
            if (seen.Add(code))
                result.Add(code);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static JsonArray ParseFieldArray(string? json)
    {
        try
        {
            var node = JsonNode.Parse(json ?? "[]");
            return node as JsonArray ??
                   throw Schema(
                       "$.schema.fields",
                       "FIELDS_ARRAY_REQUIRED");
        }
        catch (JsonException)
        {
            throw Schema(
                "$.schema.fields",
                "FIELDS_JSON_INVALID");
        }
    }

    private static IReadOnlyDictionary<string, SchemaField>
        BuildSchemaFieldMap(JsonArray array)
    {
        var result =
            new Dictionary<string, SchemaField>(StringComparer.Ordinal);
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject field)
            {
                throw Schema(
                    $"$.schema.fields[{index}]",
                    "FIELD_OBJECT_REQUIRED");
            }
            var id = ReadString(field, "id") ??
                     ReadString(field, "key");
            if (string.IsNullOrWhiteSpace(id))
            {
                throw Schema(
                    $"$.schema.fields[{index}].id",
                    "FIELD_ID_REQUIRED");
            }
            if (!result.TryAdd(
                    id,
                    new SchemaField(
                        id,
                        ReadString(field, "type"),
                        index,
                        field)))
            {
                throw Schema(
                    $"$.schema.fields[{index}].id",
                    "DUPLICATE_SCHEMA_FIELD_ID");
            }
        }
        return result;
    }

    private static string MapFieldType(
        string? value,
        string path)
        => value switch
        {
            "number" =>
                DynamicFormStatisticFieldTypes.Number,
            "date" =>
                DynamicFormStatisticFieldTypes.Date,
            "fullDate" =>
                DynamicFormStatisticFieldTypes.FullDate,
            "boolean" =>
                DynamicFormStatisticFieldTypes.Boolean,
            "singleSelect" =>
                DynamicFormStatisticFieldTypes.SingleSelect,
            "multiSelect" =>
                DynamicFormStatisticFieldTypes.MultiSelect,
            "shortText" =>
                DynamicFormStatisticFieldTypes.ShortText,
            "longText" =>
                DynamicFormStatisticFieldTypes.LongText,
            "stringList" =>
                DynamicFormStatisticFieldTypes.StringList,
            "richText" =>
                throw Schema(path, "FIELD_TYPE_STATISTIC_FORBIDDEN"),
            _ => throw Schema(path, "FIELD_TYPE_UNSUPPORTED")
        };

    private static string ExpectedLabelDataType(string fieldType)
        => fieldType switch
        {
            DynamicFormStatisticFieldTypes.Number =>
                LabelDataTypes.Number,
            DynamicFormStatisticFieldTypes.Date or
                DynamicFormStatisticFieldTypes.FullDate =>
                LabelDataTypes.Date,
            DynamicFormStatisticFieldTypes.Boolean =>
                LabelDataTypes.Boolean,
            DynamicFormStatisticFieldTypes.LongText or
                DynamicFormStatisticFieldTypes.StringList =>
                LabelDataTypes.StringList,
            DynamicFormStatisticFieldTypes.ShortText or
                DynamicFormStatisticFieldTypes.SingleSelect or
                DynamicFormStatisticFieldTypes.MultiSelect =>
                LabelDataTypes.ShortText,
            _ => throw Schema(
                "$.payload.fields",
                "FIELD_TYPE_STATISTIC_FORBIDDEN")
        };

    private static string ComputeStructureHash(JsonObject field)
    {
        var structure = (JsonObject)field.DeepClone();
        foreach (var property in StatisticProperties)
            structure.Remove(property);
        return StatConfigCanonicalJson.HashUtf8(
            CanonicalNode(structure));
    }

    private static void ValidatePersistedFieldStructure(
        IReadOnlyList<PersistedFieldConfig> fields,
        IReadOnlyDictionary<string, SchemaField> schemaFields,
        string ownerId)
    {
        foreach (var field in fields)
        {
            if (!schemaFields.TryGetValue(
                    field.FieldId,
                    out var schemaField))
            {
                throw IntegrityConflict(
                    ownerId,
                    "CONFIGURED_FIELD_MISSING");
            }
            var currentType = MapFieldType(
                schemaField.Type,
                "$.fieldConfig");
            if (!string.Equals(
                    currentType,
                    field.FieldType,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    field.StructureHash,
                    ComputeStructureHash(schemaField.Node),
                    StringComparison.Ordinal))
            {
                throw IntegrityConflict(
                    ownerId,
                    "CONFIGURED_FIELD_STRUCTURE");
            }
            if (!field.IsStatistic ||
                field.Statistic is null)
            {
                throw IntegrityConflict(
                    ownerId,
                    "FIELD_SECTION_SCHEMA");
            }
            ValidateStatisticSettings(
                field.FieldType,
                field.Statistic,
                "$.fieldConfig");
        }
    }

    private static void EnsureUniqueLabelTargets(
        IEnumerable<PersistedFieldConfig> fields)
    {
        var targets =
            new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            foreach (var code in field.StatisticLabelCodes)
            {
                if (targets.TryGetValue(code, out var current) &&
                    !string.Equals(
                        current,
                        field.FieldId,
                        StringComparison.Ordinal))
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode
                            .DYNAMIC_FORM_LABEL_STATISTIC_TARGET_CONFLICT,
                        new
                        {
                            path = "$.payload.fields",
                            reason =
                                "FIELD_STATISTIC_LABEL_TARGET_CONFLICT",
                            code,
                            firstFieldId = current,
                            secondFieldId = field.FieldId
                        });
                }
                targets[code] = field.FieldId;
            }
        }
    }

    private static List<string> BuildDependencyPins(
        IEnumerable<PersistedFieldConfig> fields)
        => fields
            .SelectMany(field => field.LabelSnapshots)
            .Select(label =>
                $"LABEL:{label.LabelId}:{label.VersionId}:" +
                $"{label.VersionNo}:{label.ConfigHash}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(pin => pin, StringComparer.Ordinal)
            .ToList();

    private static string CanonicalSectionJson(
        string? json,
        string path)
    {
        try
        {
            using var document =
                JsonDocument.Parse(json ?? "[]");
            if (document.RootElement.ValueKind !=
                JsonValueKind.Array)
            {
                throw Schema(path, "SECTION_ARRAY_REQUIRED");
            }
            return StatConfigCanonicalJson.CanonicalizeElement(
                document.RootElement);
        }
        catch (JsonException)
        {
            throw Schema(path, "SECTION_JSON_INVALID");
        }
    }

    private static List<PersistedFieldConfig>
        DeserializeFieldSection(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<
                       List<PersistedFieldConfig>>(
                       json,
                       StatConfigCanonicalJson.StrictJsonOptions) ??
                   new List<PersistedFieldConfig>();
        }
        catch (JsonException)
        {
            throw Schema(
                "$.fieldConfig",
                "FIELD_SECTION_SCHEMA_INVALID");
        }
    }

    private static void PersistNextState(
        DynamicFormTemplate owner,
        ConfigState current,
        ConfigState next,
        string actorUserId,
        DateTime now)
    {
        owner.StatisticConfigSnapshots ??= new();
        if (owner.StatisticConfigSnapshots.Count == 0)
        {
            owner.StatisticConfigSnapshots.Add(
                CreateSnapshot(
                    current,
                    owner.CreatedAtUtc,
                    owner.CreatedByUserId));
        }

        owner.FieldsJson = next.FieldsJson;
        owner.BlocksJson = next.BlocksJson;
        if (DynamicFormNativeTableDefinition.IsNative(owner)) owner.TablesJson = next.NativeTablesJson;
        owner.StatisticConfigId = next.ConfigId;
        owner.StatisticConfigPreviousVersionId =
            next.PreviousVersionId;
        owner.StatisticConfigVersionId = next.VersionId;
        owner.StatisticConfigVersionNo = next.VersionNo;
        owner.StatisticConfigRevision = next.Revision;
        owner.StatisticConfigStatus = next.Status;
        owner.StatisticConfigHash = next.ConfigHash;
        owner.StatisticConfigDependencyPins =
            next.DependencyPins.ToList();
        owner.StatisticConfigSections =
            new DynamicFormStatisticConfigSections
            {
                FieldSectionJson = next.FieldSectionJson,
                TableSectionJson = next.TableSectionJson,
                NativeTargetSectionJson = next.NativeTargetSectionJson,
                NativePlanSectionJson = next.NativePlanSectionJson
            };
        owner.StatisticConfigSnapshots.Add(
            CreateSnapshot(next, now, actorUserId));

        owner.StatisticConfigUpdatedAtUtc = now;
        owner.StatisticConfigUpdatedByUserId = actorUserId;
        owner.StatisticConfigUpdateMonthKey =
            now.ToString("yyyy-MM");
        owner.UpdatedAtUtc = now;
        owner.UpdatedByUserId = actorUserId;
        owner.Revision = checked(owner.Revision + 1);
    }

    private static DynamicFormStatisticConfigVersionSnapshot
        CreateSnapshot(
            ConfigState state,
            DateTime createdAtUtc,
            string? actorUserId)
        => new()
        {
            VersionId = state.VersionId,
            PreviousVersionId = state.PreviousVersionId,
            VersionNo = state.VersionNo,
            Revision = state.Revision,
            Status = state.Status,
            ConfigHash = state.ConfigHash,
            DependencyPins = state.DependencyPins.ToList(),
            Sections = new DynamicFormStatisticConfigSections
            {
                FieldSectionJson = state.FieldSectionJson,
                TableSectionJson = state.TableSectionJson,
                NativeTargetSectionJson = state.NativeTargetSectionJson,
                NativePlanSectionJson = state.NativePlanSectionJson
            },
            CreatedAtUtc = createdAtUtc,
            CreatedByUserId = actorUserId
        };

    private static DynamicFormStatisticConfigResult ToResult(
        DynamicFormTemplate owner,
        ConfigState state,
        MeResponse me,
        string? receiptId)
    {
        var canManage =
            RoleGuard.IsSystemAdmin(me) ||
            string.Equals(
                owner.CreatedByUserId,
                me.Id,
                StringComparison.Ordinal);
        var snapshots =
            owner.StatisticConfigSnapshots is { Count: > 0 }
                ? owner.StatisticConfigSnapshots
                    .OrderBy(
                        snapshot => snapshot.VersionNo)
                    .Select(snapshot => ToSnapshotDto(owner.Id, snapshot))
                    .ToList()
                : new List<
                    DynamicFormStatisticConfigVersionSnapshotDto>
                {
                    ToSnapshotDto(owner.Id,
                        CreateSnapshot(
                            state,
                            owner.CreatedAtUtc,
                            owner.CreatedByUserId))
                };
        return new DynamicFormStatisticConfigResult(
            StatConfigOwnerKinds.DynamicForm,
            owner.Id,
            state.ConfigId,
            state.VersionId,
            state.VersionNo,
            state.Revision,
            state.Status,
            state.ConfigHash,
            state.DependencyPins,
            new StatConfigPermissionSet(
                CanReadConfig: true,
                CanManageDraft: canManage,
                CanLockVersion: false,
                CanViewResult: false,
                CanReadDiagnostics: false),
            state.Fields.Select(ToFieldDto).ToList(),
            state.Tables.Select(ToTableDto).ToList(),
            StatConfigCanonicalJson.HashUtf8(
                state.FieldSectionJson),
            StatConfigCanonicalJson.HashUtf8(
                state.TableSectionJson),
            snapshots,
            receiptId)
        {
            NativeTargetConfig = DeserializeNativeSection(state.NativeTargetSectionJson),
            NativeTargetSectionHash = state.NativeTargetSectionJson is null ? null : StatConfigCanonicalJson.HashUtf8(state.NativeTargetSectionJson),
            NativePlanConfig = DeserializeNativePlanSection(state.NativePlanSectionJson),
            NativePlanSectionHash = state.NativePlanSectionJson is null ? null : StatConfigCanonicalJson.HashUtf8(state.NativePlanSectionJson)
        };
    }

    private static DynamicFormStatisticConfigVersionSnapshotDto
        ToSnapshotDto(
            string ownerId,
            DynamicFormStatisticConfigVersionSnapshot snapshot)
    {
        var sections = snapshot.Sections ??
                       new DynamicFormStatisticConfigSections();
        var fieldSectionJson = CanonicalSectionJson(
            sections.FieldSectionJson,
            "$.versions.fields");
        var tableSectionJson = CanonicalSectionJson(
            sections.TableSectionJson,
            "$.versions.tableConfig");
        // Native history before its first v2 plan still carries a native section
        // and hash. Verify those snapshots too; do not silently trust their pins.
        if (sections.NativeTargetSectionJson is not null || sections.NativePlanSectionJson is not null)
        {
            var historicalFields = DeserializeFieldSection(fieldSectionJson);
            var historicalTables = DeserializeTableSection(tableSectionJson);
            var nativeJson = sections.NativeTargetSectionJson ?? throw IntegrityConflict(ownerId, "NATIVE_HISTORY_SECTION_REQUIRED");
            var planJson = sections.NativePlanSectionJson is null ? null
                : StatConfigCanonicalJson.Canonicalize(DeserializeNativePlanSection(sections.NativePlanSectionJson));
            var pins = NativeDependencyPins(BuildDependencyPins(historicalFields, historicalTables), nativeJson, planJson);
            ValidateNativeTargetLimits(historicalFields, historicalTables, nativeJson, planJson);
            if (!(snapshot.DependencyPins ?? new()).SequenceEqual(pins, StringComparer.Ordinal)
                || snapshot.ConfigHash != ComputeConfigHash(ownerId, fieldSectionJson, tableSectionJson, pins, nativeJson, planJson))
                throw IntegrityConflict(ownerId, "NATIVE_HISTORY_HASH_OR_PINS");
        }
        return new DynamicFormStatisticConfigVersionSnapshotDto(
            snapshot.VersionId,
            snapshot.PreviousVersionId,
            snapshot.VersionNo,
            snapshot.Revision,
            snapshot.Status,
            snapshot.ConfigHash,
            snapshot.DependencyPins ?? new List<string>(),
            DeserializeFieldSection(fieldSectionJson)
                .Select(ToFieldDto)
                .ToList(),
            DeserializeTableSection(tableSectionJson)
                .Select(ToTableDto)
                .ToList(),
            StatConfigCanonicalJson.HashUtf8(fieldSectionJson),
            StatConfigCanonicalJson.HashUtf8(tableSectionJson),
            snapshot.CreatedAtUtc)
        {
            NativeTargetConfig = DeserializeNativeSection(sections.NativeTargetSectionJson),
            NativeTargetSectionHash = sections.NativeTargetSectionJson is null ? null : StatConfigCanonicalJson.HashUtf8(sections.NativeTargetSectionJson),
            NativePlanConfig = DeserializeNativePlanSection(sections.NativePlanSectionJson),
            NativePlanSectionHash = sections.NativePlanSectionJson is null ? null : StatConfigCanonicalJson.HashUtf8(sections.NativePlanSectionJson)
        };
    }

    private static DynamicFormStatisticFieldConfigDto ToFieldDto(
        PersistedFieldConfig field)
        => new(
            field.FieldId,
            field.FieldType,
            field.IsStatistic,
            field.Statistic,
            field.StatisticLabelCodes,
            field.LabelSnapshots,
            field.StructureHash);

    private async Task<DynamicFormStatisticConfigResult?>
        LoadReplayAsync(
            IClientSessionHandle session,
            string receiptId,
            string requestHash,
            CancellationToken ct)
    {
        var receipt = await _ctx.StatConfigCommandReceipts
            .Find(session, item => item.Id == receiptId)
            .FirstOrDefaultAsync(ct);
        return receipt is null
            ? null
            : RestoreReplay(receipt, requestHash);
    }

    private static DynamicFormStatisticConfigResult RestoreReplay(
        StatConfigCommandReceipt receipt,
        string requestHash)
    {
        if (!string.Equals(
                receipt.RequestHash,
                requestHash,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_COMMAND_REPLAY_CONFLICT,
                new
                {
                    ownerKind = receipt.OwnerKind,
                    ownerId = receipt.OwnerId,
                    commandId = receipt.CommandId
                });
        }
        if (!string.Equals(
                receipt.ResponseHash,
                StatConfigCanonicalJson.HashUtf8(
                    receipt.ResponseJson),
                StringComparison.Ordinal))
        {
            throw IntegrityConflict(
                receipt.OwnerId,
                "RECEIPT_RESPONSE_HASH");
        }

        try
        {
            return JsonSerializer.Deserialize<
                       DynamicFormStatisticConfigResult>(
                       receipt.ResponseJson,
                       StatConfigCanonicalJson.StrictJsonOptions) ??
                   throw new JsonException();
        }
        catch (JsonException)
        {
            throw IntegrityConflict(
                receipt.OwnerId,
                "RECEIPT_RESPONSE_SCHEMA");
        }
    }

    private static StatConfigCommandReceipt CreateReceipt(
        string receiptId,
        NormalizedStatConfigCommand<
            DynamicFormStatisticConfigPayload> command,
        ConfigState state,
        DynamicFormStatisticConfigResult result,
        string actorUserId,
        DateTime createdAtUtc)
    {
        var responseJson =
            StatConfigCanonicalJson.Canonicalize(result);
        return new StatConfigCommandReceipt
        {
            Id = receiptId,
            OwnerKind = StatConfigOwnerKinds.DynamicForm,
            OwnerId = result.OwnerId,
            CommandKind = CommandKind,
            CommandId = command.CommandId,
            RequestHash = command.RequestHash,
            ResponseJson = responseJson,
            ResponseHash =
                StatConfigCanonicalJson.HashUtf8(responseJson),
            ResultConfigId = state.ConfigId,
            ResultVersionId = state.VersionId,
            ResultVersionNo = state.VersionNo,
            ResultRevision = state.Revision,
            ResultStatus = state.Status,
            ResultConfigHash = state.ConfigHash,
            ActorUserId = actorUserId,
            CreatedAtUtc = createdAtUtc
        };
    }

    private static DateTime UtcNowAtMillisecondPrecision()
    {
        var now = DateTime.UtcNow;
        return new DateTime(
            now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
    }

    private static void EnsureCas(
        ConfigState state,
        NormalizedStatConfigCommand<
            DynamicFormStatisticConfigPayload> command)
    {
        if (state.Revision != command.ExpectedRevision ||
            !string.Equals(
                state.ConfigHash,
                command.ExpectedConfigHash,
                StringComparison.Ordinal))
        {
            throw CasConflict(command, state);
        }
    }

    private static AppException CasConflict(
        NormalizedStatConfigCommand<
            DynamicFormStatisticConfigPayload> command,
        ConfigState state)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                expectedRevision = command.ExpectedRevision,
                expectedConfigHash =
                    command.ExpectedConfigHash,
                actualRevision = state.Revision,
                actualConfigHash = state.ConfigHash
            });

    private static string ComputeConfigHash(
        string ownerId,
        string fieldSectionJson,
        string tableSectionJson,
        IReadOnlyList<string> dependencyPins,
        string? nativeTargetSectionJson = null,
        string? nativePlanSectionJson = null)
    {
        var fields = ParseElement(fieldSectionJson);
        var tables = ParseElement(tableSectionJson);
        if (nativePlanSectionJson is not null)
            return StatConfigCanonicalJson.HashObject(new
            {
                ownerKind = StatConfigOwnerKinds.DynamicForm, ownerId,
                fieldConfig = fields, tableConfig = tables, nativeTargetsVersion = 1,
                nativeTargetConfig = ParseElement(nativeTargetSectionJson ?? throw IntegrityConflict(ownerId, "NATIVE_SECTION_REQUIRED")),
                nativePlanVersion = 2, nativePlanConfig = ParseElement(nativePlanSectionJson), dependencyPins
            });
        if (nativeTargetSectionJson is not null)
            return StatConfigCanonicalJson.HashObject(new
            {
                ownerKind = StatConfigOwnerKinds.DynamicForm, ownerId,
                fieldConfig = fields, tableConfig = tables,
                nativeTargetsVersion = 1,
                nativeTargetConfig = ParseElement(nativeTargetSectionJson), dependencyPins
            });
        return StatConfigCanonicalJson.HashObject(new
        {
            ownerKind = StatConfigOwnerKinds.DynamicForm,
            ownerId,
            fieldConfig = fields,
            tableConfig = tables,
            dependencyPins
        });
    }

    private static string CanonicalNode(JsonNode node)
    {
        using var document =
            JsonDocument.Parse(node.ToJsonString());
        return StatConfigCanonicalJson.CanonicalizeElement(
            document.RootElement);
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static FilterDefinition<DynamicFormTemplate>
        BuildOwnerFilter(
            string id,
            MeResponse me)
    {
        var filter = Builders<DynamicFormTemplate>.Filter;
        var result =
            filter.Eq(owner => owner.Id, id) &
            filter.Eq(owner => owner.IsDeleted, false);
        if (!RoleGuard.IsSystemAdmin(me))
        {
            result &= filter.Eq(
                owner => owner.CreatedByUserId,
                me.Id);
        }
        return result;
    }

    private static FilterDefinition<DynamicFormTemplate>
        BuildOwnerCasFilter(
            string id,
            int ownerRevision,
            ConfigState state,
            bool hadPersistedIdentity)
    {
        var filter = Builders<DynamicFormTemplate>.Filter;
        var result =
            filter.Eq(owner => owner.Id, id) &
            filter.Eq(owner => owner.IsDeleted, false) &
            filter.Eq(owner => owner.Revision, ownerRevision);
        if (hadPersistedIdentity)
        {
            result &=
                filter.Eq(
                    owner => owner.StatisticConfigRevision,
                    state.Revision) &
                filter.Eq(
                    owner => owner.StatisticConfigHash,
                    state.ConfigHash);
        }
        else
        {
            result &= filter.Eq(
                owner => owner.StatisticConfigHash,
                null);
        }
        return result;
    }

    private static FilterDefinition<LabelCatalogItem>
        BuildLabelVisibilityFilter(MeResponse me)
    {
        var filter = Builders<LabelCatalogItem>.Filter;
        if (RoleGuard.IsSystemAdmin(me))
            return FilterDefinition<LabelCatalogItem>.Empty;

        var scopes =
            new List<FilterDefinition<LabelCatalogItem>>
            {
                filter.Eq(
                    label => label.ScopeType,
                    LabelScopeTypes.Global)
            };
        if (!string.IsNullOrWhiteSpace(me.UnitId))
        {
            scopes.Add(
                filter.Eq(
                    label => label.ScopeType,
                    LabelScopeTypes.Unit) &
                filter.Eq(
                    label => label.ScopeId,
                    me.UnitId));
        }
        if (RoleGuard.TryGetManagerUnit(
                me,
                out var managedUnitId))
        {
            scopes.Add(
                filter.Eq(
                    label => label.ScopeType,
                    LabelScopeTypes.Unit) &
                filter.Eq(
                    label => label.ScopeId,
                    managedUnitId));
        }
        if (RoleGuard.IsManagerLevel(me) &&
            !string.IsNullOrWhiteSpace(me.UnitId))
        {
            scopes.Add(
                filter.Eq(
                    label => label.ScopeType,
                    LabelScopeTypes.Level) &
                filter.Eq(
                    label => label.ScopeId,
                    me.UnitId));
        }
        return filter.Or(scopes);
    }

    private static string NormalizeOwnerId(string? value)
    {
        var normalized = value?.Trim();
        if (!ObjectId.TryParse(normalized, out _))
            throw OwnerNotFound(normalized ?? string.Empty);
        return normalized!;
    }

    private static string ReceiptId(
        string ownerId,
        string commandId)
        => StatConfigCanonicalJson.HashUtf8(
            $"{StatConfigOwnerKinds.DynamicForm}\0" +
            $"{ownerId}\0{commandId}");

    private static string CurrentStatus(
        DynamicFormTemplate owner)
        => owner.IsPublished
            ? StatConfigStatuses.Locked
            : StatConfigStatuses.Draft;

    private static string? ReadString(
        JsonObject owner,
        string propertyName)
        => owner[propertyName] is JsonValue value &&
           value.TryGetValue<string>(out var text)
            ? text?.Trim()
            : null;

    private static bool ReadBoolean(
        JsonObject owner,
        string propertyName)
        => owner[propertyName] is JsonValue value &&
           value.TryGetValue<bool>(out var result) &&
           result;

    private static IReadOnlyList<string> ReadStringArray(
        JsonNode? node)
    {
        if (node is null)
            return Array.Empty<string>();
        if (node is not JsonArray array)
        {
            throw Schema(
                "$.schema.fields.statisticLabelCodes",
                "STRING_ARRAY_REQUIRED");
        }
        var values = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value ||
                !value.TryGetValue<string>(out var text))
            {
                throw Schema(
                    "$.schema.fields.statisticLabelCodes",
                    "STRING_ARRAY_REQUIRED");
            }
            values.Add(text);
        }
        return values;
    }

    private static AppException OwnerNotFound(string ownerId)
        => AppExceptionFactory.NotFound(
            AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
            new
            {
                ownerKind = StatConfigOwnerKinds.DynamicForm,
                ownerId
            });

    private static AppException Schema(
        string path,
        string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode
                .DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID,
            new { path, reason });

    private static AppException OperationError(
        string path,
        string reason,
        string? fieldType,
        string? operation)
        => AppExceptionFactory.BadRequest(
            AppErrorCode
                .DYNAMIC_FORM_STATISTIC_OPERATION_INVALID,
            new
            {
                path,
                reason,
                fieldType,
                operation,
                allowedOperations =
                    fieldType is not null &&
                    DynamicFormStatisticOperationContract
                        .AllowedOperationsByFieldType
                        .TryGetValue(fieldType, out var allowed)
                        ? allowed.OrderBy(
                                value => value,
                                StringComparer.Ordinal)
                            .ToArray()
                        : Array.Empty<string>()
            });

    private static AppException BucketError(
        string path,
        string reason,
        string? fieldType,
        string? bucketMode)
        => AppExceptionFactory.BadRequest(
            AppErrorCode
                .DYNAMIC_FORM_STATISTIC_BUCKET_MODE_INVALID,
            new
            {
                path,
                reason,
                fieldType,
                bucketMode,
                allowedBucketModes = new[]
                {
                    DynamicFormStatisticBucketModes.None,
                    DynamicFormStatisticBucketModes.Option,
                    DynamicFormStatisticBucketModes.Date
                }
            });

    private static AppException IntegrityConflict(
        string ownerId,
        string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                ownerKind = StatConfigOwnerKinds.DynamicForm,
                ownerId,
                reason = $"DYNAMIC_FORM_STATISTIC_{reason}_INTEGRITY"
            });

    private sealed record NormalizedMutation(
        string FieldId,
        bool IsStatistic,
        DynamicFormStatisticSettingsPayload? Statistic,
        IReadOnlyList<string> StatisticLabelCodes,
        int InputIndex);

    private sealed record PersistedFieldConfig(
        string FieldId,
        string FieldType,
        bool IsStatistic,
        DynamicFormStatisticSettingsPayload? Statistic,
        IReadOnlyList<string> StatisticLabelCodes,
        IReadOnlyList<DynamicFormStatisticLabelSnapshotDto>
            LabelSnapshots,
        string StructureHash);

    private sealed record ConfigState(
        string ConfigId,
        string VersionId,
        string? PreviousVersionId,
        int VersionNo,
        long Revision,
        string Status,
        string ConfigHash,
        IReadOnlyList<string> DependencyPins,
        string FieldSectionJson,
        string TableSectionJson,
        IReadOnlyList<PersistedFieldConfig> Fields,
        IReadOnlyList<PersistedTableConfig> Tables,
        string FieldsJson,
        string BlocksJson,
        bool IsVirtual)
    {
        public string? NativeTargetSectionJson { get; init; }
        public string? NativePlanSectionJson { get; init; }
        public string? NativeTablesJson { get; init; }
    }

    private sealed record SchemaField(
        string Id,
        string? Type,
        int Index,
        JsonObject Node);
}
