using MongoDB.Bson;
using MongoDB.Driver;
using System.Text;
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
    private const int MaxDataCells = DynamicExcelRuntimePolicy.MaxDirectTableAggregateInputCells;
    private const int MaxHeaderRows = 30;
    private const int MaxHeaderCols = 30;
    private const int MaxSheetCells = 20000;
    private const int MaxJsonScanDepth = 80;

    private static readonly Regex DangerousTemplateTextRegex = new(
        @"<\s*(script|iframe|object|embed|svg|img|style|link|meta)\b|javascript\s*:|vbscript\s*:|data\s*:\s*text/html|on[a-z]+\s*=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly ILabelEnumCatalogService _enumCatalogs;

    public DynamicExcelService(
        MongoDbContext ctx,
        MeAccessor me,
        ILabelEnumCatalogService enumCatalogs)
    {
        _ctx = ctx;
        _me = me;
        _enumCatalogs = enumCatalogs;
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
        await EnsureNotLinkedToRuntimeAsync(templateId, ct);

        if (await IsUsedByDynamicFormAsync(templateId, ct))
            throw DynamicExcelInUse(AppErrorCode.DYNAMIC_EXCEL_IN_USE_BY_DYNAMIC_FORM, templateId);
    }

    private async Task EnsureNotLinkedToRuntimeAsync(string templateId, CancellationToken ct)
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
        await _enumCatalogs.ValidateVisibleActiveCatalogsAsync(ExtractEnumCatalogIdsFromDynamicExcelSpec(req.SpecJson), ct);

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

        try
        {
            await _ctx.DynamicExcelTemplates.InsertOneAsync(doc, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw DynamicExcelCodeDuplicate(code!);
        }
        catch (MongoBulkWriteException<DynamicExcelTemplate> ex) when (
            ex.WriteErrors.Any(error => error.Category == ServerErrorCategory.DuplicateKey))
        {
            throw DynamicExcelCodeDuplicate(code!);
        }

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

        var updatesWorkbook =
            !string.IsNullOrWhiteSpace(req.TableMode) ||
            req.ContractVersion.HasValue ||
            !string.IsNullOrWhiteSpace(req.RawWorkbookDataJson) ||
            !string.IsNullOrWhiteSpace(req.SpecJson) ||
            req.DataRect is not null ||
            req.W.HasValue ||
            req.H.HasValue;

        if (updatesWorkbook)
        {
            await EnsureNotLinkedToRuntimeAsync(id, ct);
            var payload = new CreateDynamicExcelReq(
                doc.Code,
                req.Name,
                string.IsNullOrWhiteSpace(req.TableMode) ? doc.TableMode : req.TableMode,
                req.ContractVersion ?? doc.ContractVersion,
                string.IsNullOrWhiteSpace(req.RawWorkbookDataJson) ? doc.RawWorkbookDataJson : req.RawWorkbookDataJson,
                string.IsNullOrWhiteSpace(req.SpecJson) ? doc.SpecJson : req.SpecJson,
                req.DataRect ?? new DynamicExcelDataRectDto(doc.DataRectR0, doc.DataRectC0, doc.DataRectR1, doc.DataRectC1),
                req.W ?? doc.W,
                req.H ?? doc.H);
            var tableMode = ValidateDynamicExcelPayload(payload);
            ValidateDynamicExcelSemanticUpdateContract(doc, payload);
            await _enumCatalogs.ValidateVisibleActiveCatalogsAsync(ExtractEnumCatalogIdsFromDynamicExcelSpec(payload.SpecJson), ct);

            update = update
                .Set(x => x.TableMode, tableMode)
                .Set(x => x.ContractVersion, Math.Max(1, payload.ContractVersion ?? 1))
                .Set(x => x.RawWorkbookDataJson, payload.RawWorkbookDataJson.Trim())
                .Set(x => x.SpecJson, payload.SpecJson.Trim())
                .Set(x => x.DataRectR0, payload.DataRect?.R0 ?? 0)
                .Set(x => x.DataRectC0, payload.DataRect?.C0 ?? 0)
                .Set(x => x.DataRectR1, payload.DataRect?.R1 ?? 0)
                .Set(x => x.DataRectC1, payload.DataRect?.C1 ?? 0)
                .Set(x => x.W, payload.W)
                .Set(x => x.H, payload.H);
        }

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
        if (w <= 0 || h <= 0 || w > MaxSheetCells || h > MaxSheetCells)
            throw DynamicExcelValidation(
                $"Kích thước vùng nhập dữ liệu của biểu mẫu Excel động phải nằm trong 1..{MaxSheetCells}.",
                new { w, h, maxDimension = MaxSheetCells });

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
        ValidateNoDangerousTemplateStrings(workbookDocument.RootElement, "rawWorkbookDataJson");
        ValidateNoDangerousTemplateStrings(specDocument.RootElement, "specJson");

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
        var specialRanges = ValidateDynamicExcelSpecialRanges(specDocument.RootElement, dataRect);

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

        ValidateDynamicExcelWorkbookInputCellsAreEmpty(workbookDocument.RootElement, dataRect, specialRanges);

        return tableMode;
    }

    private static void ValidateDynamicExcelSemanticUpdateContract(
        DynamicExcelTemplate current,
        CreateDynamicExcelReq next)
    {
        var currentDataRect = new DynamicExcelDataRectDto(
            current.DataRectR0,
            current.DataRectC0,
            current.DataRectR1,
            current.DataRectC1);
        var nextDataRect = next.DataRect
            ?? throw DynamicExcelValidation("Thiếu vùng nhập dữ liệu của biểu mẫu Excel động.", new { field = "dataRect" });

        if (!DynamicExcelDataRectEquals(currentDataRect, nextDataRect) ||
            current.W != next.W ||
            current.H != next.H)
        {
            throw DynamicExcelValidation(
                "Không được sửa cấu trúc bảng Excel động ở màn hình cập nhật. Chỉ được cập nhật header, tiêu đề và công thức.",
                new
                {
                    locked = new { dataRect = currentDataRect, current.W, current.H },
                    requested = new { dataRect = nextDataRect, next.W, next.H }
                });
        }

        var nextTableMode = NormalizeDynamicExcelTableMode(next.TableMode);
        if (!string.Equals(current.TableMode, nextTableMode, StringComparison.Ordinal))
        {
            throw DynamicExcelValidation(
                "Không được sửa kiểu nhập/tổng hợp bảng Excel động ở màn hình cập nhật.",
                new { current.TableMode, requestedTableMode = nextTableMode });
        }

        var currentContractVersion = Math.Max(1, current.ContractVersion);
        var nextContractVersion = Math.Max(1, next.ContractVersion ?? 1);
        if (currentContractVersion != nextContractVersion)
        {
            throw DynamicExcelValidation(
                "Không được sửa phiên bản contract của bảng Excel động ở màn hình cập nhật.",
                new { currentContractVersion, nextContractVersion });
        }

        using var currentSpecDocument = ParseRequiredJson(current.SpecJson, "specJson", JsonValueKind.Object);
        using var nextSpecDocument = ParseRequiredJson(next.SpecJson, "specJson", JsonValueKind.Object);
        ValidateNoUnexpectedDynamicExcelSpecProperties(nextSpecDocument.RootElement);

        var currentLockedSpec = CanonicalizeLockedDynamicExcelSpec(currentSpecDocument.RootElement);
        var nextLockedSpec = CanonicalizeLockedDynamicExcelSpec(nextSpecDocument.RootElement);
        if (!string.Equals(currentLockedSpec, nextLockedSpec, StringComparison.Ordinal))
        {
            throw DynamicExcelValidation(
                "Không được sửa loại bảng, kích thước, vùng dữ liệu hoặc kiểu dữ liệu. Chỉ được cập nhật header, tiêu đề và công thức.",
                new { field = "specJson" });
        }

        var currentLockedSpecialRanges = CanonicalizeLockedSpecialRanges(currentSpecDocument.RootElement, currentDataRect);
        var nextLockedSpecialRanges = CanonicalizeLockedSpecialRanges(nextSpecDocument.RootElement, nextDataRect);
        if (!string.Equals(currentLockedSpecialRanges, nextLockedSpecialRanges, StringComparison.Ordinal))
        {
            throw DynamicExcelValidation(
                "Không được sửa các vùng đặc biệt đang khóa. Chỉ được thêm, sửa hoặc xóa vùng tiêu đề/công thức.",
                new { field = "specJson.specialRanges" });
        }
    }

    private static bool DynamicExcelDataRectEquals(DynamicExcelDataRectDto a, DynamicExcelDataRectDto b)
        => a.R0 == b.R0 && a.C0 == b.C0 && a.R1 == b.R1 && a.C1 == b.C1;

    private static string CanonicalizeLockedSpecialRanges(JsonElement spec, DynamicExcelDataRectDto dataRect)
    {
        var ranges = ValidateDynamicExcelSpecialRanges(spec, dataRect)
            .Where(range => !IsEditableSemanticSpecialRangeRole(range.Role))
            .OrderBy(range => range.R0)
            .ThenBy(range => range.C0)
            .ThenBy(range => range.R1)
            .ThenBy(range => range.C1)
            .ThenBy(range => range.Role, StringComparer.Ordinal)
            .Select(range => new { range.Role, range.R0, range.C0, range.R1, range.C1 })
            .ToList();

        return JsonSerializer.Serialize(ranges);
    }

    private static bool IsEditableSemanticSpecialRangeRole(string? role)
        => string.Equals(role, "FORMULA", StringComparison.Ordinal) ||
           string.Equals(role, "TITLE", StringComparison.Ordinal);

    private static void ValidateNoUnexpectedDynamicExcelSpecProperties(JsonElement spec)
    {
        var kind = ReadJsonString(spec, "kind")?.ToUpperInvariant();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "kind",
            "defaultDataType",
            "defaultOptions",
            "dataTypeOverrides",
            "specialRanges"
        };

        foreach (var propertyName in kind switch
        {
            "TOP" => new[] { "topRows", "topCols", "dataRows" },
            "LEFT" => new[] { "leftRows", "leftCols", "dataCols" },
            "MATRIX" => new[] { "topRows", "topCols", "leftRows", "leftCols" },
            _ => Array.Empty<string>()
        })
        {
            allowed.Add(propertyName);
        }

        var unexpected = spec.EnumerateObject()
            .Select(property => property.Name)
            .Where(propertyName => !allowed.Contains(propertyName))
            .ToList();

        if (unexpected.Count == 0)
            return;

        throw DynamicExcelValidation(
            "specJson của Dynamic Excel có trường không thuộc contract cập nhật an toàn.",
            new { field = "specJson", unexpected });
    }

    private static string CanonicalizeLockedDynamicExcelSpec(JsonElement spec)
    {
        var kind = ReadJsonString(spec, "kind")?.ToUpperInvariant();
        var locked = new SortedDictionary<string, string?>(StringComparer.Ordinal)
        {
            ["kind"] = kind,
            ["defaultDataType"] = spec.TryGetProperty("defaultDataType", out var defaultDataType)
                ? NormalizeDynamicExcelDataType(ReadJsonString(spec, "defaultDataType"), "specJson.defaultDataType")
                : LabelDataTypes.Number,
            ["defaultOptions"] = spec.TryGetProperty("defaultOptions", out var defaultOptions) && defaultOptions.ValueKind != JsonValueKind.Null
                ? CanonicalizeJson(defaultOptions)
                : "[]",
            ["dataTypeOverrides"] = CanonicalizeDynamicExcelDataTypeOverrides(spec),
        };

        switch (kind)
        {
            case "TOP":
                locked["topRows"] = ReadRequiredPositiveInt(spec, "topRows").ToString();
                locked["topCols"] = ReadRequiredPositiveInt(spec, "topCols").ToString();
                locked["dataRows"] = ReadRequiredPositiveInt(spec, "dataRows").ToString();
                break;
            case "LEFT":
                locked["leftRows"] = ReadRequiredPositiveInt(spec, "leftRows").ToString();
                locked["leftCols"] = ReadRequiredPositiveInt(spec, "leftCols").ToString();
                locked["dataCols"] = ReadRequiredPositiveInt(spec, "dataCols").ToString();
                break;
            case "MATRIX":
                locked["topRows"] = ReadRequiredPositiveInt(spec, "topRows").ToString();
                locked["topCols"] = ReadRequiredPositiveInt(spec, "topCols").ToString();
                locked["leftRows"] = ReadRequiredPositiveInt(spec, "leftRows").ToString();
                locked["leftCols"] = ReadRequiredPositiveInt(spec, "leftCols").ToString();
                break;
        }

        return JsonSerializer.Serialize(locked);
    }

    private static string CanonicalizeDynamicExcelDataTypeOverrides(JsonElement spec)
    {
        if (!spec.TryGetProperty("dataTypeOverrides", out var overrides) ||
            overrides.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "[]";
        }

        if (overrides.ValueKind != JsonValueKind.Array)
            return CanonicalizeJson(overrides);

        var items = overrides
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(CanonicalizeDynamicExcelDataTypeOverride)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();

        return JsonSerializer.Serialize(items);
    }

    private static string CanonicalizeDynamicExcelDataTypeOverride(JsonElement item)
    {
        var scope = ReadJsonString(item, "scope")?.ToUpperInvariant();
        var dataType = NormalizeDynamicExcelDataType(ReadJsonString(item, "dataType"), "specJson.dataTypeOverrides[].dataType");
        var locked = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["scope"] = scope,
            ["dataType"] = dataType,
        };

        if (scope == "COLUMN" || scope == "ROW")
            locked["index"] = ReadRequiredNonNegativeInt(item, "index");
        else if (scope == "RANGE")
        {
            locked["r0"] = ReadRequiredNonNegativeInt(item, "r0");
            locked["c0"] = ReadRequiredNonNegativeInt(item, "c0");
            locked["r1"] = ReadRequiredNonNegativeInt(item, "r1");
            locked["c1"] = ReadRequiredNonNegativeInt(item, "c1");
        }

        if (item.TryGetProperty("options", out var options) &&
            options.ValueKind == JsonValueKind.Array &&
            options.GetArrayLength() > 0)
        {
            locked["options"] = CanonicalizeJson(options);
        }

        if (item.TryGetProperty("valueSource", out var valueSource) &&
            valueSource.ValueKind == JsonValueKind.Object)
        {
            locked["valueSource"] = CanonicalizeJson(valueSource);
        }

        return JsonSerializer.Serialize(locked);
    }

    private static string CanonicalizeJson(JsonElement element, Func<string, bool>? skipProperty = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalJson(writer, element, skipProperty);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalJson(
        Utf8JsonWriter writer,
        JsonElement element,
        Func<string, bool>? skipProperty)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (skipProperty?.Invoke(property.Name) == true)
                        continue;

                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value, skipProperty);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonicalJson(writer, item, skipProperty);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
        }
    }

    private static void ValidateNoDangerousTemplateStrings(JsonElement element, string fieldName)
        => ValidateNoDangerousTemplateStrings(element, fieldName, "$", 0);

    private static void ValidateNoDangerousTemplateStrings(
        JsonElement element,
        string fieldName,
        string path,
        int depth)
    {
        if (depth > MaxJsonScanDepth)
            throw DynamicExcelValidation(
                "JSON template Excel động quá sâu, không an toàn để xử lý.",
                new { field = fieldName, path, maxDepth = MaxJsonScanDepth });

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (!string.IsNullOrEmpty(text) && DangerousTemplateTextRegex.IsMatch(text))
                    throw DynamicExcelValidation(
                        "Nội dung template Excel động có chuỗi không an toàn.",
                        new { field = fieldName, path });
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name is "__proto__" or "constructor" or "prototype" ||
                        DangerousTemplateTextRegex.IsMatch(property.Name))
                    {
                        throw DynamicExcelValidation(
                            "Tên thuộc tính JSON trong template Excel động không an toàn.",
                            new { field = fieldName, path = $"{path}.{property.Name}" });
                    }

                    ValidateNoDangerousTemplateStrings(property.Value, fieldName, $"{path}.{property.Name}", depth + 1);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    ValidateNoDangerousTemplateStrings(item, fieldName, $"{path}[{index}]", depth + 1);
                    index++;
                }
                break;
        }
    }

    private static void ValidateDynamicExcelWorkbookInputCellsAreEmpty(
        JsonElement workbook,
        DynamicExcelDataRectDto dataRect,
        IReadOnlyCollection<DynamicExcelSpecialRange> specialRanges)
    {
        if (workbook.ValueKind != JsonValueKind.Array || workbook.GetArrayLength() == 0)
            return;

        var firstSheet = workbook[0];
        if (firstSheet.ValueKind != JsonValueKind.Object)
            return;

        var specialMask = BuildDynamicExcelSpecialCellMask(dataRect, specialRanges);

        if (firstSheet.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            var rowIndex = 0;
            foreach (var row in data.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Array)
                {
                    var colIndex = 0;
                    foreach (var cell in row.EnumerateArray())
                    {
                        ValidateDynamicExcelTemplateInputCell(cell, rowIndex, colIndex, dataRect, specialMask, "data");
                        colIndex++;
                    }
                }

                rowIndex++;
            }
        }

        if (!firstSheet.TryGetProperty("celldata", out var celldata) || celldata.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in celldata.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var r = ReadJsonInt(item, "r");
            var c = ReadJsonInt(item, "c");
            if (!r.HasValue || !c.HasValue)
                continue;

            var cell = item.TryGetProperty("v", out var value) ? value : item;
            ValidateDynamicExcelTemplateInputCell(cell, r.Value, c.Value, dataRect, specialMask, "celldata");
        }
    }

    private static void ValidateDynamicExcelTemplateInputCell(
        JsonElement cell,
        int r,
        int c,
        DynamicExcelDataRectDto dataRect,
        DynamicExcelSpecialCellMask? specialMask,
        string source)
    {
        if (!IsDynamicExcelInputCell(dataRect, specialMask, r, c))
            return;

        if (!HasMeaningfulDynamicExcelTemplateCellValue(cell))
            return;

        throw DynamicExcelValidation(
            "Template Excel động không được nhập sẵn dữ liệu trong vùng reporter nhập.",
            new { source, cell = new { r, c }, dataRect });
    }

    private static bool IsDynamicExcelInputCell(
        DynamicExcelDataRectDto dataRect,
        DynamicExcelSpecialCellMask? specialMask,
        int r,
        int c)
    {
        if (r < dataRect.R0 || r > dataRect.R1 || c < dataRect.C0 || c > dataRect.C1)
            return false;

        return !IsDynamicExcelMaskedSpecialCell(specialMask, r, c);
    }

    private static bool HasMeaningfulDynamicExcelTemplateCellValue(JsonElement cell)
    {
        return cell.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrWhiteSpace(cell.GetString()),
            JsonValueKind.Number => true,
            JsonValueKind.True or JsonValueKind.False => true,
            JsonValueKind.Object => HasMeaningfulDynamicExcelTemplateCellObject(cell),
            _ => false
        };
    }

    private static bool HasMeaningfulDynamicExcelTemplateCellObject(JsonElement cell)
    {
        foreach (var propertyName in new[] { "v", "m", "f" })
        {
            if (!cell.TryGetProperty(propertyName, out var value))
                continue;

            if (HasMeaningfulDynamicExcelTemplateCellValue(value))
                return true;
        }

        return false;
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

        var specialRanges = ValidateDynamicExcelSpecialRanges(spec, expectedDataRect);
        ValidateNumericGridLimits(spec, kind, expectedDataRect, specialRanges);
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
        DynamicExcelDataRectDto dataRect,
        IReadOnlyCollection<DynamicExcelSpecialRange> specialRanges)
    {
        var dataRows = dataRect.R1 - dataRect.R0 + 1;
        var dataCols = dataRect.C1 - dataRect.C0 + 1;
        var dataCells = CountDynamicExcelInputCells(dataRect, specialRanges);
        if (dataCells > MaxDataCells)
            throw DynamicExcelValidation(
                $"Vùng dữ liệu có {dataCells} ô nhập trong dataRect {dataCols}x{dataRows}. Giới hạn values1D: {MaxDataCells} ô.",
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

    private static List<DynamicExcelSpecialRange> ValidateDynamicExcelSpecialRanges(
        JsonElement spec,
        DynamicExcelDataRectDto dataRect)
    {
        if (!spec.TryGetProperty("specialRanges", out var ranges) || ranges.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new List<DynamicExcelSpecialRange>();

        if (ranges.ValueKind != JsonValueKind.Array)
            throw DynamicExcelValidation(
                "specialRanges của Dynamic Excel phải là JSON array.",
                new { field = "specJson.specialRanges", actualKind = ranges.ValueKind.ToString() });

        var rows = new List<DynamicExcelSpecialRange>();
        var index = 0;
        foreach (var item in ranges.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw DynamicExcelValidation(
                    "specialRanges item phải là JSON object.",
                    new { index, actualKind = item.ValueKind.ToString() });

            var role = NormalizeDynamicExcelSpecialRole(ReadJsonString(item, "role") ?? ReadJsonString(item, "kind") ?? ReadJsonString(item, "type"));
            if (string.IsNullOrWhiteSpace(role))
                throw DynamicExcelValidation(
                    "specialRanges.role không hợp lệ.",
                    new { index, allowed = new[] { "FORMULA", "TITLE", "BLANK", "HEADER", "STYLE" } });

            var range = new DynamicExcelSpecialRange(
                ReadRequiredNonNegativeInt(item, "r0"),
                ReadRequiredNonNegativeInt(item, "c0"),
                ReadRequiredNonNegativeInt(item, "r1"),
                ReadRequiredNonNegativeInt(item, "c1"),
                role);

            if (range.R1 < range.R0 || range.C1 < range.C0)
                throw DynamicExcelValidation(
                    "specialRanges có tọa độ không hợp lệ.",
                    new { index, range });

            if (range.R0 < dataRect.R0 || range.C0 < dataRect.C0 || range.R1 > dataRect.R1 || range.C1 > dataRect.C1)
                throw DynamicExcelValidation(
                    "specialRanges phải nằm trong vùng dữ liệu.",
                    new { index, range, dataRect });

            rows.Add(range);
            index++;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            for (var j = i + 1; j < rows.Count; j++)
            {
                if (!DynamicExcelRangesOverlap(rows[i], rows[j]))
                    continue;

                throw DynamicExcelValidation(
                    "specialRanges không được overlap.",
                    new { first = i, second = j, ranges = new[] { rows[i], rows[j] } });
            }
        }

        if (rows.Count > 0 && CountDynamicExcelInputCells(dataRect, rows) == 0)
            throw DynamicExcelValidation(
                "Vùng dữ liệu phải còn ít nhất một ô nhập sau khi loại specialRanges.",
                new { dataRect });

        return rows;
    }

    private static int CountDynamicExcelInputCells(
        DynamicExcelDataRectDto dataRect,
        IReadOnlyCollection<DynamicExcelSpecialRange> specialRanges)
    {
        if (dataRect.R1 < dataRect.R0 || dataRect.C1 < dataRect.C0)
            return 0;

        var width = dataRect.C1 - dataRect.C0 + 1;
        var height = dataRect.R1 - dataRect.R0 + 1;
        var specialMask = BuildDynamicExcelSpecialCellMask(dataRect, specialRanges);
        return width * height - (specialMask?.MaskedCount ?? 0);
    }

    private static DynamicExcelSpecialCellMask? BuildDynamicExcelSpecialCellMask(
        DynamicExcelDataRectDto dataRect,
        IReadOnlyCollection<DynamicExcelSpecialRange> specialRanges)
    {
        if (specialRanges.Count == 0)
            return null;

        var width = dataRect.C1 - dataRect.C0 + 1;
        var height = dataRect.R1 - dataRect.R0 + 1;
        if (width <= 0 || height <= 0)
            return null;

        var flags = new bool[width * height];
        var masked = 0;
        foreach (var range in specialRanges)
        {
            var r0 = Math.Max(dataRect.R0, range.R0);
            var c0 = Math.Max(dataRect.C0, range.C0);
            var r1 = Math.Min(dataRect.R1, range.R1);
            var c1 = Math.Min(dataRect.C1, range.C1);
            if (r1 < r0 || c1 < c0)
                continue;

            for (var r = r0; r <= r1; r++)
            {
                var offset = (r - dataRect.R0) * width + (c0 - dataRect.C0);
                for (var c = c0; c <= c1; c++)
                {
                    if (!flags[offset])
                    {
                        flags[offset] = true;
                        masked++;
                    }
                    offset++;
                }
            }
        }

        return new DynamicExcelSpecialCellMask(dataRect, width, flags, masked);
    }

    private static bool IsDynamicExcelMaskedSpecialCell(DynamicExcelSpecialCellMask? mask, int r, int c)
    {
        if (mask is null)
            return false;
        if (r < mask.DataRect.R0 || r > mask.DataRect.R1 || c < mask.DataRect.C0 || c > mask.DataRect.C1)
            return false;

        return mask.Flags[(r - mask.DataRect.R0) * mask.Width + (c - mask.DataRect.C0)];
    }

    private static string? NormalizeDynamicExcelSpecialRole(string? value)
    {
        var role = value?.Trim().ToUpperInvariant();
        if (role == "FORMULAR")
            role = "FORMULA";
        if (role == "HEADER")
            role = "TITLE";
        if (role is "STYLE" or "EMPTY" or "EMPTY_INPUT")
            role = "BLANK";
        return role is "FORMULA" or "TITLE" or "BLANK" ? role : null;
    }

    private static bool DynamicExcelRangesOverlap(DynamicExcelSpecialRange a, DynamicExcelSpecialRange b)
        => a.R0 <= b.R1 && a.R1 >= b.R0 && a.C0 <= b.C1 && a.C1 >= b.C0;

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
            "IGNORE" or "IGNORED" or "SKIP" => "IGNORE",
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
            "IGNORE" => "IGNORE",
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
                        "MULTI_SELECT",
                        "IGNORE"
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

        if (TryGetDynamicExcelValueSource(owner, out var valueSource))
        {
            if (valueSource.SourceType == LabelValueSourceTypes.EnumCatalog)
            {
                if (!string.IsNullOrWhiteSpace(valueSource.CatalogId))
                    return;

                throw DynamicExcelValidation(
                    "Nguồn ENUM_CATALOG phải chọn danh mục enum.",
                    new { field, dataType, sourceType = valueSource.SourceType });
            }

            if (LabelValueSourceTypes.UsesCatalog(valueSource.SourceType))
                return;

            if (valueSource.SourceType == LabelValueSourceTypes.FixedEnum &&
                valueSource.Options.HasValue &&
                valueSource.Options.Value.ValueKind == JsonValueKind.Array)
            {
                ValidateDynamicExcelOptionArray(
                    valueSource.Options.Value,
                    dataType,
                    $"{field}.valueSource.options");
                return;
            }
        }

        if (!owner.TryGetProperty(propertyName, out var options) || options.ValueKind != JsonValueKind.Array)
            throw DynamicExcelValidation(
                $"{dataType} phải cấu hình danh sách enum cố định.",
                new { field, dataType, propertyName });

        ValidateDynamicExcelOptionArray(options, dataType, field);
    }

    private static void ValidateDynamicExcelOptionArray(
        JsonElement options,
        string dataType,
        string field)
    {
        if (options.ValueKind != JsonValueKind.Array)
            throw DynamicExcelValidation(
                $"{dataType} phải cấu hình danh sách enum cố định.",
                new { field, dataType, actualKind = options.ValueKind.ToString() });

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

    private static bool TryGetDynamicExcelValueSource(JsonElement owner, out DynamicExcelValueSourceSpec valueSource)
    {
        valueSource = default;
        if (!owner.TryGetProperty("valueSource", out var source) || source.ValueKind != JsonValueKind.Object)
            return false;

        var sourceType = ReadJsonString(source, "sourceType")
                         ?? ReadJsonString(source, "type")
                         ?? ReadJsonString(source, "valueSourceType");
        var normalized = LabelValueSourceTypes.Normalize(sourceType);
        if (normalized == LabelValueSourceTypes.None)
            return false;

        var catalogId = ReadJsonString(source, "catalogId")
                        ?? ReadJsonString(source, "valueSourceCatalogId")
                        ?? ReadJsonString(source, "enumCatalogId");
        JsonElement? options = null;
        if (source.TryGetProperty("options", out var sourceOptions))
            options = sourceOptions;

        valueSource = new DynamicExcelValueSourceSpec(normalized, catalogId, options);
        return true;
    }

    private readonly record struct DynamicExcelValueSourceSpec(
        string SourceType,
        string? CatalogId,
        JsonElement? Options);

    private static IReadOnlyList<string> ExtractEnumCatalogIdsFromDynamicExcelSpec(string? specJson)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(specJson))
            return Array.Empty<string>();

        try
        {
            using var document = JsonDocument.Parse(specJson);
            CollectEnumCatalogIds(document.RootElement, result);
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }

        return result.ToList();
    }

    private static void CollectEnumCatalogIds(JsonElement element, ISet<string> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetDynamicExcelValueSource(element, out var source) &&
                source.SourceType == LabelValueSourceTypes.EnumCatalog &&
                !string.IsNullOrWhiteSpace(source.CatalogId))
            {
                result.Add(source.CatalogId.Trim());
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
    private sealed record DynamicExcelSpecialRange(int R0, int C0, int R1, int C1, string Role);
    private sealed record DynamicExcelSpecialCellMask(
        DynamicExcelDataRectDto DataRect,
        int Width,
        bool[] Flags,
        int MaskedCount);

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

    private static AppException DynamicExcelCodeDuplicate(string code)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_EXCEL_CODE_DUPLICATE,
            new { code });

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
