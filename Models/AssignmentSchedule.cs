using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

public sealed class QuarterDayRule
{
    [BsonElement("quarter")]
    public int Quarter { get; set; }

    [BsonElement("days")]
    public int[] Days { get; set; } = Array.Empty<int>();
}

public sealed class SemiAnnualDayRule
{
    [BsonElement("half")]
    public int Half { get; set; }

    [BsonElement("days")]
    public int[] Days { get; set; } = Array.Empty<int>();
}

public sealed class AssignmentSchedule
{
    // DAILY / WEEKLY / MONTHLY / QUARTERLY / SEMI_ANNUAL
    [BsonElement("cycleType")]
    public string? CycleType { get; set; }

    // ngày bắt đầu áp dụng lịch này
    [BsonElement("startDate")]
    public DateTime? StartDate { get; set; }

    // WEEKLY: các thứ trong tuần cần báo cáo. VD [2, 6]
    [BsonElement("weekDays")]
    public List<int>? WeekDays { get; set; }

    // MONTHLY: các ngày trong tháng. VD [1, 15, 18]
    [BsonElement("monthDays")]
    public List<int>? MonthDays { get; set; }

    // Legacy offsets repeat in every quarter. Keep for existing assignments.
    [BsonElement("quarterDays")]
    public int[] QuarterDays { get; set; }

    // Legacy offsets repeat in both halves. Keep for existing assignments.
    [BsonElement("semiAnnualDays")]
    public int[] SemiAnnualDays { get; set; }

    // New rules retain the selected quarter/half instead of repeating offsets.
    [BsonElement("quarterDayRules")]
    public List<QuarterDayRule>? QuarterDayRules { get; set; }

    [BsonElement("semiAnnualDayRules")]
    public List<SemiAnnualDayRule>? SemiAnnualDayRules { get; set; }

    [BsonElement("note")]
    public string? Note { get; set; }
}
