namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

/// <summary>
/// Server-only producer boundary for the opaque five-input token. P10-T09 freezes
/// the contract but intentionally provides no implementation: lifecycle candidate
/// selection and authoritative owner reads begin at P10-T10.
/// </summary>
internal interface IStatisticReconciliationExpectedLedgerAuthoritativeInputProvider
{
    ValueTask<StatisticReconciliationExpectedLedgerCompileInput> ReadAsync(
        ExpectedLedgerCompilationContextPin requestedContext,
        CancellationToken cancellationToken);
}
