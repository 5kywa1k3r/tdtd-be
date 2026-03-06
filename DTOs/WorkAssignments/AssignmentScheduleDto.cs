namespace tdtd_be.DTOs.WorkAssignments;

public sealed record QuarterDayRuleDto(
    int Quarter,
    List<int> Days
);

public sealed record SemiAnnualDayRuleDto(
    int Half,
    List<int> Days
);

public sealed record AssignmentScheduleDto(
    string? CycleType,
    DateTime? StartDate,
    List<int>? WeekDays,
    List<int>? MonthDays,
    List<int>? QuarterDays,
    List<int>? SemiAnnualDays,
    string? Note
);