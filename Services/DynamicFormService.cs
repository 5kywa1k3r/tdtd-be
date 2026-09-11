using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicExcel;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services;

public interface IDynamicFormService
{
    Task<PagedResult<DynamicFormRow>> SearchAsync(DynamicFormSearchReq req, CancellationToken ct);
    Task<DynamicFormDetail> GetByIdAsync(string id, CancellationToken ct);
    Task<NextCodeResp> GetNextCodeAsync(int? year, CancellationToken ct);
    Task<DynamicFormDetail> CreateAsync(CreateDynamicFormReq req, CancellationToken ct);
    Task<DynamicFormVersionHistoryResp> GetVersionHistoryAsync(string id, CancellationToken ct);
    Task<DynamicFormDetail> CreateNextVersionAsync(
        string id,
        CreateDynamicFormVersionReq req,
        CancellationToken ct);
    Task<DynamicFormDetail> UpdateAsync(string id, UpdateDynamicFormReq req, CancellationToken ct);
    Task<DynamicFormStatisticConfigResult> GetStatisticsAsync(
        string id,
        CancellationToken ct);
    Task<DynamicFormStatisticConfigResult> PatchStatisticsAsync(
        string id,
        JsonElement body,
        CancellationToken ct);
    Task<DynamicFormStatisticConfigResult> GetStatisticConfigAsync(
        string id,
        CancellationToken ct);
    Task<DynamicFormStatisticConfigResult> UpdateStatisticConfigAsync(
        string id,
        JsonElement body,
        CancellationToken ct);
    Task<DynamicFormDetail> PublishAsync(string id, PublishDynamicFormReq? req, CancellationToken ct);
    Task<DynamicFormDetail> CloneAsync(string id, CloneDynamicFormReq req, CancellationToken ct);
    Task<DynamicFormDetail> WrapDynamicExcelAsync(WrapDynamicExcelAsFormReq req, CancellationToken ct);
    Task<DynamicFormDetail> ImportDynamicExcelBlockAsync(string id, ImportDynamicExcelBlockReq req, CancellationToken ct);
    Task DeleteAsync(string id, int? expectedRevision, CancellationToken ct);
}

public sealed class DynamicFormService : IDynamicFormService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex LabelCodeRegex = new("^[a-z0-9][a-z0-9_.-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex MetricKeyRegex = new("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,255}$", RegexOptions.Compiled);
    private static readonly Regex GenericFieldDisplayNameRegex = new(
        "^(field|truong|number|date|full\\s*date|fulldate|short\\s*text|shorttext|long\\s*text|longtext|boolean|single\\s*select|singleselect|multi\\s*select|multiselect|so|ngay|ngay\\s*day\\s*du|van\\s*ban\\s*ngan|van\\s*ban\\s*dai|chon\\s*mot|chon\\s*nhieu|co\\s*khong)[\\s_-]*\\d*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> GenericFieldDisplayNames = new(StringComparer.Ordinal)
    {
        "shorttext",
        "longtext",
        "number",
        "date",
        "fulldate",
        "singleselect",
        "multiselect",
        "boolean",
        "short text",
        "long text",
        "single select",
        "multi select",
        "van ban ngan",
        "van ban dai",
        "so",
        "ngay",
        "ngay day du",
        "chon mot",
        "chon nhieu",
        "co/khong",
        "co khong"
    };
    private const int MaxFieldsPerForm = 200;
    private const int MaxOptionsPerSelectField = 100;
    private const int MaxTableBlocksPerForm = 30;
    private const int MaxLabelStatisticTargetsPerForm = 30;
    private const int MaxSchemaPayloadBytes = 1024 * 1024;
    private const int MaxSearchTermLength = 200;
    private const int MaxSearchOffset = 100_000;
    private static readonly HashSet<string> AllowedFieldTypes = new(StringComparer.Ordinal)
    {
        "shortText",
        "longText",
        "richText",
        "stringList",
        "number",
        "date",
        "fullDate",
        "singleSelect",
        "multiSelect",
        "boolean"
    };
    private static readonly HashSet<string> ChoiceFieldTypes = new(StringComparer.Ordinal)
    {
        "shortText",
        "singleSelect",
        "multiSelect"
    };
    private static readonly HashSet<string> AllowedFieldValueSourceTypes = new(StringComparer.Ordinal)
    {
        LabelValueSourceTypes.FixedEnum,
        LabelValueSourceTypes.EnumCatalog,
        LabelValueSourceTypes.SystemUnit,
        LabelValueSourceTypes.SystemUser,
        LabelValueSourceTypes.SystemPosition,
        LabelValueSourceTypes.SystemUnitType
    };
    private static readonly HashSet<string> AllowedTableModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "FIXED_GRID",
        "APPEND_ROWS",
        "APPEND_COLUMNS",
        "MATRIX",
        "SUMMARY_TEMPLATE"
    };
    private static readonly HashSet<string> AllowedExcelSpecKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "TOP",
        "LEFT",
        "MATRIX"
    };
    private static readonly HashSet<string> AllowedSummaryTemplateGroupBy = new(StringComparer.OrdinalIgnoreCase)
    {
        "UNIT",
        "ASSIGNMENT",
        "ROOT_ASSIGNMENT",
        "USER",
        "LABEL",
        "PERIOD"
    };
    private static readonly HashSet<string> AllowedSummaryTemplateRepeatFor = new(StringComparer.Ordinal)
    {
        "selectedUnits",
        "scopeAssignments",
        "none"
    };

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly IDynamicFormStatisticConfigCommandService _statisticConfig;
    private readonly ILabelEnumCatalogService _enumCatalogs;

    public DynamicFormService(
        MongoDbContext ctx,
        MeAccessor me,
        IDynamicFormStatisticConfigCommandService statisticConfig,
        ILabelEnumCatalogService enumCatalogs)
    {
        _ctx = ctx;
        _me = me;
        _statisticConfig = statisticConfig;
        _enumCatalogs = enumCatalogs;
    }

    public async Task<NextCodeResp> GetNextCodeAsync(int? year, CancellationToken ct)
    {
        var y = year ?? DateTime.UtcNow.Year;
        var (prefix, nextSeq, nextCode) = await ComputeNextCodeAsync(y, ct);
        return new NextCodeResp(prefix, y, nextSeq, nextCode);
    }

    public async Task<PagedResult<DynamicFormRow>> SearchAsync(DynamicFormSearchReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var page = Math.Max(0, req.Page);
        var pageSize = Math.Clamp(req.PageSize, 1, 100);
        var searchOffset = (long)page * pageSize;
        if (searchOffset > MaxSearchOffset)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    reason = "DYNAMIC_FORM_SEARCH_OFFSET_TOO_LARGE",
                    page,
                    pageSize,
                    maxOffset = MaxSearchOffset
                });
        }

        var f = Builders<DynamicFormTemplate>.Filter;
        var filter = f.Eq(x => x.IsDeleted, false);
        var approvedIds = await LoadApprovedCloneTemplateIdsAsync(me.Id, ct);
        filter &= BuildVisibleFilter(me, approvedIds);

        if (!string.IsNullOrWhiteSpace(req.Code))
        {
            filter &= f.Regex("code", BuildLiteralSearchRegex(req.Code, nameof(req.Code)));
        }

        if (!string.IsNullOrWhiteSpace(req.Name))
        {
            filter &= f.Regex("name", BuildLiteralSearchRegex(req.Name, nameof(req.Name)));
        }

        if (!string.IsNullOrWhiteSpace(req.CreatedBy))
        {
            filter &= f.Regex(
                "createdByUsername",
                BuildLiteralSearchRegex(req.CreatedBy, nameof(req.CreatedBy)));
        }

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var rx = BuildLiteralSearchRegex(req.Q, nameof(req.Q));
            filter &= f.Regex("code", rx) | f.Regex("name", rx);
        }

        if (req.CreatedFromUtc.HasValue)
            filter &= f.Gte(x => x.CreatedAtUtc, req.CreatedFromUtc.Value);

        if (req.CreatedToUtc.HasValue)
            filter &= f.Lte(x => x.CreatedAtUtc, req.CreatedToUtc.Value);

        var tagFilters = NormalizeLabelCodes(req.TagCodes);
        if (tagFilters.Length > 0)
            filter &= f.In("tagCodes", tagFilters);

        if (req.IsActive.HasValue)
            filter &= f.Eq(x => x.IsActive, req.IsActive.Value);

        if (req.IsPublished.HasValue)
            filter &= f.Eq(x => x.IsPublished, req.IsPublished.Value);

        var total = await _ctx.DynamicFormTemplates.CountDocumentsAsync(filter, cancellationToken: ct);
        var sort = BuildSort(req.SortField, req.SortDirection);

        var docs = await _ctx.DynamicFormTemplates
            .Find(filter)
            .Sort(sort)
            .Skip((int)searchOffset)
            .Limit(pageSize)
            .ToListAsync(ct);

        var items = docs.Select(x => ToRow(x, me, approvedIds)).ToList();
        return new PagedResult<DynamicFormRow>(items, total, page, pageSize);
    }

    public async Task<DynamicFormDetail> GetByIdAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var doc = await LoadAsync(id, ct);
        await RequireCanReadAsync(me, doc, ct);
        return await ToDetailAsync(doc, me, ct);
    }

    public async Task<DynamicFormVersionHistoryResp> GetVersionHistoryAsync(
        string id,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var source = await LoadAsync(id, ct);
        RequireCanReadVersionHistory(me, source);

        var familyId = EffectiveFamilyId(source);
        var versions = await _ctx.DynamicFormTemplates
            .Find(BuildFamilyFilter(familyId))
            .SortByDescending(x => x.VersionNo)
            .ThenByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        var rows = versions
            .Select(x => ToRow(x, me, new HashSet<string>(StringComparer.Ordinal)))
            .ToArray();
        return new DynamicFormVersionHistoryResp(familyId, source.Code, rows);
    }

    public async Task<DynamicFormDetail> CreateNextVersionAsync(
        string id,
        CreateDynamicFormVersionReq req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var source = await LoadAsync(id, ct);
        RequireCanMutate(me, source);
        var expectedRevision = RequireExpectedRevision(req.ExpectedRevision, source);
        if (!source.IsPublished)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FORM_PUBLISHED_REQUIRED,
                DynamicFormDetails(source, me.Id));
        }

        _ = RequirePublishedSchemaIntegrity(source);

        var familyId = EffectiveFamilyId(source);
        var latest = await _ctx.DynamicFormTemplates
            .Find(BuildFamilyFilter(familyId))
            .SortByDescending(x => x.VersionNo)
            .ThenByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (latest is null ||
            !string.Equals(latest.Id, source.Id, StringComparison.Ordinal) ||
            !latest.IsPublished)
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FORM_VERSION_CONFLICT,
                new
                {
                    familyId,
                    sourceVersionId = source.Id,
                    sourceRevision = expectedRevision,
                    latestVersionId = latest?.Id,
                    latestVersionNo = latest?.VersionNo,
                    latestIsPublished = latest?.IsPublished,
                    reason = latest is not null && !latest.IsPublished
                        ? "DYNAMIC_FORM_VERSION_DRAFT_EXISTS"
                        : "DYNAMIC_FORM_VERSION_SOURCE_NOT_LATEST"
                });
        }

        EnsureFieldsLimit(source.FieldsJson);
        EnsureFieldDisplayNames(source.FieldsJson);
        EnsureFieldSchemaContract(source.FieldsJson, source.SectionsJson);
        var retainedDynamicExcelTemplateIds = ExtractDynamicExcelTemplateIds(source.BlocksJson, source.ExcelBlockJson);
        var blocksJson = NormalizeBlocksJson(source.BlocksJson, source.ExcelBlockJson);
        blocksJson = NormalizeBlocksForSections(blocksJson, source.SectionsJson);
        blocksJson = await NormalizeBlocksForDynamicExcelTemplatesAsync(
            me,
            blocksJson,
            ct,
            retainedDynamicExcelTemplateIds);
        blocksJson = CarryForwardTableStatisticMetadata(
            source.BlocksJson,
            blocksJson);
        var excelBlockJson = ExtractFirstBlockJson(blocksJson);
        EnsureTableStatisticContract(excelBlockJson, "ExcelBlockJson");
        EnsureBlocksTableStatisticContract(blocksJson, "BlocksJson");
        EnsureSchemaPayloadBudget(source.SectionsJson, source.FieldsJson, blocksJson);
        EnsureTypedSchemaProjection(
            source.SectionsJson,
            source.FieldsJson,
            excelBlockJson,
            blocksJson);

        var now = DateTime.UtcNow;
        var versionId = ObjectId.GenerateNewId().ToString();
        var next = new DynamicFormTemplate
        {
            Id = versionId,
            Code = source.Code,
            Name = string.IsNullOrWhiteSpace(req.Name) ? source.Name : NormalizeName(req.Name),
            Description = req.Description is null ? source.Description : NormalizeOptionalText(req.Description),
            TagCodes = source.TagCodes,
            // A version remains owned by the same form family. The actor can be an
            // administrator, but creating a successor must not silently transfer
            // owner permissions away from the family owner.
            CreatedByUsername = source.CreatedByUsername,
            SchemaVersion = source.SchemaVersion,
            VersionNo = Math.Max(1, source.VersionNo) + 1,
            FamilyId = familyId,
            PreviousVersionId = source.Id,
            LineageStatus = DynamicFormLineageStatuses.Version,
            Revision = 1,
            IsActive = true,
            IsPublished = false,
            SectionsJson = source.SectionsJson,
            FieldsJson = source.FieldsJson,
            ExcelBlockJson = excelBlockJson,
            BlocksJson = blocksJson,
            ExcelBlockDynamicExcelTemplateId =
                source.ExcelBlockDynamicExcelTemplateId
                ?? ExtractPrimaryBlockDynamicExcelTemplateId(excelBlockJson, blocksJson),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = source.CreatedByUserId,
            UpdatedByUserId = me.Id,
            IsDeleted = false,
        };

        await ReserveNextVersionAsync(source, expectedRevision, me.Id, now, ct);
        await InsertDynamicFormVersionAsync(next, source, expectedRevision, ct);
        return await ToDetailAsync(next, me, ct);
    }

    public async Task<DynamicFormDetail> CreateAsync(CreateDynamicFormReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var schemaInput = DynamicFormSchemaAdapter.ResolveInput(
            req.Schema,
            req.SectionsJson,
            req.FieldsJson,
            req.ExcelBlockJson,
            req.BlocksJson);
        EnsureTypedSchemaProjection(
            schemaInput.SectionsJson,
            schemaInput.FieldsJson,
            schemaInput.ExcelBlockJson,
            schemaInput.BlocksJson);
        var now = DateTime.UtcNow;
        var code = NormalizeCode(req.Code);
        if (string.IsNullOrWhiteSpace(code))
        {
            var (_, _, nextCode) = await ComputeNextCodeAsync(now.Year, ct);
            code = nextCode;
        }

        var tagCodes = NormalizeLabelCodes(req.TagCodes);
        var sectionsJson = NormalizeJsonArray(schemaInput.SectionsJson, "SectionsJson");
        var fieldsJson = NormalizeJsonArray(schemaInput.FieldsJson, "FieldsJson");
        EnsureFieldsLimit(fieldsJson);
        EnsureFieldDisplayNames(fieldsJson);
        fieldsJson = NormalizeFieldPayload(fieldsJson, existingFieldsJson: null)
                     ?? "[]";
        EnsureNoAlternateFieldStatisticCreate(fieldsJson);
        EnsureFieldSchemaContract(fieldsJson, sectionsJson);
        var excelBlockJson = NormalizeOptionalJsonObject(schemaInput.ExcelBlockJson, "ExcelBlockJson");
        var blocksJson = NormalizeBlocksJson(schemaInput.BlocksJson, excelBlockJson);
        EnsureNoAlternateTableStatisticCreate(blocksJson);
        blocksJson = NormalizeBlocksForSections(blocksJson, sectionsJson);
        blocksJson = await NormalizeBlocksForDynamicExcelTemplatesAsync(me, blocksJson, ct);
        excelBlockJson = ExtractFirstBlockJson(blocksJson);
        EnsureTableStatisticContract(excelBlockJson, "ExcelBlockJson");
        EnsureBlocksTableStatisticContract(blocksJson, "BlocksJson");
        await EnsureLabelReferencesAsync(me, tagCodes, sectionsJson, fieldsJson, excelBlockJson, blocksJson, ct);
        await _enumCatalogs.ValidateVisibleActiveCatalogsAsync(ExtractEnumCatalogIds(fieldsJson, excelBlockJson, blocksJson), ct);
        EnsureUniqueLabelStatisticTargets(fieldsJson, blocksJson);
        EnsureSchemaPayloadBudget(sectionsJson, fieldsJson, blocksJson);
        EnsureTypedSchemaProjection(sectionsJson, fieldsJson, excelBlockJson, blocksJson);

        var docId = ObjectId.GenerateNewId().ToString();
        var doc = new DynamicFormTemplate
        {
            Id = docId,
            Code = code,
            Name = NormalizeName(req.Name),
            Description = NormalizeOptionalText(req.Description),
            TagCodes = tagCodes,
            CreatedByUsername = me.Username,
            SchemaVersion = Math.Max(1, req.SchemaVersion ?? 1),
            VersionNo = 1,
            FamilyId = docId,
            LineageStatus = DynamicFormLineageStatuses.Root,
            Revision = 1,
            IsActive = req.IsActive,
            IsPublished = false,
            SectionsJson = sectionsJson,
            FieldsJson = fieldsJson,
            ExcelBlockJson = excelBlockJson,
            BlocksJson = blocksJson,
            ExcelBlockDynamicExcelTemplateId = ExtractPrimaryBlockDynamicExcelTemplateId(excelBlockJson, blocksJson),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = me.Id,
            UpdatedByUserId = me.Id,
            IsDeleted = false,
        };

        await InsertDynamicFormAsync(doc, ct);
        return await ToDetailAsync(doc, me, ct);
    }

    public async Task<DynamicFormDetail> UpdateAsync(string id, UpdateDynamicFormReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var doc = await LoadAsync(id, ct);

        RequireCanMutate(me, doc);
        var expectedRevision = RequireExpectedRevision(req.ExpectedRevision, doc);
        EnsureDraftTemplate(doc);
        await EnsureNotLinkedToRuntimeAsync(id, ct);
        var schemaInput = DynamicFormSchemaAdapter.ResolveInput(
            req.Schema,
            req.SectionsJson,
            req.FieldsJson,
            req.ExcelBlockJson,
            req.BlocksJson);
        EnsureTypedSchemaProjection(
            schemaInput.SectionsJson,
            schemaInput.FieldsJson,
            schemaInput.ExcelBlockJson,
            schemaInput.BlocksJson);
        var retainedDynamicExcelTemplateIds = ExtractDynamicExcelTemplateIds(doc.BlocksJson, doc.ExcelBlockJson);

        var now = DateTime.UtcNow;
        var tagCodes = NormalizeLabelCodes(req.TagCodes);
        var sectionsJson = NormalizeJsonArray(schemaInput.SectionsJson, "SectionsJson");
        var fieldsJson = NormalizeJsonArray(schemaInput.FieldsJson, "FieldsJson");
        EnsureFieldsLimit(fieldsJson);
        EnsureFieldDisplayNames(fieldsJson);
        fieldsJson = NormalizeFieldPayload(fieldsJson, doc.FieldsJson)
                     ?? "[]";
        EnsureNoAlternateFieldStatisticUpdate(doc.FieldsJson, fieldsJson);
        EnsureFieldSchemaContract(fieldsJson, sectionsJson);
        var excelBlockJson = NormalizeOptionalJsonObject(schemaInput.ExcelBlockJson, "ExcelBlockJson");
        var blocksJson = NormalizeBlocksJson(schemaInput.BlocksJson, excelBlockJson);
        EnsureNoAlternateTableStatisticUpdate(
            NormalizeBlocksJson(doc.BlocksJson, doc.ExcelBlockJson),
            blocksJson,
            doc.StatisticConfigSections?.TableSectionJson);
        blocksJson = NormalizeBlocksForSections(blocksJson, sectionsJson);
        blocksJson = await NormalizeBlocksForDynamicExcelTemplatesAsync(me, blocksJson, ct, retainedDynamicExcelTemplateIds);
        blocksJson = CarryForwardTableStatisticMetadata(
            doc.BlocksJson,
            blocksJson);
        excelBlockJson = ExtractFirstBlockJson(blocksJson);
        EnsureTableStatisticContract(excelBlockJson, "ExcelBlockJson");
        EnsureBlocksTableStatisticContract(blocksJson, "BlocksJson");
        await EnsureLabelReferencesAsync(me, tagCodes, sectionsJson, fieldsJson, excelBlockJson, blocksJson, ct);
        await _enumCatalogs.ValidateVisibleActiveCatalogsAsync(ExtractEnumCatalogIds(fieldsJson, excelBlockJson, blocksJson), ct);
        EnsureUniqueLabelStatisticTargets(fieldsJson, blocksJson);
        EnsureSchemaPayloadBudget(sectionsJson, fieldsJson, blocksJson);
        EnsureTypedSchemaProjection(sectionsJson, fieldsJson, excelBlockJson, blocksJson);

        var update = Builders<DynamicFormTemplate>.Update
            .Set(x => x.Name, NormalizeName(req.Name))
            .Set(x => x.Description, NormalizeOptionalText(req.Description))
            .Set(x => x.TagCodes, tagCodes)
            .Set(x => x.SchemaVersion, Math.Max(1, req.SchemaVersion ?? doc.SchemaVersion))
            .Set(x => x.IsActive, req.IsActive)
            .Set(x => x.SectionsJson, sectionsJson)
            .Set(x => x.FieldsJson, fieldsJson)
            .Set(x => x.ExcelBlockJson, excelBlockJson)
            .Set(x => x.BlocksJson, blocksJson)
            .Set(x => x.ExcelBlockDynamicExcelTemplateId, ExtractPrimaryBlockDynamicExcelTemplateId(excelBlockJson, blocksJson))
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id)
            .Set(x => x.Revision, expectedRevision + 1);

        UpdateResult res;
        try
        {
            res = await _ctx.DynamicFormTemplates.UpdateOneAsync(
                BuildRevisionFilter(id, expectedRevision, requireDraft: true),
                update,
                cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (
            !string.IsNullOrWhiteSpace(doc.WrapReuseKey) &&
            req.IsActive &&
            ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new AppException(
                AppErrorCode.DYNAMIC_FORM_WRAP_REUSE_CONFLICT,
                new
                {
                    reason = "DYNAMIC_FORM_ACTIVE_WRAP_REUSE_EXISTS",
                    action = "RELOAD_DYNAMIC_FORM_LIST",
                },
                innerException: ex);
        }

        if (res.MatchedCount == 0)
            throw await ResolveRevisionFailureAsync(id, expectedRevision, ct);

        return await GetByIdAsync(id, ct);
    }

    public Task<DynamicFormStatisticConfigResult> GetStatisticsAsync(
        string id,
        CancellationToken ct)
        => _statisticConfig.GetAsync(id, ct);

    public Task<DynamicFormStatisticConfigResult> PatchStatisticsAsync(
        string id,
        JsonElement body,
        CancellationToken ct)
        => _statisticConfig.PatchAsync(id, body, ct);

    public Task<DynamicFormStatisticConfigResult> GetStatisticConfigAsync(
        string id,
        CancellationToken ct)
        => GetStatisticsAsync(id, ct);

    public Task<DynamicFormStatisticConfigResult> UpdateStatisticConfigAsync(
        string id,
        JsonElement body,
        CancellationToken ct)
        => PatchStatisticsAsync(id, body, ct);

    public async Task<DynamicFormDetail> PublishAsync(
        string id,
        PublishDynamicFormReq? req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var doc = await LoadAsync(id, ct);

        RequireCanMutate(me, doc);

        if (doc.IsPublished)
            return await ToDetailAsync(doc, me, ct);

        var expectedRevision = RequireExpectedRevision(req?.ExpectedRevision, doc);

        EnsureTypedSchemaProjection(
            doc.SectionsJson,
            doc.FieldsJson,
            doc.ExcelBlockJson,
            doc.BlocksJson);
        EnsureFieldsLimit(doc.FieldsJson);
        EnsureFieldDisplayNames(doc.FieldsJson);
        EnsureFieldSchemaContract(doc.FieldsJson, doc.SectionsJson);
        var retainedDynamicExcelTemplateIds = ExtractDynamicExcelTemplateIds(doc.BlocksJson, doc.ExcelBlockJson);
        var blocksJson = NormalizeBlocksJson(doc.BlocksJson, doc.ExcelBlockJson);
        blocksJson = NormalizeBlocksForSections(blocksJson, doc.SectionsJson);
        blocksJson = await NormalizeBlocksForDynamicExcelTemplatesAsync(me, blocksJson, ct, retainedDynamicExcelTemplateIds);
        blocksJson = CarryForwardTableStatisticMetadata(
            doc.BlocksJson,
            blocksJson);
        var excelBlockJson = ExtractFirstBlockJson(blocksJson);
        await EnsureLabelReferencesAsync(
            me,
            doc.TagCodes,
            doc.SectionsJson,
            doc.FieldsJson,
            excelBlockJson,
            blocksJson,
            ct);
        await _enumCatalogs.ValidateVisibleActiveCatalogsAsync(
            ExtractEnumCatalogIds(doc.FieldsJson, excelBlockJson, blocksJson),
            ct);
        EnsureTableStatisticContract(excelBlockJson, "ExcelBlockJson");
        EnsureBlocksTableStatisticContract(blocksJson, "BlocksJson");
        EnsureUniqueLabelStatisticTargets(doc.FieldsJson, blocksJson);
        EnsureSchemaPayloadBudget(doc.SectionsJson, doc.FieldsJson, blocksJson);
        EnsurePublishableSchema(doc.SectionsJson, doc.FieldsJson, blocksJson);
        EnsureTypedSchemaProjection(doc.SectionsJson, doc.FieldsJson, excelBlockJson, blocksJson);
        var publishedSnapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(
            doc.SchemaVersion,
            doc.SectionsJson,
            doc.FieldsJson,
            blocksJson);
        var lockedStatisticSnapshots =
            PrepareStatisticConfigForPublish(doc);

        var now = DateTime.UtcNow;
        var update = Builders<DynamicFormTemplate>.Update
            .Set(x => x.IsPublished, true)
            .Set(x => x.IsActive, true)
            .Set(x => x.ExcelBlockJson, excelBlockJson)
            .Set(x => x.BlocksJson, blocksJson)
            .Set(x => x.ExcelBlockDynamicExcelTemplateId, ExtractPrimaryBlockDynamicExcelTemplateId(excelBlockJson, blocksJson))
            .Set(x => x.FamilyId, EffectiveFamilyId(doc))
            .Set(x => x.LineageStatus, EffectiveLineageStatus(doc))
            .Set(x => x.PublishedSchemaSnapshotJson, publishedSnapshot.Json)
            .Set(x => x.PublishedSchemaHash, publishedSnapshot.Sha256)
            .Set(x => x.PublishedAtUtc, now)
            .Set(x => x.PublishedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id)
            .Set(x => x.Revision, expectedRevision + 1);
        if (lockedStatisticSnapshots is not null)
        {
            update = update
                .Set(
                    x => x.StatisticConfigStatus,
                    StatConfigStatuses.Locked)
                .Set(
                    x => x.StatisticConfigSnapshots,
                    lockedStatisticSnapshots);
        }

        var res = await _ctx.DynamicFormTemplates.UpdateOneAsync(
            BuildRevisionFilter(id, expectedRevision, requireDraft: true),
            update,
            cancellationToken: ct);

        if (res.MatchedCount == 0)
        {
            // Publishing is idempotent across racing callers: exactly one CAS
            // transition wins, while other authorized callers observe that same
            // immutable published version instead of surfacing a false conflict.
            var current = await LoadAsync(id, ct);
            RequireCanMutate(me, current);
            if (current.IsPublished)
                return await ToDetailAsync(current, me, ct);

            throw await ResolveRevisionFailureAsync(id, expectedRevision, ct);
        }

        return await GetByIdAsync(id, ct);
    }

    public async Task<DynamicFormDetail> CloneAsync(string id, CloneDynamicFormReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var source = await LoadAsync(id, ct);
        await RequireCanCloneAsync(me, source, ct);
        if (source.IsPublished)
            _ = RequirePublishedSchemaIntegrity(source);
        EnsureTypedSchemaProjection(
            source.SectionsJson,
            source.FieldsJson,
            source.ExcelBlockJson,
            source.BlocksJson);
        EnsureFieldSchemaContract(source.FieldsJson, source.SectionsJson);
        var now = DateTime.UtcNow;
        var code = NormalizeCode(req.Code);
        if (string.IsNullOrWhiteSpace(code))
        {
            var (_, _, nextCode) = await ComputeNextCodeAsync(now.Year, ct);
            code = nextCode;
        }

        var retainedDynamicExcelTemplateIds = ExtractDynamicExcelTemplateIds(source.BlocksJson, source.ExcelBlockJson);
        var sourceBlocksJson = NormalizeBlocksJson(source.BlocksJson, source.ExcelBlockJson);
        sourceBlocksJson = NormalizeBlocksForSections(sourceBlocksJson, source.SectionsJson);
        sourceBlocksJson = await NormalizeBlocksForDynamicExcelTemplatesAsync(
            me,
            sourceBlocksJson,
            ct,
            retainedDynamicExcelTemplateIds);
        sourceBlocksJson = CarryForwardTableStatisticMetadata(
            source.BlocksJson,
            sourceBlocksJson);
        var sourceExcelBlockJson = ExtractFirstBlockJson(sourceBlocksJson);
        EnsureTableStatisticContract(sourceExcelBlockJson, "ExcelBlockJson");
        EnsureBlocksTableStatisticContract(sourceBlocksJson, "BlocksJson");
        EnsureSchemaPayloadBudget(source.SectionsJson, source.FieldsJson, sourceBlocksJson);
        EnsureTypedSchemaProjection(
            source.SectionsJson,
            source.FieldsJson,
            sourceExcelBlockJson,
            sourceBlocksJson);
        var cloneId = ObjectId.GenerateNewId().ToString();
        var clone = new DynamicFormTemplate
        {
            Id = cloneId,
            Code = code,
            Name = string.IsNullOrWhiteSpace(req.Name) ? $"{source.Name} - Copy" : NormalizeName(req.Name),
            Description = source.Description,
            TagCodes = source.TagCodes,
            CreatedByUsername = me.Username,
            SchemaVersion = source.SchemaVersion,
            VersionNo = 1,
            FamilyId = cloneId,
            ClonedFromVersionId = source.Id,
            LineageStatus = DynamicFormLineageStatuses.Clone,
            Revision = 1,
            IsActive = true,
            IsPublished = false,
            SectionsJson = source.SectionsJson,
            FieldsJson = source.FieldsJson,
            ExcelBlockJson = sourceExcelBlockJson,
            BlocksJson = sourceBlocksJson,
            ExcelBlockDynamicExcelTemplateId =
                source.ExcelBlockDynamicExcelTemplateId
                ?? ExtractPrimaryBlockDynamicExcelTemplateId(sourceExcelBlockJson, sourceBlocksJson),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = me.Id,
            UpdatedByUserId = me.Id,
            IsDeleted = false,
        };

        await InsertDynamicFormAsync(clone, ct);
        return await ToDetailAsync(clone, me, ct);
    }

    public async Task<DynamicFormDetail> WrapDynamicExcelAsync(
        WrapDynamicExcelAsFormReq req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var dynamicExcelId = NormalizeDynamicExcelTemplateId(req.DynamicExcelTemplateId);

        var excel = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == dynamicExcelId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw DynamicExcelNotFound(dynamicExcelId);
        RequireCanReadDynamicExcel(me, excel);

        var wrapReuseKey = ShouldReuseExistingWrap(req)
            ? BuildWrapReuseKey(me.Id, dynamicExcelId)
            : null;
        if (wrapReuseKey is not null)
        {
            var existing = await _ctx.DynamicFormTemplates
                .Find(x =>
                    x.WrapReuseKey == wrapReuseKey &&
                    !x.IsDeleted &&
                    x.IsActive &&
                    !x.IsPublished)
                .SortBy(x => x.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);

            if (existing is not null)
                return await ToDetailAsync(existing, me, ct);
        }

        var now = DateTime.UtcNow;
        var code = NormalizeCode(req.Code);
        if (string.IsNullOrWhiteSpace(code))
        {
            var (_, _, nextCode) = await ComputeNextCodeAsync(now.Year, ct);
            code = nextCode;
        }
        var tagCodes = NormalizeLabelCodes(req.TagCodes);

        var sectionId = createStableSectionId(dynamicExcelId);
        var snapshot = BuildDynamicExcelBlockSnapshot(excel, sectionId);

        var section = new[]
        {
            new
            {
                id = sectionId,
                title = "Bảng Excel động",
                description = excel.Name,
                order = 0,
            }
        };

        var excelBlockJson = JsonSerializer.Serialize(snapshot, JsonOptions);
        var blocksJson = JsonSerializer.Serialize(new[] { snapshot }, JsonOptions);

        var docId = ObjectId.GenerateNewId().ToString();
        var doc = new DynamicFormTemplate
        {
            Id = docId,
            Code = code,
            Name = string.IsNullOrWhiteSpace(req.Name) ? excel.Name : NormalizeName(req.Name!),
            Description = NormalizeOptionalText(req.Description),
            TagCodes = tagCodes,
            CreatedByUsername = me.Username,
            SchemaVersion = 1,
            VersionNo = 1,
            FamilyId = docId,
            LineageStatus = DynamicFormLineageStatuses.Wrapped,
            WrapReuseKey = wrapReuseKey,
            Revision = 1,
            IsActive = true,
            IsPublished = false,
            SectionsJson = JsonSerializer.Serialize(section, JsonOptions),
            FieldsJson = "[]",
            ExcelBlockJson = excelBlockJson,
            BlocksJson = blocksJson,
            ExcelBlockDynamicExcelTemplateId = excel.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = me.Id,
            UpdatedByUserId = me.Id,
            IsDeleted = false,
        };

        await EnsureLabelReferencesAsync(me, tagCodes, doc.SectionsJson, doc.FieldsJson, doc.ExcelBlockJson, doc.BlocksJson, ct);
        EnsureFieldsLimit(doc.FieldsJson);
        EnsureFieldDisplayNames(doc.FieldsJson);
        EnsureFieldSchemaContract(doc.FieldsJson, doc.SectionsJson);
        EnsureUniqueLabelStatisticTargets(doc.FieldsJson, doc.BlocksJson);
        EnsureSchemaPayloadBudget(doc.SectionsJson, doc.FieldsJson, doc.BlocksJson);
        EnsureTypedSchemaProjection(
            doc.SectionsJson,
            doc.FieldsJson,
            doc.ExcelBlockJson,
            doc.BlocksJson);

        try
        {
            await InsertDynamicFormAsync(doc, ct);
        }
        catch (AppException ex) when (
            wrapReuseKey is not null &&
            ex.Code == AppErrorCode.DYNAMIC_FORM_CODE_CONFLICT)
        {
            var winner = await _ctx.DynamicFormTemplates
                .Find(x =>
                    x.WrapReuseKey == wrapReuseKey &&
                    !x.IsDeleted &&
                    x.IsActive &&
                    !x.IsPublished)
                .FirstOrDefaultAsync(ct);
            if (winner is not null)
                return await ToDetailAsync(winner, me, ct);

            throw;
        }

        return await ToDetailAsync(doc, me, ct);

        static string createStableSectionId(string id) => $"excel_{id}";

        static bool ShouldReuseExistingWrap(WrapDynamicExcelAsFormReq request)
            => string.IsNullOrWhiteSpace(request.Code)
               && string.IsNullOrWhiteSpace(request.Name)
               && string.IsNullOrWhiteSpace(request.Description)
               && (request.TagCodes is null || request.TagCodes.Length == 0);

        static string BuildWrapReuseKey(string actorUserId, string templateId)
            => $"{actorUserId}:{templateId}";
    }

    public async Task<DynamicFormDetail> ImportDynamicExcelBlockAsync(
        string id,
        ImportDynamicExcelBlockReq req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var doc = await LoadAsync(id, ct);

        RequireCanMutate(me, doc);
        var expectedRevision = RequireExpectedRevision(req.ExpectedRevision, doc);
        EnsureDraftTemplate(doc);
        await EnsureNotLinkedToRuntimeAsync(id, ct);
        EnsureTypedSchemaProjection(
            doc.SectionsJson,
            doc.FieldsJson,
            doc.ExcelBlockJson,
            doc.BlocksJson);
        var retainedDynamicExcelTemplateIds = ExtractDynamicExcelTemplateIds(doc.BlocksJson, doc.ExcelBlockJson);

        var dynamicExcelId = NormalizeDynamicExcelTemplateId(req.DynamicExcelTemplateId);

        var excel = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == dynamicExcelId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw DynamicExcelNotFound(dynamicExcelId);
        RequireCanReadDynamicExcel(me, excel);

        var currentBlocksJson = NormalizeBlocksJson(doc.BlocksJson, doc.ExcelBlockJson);
        EnsureFieldsLimit(doc.FieldsJson);
        EnsureFieldDisplayNames(doc.FieldsJson);
        EnsureFieldSchemaContract(doc.FieldsJson, doc.SectionsJson);
        currentBlocksJson = NormalizeBlocksForSections(currentBlocksJson, doc.SectionsJson);
        var sectionId = NormalizeImportSectionId(req.SectionId, doc.SectionsJson);
        var snapshot = BuildDynamicExcelBlockSnapshot(excel, sectionId);
        var blocksJson = AppendDynamicExcelBlock(currentBlocksJson, snapshot);
        blocksJson = NormalizeBlocksForSections(blocksJson, doc.SectionsJson);
        blocksJson = await NormalizeBlocksForDynamicExcelTemplatesAsync(me, blocksJson, ct, retainedDynamicExcelTemplateIds);
        blocksJson = CarryForwardTableStatisticMetadata(
            doc.BlocksJson,
            blocksJson);
        var excelBlockJson = ExtractFirstBlockJson(blocksJson);
        EnsureBlocksTableStatisticContract(blocksJson, "BlocksJson");
        await EnsureLabelReferencesAsync(me, doc.TagCodes, doc.SectionsJson, doc.FieldsJson, excelBlockJson, blocksJson, ct);
        await _enumCatalogs.ValidateVisibleActiveCatalogsAsync(
            ExtractEnumCatalogIds(doc.FieldsJson, excelBlockJson, blocksJson),
            ct);
        EnsureUniqueLabelStatisticTargets(doc.FieldsJson, blocksJson);
        EnsureSchemaPayloadBudget(doc.SectionsJson, doc.FieldsJson, blocksJson);
        EnsureTypedSchemaProjection(doc.SectionsJson, doc.FieldsJson, excelBlockJson, blocksJson);

        var now = DateTime.UtcNow;
        var update = Builders<DynamicFormTemplate>.Update
            .Set(x => x.BlocksJson, blocksJson)
            .Set(x => x.ExcelBlockJson, excelBlockJson)
            .Set(x => x.ExcelBlockDynamicExcelTemplateId, ExtractPrimaryBlockDynamicExcelTemplateId(excelBlockJson, blocksJson))
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id)
            .Set(x => x.Revision, expectedRevision + 1);

        var res = await _ctx.DynamicFormTemplates.UpdateOneAsync(
            BuildRevisionFilter(id, expectedRevision, requireDraft: true),
            update,
            cancellationToken: ct);

        if (res.MatchedCount == 0)
            throw await ResolveRevisionFailureAsync(id, expectedRevision, ct);

        return await GetByIdAsync(id, ct);
    }

    public async Task DeleteAsync(string id, int? expectedRevision, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var doc = await LoadAsync(id, ct);

        RequireCanMutate(me, doc);
        EnsureDraftTemplate(doc);
        var requiredRevision = RequireExpectedRevision(expectedRevision, doc);
        await EnsureNotLinkedToRuntimeAsync(id, ct);

        var now = DateTime.UtcNow;
        var update = Builders<DynamicFormTemplate>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.DeletedAtUtc, now)
            .Set(x => x.DeletedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id)
            .Set(x => x.Revision, requiredRevision + 1);

        var res = await _ctx.DynamicFormTemplates.UpdateOneAsync(
            BuildRevisionFilter(id, requiredRevision, requireDraft: true),
            update,
            cancellationToken: ct);

        if (res.MatchedCount == 0)
            throw await ResolveRevisionFailureAsync(id, requiredRevision, ct);
    }

    private async Task<DynamicFormTemplate> LoadAsync(string id, CancellationToken ct)
    {
        var normalizedId = NormalizeDynamicFormTemplateId(id);

        return await _ctx.DynamicFormTemplates
            .Find(x => x.Id == normalizedId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw DynamicFormNotFound(normalizedId);
    }

    private static BsonRegularExpression BuildLiteralSearchRegex(string value, string field)
    {
        var normalized = value.Trim();
        if (normalized.Length > MaxSearchTermLength)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field,
                    reason = "DYNAMIC_FORM_SEARCH_TERM_TOO_LONG",
                    maxLength = MaxSearchTermLength
                });
        }

        return new BsonRegularExpression(Regex.Escape(normalized), "i");
    }

    private async Task InsertDynamicFormAsync(DynamicFormTemplate doc, CancellationToken ct)
    {
        try
        {
            await _ctx.DynamicFormTemplates.InsertOneAsync(doc, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new AppException(
                AppErrorCode.DYNAMIC_FORM_CODE_CONFLICT,
                new { doc.Code },
                innerException: ex);
        }
    }

    private async Task InsertDynamicFormVersionAsync(
        DynamicFormTemplate next,
        DynamicFormTemplate source,
        int expectedRevision,
        CancellationToken ct)
    {
        try
        {
            await _ctx.DynamicFormTemplates.InsertOneAsync(next, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new AppException(
                AppErrorCode.DYNAMIC_FORM_VERSION_CONFLICT,
                new
                {
                    familyId = EffectiveFamilyId(source),
                    sourceVersionId = source.Id,
                    sourceRevision = expectedRevision,
                    nextVersionNo = next.VersionNo,
                    reason = "DYNAMIC_FORM_VERSION_ALREADY_EXISTS"
                },
                innerException: ex);
        }
    }

    private async Task ReserveNextVersionAsync(
        DynamicFormTemplate source,
        int expectedRevision,
        string actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        var filter = BuildRevisionFilter(source.Id, expectedRevision, requireDraft: false)
                     & Builders<DynamicFormTemplate>.Filter.Eq(x => x.IsPublished, true);
        var update = Builders<DynamicFormTemplate>.Update
            .Set(x => x.Revision, expectedRevision + 1)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);

        var result = await _ctx.DynamicFormTemplates.UpdateOneAsync(
            filter,
            update,
            cancellationToken: ct);
        if (result.MatchedCount == 0)
            throw await ResolveRevisionFailureAsync(source.Id, expectedRevision, ct);
    }

    private static int EffectiveRevision(DynamicFormTemplate doc)
        => Math.Max(1, doc.Revision);

    private static string EffectiveFamilyId(DynamicFormTemplate doc)
        => string.IsNullOrWhiteSpace(doc.FamilyId) ? doc.Id : doc.FamilyId;

    private static string EffectiveLineageStatus(DynamicFormTemplate doc)
        => string.IsNullOrWhiteSpace(doc.LineageStatus)
            ? DynamicFormLineageStatuses.Legacy
            : doc.LineageStatus;

    private static FilterDefinition<DynamicFormTemplate> BuildFamilyFilter(string familyId)
    {
        var f = Builders<DynamicFormTemplate>.Filter;
        var legacyRoot = f.Eq(x => x.Id, familyId)
                         & (f.Exists("familyId", false) | f.Eq(x => x.FamilyId, null));
        return f.Eq(x => x.IsDeleted, false)
               & (f.Eq(x => x.FamilyId, familyId) | legacyRoot);
    }

    private static int RequireExpectedRevision(int? expectedRevision, DynamicFormTemplate doc)
    {
        if (!expectedRevision.HasValue || expectedRevision.Value <= 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FORM_REVISION_REQUIRED,
                new
                {
                    dynamicFormTemplateId = doc.Id,
                    expectedRevision,
                    currentRevision = EffectiveRevision(doc)
                });
        }

        var currentRevision = EffectiveRevision(doc);
        if (expectedRevision.Value != currentRevision)
            throw RevisionConflict(doc, expectedRevision.Value);

        return expectedRevision.Value;
    }

    private static FilterDefinition<DynamicFormTemplate> BuildRevisionFilter(
        string id,
        int expectedRevision,
        bool requireDraft)
    {
        var f = Builders<DynamicFormTemplate>.Filter;
        var revisionFilter = f.Eq(x => x.Revision, expectedRevision);
        if (expectedRevision == 1)
        {
            revisionFilter |= f.Exists("revision", false);
            revisionFilter |= f.Lte(x => x.Revision, 0);
        }

        var filter = f.Eq(x => x.Id, id)
                     & f.Eq(x => x.IsDeleted, false)
                     & revisionFilter;
        if (requireDraft)
            filter &= f.Eq(x => x.IsPublished, false);

        return filter;
    }

    private async Task<AppException> ResolveRevisionFailureAsync(
        string id,
        int expectedRevision,
        CancellationToken ct)
    {
        var current = await _ctx.DynamicFormTemplates
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        return current is null
            ? DynamicFormNotFound(id)
            : RevisionConflict(current, expectedRevision);
    }

    private static AppException RevisionConflict(DynamicFormTemplate current, int expectedRevision)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FORM_REVISION_CONFLICT,
            new
            {
                dynamicFormTemplateId = current.Id,
                expectedRevision,
                currentRevision = EffectiveRevision(current),
                current.IsPublished,
                current.UpdatedAtUtc,
                current.UpdatedByUserId
            });

    private async Task<DynamicFormDetail> ToDetailAsync(DynamicFormTemplate x, MeResponse me, CancellationToken ct)
    {
        var canViewByCloneGrant = await HasApprovedCloneGrantAsync(x.Id, me.Id, ct);
        var blocksJson = NormalizeBlocksJson(x.BlocksJson, x.ExcelBlockJson);
        var publishedSnapshot = ResolvePublishedSnapshot(x);
        return new DynamicFormDetail(
            x.Id,
            x.Code,
            x.Name,
            x.Description,
            x.TagCodes,
            x.SchemaVersion,
            x.VersionNo,
            EffectiveFamilyId(x),
            x.PreviousVersionId,
            x.ClonedFromVersionId,
            EffectiveLineageStatus(x),
            EffectiveRevision(x),
            x.IsActive,
            x.IsPublished,
            x.CreatedByUserId,
            x.CreatedByUsername,
            x.CreatedAtUtc,
            x.UpdatedAtUtc,
            x.PublishedAtUtc,
            x.SectionsJson,
            x.FieldsJson,
            x.ExcelBlockJson,
            blocksJson,
            x.ExcelBlockDynamicExcelTemplateId,
            x.StatisticConfigUpdatedAtUtc,
            x.StatisticConfigUpdatedByUserId,
            x.StatisticConfigUpdateMonthKey,
            CanMutate(me, x),
            CanClone(me, x, canViewByCloneGrant),
            canViewByCloneGrant,
            publishedSnapshot?.Json,
            publishedSnapshot?.Sha256,
            BuildActionCapabilities(me, x, canViewByCloneGrant),
            DynamicFormSchemaAdapter.FromLegacy(
                x.SectionsJson,
                x.FieldsJson,
                x.ExcelBlockJson,
                blocksJson));
    }

    private static DynamicFormRow ToRow(
        DynamicFormTemplate x,
        MeResponse me,
        IReadOnlySet<string> approvedCloneTemplateIds)
    {
        var canViewByCloneGrant = approvedCloneTemplateIds.Contains(x.Id);
        return new DynamicFormRow(
            x.Id,
            x.Code,
            x.Name,
            x.Description,
            x.TagCodes,
            x.SchemaVersion,
            x.VersionNo,
            EffectiveFamilyId(x),
            x.PreviousVersionId,
            x.ClonedFromVersionId,
            EffectiveLineageStatus(x),
            EffectiveRevision(x),
            x.IsActive,
            x.IsPublished,
            x.CreatedByUserId,
            x.CreatedByUsername,
            x.CreatedAtUtc,
            CanMutate(me, x),
            CanClone(me, x, canViewByCloneGrant),
            canViewByCloneGrant,
            ResolvePublishedSnapshot(x)?.Sha256,
            BuildActionCapabilities(me, x, canViewByCloneGrant));
    }

    private static DynamicFormPublishedSchemaSnapshot? ResolvePublishedSnapshot(
        DynamicFormTemplate doc)
    {
        if (!doc.IsPublished)
            return null;

        return RequirePublishedSchemaIntegrity(doc);
    }

    private static DynamicFormPublishedSchemaSnapshot RequirePublishedSchemaIntegrity(
        DynamicFormTemplate doc)
    {
        try
        {
            return DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(doc);
        }
        catch (InvalidOperationException ex)
        {
            throw new AppException(
                AppErrorCode.DYNAMIC_FORM_PUBLISHED_SCHEMA_INTEGRITY_FAILED,
                new
                {
                    dynamicFormTemplateId = doc.Id,
                    reason = "PUBLISHED_SCHEMA_SNAPSHOT_OR_LIVE_STRUCTURE_MISMATCH"
                },
                innerException: ex);
        }
    }

    private static AppException DynamicFormIdRequired(string? dynamicFormTemplateId)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.DYNAMIC_FORM_TEMPLATE_ID_REQUIRED,
            new { dynamicFormTemplateId });

    private static string NormalizeDynamicFormTemplateId(string? dynamicFormTemplateId)
    {
        if (string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            throw DynamicFormIdRequired(dynamicFormTemplateId);
        if (!ObjectId.TryParse(dynamicFormTemplateId, out var objectId))
        {
            throw DynamicFormValidation(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                "DYNAMIC_FORM_TEMPLATE_ID_INVALID",
                new { fieldName = "dynamicFormTemplateId" });
        }

        return objectId.ToString();
    }

    private static AppException DynamicFormNotFound(string? dynamicFormTemplateId)
        => AppExceptionFactory.NotFound(
            AppErrorCode.DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
            new { dynamicFormTemplateId });

    private static AppException DynamicFormInUse(AppErrorCode code, string dynamicFormTemplateId)
        => AppExceptionFactory.Create(
            code,
            new { dynamicFormTemplateId });

    private static AppException DynamicExcelIdRequired(string? dynamicExcelTemplateId)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.DYNAMIC_EXCEL_TEMPLATE_ID_REQUIRED,
            new { dynamicExcelTemplateId });

    private static string NormalizeDynamicExcelTemplateId(string? dynamicExcelTemplateId)
    {
        if (string.IsNullOrWhiteSpace(dynamicExcelTemplateId))
            throw DynamicExcelIdRequired(dynamicExcelTemplateId);
        if (!ObjectId.TryParse(dynamicExcelTemplateId, out var objectId))
        {
            throw DynamicFormValidation(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                "DYNAMIC_EXCEL_TEMPLATE_ID_INVALID",
                new { fieldName = "dynamicExcelTemplateId" });
        }

        return objectId.ToString();
    }

    private static AppException DynamicExcelNotFound(string? dynamicExcelTemplateId)
        => AppExceptionFactory.NotFound(
            AppErrorCode.DYNAMIC_EXCEL_TEMPLATE_NOT_FOUND,
            new { dynamicExcelTemplateId });

    private static object DynamicFormDetails(DynamicFormTemplate doc, string? actorUserId = null)
        => new
        {
            dynamicFormTemplateId = doc.Id,
            doc.Code,
            doc.Name,
            doc.CreatedByUserId,
            doc.IsPublished,
            actorUserId
        };

    private static object ForbiddenDetails(string action)
        => new
        {
            reason = "DYNAMIC_FORM_ACCESS_FORBIDDEN",
            action
        };

    private static FilterDefinition<DynamicFormTemplate> BuildVisibleFilter(
        MeResponse me,
        IReadOnlySet<string> approvedCloneTemplateIds)
    {
        var f = Builders<DynamicFormTemplate>.Filter;
        if (RoleGuard.IsSystemAdmin(me))
            return f.Empty;

        var filter = f.Eq(x => x.CreatedByUserId, me.Id);

        if (approvedCloneTemplateIds.Count > 0)
            filter |= f.In(x => x.Id, approvedCloneTemplateIds);

        return filter;
    }

    private async Task<HashSet<string>> LoadApprovedCloneTemplateIdsAsync(string userId, CancellationToken ct)
    {
        var ids = await _ctx.DynamicFormCloneRequests
            .Find(x =>
                x.RequesterUserId == userId &&
                x.Status == DynamicFormCloneRequestStatus.Approved &&
                !x.IsDeleted)
            .Project(x => x.DynamicFormTemplateId)
            .ToListAsync(ct);

        return ids
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);
    }

    private Task<bool> HasApprovedCloneGrantAsync(string templateId, string userId, CancellationToken ct)
        => _ctx.DynamicFormCloneRequests
            .Find(x =>
                x.DynamicFormTemplateId == templateId &&
                x.RequesterUserId == userId &&
                x.Status == DynamicFormCloneRequestStatus.Approved &&
                !x.IsDeleted)
            .Limit(1)
            .AnyAsync(ct);

    private async Task RequireCanReadAsync(MeResponse me, DynamicFormTemplate doc, CancellationToken ct)
    {
        if (RoleGuard.IsSystemAdmin(me) ||
            string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal))
            return;

        if (await HasApprovedCloneGrantAsync(doc.Id, me.Id, ct))
            return;

        if (await HasRuntimeReadGrantAsync(doc.Id, me.Id, ct))
            return;

        throw AppExceptionFactory.Forbidden(
            AppErrorCode.DYNAMIC_FORM_READ_FORBIDDEN,
            ForbiddenDetails("READ_DYNAMIC_FORM"));
    }

    private static void RequireCanReadVersionHistory(MeResponse me, DynamicFormTemplate doc)
    {
        if (RoleGuard.IsSystemAdmin(me) ||
            string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal))
            return;

        throw AppExceptionFactory.Forbidden(
            AppErrorCode.DYNAMIC_FORM_READ_FORBIDDEN,
            ForbiddenDetails("READ_VERSION_HISTORY"));
    }

    private async Task<bool> HasRuntimeReadGrantAsync(
        string templateId,
        string userId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(templateId) || string.IsNullOrWhiteSpace(userId))
            return false;

        if (await _ctx.WorkTemplateAssignees
                .Find(x =>
                    x.DynamicFormTemplateId == templateId &&
                    x.AssigneeUserId == userId &&
                    x.IsActive &&
                    !x.IsDeleted)
                .Limit(1)
                .AnyAsync(ct))
            return true;

        if (await _ctx.MyReportPeriodListDocRoles
                .Find(x =>
                    x.DynamicFormTemplateId == templateId &&
                    x.UserId == userId &&
                    !x.IsDeleted)
                .Limit(1)
                .AnyAsync(ct))
            return true;

        if (await _ctx.WorkAssignments
                .Find(x =>
                    x.DynamicFormTemplateId == templateId &&
                    x.CreatedByUserId == userId &&
                    x.IsActive &&
                    (x.FlowEffectiveStatus == null || x.FlowEffectiveStatus == DynamicFlowEffectiveStatuses.Effective) &&
                    !x.IsDeleted)
                .Limit(1)
                .AnyAsync(ct))
            return true;

        return await _ctx.ReviewReportListDocRoles
            .Find(x =>
                x.DynamicFormTemplateId == templateId &&
                x.ReviewerUserId == userId &&
                x.ReportIsActive &&
                !x.IsDeleted)
            .Limit(1)
            .AnyAsync(ct);
    }

    private async Task RequireCanCloneAsync(MeResponse me, DynamicFormTemplate doc, CancellationToken ct)
    {
        if (RoleGuard.IsSystemAdmin(me) ||
            string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal))
            return;

        if (await HasApprovedCloneGrantAsync(doc.Id, me.Id, ct))
            return;

        throw AppExceptionFactory.Forbidden(
            AppErrorCode.DYNAMIC_FORM_CLONE_FORBIDDEN,
            ForbiddenDetails("CLONE_DYNAMIC_FORM"));
    }

    private static bool CanMutate(MeResponse me, DynamicFormTemplate doc)
        => string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal) || RoleGuard.IsSystemAdmin(me);

    private static bool CanClone(MeResponse me, DynamicFormTemplate doc, bool canViewByCloneGrant)
        => RoleGuard.IsSystemAdmin(me)
           || string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal)
           || canViewByCloneGrant;

    private static DynamicFormActionCapabilities BuildActionCapabilities(
        MeResponse me,
        DynamicFormTemplate doc,
        bool canViewByCloneGrant)
    {
        var canMutate = CanMutate(me, doc);
        var isDraft = !doc.IsPublished;
        return new DynamicFormActionCapabilities(
            CanRead: true,
            CanUpdate: canMutate && isDraft,
            CanDelete: canMutate && isDraft,
            CanPublish: canMutate && isDraft,
            CanCreateVersion: canMutate && doc.IsPublished,
            CanViewHistory: RoleGuard.IsSystemAdmin(me) ||
                            string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal),
            CanClone: CanClone(me, doc, canViewByCloneGrant),
            CanImport: canMutate && isDraft,
            CanUpdateStatistics: canMutate);
    }

    private static void RequireCanReadDynamicExcel(MeResponse me, DynamicExcelTemplate doc)
    {
        if (!RoleGuard.IsSystemAdmin(me) &&
            !string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal))
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.DYNAMIC_EXCEL_READ_FORBIDDEN,
                new { reason = "DYNAMIC_EXCEL_ACCESS_FORBIDDEN" });
    }

    private static AppException DynamicFormValidation(
        AppErrorCode code,
        string reason,
        object? details = null,
        Exception? innerException = null)
        => new(
            code,
            new
            {
                reason,
                details
            },
            message: null,
            innerException: innerException);

    private static AppException DynamicFormJsonInvalid(
        string fieldName,
        string reason,
        Exception? innerException = null)
        => DynamicFormValidation(
            AppErrorCode.DYNAMIC_FORM_JSON_INVALID,
            reason,
            new { fieldName },
            innerException);

    private static AppException DynamicFormJsonKindInvalid(
        string fieldName,
        string expectedKind,
        JsonValueKind actualKind,
        string reason)
        => DynamicFormValidation(
            AppErrorCode.DYNAMIC_FORM_JSON_KIND_INVALID,
            reason,
            new
            {
                fieldName,
                expectedKind,
                actualKind = actualKind.ToString()
            });

    private static AppException DynamicFormLimitExceeded(
        string limitName,
        int limit,
        int actual,
        string reason)
        => DynamicFormValidation(
            AppErrorCode.DYNAMIC_FORM_LIMIT_EXCEEDED,
            reason,
            new
            {
                limitName,
                limit,
                actual
            });

    private void RequireCanMutate(MeResponse me, DynamicFormTemplate doc)
    {
        if (RoleGuard.IsSystemAdmin(me))
            return;

        if (!string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal))
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.DYNAMIC_FORM_MUTATE_FORBIDDEN,
                ForbiddenDetails("MUTATE_DYNAMIC_FORM"));
    }

    private static void RequireCanUpdateStatisticConfig(
        MeResponse me,
        DynamicFormTemplate doc,
        bool isSystemAdmin)
    {
        if (isSystemAdmin)
            return;

        if (!string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal))
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.DYNAMIC_FORM_STATISTIC_CONFIG_FORBIDDEN,
                ForbiddenDetails("UPDATE_DYNAMIC_FORM_STATISTICS"));
    }

    private static string BuildStatisticConfigMonthKey(DateTime utc)
        => utc.ToString("yyyy-MM");

    private static void EnsureDraftTemplate(DynamicFormTemplate doc)
    {
        if (doc.IsPublished)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FORM_DRAFT_REQUIRED,
                DynamicFormDetails(doc));
    }

    private async Task EnsureNotLinkedToRuntimeAsync(string templateId, CancellationToken ct)
    {
        if (await ContainsDynamicFormTemplateIdAsync(_ctx.WorkAssignments.CollectionNamespace.CollectionName, templateId, ct))
            throw DynamicFormInUse(AppErrorCode.DYNAMIC_FORM_IN_USE_BY_ASSIGNMENT, templateId);

        if (await ContainsDynamicFormTemplateIdAsync(_ctx.WorkTemplateAssignees.CollectionNamespace.CollectionName, templateId, ct))
            throw DynamicFormInUse(AppErrorCode.DYNAMIC_FORM_IN_USE_BY_BINDING, templateId);

        if (await ContainsDynamicFormTemplateIdAsync(_ctx.WorkReportPeriods.CollectionNamespace.CollectionName, templateId, ct))
            throw DynamicFormInUse(AppErrorCode.DYNAMIC_FORM_IN_USE_BY_PERIOD, templateId);

        if (await ContainsDynamicFormTemplateIdAsync(_ctx.WorkAssignmentReports.CollectionNamespace.CollectionName, templateId, ct))
            throw DynamicFormInUse(AppErrorCode.DYNAMIC_FORM_IN_USE_BY_REPORT, templateId);

        if (await ContainsDynamicFlowReferenceAsync(templateId, ct))
            throw DynamicFormInUse(AppErrorCode.DYNAMIC_FORM_IN_USE_BY_FLOW, templateId);
    }

    private async Task<bool> ContainsDynamicFormTemplateIdAsync(
        string collectionName,
        string templateId,
        CancellationToken ct)
    {
        var collection = _ctx.Db.GetCollection<BsonDocument>(collectionName);
        var f = Builders<BsonDocument>.Filter;
        var idFilter = BuildObjectIdOrStringFilter(f, "dynamicFormTemplateId", templateId);
        var filter = idFilter & f.Ne("isDeleted", true);

        return await collection.Find(filter).Limit(1).AnyAsync(ct);
    }

    private async Task<bool> ContainsDynamicFlowReferenceAsync(
        string templateId,
        CancellationToken ct)
    {
        var f = Builders<BsonDocument>.Filter;
        var active = f.Ne("isDeleted", true);
        var rootReference = BuildObjectIdOrStringFilter(
            f,
            "rootDynamicFormTemplateId",
            templateId);

        var templates = _ctx.Db.GetCollection<BsonDocument>(
            _ctx.DynamicFlowTemplates.CollectionNamespace.CollectionName);
        if (await templates.Find(active & rootReference).Limit(1).AnyAsync(ct))
            return true;

        var payloadReferencePattern =
            "\\\"(?:rootDynamicFormTemplateId|dynamicFormTemplateId|formTemplateId|" +
            "sourceDynamicFormTemplateId|sourceFormTemplateId|" +
            "targetDynamicFormTemplateId|targetFormTemplateId)\\\"" +
            "\\s*:\\s*\\\"" + Regex.Escape(templateId) + "\\\"";
        var versions = _ctx.Db.GetCollection<BsonDocument>(
            _ctx.DynamicFlowTemplateVersions.CollectionNamespace.CollectionName);
        var versionReference = rootReference |
                               f.Regex(
                                   "payloadJson",
                                   new BsonRegularExpression(payloadReferencePattern));

        return await versions.Find(active & versionReference).Limit(1).AnyAsync(ct);
    }

    private static FilterDefinition<BsonDocument> BuildObjectIdOrStringFilter(
        FilterDefinitionBuilder<BsonDocument> filters,
        string field,
        string value)
        => ObjectId.TryParse(value, out var objectId)
            ? filters.Or(filters.Eq(field, objectId), filters.Eq(field, value))
            : filters.Eq(field, value);

    private async Task<(string prefix, int nextSeq, string nextCode)> ComputeNextCodeAsync(
        int year,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var prefix = $"FORM-{me.Username}-{year}-";
        var filter = Builders<DynamicFormTemplate>.Filter.Where(x =>
            !x.IsDeleted && x.Code.StartsWith(prefix));

        var last = await _ctx.DynamicFormTemplates
            .Find(filter)
            .Sort(Builders<DynamicFormTemplate>.Sort.Descending(x => x.Code))
            .Limit(1)
            .FirstOrDefaultAsync(ct);

        var nextSeq = 1;
        if (last is not null && last.Code.Length > prefix.Length)
        {
            var tail = last.Code[prefix.Length..];
            if (int.TryParse(tail, out var seq))
                nextSeq = seq + 1;
        }

        var nextCode = prefix + nextSeq.ToString("D6");
        return (prefix, nextSeq, nextCode);
    }

    private static SortDefinition<DynamicFormTemplate> BuildSort(string? field, string? dir)
    {
        var sortField = (field ?? "createdAtUtc").Trim() switch
        {
            "code" => "code",
            "name" => "name",
            "versionNo" => "versionNo",
            "createdByUsername" => "createdByUsername",
            "updatedAtUtc" => "updatedAtUtc",
            _ => "createdAtUtc",
        };

        var sortAscending = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
        var sort = sortAscending
            ? Builders<DynamicFormTemplate>.Sort.Ascending(sortField)
            : Builders<DynamicFormTemplate>.Sort.Descending(sortField);

        var stableSorts = new List<SortDefinition<DynamicFormTemplate>> { sort };
        if (!string.Equals(sortField, "createdAtUtc", StringComparison.Ordinal))
            stableSorts.Add(Builders<DynamicFormTemplate>.Sort.Descending(x => x.CreatedAtUtc));

        stableSorts.Add(Builders<DynamicFormTemplate>.Sort.Descending("_id"));
        return Builders<DynamicFormTemplate>.Sort.Combine(stableSorts);
    }

    private static string NormalizeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_NAME_REQUIRED,
                "Ten dynamic form khong duoc trong.");

        return value.Trim();
    }

    private static string? NormalizeCode(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void EnsureTableStatisticContract(string? excelBlockJson, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(excelBlockJson))
            return;

        using var document = ParseJson(excelBlockJson, fieldName);
        var root = document.RootElement;
        if (root.ValueKind is JsonValueKind.Null)
            return;

        if (root.ValueKind != JsonValueKind.Object)
            throw DynamicFormJsonKindInvalid(
                fieldName,
                "object or null",
                root.ValueKind,
                $"{fieldName} phai la JSON object hoac null.");

        var tableMode = ReadOptionalString(root, "tableMode") ?? "FIXED_GRID";
        if (!AllowedTableModes.Contains(tableMode))
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_TABLE_MODE_INVALID,
                "Kiểu nhập bảng không hợp lệ.",
                BuildTableModeDetails(fieldName, tableMode, excelSpecKind: null));

        var excelSpecKind = ReadOptionalString(root, "excelSpecKind")
                            ?? ReadOptionalString(root, "ExcelSpecKind")
                            ?? ReadOptionalString(root, "sourceKind")
                            ?? ReadOptionalString(root, "SourceKind");
        if (!string.IsNullOrWhiteSpace(excelSpecKind)
            && !AllowedExcelSpecKinds.Contains(excelSpecKind))
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_EXCEL_BLOCK_INVALID,
                "Loại bảng Excel động không hợp lệ.",
                BuildTableModeDetails(fieldName, tableMode, excelSpecKind));

        if (!IsTableModeAllowedForExcelSpecKind(tableMode, excelSpecKind))
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_TABLE_MODE_MISMATCH,
                "Kiểu nhập bảng không phù hợp với loại bảng Excel động.",
                BuildTableModeDetails(fieldName, tableMode, excelSpecKind));

        if (root.TryGetProperty("indexMap", out var indexMap))
            ValidateIndexMap(indexMap);

        if (root.TryGetProperty("metricRules", out var metricRules))
            ValidateMetricRules(metricRules);

        ValidateMetricLabelTargets(root, fieldName);

        if (string.Equals(tableMode, "SUMMARY_TEMPLATE", StringComparison.OrdinalIgnoreCase))
            ValidateSummaryTemplateLayout(root);

        static void ValidateIndexMap(JsonElement indexMap)
        {
            if (indexMap.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return;

            if (indexMap.ValueKind != JsonValueKind.Array)
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_TABLE_CONTRACT_INVALID,
                    "indexMap phai la JSON array.",
                    new { propertyName = "indexMap", actualKind = indexMap.ValueKind.ToString() });

            foreach (var item in indexMap.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw DynamicFormValidation(
                        AppErrorCode.DYNAMIC_FORM_TABLE_CONTRACT_INVALID,
                        "indexMap item phai la JSON object.",
                        new { propertyName = "indexMap", actualKind = item.ValueKind.ToString() });

                var metricKey = ReadOptionalString(item, "metricKey");
                if (string.IsNullOrWhiteSpace(metricKey))
                    throw DynamicFormValidation(
                        AppErrorCode.DYNAMIC_FORM_METRIC_KEY_INVALID,
                        "indexMap.metricKey khong duoc trong.",
                        new { propertyName = "indexMap.metricKey" });

                ValidateMetricKey(metricKey);
            }
        }

        static void ValidateMetricRules(JsonElement metricRules)
        {
            if (metricRules.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return;

            if (metricRules.ValueKind != JsonValueKind.Array)
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_TABLE_CONTRACT_INVALID,
                    "metricRules phai la JSON array.",
                    new { propertyName = "metricRules", actualKind = metricRules.ValueKind.ToString() });

            foreach (var item in metricRules.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw DynamicFormValidation(
                        AppErrorCode.DYNAMIC_FORM_TABLE_CONTRACT_INVALID,
                        "metricRules item phai la JSON object.",
                        new { propertyName = "metricRules", actualKind = item.ValueKind.ToString() });

                var metricKey = ReadOptionalString(item, "metricKey");
                if (string.IsNullOrWhiteSpace(metricKey))
                    throw DynamicFormValidation(
                        AppErrorCode.DYNAMIC_FORM_METRIC_KEY_INVALID,
                        "metricRules.metricKey khong duoc trong.",
                        new { propertyName = "metricRules.metricKey" });

                ValidateMetricKey(metricKey);
            }
        }
    }

    private static void ValidateMetricLabelTargets(JsonElement block, string fieldName)
    {
        if (!block.TryGetProperty("metricLabelTargets", out var targets)
            || targets.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (targets.ValueKind != JsonValueKind.Array)
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                "metricLabelTargets phai la JSON array.",
                new { propertyName = $"{fieldName}.metricLabelTargets", actualKind = targets.ValueKind.ToString() });

        var index = 0;
        foreach (var target in targets.EnumerateArray())
        {
            if (target.ValueKind != JsonValueKind.Object)
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                    "metricLabelTargets item phai la JSON object.",
                    new { propertyName = $"{fieldName}.metricLabelTargets[{index}]", actualKind = target.ValueKind.ToString() });

            var labelCode = ReadOptionalString(target, "statisticLabelCode");
            if (string.IsNullOrWhiteSpace(labelCode))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_LABEL_CODE_INVALID,
                    "metricLabelTargets.statisticLabelCode khong duoc trong.",
                    new { propertyName = $"{fieldName}.metricLabelTargets[{index}].statisticLabelCode" });

            NormalizeLabelCode(labelCode);

            var metricKey = ReadOptionalString(target, "metricKey");
            var hasMetric = !string.IsNullOrWhiteSpace(metricKey);
            var hasRange = TryReadMetricLabelRange(target, out _);

            if (hasMetric == hasRange)
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                    "metricLabelTargets can dung dung mot trong hai kieu: metricKey hoac range.",
                    new { propertyName = $"{fieldName}.metricLabelTargets[{index}]", hasMetric, hasRange });

            if (hasMetric)
                ValidateMetricKey(metricKey!);
            else
                ResolveMetricLabelTargetDataType(block, target, fieldName, index);

            index++;
        }
    }

    private static string ResolveMetricLabelTargetDataType(
        JsonElement block,
        JsonElement target,
        string fieldName,
        int targetIndex)
    {
        if (!TryReadMetricLabelRange(target, out var range))
        {
            var explicitType = ReadOptionalString(target, "dataType")
                               ?? ReadOptionalString(target, "targetDataType")
                               ?? ReadOptionalString(block, "defaultDataType")
                               ?? ReadOptionalString(block, "dataType");
            return LabelDataTypes.Normalize(explicitType);
        }

        var dataRect = ReadBlockDataRect(block, fieldName);
        if (!ContainsRange(dataRect, range))
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                "Vùng gắn nhãn thống kê phải nằm trong vùng dữ liệu của bảng Excel động.",
                new
                {
                    propertyName = $"{fieldName}.metricLabelTargets[{targetIndex}]",
                    range,
                    dataRect
                });

        string? resolved = null;
        for (var r = range.R0; r <= range.R1; r++)
        {
            for (var c = range.C0; c <= range.C1; c++)
            {
                var cellType = ResolveBlockCellDataType(block, r, c);
                if (resolved is null)
                {
                    resolved = cellType;
                    continue;
                }

                if (!string.Equals(resolved, cellType, StringComparison.OrdinalIgnoreCase))
                    throw DynamicFormValidation(
                        AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                        "Range gan nhan thong ke phai co cung mot kieu du lieu.",
                        new
                        {
                            propertyName = $"{fieldName}.metricLabelTargets[{targetIndex}]",
                            range,
                            firstDataType = resolved,
                            conflictDataType = cellType,
                            conflictCell = new { r, c }
                        });
            }
        }

        resolved ??= LabelDataTypes.Normalize(ReadOptionalString(block, "defaultDataType"));
        var explicitDataType = ReadOptionalString(target, "dataType")
                               ?? ReadOptionalString(target, "targetDataType");
        if (!string.IsNullOrWhiteSpace(explicitDataType))
        {
            var normalizedExplicit = LabelDataTypes.Normalize(explicitDataType);
            if (!string.Equals(resolved, normalizedExplicit, StringComparison.OrdinalIgnoreCase))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                    "Kiểu dữ liệu khai báo cho vùng không khớp kiểu dữ liệu thực của vùng dữ liệu.",
                    new
                    {
                        propertyName = $"{fieldName}.metricLabelTargets[{targetIndex}].dataType",
                        expectedDataType = resolved,
                        actualDataType = normalizedExplicit
                    });
        }

        return resolved;
    }

    private static MetricLabelRange ReadBlockDataRect(JsonElement block, string fieldName)
    {
        if (!block.TryGetProperty("dataRect", out var dataRect)
            || dataRect.ValueKind != JsonValueKind.Object)
        {
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                "Cấu hình vùng gắn nhãn thống kê cần vùng dữ liệu trong bảng Excel động.",
                new { propertyName = $"{fieldName}.dataRect" });
        }

        if (!TryReadRangeCoordinates(dataRect, out var range))
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                "Vùng dữ liệu của bảng Excel động không hợp lệ.",
                new { propertyName = $"{fieldName}.dataRect" });

        return range;
    }

    private static bool TryReadMetricLabelRange(JsonElement target, out MetricLabelRange range)
    {
        if (target.TryGetProperty("range", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            return TryReadRangeCoordinates(nested, out range);
        }

        return TryReadRangeCoordinates(target, out range);
    }

    private static bool TryReadRangeCoordinates(JsonElement element, out MetricLabelRange range)
    {
        range = default;
        var r0 = ReadOptionalNonNegativeInt(element, "r0");
        var c0 = ReadOptionalNonNegativeInt(element, "c0");
        var r1 = ReadOptionalNonNegativeInt(element, "r1");
        var c1 = ReadOptionalNonNegativeInt(element, "c1");
        if (!r0.HasValue || !c0.HasValue || !r1.HasValue || !c1.HasValue)
            return false;
        if (r1.Value < r0.Value || c1.Value < c0.Value)
            return false;

        range = new MetricLabelRange(r0.Value, c0.Value, r1.Value, c1.Value);
        return true;
    }

    private static int? ReadOptionalNonNegativeInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out var number)
               && number >= 0
            ? number
            : null;
    }

    private static bool ContainsRange(MetricLabelRange outer, MetricLabelRange inner)
        => inner.R0 >= outer.R0
           && inner.C0 >= outer.C0
           && inner.R1 <= outer.R1
           && inner.C1 <= outer.C1;

    private static string ResolveBlockCellDataType(JsonElement block, int row, int column)
    {
        var defaultType = LabelDataTypes.Normalize(
            ReadOptionalString(block, "defaultDataType")
            ?? ReadOptionalString(block, "dataType"));
        var specKind = ReadOptionalString(block, "excelSpecKind")
                       ?? ReadOptionalString(block, "kind");

        if (!block.TryGetProperty("dataTypeOverrides", out var overrides)
            || overrides.ValueKind != JsonValueKind.Array)
        {
            return defaultType;
        }

        var resolved = defaultType;
        foreach (var item in overrides.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var scope = ReadOptionalString(item, "scope")?.ToUpperInvariant();
            var dataType = LabelDataTypes.Normalize(ReadOptionalString(item, "dataType"));
            if (string.Equals(scope, "COLUMN", StringComparison.Ordinal)
                && string.Equals(specKind, "TOP", StringComparison.OrdinalIgnoreCase)
                && ReadOptionalNonNegativeInt(item, "index") == column)
            {
                resolved = dataType;
                continue;
            }

            if (string.Equals(scope, "ROW", StringComparison.Ordinal)
                && string.Equals(specKind, "LEFT", StringComparison.OrdinalIgnoreCase)
                && ReadOptionalNonNegativeInt(item, "index") == row)
            {
                resolved = dataType;
                continue;
            }

            if (string.Equals(scope, "RANGE", StringComparison.Ordinal)
                && TryReadRangeCoordinates(item, out var range)
                && row >= range.R0
                && row <= range.R1
                && column >= range.C0
                && column <= range.C1)
            {
                resolved = dataType;
            }
        }

        return resolved;
    }

    private static void EnsureBlocksTableStatisticContract(string? blocksJson, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(blocksJson))
            return;

        using var document = ParseJson(blocksJson, fieldName);
        var root = document.RootElement;
        if (root.ValueKind is JsonValueKind.Null)
            return;

        if (root.ValueKind != JsonValueKind.Array)
            throw DynamicFormJsonKindInvalid(
                fieldName,
                "array or null",
                root.ValueKind,
                $"{fieldName} phai la JSON array hoac null.");

        var index = 0;
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw DynamicFormJsonKindInvalid(
                    $"{fieldName}[{index}]",
                    "object",
                    item.ValueKind,
                    $"{fieldName}[{index}] phai la JSON object.");

            EnsureTableStatisticContract(item.GetRawText(), $"{fieldName}[{index}]");
            index++;
        }
    }

    private static void ValidateSummaryTemplateLayout(JsonElement root)
    {
        var outputLayout = default(JsonElement);
        var hasOutputLayout = root.TryGetProperty("outputLayout", out outputLayout)
                              && outputLayout.ValueKind != JsonValueKind.Null
                              && outputLayout.ValueKind != JsonValueKind.Undefined;

        if (hasOutputLayout && outputLayout.ValueKind != JsonValueKind.Object)
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_SUMMARY_LAYOUT_INVALID,
                "SUMMARY_TEMPLATE.outputLayout phai la JSON object.",
                new { propertyName = "SUMMARY_TEMPLATE.outputLayout", actualKind = outputLayout.ValueKind.ToString() });

        var sourceBlockId = ReadOptionalString(root, "sourceBlockId")
                            ?? (hasOutputLayout ? ReadOptionalString(outputLayout, "sourceBlockId") : null);
        if (string.IsNullOrWhiteSpace(sourceBlockId))
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_SUMMARY_SOURCE_REQUIRED,
                "SUMMARY_TEMPLATE.sourceBlockId khong duoc trong.",
                new { propertyName = "SUMMARY_TEMPLATE.sourceBlockId" });

        if (root.TryGetProperty("groupBy", out var groupBy))
        {
            ValidateSummaryGroupBy(groupBy);
        }
        else if (hasOutputLayout && outputLayout.TryGetProperty("groupBy", out var outputGroupBy))
        {
            ValidateSummaryGroupBy(outputGroupBy);
        }

        JsonElement rowLayout;
        if (root.TryGetProperty("rowLayout", out rowLayout))
        {
            ValidateSummaryRowLayout(rowLayout);
            return;
        }

        if (hasOutputLayout && outputLayout.TryGetProperty("rowLayout", out rowLayout))
        {
            ValidateSummaryRowLayout(rowLayout);
            return;
        }

        throw DynamicFormValidation(
            AppErrorCode.DYNAMIC_FORM_SUMMARY_LAYOUT_REQUIRED,
            "SUMMARY_TEMPLATE.rowLayout khong duoc trong.",
            new { propertyName = "SUMMARY_TEMPLATE.rowLayout" });
    }

    private static void ValidateSummaryGroupBy(JsonElement groupBy)
    {
        if (groupBy.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;

        if (groupBy.ValueKind != JsonValueKind.Array)
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_SUMMARY_GROUP_BY_INVALID,
                "SUMMARY_TEMPLATE.groupBy phai la JSON array.",
                new { propertyName = "SUMMARY_TEMPLATE.groupBy", actualKind = groupBy.ValueKind.ToString() });

        foreach (var item in groupBy.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_SUMMARY_GROUP_BY_INVALID,
                    "SUMMARY_TEMPLATE.groupBy item phai la string.",
                    new { propertyName = "SUMMARY_TEMPLATE.groupBy", actualKind = item.ValueKind.ToString() });

            var value = item.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(value) || !AllowedSummaryTemplateGroupBy.Contains(value))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_SUMMARY_GROUP_BY_INVALID,
                    $"SUMMARY_TEMPLATE.groupBy khong hop le: {value}.",
                    new { groupBy = value });
        }
    }

    private static void ValidateSummaryRowLayout(JsonElement rowLayout)
    {
        if (rowLayout.ValueKind != JsonValueKind.Array)
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_SUMMARY_LAYOUT_INVALID,
                "SUMMARY_TEMPLATE.rowLayout phai la JSON array.",
                new { propertyName = "SUMMARY_TEMPLATE.rowLayout", actualKind = rowLayout.ValueKind.ToString() });

        var count = 0;
        foreach (var item in rowLayout.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_SUMMARY_LAYOUT_INVALID,
                    "SUMMARY_TEMPLATE.rowLayout item phai la JSON object.",
                    new { propertyName = "SUMMARY_TEMPLATE.rowLayout", actualKind = item.ValueKind.ToString() });

            count++;

            var repeatFor = ReadOptionalString(item, "repeatFor");
            if (!string.IsNullOrWhiteSpace(repeatFor) && !AllowedSummaryTemplateRepeatFor.Contains(repeatFor))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_SUMMARY_LAYOUT_INVALID,
                    $"SUMMARY_TEMPLATE.rowLayout.repeatFor khong hop le: {repeatFor}.",
                    new { repeatFor });

            if (item.TryGetProperty("rowsPerUnit", out var rowsPerUnit)
                && rowsPerUnit.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                if (rowsPerUnit.ValueKind != JsonValueKind.Number
                    || !rowsPerUnit.TryGetInt32(out var rows)
                    || rows < 1
                    || rows > 100)
                {
                    throw DynamicFormValidation(
                        AppErrorCode.DYNAMIC_FORM_SUMMARY_LAYOUT_INVALID,
                        "SUMMARY_TEMPLATE.rowLayout.rowsPerUnit phai nam trong 1..100.",
                        new { propertyName = "SUMMARY_TEMPLATE.rowLayout.rowsPerUnit" });
                }
            }

            if (!item.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Array)
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_SUMMARY_METRICS_REQUIRED,
                    "SUMMARY_TEMPLATE.rowLayout.metrics phai la JSON array.",
                    new { propertyName = "SUMMARY_TEMPLATE.rowLayout.metrics" });

            var metricCount = 0;
            foreach (var metric in metrics.EnumerateArray())
            {
                if (metric.ValueKind != JsonValueKind.String)
                    throw DynamicFormValidation(
                        AppErrorCode.DYNAMIC_FORM_SUMMARY_METRICS_REQUIRED,
                        "SUMMARY_TEMPLATE.rowLayout.metrics item phai la string.",
                        new { propertyName = "SUMMARY_TEMPLATE.rowLayout.metrics", actualKind = metric.ValueKind.ToString() });

                var metricKey = metric.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(metricKey))
                    throw DynamicFormValidation(
                        AppErrorCode.DYNAMIC_FORM_METRIC_KEY_INVALID,
                        "SUMMARY_TEMPLATE.rowLayout.metrics khong duoc chua chuoi trong.",
                        new { propertyName = "SUMMARY_TEMPLATE.rowLayout.metrics" });

                ValidateMetricKey(metricKey);
                metricCount++;
            }

            if (metricCount == 0)
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_SUMMARY_METRICS_REQUIRED,
                    "SUMMARY_TEMPLATE.rowLayout.metrics khong duoc trong.",
                    new { propertyName = "SUMMARY_TEMPLATE.rowLayout.metrics" });
        }

        if (count == 0)
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_SUMMARY_LAYOUT_REQUIRED,
                "SUMMARY_TEMPLATE.rowLayout khong duoc trong.",
                new { propertyName = "SUMMARY_TEMPLATE.rowLayout" });
    }

    private static string? ReadOptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return value.GetString()?.Trim();
    }

    private static string? ReadOptionalString(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is not JsonValue value)
            return null;

        return value.TryGetValue<string>(out var text) ? text?.Trim() : null;
    }

    private static bool IsTableModeAllowedForExcelSpecKind(string tableMode, string? excelSpecKind)
    {
        if (string.Equals(tableMode, "SUMMARY_TEMPLATE", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(excelSpecKind))
            return true;

        if (string.Equals(excelSpecKind, "TOP", StringComparison.OrdinalIgnoreCase))
            return string.Equals(tableMode, "FIXED_GRID", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(tableMode, "APPEND_ROWS", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(excelSpecKind, "LEFT", StringComparison.OrdinalIgnoreCase))
            return string.Equals(tableMode, "FIXED_GRID", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(tableMode, "APPEND_COLUMNS", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(excelSpecKind, "MATRIX", StringComparison.OrdinalIgnoreCase))
            return string.Equals(tableMode, "FIXED_GRID", StringComparison.OrdinalIgnoreCase);

        return true;
    }

    private static object BuildTableModeDetails(string fieldName, string? tableMode, string? excelSpecKind)
        => new
        {
            fieldName,
            tableMode,
            tableModeLabel = DescribeTableMode(tableMode),
            excelSpecKind,
            excelSpecKindLabel = DescribeExcelSpecKind(excelSpecKind),
            allowedTableModes = GetAllowedTableModesForExcelSpecKind(excelSpecKind)
                .Select(mode => new { value = mode, label = DescribeTableMode(mode) })
                .ToArray(),
        };

    private static string[] GetAllowedTableModesForExcelSpecKind(string? excelSpecKind)
    {
        if (string.Equals(excelSpecKind, "TOP", StringComparison.OrdinalIgnoreCase))
            return new[] { "FIXED_GRID", "APPEND_ROWS" };

        if (string.Equals(excelSpecKind, "LEFT", StringComparison.OrdinalIgnoreCase))
            return new[] { "FIXED_GRID", "APPEND_COLUMNS" };

        if (string.Equals(excelSpecKind, "MATRIX", StringComparison.OrdinalIgnoreCase))
            return new[] { "FIXED_GRID" };

        return new[] { "FIXED_GRID", "APPEND_ROWS", "APPEND_COLUMNS", "MATRIX", "SUMMARY_TEMPLATE" };
    }

    private static string DescribeTableMode(string? tableMode)
        => tableMode?.Trim().ToUpperInvariant() switch
        {
            "FIXED_GRID" => "Bảng cố định",
            "APPEND_ROWS" => "Thêm theo dòng",
            "APPEND_COLUMNS" => "Thêm theo cột",
            "MATRIX" => "Bảng ma trận",
            "SUMMARY_TEMPLATE" => "Mẫu tổng hợp",
            null or "" => "Chưa chọn",
            _ => tableMode!,
        };

    private static string DescribeExcelSpecKind(string? excelSpecKind)
        => excelSpecKind?.Trim().ToUpperInvariant() switch
        {
            "TOP" => "Bảng ngang",
            "LEFT" => "Bảng dọc",
            "MATRIX" => "Bảng ma trận",
            null or "" => "Chưa xác định",
            _ => excelSpecKind!,
        };

    private static string? ReadDynamicExcelSpecKind(string? specJson)
    {
        if (string.IsNullOrWhiteSpace(specJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(specJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var kind = ReadOptionalString(document.RootElement, "kind");
            return !string.IsNullOrWhiteSpace(kind) && AllowedExcelSpecKinds.Contains(kind)
                ? kind.ToUpperInvariant()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidateMetricKey(string metricKey)
    {
        if (!MetricKeyRegex.IsMatch(metricKey))
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_METRIC_KEY_INVALID,
                "metricKey chi gom chu, so, dau ., _, :, - va toi da 256 ky tu.",
                new { metricKey });
    }

    private static DynamicFormExcelBlockSnapshot BuildDynamicExcelBlockSnapshot(
        DynamicExcelTemplate excel,
        string sectionId)
    {
        var blockId = $"excel_{excel.Id}";
        var typeMetadata = ReadDynamicExcelTypeMetadata(excel.SpecJson);
        var specKind = ReadDynamicExcelSpecKind(excel.SpecJson);
        var tableMode = NormalizeDynamicExcelTemplateTableMode(excel.TableMode, specKind);
        var statisticsInputCellCount = CountDynamicExcelTemplateInputCells(excel);
        var statisticsDisabled = DynamicExcelRuntimePolicy.ShouldDisableBackgroundTableStatistics(statisticsInputCellCount);
        return new DynamicFormExcelBlockSnapshot(
            excel.Id,
            excel.Code,
            excel.Name,
            new DynamicExcelDataRectDto(
                excel.DataRectR0,
                excel.DataRectC0,
                excel.DataRectR1,
                excel.DataRectC1),
            excel.W,
            excel.H,
            blockId,
            sectionId,
            tableMode,
            Array.Empty<DynamicFormTableIndexMapItem>(),
            specKind,
            typeMetadata.DefaultDataType,
            typeMetadata.DefaultOptions,
            typeMetadata.DataTypeOverrides,
            typeMetadata.SpecialRanges,
            statisticsDisabled,
            statisticsInputCellCount,
            DynamicExcelRuntimePolicy.MaxBackgroundTableStatisticInputCells,
            statisticsDisabled
                ? DynamicExcelRuntimePolicy.BuildBackgroundTableStatisticsDisabledReason(statisticsInputCellCount)
                : null);
    }

    private static string NormalizeDynamicExcelTemplateTableMode(string? tableMode, string? specKind)
    {
        var normalized = string.IsNullOrWhiteSpace(tableMode)
            ? "FIXED_GRID"
            : tableMode.Trim().ToUpperInvariant();

        var ok = specKind?.Trim().ToUpperInvariant() switch
        {
            "TOP" => normalized is "FIXED_GRID" or "APPEND_ROWS",
            "LEFT" => normalized is "FIXED_GRID" or "APPEND_COLUMNS",
            "MATRIX" => normalized is "FIXED_GRID",
            _ => false
        };

        return ok ? normalized : "FIXED_GRID";
    }

    private static DynamicExcelTypeMetadata ReadDynamicExcelTypeMetadata(string? specJson)
    {
        const string fallbackType = LabelDataTypes.Number;
        if (string.IsNullOrWhiteSpace(specJson))
            return new DynamicExcelTypeMetadata(fallbackType, Array.Empty<JsonElement>(), Array.Empty<JsonElement>(), Array.Empty<JsonElement>());

        try
        {
            using var document = JsonDocument.Parse(specJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new DynamicExcelTypeMetadata(fallbackType, Array.Empty<JsonElement>(), Array.Empty<JsonElement>(), Array.Empty<JsonElement>());

            var defaultType = NormalizeDynamicExcelMetadataDataType(
                ReadOptionalString(document.RootElement, "defaultDataType"));

            var defaultOptions = Array.Empty<JsonElement>();
            if (document.RootElement.TryGetProperty("defaultOptions", out var defaultOptionsElement)
                && defaultOptionsElement.ValueKind == JsonValueKind.Array)
            {
                defaultOptions = defaultOptionsElement
                    .EnumerateArray()
                    .Where(item => item.ValueKind is JsonValueKind.Object or JsonValueKind.String)
                    .Select(item => item.Clone())
                    .ToArray();
            }

            var overrides = Array.Empty<JsonElement>();
            if (document.RootElement.TryGetProperty("dataTypeOverrides", out var overrideElement)
                && overrideElement.ValueKind == JsonValueKind.Array)
            {
                overrides = overrideElement
                    .EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .Select(item => item.Clone())
                    .ToArray();
            }

            var specialRanges = Array.Empty<JsonElement>();
            if (document.RootElement.TryGetProperty("specialRanges", out var specialRangeElement)
                && specialRangeElement.ValueKind == JsonValueKind.Array)
            {
                specialRanges = specialRangeElement
                    .EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .Select(item => item.Clone())
                    .ToArray();
            }

            return new DynamicExcelTypeMetadata(defaultType, defaultOptions, overrides, specialRanges);
        }
        catch (JsonException)
        {
            return new DynamicExcelTypeMetadata(fallbackType, Array.Empty<JsonElement>(), Array.Empty<JsonElement>(), Array.Empty<JsonElement>());
        }
    }

    private static int CountDynamicExcelTemplateInputCells(DynamicExcelTemplate template)
    {
        var dataRect = new DynamicExcelRuntimeRect(
            template.DataRectR0,
            template.DataRectC0,
            template.DataRectR1,
            template.DataRectC1);

        if (string.IsNullOrWhiteSpace(template.SpecJson))
            return DynamicExcelRuntimePolicy.CountInputCells(dataRect);

        try
        {
            using var document = JsonDocument.Parse(template.SpecJson);
            var specialRanges = DynamicExcelRuntimePolicy.ReadSpecialRanges(document.RootElement, dataRect);
            return DynamicExcelRuntimePolicy.CountInputCells(dataRect, specialRanges);
        }
        catch (JsonException)
        {
            return DynamicExcelRuntimePolicy.CountInputCells(dataRect);
        }
    }

    private sealed record DynamicExcelTypeMetadata(
        string DefaultDataType,
        JsonElement[] DefaultOptions,
        JsonElement[] DataTypeOverrides,
        JsonElement[] SpecialRanges);

    private static string NormalizeDynamicExcelMetadataDataType(string? value)
    {
        var raw = value?.Trim().ToUpperInvariant();
        if (raw is "FULL_DATE" or "FULLDATE" or "STRICT_DATE")
            return "FULL_DATE";
        if (raw is "MULTI_SELECT" or "MULTISELECT")
            return "MULTI_SELECT";
        if (raw is "IGNORE" or "IGNORED" or "SKIP")
            return "IGNORE";

        var normalized = LabelDataTypes.Normalize(value);
        return normalized == LabelDataTypes.LongText ? LabelDataTypes.StringList : normalized;
    }

    private async Task EnsureLabelReferencesAsync(
        MeResponse me,
        string[]? formTagCodes,
        string? sectionsJson,
        string? fieldsJson,
        string? excelBlockJson,
        string? blocksJson,
        CancellationToken ct)
    {
        var codes = new HashSet<string>(NormalizeLabelCodes(formTagCodes), StringComparer.OrdinalIgnoreCase);
        CollectLabelReferenceCodes(sectionsJson, codes);
        CollectLabelReferenceCodes(fieldsJson, codes);
        CollectLabelReferenceCodes(excelBlockJson, codes);
        CollectLabelReferenceCodes(blocksJson, codes);

        if (codes.Count == 0)
            return;

        var fb = Builders<LabelCatalogItem>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(x => x.IsActive, true)
                     & fb.In(x => x.Code, codes)
                     & BuildLabelVisibilityFilter(me);

        var found = await _ctx.Labels
            .Find(filter)
            .Project(x => new { x.Code, x.DataType, x.Usage })
            .ToListAsync(ct);

        var foundTypes = found
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => LabelDataTypes.Normalize(x.First().DataType),
                StringComparer.OrdinalIgnoreCase);
        var foundUsages = found
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => LabelUsages.Normalize(x.First().Usage, LabelUsages.Classification),
                StringComparer.OrdinalIgnoreCase);
        var foundSet = new HashSet<string>(foundTypes.Keys, StringComparer.OrdinalIgnoreCase);
        var missing = codes
            .Where(code => !foundSet.Contains(code))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missing.Length > 0)
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_LABEL_NOT_FOUND_OR_INACTIVE,
                "Nhan khong ton tai, inactive hoac ngoai pham vi.",
                new { missing });

        EnsureLabelUsageCompatibility(
            formTagCodes,
            sectionsJson,
            fieldsJson,
            excelBlockJson,
            blocksJson,
            foundTypes,
            foundUsages);
    }

    private static FilterDefinition<LabelCatalogItem> BuildLabelVisibilityFilter(MeResponse me)
    {
        var fb = Builders<LabelCatalogItem>.Filter;
        if (RoleGuard.IsSystemAdmin(me))
            return FilterDefinition<LabelCatalogItem>.Empty;

        var scopes = new List<FilterDefinition<LabelCatalogItem>>
        {
            fb.Eq(x => x.ScopeType, LabelScopeTypes.Global)
        };

        if (!string.IsNullOrWhiteSpace(me.UnitId))
            scopes.Add(fb.Eq(x => x.ScopeType, LabelScopeTypes.Unit) & fb.Eq(x => x.ScopeId, me.UnitId));

        if (RoleGuard.TryGetManagerUnit(me, out var managedUnitId))
            scopes.Add(fb.Eq(x => x.ScopeType, LabelScopeTypes.Unit) & fb.Eq(x => x.ScopeId, managedUnitId));

        if (RoleGuard.IsManagerLevel(me) && !string.IsNullOrWhiteSpace(me.UnitId))
            scopes.Add(fb.Eq(x => x.ScopeType, LabelScopeTypes.Level) & fb.Eq(x => x.ScopeId, me.UnitId));

        return fb.Or(scopes);
    }

    private static void CollectLabelReferenceCodes(string? json, HashSet<string> target)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            using var document = JsonDocument.Parse(json);
            CollectLabelReferenceCodes(document.RootElement, target);
        }
        catch (JsonException)
        {
            return;
        }
    }

    private static void CollectLabelReferenceCodes(JsonElement element, HashSet<string> target)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (IsLabelCodeProperty(property.Name))
                {
                    AddLabelCodes(property.Value, target);
                    continue;
                }

                CollectLabelReferenceCodes(property.Value, target);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectLabelReferenceCodes(item, target);
        }
    }

    private static bool IsLabelCodeProperty(string name)
        => string.Equals(name, "tagCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "statisticLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "allowedRowLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "rowLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "statisticLabelCode", StringComparison.OrdinalIgnoreCase);

    private static void AddLabelCodes(JsonElement element, HashSet<string> target)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            target.Add(NormalizeLabelCode(element.GetString()));
            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                target.Add(NormalizeLabelCode(item.GetString()));
        }
    }

    private static void EnsureUniqueLabelStatisticTargets(string? fieldsJson, string? blocksJson)
    {
        var targetsByLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var targetCount = 0;

        targetCount += CountStatisticFields(fieldsJson);
        foreach (var field in ReadFieldStatisticTargets(fieldsJson))
        {
            AddUniqueLabelTarget(targetsByLabel, field.LabelCode, field.Target);
        }

        var metricTargets = ReadMetricLabelTargets(blocksJson).ToList();
        targetCount += metricTargets.Count;
        foreach (var column in metricTargets)
        {
            AddUniqueLabelTarget(targetsByLabel, column.LabelCode, column.Target);
        }

        if (targetCount > MaxLabelStatisticTargetsPerForm)
        {
            throw DynamicFormLimitExceeded(
                "labelStatisticTargets",
                MaxLabelStatisticTargetsPerForm,
                targetCount,
                $"Dynamic Form chi toi da {MaxLabelStatisticTargetsPerForm} field/column gan nhan thong ke.");
        }
    }

    private static void EnsureLabelUsageCompatibility(
        string[]? formTagCodes,
        string? sectionsJson,
        string? fieldsJson,
        string? excelBlockJson,
        string? blocksJson,
        IReadOnlyDictionary<string, string> labelDataTypes,
        IReadOnlyDictionary<string, string> labelUsages)
    {
        foreach (var code in NormalizeLabelCodes(formTagCodes))
            EnsureLabelHasUsage(
                code,
                "form.tagCodes",
                LabelUsages.Classification,
                labelUsages,
                "Nhan gan tag bieu mau phai co usage=CLASSIFICATION.");

        foreach (var target in ReadLabelPropertyTargets("SectionsJson", sectionsJson, "tagCodes")
                     .Concat(ReadLabelPropertyTargets("FieldsJson", fieldsJson, "tagCodes"))
                     .Concat(ReadLabelPropertyTargets("ExcelBlockJson", excelBlockJson, "tagCodes"))
                     .Concat(ReadLabelPropertyTargets("BlocksJson", blocksJson, "tagCodes")))
        {
            EnsureLabelHasUsage(
                target.LabelCode,
                target.Target,
                LabelUsages.Classification,
                labelUsages,
                "Nhan gan tag phai co usage=CLASSIFICATION.");
        }

        EnsureStatisticLabelTypeCompatibility(fieldsJson, blocksJson, labelDataTypes, labelUsages);
        EnsureTableTargetLabelTypeCompatibility(excelBlockJson, blocksJson, labelDataTypes, labelUsages);
    }

    private static void EnsureStatisticLabelTypeCompatibility(
        string? fieldsJson,
        string? blocksJson,
        IReadOnlyDictionary<string, string> labelDataTypes,
        IReadOnlyDictionary<string, string> labelUsages)
    {
        foreach (var target in ReadFieldStatisticTargets(fieldsJson))
        {
            if (string.IsNullOrWhiteSpace(target.ExpectedDataType))
                continue;

            EnsureLabelCanBeStatistic(target, labelUsages);

            if (!labelDataTypes.TryGetValue(target.LabelCode, out var actualDataType))
                continue;

            var expectedDataType = LabelDataTypes.Normalize(target.ExpectedDataType);
            if (!string.Equals(actualDataType, expectedDataType, StringComparison.OrdinalIgnoreCase))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                    "Kieu du lieu cua nhan thong ke khong khop voi field.",
                    new
                    {
                        target = target.Target,
                        labelCode = target.LabelCode,
                        expectedDataType,
                        actualDataType
                    });
        }

        foreach (var target in ReadMetricLabelTargets(blocksJson))
        {
            EnsureLabelCanBeStatistic(target, labelUsages);

            if (!labelDataTypes.TryGetValue(target.LabelCode, out var actualDataType))
                continue;

            var expectedDataType = LabelDataTypes.Normalize(target.ExpectedDataType);
            if (!string.Equals(actualDataType, expectedDataType, StringComparison.OrdinalIgnoreCase))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                    "Kieu du lieu cua nhan thong ke bang khong khop voi vung/metric duoc gan.",
                    new
                    {
                        target = target.Target,
                        labelCode = target.LabelCode,
                        expectedDataType,
                        actualDataType
                    });
        }
    }

    private static void EnsureLabelCanBeStatistic(
        LabelStatisticTarget target,
        IReadOnlyDictionary<string, string> labelUsages)
    {
        EnsureLabelHasUsage(
            target.LabelCode,
            target.Target,
            LabelUsages.Statistic,
            labelUsages,
            "Nhan gan vao muc tieu thong ke phai co usage=STATISTIC.");
    }

    private static void EnsureTableTargetLabelTypeCompatibility(
        string? excelBlockJson,
        string? blocksJson,
        IReadOnlyDictionary<string, string> labelDataTypes,
        IReadOnlyDictionary<string, string> labelUsages)
    {
        foreach (var target in ReadTableTargetLabelTargets("ExcelBlockJson", excelBlockJson)
                     .Concat(ReadTableTargetLabelTargets("BlocksJson", blocksJson)))
        {
            EnsureLabelHasUsage(
                target.LabelCode,
                target.Target,
                LabelUsages.TableTarget,
                labelUsages,
                "Nhan gan vao vi tri bang phai co usage=TABLE_TARGET.");

            if (string.IsNullOrWhiteSpace(target.ExpectedDataType))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                    "Nhan vi tri bang phai khai bao kieu du lieu target.",
                    new
                    {
                        target = target.Target,
                        labelCode = target.LabelCode,
                        expectedFields = new[] { "rowLabelDataType", "columnLabelDataType", "cellLabelDataType", "rangeLabelDataType", "targetDataType", "defaultDataType" }
                    });

            if (!labelDataTypes.TryGetValue(target.LabelCode, out var actualDataType))
                continue;

            var expectedDataType = LabelDataTypes.Normalize(target.ExpectedDataType);
            if (!string.Equals(actualDataType, expectedDataType, StringComparison.OrdinalIgnoreCase))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                    "Kieu du lieu cua nhan vi tri bang khong khop voi Dynamic Excel target.",
                    new
                    {
                        target = target.Target,
                        labelCode = target.LabelCode,
                        expectedDataType,
                        actualDataType
                    });
        }
    }

    private static void EnsureLabelHasUsage(
        string labelCode,
        string target,
        string expectedUsage,
        IReadOnlyDictionary<string, string> labelUsages,
        string message)
    {
        if (!labelUsages.TryGetValue(labelCode, out var usage))
            return;

        var normalizedUsage = LabelUsages.Normalize(usage, string.Empty);
        if (string.Equals(normalizedUsage, expectedUsage, StringComparison.Ordinal))
            return;

        throw DynamicFormValidation(
            AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
            message,
            new
            {
                target,
                labelCode,
                expectedUsage,
                actualUsage = usage
            });
    }

    private static void EnsureFieldSchemaContract(string? fieldsJson, string sectionsJson)
    {
        var sectionIds = ReadSectionIds(sectionsJson).ToHashSet(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(fieldsJson))
            return;

        using var document = ParseJson(fieldsJson, "FieldsJson");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw DynamicFormJsonKindInvalid(
                "FieldsJson",
                "array",
                document.RootElement.ValueKind,
                "FieldsJson phai la JSON array.");

        var fieldIds = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var field in document.RootElement.EnumerateArray())
        {
            var path = $"FieldsJson[{index}]";
            if (field.ValueKind != JsonValueKind.Object)
                throw DynamicFormFieldConfigInvalid(
                    path,
                    $"{path} phai la JSON object.",
                    new { actualKind = field.ValueKind.ToString() });

            var id = ReadOptionalString(field, "id");
            if (string.IsNullOrWhiteSpace(id))
                throw DynamicFormFieldConfigInvalid(
                    $"{path}.id",
                    "Field id khong duoc trong.");
            if (!fieldIds.Add(id))
                throw DynamicFormFieldConfigInvalid(
                    $"{path}.id",
                    "Field id bi trung trong cung Dynamic Form.",
                    new { fieldId = id });

            var sectionId = ReadOptionalString(field, "sectionId");
            if (string.IsNullOrWhiteSpace(sectionId) || !sectionIds.Contains(sectionId))
                throw DynamicFormFieldConfigInvalid(
                    $"{path}.sectionId",
                    "Field phai tro den section ton tai trong Dynamic Form.",
                    new { fieldId = id, sectionId });

            var type = ReadOptionalString(field, "type");
            if (string.IsNullOrWhiteSpace(type) || !AllowedFieldTypes.Contains(type))
                throw DynamicFormFieldConfigInvalid(
                    $"{path}.type",
                    "Field type nam ngoai 10 kieu duoc cong bo.",
                    new { fieldId = id, type, allowedTypes = AllowedFieldTypes.OrderBy(x => x, StringComparer.Ordinal).ToArray() });

            EnsureOptionalBooleanProperty(field, "required", path, id);
            EnsureOptionalBooleanProperty(field, "isStatistic", path, id);

            var hasFieldOptions = field.TryGetProperty("options", out var fieldOptions)
                                  && fieldOptions.ValueKind != JsonValueKind.Null;
            var fieldOptionCount = ValidateFieldOptions(field, "options", path, id);
            var hasValueSource = field.TryGetProperty("valueSource", out var valueSource)
                                 && valueSource.ValueKind != JsonValueKind.Null;
            var valueSourceType = (string?)null;
            var sourceOptionCount = 0;

            if (hasValueSource)
            {
                if (valueSource.ValueKind != JsonValueKind.Object)
                    throw DynamicFormFieldConfigInvalid(
                        $"{path}.valueSource",
                        "valueSource phai la JSON object.",
                        new { fieldId = id, actualKind = valueSource.ValueKind.ToString() });

                valueSourceType = ReadOptionalString(valueSource, "sourceType");
                if (string.IsNullOrWhiteSpace(valueSourceType) || !AllowedFieldValueSourceTypes.Contains(valueSourceType))
                    throw DynamicFormFieldConfigInvalid(
                        $"{path}.valueSource.sourceType",
                        "valueSource.sourceType nam ngoai 6 nguon duoc cong bo.",
                        new
                        {
                            fieldId = id,
                            sourceType = valueSourceType,
                            allowedSourceTypes = AllowedFieldValueSourceTypes.OrderBy(x => x, StringComparer.Ordinal).ToArray()
                        });

                sourceOptionCount = ValidateFieldOptions(valueSource, "options", $"{path}.valueSource", id);
                var hasSourceOptions = valueSource.TryGetProperty("options", out var sourceOptions)
                                       && sourceOptions.ValueKind != JsonValueKind.Null;
                if (!string.Equals(valueSourceType, LabelValueSourceTypes.FixedEnum, StringComparison.Ordinal)
                    && hasSourceOptions)
                {
                    throw DynamicFormFieldConfigInvalid(
                        $"{path}.valueSource.options",
                        "Chi FIXED_ENUM duoc khai bao options trong valueSource.",
                        new { fieldId = id, sourceType = valueSourceType });
                }

                if (string.Equals(valueSourceType, LabelValueSourceTypes.EnumCatalog, StringComparison.Ordinal)
                    && string.IsNullOrWhiteSpace(ReadOptionalString(valueSource, "catalogId")))
                {
                    throw DynamicFormFieldConfigInvalid(
                        $"{path}.valueSource.catalogId",
                        "ENUM_CATALOG phai khai bao catalogId.",
                        new { fieldId = id });
                }

                if (string.Equals(valueSourceType, LabelValueSourceTypes.FixedEnum, StringComparison.Ordinal)
                    && hasFieldOptions
                    && hasSourceOptions
                    && !HaveEquivalentFieldOptions(fieldOptions, sourceOptions))
                {
                    throw DynamicFormFieldConfigInvalid(
                        $"{path}.valueSource.options",
                        "options o field va FIXED_ENUM valueSource phai trung khop neu cung duoc khai bao.",
                        new { fieldId = id });
                }
            }

            var isChoice = ChoiceFieldTypes.Contains(type);
            if (!isChoice && (hasFieldOptions || hasValueSource))
                throw DynamicFormFieldConfigInvalid(
                    path,
                    "Chi shortText, singleSelect va multiSelect duoc khai bao options/valueSource.",
                    new { fieldId = id, type });

            if (isChoice
                && !hasValueSource
                && fieldOptionCount == 0)
            {
                throw DynamicFormFieldConfigInvalid(
                    $"{path}.options",
                    "Field lua chon dung options cuc bo phai co it nhat mot option.",
                    new { fieldId = id, type });
            }

            if (isChoice
                && string.Equals(valueSourceType, LabelValueSourceTypes.FixedEnum, StringComparison.Ordinal)
                && fieldOptionCount + sourceOptionCount == 0)
            {
                throw DynamicFormFieldConfigInvalid(
                    $"{path}.valueSource.options",
                    "FIXED_ENUM phai co it nhat mot option o field hoac valueSource.",
                    new { fieldId = id, type });
            }

            if (string.Equals(type, "richText", StringComparison.Ordinal)
                && (ReadBoolean(field, "isStatistic")
                    || ReadLabelCodes(field, "statisticLabelCodes").Count > 0
                    || field.TryGetProperty("statistic", out var statistic) && statistic.ValueKind != JsonValueKind.Null))
            {
                throw DynamicFormFieldConfigInvalid(
                    path,
                    "richText khong duoc lam statistic target trong contract v1.",
                    new { fieldId = id });
            }

            index++;
        }
    }

    private static int ValidateFieldOptions(
        JsonElement owner,
        string propertyName,
        string ownerPath,
        string fieldId)
    {
        if (!owner.TryGetProperty(propertyName, out var options) || options.ValueKind == JsonValueKind.Null)
            return 0;

        var path = $"{ownerPath}.{propertyName}";
        if (options.ValueKind != JsonValueKind.Array)
            throw DynamicFormFieldConfigInvalid(
                path,
                $"{path} phai la JSON array.",
                new { fieldId, actualKind = options.ValueKind.ToString() });

        var count = options.GetArrayLength();
        if (count > MaxOptionsPerSelectField)
            throw DynamicFormLimitExceeded(
                "fieldOptions",
                MaxOptionsPerSelectField,
                count,
                $"Field chi duoc co toi da {MaxOptionsPerSelectField} options.");

        var optionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var optionIndex = 0;
        foreach (var option in options.EnumerateArray())
        {
            var optionPath = $"{path}[{optionIndex}]";
            if (option.ValueKind != JsonValueKind.Object)
                throw DynamicFormFieldConfigInvalid(
                    optionPath,
                    $"{optionPath} phai la JSON object.",
                    new { fieldId, actualKind = option.ValueKind.ToString() });

            var code = ReadOptionalString(option, "code");
            var label = ReadOptionalString(option, "label");
            if (string.IsNullOrWhiteSpace(code))
                throw DynamicFormFieldConfigInvalid(
                    $"{optionPath}.code",
                    "Option code khong duoc trong.",
                    new { fieldId });
            if (!optionCodes.Add(code))
                throw DynamicFormFieldConfigInvalid(
                    $"{optionPath}.code",
                    "Option code bi trung trong cung field.",
                    new { fieldId, optionCode = code });
            if (string.IsNullOrWhiteSpace(label))
                throw DynamicFormFieldConfigInvalid(
                    $"{optionPath}.label",
                    "Option label khong duoc trong.",
                    new { fieldId, optionCode = code });

            optionIndex++;
        }

        return count;
    }

    private static bool HaveEquivalentFieldOptions(JsonElement fieldOptions, JsonElement sourceOptions)
    {
        if (fieldOptions.ValueKind != JsonValueKind.Array || sourceOptions.ValueKind != JsonValueKind.Array)
            return false;

        var fieldRows = fieldOptions.EnumerateArray().ToArray();
        var sourceRows = sourceOptions.EnumerateArray().ToArray();
        if (fieldRows.Length != sourceRows.Length)
            return false;

        for (var index = 0; index < fieldRows.Length; index++)
        {
            if (!string.Equals(
                    ReadOptionalString(fieldRows[index], "code"),
                    ReadOptionalString(sourceRows[index], "code"),
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    ReadOptionalString(fieldRows[index], "label"),
                    ReadOptionalString(sourceRows[index], "label"),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureOptionalBooleanProperty(
        JsonElement field,
        string propertyName,
        string fieldPath,
        string fieldId)
    {
        if (!field.TryGetProperty(propertyName, out var value))
            return;

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return;

        throw DynamicFormFieldConfigInvalid(
            $"{fieldPath}.{propertyName}",
            $"{propertyName} phai la boolean.",
            new { fieldId, actualKind = value.ValueKind.ToString() });
    }

    private static AppException DynamicFormFieldConfigInvalid(
        string path,
        string reason,
        object? details = null)
        => DynamicFormValidation(
            AppErrorCode.DYNAMIC_FORM_FIELD_CONFIG_INVALID,
            reason,
            new { path, details });

    private static void EnsureFieldsLimit(string? fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
            return;

        using var document = ParseJson(fieldsJson, "FieldsJson");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw DynamicFormJsonKindInvalid(
                "FieldsJson",
                "array",
                document.RootElement.ValueKind,
                "FieldsJson phai la JSON array.");

        if (document.RootElement.GetArrayLength() > MaxFieldsPerForm)
            throw DynamicFormLimitExceeded(
                "fields",
                MaxFieldsPerForm,
                document.RootElement.GetArrayLength(),
                $"Dynamic Form chi toi da {MaxFieldsPerForm} fields.");
    }

    private static void EnsureSchemaPayloadBudget(
        string? sectionsJson,
        string? fieldsJson,
        string? blocksJson)
    {
        var actualBytes = Encoding.UTF8.GetByteCount(sectionsJson ?? "[]")
                          + Encoding.UTF8.GetByteCount(fieldsJson ?? "[]")
                          + Encoding.UTF8.GetByteCount(blocksJson ?? "[]")
                          + 64;
        if (actualBytes <= MaxSchemaPayloadBytes)
            return;

        throw DynamicFormLimitExceeded(
            "schemaPayloadBytes",
            MaxSchemaPayloadBytes,
            actualBytes,
            $"Dynamic Form schema chi toi da {MaxSchemaPayloadBytes} UTF-8 bytes.");
    }

    private static void EnsureTypedSchemaProjection(
        string? sectionsJson,
        string? fieldsJson,
        string? excelBlockJson,
        string? blocksJson)
    {
        try
        {
            _ = DynamicFormSchemaAdapter.FromLegacy(
                sectionsJson,
                fieldsJson,
                excelBlockJson,
                blocksJson);
        }
        catch (Exception ex) when (
            ex is JsonException or NotSupportedException or ArgumentException)
        {
            var jsonPath = (ex as JsonException)?.Path;
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_JSON_KIND_INVALID,
                "DYNAMIC_FORM_TYPED_SCHEMA_KIND_INVALID",
                new
                {
                    path = string.IsNullOrWhiteSpace(jsonPath) ? null : jsonPath,
                    contract = "DynamicFormSchema"
                },
                ex);
        }
    }

    private static void EnsurePublishableSchema(
        string sectionsJson,
        string fieldsJson,
        string blocksJson)
    {
        using var sections = ParseJson(sectionsJson, "SectionsJson");
        using var fields = ParseJson(fieldsJson, "FieldsJson");
        using var blocks = ParseJson(blocksJson, "BlocksJson");
        if (sections.RootElement.GetArrayLength() == 0)
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
                "Bieu mau can it nhat mot section truoc khi cong bo.",
                new { path = "SectionsJson" });

        if (fields.RootElement.GetArrayLength() == 0 && blocks.RootElement.GetArrayLength() == 0)
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_FIELD_CONFIG_INVALID,
                "Bieu mau can it nhat mot field hoac table block truoc khi cong bo.",
                new { path = "FieldsJson" });
    }

    private static void EnsureFieldDisplayNames(string? fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
            return;

        using var document = ParseJson(fieldsJson, "FieldsJson");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw DynamicFormJsonKindInvalid(
                "FieldsJson",
                "array",
                document.RootElement.ValueKind,
                "FieldsJson phai la JSON array.");

        var index = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw DynamicFormJsonKindInvalid(
                    $"FieldsJson[{index}]",
                    "object",
                    item.ValueKind,
                    $"FieldsJson[{index}] phai la JSON object.");

            var type = ReadOptionalString(item, "type") ?? string.Empty;
            var key = ReadOptionalString(item, "key");
            var displayName = ReadOptionalString(item, "name")
                              ?? ReadOptionalString(item, "displayName")
                              ?? ReadOptionalString(item, "label");

            if (string.IsNullOrWhiteSpace(displayName) || IsGenericFieldDisplayName(type, displayName))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_FIELD_NAME_INVALID,
                    "Ten hien thi cua field phai la ten/cau hoi rieng cho nguoi nhap, khong duoc dung ten kieu du lieu.",
                    new
                    {
                        fieldName = "FieldsJson",
                        index,
                        key,
                        type,
                        displayName,
                        note = "name la ten hien thi; label code cho thong ke/trich xuat nam trong statisticLabelCodes/tagCodes/rowLabelCodes."
                    });

            index++;
        }
    }

    private static string? NormalizeFieldDisplayAliases(string? fieldsJson)
        => NormalizeFieldPayload(fieldsJson, existingFieldsJson: null);

    private static IReadOnlyList<string> ExtractEnumCatalogIds(params string?[] jsonValues)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var json in jsonValues)
        {
            if (string.IsNullOrWhiteSpace(json))
                continue;

            try
            {
                using var document = JsonDocument.Parse(json);
                CollectEnumCatalogIds(document.RootElement, result);
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return result.ToList();
    }

    private static void CollectEnumCatalogIds(JsonElement element, ISet<string> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("valueSource", out var source) &&
                source.ValueKind == JsonValueKind.Object)
            {
                var sourceType = LabelValueSourceTypes.Normalize(
                    ReadOptionalString(source, "sourceType")
                    ?? ReadOptionalString(source, "type")
                    ?? ReadOptionalString(source, "valueSourceType"));
                var catalogId = ReadOptionalString(source, "catalogId")
                                ?? ReadOptionalString(source, "valueSourceCatalogId")
                                ?? ReadOptionalString(source, "enumCatalogId");
                if (sourceType == LabelValueSourceTypes.EnumCatalog && string.IsNullOrWhiteSpace(catalogId))
                {
                    throw DynamicFormValidation(
                        AppErrorCode.DYNAMIC_FORM_JSON_INVALID,
                        "Nguồn ENUM_CATALOG phải chọn danh mục enum.",
                        new { sourceType });
                }

                if (sourceType == LabelValueSourceTypes.EnumCatalog)
                {
                    result.Add(catalogId.Trim());
                }
            }

            foreach (var property in element.EnumerateObject())
                CollectEnumCatalogIds(property.Value, result);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectEnumCatalogIds(item, result);
        }
    }

    private static string? NormalizeFieldPayload(string? fieldsJson, string? existingFieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
            return fieldsJson;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(fieldsJson);
        }
        catch (JsonException ex)
        {
            throw DynamicFormJsonInvalid("FieldsJson", "FieldsJson khong phai JSON hop le.", ex);
        }

        if (node is not JsonArray fields)
            return fieldsJson;

        var existingKeysById = ReadExistingFieldKeysById(existingFieldsJson);
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in fields)
        {
            if (item is not JsonObject field)
                continue;

            var id = ReadOptionalString(field, "id");
            var name = ReadOptionalString(field, "name")
                       ?? ReadOptionalString(field, "displayName")
                       ?? ReadOptionalString(field, "label");

            if (!string.IsNullOrWhiteSpace(name))
                field["name"] = name.Trim();

            field.Remove("displayName");
            field.Remove("label");

            var requestedKey = ReadOptionalString(field, "key");
            var existingKey = !string.IsNullOrWhiteSpace(id) && existingKeysById.TryGetValue(id, out var retainedKey)
                ? retainedKey
                : null;
            var type = ReadOptionalString(field, "type");
            var normalizedRequestedKey = NormalizeFieldTechnicalKey(requestedKey);
            var normalizedExistingKey = NormalizeFieldTechnicalKey(existingKey);
            if (!string.IsNullOrWhiteSpace(normalizedExistingKey) &&
                !string.IsNullOrWhiteSpace(normalizedRequestedKey) &&
                !string.Equals(normalizedExistingKey, normalizedRequestedKey, StringComparison.OrdinalIgnoreCase))
            {
                throw DynamicFormFieldConfigInvalid(
                    $"FieldsJson[{index}].key",
                    "Field key khong duoc doi cho cung field id.",
                    new { fieldId = id, existingKey = normalizedExistingKey, requestedKey = normalizedRequestedKey });
            }

            var baseKey = normalizedExistingKey
                          ?? normalizedRequestedKey
                          ?? NormalizeFieldTechnicalKey(id ?? type)
                          ?? $"field_{index + 1}";
            if (!usedKeys.Add(baseKey))
            {
                throw DynamicFormFieldConfigInvalid(
                    $"FieldsJson[{index}].key",
                    "Field key bi trung trong cung bieu mau.",
                    new { fieldId = id, fieldKey = baseKey });
            }

            field["key"] = baseKey;
            index++;
        }

        return fields.ToJsonString(JsonOptions);
    }

    private static Dictionary<string, string> ReadExistingFieldKeysById(string? fieldsJson)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(fieldsJson))
            return result;

        try
        {
            using var document = JsonDocument.Parse(fieldsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var id = ReadOptionalString(item, "id");
                var key = ReadOptionalString(item, "key");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(key))
                    result[id] = key;
            }
        }
        catch (JsonException)
        {
            return result;
        }

        return result;
    }

    private static string? NormalizeFieldTechnicalKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var formD = value.Trim().Normalize(System.Text.NormalizationForm.FormD);
        var chars = formD
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .ToArray();
        var withoutMarks = new string(chars).Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
        var normalized = Regex.Replace(withoutMarks, "[^a-z0-9_.-]+", "_").Trim('_', '.', '-');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (!char.IsLetter(normalized[0]))
            normalized = $"field_{normalized}";

        return normalized.Length <= 96 ? normalized : normalized[..96].TrimEnd('_', '.', '-');
    }

    private static bool IsGenericFieldDisplayName(string? fieldType, string displayName)
    {
        var normalized = NormalizeForComparison(displayName);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var normalizedType = NormalizeForComparison(fieldType ?? string.Empty);
        return string.Equals(normalized, normalizedType, StringComparison.Ordinal)
               || GenericFieldDisplayNames.Contains(normalized)
               || GenericFieldDisplayNameRegex.IsMatch(normalized);
    }

    private static string NormalizeForComparison(string value)
    {
        var formD = value.Normalize(System.Text.NormalizationForm.FormD);
        var chars = formD
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .ToArray();

        var withoutMarks = new string(chars).Normalize(System.Text.NormalizationForm.FormC);
        return Regex.Replace(withoutMarks.Trim().ToLowerInvariant(), "\\s+", " ");
    }

    private static void EnsureStatisticConfigOnlyChange(string? currentJson, string? nextJson, string fieldName)
    {
        var removeFieldStatisticLabels = string.Equals(fieldName, "FieldsJson", StringComparison.OrdinalIgnoreCase);
        var currentCanonical = CanonicalizeWithoutStatisticConfig(currentJson, fieldName, removeFieldStatisticLabels);
        var nextCanonical = CanonicalizeWithoutStatisticConfig(nextJson, fieldName, removeFieldStatisticLabels);
        if (!string.Equals(currentCanonical, nextCanonical, StringComparison.Ordinal))
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_STATISTIC_CONFIG_STRUCTURE_INVALID,
                $"{fieldName} chi duoc thay doi cau hinh thong ke, khong duoc doi cau truc template.",
                new { fieldName });
    }

    private static string CanonicalizeWithoutStatisticConfig(
        string? json,
        string fieldName,
        bool removeFieldStatisticLabels)
    {
        try
        {
            return DynamicFormPublishedSchemaSnapshotBuilder.CanonicalizeStructureForComparison(
                json,
                stripStatisticConfig: true,
                removeFieldStatisticLabels);
        }
        catch (InvalidOperationException ex)
        {
            throw DynamicFormJsonInvalid(fieldName, $"{fieldName} khong phai JSON hop le.", ex);
        }
    }

    private static void EnsureNoAlternateFieldStatisticCreate(
        string fieldsJson)
    {
        using var document = ParseJson(fieldsJson, "FieldsJson");
        var index = 0;
        foreach (var field in document.RootElement.EnumerateArray())
        {
            var changedProperty = FirstConfiguredStatisticProperty(field);
            if (changedProperty is not null)
            {
                throw AlternateStatisticWriter(
                    $"$.schema.fields[{index}].{changedProperty}",
                    "USE_CANONICAL_STATISTICS_PATCH");
            }
            index++;
        }
    }

    private static void EnsureNoAlternateFieldStatisticUpdate(
        string currentFieldsJson,
        string nextFieldsJson)
    {
        using var currentDocument = ParseJson(
            currentFieldsJson,
            "FieldsJson");
        using var nextDocument = ParseJson(nextFieldsJson, "FieldsJson");
        var currentById = currentDocument.RootElement
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => (id: FieldIdentity(item), item))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.id))
            .ToDictionary(
                pair => pair.id!,
                pair => pair.item.Clone(),
                StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nextIndex = 0;
        foreach (var nextField in nextDocument.RootElement.EnumerateArray())
        {
            var fieldId = FieldIdentity(nextField);
            if (string.IsNullOrWhiteSpace(fieldId) ||
                !currentById.TryGetValue(fieldId, out var currentField))
            {
                var configuredProperty =
                    FirstConfiguredStatisticProperty(nextField);
                if (configuredProperty is not null)
                {
                    throw AlternateStatisticWriter(
                        $"$.schema.fields[{nextIndex}].{configuredProperty}",
                        "USE_CANONICAL_STATISTICS_PATCH");
                }
                nextIndex++;
                continue;
            }

            seen.Add(fieldId);
            var statisticProperty = FirstDifferentStatisticProperty(
                currentField,
                nextField);
            if (statisticProperty is not null)
            {
                throw AlternateStatisticWriter(
                    $"$.schema.fields[{nextIndex}].{statisticProperty}",
                    "USE_CANONICAL_STATISTICS_PATCH");
            }

            if (ReadBoolean(currentField, "isStatistic"))
            {
                var structureProperty = FirstDifferentFieldStructureProperty(
                    currentField,
                    nextField);
                if (structureProperty is not null)
                {
                    throw AlternateStatisticWriter(
                        $"$.schema.fields[{nextIndex}].{structureProperty}",
                        "CONFIGURED_STATISTIC_TARGET_STRUCTURE_IMMUTABLE");
                }
            }
            nextIndex++;
        }

        foreach (var current in currentById)
        {
            if (!seen.Contains(current.Key) &&
                ReadBoolean(current.Value, "isStatistic"))
            {
                throw AlternateStatisticWriter(
                    "$.schema.fields",
                    "CONFIGURED_STATISTIC_TARGET_REMOVAL_FORBIDDEN");
            }
        }
    }

    private static void EnsureNoAlternateTableStatisticCreate(
        string blocksJson)
    {
        using var document = ParseJson(blocksJson, "BlocksJson");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw DynamicFormJsonKindInvalid(
                "BlocksJson",
                "array",
                document.RootElement.ValueKind,
                "BlocksJson phai la JSON array.");
        }

        var blockIndex = 0;
        foreach (var block in document.RootElement.EnumerateArray())
        {
            var configuredProperty =
                FirstCallerSuppliedTableStatisticProperty(block);
            if (configuredProperty is not null)
            {
                throw AlternateStatisticWriter(
                    $"$.schema.blocks[{blockIndex}]." +
                    configuredProperty,
                    "USE_CANONICAL_STATISTICS_PATCH");
            }
            blockIndex++;
        }
    }

    private static void EnsureNoAlternateTableStatisticUpdate(
        string currentBlocksJson,
        string nextBlocksJson,
        string? tableSectionJson)
    {
        using var currentDocument = ParseJson(
            currentBlocksJson,
            "BlocksJson");
        using var nextDocument = ParseJson(
            nextBlocksJson,
            "BlocksJson");
        if (currentDocument.RootElement.ValueKind !=
                JsonValueKind.Array ||
            nextDocument.RootElement.ValueKind !=
                JsonValueKind.Array)
        {
            throw AlternateStatisticWriter(
                "$.schema.blocks",
                "TABLE_BLOCKS_ARRAY_REQUIRED");
        }

        var currentById =
            new Dictionary<string, JsonElement>(
                StringComparer.Ordinal);
        foreach (var block in
                 currentDocument.RootElement.EnumerateArray())
        {
            var blockId = ReadOptionalString(block, "blockId");
            if (!string.IsNullOrWhiteSpace(blockId))
                currentById.TryAdd(blockId, block.Clone());
        }
        var configuredBlockIds =
            ReadConfiguredTableStatisticBlockIds(tableSectionJson);
        foreach (var item in currentById)
        {
            if (HasActiveTableStatisticMetadata(item.Value))
                configuredBlockIds.Add(item.Key);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var blockIndex = 0;
        foreach (var nextBlock in
                 nextDocument.RootElement.EnumerateArray())
        {
            var configuredProperty =
                FirstCallerSuppliedTableStatisticProperty(nextBlock);
            if (configuredProperty is not null)
            {
                throw AlternateStatisticWriter(
                    $"$.schema.blocks[{blockIndex}]." +
                    configuredProperty,
                    "USE_CANONICAL_STATISTICS_PATCH");
            }

            var blockId =
                ReadOptionalString(nextBlock, "blockId");
            if (!string.IsNullOrWhiteSpace(blockId))
                seen.Add(blockId);
            if (!string.IsNullOrWhiteSpace(blockId) &&
                configuredBlockIds.Contains(blockId) &&
                currentById.TryGetValue(
                    blockId,
                    out var currentBlock) &&
                !string.Equals(
                    CanonicalizeTableBlockStructure(currentBlock),
                    CanonicalizeTableBlockStructure(nextBlock),
                    StringComparison.Ordinal))
            {
                throw AlternateStatisticWriter(
                    $"$.schema.blocks[{blockIndex}]",
                    "CONFIGURED_TABLE_STATISTIC_STRUCTURE_IMMUTABLE");
            }
            blockIndex++;
        }

        foreach (var blockId in configuredBlockIds)
        {
            if (!seen.Contains(blockId))
            {
                throw AlternateStatisticWriter(
                    "$.schema.blocks",
                    "CONFIGURED_TABLE_STATISTIC_REMOVAL_FORBIDDEN");
            }
        }
    }

    private static string?
        FirstCallerSuppliedTableStatisticProperty(
            JsonElement block)
    {
        foreach (var property in new[]
                 {
                     "metricLabelTargets",
                     "allowedRowLabelCodes",
                     "statisticsDisabled",
                     "statisticsDisabledReason",
                     "statisticColumns",
                     "statisticColumnLabels"
                 })
        {
            if (block.TryGetProperty(property, out _))
                return property;
        }
        if (!block.TryGetProperty(
                "metricRules",
                out var metricRules) ||
            metricRules.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var metricIndex = 0;
        foreach (var metric in metricRules.EnumerateArray())
        {
            if (metric.ValueKind == JsonValueKind.Object &&
                metric.TryGetProperty("aggregateOps", out _))
            {
                return $"metricRules[{metricIndex}].aggregateOps";
            }
            metricIndex++;
        }
        return null;
    }

    private static bool HasActiveTableStatisticMetadata(
        JsonElement block)
    {
        if (ReadBoolean(block, "statisticsDisabled"))
            return true;
        foreach (var property in new[]
                 {
                     "metricLabelTargets",
                     "allowedRowLabelCodes",
                     "statisticColumns",
                     "statisticColumnLabels"
                 })
        {
            if (block.TryGetProperty(property, out var value) &&
                value.ValueKind == JsonValueKind.Array &&
                value.GetArrayLength() > 0)
            {
                return true;
            }
        }
        if (block.TryGetProperty(
                "metricRules",
                out var metricRules) &&
            metricRules.ValueKind == JsonValueKind.Array)
        {
            return metricRules.EnumerateArray().Any(metric =>
                metric.ValueKind == JsonValueKind.Object &&
                metric.TryGetProperty(
                    "aggregateOps",
                    out var operations) &&
                operations.ValueKind == JsonValueKind.Array &&
                operations.GetArrayLength() > 0);
        }
        return false;
    }

    private static HashSet<string>
        ReadConfiguredTableStatisticBlockIds(
            string? tableSectionJson)
    {
        var result =
            new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(tableSectionJson))
            return result;
        using var document = ParseJson(
            tableSectionJson,
            "StatisticConfigSections.TableSectionJson");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw AlternateStatisticWriter(
                "$.tableConfig",
                "TABLE_SECTION_SCHEMA_INVALID");
        }
        foreach (var table in document.RootElement.EnumerateArray())
        {
            var blockId = ReadOptionalString(table, "blockId");
            if (!string.IsNullOrWhiteSpace(blockId))
                result.Add(blockId);
        }
        return result;
    }

    private static string CanonicalizeTableBlockStructure(
        JsonElement block)
        => DynamicFormPublishedSchemaSnapshotBuilder
            .CanonicalizeStructureForComparison(
                $"[{block.GetRawText()}]",
                stripStatisticConfig: true,
                removeFieldStatisticLabels: false);

    private static string CarryForwardTableStatisticMetadata(
        string currentBlocksJson,
        string nextBlocksJson)
    {
        using var currentDocument = ParseJson(
            currentBlocksJson,
            "BlocksJson");
        var currentById =
            currentDocument.RootElement
                .EnumerateArray()
                .Where(block =>
                    block.ValueKind == JsonValueKind.Object)
                .Select(block =>
                    (
                        BlockId:
                            ReadOptionalString(block, "blockId"),
                        Block: block.Clone()))
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.BlockId))
                .ToDictionary(
                    item => item.BlockId!,
                    item => item.Block,
                    StringComparer.Ordinal);
        var nextNode = JsonNode.Parse(nextBlocksJson);
        if (nextNode is not JsonArray nextBlocks)
        {
            throw AlternateStatisticWriter(
                "$.schema.blocks",
                "TABLE_BLOCKS_ARRAY_REQUIRED");
        }

        foreach (var nextBlock in
                 nextBlocks.OfType<JsonObject>())
        {
            var blockId =
                ReadJsonObjectString(nextBlock, "blockId");
            if (string.IsNullOrWhiteSpace(blockId) ||
                !currentById.TryGetValue(
                    blockId,
                    out var currentBlock))
            {
                continue;
            }

            foreach (var property in new[]
                     {
                         "metricLabelTargets",
                         "allowedRowLabelCodes",
                         "statisticColumns",
                         "statisticColumnLabels"
                     })
            {
                CopyTableStatisticProperty(
                    currentBlock,
                    nextBlock,
                    property);
            }
            if (ReadBoolean(
                    currentBlock,
                    "statisticsDisabled"))
            {
                nextBlock["statisticsDisabled"] = true;
                CopyTableStatisticProperty(
                    currentBlock,
                    nextBlock,
                    "statisticsDisabledReason");
            }
            CarryForwardMetricAggregateOperations(
                currentBlock,
                nextBlock);
        }

        return nextBlocks.ToJsonString(JsonOptions);
    }

    private static void CopyTableStatisticProperty(
        JsonElement currentBlock,
        JsonObject nextBlock,
        string propertyName)
    {
        if (!currentBlock.TryGetProperty(
                propertyName,
                out var value))
        {
            return;
        }
        nextBlock[propertyName] =
            JsonNode.Parse(value.GetRawText());
    }

    private static void CarryForwardMetricAggregateOperations(
        JsonElement currentBlock,
        JsonObject nextBlock)
    {
        if (!currentBlock.TryGetProperty(
                "metricRules",
                out var currentRules) ||
            currentRules.ValueKind != JsonValueKind.Array ||
            nextBlock["metricRules"] is not JsonArray nextRules)
        {
            return;
        }
        var operationsByMetricKey = currentRules
            .EnumerateArray()
            .Where(rule =>
                rule.ValueKind == JsonValueKind.Object &&
                !string.IsNullOrWhiteSpace(
                    ReadOptionalString(rule, "metricKey")) &&
                rule.TryGetProperty("aggregateOps", out _))
            .ToDictionary(
                rule => ReadOptionalString(rule, "metricKey")!,
                rule => rule.GetProperty("aggregateOps").Clone(),
                StringComparer.Ordinal);
        foreach (var nextRule in nextRules.OfType<JsonObject>())
        {
            var metricKey =
                ReadJsonObjectString(nextRule, "metricKey");
            if (!string.IsNullOrWhiteSpace(metricKey) &&
                operationsByMetricKey.TryGetValue(
                    metricKey,
                    out var operations))
            {
                nextRule["aggregateOps"] =
                    JsonNode.Parse(operations.GetRawText());
            }
        }
    }

    private static string? FirstConfiguredStatisticProperty(
        JsonElement field)
    {
        if (ReadBoolean(field, "isStatistic"))
            return "isStatistic";
        if (ReadLabelCodes(field, "statisticLabelCodes").Count > 0)
            return "statisticLabelCodes";
        if (field.TryGetProperty("statistic", out var statistic) &&
            statistic.ValueKind is not JsonValueKind.Null and
                not JsonValueKind.Undefined)
        {
            return "statistic";
        }
        return null;
    }

    private static string? FirstDifferentStatisticProperty(
        JsonElement current,
        JsonElement next)
    {
        foreach (var property in new[]
                 {
                     "isStatistic",
                     "statisticLabelCodes",
                     "statistic"
                 })
        {
            if (!CanonicalPropertyEquals(current, next, property))
                return property;
        }
        return null;
    }

    private static string? FirstDifferentFieldStructureProperty(
        JsonElement current,
        JsonElement next)
    {
        var statisticProperties = new HashSet<string>(
            new[] { "isStatistic", "statisticLabelCodes", "statistic" },
            StringComparer.Ordinal);
        var names = current.EnumerateObject()
            .Select(property => property.Name)
            .Concat(next.EnumerateObject().Select(property => property.Name))
            .Where(name => !statisticProperties.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!CanonicalPropertyEquals(current, next, name))
                return name;
        }
        return null;
    }

    private static bool CanonicalPropertyEquals(
        JsonElement left,
        JsonElement right,
        string propertyName)
    {
        var hasLeft = left.TryGetProperty(propertyName, out var leftValue);
        var hasRight = right.TryGetProperty(propertyName, out var rightValue);
        if (!hasLeft || !hasRight)
            return hasLeft == hasRight;
        return string.Equals(
            StatConfigCanonicalJson.CanonicalizeElement(leftValue),
            StatConfigCanonicalJson.CanonicalizeElement(rightValue),
            StringComparison.Ordinal);
    }

    private static string? FieldIdentity(JsonElement field)
        => ReadOptionalString(field, "id") ??
           ReadOptionalString(field, "key");

    private static AppException AlternateStatisticWriter(
        string path,
        string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.DYNAMIC_FORM_STATISTIC_CONFIG_STRUCTURE_INVALID,
            new { path, reason });

    private static List<DynamicFormStatisticConfigVersionSnapshot>?
        PrepareStatisticConfigForPublish(
            DynamicFormTemplate owner)
    {
        var hasPersistedIdentity =
            !string.IsNullOrWhiteSpace(owner.StatisticConfigId) ||
            !string.IsNullOrWhiteSpace(
                owner.StatisticConfigVersionId) ||
            owner.StatisticConfigVersionNo > 0 ||
            owner.StatisticConfigRevision > 0 ||
            !string.IsNullOrWhiteSpace(
                owner.StatisticConfigHash);
        if (!hasPersistedIdentity)
            return null;

        if (string.IsNullOrWhiteSpace(owner.StatisticConfigId) ||
            string.IsNullOrWhiteSpace(
                owner.StatisticConfigVersionId) ||
            owner.StatisticConfigVersionNo < 1 ||
            owner.StatisticConfigRevision < 1 ||
            string.IsNullOrWhiteSpace(owner.StatisticConfigHash) ||
            !string.Equals(
                owner.StatisticConfigStatus,
                StatConfigStatuses.Draft,
                StringComparison.Ordinal))
        {
            throw StatisticConfigIntegrityConflict(
                owner.Id,
                "PUBLISH_IDENTITY");
        }

        var snapshots = owner.StatisticConfigSnapshots ?? new();
        var currentSnapshots = snapshots
            .Where(snapshot =>
                string.Equals(
                    snapshot.VersionId,
                    owner.StatisticConfigVersionId,
                    StringComparison.Ordinal) &&
                snapshot.VersionNo ==
                owner.StatisticConfigVersionNo)
            .ToList();
        if (snapshots.Count != owner.StatisticConfigVersionNo ||
            currentSnapshots.Count != 1)
        {
            throw StatisticConfigIntegrityConflict(
                owner.Id,
                "PUBLISH_SNAPSHOT_IDENTITY");
        }

        var current = currentSnapshots[0];
        if (current.Revision != owner.StatisticConfigRevision ||
            !string.Equals(
                current.ConfigHash,
                owner.StatisticConfigHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                current.Status,
                StatConfigStatuses.Draft,
                StringComparison.Ordinal))
        {
            throw StatisticConfigIntegrityConflict(
                owner.Id,
                "PUBLISH_CURRENT_SNAPSHOT");
        }

        current.Status = StatConfigStatuses.Locked;
        return snapshots;
    }

    private static AppException StatisticConfigIntegrityConflict(
        string ownerId,
        string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                ownerKind = StatConfigOwnerKinds.DynamicForm,
                ownerId,
                reason =
                    $"DYNAMIC_FORM_STATISTIC_{reason}_INTEGRITY"
            });

    private static IEnumerable<LabelStatisticTarget> ReadLabelPropertyTargets(
        string fieldName,
        string? json,
        params string[] propertyNames)
    {
        if (string.IsNullOrWhiteSpace(json))
            yield break;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            var names = new HashSet<string>(propertyNames, StringComparer.OrdinalIgnoreCase);
            foreach (var target in ReadLabelPropertyTargets(document.RootElement, fieldName, names))
                yield return target;
        }
    }

    private static IEnumerable<LabelStatisticTarget> ReadLabelPropertyTargets(
        JsonElement element,
        string path,
        HashSet<string> propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var nextPath = $"{path}.{property.Name}";
                if (propertyNames.Contains(property.Name))
                {
                    foreach (var code in ReadLabelCodesFromElement(property.Value))
                        yield return new LabelStatisticTarget(code, nextPath);
                    continue;
                }

                foreach (var target in ReadLabelPropertyTargets(property.Value, nextPath, propertyNames))
                    yield return target;
            }

            yield break;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                foreach (var target in ReadLabelPropertyTargets(item, $"{path}[{index}]", propertyNames))
                    yield return target;
                index++;
            }
        }
    }

    private static IEnumerable<LabelStatisticTarget> ReadTableTargetLabelTargets(
        string fieldName,
        string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            yield break;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            foreach (var target in ReadTableTargetLabelTargets(
                         document.RootElement,
                         fieldName,
                         currentRowLabelDataType: null,
                         currentColumnLabelDataType: null,
                         currentCellLabelDataType: null,
                         currentRangeLabelDataType: null))
                yield return target;
        }
    }

    private static IEnumerable<LabelStatisticTarget> ReadTableTargetLabelTargets(
        JsonElement element,
        string path,
        string? currentRowLabelDataType,
        string? currentColumnLabelDataType,
        string? currentCellLabelDataType,
        string? currentRangeLabelDataType)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var rowLabelDataType = ReadOptionalString(element, "rowLabelDataType")
                                   ?? ReadOptionalString(element, "rowLabelTargetDataType")
                                   ?? currentRowLabelDataType;
            var columnLabelDataType = ReadOptionalString(element, "columnLabelDataType")
                                      ?? ReadOptionalString(element, "columnLabelTargetDataType")
                                      ?? currentColumnLabelDataType;
            var cellLabelDataType = ReadOptionalString(element, "cellLabelDataType")
                                    ?? ReadOptionalString(element, "cellLabelTargetDataType")
                                    ?? currentCellLabelDataType;
            var rangeLabelDataType = ReadOptionalString(element, "rangeLabelDataType")
                                     ?? ReadOptionalString(element, "rangeLabelTargetDataType")
                                     ?? currentRangeLabelDataType;

            foreach (var property in element.EnumerateObject())
            {
                var nextPath = $"{path}.{property.Name}";
                if (IsTableTargetLabelProperty(property.Name))
                {
                    var expectedDataType = ResolveTableTargetDataType(
                        element,
                        property.Name,
                        rowLabelDataType,
                        columnLabelDataType,
                        cellLabelDataType,
                        rangeLabelDataType);
                    foreach (var code in ReadLabelCodesFromElement(property.Value))
                        yield return new LabelStatisticTarget(code, nextPath, expectedDataType);
                    continue;
                }

                foreach (var target in ReadTableTargetLabelTargets(
                             property.Value,
                             nextPath,
                             rowLabelDataType,
                             columnLabelDataType,
                             cellLabelDataType,
                             rangeLabelDataType))
                    yield return target;
            }

            yield break;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                foreach (var target in ReadTableTargetLabelTargets(
                             item,
                             $"{path}[{index}]",
                             currentRowLabelDataType,
                             currentColumnLabelDataType,
                             currentCellLabelDataType,
                             currentRangeLabelDataType))
                    yield return target;
                index++;
            }
        }
    }

    private static bool IsTableTargetLabelProperty(string name)
        => string.Equals(name, "allowedRowLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "rowLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "allowedColumnLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "columnLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "allowedCellLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "cellLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "allowedRangeLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "rangeLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "targetLabelCodes", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveTableTargetDataType(
        JsonElement owner,
        string propertyName,
        string? rowLabelDataType,
        string? columnLabelDataType,
        string? cellLabelDataType,
        string? rangeLabelDataType)
    {
        var scopedType = IsRowTargetLabelProperty(propertyName)
            ? ReadOptionalString(owner, "rowLabelDataType") ?? ReadOptionalString(owner, "rowLabelTargetDataType") ?? rowLabelDataType
            : IsColumnTargetLabelProperty(propertyName)
                ? ReadOptionalString(owner, "columnLabelDataType") ?? ReadOptionalString(owner, "columnLabelTargetDataType") ?? columnLabelDataType
                : IsCellTargetLabelProperty(propertyName)
                    ? ReadOptionalString(owner, "cellLabelDataType") ?? ReadOptionalString(owner, "cellLabelTargetDataType") ?? cellLabelDataType
                    : IsRangeTargetLabelProperty(propertyName)
                        ? ReadOptionalString(owner, "rangeLabelDataType") ?? ReadOptionalString(owner, "rangeLabelTargetDataType") ?? rangeLabelDataType
                        : null;
        var explicitType = scopedType
                           ?? ReadOptionalString(owner, "targetDataType")
                           ?? ReadOptionalString(owner, "labelDataType")
                           ?? ReadOptionalString(owner, "defaultDataType")
                           ?? ReadOptionalString(owner, "dataType");

        if (!string.IsNullOrWhiteSpace(explicitType))
            return LabelDataTypes.Normalize(explicitType);

        return LabelDataTypes.Number;
    }

    private static bool IsRowTargetLabelProperty(string name)
        => string.Equals(name, "allowedRowLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "rowLabelCodes", StringComparison.OrdinalIgnoreCase);

    private static bool IsColumnTargetLabelProperty(string name)
        => string.Equals(name, "allowedColumnLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "columnLabelCodes", StringComparison.OrdinalIgnoreCase);

    private static bool IsCellTargetLabelProperty(string name)
        => string.Equals(name, "allowedCellLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "cellLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "targetLabelCodes", StringComparison.OrdinalIgnoreCase);

    private static bool IsRangeTargetLabelProperty(string name)
        => string.Equals(name, "allowedRangeLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "rangeLabelCodes", StringComparison.OrdinalIgnoreCase);

    private static List<string> ReadLabelCodesFromElement(JsonElement element)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddLabelCodes(element, set);
        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<LabelStatisticTarget> ReadFieldStatisticTargets(string? fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
            yield break;

        using var document = ParseJson(fieldsJson, "FieldsJson");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw DynamicFormJsonKindInvalid(
                "FieldsJson",
                "array",
                document.RootElement.ValueKind,
                "FieldsJson phai la JSON array.");

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw DynamicFormJsonKindInvalid(
                    "FieldsJson[]",
                    "object",
                    item.ValueKind,
                    "FieldsJson item phai la JSON object.");

            var labels = ReadLabelCodes(item, "statisticLabelCodes");
            if (labels.Count == 0)
                continue;

            if (!ReadBoolean(item, "isStatistic"))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                    "Field gan statisticLabelCodes phai duoc danh dau isStatistic=true.",
                    new { propertyName = "FieldsJson.statisticLabelCodes" });

            var fieldKey = ReadOptionalString(item, "key")
                           ?? ReadOptionalString(item, "id")
                           ?? "field";
            var expectedDataType = MapFieldTypeToLabelDataType(ReadOptionalString(item, "type"));

            foreach (var label in labels)
                yield return new LabelStatisticTarget(label, $"field:{fieldKey}", expectedDataType);
        }
    }

    private static string MapFieldTypeToLabelDataType(string? fieldType)
        => fieldType?.Trim() switch
        {
            "number" => LabelDataTypes.Number,
            "date" => LabelDataTypes.Date,
            "fullDate" => LabelDataTypes.Date,
            "boolean" => LabelDataTypes.Boolean,
            "longText" => LabelDataTypes.StringList,
            "richText" => LabelDataTypes.StringList,
            "stringList" => LabelDataTypes.StringList,
            "singleSelect" => LabelDataTypes.ShortText,
            "multiSelect" => LabelDataTypes.ShortText,
            _ => LabelDataTypes.ShortText
        };

    private static int CountStatisticFields(string? fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
            return 0;

        using var document = ParseJson(fieldsJson, "FieldsJson");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw DynamicFormJsonKindInvalid(
                "FieldsJson",
                "array",
                document.RootElement.ValueKind,
                "FieldsJson phai la JSON array.");

        var count = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw DynamicFormJsonKindInvalid(
                    "FieldsJson[]",
                    "object",
                    item.ValueKind,
                    "FieldsJson item phai la JSON object.");

            if (ReadBoolean(item, "isStatistic"))
                count++;
        }

        return count;
    }

    private static IEnumerable<LabelStatisticTarget> ReadMetricLabelTargets(string? blocksJson)
    {
        if (string.IsNullOrWhiteSpace(blocksJson))
            yield break;

        using var document = ParseJson(blocksJson, "BlocksJson");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw DynamicFormJsonKindInvalid(
                "BlocksJson",
                "array",
                document.RootElement.ValueKind,
                "BlocksJson phai la JSON array.");

        var blockIndex = 0;
        foreach (var block in document.RootElement.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                throw DynamicFormJsonKindInvalid(
                    "BlocksJson[]",
                    "object",
                    block.ValueKind,
                    "BlocksJson item phai la JSON object.");

            if (!block.TryGetProperty("metricLabelTargets", out var targets)
                || targets.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                blockIndex++;
                continue;
            }

            if (targets.ValueKind != JsonValueKind.Array)
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                    "metricLabelTargets phai la JSON array.",
                    new { propertyName = "metricLabelTargets", actualKind = targets.ValueKind.ToString() });

            var blockId = ReadOptionalString(block, "blockId")
                          ?? ReadOptionalString(block, "id")
                          ?? $"block_{blockIndex + 1}";

            var targetIndex = 0;
            foreach (var target in targets.EnumerateArray())
            {
                if (target.ValueKind != JsonValueKind.Object)
                    throw DynamicFormValidation(
                        AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                        "metricLabelTargets item phai la JSON object.",
                        new { propertyName = "metricLabelTargets", actualKind = target.ValueKind.ToString() });

                var labelCode = NormalizeLabelCode(ReadOptionalString(target, "statisticLabelCode"));
                var metricKey = ReadOptionalString(target, "metricKey");
                var hasMetric = !string.IsNullOrWhiteSpace(metricKey);
                var hasRange = TryReadMetricLabelRange(target, out var range);

                if (hasMetric == hasRange)
                    throw DynamicFormValidation(
                        AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                        "metricLabelTargets can dung dung mot trong hai kieu: metricKey hoac range.",
                        new { blockId, targetIndex, hasMetric, hasRange });

                if (hasMetric)
                {
                    ValidateMetricKey(metricKey!);
                    var expectedDataType = LabelDataTypes.Normalize(
                        ReadOptionalString(target, "dataType")
                        ?? ReadOptionalString(target, "targetDataType")
                        ?? ReadOptionalString(block, "defaultDataType")
                        ?? ReadOptionalString(block, "dataType"));
                    yield return new LabelStatisticTarget(
                        labelCode,
                        $"table:{blockId}.metric:{metricKey}",
                        expectedDataType);
                }
                else
                {
                    var expectedDataType = ResolveMetricLabelTargetDataType(block, target, $"BlocksJson[{blockIndex}]", targetIndex);
                    yield return new LabelStatisticTarget(
                        labelCode,
                        $"table:{blockId}.range:{range.R0},{range.C0}:{range.R1},{range.C1}",
                        expectedDataType);
                }

                targetIndex++;
            }

            blockIndex++;
        }
    }

    private static IEnumerable<LabelStatisticTarget> ReadExcelColumnStatisticTargets(string? blocksJson)
    {
        if (string.IsNullOrWhiteSpace(blocksJson))
            yield break;

        using var document = ParseJson(blocksJson, "BlocksJson");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw DynamicFormJsonKindInvalid(
                "BlocksJson",
                "array",
                document.RootElement.ValueKind,
                "BlocksJson phai la JSON array.");

        var blockIndex = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw DynamicFormJsonKindInvalid(
                    "BlocksJson[]",
                    "object",
                    item.ValueKind,
                    "BlocksJson item phai la JSON object.");

            var blockId = ReadOptionalString(item, "blockId")
                          ?? ReadOptionalString(item, "id")
                          ?? $"block_{blockIndex + 1}";

            var statisticColumns = ReadStatisticColumnKeys(item);

            if (item.TryGetProperty("statisticColumnLabels", out var columns))
            {
                if (columns.ValueKind != JsonValueKind.Array)
                    throw DynamicFormValidation(
                        AppErrorCode.DYNAMIC_FORM_STATISTIC_LABEL_COLUMNS_INVALID,
                        "statisticColumnLabels phai la JSON array.",
                        new { propertyName = "statisticColumnLabels", actualKind = columns.ValueKind.ToString() });

                var seenColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var column in columns.EnumerateArray())
                {
                    if (column.ValueKind != JsonValueKind.Object)
                        throw DynamicFormValidation(
                            AppErrorCode.DYNAMIC_FORM_STATISTIC_LABEL_COLUMNS_INVALID,
                            "statisticColumnLabels item phai la JSON object.",
                            new { propertyName = "statisticColumnLabels", actualKind = column.ValueKind.ToString() });

                    var labelCode = ReadOptionalString(column, "statisticLabelCode");
                    if (string.IsNullOrWhiteSpace(labelCode))
                        throw DynamicFormValidation(
                            AppErrorCode.DYNAMIC_FORM_LABEL_CODE_INVALID,
                            "statisticColumnLabels.statisticLabelCode khong duoc trong.",
                            new { propertyName = "statisticColumnLabels.statisticLabelCode" });

                    var columnKey = ReadOptionalString(column, "columnKey")
                                    ?? ReadOptionalString(column, "header")
                                    ?? ReadColumnIndexKey(column);

                    if (string.IsNullOrWhiteSpace(columnKey))
                        throw DynamicFormValidation(
                            AppErrorCode.DYNAMIC_FORM_STATISTIC_LABEL_COLUMNS_INVALID,
                            "statisticColumnLabels can columnKey, header hoac columnIndex.",
                            new { propertyName = "statisticColumnLabels" });

                    var normalizedColumnKey = NormalizeStatisticColumnKey(columnKey);
                    if (!statisticColumns.Contains(normalizedColumnKey))
                        throw DynamicFormValidation(
                            AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                            $"Cot gan label phai nam trong statisticColumns: {blockId}.{columnKey}.",
                            new { blockId, columnKey });

                    var scopedColumnKey = $"{blockId}:{columnKey}";
                    if (!seenColumns.Add(scopedColumnKey))
                        throw DynamicFormValidation(
                            AppErrorCode.DYNAMIC_FORM_STATISTIC_LABEL_COLUMNS_INVALID,
                            $"Cot thong ke bi trung trong block {blockId}: {columnKey}.",
                            new { blockId, columnKey });

                    yield return new LabelStatisticTarget(
                        NormalizeLabelCode(labelCode),
                        $"table:{blockId}.column:{columnKey}");
                }
            }

            blockIndex++;
        }
    }

    private static int CountExcelStatisticColumns(string? blocksJson)
    {
        if (string.IsNullOrWhiteSpace(blocksJson))
            return 0;

        using var document = ParseJson(blocksJson, "BlocksJson");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw DynamicFormJsonKindInvalid(
                "BlocksJson",
                "array",
                document.RootElement.ValueKind,
                "BlocksJson phai la JSON array.");

        var count = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw DynamicFormJsonKindInvalid(
                    "BlocksJson[]",
                    "object",
                    item.ValueKind,
                    "BlocksJson item phai la JSON object.");

            count += ReadStatisticColumnKeys(item).Count;
        }

        return count;
    }

    private static HashSet<string> ReadStatisticColumnKeys(JsonElement block)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!block.TryGetProperty("statisticColumns", out var columns))
            return keys;

        if (columns.ValueKind != JsonValueKind.Array)
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_STATISTIC_COLUMNS_INVALID,
                "statisticColumns phai la JSON array.",
                new { propertyName = "statisticColumns", actualKind = columns.ValueKind.ToString() });

        foreach (var column in columns.EnumerateArray())
        {
            if (column.ValueKind != JsonValueKind.Object)
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_STATISTIC_COLUMNS_INVALID,
                    "statisticColumns item phai la JSON object.",
                    new { propertyName = "statisticColumns", actualKind = column.ValueKind.ToString() });

            var columnKey = ReadOptionalString(column, "columnKey")
                            ?? ReadOptionalString(column, "header")
                            ?? ReadColumnIndexKey(column);

            if (string.IsNullOrWhiteSpace(columnKey))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_STATISTIC_COLUMNS_INVALID,
                    "statisticColumns can columnKey, header hoac columnIndex.",
                    new { propertyName = "statisticColumns" });

            keys.Add(NormalizeStatisticColumnKey(columnKey));
        }

        return keys;
    }

    private static string NormalizeStatisticColumnKey(string value)
        => value.Trim().ToLowerInvariant();

    private static List<string> ReadLabelCodes(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return new List<string>();

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddLabelCodes(value, set);
        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ReadColumnIndexKey(JsonElement element)
    {
        if (!element.TryGetProperty("columnIndex", out var value))
            return null;

        var index = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)
            ? n
            : -1;

        return index >= 0 ? $"col_{index + 1}" : null;
    }

    private static bool ReadBoolean(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.True;

    private static void AddUniqueLabelTarget(
        Dictionary<string, string> targetsByLabel,
        string labelCode,
        string target)
    {
        if (targetsByLabel.TryGetValue(labelCode, out var existing))
        {
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_CONFLICT,
                $"Nhan '{labelCode}' da gan cho {existing}, khong duoc gan tiep cho {target} trong cung Dynamic Form.",
                new { labelCode, existing, target });
        }

        targetsByLabel[labelCode] = target;
    }

    private sealed record LabelStatisticTarget(
        string LabelCode,
        string Target,
        string? ExpectedDataType = null);

    private readonly record struct MetricLabelRange(
        int R0,
        int C0,
        int R1,
        int C1);

    private static string[] NormalizeLabelCodes(string[]? labelCodes)
        => labelCodes?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeLabelCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();

    private static string NormalizeLabelCode(string? value)
    {
        var code = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(code))
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_LABEL_CODE_INVALID,
                "Ma nhan khong duoc trong.");
        if (!LabelCodeRegex.IsMatch(code))
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_LABEL_CODE_INVALID,
                "Ma nhan chi gom chu thuong, so, dau -, _ hoac . va toi da 64 ky tu.",
                new { labelCode = code });
        return code;
    }

    private static string NormalizeJsonArray(string? json, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "[]";

        ValidateJsonKind(json, fieldName, JsonValueKind.Array);
        return json.Trim();
    }

    private static string? NormalizeOptionalJsonObject(string? json, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var document = ParseJson(json, fieldName);
        if (document.RootElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
            throw DynamicFormJsonKindInvalid(
                fieldName,
                "object or null",
                document.RootElement.ValueKind,
                $"{fieldName} phai la JSON object hoac null.");

        return json.Trim();
    }

    private static string NormalizeBlocksJson(string? blocksJson, string? excelBlockJson)
    {
        if (!string.IsNullOrWhiteSpace(blocksJson))
        {
            using var blocksDocument = ParseJson(blocksJson, "BlocksJson");
            if (blocksDocument.RootElement.ValueKind == JsonValueKind.Null)
                return "[]";

            if (blocksDocument.RootElement.ValueKind != JsonValueKind.Array)
                throw DynamicFormJsonKindInvalid(
                    "BlocksJson",
                    "array or null",
                    blocksDocument.RootElement.ValueKind,
                    "BlocksJson phai la JSON array hoac null.");

            if (blocksDocument.RootElement.GetArrayLength() > 0 || string.IsNullOrWhiteSpace(excelBlockJson))
                return blocksJson.Trim();
        }

        if (string.IsNullOrWhiteSpace(excelBlockJson))
            return "[]";

        using var excelDocument = ParseJson(excelBlockJson, "ExcelBlockJson");
        if (excelDocument.RootElement.ValueKind == JsonValueKind.Null)
            return "[]";

        if (excelDocument.RootElement.ValueKind != JsonValueKind.Object)
            throw DynamicFormJsonKindInvalid(
                "ExcelBlockJson",
                "object or null",
                excelDocument.RootElement.ValueKind,
                "ExcelBlockJson phai la JSON object hoac null.");

        var block = excelDocument.RootElement.Clone();
        return JsonSerializer.Serialize(new[] { block }, JsonOptions);
    }

    private static string NormalizeBlocksForSections(string blocksJson, string sectionsJson)
    {
        var sectionIds = ReadSectionIds(sectionsJson);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(blocksJson);
        }
        catch (JsonException ex)
        {
            throw DynamicFormJsonInvalid("BlocksJson", "BlocksJson khong phai JSON hop le.", ex);
        }

        if (node is not JsonArray blocks)
            throw DynamicFormJsonInvalid("BlocksJson", "BlocksJson phai la JSON array.");

        if (blocks.Count > MaxTableBlocksPerForm)
            throw DynamicFormLimitExceeded(
                "tableBlocks",
                MaxTableBlocksPerForm,
                blocks.Count,
                $"Dynamic Form chi toi da {MaxTableBlocksPerForm} table blocks.");

        if (blocks.Count == 0)
            return "[]";

        if (sectionIds.Count == 0)
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
                "BlocksJson can sectionId hop le trong SectionsJson.",
                new { fieldName = "BlocksJson" });

        var knownSectionIds = sectionIds.ToHashSet(StringComparer.Ordinal);
        var knownBlockIds = new HashSet<string>(StringComparer.Ordinal);
        var blockIndex = 0;

        foreach (var item in blocks)
        {
            if (item is not JsonObject block)
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
                    "BlocksJson item phai la JSON object.",
                    new { fieldName = "BlocksJson" });

            RemoveRawTemplatePayload(block);
            RemoveLegacyStatisticColumnPayload(block);

            var blockId = ReadJsonObjectString(block, "blockId")
                          ?? ReadJsonObjectString(block, "BlockId");
            if (string.IsNullOrWhiteSpace(blockId))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_EXCEL_BLOCK_INVALID,
                    "BlockId khong duoc trong.",
                    new { path = $"BlocksJson[{blockIndex}].blockId" });
            if (!knownBlockIds.Add(blockId))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_BLOCK_DUPLICATE,
                    "BlockId bi trung trong cung bieu mau.",
                    new { path = $"BlocksJson[{blockIndex}].blockId", blockId });

            var sectionId = ReadJsonObjectString(block, "sectionId")
                            ?? ReadJsonObjectString(block, "SectionId");

            if (string.IsNullOrWhiteSpace(sectionId))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
                    "Block sectionId khong duoc trong.",
                    new { path = $"BlocksJson[{blockIndex}].sectionId", blockId });

            if (!knownSectionIds.Contains(sectionId))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
                    "Phần chứa bảng Excel động không tồn tại trong biểu mẫu động.",
                    new { path = $"BlocksJson[{blockIndex}].sectionId", blockId, sectionId });

            block.Remove("BlockId");
            block["blockId"] = blockId;
            block.Remove("SectionId");
            block["sectionId"] = sectionId;
            blockIndex++;
        }

        return blocks.ToJsonString(JsonOptions);
    }

    private async Task<string> NormalizeBlocksForDynamicExcelTemplatesAsync(
        MeResponse me,
        string blocksJson,
        CancellationToken ct,
        IReadOnlySet<string>? retainedDynamicExcelTemplateIds = null)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(blocksJson);
        }
        catch (JsonException ex)
        {
            throw DynamicFormJsonInvalid("BlocksJson", "BlocksJson khong phai JSON hop le.", ex);
        }

        if (node is not JsonArray blocks)
            throw DynamicFormJsonInvalid("BlocksJson", "BlocksJson phai la JSON array.");

        EnsureUniqueDynamicExcelTemplateBlocks(blocks);

        foreach (var block in blocks.OfType<JsonObject>())
        {
            var rawId = ReadJsonObjectString(block, "dynamicExcelTemplateId")
                        ?? ReadJsonObjectString(block, "DynamicExcelTemplateId")
                        ?? ReadJsonObjectString(block, "excelBlockDynamicExcelTemplateId")
                        ?? ReadJsonObjectString(block, "ExcelBlockDynamicExcelTemplateId");
            if (string.IsNullOrWhiteSpace(rawId))
                continue;

            var normalizedId = NormalizeDynamicExcelTemplateId(rawId);
            block.Remove("DynamicExcelTemplateId");
            block.Remove("excelBlockDynamicExcelTemplateId");
            block.Remove("ExcelBlockDynamicExcelTemplateId");
            block["dynamicExcelTemplateId"] = normalizedId;
        }

        var ids = blocks
            .OfType<JsonObject>()
            .Select(block => ReadJsonObjectString(block, "dynamicExcelTemplateId"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (ids.Length == 0)
            return blocks.ToJsonString(JsonOptions);

        var fb = Builders<DynamicExcelTemplate>.Filter;
        var templates = await _ctx.DynamicExcelTemplates
            .Find(fb.In(x => x.Id, ids) & fb.Eq(x => x.IsDeleted, false))
            .ToListAsync(ct);
        var byId = templates.ToDictionary(x => x.Id, StringComparer.Ordinal);

        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var template))
                throw DynamicExcelNotFound(id);

            if (retainedDynamicExcelTemplateIds?.Contains(id) != true)
                RequireCanReadDynamicExcel(me, template);
        }

        foreach (var block in blocks.OfType<JsonObject>())
        {
            var id = ReadJsonObjectString(block, "dynamicExcelTemplateId");
            if (string.IsNullOrWhiteSpace(id) || !byId.TryGetValue(id, out var template))
                continue;

            var specKind = ReadDynamicExcelSpecKind(template.SpecJson);
            if (string.IsNullOrWhiteSpace(specKind))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_EXCEL_BLOCK_INVALID,
                    "Loại bảng của biểu mẫu Excel động không hợp lệ.",
                    new
                    {
                        dynamicExcelTemplateId = id,
                        dynamicExcelName = template.Name,
                    });

            var expectedTableMode = NormalizeDynamicExcelTemplateTableMode(template.TableMode, specKind);
            var requestedTableMode = ReadJsonObjectString(block, "tableMode")
                                     ?? ReadJsonObjectString(block, "TableMode");
            if (!string.IsNullOrWhiteSpace(requestedTableMode) &&
                !string.Equals(requestedTableMode.Trim(), expectedTableMode, StringComparison.OrdinalIgnoreCase))
            {
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_TABLE_MODE_MISMATCH,
                    "TableMode cua block khong khop voi Dynamic Excel template.",
                    new
                    {
                        dynamicExcelTemplateId = id,
                        requestedTableMode,
                        expectedTableMode
                    });
            }

            block.Remove("ExcelSpecKind");
            block["excelSpecKind"] = specKind;
            block.Remove("TableMode");
            block["tableMode"] = expectedTableMode;
            ApplyDynamicExcelTypeMetadata(block, template);
        }

        return blocks.ToJsonString(JsonOptions);
    }

    private static void EnsureUniqueDynamicExcelTemplateBlocks(JsonArray blocks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in blocks.OfType<JsonObject>())
        {
            var id = ReadJsonObjectString(block, "dynamicExcelTemplateId")
                     ?? ReadJsonObjectString(block, "DynamicExcelTemplateId")
                     ?? ReadJsonObjectString(block, "excelBlockDynamicExcelTemplateId")
                     ?? ReadJsonObjectString(block, "ExcelBlockDynamicExcelTemplateId");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            if (!seen.Add(id))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_BLOCK_DUPLICATE,
                    "Bảng Excel động đã tồn tại trong biểu mẫu động.",
                    new { dynamicExcelTemplateId = id });
        }
    }

    private static void ApplyDynamicExcelTypeMetadata(JsonObject block, DynamicExcelTemplate template)
    {
        block.Remove("DefaultDataType");
        block.Remove("defaultDataType");
        block.Remove("DataTypeOverrides");
        block.Remove("dataTypeOverrides");
        block.Remove("DefaultOptions");
        block.Remove("defaultOptions");
        block.Remove("SpecialRanges");
        block.Remove("specialRanges");
        block.Remove("StatisticsDisabled");
        block.Remove("statisticsDisabled");
        block.Remove("StatisticsInputCellCount");
        block.Remove("statisticsInputCellCount");
        block.Remove("StatisticsInputCellLimit");
        block.Remove("statisticsInputCellLimit");
        block.Remove("StatisticsDisabledReason");
        block.Remove("statisticsDisabledReason");

        var metadata = ReadDynamicExcelTypeMetadata(template.SpecJson);
        block["defaultDataType"] = metadata.DefaultDataType;
        var statisticsInputCellCount = CountDynamicExcelTemplateInputCells(template);
        var statisticsDisabled = DynamicExcelRuntimePolicy.ShouldDisableBackgroundTableStatistics(statisticsInputCellCount);
        block["statisticsDisabled"] = statisticsDisabled;
        block["statisticsInputCellCount"] = statisticsInputCellCount;
        block["statisticsInputCellLimit"] = DynamicExcelRuntimePolicy.MaxBackgroundTableStatisticInputCells;
        if (statisticsDisabled)
        {
            block["statisticsDisabledReason"] =
                DynamicExcelRuntimePolicy.BuildBackgroundTableStatisticsDisabledReason(statisticsInputCellCount);
        }

        if (metadata.DefaultOptions.Length > 0)
        {
            var defaultOptions = new JsonArray();
            foreach (var item in metadata.DefaultOptions)
                defaultOptions.Add(JsonNode.Parse(item.GetRawText()));
            block["defaultOptions"] = defaultOptions;
        }

        if (metadata.DataTypeOverrides.Length == 0)
        {
            if (metadata.SpecialRanges.Length == 0)
                return;
        }
        else
        {
            var overrides = new JsonArray();
            foreach (var item in metadata.DataTypeOverrides)
                overrides.Add(JsonNode.Parse(item.GetRawText()));
            block["dataTypeOverrides"] = overrides;
        }

        if (metadata.SpecialRanges.Length > 0)
        {
            var specialRanges = new JsonArray();
            foreach (var item in metadata.SpecialRanges)
                specialRanges.Add(JsonNode.Parse(item.GetRawText()));
            block["specialRanges"] = specialRanges;
        }
    }

    private static string NormalizeImportSectionId(string? sectionId, string sectionsJson)
    {
        var sectionIds = ReadSectionIds(sectionsJson);
        if (sectionIds.Count == 0)
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_IMPORT_SECTION_INVALID,
                "Biểu mẫu động cần ít nhất một phần để nhập bảng Excel động.");

        var normalized = sectionId?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return sectionIds[0];

        if (!sectionIds.Contains(normalized, StringComparer.Ordinal))
            throw DynamicFormValidation(
                AppErrorCode.DYNAMIC_FORM_IMPORT_SECTION_INVALID,
                "SectionId khong ton tai trong Dynamic Form.",
                new { sectionId = normalized });

        return normalized;
    }

    private static List<string> ReadSectionIds(string sectionsJson)
    {
        using var document = ParseJson(sectionsJson, "SectionsJson");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw DynamicFormJsonKindInvalid(
                "SectionsJson",
                "array",
                document.RootElement.ValueKind,
                "SectionsJson phai la JSON array.");

        var ids = new List<string>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw DynamicFormJsonKindInvalid(
                    "SectionsJson[]",
                    "object",
                    item.ValueKind,
                    "SectionsJson item phai la JSON object.");

            var id = ReadOptionalString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
                    "SectionsJson.id khong duoc trong.",
                    new { propertyName = "SectionsJson.id" });

            var title = ReadOptionalString(item, "title");
            if (string.IsNullOrWhiteSpace(title))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
                    "Tiêu đề phần không được để trống.",
                    new { sectionId = id, propertyName = "SectionsJson.title" });

            if (ids.Contains(id, StringComparer.Ordinal))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
                    "SectionsJson.id bi trung.",
                    new { sectionId = id });

            ids.Add(id);
        }

        return ids;
    }

    private static void RemoveRawTemplatePayload(JsonObject block)
    {
        foreach (var name in new[]
        {
            "rawWorkbookDataJson",
            "RawWorkbookDataJson",
            "rawWorkbookData",
            "RawWorkbookData",
            "specJson",
            "SpecJson",
            "spec",
            "Spec"
        })
        {
            block.Remove(name);
        }
    }

    private static void RemoveLegacyStatisticColumnPayload(JsonObject block)
    {
        block.Remove("statisticColumns");
        block.Remove("statisticColumnLabels");
    }

    private static string? ReadJsonObjectString(JsonObject obj, string name)
        => obj.TryGetPropertyValue(name, out var value) && value is JsonValue jsonValue
           && jsonValue.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static string AppendDynamicExcelBlock(
        string blocksJson,
        DynamicFormExcelBlockSnapshot snapshot)
    {
        using var blocksDocument = ParseJson(blocksJson, "BlocksJson");
        if (blocksDocument.RootElement.ValueKind != JsonValueKind.Array)
            throw DynamicFormJsonKindInvalid(
                "BlocksJson",
                "array",
                blocksDocument.RootElement.ValueKind,
                "BlocksJson phai la JSON array.");

        var blocks = new List<JsonElement>();
        foreach (var item in blocksDocument.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw DynamicFormJsonKindInvalid(
                    "BlocksJson[]",
                    "object",
                    item.ValueKind,
                    "BlocksJson item phai la JSON object.");

            var existingExcelId = ExtractExcelBlockDynamicExcelTemplateId(item.GetRawText());
            if (string.Equals(existingExcelId, snapshot.DynamicExcelTemplateId, StringComparison.Ordinal))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_BLOCK_DUPLICATE,
                    "Bảng Excel động đã tồn tại trong biểu mẫu động.",
                    new { dynamicExcelTemplateId = snapshot.DynamicExcelTemplateId });

            blocks.Add(item.Clone());
        }

        blocks.Add(JsonSerializer.SerializeToElement(snapshot, JsonOptions));
        return JsonSerializer.Serialize(blocks, JsonOptions);
    }

    private static string? ExtractFirstBlockJson(string blocksJson)
    {
        using var blocksDocument = ParseJson(blocksJson, "BlocksJson");
        if (blocksDocument.RootElement.ValueKind != JsonValueKind.Array)
            throw DynamicFormJsonKindInvalid(
                "BlocksJson",
                "array",
                blocksDocument.RootElement.ValueKind,
                "BlocksJson phai la JSON array.");

        foreach (var item in blocksDocument.RootElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
                return item.GetRawText();
        }

        return null;
    }

    private static string? ExtractPrimaryBlockDynamicExcelTemplateId(string? excelBlockJson, string? blocksJson)
        => ExtractExcelBlockDynamicExcelTemplateId(excelBlockJson)
           ?? ExtractFirstBlockDynamicExcelTemplateId(blocksJson);

    private static HashSet<string> ExtractDynamicExcelTemplateIds(string? blocksJson, string? excelBlockJson)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        var primaryId = ExtractExcelBlockDynamicExcelTemplateId(excelBlockJson);
        if (!string.IsNullOrWhiteSpace(primaryId))
            ids.Add(primaryId);

        if (string.IsNullOrWhiteSpace(blocksJson))
            return ids;

        try
        {
            using var document = JsonDocument.Parse(blocksJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return ids;

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var id = ExtractExcelBlockDynamicExcelTemplateId(item.GetRawText());
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id);
            }
        }
        catch (JsonException)
        {
            return ids;
        }

        return ids;
    }

    private static string? ExtractExcelBlockDynamicExcelTemplateId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (document.RootElement.TryGetProperty("dynamicExcelTemplateId", out var camel)
                && camel.ValueKind == JsonValueKind.String)
            {
                return NormalizeCode(camel.GetString());
            }

            if (document.RootElement.TryGetProperty("DynamicExcelTemplateId", out var pascal)
                && pascal.ValueKind == JsonValueKind.String)
            {
                return NormalizeCode(pascal.GetString());
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? ExtractFirstBlockDynamicExcelTemplateId(string? blocksJson)
    {
        if (string.IsNullOrWhiteSpace(blocksJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(blocksJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var id = ExtractExcelBlockDynamicExcelTemplateId(item.GetRawText());
                if (!string.IsNullOrWhiteSpace(id))
                    return id;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static void ValidateJsonKind(string json, string fieldName, JsonValueKind expectedKind)
    {
        using var document = ParseJson(json, fieldName);
        if (document.RootElement.ValueKind != expectedKind)
            throw DynamicFormJsonKindInvalid(
                fieldName,
                expectedKind.ToString().ToLowerInvariant(),
                document.RootElement.ValueKind,
                $"{fieldName} phai la JSON {expectedKind.ToString().ToLowerInvariant()}.");
    }

    private static JsonDocument ParseJson(string json, string fieldName)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw DynamicFormJsonInvalid(fieldName, $"{fieldName} khong phai JSON hop le.", ex);
        }
    }
}
