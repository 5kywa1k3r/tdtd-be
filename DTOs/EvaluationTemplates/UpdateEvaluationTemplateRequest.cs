namespace tdtd_be.DTOs.EvaluationTemplates;

public sealed record UpdateEvaluationTemplateItemRequest(
    string Code,
    string Label,
    int? Order,
    bool? IsActive
);

public sealed record UpdateEvaluationTemplateRequest(
    string RepresentativeLabel,
    bool IsActive,
    string? UnitCodeScope,
    List<UpdateEvaluationTemplateItemRequest> Items
);
