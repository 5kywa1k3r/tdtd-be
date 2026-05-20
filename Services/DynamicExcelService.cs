using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.Json;
using System.Text.RegularExpressions;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicExcel;
using tdtd_be.Models;

namespace tdtd_be.Services;

public interface IDynamicExcelService
{
    Task<PagedResult<DynamicExcelRow>> SearchAsync(DynamicExcelSearchReq req, CancellationToken ct);
    Task<DynamicExcelDetail> GetByIdAsync(string id, CancellationToken ct);
    Task<NextCodeResp> GetNextCodeAsync(int? year, CancellationToken ct);
    Task<DynamicExcelDetail> CreateAsync(CreateDynamicExcelReq req, CancellationToken ct);
    Task<DynamicExcelDetail> UpdateAsync(string id, UpdateDynamicExcelReq req, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}

public sealed class DynamicExcelService : IDynamicExcelService
{
    private const int MaxDataCells = 250;
    private const int MaxHeaderRows = 30;
    private const int MaxHeaderCols = 30;
    private const int MaxSheetCells = 5000;

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;

    public DynamicExcelService(MongoDbContext ctx, MeAccessor me)
    {
        _ctx = ctx;
        _me = me;
    }

    private void RequireCanMutate(MeResponse me, DynamicExcelTemplate doc)
    {
        // Dynamic Excel templates are user-owned. Keep mutation rights tied to
        // the creator unless a separate admin takeover flow is introduced.
        if (!string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal))
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.DYNAMIC_EXCEL_MUTATE_FORBIDDEN,
                DynamicExcelDetails(doc, me.Id));
    }

    private async Task EnsureNotLinkedToWorkAsync(string templateId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            throw DynamicExcelIdRequired(templateId);

        var usedByAssignment = await _ctx.WorkAssignments
            .Find(x => x.DynamicExcelId == templateId && !x.IsDeleted)
            .AnyAsync(ct);
        if (usedByAssignment)
            throw DynamicExcelInUse(AppErrorCode.DYNAMIC_EXCEL_IN_USE_BY_ASSIGNMENT, templateId);

        var usedByBinding = await _ctx.WorkTemplateAssignees
            .Find(x => x.DynamicExcelId == templateId && !x.IsDeleted)
            .AnyAsync(ct);
        if (usedByBinding)
            throw DynamicExcelInUse(AppErrorCode.DYNAMIC_EXCEL_IN_USE_BY_BINDING, templateId);

        var usedByPeriod = await _ctx.WorkReportPeriods
            .Find(x => x.DynamicExcelId == templateId && !x.IsDeleted)
            .AnyAsync(ct);
        if (usedByPeriod)
            throw DynamicExcelInUse(AppErrorCode.DYNAMIC_EXCEL_IN_USE_BY_PERIOD, templateId);

        var usedByReport = await _ctx.WorkAssignmentReports
            .Find(x => x.DynamicExcelTemplateId == templateId && !x.IsDeleted)
            .AnyAsync(ct);
        if (usedByReport)
            throw DynamicExcelInUse(AppErrorCode.DYNAMIC_EXCEL_IN_USE_BY_REPORT, templateId);

        if (await IsUsedByDynamicFormAsync(templateId, ct))
            throw DynamicExcelInUse(AppErrorCode.DYNAMIC_EXCEL_IN_USE_BY_DYNAMIC_FORM, templateId);
    }

    private static string NormalizeSortDir(string? dir)
        => string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";

    private static string NormalizeSortField(string? field)
    {
        var f = (field ?? "createdAtUtc").Trim();
        return f switch
        {
            "code" => "code",
            "name" => "name",
            "createdAtUtc" => "createdAtUtc",
            "createdByUsername" => "createdByUsername",
            _ => "createdAtUtc"
        };
    }

    private static string ToCamelUsername(string username)
    {
        // username có thể: sa_anhdd, sa-anhdd, sa.anhdd...
        var parts = username
            .Trim()
            .Split(new[] { '_', '-', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0) return "user";

        var first = parts[0].ToLowerInvariant();
        var rest = parts.Skip(1)
            .Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1).ToLowerInvariant());

        return first + string.Concat(rest);
    }

    private async Task<(string prefix, int nextSeq, string nextCode)> ComputeNextCodeAsync(int year, CancellationToken ct)
    {
        var me = _me.RequireMe();

        // giữ nguyên username kể cả ký tự đặc biệt
        var prefix = $"{me.Username}-{year}-";

        var filter = Builders<DynamicExcelTemplate>.Filter.Where(x =>
            !x.IsDeleted && x.Code.StartsWith(prefix));

        var last = await _ctx.DynamicExcelTemplates
            .Find(filter)
            .Sort(Builders<DynamicExcelTemplate>.Sort.Descending(x => x.Code))
            .Limit(1)
            .FirstOrDefaultAsync(ct);

        var nextSeq = 1;

        if (last != null && last.Code.Length > prefix.Length)
        {
            var tail = last.Code.Substring(prefix.Length);

            if (int.TryParse(tail, out var seq))
                nextSeq = seq + 1;
        }

        var nextCode = prefix + nextSeq.ToString("D6"); // ✅ 000001

        return (prefix, nextSeq, nextCode);
    }

    public async Task<NextCodeResp> GetNextCodeAsync(int? year, CancellationToken ct)
    {
        var y = year ?? DateTime.UtcNow.Year;
        var (prefix, nextSeq, nextCode) = await ComputeNextCodeAsync(y, ct);
        return new NextCodeResp(prefix, y, nextSeq, nextCode);
    }

    public async Task<PagedResult<DynamicExcelRow>> SearchAsync(DynamicExcelSearchReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var page = Math.Max(0, req.Page);
        var pageSize = Math.Clamp(req.PageSize, 1, 100);

        var f = Builders<DynamicExcelTemplate>.Filter;
        var filter = f.Eq(x => x.IsDeleted, false) & f.Eq(x => x.CreatedByUserId, me.Id);

        if (!string.IsNullOrWhiteSpace(req.Code))
        {
            var key = Regex.Escape(req.Code.Trim());
            filter &= f.Regex("code", new BsonRegularExpression(key, "i"));
        }

        if (!string.IsNullOrWhiteSpace(req.Name))
        {
            var key = Regex.Escape(req.Name.Trim());
            filter &= f.Regex("name", new BsonRegularExpression(key, "i"));
        }

        if (!string.IsNullOrWhiteSpace(req.CreatedBy))
        {
            var key = Regex.Escape(req.CreatedBy.Trim());
            filter &= f.Regex("createdByUsername", new BsonRegularExpression(key, "i"));
        }

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var key = Regex.Escape(req.Q.Trim());
            var rx = new BsonRegularExpression(key, "i");
            filter &= (f.Regex("code", rx) | f.Regex("name", rx));
        }

        if (req.CreatedFromUtc.HasValue)
            filter &= f.Gte(x => x.CreatedAtUtc, req.CreatedFromUtc.Value);

        if (req.CreatedToUtc.HasValue)
            filter &= f.Lte(x => x.CreatedAtUtc, req.CreatedToUtc.Value);

        var total = await _ctx.DynamicExcelTemplates.CountDocumentsAsync(filter, cancellationToken: ct);

        var sortField = NormalizeSortField(req.SortField);
        var sortDir = NormalizeSortDir(req.SortDirection);

        SortDefinition<DynamicExcelTemplate> sort = sortDir == "asc"
            ? Builders<DynamicExcelTemplate>.Sort.Ascending(sortField)
            : Builders<DynamicExcelTemplate>.Sort.Descending(sortField);

        // stable fallback
        sort = Builders<DynamicExcelTemplate>.Sort.Combine(sort, Builders<DynamicExcelTemplate>.Sort.Descending(x => x.CreatedAtUtc));

        var projectedItems = await _ctx.DynamicExcelTemplates
            .Find(filter)
            .Sort(sort)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .Project(x => new
            {
                x.Id,
                x.Code,
                x.Name,
                x.SpecJson,
                x.TableMode,
                x.ContractVersion,
                x.CreatedByUsername,
                x.CreatedAtUtc
            })
            .ToListAsync(ct);

        var items = projectedItems
            .Select(x => new DynamicExcelRow(
                x.Id,
                x.Code,
                x.Name,
                ReadDynamicExcelHeaderKind(x.SpecJson),
                string.IsNullOrWhiteSpace(x.TableMode) ? "FIXED_GRID" : x.TableMode,
                x.ContractVersion <= 0 ? 1 : x.ContractVersion,
                x.CreatedByUsername,
                x.CreatedAtUtc
            ))
            .ToList();

        return new PagedResult<DynamicExcelRow>(items, total, page, pageSize);
    }

    public async Task<DynamicExcelDetail> GetByIdAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var x = await _ctx.DynamicExcelTemplates
            .Find(t => t.Id == id && !t.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw DynamicExcelNotFound(id);

        await EnsureCanReadAsync(me, x, ct);

        return new DynamicExcelDetail(
            x.Id, x.Code, x.Name,
            ReadDynamicExcelHeaderKind(x.SpecJson),
            string.IsNullOrWhiteSpace(x.TableMode) ? "FIXED_GRID" : x.TableMode,
            x.ContractVersion <= 0 ? 1 : x.ContractVersion,
            x.CreatedByUsername, x.CreatedAtUtc,
            x.RawWorkbookDataJson, x.SpecJson,
            new DynamicExcelDataRectDto(x.DataRectR0, x.DataRectC0, x.DataRectR1, x.DataRectC1),
            x.W, x.H
        );
    }

    public async Task<DynamicExcelDetail> CreateAsync(CreateDynamicExcelReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var tableMode = ValidateDynamicExcelPayload(req);

        var now = DateTime.UtcNow;
        var y = now.Year;

        var code = req.Code?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            var (_, _, nextCode) = await ComputeNextCodeAsync(y, ct);
            code = nextCode;
        }

        var doc = new DynamicExcelTemplate
        {
            Code = code!,
            Name = req.Name.Trim(),
            TableMode = tableMode,
            ContractVersion = Math.Max(1, req.ContractVersion ?? 1),
            CreatedByUsername = me.Username, // ✅ store for search, no join

            RawWorkbookDataJson = req.RawWorkbookDataJson.Trim(),
            SpecJson = req.SpecJson.Trim(),
            DataRectR0 = req.DataRect?.R0 ?? 0,
            DataRectC0 = req.DataRect?.C0 ?? 0,
            DataRectR1 = req.DataRect?.R1 ?? 0,
            DataRectC1 = req.DataRect?.C1 ?? 0,
            W = req.W,
            H = req.H,

            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = me.Id,
            UpdatedByUserId = me.Id,
            IsDeleted = false
        };

        await _ctx.DynamicExcelTemplates.InsertOneAsync(doc, cancellationToken: ct);

        return await GetByIdAsync(doc.Id, ct);
    }

    public async Task<DynamicExcelDetail> UpdateAsync(string id, UpdateDynamicExcelReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        if (string.IsNullOrWhiteSpace(req?.Name))
            throw DynamicExcelValidation("Tên biểu mẫu Excel động không được trống.", new { field = "name" });
        var now = DateTime.UtcNow;

        var doc = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw DynamicExcelNotFound(id);

        RequireCanMutate(me, doc);

        var filter = Builders<DynamicExcelTemplate>.Filter.Where(x => x.Id == id && !x.IsDeleted);

        var update = Builders<DynamicExcelTemplate>.Update
            .Set(x => x.Name, req.Name.Trim())
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        var res = await _ctx.DynamicExcelTemplates.UpdateOneAsync(filter, update, cancellationToken: ct);
        if (res.MatchedCount == 0)
            throw DynamicExcelNotFound(id);

        return await GetByIdAsync(id, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var now = DateTime.UtcNow;

        var doc = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw DynamicExcelNotFound(id);

        RequireCanMutate(me, doc);
        await EnsureNotLinkedToWorkAsync(id, ct);

        var filter = Builders<DynamicExcelTemplate>.Filter.Where(x => x.Id == id && !x.IsDeleted);
        var update = Builders<DynamicExcelTemplate>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.DeletedAtUtc, now)
            .Set(x => x.DeletedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        var res = await _ctx.DynamicExcelTemplates.UpdateOneAsync(filter, update, cancellationToken: ct);
        if (res.MatchedCount == 0)
            throw DynamicExcelNotFound(id);
    }

    private static string ValidateDynamicExcelPayload(CreateDynamicExcelReq req)
        => ValidateDynamicExcelPayloadCore(
            req?.Name,
            req?.TableMode,
            req?.RawWorkbookDataJson,
            req?.SpecJson,
            req?.DataRect,
            req?.W,
            req?.H);

    private static string ValidateDynamicExcelPayloadCore(
        string? name,
        string? tableModeRaw,
        string? rawWorkbookDataJson,
        string? specJson,
        DynamicExcelDataRectDto? dataRect,
        int? width,
        int? height)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw DynamicExcelValidation("Tên biểu mẫu Excel động không được trống.", new { field = "name" });

        if (dataRect is null)
            throw DynamicExcelValidation("Thiếu vùng nhập dữ liệu của biểu mẫu Excel động.", new { field = "dataRect" });

        var w = width ?? 0;
        var h = height ?? 0;
        if (w <= 0 || h <= 0 || w > 200 || h > 200)
            throw DynamicExcelValidation("Kích thước vùng nhập dữ liệu của biểu mẫu Excel động phải nằm trong 1..200.", new { w, h });

        if (dataRect.R0 < 0 || dataRect.C0 < 0 || dataRect.R1 < dataRect.R0 || dataRect.C1 < dataRect.C0)
            throw DynamicExcelValidation("Vùng nhập dữ liệu của biểu mẫu Excel động không hợp lệ.", new { dataRect });

        var rectWidth = dataRect.C1 - dataRect.C0 + 1;
        var rectHeight = dataRect.R1 - dataRect.R0 + 1;
        if (rectWidth != w || rectHeight != h)
            throw DynamicExcelValidation(
                "Chiều rộng và chiều cao phải khớp với vùng nhập dữ liệu của biểu mẫu Excel động.",
                new { w, h, rectWidth, rectHeight });

        using var workbookDocument = ParseRequiredJson(
            rawWorkbookDataJson,
            "rawWorkbookDataJson",
            JsonValueKind.Array);
        using var specDocument = ParseRequiredJson(specJson, "specJson", JsonValueKind.Object);

        var kind = ReadJsonString(specDocument.RootElement, "kind")?.ToUpperInvariant();
        if (kind is not ("TOP" or "LEFT" or "MATRIX"))
            throw DynamicExcelValidation(
                "Loại bảng Excel động phải là bảng ngang, bảng dọc hoặc bảng ma trận.",
                new
                {
                    kind,
                    kindLabel = DescribeDynamicExcelKind(kind),
                    allowedKinds = new[]
                    {
                        new { value = "TOP", label = "Bảng ngang" },
                        new { value = "LEFT", label = "Bảng dọc" },
                        new { value = "MATRIX", label = "Bảng ma trận" },
                    },
                });

        var tableMode = NormalizeDynamicExcelTableMode(tableModeRaw);
        ValidateDynamicExcelTableMode(kind, tableMode);
        ValidateNumericGridSpec(specDocument.RootElement, kind, dataRect, w, h);

        if (workbookDocument.RootElement.GetArrayLength() == 0)
            throw DynamicExcelValidation("Dữ liệu bảng tính của biểu mẫu Excel động phải có ít nhất một sheet.", new { field = "rawWorkbookDataJson" });

        var firstSheet = workbookDocument.RootElement[0];
        if (firstSheet.ValueKind != JsonValueKind.Object)
            throw DynamicExcelValidation("Sheet đầu tiên của biểu mẫu Excel động không hợp lệ.", new { field = "rawWorkbookDataJson[0]" });

        var sheetRows = InferSheetRows(firstSheet);
        var sheetCols = InferSheetCols(firstSheet);
        if (sheetRows > 0 && dataRect.R1 >= sheetRows)
            throw DynamicExcelValidation("Vùng nhập dữ liệu vượt quá số dòng của bảng tính.", new { dataRect, sheetRows });

        if (sheetCols > 0 && dataRect.C1 >= sheetCols)
            throw DynamicExcelValidation("Vùng nhập dữ liệu vượt quá số cột của bảng tính.", new { dataRect, sheetCols });

        return tableMode;
    }

    private static string NormalizeDynamicExcelTableMode(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            throw DynamicExcelValidation(
                "Dynamic Excel phải chọn kiểu nhập bảng ngay khi tạo.",
                new
                {
                    field = "tableMode",
                    allowed = new[] { "FIXED_GRID", "APPEND_ROWS", "APPEND_COLUMNS" }
                });

        return normalized switch
        {
            "FIXED_GRID" => "FIXED_GRID",
            "APPEND_ROWS" => "APPEND_ROWS",
            "APPEND_COLUMNS" => "APPEND_COLUMNS",
            _ => throw DynamicExcelValidation(
                "Kiểu nhập bảng của Dynamic Excel không hợp lệ.",
                new
                {
                    field = "tableMode",
                    tableMode = normalized,
                    allowed = new[] { "FIXED_GRID", "APPEND_ROWS", "APPEND_COLUMNS" }
                })
        };
    }

    private static void ValidateDynamicExcelTableMode(string kind, string tableMode)
    {
        var ok = kind switch
        {
            "TOP" => tableMode is "FIXED_GRID" or "APPEND_ROWS",
            "LEFT" => tableMode is "FIXED_GRID" or "APPEND_COLUMNS",
            "MATRIX" => tableMode is "FIXED_GRID",
            _ => false
        };

        if (ok)
            return;

        throw DynamicExcelValidation(
            "Kiểu nhập bảng không phù hợp với loại bảng Excel động.",
            new
            {
                kind,
                tableMode,
                allowedContracts = new[]
                {
                    "TOP + FIXED_GRID",
                    "TOP + APPEND_ROWS",
                    "LEFT + FIXED_GRID",
                    "LEFT + APPEND_COLUMNS",
                    "MATRIX + FIXED_GRID"
                }
            });
    }

    private static void ValidateNumericGridSpec(
        JsonElement spec,
        string kind,
        DynamicExcelDataRectDto dataRect,
        int width,
        int height)
    {
        var expectedDataRect = kind switch
        {
            "TOP" => ValidateTopSpec(spec),
            "LEFT" => ValidateLeftSpec(spec),
            "MATRIX" => ValidateMatrixSpec(spec),
            _ => dataRect
        };

        if (expectedDataRect.R0 != dataRect.R0 ||
            expectedDataRect.C0 != dataRect.C0 ||
            expectedDataRect.R1 != dataRect.R1 ||
            expectedDataRect.C1 != dataRect.C1)
        {
            throw DynamicExcelValidation(
                "Cấu hình loại bảng Excel động không khớp vùng nhập dữ liệu.",
                new { kind, expectedDataRect, dataRect });
        }

        var expectedWidth = expectedDataRect.C1 - expectedDataRect.C0 + 1;
        var expectedHeight = expectedDataRect.R1 - expectedDataRect.R0 + 1;
        if (expectedWidth != width || expectedHeight != height)
            throw DynamicExcelValidation(
                "Cấu hình loại bảng Excel động không khớp chiều rộng/chiều cao vùng dữ liệu.",
                new { kind, expectedWidth, expectedHeight, width, height });

        ValidateNumericGridLimits(spec, kind, expectedDataRect);
        ValidateNumericGridDataTypeMetadata(spec, kind, expectedDataRect);
    }

    private static DynamicExcelDataRectDto ValidateTopSpec(JsonElement spec)
    {
        var topRows = ReadRequiredPositiveInt(spec, "topRows");
        var topCols = ReadRequiredPositiveInt(spec, "topCols");
        var dataRows = ReadRequiredPositiveInt(spec, "dataRows");
        return new DynamicExcelDataRectDto(topRows, 0, topRows + dataRows - 1, topCols - 1);
    }

    private static DynamicExcelDataRectDto ValidateLeftSpec(JsonElement spec)
    {
        var leftRows = ReadRequiredPositiveInt(spec, "leftRows");
        var leftCols = ReadRequiredPositiveInt(spec, "leftCols");
        var dataCols = ReadRequiredPositiveInt(spec, "dataCols");
        return new DynamicExcelDataRectDto(0, leftCols, leftRows - 1, leftCols + dataCols - 1);
    }

    private static DynamicExcelDataRectDto ValidateMatrixSpec(JsonElement spec)
    {
        var topRows = ReadRequiredPositiveInt(spec, "topRows");
        var topCols = ReadRequiredPositiveInt(spec, "topCols");
        var leftRows = ReadRequiredPositiveInt(spec, "leftRows");
        var leftCols = ReadRequiredPositiveInt(spec, "leftCols");
        return new DynamicExcelDataRectDto(topRows, leftCols, topRows + leftRows - 1, leftCols + topCols - 1);
    }

    private static void ValidateNumericGridLimits(
        JsonElement spec,
        string kind,
        DynamicExcelDataRectDto dataRect)
    {
        var dataRows = dataRect.R1 - dataRect.R0 + 1;
        var dataCols = dataRect.C1 - dataRect.C0 + 1;
        var dataCells = dataRows * dataCols;
        if (dataCells > MaxDataCells)
            throw DynamicExcelValidation(
                $"Vùng dữ liệu quá lớn ({dataCols}x{dataRows} = {dataCells} ô). Giới hạn values1D: {MaxDataCells} ô.",
                new { dataCols, dataRows, dataCells, maxDataCells = MaxDataCells });

        var tableRows = kind switch
        {
            "TOP" => ReadRequiredPositiveInt(spec, "topRows") + ReadRequiredPositiveInt(spec, "dataRows"),
            "LEFT" => ReadRequiredPositiveInt(spec, "leftRows"),
            "MATRIX" => ReadRequiredPositiveInt(spec, "topRows") + ReadRequiredPositiveInt(spec, "leftRows"),
            _ => dataRows
        };
        var tableCols = kind switch
        {
            "TOP" => ReadRequiredPositiveInt(spec, "topCols"),
            "LEFT" => ReadRequiredPositiveInt(spec, "leftCols") + ReadRequiredPositiveInt(spec, "dataCols"),
            "MATRIX" => ReadRequiredPositiveInt(spec, "leftCols") + ReadRequiredPositiveInt(spec, "topCols"),
            _ => dataCols
        };
        var tableCells = tableRows * tableCols;
        if (tableCells > MaxSheetCells)
            throw DynamicExcelValidation(
                $"Bảng quá lớn ({tableCols}x{tableRows} = {tableCells} ô). Giới hạn: {MaxSheetCells} ô.",
                new { tableCols, tableRows, tableCells, maxSheetCells = MaxSheetCells });

        var headerRows = kind switch
        {
            "TOP" => ReadRequiredPositiveInt(spec, "topRows"),
            "LEFT" => 1,
            "MATRIX" => ReadRequiredPositiveInt(spec, "topRows"),
            _ => 1
        };
        var headerCols = kind switch
        {
            "TOP" => 1,
            "LEFT" => ReadRequiredPositiveInt(spec, "leftCols"),
            "MATRIX" => ReadRequiredPositiveInt(spec, "leftCols"),
            _ => 1
        };
        if (headerRows > MaxHeaderRows || headerCols > MaxHeaderCols)
            throw DynamicExcelValidation(
                $"Độ sâu vùng tiêu đề quá lớn ({headerCols} cột x {headerRows} hàng). Giới hạn: {MaxHeaderCols} cột và {MaxHeaderRows} hàng.",
                new { headerCols, headerRows, maxHeaderCols = MaxHeaderCols, maxHeaderRows = MaxHeaderRows });
    }

    private static void ValidateNumericGridDataTypeMetadata(
        JsonElement spec,
        string kind,
        DynamicExcelDataRectDto dataRect)
    {
        if (spec.TryGetProperty("defaultDataType", out var defaultDataType))
        {
            if (defaultDataType.ValueKind != JsonValueKind.String)
                throw DynamicExcelValidation(
                    "defaultDataType của Dynamic Excel phải là chuỗi.",
                    new { field = "specJson.defaultDataType", actualKind = defaultDataType.ValueKind.ToString() });
            var normalizedDefaultType = NormalizeDynamicExcelDataType(defaultDataType.GetString(), "specJson.defaultDataType");
            ValidateShortTextOptionsForDataType(
                spec,
                normalizedDefaultType,
                "specJson.defaultOptions",
                "defaultOptions");
        }

        if (!spec.TryGetProperty("dataTypeOverrides", out var overrides) || overrides.ValueKind == JsonValueKind.Null)
            return;

        if (overrides.ValueKind != JsonValueKind.Array)
            throw DynamicExcelValidation(
                "dataTypeOverrides của Dynamic Excel phải là JSON array.",
                new { field = "specJson.dataTypeOverrides", actualKind = overrides.ValueKind.ToString() });

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matrixRanges = new List<DynamicExcelRange>();
        var count = 0;
        foreach (var item in overrides.EnumerateArray())
        {
            count++;
            if (count > MaxDataCells)
                throw DynamicExcelValidation(
                    $"dataTypeOverrides tối đa {MaxDataCells} cấu hình.",
                    new { maxOverrides = MaxDataCells });

            if (item.ValueKind != JsonValueKind.Object)
                throw DynamicExcelValidation(
                    "dataTypeOverrides item phải là JSON object.",
                    new { index = count - 1, actualKind = item.ValueKind.ToString() });

            var scope = ReadJsonString(item, "scope")?.ToUpperInvariant();
            var dataType = NormalizeDynamicExcelDataType(ReadJsonString(item, "dataType"), $"specJson.dataTypeOverrides[{count - 1}].dataType");
            ValidateShortTextOptionsForDataType(
                item,
                dataType,
                $"specJson.dataTypeOverrides[{count - 1}].options",
                "options");
            var key = scope switch
            {
                "COLUMN" => ValidateColumnDataTypeOverride(item, kind, dataRect, count - 1),
                "ROW" => ValidateRowDataTypeOverride(item, kind, dataRect, count - 1),
                "RANGE" => ValidateRangeDataTypeOverride(item, kind, dataRect, count - 1),
                _ => throw DynamicExcelValidation(
                    "scope của cấu hình kiểu dữ liệu Dynamic Excel không hợp lệ.",
                    new { index = count - 1, scope, allowed = new[] { "COLUMN", "ROW", "RANGE" } })
            };

            if (!seen.Add(key))
                throw DynamicExcelValidation(
                    "Cấu hình kiểu dữ liệu Dynamic Excel bị trùng vùng.",
                    new { index = count - 1, scope, dataType, key });

            if (kind == "MATRIX" && scope == "RANGE")
                matrixRanges.Add(ReadDynamicExcelRange(item));
        }

        if (kind == "MATRIX")
            ValidateMatrixDataTypePartition(dataRect, matrixRanges);
    }

    private static void ValidateMatrixDataTypePartition(
        DynamicExcelDataRectDto dataRect,
        IReadOnlyCollection<DynamicExcelRange> ranges)
    {
        if (ranges.Count == 0)
            return;

        var height = dataRect.R1 - dataRect.R0 + 1;
        var width = dataRect.C1 - dataRect.C0 + 1;
        var covered = new bool[height, width];

        foreach (var range in ranges)
        {
            for (var r = range.R0; r <= range.R1; r++)
            {
                for (var c = range.C0; c <= range.C1; c++)
                {
                    var rr = r - dataRect.R0;
                    var cc = c - dataRect.C0;
                    if (covered[rr, cc])
                        throw DynamicExcelValidation(
                            "Vùng kiểu dữ liệu của ma trận không được overlap.",
                            new { range, dataRect, cell = new { r, c } });

                    covered[rr, cc] = true;
                }
            }
        }

        for (var r = 0; r < height; r++)
        {
            for (var c = 0; c < width; c++)
            {
                if (covered[r, c])
                    continue;

                throw DynamicExcelValidation(
                    "Vùng kiểu dữ liệu của ma trận phải phủ kín dataRect.",
                    new
                    {
                        dataRect,
                        missingCell = new { r = dataRect.R0 + r, c = dataRect.C0 + c }
                    });
            }
        }
    }

    private static DynamicExcelRange ReadDynamicExcelRange(JsonElement item)
        => new(
            ReadRequiredNonNegativeInt(item, "r0"),
            ReadRequiredNonNegativeInt(item, "c0"),
            ReadRequiredNonNegativeInt(item, "r1"),
            ReadRequiredNonNegativeInt(item, "c1"));

    private static string ValidateColumnDataTypeOverride(JsonElement item, string kind, DynamicExcelDataRectDto dataRect, int index)
    {
        if (kind != "TOP")
            throw DynamicExcelValidation(
                "Bảng ngang chỉ cấu hình kiểu dữ liệu theo cột; bảng dọc theo dòng; ma trận theo vùng.",
                new { index, kind, scope = "COLUMN" });

        var column = ReadRequiredNonNegativeInt(item, "index");
        if (column < dataRect.C0 || column > dataRect.C1)
            throw DynamicExcelValidation(
                "Cột cấu hình kiểu dữ liệu nằm ngoài vùng dữ liệu.",
                new { index, column, dataRect });

        return $"COLUMN:{column}";
    }

    private static string ValidateRowDataTypeOverride(JsonElement item, string kind, DynamicExcelDataRectDto dataRect, int index)
    {
        if (kind != "LEFT")
            throw DynamicExcelValidation(
                "Bảng ngang chỉ cấu hình kiểu dữ liệu theo cột; bảng dọc theo dòng; ma trận theo vùng.",
                new { index, kind, scope = "ROW" });

        var row = ReadRequiredNonNegativeInt(item, "index");
        if (row < dataRect.R0 || row > dataRect.R1)
            throw DynamicExcelValidation(
                "Dòng cấu hình kiểu dữ liệu nằm ngoài vùng dữ liệu.",
                new { index, row, dataRect });

        return $"ROW:{row}";
    }

    private static string ValidateRangeDataTypeOverride(JsonElement item, string kind, DynamicExcelDataRectDto dataRect, int index)
    {
        if (kind != "MATRIX")
            throw DynamicExcelValidation(
                "Bảng ngang chỉ cấu hình kiểu dữ liệu theo cột; bảng dọc theo dòng; ma trận theo vùng.",
                new { index, kind, scope = "RANGE" });

        var r0 = ReadRequiredNonNegativeInt(item, "r0");
        var c0 = ReadRequiredNonNegativeInt(item, "c0");
        var r1 = ReadRequiredNonNegativeInt(item, "r1");
        var c1 = ReadRequiredNonNegativeInt(item, "c1");
        if (r1 < r0 || c1 < c0)
            throw DynamicExcelValidation(
                "Vùng cấu hình kiểu dữ liệu không hợp lệ.",
                new { index, r0, c0, r1, c1 });

        if (r0 < dataRect.R0 || c0 < dataRect.C0 || r1 > dataRect.R1 || c1 > dataRect.C1)
            throw DynamicExcelValidation(
                "Vùng cấu hình kiểu dữ liệu phải nằm trong vùng dữ liệu của ma trận.",
                new { index, range = new { r0, c0, r1, c1 }, dataRect });

        return $"RANGE:{r0}:{c0}:{r1}:{c1}";
    }

    private static string NormalizeDynamicExcelDataType(string? raw, string field)
    {
        var normalized = raw?.Trim().ToUpperInvariant();
        normalized = normalized switch
        {
            "SHORTTEXT" or "SHORT_TEXT" or "TEXT" or "STRING" => LabelDataTypes.ShortText,
            "NUMBER" => LabelDataTypes.Number,
            "DATE" => LabelDataTypes.Date,
            "FULLDATE" or "FULL_DATE" or "STRICT_DATE" => "FULL_DATE",
            "BOOLEAN" => LabelDataTypes.Boolean,
            "LONGTEXT" or "LONG_TEXT" or "LONGDATE" => LabelDataTypes.LongText,
            "MULTISELECT" or "MULTI_SELECT" => "MULTI_SELECT",
            "STRINGLIST" or "STRING_LIST" => "__DYNAMIC_EXCEL_STRING_LIST_REMOVED__",
            _ => normalized
        };

        if (normalized == LabelDataTypes.LongText)
            throw DynamicExcelValidation(
                "Dynamic Excel không hỗ trợ LONG_TEXT; long text thuộc Dynamic Form fields.",
                new { field, dataType = raw });
        if (normalized == "__DYNAMIC_EXCEL_STRING_LIST_REMOVED__")
            throw DynamicExcelValidation(
                "Dynamic Excel không hỗ trợ STRING_LIST; dùng SHORT_TEXT hoặc MULTI_SELECT cho enum cố định.",
                new { field, dataType = raw });

        return normalized switch
        {
            LabelDataTypes.Number => LabelDataTypes.Number,
            LabelDataTypes.Date => LabelDataTypes.Date,
            "FULL_DATE" => "FULL_DATE",
            LabelDataTypes.Boolean => LabelDataTypes.Boolean,
            LabelDataTypes.ShortText => LabelDataTypes.ShortText,
            "MULTI_SELECT" => "MULTI_SELECT",
            _ => throw DynamicExcelValidation(
                "Kiểu dữ liệu Dynamic Excel không hợp lệ.",
                new
                {
                    field,
                    dataType = raw,
                    allowed = new[]
                    {
                        LabelDataTypes.Number,
                        LabelDataTypes.Date,
                        "FULL_DATE",
                        LabelDataTypes.Boolean,
                        LabelDataTypes.ShortText,
                        "MULTI_SELECT"
                    }
                })
        };
    }

    private static void ValidateShortTextOptionsForDataType(
        JsonElement owner,
        string dataType,
        string field,
        string propertyName)
    {
        if (!string.Equals(dataType, LabelDataTypes.ShortText, StringComparison.Ordinal) &&
            !string.Equals(dataType, "MULTI_SELECT", StringComparison.Ordinal))
            return;

        if (!owner.TryGetProperty(propertyName, out var options) || options.ValueKind != JsonValueKind.Array)
            throw DynamicExcelValidation(
                $"{dataType} phải cấu hình danh sách enum cố định.",
                new { field, dataType, propertyName });

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var item in options.EnumerateArray())
        {
            count++;
            if (count > 100)
                throw DynamicExcelValidation(
                    $"{dataType} chỉ hỗ trợ tối đa 100 lựa chọn.",
                    new { field, dataType, maxOptions = 100 });

            var code = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString()?.Trim(),
                JsonValueKind.Object => ReadJsonString(item, "code")
                                        ?? ReadJsonString(item, "value")
                                        ?? ReadJsonString(item, "id"),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(code))
                throw DynamicExcelValidation(
                    $"Mỗi lựa chọn {dataType} phải có mã không trống.",
                    new { field, dataType, index = count - 1 });

            if (!seen.Add(code))
                throw DynamicExcelValidation(
                    $"Mã lựa chọn {dataType} bị trùng.",
                    new { field, dataType, optionCode = code });
        }

        if (count == 0)
            throw DynamicExcelValidation(
                $"{dataType} phải có ít nhất một lựa chọn.",
                new { field, dataType });
    }

    private static int ReadRequiredPositiveInt(JsonElement element, string name)
    {
        var value = ReadJsonInt(element, name);
        if (!value.HasValue || value.Value <= 0)
            throw DynamicExcelValidation(
                $"Trường {name} của cấu hình Dynamic Excel phải là số nguyên dương.",
                new { field = $"specJson.{name}", value });
        return value.Value;
    }

    private static int ReadRequiredNonNegativeInt(JsonElement element, string name)
    {
        var value = ReadJsonInt(element, name);
        if (!value.HasValue || value.Value < 0)
            throw DynamicExcelValidation(
                $"Trường {name} của cấu hình kiểu dữ liệu Dynamic Excel phải là số nguyên không âm.",
                new { field = name, value });
        return value.Value;
    }

    private static JsonDocument ParseRequiredJson(string? json, string fieldName, JsonValueKind expectedKind)
    {
        var fieldLabel = DescribeDynamicExcelJsonField(fieldName);
        if (string.IsNullOrWhiteSpace(json))
            throw DynamicExcelValidation($"{fieldLabel} không được trống.", new { field = fieldName, fieldLabel });

        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != expectedKind)
            {
                var actualKind = document.RootElement.ValueKind.ToString();
                document.Dispose();
                throw DynamicExcelValidation(
                    $"{fieldLabel} phải là JSON dạng {DescribeJsonValueKind(expectedKind)}.",
                    new { field = fieldName, fieldLabel, expectedKind = expectedKind.ToString(), actualKind });
            }

            return document;
        }
        catch (JsonException ex)
        {
            throw DynamicExcelValidation($"{fieldLabel} không phải JSON hợp lệ.", new { field = fieldName, fieldLabel, ex.Message });
        }
    }

    private static string DescribeDynamicExcelJsonField(string fieldName)
        => fieldName switch
        {
            "rawWorkbookDataJson" => "Dữ liệu bảng tính của biểu mẫu Excel động",
            "specJson" => "Cấu hình loại bảng Excel động",
            _ => fieldName,
        };

    private static string DescribeJsonValueKind(JsonValueKind kind)
        => kind switch
        {
            JsonValueKind.Array => "danh sách",
            JsonValueKind.Object => "đối tượng",
            JsonValueKind.String => "chuỗi",
            JsonValueKind.Number => "số",
            JsonValueKind.True or JsonValueKind.False => "đúng/sai",
            JsonValueKind.Null => "rỗng",
            _ => kind.ToString(),
        };

    private static string DescribeDynamicExcelKind(string? kind)
        => kind?.Trim().ToUpperInvariant() switch
        {
            "TOP" => "Bảng ngang",
            "LEFT" => "Bảng dọc",
            "MATRIX" => "Bảng ma trận",
            null or "" => "Chưa xác định",
            _ => kind!,
        };

    private static string? ReadDynamicExcelHeaderKind(string? specJson)
    {
        if (string.IsNullOrWhiteSpace(specJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(specJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var kind = ReadJsonString(document.RootElement, "kind")?.ToUpperInvariant();
            return kind is "TOP" or "LEFT" or "MATRIX" ? kind : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int InferSheetRows(JsonElement sheet)
    {
        var fromRow = ReadJsonInt(sheet, "row") ?? 0;
        var fromData = sheet.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.GetArrayLength()
            : 0;
        var fromCelldata = InferCelldataBound(sheet, "r");
        return Math.Max(fromRow, Math.Max(fromData, fromCelldata));
    }

    private static int InferSheetCols(JsonElement sheet)
    {
        var fromColumn = ReadJsonInt(sheet, "column") ?? 0;
        var fromData = 0;
        if (sheet.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in data.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Array)
                    fromData = Math.Max(fromData, row.GetArrayLength());
            }
        }

        var fromCelldata = InferCelldataBound(sheet, "c");
        return Math.Max(fromColumn, Math.Max(fromData, fromCelldata));
    }

    private static int InferCelldataBound(JsonElement sheet, string propertyName)
    {
        if (!sheet.TryGetProperty("celldata", out var celldata) || celldata.ValueKind != JsonValueKind.Array)
            return 0;

        var max = 0;
        foreach (var item in celldata.EnumerateArray())
        {
            var index = ReadJsonInt(item, propertyName);
            if (index.HasValue && index.Value >= 0)
                max = Math.Max(max, index.Value + 1);
        }

        return max;
    }

    private static int? ReadJsonInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? ReadJsonString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private sealed record DynamicExcelRange(int R0, int C0, int R1, int C1);

    private static AppException DynamicExcelValidation(string message, object? details = null)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            details,
            message);

    private static AppException DynamicExcelIdRequired(string? dynamicExcelTemplateId)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.DYNAMIC_EXCEL_TEMPLATE_ID_REQUIRED,
            new { dynamicExcelTemplateId });

    private static AppException DynamicExcelNotFound(string? dynamicExcelTemplateId)
        => AppExceptionFactory.NotFound(
            AppErrorCode.DYNAMIC_EXCEL_TEMPLATE_NOT_FOUND,
            new { dynamicExcelTemplateId });

    private static AppException DynamicExcelInUse(AppErrorCode code, string dynamicExcelTemplateId)
        => AppExceptionFactory.Create(
            code,
            new { dynamicExcelTemplateId });

    private static object DynamicExcelDetails(DynamicExcelTemplate doc, string? actorUserId = null)
        => new
        {
            dynamicExcelTemplateId = doc.Id,
            doc.Code,
            doc.Name,
            doc.CreatedByUserId,
            actorUserId
        };

    private async Task<bool> IsUsedByDynamicFormAsync(string templateId, CancellationToken ct)
    {
        var escapedId = Regex.Escape(templateId);
        var fb = Builders<DynamicFormTemplate>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false)
                     & (fb.Eq(x => x.ExcelBlockDynamicExcelTemplateId, templateId)
                        | fb.Regex(x => x.ExcelBlockJson, new BsonRegularExpression(escapedId))
                        | fb.Regex(x => x.BlocksJson, new BsonRegularExpression(escapedId)));

        var candidates = await _ctx.DynamicFormTemplates
            .Find(filter)
            .Project(x => new DynamicFormBlockReadModel
            {
                ExcelBlockDynamicExcelTemplateId = x.ExcelBlockDynamicExcelTemplateId,
                ExcelBlockJson = x.ExcelBlockJson,
                BlocksJson = x.BlocksJson
            })
            .ToListAsync(ct);

        return candidates.Any(form =>
            string.Equals(form.ExcelBlockDynamicExcelTemplateId, templateId, StringComparison.Ordinal) ||
            JsonContainsDynamicExcelTemplateId(form.ExcelBlockJson, templateId) ||
            JsonContainsDynamicExcelTemplateId(form.BlocksJson, templateId));
    }

    private async Task EnsureCanReadAsync(MeResponse me, DynamicExcelTemplate doc, CancellationToken ct)
    {
        if (string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal))
            return;

        if (await HasDirectRuntimeReadGrantAsync(doc.Id, me.Id, ct))
            return;

        if (await HasDynamicFormBlockRuntimeReadGrantAsync(doc.Id, me.Id, ct))
            return;

        throw AppExceptionFactory.Forbidden(
            AppErrorCode.DYNAMIC_EXCEL_READ_FORBIDDEN,
            DynamicExcelDetails(doc, me.Id));
    }

    private async Task<bool> HasDirectRuntimeReadGrantAsync(
        string templateId,
        string userId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(templateId) || string.IsNullOrWhiteSpace(userId))
            return false;

        if (await _ctx.WorkTemplateAssignees
                .Find(x =>
                    x.DynamicExcelId == templateId &&
                    x.AssigneeUserId == userId &&
                    !x.IsDeleted)
                .Limit(1)
                .AnyAsync(ct))
            return true;

        if (await _ctx.MyReportPeriodListDocRoles
                .Find(x =>
                    x.DynamicExcelId == templateId &&
                    x.UserId == userId &&
                    !x.IsDeleted)
                .Limit(1)
                .AnyAsync(ct))
            return true;

        if (await _ctx.WorkAssignments
                .Find(x =>
                    x.DynamicExcelId == templateId &&
                    x.CreatedByUserId == userId &&
                    !x.IsDeleted)
                .Limit(1)
                .AnyAsync(ct))
            return true;

        return await _ctx.ReviewReportListDocRoles
            .Find(x =>
                x.DynamicExcelId == templateId &&
                x.ReviewerUserId == userId &&
                !x.IsDeleted)
            .Limit(1)
            .AnyAsync(ct);
    }

    private async Task<bool> HasDynamicFormBlockRuntimeReadGrantAsync(
        string templateId,
        string userId,
        CancellationToken ct)
    {
        var formIds = await LoadRuntimeReadableDynamicFormIdsAsync(userId, ct);
        if (formIds.Count == 0)
            return false;

        var forms = await _ctx.DynamicFormTemplates
            .Find(x => formIds.Contains(x.Id) && !x.IsDeleted)
            .Project(x => new DynamicFormBlockReadModel
            {
                ExcelBlockDynamicExcelTemplateId = x.ExcelBlockDynamicExcelTemplateId,
                ExcelBlockJson = x.ExcelBlockJson,
                BlocksJson = x.BlocksJson
            })
            .ToListAsync(ct);

        return forms.Any(form =>
            string.Equals(form.ExcelBlockDynamicExcelTemplateId, templateId, StringComparison.Ordinal) ||
            JsonContainsDynamicExcelTemplateId(form.ExcelBlockJson, templateId) ||
            JsonContainsDynamicExcelTemplateId(form.BlocksJson, templateId));
    }

    private async Task<HashSet<string>> LoadRuntimeReadableDynamicFormIdsAsync(
        string userId,
        CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        void Add(IEnumerable<string?> ids)
        {
            foreach (var id in ids)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    result.Add(id.Trim());
            }
        }

        Add(await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.AssigneeUserId == userId &&
                x.DynamicFormTemplateId != null &&
                x.DynamicFormTemplateId != string.Empty &&
                !x.IsDeleted)
            .Project(x => x.DynamicFormTemplateId)
            .ToListAsync(ct));

        Add(await _ctx.MyReportPeriodListDocRoles
            .Find(x =>
                x.UserId == userId &&
                x.DynamicFormTemplateId != null &&
                x.DynamicFormTemplateId != string.Empty &&
                !x.IsDeleted)
            .Project(x => x.DynamicFormTemplateId)
            .ToListAsync(ct));

        Add(await _ctx.WorkAssignments
            .Find(x =>
                x.CreatedByUserId == userId &&
                x.DynamicFormTemplateId != null &&
                x.DynamicFormTemplateId != string.Empty &&
                !x.IsDeleted)
            .Project(x => x.DynamicFormTemplateId)
            .ToListAsync(ct));

        Add(await _ctx.ReviewReportListDocRoles
            .Find(x =>
                x.ReviewerUserId == userId &&
                x.DynamicFormTemplateId != null &&
                x.DynamicFormTemplateId != string.Empty &&
                !x.IsDeleted)
            .Project(x => x.DynamicFormTemplateId)
            .ToListAsync(ct));

        return result;
    }

    private static bool JsonContainsDynamicExcelTemplateId(string? json, string templateId)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(templateId))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonContainsDynamicExcelTemplateId(document.RootElement, templateId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool JsonContainsDynamicExcelTemplateId(JsonElement element, string templateId)
    {
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(item => JsonContainsDynamicExcelTemplateId(item, templateId));

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (ReadTemplateId(element, "dynamicExcelTemplateId", templateId) ||
            ReadTemplateId(element, "DynamicExcelTemplateId", templateId))
        {
            return true;
        }

        return element.EnumerateObject().Any(property =>
            (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) &&
            JsonContainsDynamicExcelTemplateId(property.Value, templateId));
    }

    private static bool ReadTemplateId(JsonElement element, string propertyName, string templateId)
        => element.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.String &&
           string.Equals(value.GetString()?.Trim(), templateId, StringComparison.Ordinal);

    private sealed class DynamicFormBlockReadModel
    {
        public string? ExcelBlockDynamicExcelTemplateId { get; init; }
        public string? ExcelBlockJson { get; init; }
        public string? BlocksJson { get; init; }
    }

}
