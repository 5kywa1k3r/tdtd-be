namespace tdtd_be.DTOs.AggregateMapping;

public sealed record AggregateConfigCreateCommandDto(AggregatePeriodContextDto Context, AggregateConfigCreateRequestDto Config);
public sealed record AggregateInstanceCreateCommandDto(string CommandId, AggregatePeriodContextDto Context, string ConfigId);
public sealed record AggregateMappingChangeDto(long ExpectedRevision, AggregateInstanceOverrideDto? Overrides,
    AggregateInstanceSelectionDto Selection, List<string> UnlinkedMembers, bool ResetToPinned);
public sealed record AggregateMappingPreviewCommandDto(AggregatePeriodContextDto Context, AggregateMappingChangeDto Change);
public sealed record AggregateMappingApplyCommandDto(string CommandId, AggregatePeriodContextDto Context,
    AggregateMappingChangeDto Change, string ConfirmationToken);
public sealed record AggregateConfigImpactCommandDto(AggregatePeriodContextDto Context, AggregateConfigImpactRequestDto Impact);
public sealed record AggregateConfigRevisionCommandDto(string CommandId, AggregatePeriodContextDto Context,
    AggregateConfigImpactRequestDto Impact, string ConfirmationToken);
public sealed record AggregateUnlinkPreviewCommandDto(AggregatePeriodContextDto Context, long ExpectedRevision);
public sealed record AggregateUnlinkAllCommandDto(string CommandId, AggregatePeriodContextDto Context, long ExpectedRevision, string ConfirmationToken);
public sealed record AggregateRawDraftCommandDto(string CommandId, AggregatePeriodContextDto Context, long ExpectedRevision, string RawJson);
public sealed record AggregateDeclarationPreviewCommandDto(AggregatePeriodContextDto Context, AggregateDataWindowDeclarationDto Declaration);
public sealed record AggregateDeclarationSaveCommandDto(string CommandId, AggregatePeriodContextDto Context,
    AggregateDataWindowDeclarationDto Declaration, string ConfirmationToken);
public sealed record AggregateInstanceReadCommandDto(AggregatePeriodContextDto Context);
