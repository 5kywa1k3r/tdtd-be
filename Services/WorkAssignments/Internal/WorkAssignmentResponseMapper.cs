using tdtd_be.DTOs.Users;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentResponseMapper
{
    public static WorkAssignmentResponse ToResponse(WorkAssignment entity, bool hasData)
    {
        return new WorkAssignmentResponse
        {
            Id = entity.Id!,
            WorkId = entity.WorkId,

            ParentAssignmentId = entity.ParentAssignmentId,
            RootAssignmentId = entity.RootAssignmentId,
            Level = entity.Level,
            Code = entity.Code,
            Path = entity.Path,

            DynamicExcelId = entity.DynamicExcelId,
            DynamicExcelCode = entity.DynamicExcelCode,
            DynamicExcelName = entity.DynamicExcelName,

            WorkType = entity.WorkType,
            AssignmentType = entity.AssignmentType,
            AggregationType = entity.AggregationType,
            Schedule = WorkAssignmentScheduleHelper.ToScheduleDto(entity.Schedule),

            Assignees = (entity.Assignees ?? new List<UserRef>())
                .Select(ToUserRefDto)
                .ToList(),

            LeaderWatcherUserIds = entity.LeaderWatcherUserIds ?? new List<string>(),
            LeaderWatchers = (entity.LeaderWatchers ?? new List<UserRef>())
                .Select(ToUserRefDto)
                .ToList(),

            Description = entity.Description,
            IsActive = entity.IsActive,

            HasData = hasData,
            TemplateLocked = hasData,

            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc
        };
    }

    private static UserRefDTO ToUserRefDto(UserRef x) => new(
        userId: x.UserId,
        username: x.Username,
        fullName: x.FullName,
        unitId: x.UnitId,
        unitSymbol: x.UnitSymbol,
        unitShortName: x.UnitShortName,
        unitName: x.UnitName,
        positionCode: x.PositionCode,
        positionName: x.PositionName
    );
}