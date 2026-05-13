using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;

namespace tdtd_be.Jobs;

public sealed class HangfireSucceededJobExpirationFilter : JobFilterAttribute, IApplyStateFilter
{
    private readonly TimeSpan _expirationTimeout;

    public HangfireSucceededJobExpirationFilter(TimeSpan expirationTimeout)
    {
        _expirationTimeout = expirationTimeout;
    }

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        if (string.Equals(context.NewState?.Name, SucceededState.StateName, StringComparison.Ordinal))
            context.JobExpirationTimeout = _expirationTimeout;
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }
}
