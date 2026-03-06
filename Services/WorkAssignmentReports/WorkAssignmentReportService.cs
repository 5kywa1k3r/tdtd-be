using MongoDB.Driver;
using System.Text.Json;
using tdtd_be.Data;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.DTOs.Common;
using tdtd_be.Models;
using tdtd_be.Models.Enums;

namespace tdtd_be.Services.WorkAssignmentReports;

/// <summary>
/// Service xử lý report theo kỳ của WorkAssignment.
/// 
/// Phase 1:
/// - chưa đi sâu workflow duyệt
/// - tập trung init draft / get / search / save draft
/// - giữ snapshot template + schedule
/// - lưu workbook + values1D
/// </summary>
public sealed class WorkAssignmentReportService : IWorkAssignmentReportService
{
    private readonly MongoDbContext _ctx;

    /// <summary>
    /// Json options dùng serialize snapshot / values1D.
    /// Có thể thay bằng helper JsonOptions chung của dự án nếu đã có.
    /// </summary>
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public WorkAssignmentReportService(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    /// <summary>
    /// Khởi tạo draft mới cho 1 assignment tại 1 kỳ.
    /// Snapshot template + schedule tại thời điểm tạo.
    /// </summary>
    public async Task<WorkAssignmentReportResponse> InitDraftAsync(
        string workAssignmentId,
        InitWorkAssignmentReportRequest req,
        string currentUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workAssignmentId))
            throw new ArgumentException("workAssignmentId không được trống.", nameof(workAssignmentId));

        if (string.IsNullOrWhiteSpace(req.PeriodKey))
            throw new ArgumentException("PeriodKey không được trống.", nameof(req.PeriodKey));

        // TODO phase sau:
        // - permission check đọc/khởi tạo theo assignment
        // - validate periodKey chặt hơn theo cycleType

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == workAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (!assignment.IsActive)
            throw new InvalidOperationException("WorkAssignment đã dừng hiệu lực, không thể tạo báo cáo mới.");

        if (assignment is null)
            throw new InvalidOperationException("Không tìm thấy WorkAssignment.");

        if (string.IsNullOrWhiteSpace(assignment.DynamicExcelId))
            throw new InvalidOperationException("WorkAssignment chưa cấu hình template.");

        var template = await _ctx.DynamicExcelTemplates
            .Find(x => x.Id == assignment.DynamicExcelId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (template is null)
            throw new InvalidOperationException("Không tìm thấy DynamicExcelTemplate.");

        // Phase 1: nếu assignment + kỳ đã có report current thì chặn luôn.
        // Sau này phase versioning có thể mở rộng sang tạo version mới.
        var existedCurrent = await _ctx.WorkAssignmentReports
            .Find(x =>
                x.WorkAssignmentId == workAssignmentId &&
                x.PeriodKey == req.PeriodKey &&
                x.IsCurrent &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (existedCurrent is not null)
            throw new InvalidOperationException("Kỳ báo cáo này đã có bản hiện hành.");

        var templateSnapshot = BuildTemplateSnapshot(template);
        var scheduleSnapshot = BuildScheduleSnapshot(assignment);

        var now = DateTime.UtcNow;

        var entity = new WorkAssignmentReport
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId().ToString(),

            // root work để search nhanh theo Work
            WorkId = assignment.WorkId,

            // node assignment sinh ra report
            WorkAssignmentId = assignment.Id,

            // kỳ báo cáo thực tế
            PeriodKey = req.PeriodKey.Trim(),
            PeriodStart = req.PeriodStart,
            PeriodEnd = req.PeriodEnd,

            // phase 1 luôn tạo draft
            Status = WorkAssignmentReportStatus.Draft,

            // snapshot template + schedule
            TemplateSnapshotJson = JsonSerializer.Serialize(templateSnapshot, _jsonOptions),
            ScheduleSnapshotJson = JsonSerializer.Serialize(scheduleSnapshot, _jsonOptions),

            // copy field rời từ template để query / render nhanh
            DynamicExcelTemplateId = template.Id,
            DynamicExcelTemplateCode = template.Code,
            DynamicExcelTemplateName = template.Name,

            // draft mới ban đầu dùng workbook gốc từ template
            RawWorkbookDataJson = template.RawWorkbookDataJson,
            SpecJson = template.SpecJson,

            // copy dataRect
            DataRectR0 = template.DataRectR0,
            DataRectC0 = template.DataRectC0,
            DataRectR1 = template.DataRectR1,
            DataRectC1 = template.DataRectC1,
            W = template.W,
            H = template.H,

            // draft mới chưa có data thực => values1D rỗng/null theo kích thước
            Values1DJson = JsonSerializer.Serialize(CreateEmptyValues1D(template.W, template.H), _jsonOptions),

            Note = req.Note,

            // phase 1 bản đầu luôn là version 1/current
            VersionNo = 1,
            IsCurrent = true,

            // base audit
            CreatedByUserId = currentUserId,
            UpdatedByUserId = currentUserId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            IsDeleted = false
        };

        await _ctx.WorkAssignmentReports.InsertOneAsync(entity, cancellationToken: ct);
        return MapToResponse(entity);
    }

    /// <summary>
    /// Lấy detail 1 report.
    /// </summary>
    public async Task<WorkAssignmentReportResponse> GetByIdAsync(
        string id,
        string currentUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id không được trống.", nameof(id));

        var entity = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw new InvalidOperationException("Không tìm thấy WorkAssignmentReport.");

        // TODO phase sau: permission check read
        _ = currentUserId;

        return MapToResponse(entity);
    }

    /// <summary>
    /// Lấy list report theo assignment.
    /// </summary>
    public async Task<List<WorkAssignmentReportListRow>> GetByAssignmentAsync(
        string workAssignmentId,
        string currentUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workAssignmentId))
            throw new ArgumentException("workAssignmentId không được trống.", nameof(workAssignmentId));

        // TODO phase sau: permission check read
        _ = currentUserId;

        var rows = await _ctx.WorkAssignmentReports
            .Find(x => x.WorkAssignmentId == workAssignmentId && !x.IsDeleted)
            .SortByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(ct);

        return rows.Select(MapToListRow).ToList();
    }

    /// <summary>
    /// Search/paging report.
    /// </summary>
    public async Task<PagedResult<WorkAssignmentReportListRow>> SearchAsync(
        WorkAssignmentReportSearchRequest req,
        string currentUserId,
        CancellationToken ct = default)
    {
        req ??= new WorkAssignmentReportSearchRequest();

        var page = req.Page < 0 ? 0 : req.Page;
        var pageSize = req.PageSize <= 0 ? 20 : req.PageSize;

        var filter = Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(req.WorkId))
            filter &= Builders<WorkAssignmentReport>.Filter.Eq(x => x.WorkId, req.WorkId);

        if (!string.IsNullOrWhiteSpace(req.WorkAssignmentId))
            filter &= Builders<WorkAssignmentReport>.Filter.Eq(x => x.WorkAssignmentId, req.WorkAssignmentId);

        if (!string.IsNullOrWhiteSpace(req.PeriodKey))
            filter &= Builders<WorkAssignmentReport>.Filter.Eq(x => x.PeriodKey, req.PeriodKey.Trim());

        if (req.Status.HasValue)
            filter &= Builders<WorkAssignmentReport>.Filter.Eq(x => x.Status, (WorkAssignmentReportStatus)req.Status.Value);

        if (req.IsCurrent.HasValue)
            filter &= Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsCurrent, req.IsCurrent.Value);

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var q = req.Q.Trim();

            filter &= Builders<WorkAssignmentReport>.Filter.Or(
                Builders<WorkAssignmentReport>.Filter.Regex(x => x.PeriodKey, new MongoDB.Bson.BsonRegularExpression(q, "i")),
                Builders<WorkAssignmentReport>.Filter.Regex(x => x.DynamicExcelTemplateCode, new MongoDB.Bson.BsonRegularExpression(q, "i")),
                Builders<WorkAssignmentReport>.Filter.Regex(x => x.DynamicExcelTemplateName, new MongoDB.Bson.BsonRegularExpression(q, "i"))
            );
        }

        // TODO phase sau: permission filter theo user/doc-role
        _ = currentUserId;

        var sort = BuildSortDefinition(req.SortField, req.SortDirection);

        var total = await _ctx.WorkAssignmentReports.CountDocumentsAsync(filter, cancellationToken: ct);

        var rows = await _ctx.WorkAssignmentReports
            .Find(filter)
            .Sort(sort)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return new PagedResult<WorkAssignmentReportListRow>(
            rows.Select(MapToListRow).ToList(),
            total,
            page,
            pageSize
        );
    }

/// <summary>
/// Lưu draft workbook.
/// FE tự extract values1D và gửi lên.
/// Backend chỉ validate kích thước rồi lưu.
/// </summary>
public async Task<WorkAssignmentReportResponse> SaveDraftAsync(
    string id,
    SaveWorkAssignmentReportDraftRequest req,
    string currentUserId,
    CancellationToken ct = default)
{
    if (string.IsNullOrWhiteSpace(id))
        throw new ArgumentException("Id không được trống.", nameof(id));

    if (req is null)
        throw new ArgumentNullException(nameof(req));

    if (string.IsNullOrWhiteSpace(req.RawWorkbookDataJson))
        throw new ArgumentException("RawWorkbookDataJson không được trống.", nameof(req));

    var entity = await _ctx.WorkAssignmentReports
        .Find(x => x.Id == id && !x.IsDeleted)
        .FirstOrDefaultAsync(ct);

    if (entity is null)
        throw new InvalidOperationException("Không tìm thấy WorkAssignmentReport.");

    if (entity.Status != WorkAssignmentReportStatus.Draft)
        throw new InvalidOperationException("Chỉ được lưu khi report đang ở trạng thái Draft.");

    // TODO phase sau:
    // - permission check edit draft
    // - validate workbook read-only ngoài dataRect nếu cần

    var expectedLength = entity.W * entity.H;
    var actualLength = req.Values1D?.Count ?? 0;

    if (actualLength != expectedLength)
    {
        throw new InvalidOperationException(
            $"Values1D không hợp lệ. Expected={expectedLength}, Actual={actualLength}.");
    }

    var now = DateTime.UtcNow;
    var values1DJson = JsonSerializer.Serialize(req.Values1D, _jsonOptions);

    var update = Builders<WorkAssignmentReport>.Update
        .Set(x => x.RawWorkbookDataJson, req.RawWorkbookDataJson)
        .Set(x => x.Values1DJson, values1DJson)
        .Set(x => x.Note, req.Note)
        .Set(x => x.UpdatedAtUtc, now)
        .Set(x => x.UpdatedByUserId, currentUserId);

    await _ctx.WorkAssignmentReports.UpdateOneAsync(
        x => x.Id == id && !x.IsDeleted,
        update,
        cancellationToken: ct);

    entity.RawWorkbookDataJson = req.RawWorkbookDataJson;
    entity.Values1DJson = values1DJson;
    entity.Note = req.Note;
    entity.UpdatedAtUtc = now;
    entity.UpdatedByUserId = currentUserId;

    return MapToResponse(entity);
}

    // =========================
    // Helpers
    // =========================

    /// <summary>
    /// Build snapshot template để lưu lịch sử đúng tại thời điểm tạo report.
    /// </summary>
    private static TemplateSnapshotDTO BuildTemplateSnapshot(DynamicExcelTemplate template)
    {
        return new TemplateSnapshotDTO
        {
            TemplateId = template.Id,
            Code = template.Code,
            Name = template.Name,
            SpecJson = template.SpecJson,
            RawWorkbookDataJson = template.RawWorkbookDataJson,
            DataRectR0 = template.DataRectR0,
            DataRectC0 = template.DataRectC0,
            DataRectR1 = template.DataRectR1,
            DataRectC1 = template.DataRectC1,
            W = template.W,
            H = template.H
        };
    }

    /// <summary>
    /// Build snapshot schedule từ assignment hiện tại.
    /// </summary>
    private static ScheduleSnapshotDTO BuildScheduleSnapshot(WorkAssignment assignment)
    {
        return new ScheduleSnapshotDTO
        {
            CycleType = assignment.Schedule?.CycleType ?? string.Empty,
            StartDate = assignment.Schedule?.StartDate,
            WeekDays = assignment.Schedule?.WeekDays?.ToArray() ?? Array.Empty<int>(),
            MonthDays = assignment.Schedule?.MonthDays?.ToArray() ?? Array.Empty<int>(),

            // QuarterDayRule[] -> int[]
            QuarterDays = assignment.Schedule?.QuarterDays?.ToArray() ?? Array.Empty<int>(),

            // SemiAnnualDayRule[] -> int[]
            SemiAnnualDays = assignment.Schedule?.SemiAnnualDays?.ToArray() ?? Array.Empty<int>(),

            Note = assignment.Schedule?.Note
        };
    }

    /// <summary>
    /// Tạo values1D rỗng theo kích thước W * H.
    /// Dùng cho report draft mới tạo từ template, khi user chưa nhập dữ liệu.
    /// </summary>
    private static List<decimal?> CreateEmptyValues1D(int w, int h)
    {
        var len = Math.Max(0, w) * Math.Max(0, h);
        return Enumerable.Range(0, len).Select(_ => (decimal?)null).ToList();
    }

    /// <summary>
    /// Build sort definition theo tên field FE gửi lên.
    /// </summary>
    private static SortDefinition<WorkAssignmentReport> BuildSortDefinition(string? sortField, string? sortDirection)
    {
        var desc = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);

        return (sortField ?? "updatedAtUtc").ToLowerInvariant() switch
        {
            "createdatutc" => desc
                ? Builders<WorkAssignmentReport>.Sort.Descending(x => x.CreatedAtUtc)
                : Builders<WorkAssignmentReport>.Sort.Ascending(x => x.CreatedAtUtc),

            "periodkey" => desc
                ? Builders<WorkAssignmentReport>.Sort.Descending(x => x.PeriodKey)
                : Builders<WorkAssignmentReport>.Sort.Ascending(x => x.PeriodKey),

            "versionno" => desc
                ? Builders<WorkAssignmentReport>.Sort.Descending(x => x.VersionNo)
                : Builders<WorkAssignmentReport>.Sort.Ascending(x => x.VersionNo),

            _ => desc
                ? Builders<WorkAssignmentReport>.Sort.Descending(x => x.UpdatedAtUtc)
                : Builders<WorkAssignmentReport>.Sort.Ascending(x => x.UpdatedAtUtc)
        };
    }

    /// <summary>
    /// Map entity sang row gọn cho list.
    /// </summary>
    private static WorkAssignmentReportListRow MapToListRow(WorkAssignmentReport x)
    {
        return new WorkAssignmentReportListRow
        {
            Id = x.Id,
            WorkId = x.WorkId,
            WorkAssignmentId = x.WorkAssignmentId,
            PeriodKey = x.PeriodKey,
            Status = x.Status,
            VersionNo = x.VersionNo,
            IsCurrent = x.IsCurrent,
            DynamicExcelTemplateId = x.DynamicExcelTemplateId,
            DynamicExcelTemplateCode = x.DynamicExcelTemplateCode,
            DynamicExcelTemplateName = x.DynamicExcelTemplateName,
            SubmittedAtUtc = x.SubmittedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc
        };
    }

    /// <summary>
    /// Map entity sang detail response.
    /// </summary>
    private static WorkAssignmentReportResponse MapToResponse(WorkAssignmentReport x)
    {
        return new WorkAssignmentReportResponse
        {
            Id = x.Id,
            WorkId = x.WorkId,
            WorkAssignmentId = x.WorkAssignmentId,
            PeriodKey = x.PeriodKey,
            PeriodStart = x.PeriodStart,
            PeriodEnd = x.PeriodEnd,
            Status = x.Status,

            TemplateSnapshotJson = x.TemplateSnapshotJson,
            ScheduleSnapshotJson = x.ScheduleSnapshotJson,

            DynamicExcelTemplateId = x.DynamicExcelTemplateId,
            DynamicExcelTemplateCode = x.DynamicExcelTemplateCode,
            DynamicExcelTemplateName = x.DynamicExcelTemplateName,

            RawWorkbookDataJson = x.RawWorkbookDataJson,
            SpecJson = x.SpecJson,

            DataRectR0 = x.DataRectR0,
            DataRectC0 = x.DataRectC0,
            DataRectR1 = x.DataRectR1,
            DataRectC1 = x.DataRectC1,
            W = x.W,
            H = x.H,

            Values1DJson = x.Values1DJson,
            Note = x.Note,

            VersionNo = x.VersionNo,
            IsCurrent = x.IsCurrent,

            SubmittedAtUtc = x.SubmittedAtUtc,
            SubmittedByUserId = x.SubmittedByUserId,

            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc
        };
    }

    /// <summary>
    /// Danh sách ngoài cùng của user trong 1 Work, nhóm theo template.
    /// 
    /// Logic:
    /// - đi từ WorkAssignment (vì có assignment chưa hề có report)
    /// - lọc theo workId + assignee hiện tại
    /// - group theo DynamicExcelId
    /// - gắn thông tin report mới nhất nếu có
    /// </summary>
    public async Task<PagedResult<MyReportTemplateRow>> SearchMyReportTemplatesAsync(
        string workId,
        MyReportTemplateSearchRequest req,
        string currentUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workId))
            throw new ArgumentException("workId không được trống.", nameof(workId));

        req ??= new MyReportTemplateSearchRequest();

        var page = req.Page < 0 ? 0 : req.Page;
        var pageSize = req.PageSize <= 0 ? 20 : req.PageSize;

        // =========================
        // 1. Query assignment của user trong work
        // =========================
        var assignmentFilter = Builders<WorkAssignment>.Filter.Eq(x => x.IsDeleted, false)
            & Builders<WorkAssignment>.Filter.Eq(x => x.WorkId, workId);

        if (req.IsActive.HasValue)
            assignmentFilter &= Builders<WorkAssignment>.Filter.Eq(x => x.IsActive, req.IsActive.Value);

        // IMPORTANT:
        // ĐOẠN NÀY PHẢI MAP ĐÚNG THEO MODEL THẬT CỦA BỆ HẠ.
        // Hiện thần thiếp giả định WorkAssignment có:
        // Assignees: list object có UserId
        assignmentFilter &= Builders<WorkAssignment>.Filter.ElemMatch(
            x => x.Assignees,
            a => a.UserId == currentUserId);

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var q = req.Q.Trim();

            // nếu assignment đã có copy sẵn code/name template thì tận dụng
            assignmentFilter &= Builders<WorkAssignment>.Filter.Or(
                Builders<WorkAssignment>.Filter.Regex(x => x.DynamicExcelCode, new MongoDB.Bson.BsonRegularExpression(q, "i")),
                Builders<WorkAssignment>.Filter.Regex(x => x.DynamicExcelName, new MongoDB.Bson.BsonRegularExpression(q, "i"))
            );
        }

        var assignments = await _ctx.WorkAssignments
            .Find(assignmentFilter)
            .ToListAsync(ct);

        if (assignments.Count == 0)
        {
            return new PagedResult<MyReportTemplateRow>(new List<MyReportTemplateRow>(), 0, page, pageSize);
        }

        // =========================
        // 2. Lấy report liên quan tới các assignment đó
        // =========================
        var assignmentIds = assignments
            .Select(x => x.Id)
            .Distinct()
            .ToList();

        var reportFilter = Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false)
            & Builders<WorkAssignmentReport>.Filter.In(x => x.WorkAssignmentId, assignmentIds);

        var reports = await _ctx.WorkAssignmentReports
            .Find(reportFilter)
            .SortByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(ct);

        // =========================
        // 3. Group assignment theo template
        // =========================
        var rows = assignments
            .GroupBy(x => new
            {
                x.DynamicExcelId,
                x.DynamicExcelCode,
                x.DynamicExcelName
            })
            .Select(g =>
            {
                var groupAssignmentIds = g.Select(x => x.Id).ToHashSet();

                var groupReports = reports
                    .Where(r => groupAssignmentIds.Contains(r.WorkAssignmentId))
                    .OrderByDescending(r => r.UpdatedAtUtc)
                    .ToList();

                var latestReport = groupReports.FirstOrDefault();

                return new MyReportTemplateRow
                {
                    DynamicExcelId = g.Key.DynamicExcelId ?? string.Empty,
                    DynamicExcelCode = g.Key.DynamicExcelCode ?? string.Empty,
                    DynamicExcelName = g.Key.DynamicExcelName ?? string.Empty,

                    AssignmentCount = g.Count(),
                    ReportCount = groupReports.Count,

                    LatestPeriodKey = latestReport?.PeriodKey,
                    LatestReportStatus = latestReport is null ? null : (int)latestReport.Status,
                    LatestUpdatedAtUtc = latestReport?.UpdatedAtUtc,
                    LatestReportId = latestReport?.Id
                };
            })
            .ToList();

        // =========================
        // 4. Filter HasReport sau khi group
        // =========================
        if (req.HasReport.HasValue)
        {
            rows = req.HasReport.Value
                ? rows.Where(x => x.ReportCount > 0).ToList()
                : rows.Where(x => x.ReportCount == 0).ToList();
        }

        // =========================
        // 5. Sort
        // =========================
        rows = ApplyMyReportTemplateSort(rows, req.SortField, req.SortDirection);

        // =========================
        // 6. Paging
        // =========================
        var total = rows.Count;
        var pagedRows = rows
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToList();

        return new PagedResult<MyReportTemplateRow>(pagedRows, total, page, pageSize);
    }

    /// <summary>
    /// Sort outer list theo field FE gửi lên.
    /// </summary>
    private static List<MyReportTemplateRow> ApplyMyReportTemplateSort(
        List<MyReportTemplateRow> rows,
        string? sortField,
        string? sortDirection)
    {
        var desc = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);

        return (sortField ?? "latestUpdatedAtUtc").ToLowerInvariant() switch
        {
            "dynamicexcelcode" => desc
                ? rows.OrderByDescending(x => x.DynamicExcelCode).ToList()
                : rows.OrderBy(x => x.DynamicExcelCode).ToList(),

            "dynamicexcelname" => desc
                ? rows.OrderByDescending(x => x.DynamicExcelName).ToList()
                : rows.OrderBy(x => x.DynamicExcelName).ToList(),

            "assignmentcount" => desc
                ? rows.OrderByDescending(x => x.AssignmentCount).ToList()
                : rows.OrderBy(x => x.AssignmentCount).ToList(),

            _ => desc
                ? rows.OrderByDescending(x => x.LatestUpdatedAtUtc ?? DateTime.MinValue).ToList()
                : rows.OrderBy(x => x.LatestUpdatedAtUtc ?? DateTime.MinValue).ToList(),
        };
    }
}