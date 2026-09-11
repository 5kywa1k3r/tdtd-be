namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

/// <summary>
/// Pure P10 expected-ledger boundary. The compiler owns no database/query service
/// and receives no P9 projection, aggregate, result, API or export object.
/// </summary>
public interface IStatisticReconciliationExpectedLedgerCompiler
{
    StatisticReconciliationExpectedLedgerBoundInputs BindInputs(
        StatisticReconciliationExpectedLedgerCompileInput input);

    StatisticReconciliationExpectedLedgerDependencyGraph DescribeDependencies();
}
