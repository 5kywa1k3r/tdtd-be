namespace tdtd_be.DTOs.AggregateMapping;

public sealed record AggregateEditorBootstrapDto(string? ReportId, AggregatePeriodContextDto? ViewContext = null);
public sealed record AggregateEditorSchemaQueryDto(AggregatePeriodContextDto Context, AggregateFormPinDto Form);
public sealed record AggregateConfigReadCommandDto(AggregatePeriodContextDto Context, long? Revision = null);
public sealed record AggregateEditorOptionDto(string Code, string Label);
public sealed record AggregateEditorSourceFormDto(string FormId, string FamilyId, int VersionNo, string SchemaHash, string Name);
public sealed record AggregateEditorMemberDto(string Id, string Name, string? SectionId, string SourceType,
    string ValueType, bool Supported, string? ReasonCode, IReadOnlyList<AggregateEditorOptionDto> Options,
    AggregateEditorTableDto? Table = null, AggregateEditorListDto? List = null);
public sealed record AggregateEditorListFieldDto(string Id, string Name, string ValueType, string? CatalogId,
    IReadOnlyList<AggregateEditorOptionDto> Options, bool Required,
    IReadOnlyList<AggregateEditorListSpecialOptionDto> SpecialOptions);
public sealed record AggregateEditorListSpecialOptionDto(string Kind, string Label, AggregateListPredicateDto Predicate);
public sealed record AggregateEditorListDto(string ItemLabel, IReadOnlyList<AggregateEditorListFieldDto> Fields);
public sealed record AggregateEditorTableAxisDto(string Id, string Name);
public sealed record AggregateEditorTableDto(string Layout, IReadOnlyList<AggregateEditorTableAxisDto> Columns,
    IReadOnlyList<AggregateEditorTableAxisDto> Rows, IReadOnlyList<IReadOnlyList<string>> CellTypes,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<AggregateEditorOptionDto>>> CellOptions);
public sealed record AggregateEditorSectionDto(string Id, string Title);
public sealed record AggregateEditorFormDto(AggregateFormPinDto Pin, string Name,
    IReadOnlyList<AggregateEditorSectionDto> Sections, IReadOnlyList<AggregateEditorMemberDto> Members,
    AggregateEditorReportOptionsDto? ReportOptions = null);
public sealed record AggregateEditorReportOptionsDto(IReadOnlyList<AggregateEditorOptionDto> Periods,
    IReadOnlyList<AggregateEditorOptionDto> Units);
