using tdtd_be.Models;

namespace tdtd_be.Services.Common;

public sealed class DocRoleReadModelProjectionResilientService : IDocRoleReadModelProjectionService
{
    private readonly DocRoleReadModelProjectionService _inner;
    private readonly IDocRoleReadModelProjectionRetryJobService _retryJobs;

    public DocRoleReadModelProjectionResilientService(
        DocRoleReadModelProjectionService inner,
        IDocRoleReadModelProjectionRetryJobService retryJobs)
    {
        _inner = inner;
        _retryJobs = retryJobs;
    }

    public async Task RebuildWorkAsync(string workId, string byUserId, CancellationToken ct)
    {
        try
        {
            await _inner.RebuildWorkAsync(workId, byUserId, ct);
        }
        catch (Exception ex)
        {
            await _retryJobs.EnqueueRebuildWorkAsync(workId, byUserId, "projection-write-failed", ex, CancellationToken.None);
            throw;
        }
    }

    public async Task RebuildAssignmentAsync(string assignmentId, string byUserId, CancellationToken ct)
    {
        try
        {
            await _inner.RebuildAssignmentAsync(assignmentId, byUserId, ct);
        }
        catch (Exception ex)
        {
            await _retryJobs.EnqueueRebuildAssignmentAsync(assignmentId, byUserId, "projection-write-failed", ex, CancellationToken.None);
            throw;
        }
    }

    public async Task RebuildWorkAssignmentsAsync(string workId, string byUserId, CancellationToken ct)
    {
        try
        {
            await _inner.RebuildWorkAssignmentsAsync(workId, byUserId, ct);
        }
        catch (Exception ex)
        {
            await _retryJobs.EnqueueRebuildWorkAssignmentsAsync(workId, byUserId, "work-assignment-projection-write-failed", ex, CancellationToken.None);
            throw;
        }
    }

    public async Task RebuildReportPeriodAsync(string workReportPeriodId, string byUserId, CancellationToken ct)
    {
        try
        {
            await _inner.RebuildReportPeriodAsync(workReportPeriodId, byUserId, ct);
        }
        catch (Exception ex)
        {
            await _retryJobs.EnqueueRebuildReportPeriodAsync(workReportPeriodId, byUserId, "projection-write-failed", ex, CancellationToken.None);
            throw;
        }
    }

    public async Task RebuildWorkReportPeriodsAsync(string workId, string byUserId, CancellationToken ct)
    {
        try
        {
            await _inner.RebuildWorkReportPeriodsAsync(workId, byUserId, ct);
        }
        catch (Exception ex)
        {
            await _retryJobs.EnqueueRebuildWorkReportPeriodsAsync(workId, byUserId, "work-report-period-projection-write-failed", ex, CancellationToken.None);
            throw;
        }
    }

    public async Task RebuildMyReportTemplateAsync(
        string workId,
        string dynamicFormTemplateId,
        string userId,
        string byUserId,
        CancellationToken ct)
    {
        try
        {
            await _inner.RebuildMyReportTemplateAsync(workId, dynamicFormTemplateId, userId, byUserId, ct);
        }
        catch (Exception ex)
        {
            await _retryJobs.EnqueueRebuildMyReportTemplateAsync(workId, dynamicFormTemplateId, userId, byUserId, "projection-write-failed", ex, CancellationToken.None);
            throw;
        }
    }

    public async Task SoftDeleteByDocAsync(DocType docType, string docId, string byUserId, CancellationToken ct)
    {
        try
        {
            await _inner.SoftDeleteByDocAsync(docType, docId, byUserId, ct);
        }
        catch (Exception ex)
        {
            await _retryJobs.EnqueueSoftDeleteDocAsync(docType, docId, byUserId, "projection-soft-delete-failed", ex, CancellationToken.None);
            throw;
        }
    }
}
