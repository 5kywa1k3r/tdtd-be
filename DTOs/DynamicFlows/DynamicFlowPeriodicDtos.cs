namespace tdtd_be.DTOs.DynamicFlows;

public sealed class DynamicFlowPeriodicScheduleCreateRequest
{
    public string FlowTemplateVersionId { get; set; } = string.Empty;
    public string CommandId { get; set; } = string.Empty;
    public List<string> TargetUnitIds { get; set; } = new();
    public string TimeZoneId { get; set; } = string.Empty;
    public string LocalTime { get; set; } = string.Empty;
    public DateTime? EffectiveFromUtc { get; set; }
}

public sealed class DynamicFlowPeriodicManualRerunRequest
{
    public string CommandId { get; set; } = string.Empty;
}

public sealed class DynamicFlowPeriodicScheduleDto
{
    public string ScheduleId { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
    public string FlowTemplateVersionId { get; set; } = string.Empty;
    public int FlowTemplateVersionNo { get; set; }
    public string ScheduleKey { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = string.Empty;
    public string NormalizedTimeZoneId { get; set; } = string.Empty;
    public string Cadence { get; set; } = string.Empty;
    public string LocalTime { get; set; } = string.Empty;
    public string PolicyVersion { get; set; } = string.Empty;
    public string ScheduleIdentityHash { get; set; } = string.Empty;
    public DateTime NextDueAtUtc { get; set; }
    public string State { get; set; } = string.Empty;
    public long Revision { get; set; }
}

public sealed class DynamicFlowPeriodicOccurrenceDto
{
    public string OccurrenceId { get; set; } = string.Empty;
    public string ScheduleId { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
    public string PeriodKey { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = string.Empty;
    public string PolicyVersion { get; set; } = string.Empty;
    public DateTime ScheduledAtUtc { get; set; }
    public DateTime ObservedAtUtc { get; set; }
    public string State { get; set; } = string.Empty;
    public string? ReasonCode { get; set; }
    public string? FlowInstanceId { get; set; }
    public IReadOnlyList<string> ManualCommandIds { get; set; } =
        Array.Empty<string>();
    public long Revision { get; set; }
}

public sealed class DynamicFlowPeriodicProcessRequest
{
    public DateTime? ObservedAtUtc { get; set; }
    public int MaxItems { get; set; } = 20;
}

public sealed class DynamicFlowPeriodicProcessResponse
{
    public DateTime ObservedAtUtc { get; set; }
    public int ClaimedCount { get; set; }
    public int LaunchedCount { get; set; }
    public int MissedCount { get; set; }
    public int RecoveredCount { get; set; }
}
