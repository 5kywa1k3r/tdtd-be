namespace tdtd_be.Options;

public sealed class WorkAssignmentScopeOptions
{
    public List<UnitTypeAssignmentRuleOptions> UnitTypeAssignmentRules { get; set; } = new();
}

public sealed class UnitTypeAssignmentRuleOptions
{
    public bool Enabled { get; set; } = true;
    public List<string> ActorUnitTypeCodes { get; set; } = new();
    public List<string> TargetUnitTypeCodes { get; set; } = new();
    public List<string> TargetAccountKinds { get; set; } = new();
}
