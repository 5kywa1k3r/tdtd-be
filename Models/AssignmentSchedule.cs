using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

public sealed class AssignmentSchedule
{
    // WEEKLY / MONTHLY / QUARTERLY / SEMI_ANNUAL
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

    // QUARTERLY: map theo quý
    // Q1: [5, 20], Q2: [10], ...
    [BsonElement("quarterDays")]
    public int[] QuarterDays { get; set; }

    // SEMI_ANNUAL: nửa năm 1 và nửa năm 2
    [BsonElement("semiAnnualDays")]
    public int[] SemiAnnualDays { get; set; }

    [BsonElement("note")]
    public string? Note { get; set; }
}