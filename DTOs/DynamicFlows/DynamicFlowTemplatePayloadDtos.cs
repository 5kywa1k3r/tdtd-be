using System.Text.Json;
using System.Text.Json.Serialization;
using tdtd_be.Models;

namespace tdtd_be.DTOs.DynamicFlows;

public sealed class DynamicFlowTemplatePayloadDto
{
    public int SchemaVersion { get; set; } = DynamicFlowDefinitionSchema.CurrentVersion;
    public string ArchetypeId { get; set; } = string.Empty;
    public string EntryStepId { get; set; } = string.Empty;
    public string? RootDynamicFormTemplateId { get; set; }
    public string? ResultOwnerStepId { get; set; }
    public string? ResultOwnerFormNodeId { get; set; }
    public string? StatisticsOwnerStepId { get; set; }
    public string? StatisticsOwnerFormNodeId { get; set; }
    public string? CatalogVersion { get; set; }
    public string? CatalogSemanticHash { get; set; }
    public List<DynamicFlowFormNodeDto> FormNodes { get; set; } = new();
    public List<DynamicFlowTopologyNodeDto> Nodes { get; set; } = new();
    public List<DynamicFlowTopologyEdgeDto> Edges { get; set; } = new();

    [Obsolete("Legacy schema v1 only. Canonical schema v2 uses nodes.")]
    public List<DynamicFlowStepDefinitionDto> Steps { get; set; } = new();

    [Obsolete("Legacy schema v1 only. Canonical schema v2 uses edges.")]
    public List<DynamicFlowTransitionDto> Transitions { get; set; } = new();
    public List<DynamicFlowActorPolicyDto> ActorPolicies { get; set; } = new();
    public List<DynamicFlowFieldPolicyDto> FieldPolicies { get; set; } = new();
    public List<DynamicFlowTableColumnPolicyDto> TableColumnPolicies { get; set; } = new();
    public List<DynamicFlowMappingRuleDto> MappingRules { get; set; } = new();
    public Dictionary<string, JsonElement> RollbackPolicy { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, JsonElement> FinalResultPolicy { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, JsonElement> StatisticProfile { get; set; } = new(StringComparer.Ordinal);

}

public sealed class DynamicFlowFormNodeDto
{
    public string FormNodeId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string? DynamicFormFamilyId { get; set; }
    public int? DynamicFormVersionNo { get; set; }
    public string? DynamicFormSchemaHash { get; set; }
    public string? DynamicFormSnapshotHash { get; set; }
}

public sealed class DynamicFlowTopologyNodeDto
{
    public string NodeId { get; set; } = string.Empty;
    public string NodeCode { get; set; } = string.Empty;
    public string NodeKind { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? FormNodeId { get; set; }
    public List<string> DeclaredRoles { get; set; } = new();
    public DynamicFlowGatewayDefinitionDto? Gateway { get; set; }
}

public sealed class DynamicFlowTopologyEdgeDto
{
    public string TransitionId { get; set; } = string.Empty;
    public string FromNodeId { get; set; } = string.Empty;
    public string ToNodeId { get; set; } = string.Empty;
    public DynamicFlowConditionDto? Condition { get; set; }
}

public sealed class DynamicFlowGatewayDefinitionDto
{
    public string Kind { get; set; } = string.Empty;
    public List<string> ExpectedIncomingNodeIds { get; set; } = new();
    public int? RequiredIncomingCount { get; set; }
    public string? ReviewRole { get; set; }
    public string? SubflowFamilyId { get; set; }
    public string? SubflowVersionId { get; set; }
    public string? ScheduleKey { get; set; }
    public string? RollbackTargetNodeId { get; set; }
}

/// <summary>
/// Declarative, non-executable condition AST. Script/expression text is not part
/// of this contract by design.
/// </summary>
public sealed class DynamicFlowConditionDto
{
    public string Operator { get; set; } = string.Empty;
    public string? Field { get; set; }
    public JsonElement? Value { get; set; }
    public List<JsonElement> Values { get; set; } = new();
    public List<DynamicFlowConditionDto> Children { get; set; } = new();
}

public static class DynamicFlowNodeKinds
{
    public const string FormStep = "FORM_STEP";
    public const string Gateway = "GATEWAY";
    public const string Final = "FINAL";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        FormStep,
        Gateway,
        Final
    };
}

public static class DynamicFlowGatewayKinds
{
    public const string Fork = "FORK";
    public const string JoinAll = "JOIN_ALL";
    public const string JoinAny = "JOIN_ANY";
    public const string JoinNOfM = "JOIN_N_OF_M";
    public const string Condition = "CONDITION";
    public const string Review = "REVIEW";
    public const string Subflow = "SUBFLOW";
    public const string Schedule = "SCHEDULE";
    public const string RollbackFinalize = "ROLLBACK_FINALIZE";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Fork,
        JoinAll,
        JoinAny,
        JoinNOfM,
        Condition,
        Review,
        Subflow,
        Schedule,
        RollbackFinalize
    };
}

public sealed class DynamicFlowStepDefinitionDto
{
    public string StepId { get; set; } = string.Empty;
    public string StepCode { get; set; } = string.Empty;
    public int? StepOrder { get; set; }
    public string? FormNodeId { get; set; }
    public string? DynamicFormTemplateId { get; set; }

}

public sealed class DynamicFlowTransitionDto
{
    public string? FromStepId { get; set; }
    public string? FromStepCode { get; set; }
    public string? ToStepId { get; set; }
    public string? ToStepCode { get; set; }

}

public sealed class DynamicFlowActorPolicyDto
{
    public string? PolicyId { get; set; }
    public string? StepId { get; set; }
    public string? StepCode { get; set; }
    public string? ActorRole { get; set; }
    public bool? AllowSubFlow { get; set; }
    public bool? AllowForward { get; set; }
    public bool? CanFinalize { get; set; }

}

public sealed class DynamicFlowFieldPolicyDto
{
    public string? PolicyId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? StepId { get; set; }
    public string? StepCode { get; set; }
    public string? ActorRole { get; set; }
    public string? FieldId { get; set; }
    public string? FieldKey { get; set; }
    public bool? Read { get; set; }
    public bool? Write { get; set; }
    public bool? Required { get; set; }
    public bool? Hidden { get; set; }
    public bool? Locked { get; set; }
    public bool? LockedAfterSubmit { get; set; }

}

public sealed class DynamicFlowTableColumnPolicyDto
{
    public string? PolicyId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? StepId { get; set; }
    public string? StepCode { get; set; }
    public string? ActorRole { get; set; }
    public string? BlockId { get; set; }
    public string? ColumnKey { get; set; }
    public bool? Read { get; set; }
    public bool? Write { get; set; }
    public bool? Required { get; set; }
    public bool? Hidden { get; set; }
    public bool? Locked { get; set; }
    public bool? LockedAfterSubmit { get; set; }

}
