using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Services.Common;

namespace tdtd_be.Services.Works
{
    public interface IWorkPermissionService
    {
        void EnsureCanCreateRoot(MeResponse me);
        Task EnsureCanReadAsync(string workId, string userId, CancellationToken ct);
        Task EnsureCanUpdateRootAsync(string workId, string userId, CancellationToken ct);
        Task EnsureCanDeleteRootAsync(string workId, string userId, CancellationToken ct);
    }

    public sealed class WorkPermissionService : IWorkPermissionService
    {
        private readonly IDocRoleService _docRole;

        public WorkPermissionService(IDocRoleService docRole)
        {
            _docRole = docRole;
        }

        public void EnsureCanCreateRoot(MeResponse me)
        {
            if (me == null ||
                string.IsNullOrWhiteSpace(me.Id) ||
                !RoleGuard.IsGeneratedManagementAccount(me))
                throw AppExceptionFactory.Forbidden(AppErrorCode.WORK_CREATE_FORBIDDEN);
        }

        public async Task EnsureCanReadAsync(string workId, string userId, CancellationToken ct)
        {
            var ok = await _docRole.HasAnyRoleAsync(DocType.WORK, workId, userId, ct);
            if (!ok)
                throw AppExceptionFactory.Forbidden(AppErrorCode.WORK_READ_FORBIDDEN, new { workId });
        }

        public async Task EnsureCanUpdateRootAsync(string workId, string userId, CancellationToken ct)
        {
            var ok = await _docRole.HasRoleAsync(DocType.WORK, workId, userId, DocRoleType.OWNER, ct);
            if (!ok)
                throw AppExceptionFactory.Forbidden(AppErrorCode.WORK_UPDATE_FORBIDDEN, new { workId });
        }

        public async Task EnsureCanDeleteRootAsync(string workId, string userId, CancellationToken ct)
        {
            var ok = await _docRole.HasRoleAsync(DocType.WORK, workId, userId, DocRoleType.OWNER, ct);
            if (!ok)
                throw AppExceptionFactory.Forbidden(AppErrorCode.WORK_DELETE_FORBIDDEN, new { workId });
        }
    }
}
