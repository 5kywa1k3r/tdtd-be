using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using tdtd_be.Common.Errors;

namespace tdtd_be.Services.WorkAssignments;

/// <summary>Yud 30/09/2026: tạm khóa tổng hợp cũ; giữ mã và dữ liệu lịch sử.
/// Không liên quan Aggregate v2, payload reader, Flow hoặc phase barrier.</summary>
public static class LegacyAggregateRetirement
{
    // Khóa tại source, không cho cấu hình triển khai/capability cũ tự mở lại.
    public static bool IsDisabled => true;
    public const string Reason = "LEGACY_AGGREGATE_DISABLED";
    public const string Message = "Luồng tổng hợp cũ đã tạm ngừng. Mở báo cáo nháp và chọn Tổng hợp vào báo cáo.";

    public static void Reject()
    {
        if (IsDisabled)
            throw AppExceptionFactory.BadRequest(AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
                new { reason = Reason }, Message);
    }

    // Chỉ dùng khi tạo giao việc. Dữ liệu đã lưu vẫn được đọc nguyên trạng.
    // Kiểm trước normalizer vì normalizer cũ chuyển AGGREGATE_CHILDREN thành MANUAL.
    public static void RequireManualSourceRules(string? json)
    {
        if (!IsDisabled || string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            Check(document.RootElement);
        }
        catch (JsonException)
        {
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_DATA_SOURCE_RULES_INVALID);
        }
    }

    public static void RequireUnchangedLegacySummary(string? requested, string? current)
    {
        // Lưu nháp thông thường được giữ nguyên metadata cũ; không tạo/sửa công thức cũ qua save DTO.
        if (!IsDisabled || requested is null || string.Equals(requested?.Trim(), current?.Trim(), StringComparison.Ordinal)) return;
        if (IsLegacySummary(requested) || IsLegacySummary(current)) Reject();
    }

    private static bool IsLegacySummary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(p =>
                    p.Name.Equals("kind", StringComparison.OrdinalIgnoreCase)
                    && p.Value.ValueKind == JsonValueKind.String
                    && p.Value.GetString() == "DYNAMIC_FORM_AGGREGATE_DRAFT");
        }
        catch (JsonException) { return false; } // Validator payload hiện hành xử lý JSON sai.
    }

    private static void Check(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Check(item);
        if (value.ValueKind != JsonValueKind.Object) return;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name.Equals("sourceRule", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.Value.GetString())
                && !string.Equals(property.Value.GetString()?.Trim(), "MANUAL", StringComparison.OrdinalIgnoreCase))
                Reject();
            if (property.Name.StartsWith("source", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("sourceRule", StringComparison.OrdinalIgnoreCase)
                && (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString())
                    || property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() > 0))
                Reject();
            Check(property.Value);
        }
    }
}

// Resource filter chặn trước model binding/action: API cũ không gọi evaluator hoặc ghi DB.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class LegacyAggregateDisabledAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (LegacyAggregateRetirement.IsDisabled)
            context.Result = new ObjectResult(new {
                code = LegacyAggregateRetirement.Reason,
                message = LegacyAggregateRetirement.Message
            }) { StatusCode = StatusCodes.Status410Gone };
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
