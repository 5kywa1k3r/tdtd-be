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
            Name = ResolveAssignmentName(entity),

            ParentAssignmentId = entity.ParentAssignmentId,
            RootAssignmentId = entity.RootAssignmentId,
            Level = entity.Level,
            Code = entity.Code,
            Path = entity.Path,

            DynamicExcelId = entity.DynamicExcelId,
            DynamicExcelCode = entity.DynamicExcelCode,
            DynamicExcelName = entity.DynamicExcelName,
            DynamicFormTemplateId = entity.DynamicFormTemplateId,
            DynamicFormTemplateCode = entity.DynamicFormTemplateCode,
            DynamicFormTemplateName = entity.DynamicFormTemplateName,
            DynamicFormDataSourceRulesJson = entity.DynamicFormDataSourceRulesJson,
            AutoApproveConditionJson = entity.AutoApproveConditionJson,

            WorkType = entity.WorkType,
            AssignmentType = entity.AssignmentType,
            AggregationType = entity.AggregationType,
            Schedule = WorkAssignmentScheduleHelper.ToScheduleDto(entity.Schedule),
            StartDate = entity.StartDate,
            DueDate = entity.DueDate,
            CompletedDate = entity.CompletedDate,
            CompletedAtUtc = entity.CompletedAtUtc,
            CompletedByUserId = entity.CompletedByUserId,

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

            ProgressStatus = entity.ProgressStatus,
            ProgressStatusUpdatedAtUtc = entity.ProgressStatusUpdatedAtUtc,
            LatestPeriodKey = entity.LatestPeriodKey,
            LatestDueAtUtc = entity.LatestDueAtUtc,
            HasAnyDuePeriod = entity.HasAnyDuePeriod,
            HasOverduePeriod = entity.HasOverduePeriod,

            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,

            EvaluationTemplateId = entity.EvaluationTemplateId,
            EvaluationTemplateCode = entity.EvaluationTemplateCode,
            EvaluationTemplateLabel = entity.EvaluationTemplateLabel,
            EvaluationCode = entity.EvaluationCode,
            EvaluationLabel = entity.EvaluationLabel,
            EvaluationNote = entity.EvaluationNote,
            EvaluatedAtUtc = entity.EvaluatedAtUtc,
            EvaluatedByUserId = entity.EvaluatedByUserId,

            WorstPeriodStatus = entity.WorstPeriodStatus,
            WorstOverdueReasonCode = entity.WorstOverdueReasonCode,
            WorstOverdueReasonLabel = entity.WorstOverdueReasonLabel,
            HasManualEvaluations = entity.HasManualEvaluations,
            EvaluatedAssignmentCount = entity.EvaluatedAssignmentCount,
            WorstEvaluationCode = entity.WorstEvaluationCode,
            WorstEvaluationLabel = entity.WorstEvaluationLabel,
            DueAtUtc = entity.DueAtUtc,
        };
    }

    private static string ResolveAssignmentName(WorkAssignment entity)
    {
        var name = entity.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        return entity.DynamicFormTemplateName?.Trim()
               ?? entity.DynamicExcelName?.Trim()
               ?? entity.Code
               ?? entity.Id
               ?? string.Empty;
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
