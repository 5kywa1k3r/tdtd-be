namespace tdtd_be.DTOs.EvaluationTemplates;

public sealed record EvaluationTemplateItemDto(
    string Code,
    string Label,
    int Order,
    bool? IsActive
);

public sealed record EvaluationTemplateDto(
    string Id,
    string RepresentativeCode,
    string RepresentativeLabel,
    int ItemCount,
    bool IsActive,
    string? UnitCodeScope,
    List<EvaluationTemplateItemDto> Items
);
