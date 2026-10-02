namespace tdtd_be.DTOs.WorkAssignments;

public sealed record WorkAssignmentHandoverUnitScopeDto(string ActorUnitId, IReadOnlyList<string> UnitIds);
