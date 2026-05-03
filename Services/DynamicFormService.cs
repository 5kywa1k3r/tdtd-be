using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicExcel;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;

namespace tdtd_be.Services;

public interface IDynamicFormService
{
    Task<PagedResult<DynamicFormRow>> SearchAsync(DynamicFormSearchReq req, CancellationToken ct);
    Task<DynamicFormDetail> GetByIdAsync(string id, CancellationToken ct);
    Task<NextCodeResp> GetNextCodeAsync(int? year, CancellationToken ct);
    Task<DynamicFormDetail> CreateAsync(CreateDynamicFormReq req, CancellationToken ct);
    Task<DynamicFormDetail> UpdateAsync(string id, UpdateDynamicFormReq req, CancellationToken ct);
    Task<DynamicFormDetail> PublishAsync(string id, CancellationToken ct);
    Task<DynamicFormDetail> CloneAsync(string id, CloneDynamicFormReq req, CancellationToken ct);
    Task<DynamicFormDetail> WrapDynamicExcelAsync(WrapDynamicExcelAsFormReq req, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}

public sealed class DynamicFormService : IDynamicFormService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex LabelCodeRegex = new("^[a-z0-9][a-z0-9_.-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex MetricKeyRegex = new("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,255}$", RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedTableModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "FIXED_GRID",
        "APPEND_ROWS",
        "APPEND_COLUMNS",
        "MATRIX",
        "SUMMARY_TEMPLATE"
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

    public DynamicFormService(MongoDbContext ctx, MeAccessor me)
    {
        _ctx = ctx;
        _me = me;
    }

    public async Task<NextCodeResp> GetNextCodeAsync(int? year, CancellationToken ct)
    {
        var y = year ?? DateTime.UtcNow.Year;
        var (prefix, nextSeq, nextCode) = await ComputeNextCodeAsync(y, ct);
        return new NextCodeResp(prefix, y, nextSeq, nextCode);
    }

    public async Task<PagedResult<DynamicFormRow>> SearchAsync(DynamicFormSearchReq req, CancellationToken ct)
    {
        var page = Math.Max(0, req.Page);
        var pageSize = Math.Clamp(req.PageSize, 1, 100);

        var f = Builders<DynamicFormTemplate>.Filter;
        var filter = f.Eq(x => x.IsDeleted, false);

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

        var labelFilters = NormalizeLabels(req.Labels);
        if (labelFilters.Length > 0)
            filter &= f.In("labels", labelFilters);

        if (req.IsActive.HasValue)
            filter &= f.Eq(x => x.IsActive, req.IsActive.Value);

        if (req.IsPublished.HasValue)
            filter &= f.Eq(x => x.IsPublished, req.IsPublished.Value);

        var total = await _ctx.DynamicFormTemplates.CountDocumentsAsync(filter, cancellationToken: ct);
        var sort = BuildSort(req.SortField, req.SortDirection);

        var items = await _ctx.DynamicFormTemplates
            .Find(filter)
            .Sort(sort)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .Project(x => new DynamicFormRow(
                x.Id,
                x.Code,
                x.Name,
                x.Description,
                x.Labels,
                x.SchemaVersion,
                x.VersionNo,
                x.IsActive,
                x.IsPublished,
                x.CreatedByUsername,
                x.CreatedAtUtc))
            .ToListAsync(ct);

        return new PagedResult<DynamicFormRow>(items, total, page, pageSize);
    }

    public async Task<DynamicFormDetail> GetByIdAsync(string id, CancellationToken ct)
    {
        var doc = await LoadAsync(id, ct);
        return ToDetail(doc);
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

        var labels = NormalizeLabels(req.Labels);
        var sectionsJson = NormalizeJsonArray(req.SectionsJson, "SectionsJson");
        var fieldsJson = NormalizeJsonArray(req.FieldsJson, "FieldsJson");
        var excelBlockJson = NormalizeOptionalJsonObject(req.ExcelBlockJson, "ExcelBlockJson");
        EnsureTableStatisticContract(excelBlockJson, "ExcelBlockJson");
        await EnsureLabelReferencesAsync(me, labels, sectionsJson, fieldsJson, excelBlockJson, ct);

        var doc = new DynamicFormTemplate
        {
            Code = code,
            Name = NormalizeName(req.Name),
            Description = NormalizeOptionalText(req.Description),
            Labels = labels,
            CreatedByUsername = me.Username,
            SchemaVersion = Math.Max(1, req.SchemaVersion ?? 1),
            VersionNo = 1,
            IsActive = req.IsActive,
            IsPublished = false,
            SectionsJson = sectionsJson,
            FieldsJson = fieldsJson,
            ExcelBlockJson = excelBlockJson,
            ExcelBlockDynamicExcelTemplateId = ExtractExcelBlockDynamicExcelTemplateId(excelBlockJson),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = me.Id,
            UpdatedByUserId = me.Id,
            IsDeleted = false,
        };

        await _ctx.DynamicFormTemplates.InsertOneAsync(doc, cancellationToken: ct);
        return ToDetail(doc);
    }

    public async Task<DynamicFormDetail> UpdateAsync(string id, UpdateDynamicFormReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var doc = await LoadAsync(id, ct);

        RequireCanMutate(me, doc);
        EnsureDraftTemplate(doc);
        await EnsureNotLinkedToRuntimeAsync(id, ct);

        var now = DateTime.UtcNow;
        var labels = NormalizeLabels(req.Labels);
        var sectionsJson = NormalizeJsonArray(req.SectionsJson, "SectionsJson");
        var fieldsJson = NormalizeJsonArray(req.FieldsJson, "FieldsJson");
        var excelBlockJson = NormalizeOptionalJsonObject(req.ExcelBlockJson, "ExcelBlockJson");
        EnsureTableStatisticContract(excelBlockJson, "ExcelBlockJson");
        await EnsureLabelReferencesAsync(me, labels, sectionsJson, fieldsJson, excelBlockJson, ct);

        var update = Builders<DynamicFormTemplate>.Update
            .Set(x => x.Name, NormalizeName(req.Name))
            .Set(x => x.Description, NormalizeOptionalText(req.Description))
            .Set(x => x.Labels, labels)
            .Set(x => x.SchemaVersion, Math.Max(1, req.SchemaVersion ?? doc.SchemaVersion))
            .Set(x => x.IsActive, req.IsActive)
            .Set(x => x.SectionsJson, sectionsJson)
            .Set(x => x.FieldsJson, fieldsJson)
            .Set(x => x.ExcelBlockJson, excelBlockJson)
            .Set(x => x.ExcelBlockDynamicExcelTemplateId, ExtractExcelBlockDynamicExcelTemplateId(excelBlockJson))
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        var res = await _ctx.DynamicFormTemplates.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (res.MatchedCount == 0)
            throw new InvalidOperationException("Dynamic form template not found.");

        return await GetByIdAsync(id, ct);
    }

    public async Task<DynamicFormDetail> PublishAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var doc = await LoadAsync(id, ct);

        RequireCanMutate(me, doc);

        if (doc.IsPublished)
            return ToDetail(doc);

        await EnsureLabelReferencesAsync(
            me,
            doc.Labels,
            doc.SectionsJson,
            doc.FieldsJson,
            doc.ExcelBlockJson,
            ct);
        EnsureTableStatisticContract(doc.ExcelBlockJson, "ExcelBlockJson");

        var now = DateTime.UtcNow;
        var update = Builders<DynamicFormTemplate>.Update
            .Set(x => x.IsPublished, true)
            .Set(x => x.IsActive, true)
            .Set(x => x.PublishedAtUtc, now)
            .Set(x => x.PublishedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        var res = await _ctx.DynamicFormTemplates.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (res.MatchedCount == 0)
            throw new InvalidOperationException("Dynamic form template not found.");

        return await GetByIdAsync(id, ct);
    }

    public async Task<DynamicFormDetail> CloneAsync(string id, CloneDynamicFormReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var source = await LoadAsync(id, ct);
        var now = DateTime.UtcNow;
        var code = NormalizeCode(req.Code);
        if (string.IsNullOrWhiteSpace(code))
        {
            var (_, _, nextCode) = await ComputeNextCodeAsync(now.Year, ct);
            code = nextCode;
        }

        var clone = new DynamicFormTemplate
        {
            Code = code,
            Name = string.IsNullOrWhiteSpace(req.Name) ? $"{source.Name} - Copy" : NormalizeName(req.Name),
            Description = source.Description,
            Labels = source.Labels,
            CreatedByUsername = me.Username,
            SchemaVersion = source.SchemaVersion,
            VersionNo = source.VersionNo + 1,
            IsActive = true,
            IsPublished = false,
            SectionsJson = source.SectionsJson,
            FieldsJson = source.FieldsJson,
            ExcelBlockJson = source.ExcelBlockJson,
            ExcelBlockDynamicExcelTemplateId = source.ExcelBlockDynamicExcelTemplateId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = me.Id,
            UpdatedByUserId = me.Id,
            IsDeleted = false,
        };

        await _ctx.DynamicFormTemplates.InsertOneAsync(clone, cancellationToken: ct);
        return ToDetail(clone);
    }

    public async Task<DynamicFormDetail> WrapDynamicExcelAsync(
        WrapDynamicExcelAsFormReq req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var dynamicExcelId = req.DynamicExcelTemplateId?.Trim();
        if (string.IsNullOrWhiteSpace(dynamicExcelId))
            throw new BadHttpRequestException("DynamicExcelTemplateId không được trống.");

        var excel = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == dynamicExcelId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Dynamic excel template not found.");

        if (ShouldReuseExistingWrap(req))
        {
            var existing = await _ctx.DynamicFormTemplates
                .Find(x =>
                    x.ExcelBlockDynamicExcelTemplateId == dynamicExcelId &&
                    !x.IsDeleted &&
                    x.IsActive)
                .SortBy(x => x.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);

            if (existing is not null)
                return ToDetail(existing);
        }

        var now = DateTime.UtcNow;
        var code = NormalizeCode(req.Code);
        if (string.IsNullOrWhiteSpace(code))
        {
            var (_, _, nextCode) = await ComputeNextCodeAsync(now.Year, ct);
            code = nextCode;
        }
        var labels = NormalizeLabels(req.Labels);

        var sectionId = createStableSectionId(dynamicExcelId);
        var blockId = $"excel_{excel.Id}";
        var snapshot = new DynamicFormExcelBlockSnapshot(
            excel.Id,
            excel.Code,
            excel.Name,
            excel.RawWorkbookDataJson,
            excel.SpecJson,
            new DynamicExcelDataRectDto(
                excel.DataRectR0,
                excel.DataRectC0,
                excel.DataRectR1,
                excel.DataRectC1),
            excel.W,
            excel.H,
            blockId,
            "FIXED_GRID",
            BuildFixedGridIndexMap(blockId, excel.W, excel.H));

        var section = new[]
        {
            new
            {
                id = sectionId,
                title = "Excel block",
                description = excel.Name,
                order = 0,
            }
        };

        var doc = new DynamicFormTemplate
        {
            Code = code,
            Name = string.IsNullOrWhiteSpace(req.Name) ? excel.Name : NormalizeName(req.Name!),
            Description = NormalizeOptionalText(req.Description),
            Labels = labels,
            CreatedByUsername = me.Username,
            SchemaVersion = 1,
            VersionNo = 1,
            IsActive = true,
            IsPublished = true,
            PublishedAtUtc = now,
            PublishedByUserId = me.Id,
            SectionsJson = JsonSerializer.Serialize(section, JsonOptions),
            FieldsJson = "[]",
            ExcelBlockJson = JsonSerializer.Serialize(snapshot, JsonOptions),
            ExcelBlockDynamicExcelTemplateId = excel.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = me.Id,
            UpdatedByUserId = me.Id,
            IsDeleted = false,
        };

        await EnsureLabelReferencesAsync(me, labels, doc.SectionsJson, doc.FieldsJson, doc.ExcelBlockJson, ct);

        await _ctx.DynamicFormTemplates.InsertOneAsync(doc, cancellationToken: ct);
        return ToDetail(doc);

        static string createStableSectionId(string id) => $"excel_{id}";

        static bool ShouldReuseExistingWrap(WrapDynamicExcelAsFormReq request)
            => string.IsNullOrWhiteSpace(request.Code)
               && string.IsNullOrWhiteSpace(request.Name)
               && string.IsNullOrWhiteSpace(request.Description)
               && (request.Labels is null || request.Labels.Length == 0);
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
            throw new InvalidOperationException("Dynamic form template not found.");
    }

    private async Task<DynamicFormTemplate> LoadAsync(string id, CancellationToken ct)
        => await _ctx.DynamicFormTemplates
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Dynamic form template not found.");

    private static DynamicFormDetail ToDetail(DynamicFormTemplate x)
        => new(
            x.Id,
            x.Code,
            x.Name,
            x.Description,
            x.Labels,
            x.SchemaVersion,
            x.VersionNo,
            x.IsActive,
            x.IsPublished,
            x.CreatedByUsername,
            x.CreatedAtUtc,
            x.UpdatedAtUtc,
            x.PublishedAtUtc,
            x.SectionsJson,
            x.FieldsJson,
            x.ExcelBlockJson,
            x.ExcelBlockDynamicExcelTemplateId);

    private void RequireCanMutate(MeResponse me, DynamicFormTemplate doc)
    {
        if (!string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal))
            throw new BadHttpRequestException("Bạn không có quyền sửa/xóa dynamic form này.");
    }

    private static void EnsureDraftTemplate(DynamicFormTemplate doc)
    {
        if (doc.IsPublished)
            throw new InvalidOperationException("Dynamic form đã publish. Hãy clone để tạo phiên bản mới.");
    }

    private async Task EnsureNotLinkedToRuntimeAsync(string templateId, CancellationToken ct)
    {
        if (await ContainsDynamicFormTemplateIdAsync(_ctx.WorkAssignments.CollectionNamespace.CollectionName, templateId, ct))
            throw new InvalidOperationException("Dynamic form template is already used by an assignment.");

        if (await ContainsDynamicFormTemplateIdAsync(_ctx.WorkTemplateAssignees.CollectionNamespace.CollectionName, templateId, ct))
            throw new InvalidOperationException("Dynamic form template is already used by assignment bindings.");

        if (await ContainsDynamicFormTemplateIdAsync(_ctx.WorkReportPeriods.CollectionNamespace.CollectionName, templateId, ct))
            throw new InvalidOperationException("Dynamic form template is already used by report periods.");

        if (await ContainsDynamicFormTemplateIdAsync(_ctx.WorkAssignmentReports.CollectionNamespace.CollectionName, templateId, ct))
            throw new InvalidOperationException("Dynamic form template is already used by report data.");
    }

    private async Task<bool> ContainsDynamicFormTemplateIdAsync(
        string collectionName,
        string templateId,
        CancellationToken ct)
    {
        var collection = _ctx.Db.GetCollection<BsonDocument>(collectionName);
        var filter = Builders<BsonDocument>.Filter.Eq("dynamicFormTemplateId", templateId)
            & Builders<BsonDocument>.Filter.Ne("isDeleted", true);

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
            throw new BadHttpRequestException("Tên dynamic form không được trống.");

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
            throw new BadHttpRequestException($"{fieldName} phải là JSON object hoặc null.");

        var tableMode = ReadOptionalString(root, "tableMode") ?? "FIXED_GRID";
        if (!AllowedTableModes.Contains(tableMode))
            throw new BadHttpRequestException($"tableMode không hợp lệ: {tableMode}.");

        if (root.TryGetProperty("indexMap", out var indexMap))
            ValidateIndexMap(indexMap);

        if (root.TryGetProperty("metricRules", out var metricRules))
            ValidateMetricRules(metricRules);

        if (string.Equals(tableMode, "SUMMARY_TEMPLATE", StringComparison.OrdinalIgnoreCase))
            ValidateSummaryTemplateLayout(root);

        static void ValidateIndexMap(JsonElement indexMap)
        {
            if (indexMap.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return;

            if (indexMap.ValueKind != JsonValueKind.Array)
                throw new BadHttpRequestException("indexMap phải là JSON array.");

            foreach (var item in indexMap.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new BadHttpRequestException("indexMap item phải là JSON object.");

                var metricKey = ReadOptionalString(item, "metricKey");
                if (string.IsNullOrWhiteSpace(metricKey))
                    throw new BadHttpRequestException("indexMap.metricKey không được trống.");

                ValidateMetricKey(metricKey);
            }
        }

        static void ValidateMetricRules(JsonElement metricRules)
        {
            if (metricRules.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return;

            if (metricRules.ValueKind != JsonValueKind.Array)
                throw new BadHttpRequestException("metricRules phải là JSON array.");

            foreach (var item in metricRules.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new BadHttpRequestException("metricRules item phải là JSON object.");

                var metricKey = ReadOptionalString(item, "metricKey");
                if (string.IsNullOrWhiteSpace(metricKey))
                    throw new BadHttpRequestException("metricRules.metricKey không được trống.");

                ValidateMetricKey(metricKey);
            }
        }
    }

    private static void ValidateSummaryTemplateLayout(JsonElement root)
    {
        var outputLayout = default(JsonElement);
        var hasOutputLayout = root.TryGetProperty("outputLayout", out outputLayout)
                              && outputLayout.ValueKind != JsonValueKind.Null
                              && outputLayout.ValueKind != JsonValueKind.Undefined;

        if (hasOutputLayout && outputLayout.ValueKind != JsonValueKind.Object)
            throw new BadHttpRequestException("SUMMARY_TEMPLATE.outputLayout phai la JSON object.");

        var sourceBlockId = ReadOptionalString(root, "sourceBlockId")
                            ?? (hasOutputLayout ? ReadOptionalString(outputLayout, "sourceBlockId") : null);
        if (string.IsNullOrWhiteSpace(sourceBlockId))
            throw new BadHttpRequestException("SUMMARY_TEMPLATE.sourceBlockId khong duoc trong.");

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

        throw new BadHttpRequestException("SUMMARY_TEMPLATE.rowLayout khong duoc trong.");
    }

    private static void ValidateSummaryGroupBy(JsonElement groupBy)
    {
        if (groupBy.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;

        if (groupBy.ValueKind != JsonValueKind.Array)
            throw new BadHttpRequestException("SUMMARY_TEMPLATE.groupBy phai la JSON array.");

        foreach (var item in groupBy.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new BadHttpRequestException("SUMMARY_TEMPLATE.groupBy item phai la string.");

            var value = item.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(value) || !AllowedSummaryTemplateGroupBy.Contains(value))
                throw new BadHttpRequestException($"SUMMARY_TEMPLATE.groupBy khong hop le: {value}.");
        }
    }

    private static void ValidateSummaryRowLayout(JsonElement rowLayout)
    {
        if (rowLayout.ValueKind != JsonValueKind.Array)
            throw new BadHttpRequestException("SUMMARY_TEMPLATE.rowLayout phai la JSON array.");

        var count = 0;
        foreach (var item in rowLayout.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new BadHttpRequestException("SUMMARY_TEMPLATE.rowLayout item phai la JSON object.");

            count++;

            var repeatFor = ReadOptionalString(item, "repeatFor");
            if (!string.IsNullOrWhiteSpace(repeatFor) && !AllowedSummaryTemplateRepeatFor.Contains(repeatFor))
                throw new BadHttpRequestException($"SUMMARY_TEMPLATE.rowLayout.repeatFor khong hop le: {repeatFor}.");

            if (item.TryGetProperty("rowsPerUnit", out var rowsPerUnit)
                && rowsPerUnit.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                if (rowsPerUnit.ValueKind != JsonValueKind.Number
                    || !rowsPerUnit.TryGetInt32(out var rows)
                    || rows < 1
                    || rows > 100)
                {
                    throw new BadHttpRequestException("SUMMARY_TEMPLATE.rowLayout.rowsPerUnit phai nam trong 1..100.");
                }
            }

            if (!item.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Array)
                throw new BadHttpRequestException("SUMMARY_TEMPLATE.rowLayout.metrics phai la JSON array.");

            var metricCount = 0;
            foreach (var metric in metrics.EnumerateArray())
            {
                if (metric.ValueKind != JsonValueKind.String)
                    throw new BadHttpRequestException("SUMMARY_TEMPLATE.rowLayout.metrics item phai la string.");

                var metricKey = metric.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(metricKey))
                    throw new BadHttpRequestException("SUMMARY_TEMPLATE.rowLayout.metrics khong duoc chua chuoi trong.");

                ValidateMetricKey(metricKey);
                metricCount++;
            }

            if (metricCount == 0)
                throw new BadHttpRequestException("SUMMARY_TEMPLATE.rowLayout.metrics khong duoc trong.");
        }

        if (count == 0)
            throw new BadHttpRequestException("SUMMARY_TEMPLATE.rowLayout khong duoc trong.");
    }

    private static string? ReadOptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return value.GetString()?.Trim();
    }

    private static void ValidateMetricKey(string metricKey)
    {
        if (!MetricKeyRegex.IsMatch(metricKey))
            throw new BadHttpRequestException("metricKey chỉ gồm chữ, số, dấu ., _, :, - và tối đa 256 ký tự.");
    }

    private static DynamicFormTableIndexMapItem[] BuildFixedGridIndexMap(
        string blockId,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0)
            return Array.Empty<DynamicFormTableIndexMapItem>();

        var rows = new List<DynamicFormTableIndexMapItem>(width * height);
        for (var r = 0; r < height; r++)
        {
            for (var c = 0; c < width; c++)
            {
                var index = r * width + c;
                var rowKey = $"row_{r + 1}";
                var columnKey = $"col_{c + 1}";
                rows.Add(new DynamicFormTableIndexMapItem(
                    index,
                    rowKey,
                    columnKey,
                    $"table:{blockId}.row:{rowKey}.column:{columnKey}"));
            }
        }

        return rows.ToArray();
    }

    private async Task EnsureLabelReferencesAsync(
        MeResponse me,
        string[]? formLabels,
        string? sectionsJson,
        string? fieldsJson,
        string? excelBlockJson,
        CancellationToken ct)
    {
        var codes = new HashSet<string>(NormalizeLabels(formLabels), StringComparer.OrdinalIgnoreCase);
        CollectLabelCodes(sectionsJson, codes);
        CollectLabelCodes(fieldsJson, codes);
        CollectLabelCodes(excelBlockJson, codes);

        if (codes.Count == 0)
            return;

        var fb = Builders<LabelCatalogItem>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(x => x.IsActive, true)
                     & fb.In(x => x.Code, codes)
                     & BuildLabelVisibilityFilter(me);

        var found = await _ctx.Labels
            .Find(filter)
            .Project(x => x.Code)
            .ToListAsync(ct);

        var foundSet = new HashSet<string>(found, StringComparer.OrdinalIgnoreCase);
        var missing = codes
            .Where(code => !foundSet.Contains(code))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missing.Length > 0)
            throw new BadHttpRequestException($"Nhãn không tồn tại, inactive hoặc ngoài phạm vi: {string.Join(", ", missing)}.");
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

    private static void CollectLabelCodes(string? json, HashSet<string> target)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            using var document = JsonDocument.Parse(json);
            CollectLabelCodes(document.RootElement, target);
        }
        catch (JsonException)
        {
            return;
        }
    }

    private static void CollectLabelCodes(JsonElement element, HashSet<string> target)
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

                CollectLabelCodes(property.Value, target);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectLabelCodes(item, target);
        }
    }

    private static bool IsLabelCodeProperty(string name)
        => string.Equals(name, "labelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "allowedLabelCodes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "labels", StringComparison.OrdinalIgnoreCase);

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

    private static string[] NormalizeLabels(string[]? labels)
        => labels?
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
            throw new BadHttpRequestException("Mã nhãn không được trống.");
        if (!LabelCodeRegex.IsMatch(code))
            throw new BadHttpRequestException("Mã nhãn chỉ gồm chữ thường, số, dấu -, _ hoặc . và tối đa 64 ký tự.");
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
            throw new BadHttpRequestException($"{fieldName} phải là JSON object hoặc null.");

        return json.Trim();
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

    private static void ValidateJsonKind(string json, string fieldName, JsonValueKind expectedKind)
    {
        using var document = ParseJson(json, fieldName);
        if (document.RootElement.ValueKind != expectedKind)
            throw new BadHttpRequestException($"{fieldName} phải là JSON {expectedKind.ToString().ToLowerInvariant()}.");
    }

    private static JsonDocument ParseJson(string json, string fieldName)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BadHttpRequestException($"{fieldName} không phải JSON hợp lệ.", ex);
        }
    }
}
