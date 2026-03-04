using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
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
    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;

    public DynamicExcelService(MongoDbContext ctx, MeAccessor me)
    {
        _ctx = ctx;
        _me = me;
    }

    private void RequireCanMutate(MeResponse me, DynamicExcelTemplate doc)
    {
        // ✅ KHUNG: chỉnh theo RoleGuard hiện tại 
        // - ADMIN/SYS full
        // - còn lại: chỉ người tạo được sửa/xóa
        if (!string.Equals(doc.CreatedByUserId, me.Id, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Bạn không có quyền sửa/xóa bảng biểu này.");
    }

    private Task EnsureNotLinkedToWorkAsync(string templateId, CancellationToken ct)
    {
        // ✅ KHUNG: sau này check collection Work/WorkNode/Submission...
        // if (hasLink) throw new InvalidOperationException("Bảng biểu đã được dùng trong Work, không thể sửa/xóa.");
        return Task.CompletedTask;
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
        var page = Math.Max(0, req.Page);
        var pageSize = Math.Clamp(req.PageSize, 1, 100);

        var f = Builders<DynamicExcelTemplate>.Filter;
        var filter = f.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(req.Code))
        {
            var key = req.Code.Trim();
            filter &= f.Regex("code", new BsonRegularExpression(key, "i"));
        }

        if (!string.IsNullOrWhiteSpace(req.Name))
        {
            var key = req.Name.Trim();
            filter &= f.Regex("name", new BsonRegularExpression(key, "i"));
        }

        if (!string.IsNullOrWhiteSpace(req.CreatedBy))
        {
            var key = req.CreatedBy.Trim();
            filter &= f.Regex("createdByUsername", new BsonRegularExpression(key, "i"));
        }

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var key = req.Q.Trim();
            var rx = new BsonRegularExpression(key, "i");
            filter &= (f.Regex("code", rx) | f.Regex("name", rx));
        }

        if (req.CreatedFromUtc.HasValue)
            filter &= f.Gte(x => x.CreatedAtUtc, req.CreatedFromUtc.Value);

        if (req.CreatedToUtc.HasValue)
            filter &= f.Lte(x => x.CreatedAtUtc, req.CreatedToUtc.Value);

        if (req.Labels is { Length: > 0 })
        {
            // any match
            filter &= f.In("labels", req.Labels);
        }

        var total = await _ctx.DynamicExcelTemplates.CountDocumentsAsync(filter, cancellationToken: ct);

        var sortField = NormalizeSortField(req.SortField);
        var sortDir = NormalizeSortDir(req.SortDirection);

        SortDefinition<DynamicExcelTemplate> sort = sortDir == "asc"
            ? Builders<DynamicExcelTemplate>.Sort.Ascending(sortField)
            : Builders<DynamicExcelTemplate>.Sort.Descending(sortField);

        // stable fallback
        sort = Builders<DynamicExcelTemplate>.Sort.Combine(sort, Builders<DynamicExcelTemplate>.Sort.Descending(x => x.CreatedAtUtc));

        var items = await _ctx.DynamicExcelTemplates
            .Find(filter)
            .Sort(sort)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .Project(x => new DynamicExcelRow(
                x.Id, x.Code, x.Name, x.Labels, x.CreatedByUsername, x.CreatedAtUtc
            ))
            .ToListAsync(ct);

        return new PagedResult<DynamicExcelRow>(items, total, page, pageSize);
    }

    public async Task<DynamicExcelDetail> GetByIdAsync(string id, CancellationToken ct)
    {
        var x = await _ctx.DynamicExcelTemplates
            .Find(t => t.Id == id && !t.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Dynamic excel template not found.");

        return new DynamicExcelDetail(
            x.Id, x.Code, x.Name, x.Labels, x.CreatedByUsername, x.CreatedAtUtc,
            x.RawWorkbookDataJson, x.SpecJson,
            new DynamicExcelDataRectDto(x.DataRectR0, x.DataRectC0, x.DataRectR1, x.DataRectC1),
            x.W, x.H
        );
    }

    public async Task<DynamicExcelDetail> CreateAsync(CreateDynamicExcelReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();

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
            Labels = req.Labels ?? Array.Empty<string>(),
            CreatedByUsername = me.Username, // ✅ store for search, no join

            RawWorkbookDataJson = req.RawWorkbookDataJson,
            SpecJson = req.SpecJson,
            DataRectR0 = req.DataRect.R0,
            DataRectC0 = req.DataRect.C0,
            DataRectR1 = req.DataRect.R1,
            DataRectC1 = req.DataRect.C1,
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
        var now = DateTime.UtcNow;

        var doc = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Dynamic excel template not found.");

        RequireCanMutate(me, doc);
        await EnsureNotLinkedToWorkAsync(id, ct);

        var filter = Builders<DynamicExcelTemplate>.Filter.Where(x => x.Id == id && !x.IsDeleted);

        var update = Builders<DynamicExcelTemplate>.Update
            .Set(x => x.Name, req.Name.Trim())
            .Set(x => x.Labels, req.Labels ?? Array.Empty<string>())
            .Set(x => x.RawWorkbookDataJson, req.RawWorkbookDataJson)
            .Set(x => x.SpecJson, req.SpecJson)
            .Set(x => x.DataRectR0, req.DataRect.R0)
            .Set(x => x.DataRectC0, req.DataRect.C0)
            .Set(x => x.DataRectR1, req.DataRect.R1)
            .Set(x => x.DataRectC1, req.DataRect.C1)
            .Set(x => x.W, req.W)
            .Set(x => x.H, req.H)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        var res = await _ctx.DynamicExcelTemplates.UpdateOneAsync(filter, update, cancellationToken: ct);
        if (res.MatchedCount == 0) throw new InvalidOperationException("Dynamic excel template not found.");

        return await GetByIdAsync(id, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var now = DateTime.UtcNow;

        var filter = Builders<DynamicExcelTemplate>.Filter.Where(x => x.Id == id && !x.IsDeleted);
        var update = Builders<DynamicExcelTemplate>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.DeletedAtUtc, now)
            .Set(x => x.DeletedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        var res = await _ctx.DynamicExcelTemplates.UpdateOneAsync(filter, update, cancellationToken: ct);
        if (res.MatchedCount == 0) throw new InvalidOperationException("Dynamic excel template not found.");
    }
}