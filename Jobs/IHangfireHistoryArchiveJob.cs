namespace tdtd_be.Jobs;

public interface IHangfireHistoryArchiveJob
{
    Task RunAsync(CancellationToken ct = default);
}
