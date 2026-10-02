namespace tdtd_be.DTOs.AggregateMapping;

// Unsaved P02 authoring queries. P03 persisted config queries use the P01 config pins.
public sealed record AggregateDraftSourceQueryDto(AggregatePeriodContextDto Context,
    AggregateFormPinDto Form, string? Cursor, int PageSize);
public sealed record AggregateSourceFormsQueryDto(AggregatePeriodContextDto Context);
