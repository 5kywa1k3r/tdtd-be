using System.Globalization;
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
using tdtd_be.Jobs;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Services;

public interface IDynamicFormService
{
    Task<PagedResult<DynamicFormRow>> SearchAsync(DynamicFormSearchReq req, CancellationToken ct);
    Task<DynamicFormDetail> GetByIdAsync(string id, CancellationToken ct);
    Task<NextCodeResp> GetNextCodeAsync(int? year, CancellationToken ct);
    Task<DynamicFormDetail> CreateAsync(CreateDynamicFormReq req, CancellationToken ct);
    Task<DynamicFormDetail> UpdateAsync(string id, UpdateDynamicFormReq req, CancellationToken ct);
    Task<DynamicFormStatisticConfigUpdateResp> UpdateStatisticConfigAsync(
        string id,
        UpdateDynamicFormStatisticConfigReq req,
        CancellationToken ct);
    Task<DynamicFormDetail> PublishAsync(string id, CancellationToken ct);
    Task<DynamicFormDetail> CloneAsync(string id, CloneDynamicFormReq req, CancellationToken ct);
    Task<DynamicFormDetail> WrapDynamicExcelAsync(WrapDynamicExcelAsFormReq req, CancellationToken ct);
    Task<DynamicFormDetail> ImportDynamicExcelBlockAsync(string id, ImportDynamicExcelBlockReq req, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
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
    private const int MaxTableBlocksPerForm = 10;
    private const int MaxLabelStatisticTargetsPerForm = 30;
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
    private readonly IWorkReportStatisticRebuildJobService _statisticRebuildJobs;

    public DynamicFormService(
        MongoDbContext ctx,
        MeAccessor me,
        IWorkReportStatisticRebuildJobService statisticRebuildJobs)
    {
        _ctx = ctx;
        _me = me;
        _statisticRebuildJobs = statisticRebuildJobs;
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

        var f = Builders<DynamicFormTemplate>.Filter;
        var filter = f.Eq(x => x.IsDeleted, false);
        var approvedIds = await LoadApprovedCloneTemplateIdsAsync(me.Id, ct);
        filter &= BuildVisibleFilter(me.Id, approvedIds);

        if (!string.IsNullOrWhiteSpace(req.Code))
        {
            filter &= f.Regex("code", new BsonRegularExpression(req.Code.Trim(), "i"));
        }

        if (!string.IsNullOrWhiteSpace(req.Name))
        {
            filter &= f.Regex("name", new BsonRegularExpression(req.Name.Trim(), "i"));
        }

        if (!string.IsNullOrWhiteSpace(req.CreatedBy))
        {
            filter &= f.Regex("createdByUsername", new BsonRegularExpression(req.CreatedBy.Trim(), "i"));
        }

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var rx = new BsonRegularExpression(req.Q.Trim(), "i");
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
            .Skip(page * pageSize)
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

    public async Task<DynamicFormDetail> CreateAsync(CreateDynamicFormReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var now = DateTime.UtcNow;
        var code = NormalizeCode(req.Code);
        if (string.IsNullOrWhiteSpace(code))
        {
            var (_, _, nextCode) = await ComputeNextCodeAsync(now.Year, ct);
            code = nextCode;
        }

        var tagCodes = NormalizeLabelCodes(req.TagCodes);
        var sectionsJson = NormalizeJsonArray(req.SectionsJson, "SectionsJson");
        var fieldsJson = NormalizeJsonArray(req.FieldsJson, "FieldsJson");
        EnsureFieldsLimit(fieldsJson);
        EnsureFieldDisplayNames(fieldsJson);
        fieldsJson = NormalizeFieldPayload(fieldsJson, existingFieldsJson: null);
        var excelBlockJson = NormalizeOptionalJsonObject(req.ExcelBlockJson, "ExcelBlockJson");
        var blocksJson = NormalizeBlocksJson(req.BlocksJson, excelBlockJson);
        blocksJson = NormalizeBlocksForSections(blocksJson, sectionsJson);
        blocksJson = await NormalizeBlocksForDynamicExcelTemplatesAsync(me, blocksJson, ct);
        excelBlockJson = ExtractFirstBlockJson(blocksJson);
        EnsureTableStatisticContract(excelBlockJson, "ExcelBlockJson");
        EnsureBlocksTableStatisticContract(blocksJson, "BlocksJson");
        await EnsureLabelReferencesAsync(me, tagCodes, sectionsJson, fieldsJson, excelBlockJson, blocksJson, ct);
        EnsureUniqueLabelStatisticTargets(fieldsJson, blocksJson);

        var doc = new DynamicFormTemplate
        {
            Code = code,
            Name = NormalizeName(req.Name),
            Description = NormalizeOptionalText(req.Description),
            TagCodes = tagCodes,
            CreatedByUsername = me.Username,
            SchemaVersion = Math.Max(1, req.SchemaVersion ?? 1),
            VersionNo = 1,
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

        await _ctx.DynamicFormTemplates.InsertOneAsync(doc, cancellationToken: ct);
        return await ToDetailAsync(doc, me, ct);
    }

    public async Task<DynamicFormDetail> UpdateAsync(string id, UpdateDynamicFormReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var doc = await LoadAsync(id, ct);

        RequireCanMutate(me, doc);
        EnsureDraftTemplate(doc);
        await EnsureNotLinkedToRuntimeAsync(id, ct);
        var retainedDynamicExcelTemplateIds = ExtractDynamicExcelTemplateIds(doc.BlocksJson, doc.ExcelBlockJson);

        var now = DateTime.UtcNow;
        var tagCodes = NormalizeLabelCodes(req.TagCodes);
        var sectionsJson = NormalizeJsonArray(req.SectionsJson, "SectionsJson");
        var fieldsJson = NormalizeJsonArray(req.FieldsJson, "FieldsJson");
        EnsureFieldsLimit(fieldsJson);
        EnsureFieldDisplayNames(fieldsJson);
        fieldsJson = NormalizeFieldPayload(fieldsJson, doc.FieldsJson);
        var excelBlockJson = NormalizeOptionalJsonObject(req.ExcelBlockJson, "ExcelBlockJson");
        var blocksJson = NormalizeBlocksJson(req.BlocksJson, excelBlockJson);
        blocksJson = NormalizeBlocksForSections(blocksJson, sectionsJson);
        blocksJson = await NormalizeBlocksForDynamicExcelTemplatesAsync(me, blocksJson, ct, retainedDynamicExcelTemplateIds);
        excelBlockJson = ExtractFirstBlockJson(blocksJson);
        EnsureTableStatisticContract(excelBlockJson, "ExcelBlockJson");
        EnsureBlocksTableStatisticContract(blocksJson, "BlocksJson");
        await EnsureLabelReferencesAsync(me, tagCodes, sectionsJson, fieldsJson, excelBlockJson, blocksJson, ct);
        EnsureUniqueLabelStatisticTargets(fieldsJson, blocksJson);

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
            .Set(x => x.UpdatedByUserId, me.Id);

        var res = await _ctx.DynamicFormTemplates.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (res.MatchedCount == 0)
            throw DynamicFormNotFound(id);

        return await GetByIdAsync(id, ct);
    }

    public async Task<DynamicFormStatisticConfigUpdateResp> UpdateStatisticConfigAsync(
        string id,
        UpdateDynamicFormStatisticConfigReq req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var doc = await LoadAsync(id, ct);
        var isSystemAdmin = RoleGuard.IsSystemAdmin(me);
        RequireCanUpdateStatisticConfig(me, doc, isSystemAdmin);

        var now = DateTime.UtcNow;
        var retainedDynamicExcelTemplateIds = ExtractDynamicExcelTemplateIds(doc.BlocksJson, doc.ExcelBlockJson);
        var monthKey = BuildStatisticConfigMonthKey(now);
        if (!isSystemAdmin && string.Equals(doc.StatisticConfigUpdateMonthKey, monthKey, StringComparison.Ordinal))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FORM_STATISTIC_CONFIG_RATE_LIMITED,
                DynamicFormDetails(doc, me.Id));

        var fieldsJson = NormalizeJsonArray(req.FieldsJson, "FieldsJson");
        EnsureFieldsLimit(fieldsJson);
        EnsureFieldDisplayNames(fieldsJson);
        fieldsJson = NormalizeFieldPayload(fieldsJson, doc.FieldsJson);
        var excelBlockJson = NormalizeOptionalJsonObject(req.ExcelBlockJson, "ExcelBlockJson");
        var blocksJson = NormalizeBlocksJson(req.BlocksJson, excelBlockJson);
        blocksJson = NormalizeBlocksForSections(blocksJson, doc.SectionsJson);
        blocksJson = await NormalizeBlocksForDynamicExcelTemplatesAsync(me, blocksJson, ct, retainedDynamicExcelTemplateIds);
        excelBlockJson = ExtractFirstBlockJson(blocksJson);

        var currentBlocksJson = NormalizeBlocksJson(doc.BlocksJson, doc.ExcelBlockJson);
        currentBlocksJson = NormalizeBlocksForSections(currentBlocksJson, doc.SectionsJson);
        currentBlocksJson = await NormalizeBlocksForDynamicExcelTemplatesAsync(me, currentBlocksJson, ct, retainedDynamicExcelTemplateIds);
        EnsureStatisticConfigOnlyChange(doc.FieldsJson, fieldsJson, "FieldsJson");
        EnsureStatisticConfigOnlyChange(currentBlocksJson, blocksJson, "BlocksJson");
        EnsureTableStatisticContract(excelBlockJson, "ExcelBlockJson");
        EnsureBlocksTableStatisticContract(blocksJson, "BlocksJson");
        await EnsureLabelReferencesAsync(me, doc.TagCodes, doc.SectionsJson, fieldsJson, excelBlockJson, blocksJson, ct);
        EnsureUniqueLabelStatisticTargets(fieldsJson, blocksJson);

        var update = Builders<DynamicFormTemplate>.Update
            .Set(x => x.FieldsJson, fieldsJson)
            .Set(x => x.ExcelBlockJson, excelBlockJson)
            .Set(x => x.BlocksJson, blocksJson)
            .Set(x => x.ExcelBlockDynamicExcelTemplateId, ExtractPrimaryBlockDynamicExcelTemplateId(excelBlockJson, blocksJson))
            .Set(x => x.StatisticConfigUpdatedAtUtc, now)
            .Set(x => x.StatisticConfigUpdatedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        if (!isSystemAdmin)
            update = update.Set(x => x.StatisticConfigUpdateMonthKey, monthKey);

        var res = await _ctx.DynamicFormTemplates.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (res.MatchedCount == 0)
            throw DynamicFormNotFound(id);

        var detail = await GetByIdAsync(id, ct);
        var rebuildJob = await _statisticRebuildJobs.EnqueueForTemplateStatisticConfigAsync(
            doc,
            me.Id,
            isSystemAdmin,
            ct);
        if (isSystemAdmin)
            HangfireRecurringJobRegistrar.TriggerDynamicFormStatisticRebuildNow();

        return new DynamicFormStatisticConfigUpdateResp(
            detail,
            rebuildJob.JobId,
            rebuildJob.QueuedReportCount,
            rebuildJob.ScheduledAtUtc,
            rebuildJob.RunsImmediately,
            detail.StatisticConfigUpdatedAtUtc,
            detail.StatisticConfigUpdatedByUserId,
            detail.StatisticConfigUpdateMonthKey);
    }

    public async Task<DynamicFormDetail> PublishAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var doc = await LoadAsync(id, ct);

        RequireCanMutate(me, doc);

        if (doc.IsPublished)
            return await ToDetailAsync(doc, me, ct);

        EnsureFieldsLimit(doc.FieldsJson);
        EnsureFieldDisplayNames(doc.FieldsJson);
        var retainedDynamicExcelTemplateIds = ExtractDynamicExcelTemplateIds(doc.BlocksJson, doc.ExcelBlockJson);
        var blocksJson = NormalizeBlocksJson(doc.BlocksJson, doc.ExcelBlockJson);
        blocksJson = NormalizeBlocksForSections(blocksJson, doc.SectionsJson);
        blocksJson = await NormalizeBlocksForDynamicExcelTemplatesAsync(me, blocksJson, ct, retainedDynamicExcelTemplateIds);
        var excelBlockJson = ExtractFirstBlockJson(blocksJson);
        await EnsureLabelReferencesAsync(
            me,
            doc.TagCodes,
            doc.SectionsJson,
            doc.FieldsJson,
            excelBlockJson,
            blocksJson,
            ct);
        EnsureTableStatisticContract(excelBlockJson, "ExcelBlockJson");
        EnsureBlocksTableStatisticContract(blocksJson, "BlocksJson");
        EnsureUniqueLabelStatisticTargets(doc.FieldsJson, blocksJson);

        var now = DateTime.UtcNow;
        var update = Builders<DynamicFormTemplate>.Update
            .Set(x => x.IsPublished, true)
            .Set(x => x.IsActive, true)
            .Set(x => x.ExcelBlockJson, excelBlockJson)
            .Set(x => x.BlocksJson, blocksJson)
            .Set(x => x.ExcelBlockDynamicExcelTemplateId, ExtractPrimaryBlockDynamicExcelTemplateId(excelBlockJson, blocksJson))
            .Set(x => x.PublishedAtUtc, now)
            .Set(x => x.PublishedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        var res = await _ctx.DynamicFormTemplates.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (res.MatchedCount == 0)
            throw DynamicFormNotFound(id);

        return await GetByIdAsync(id, ct);
    }

    public async Task<DynamicFormDetail> CloneAsync(string id, CloneDynamicFormReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var source = await LoadAsync(id, ct);
        await RequireCanCloneAsync(me, source, ct);
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
        var sourceExcelBlockJson = ExtractFirstBlockJson(sourceBlocksJson);
        EnsureTableStatisticContract(sourceExcelBlockJson, "ExcelBlockJson");
        EnsureBlocksTableStatisticContract(sourceBlocksJson, "BlocksJson");
        var clone = new DynamicFormTemplate
        {
            Code = code,
            Name = string.IsNullOrWhiteSpace(req.Name) ? $"{source.Name} - Copy" : NormalizeName(req.Name),
            Description = source.Description,
            TagCodes = source.TagCodes,
            CreatedByUsername = me.Username,
            SchemaVersion = source.SchemaVersion,
            VersionNo = source.VersionNo + 1,
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

        await _ctx.DynamicFormTemplates.InsertOneAsync(clone, cancellationToken: ct);
        return await ToDetailAsync(clone, me, ct);
    }

    public async Task<DynamicFormDetail> WrapDynamicExcelAsync(
        WrapDynamicExcelAsFormReq req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var dynamicExcelId = req.DynamicExcelTemplateId?.Trim();
        if (string.IsNullOrWhiteSpace(dynamicExcelId))
            throw DynamicExcelIdRequired(dynamicExcelId);

        var excel = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == dynamicExcelId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw DynamicExcelNotFound(dynamicExcelId);
        RequireCanReadDynamicExcel(me, excel);

        if (ShouldReuseExistingWrap(req))
        {
            var existing = await _ctx.DynamicFormTemplates
                .Find(x =>
                    x.ExcelBlockDynamicExcelTemplateId == dynamicExcelId &&
                    x.CreatedByUserId == me.Id &&
                    !x.IsDeleted &&
                    x.IsActive)
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

        var doc = new DynamicFormTemplate
        {
            Code = code,
            Name = string.IsNullOrWhiteSpace(req.Name) ? excel.Name : NormalizeName(req.Name!),
            Description = NormalizeOptionalText(req.Description),
            TagCodes = tagCodes,
            CreatedByUsername = me.Username,
            SchemaVersion = 1,
            VersionNo = 1,
            IsActive = true,
            IsPublished = true,
            PublishedAtUtc = now,
            PublishedByUserId = me.Id,
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
        EnsureUniqueLabelStatisticTargets(doc.FieldsJson, doc.BlocksJson);

        await _ctx.DynamicFormTemplates.InsertOneAsync(doc, cancellationToken: ct);
        return await ToDetailAsync(doc, me, ct);

        static string createStableSectionId(string id) => $"excel_{id}";

        static bool ShouldReuseExistingWrap(WrapDynamicExcelAsFormReq request)
            => string.IsNullOrWhiteSpace(request.Code)
               && string.IsNullOrWhiteSpace(request.Name)
               && string.IsNullOrWhiteSpace(request.Description)
               && (request.TagCodes is null || request.TagCodes.Length == 0);
    }

    public async Task<DynamicFormDetail> ImportDynamicExcelBlockAsync(
        string id,
        ImportDynamicExcelBlockReq req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var doc = await LoadAsync(id, ct);

        RequireCanMutate(me, doc);
        EnsureDraftTemplate(doc);
        await EnsureNotLinkedToRuntimeAsync(id, ct);
        var retainedDynamicExcelTemplateIds = ExtractDynamicExcelTemplateIds(doc.BlocksJson, doc.ExcelBlockJson);

        var dynamicExcelId = req.DynamicExcelTemplateId?.Trim();
        if (string.IsNullOrWhiteSpace(dynamicExcelId))
            throw DynamicExcelIdRequired(dynamicExcelId);

        var excel = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == dynamicExcelId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw DynamicExcelNotFound(dynamicExcelId);
        RequireCanReadDynamicExcel(me, excel);

        var currentBlocksJson = NormalizeBlocksJson(doc.BlocksJson, doc.ExcelBlockJson);
        EnsureFieldsLimit(doc.FieldsJson);
        EnsureFieldDisplayNames(doc.FieldsJson);
        currentBlocksJson = NormalizeBlocksForSections(currentBlocksJson, doc.SectionsJson);
        var sectionId = NormalizeImportSectionId(req.SectionId, doc.SectionsJson);
        var snapshot = BuildDynamicExcelBlockSnapshot(excel, sectionId);
        var blocksJson = AppendDynamicExcelBlock(currentBlocksJson, snapshot);
        blocksJson = NormalizeBlocksForSections(blocksJson, doc.SectionsJson);
        blocksJson = await NormalizeBlocksForDynamicExcelTemplatesAsync(me, blocksJson, ct, retainedDynamicExcelTemplateIds);
        var excelBlockJson = ExtractFirstBlockJson(blocksJson);
        EnsureBlocksTableStatisticContract(blocksJson, "BlocksJson");
        await EnsureLabelReferencesAsync(me, doc.TagCodes, doc.SectionsJson, doc.FieldsJson, excelBlockJson, blocksJson, ct);
        EnsureUniqueLabelStatisticTargets(doc.FieldsJson, blocksJson);

        var now = DateTime.UtcNow;
        var update = Builders<DynamicFormTemplate>.Update
            .Set(x => x.BlocksJson, blocksJson)
            .Set(x => x.ExcelBlockJson, excelBlockJson)
            .Set(x => x.ExcelBlockDynamicExcelTemplateId, ExtractPrimaryBlockDynamicExcelTemplateId(excelBlockJson, blocksJson))
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        var res = await _ctx.DynamicFormTemplates.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (res.MatchedCount == 0)
            throw DynamicFormNotFound(id);

        return await GetByIdAsync(id, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var doc = await LoadAsync(id, ct);

        RequireCanMutate(me, doc);
        EnsureDraftTemplate(doc);
        await EnsureNotLinkedToRuntimeAsync(id, ct);

        var now = DateTime.UtcNow;
        var update = Builders<DynamicFormTemplate>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.DeletedAtUtc, now)
            .Set(x => x.DeletedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        var res = await _ctx.DynamicFormTemplates.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (res.MatchedCount == 0)
            throw DynamicFormNotFound(id);
    }

    private async Task<DynamicFormTemplate> LoadAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw DynamicFormIdRequired(id);

        return await _ctx.DynamicFormTemplates
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw DynamicFormNotFound(id);
    }

    private async Task<DynamicFormDetail> ToDetailAsync(DynamicFormTemplate x, MeResponse me, CancellationToken ct)
    {
        var canViewByCloneGrant = await HasApprovedCloneGrantAsync(x.Id, me.Id, ct);
        return new DynamicFormDetail(
            x.Id,
            x.Code,
            x.Name,
            x.Description,
            x.TagCodes,
            x.SchemaVersion,
            x.VersionNo,
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
            NormalizeBlocksJson(x.BlocksJson, x.ExcelBlockJson),
            x.ExcelBlockDynamicExcelTemplateId,
            x.StatisticConfigUpdatedAtUtc,
            x.StatisticConfigUpdatedByUserId,
            x.StatisticConfigUpdateMonthKey,
            CanMutate(me, x),
            CanClone(me, x, canViewByCloneGrant),
            canViewByCloneGrant);
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
            x.IsActive,
            x.IsPublished,
            x.CreatedByUserId,
            x.CreatedByUsername,
            x.CreatedAtUtc,
            CanMutate(me, x),
            CanClone(me, x, canViewByCloneGrant),
            canViewByCloneGrant);
    }

    private static AppException DynamicFormIdRequired(string? dynamicFormTemplateId)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.DYNAMIC_FORM_TEMPLATE_ID_REQUIRED,
            new { dynamicFormTemplateId });

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

    private static FilterDefinition<DynamicFormTemplate> BuildVisibleFilter(
        string userId,
        IReadOnlySet<string> approvedCloneTemplateIds)
    {
        var f = Builders<DynamicFormTemplate>.Filter;
        var filter = f.Eq(x => x.CreatedByUserId, userId);

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
        if (string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal))
            return;

        if (await HasApprovedCloneGrantAsync(doc.Id, me.Id, ct))
            return;

        if (await HasRuntimeReadGrantAsync(doc.Id, me.Id, ct))
            return;

        throw AppExceptionFactory.Forbidden(
            AppErrorCode.DYNAMIC_FORM_READ_FORBIDDEN,
            DynamicFormDetails(doc, me.Id));
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
                    !x.IsDeleted)
                .Limit(1)
                .AnyAsync(ct))
            return true;

        return await _ctx.ReviewReportListDocRoles
            .Find(x =>
                x.DynamicFormTemplateId == templateId &&
                x.ReviewerUserId == userId &&
                !x.IsDeleted)
            .Limit(1)
            .AnyAsync(ct);
    }

    private async Task RequireCanCloneAsync(MeResponse me, DynamicFormTemplate doc, CancellationToken ct)
    {
        if (string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal))
            return;

        if (await HasApprovedCloneGrantAsync(doc.Id, me.Id, ct))
            return;

        throw AppExceptionFactory.Forbidden(
            AppErrorCode.DYNAMIC_FORM_CLONE_FORBIDDEN,
            DynamicFormDetails(doc, me.Id));
    }

    private static bool CanMutate(MeResponse me, DynamicFormTemplate doc)
        => string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal) || RoleGuard.IsSystemAdmin(me);

    private static bool CanClone(MeResponse me, DynamicFormTemplate doc, bool canViewByCloneGrant)
        => string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal) || canViewByCloneGrant;

    private static void RequireCanReadDynamicExcel(MeResponse me, DynamicExcelTemplate doc)
    {
        if (!string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal))
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.DYNAMIC_EXCEL_READ_FORBIDDEN,
                new
                {
                    dynamicExcelTemplateId = doc.Id,
                    doc.Code,
                    doc.Name,
                    doc.CreatedByUserId,
                    actorUserId = me.Id
                });
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
                DynamicFormDetails(doc, me.Id));
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
                DynamicFormDetails(doc, me.Id));
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
    }

    private async Task<bool> ContainsDynamicFormTemplateIdAsync(
        string collectionName,
        string templateId,
        CancellationToken ct)
    {
        var collection = _ctx.Db.GetCollection<BsonDocument>(collectionName);
        var f = Builders<BsonDocument>.Filter;
        var idFilter = ObjectId.TryParse(templateId, out var objectId)
            ? f.Or(
                f.Eq("dynamicFormTemplateId", objectId),
                f.Eq("dynamicFormTemplateId", templateId))
            : f.Eq("dynamicFormTemplateId", templateId);
        var filter = idFilter & f.Ne("isDeleted", true);

        return await collection.Find(filter).Limit(1).AnyAsync(ct);
    }

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

        var sort = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase)
            ? Builders<DynamicFormTemplate>.Sort.Ascending(sortField)
            : Builders<DynamicFormTemplate>.Sort.Descending(sortField);

        return Builders<DynamicFormTemplate>.Sort.Combine(
            sort,
            Builders<DynamicFormTemplate>.Sort.Descending(x => x.CreatedAtUtc));
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
            typeMetadata.DataTypeOverrides);
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
            return new DynamicExcelTypeMetadata(fallbackType, Array.Empty<JsonElement>(), Array.Empty<JsonElement>());

        try
        {
            using var document = JsonDocument.Parse(specJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new DynamicExcelTypeMetadata(fallbackType, Array.Empty<JsonElement>(), Array.Empty<JsonElement>());

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

            return new DynamicExcelTypeMetadata(defaultType, defaultOptions, overrides);
        }
        catch (JsonException)
        {
            return new DynamicExcelTypeMetadata(fallbackType, Array.Empty<JsonElement>(), Array.Empty<JsonElement>());
        }
    }

    private sealed record DynamicExcelTypeMetadata(
        string DefaultDataType,
        JsonElement[] DefaultOptions,
        JsonElement[] DataTypeOverrides);

    private static string NormalizeDynamicExcelMetadataDataType(string? value)
    {
        var raw = value?.Trim().ToUpperInvariant();
        if (raw is "FULL_DATE" or "FULLDATE" or "STRICT_DATE")
            return "FULL_DATE";
        if (raw is "MULTI_SELECT" or "MULTISELECT")
            return "MULTI_SELECT";

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
            var baseKey = NormalizeFieldTechnicalKey(requestedKey ?? existingKey ?? id ?? type)
                          ?? $"field_{index + 1}";
            field["key"] = MakeUniqueFieldTechnicalKey(baseKey, usedKeys);
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

    private static string MakeUniqueFieldTechnicalKey(string baseKey, HashSet<string> usedKeys)
    {
        var normalizedBase = string.IsNullOrWhiteSpace(baseKey) ? "field" : baseKey;
        var candidate = normalizedBase;
        var suffix = 2;
        while (!usedKeys.Add(candidate))
        {
            var suffixText = $"_{suffix}";
            var maxBaseLength = Math.Max(1, 96 - suffixText.Length);
            var prefix = normalizedBase.Length <= maxBaseLength
                ? normalizedBase
                : normalizedBase[..maxBaseLength].TrimEnd('_', '.', '-');
            candidate = $"{prefix}{suffixText}";
            suffix++;
        }

        return candidate;
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
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw DynamicFormJsonInvalid(fieldName, $"{fieldName} khong phai JSON hop le.", ex);
        }

        if (node is null)
            throw DynamicFormJsonInvalid(fieldName, $"{fieldName} khong phai JSON hop le.");

        StripStatisticConfig(node, removeFieldStatisticLabels);
        return node.ToJsonString(JsonOptions);
    }

    private static void StripStatisticConfig(JsonNode? node, bool removeFieldStatisticLabels)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("isStatistic");
            obj.Remove("statistic");
            if (removeFieldStatisticLabels)
            {
                if (IsDynamicFormFieldObject(obj))
                {
                    obj.Remove("name");
                    obj.Remove("displayName");
                    obj.Remove("label");
                }

                obj.Remove("statisticLabelCodes");
            }
            obj.Remove("statisticColumns");
            obj.Remove("statisticColumnLabels");
            obj.Remove("metricLabelTargets");

            foreach (var child in obj.ToList())
                StripStatisticConfig(child.Value, removeFieldStatisticLabels);
            return;
        }

        if (node is JsonArray arr)
        {
            foreach (var item in arr)
                StripStatisticConfig(item, removeFieldStatisticLabels);
        }
    }

    private static bool IsDynamicFormFieldObject(JsonObject obj)
        => obj.ContainsKey("id")
           && obj.ContainsKey("type")
           && (obj.ContainsKey("key") || obj.ContainsKey("sectionId"));

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
        var fallbackSectionId = sectionIds[0];

        foreach (var item in blocks)
        {
            if (item is not JsonObject block)
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
                    "BlocksJson item phai la JSON object.",
                    new { fieldName = "BlocksJson" });

            RemoveRawTemplatePayload(block);
            RemoveLegacyStatisticColumnPayload(block);

            var sectionId = ReadJsonObjectString(block, "sectionId")
                            ?? ReadJsonObjectString(block, "SectionId")
                            ?? fallbackSectionId;

            if (!knownSectionIds.Contains(sectionId))
                throw DynamicFormValidation(
                    AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
                    "Phần chứa bảng Excel động không tồn tại trong biểu mẫu động.",
                    new { sectionId });

            block.Remove("SectionId");
            block["sectionId"] = sectionId;
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

        var ids = blocks
            .OfType<JsonObject>()
            .Select(block =>
                ReadJsonObjectString(block, "dynamicExcelTemplateId")
                ?? ReadJsonObjectString(block, "DynamicExcelTemplateId"))
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
            var id = ReadJsonObjectString(block, "dynamicExcelTemplateId")
                     ?? ReadJsonObjectString(block, "DynamicExcelTemplateId");
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

            block.Remove("ExcelSpecKind");
            block["excelSpecKind"] = specKind;
            block.Remove("TableMode");
            block["tableMode"] = NormalizeDynamicExcelTemplateTableMode(template.TableMode, specKind);
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

        var metadata = ReadDynamicExcelTypeMetadata(template.SpecJson);
        block["defaultDataType"] = metadata.DefaultDataType;

        if (metadata.DefaultOptions.Length > 0)
        {
            var defaultOptions = new JsonArray();
            foreach (var item in metadata.DefaultOptions)
                defaultOptions.Add(JsonNode.Parse(item.GetRawText()));
            block["defaultOptions"] = defaultOptions;
        }

        if (metadata.DataTypeOverrides.Length == 0)
            return;

        var overrides = new JsonArray();
        foreach (var item in metadata.DataTypeOverrides)
            overrides.Add(JsonNode.Parse(item.GetRawText()));
        block["dataTypeOverrides"] = overrides;
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
