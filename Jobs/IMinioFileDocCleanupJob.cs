using System.Threading;
using System.Threading.Tasks;

namespace tdtd_be.Jobs;

public interface IMinioFileDocCleanupJob
{
    Task RunAsync(CancellationToken cancellationToken = default);
}
