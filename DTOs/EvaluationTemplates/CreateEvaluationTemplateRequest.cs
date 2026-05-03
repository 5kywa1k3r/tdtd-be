namespace tdtd_be.DTOs.EvaluationTemplates;

public sealed record CreateEvaluationTemplateItemRequest(
    string Code,
    string Label,
    int? Order,
    bool? IsActive
);

public sealed record CreateEvaluationTemplateRequest(
    string RepresentativeCode,
    string RepresentativeLabel,
    string? UnitCodeScope,
    List<CreateEvaluationTemplateItemRequest> Items
);
