using MongoDB.Driver;
using tdtd_be.Services.DynamicFlows;

// Observe escaped transaction failures in the private load host without changing retry behavior.
internal sealed class ScaleDiagnosticTransactions(IDynamicFlowDefinitionTransactionRunner inner) : IDynamicFlowDefinitionTransactionRunner
{
    public async Task ExecuteAsync(Func<IClientSessionHandle,CancellationToken,Task> operation,CancellationToken ct=default)
    {
        try{await inner.ExecuteAsync(operation,ct);}catch(Exception ex){Console.Error.WriteLine("SCALE_TRANSACTION_FAILURE "+ex);throw;}
    }
    public async Task<T> ExecuteAsync<T>(Func<IClientSessionHandle,CancellationToken,Task<T>> operation,CancellationToken ct=default)
    {
        try{return await inner.ExecuteAsync(operation,ct);}catch(Exception ex){Console.Error.WriteLine("SCALE_TRANSACTION_FAILURE "+ex);throw;}
    }
}
