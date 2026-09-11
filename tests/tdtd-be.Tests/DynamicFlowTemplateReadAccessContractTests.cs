using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Controllers;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowTemplateReadAccessContractTests
{
    private const string ActorUserId = "507f1f77bcf86cd799439011";
    private const string OtherUserId = "507f191e810c19729de860ea";
    private const string TemplateId = "507f1f77bcf86cd799439012";
    private const string OtherTemplateId = "507f1f77bcf86cd799439013";
    private const int VersionNo = 3;

    public static void Run()
    {
        AssertRoleAndOwnerRules();
        AssertAssignmentParticipantRules();
        AssertLaunchRules();
        AssertSearchBulkProjectsReadableVersions();
        AssertControllerForwardsActorToEveryRead();
        AssertCreateIdempotencyHeaderContract();
    }

    private static void AssertRoleAndOwnerRules()
    {
        Require(
            DynamicFlowTemplateReadAccess.IsAdministrator(new AppUser { Roles = new List<string> { "admin" } }),
            "ADMIN must read every Dynamic Flow template");
        Require(
            DynamicFlowTemplateReadAccess.IsAdministrator(new AppUser { Roles = new List<string> { "SYSTEM_ADMIN" } }),
            "SYSTEM_ADMIN must read every Dynamic Flow template");
        Require(
            !DynamicFlowTemplateReadAccess.IsAdministrator(new AppUser { Roles = new List<string> { "USER" } }),
            "ordinary roles must not receive administrator read access");
        Require(
            DynamicFlowTemplateReadAccess.CanCreateDefinition(new AppUser
            {
                UnitId = TemplateId,
                Roles = new List<string> { $"MANAGER_UNIT:{TemplateId}" }
            }),
            "a scoped manager role must create definitions only for the actor's exact unit");
        Require(
            !DynamicFlowTemplateReadAccess.CanCreateDefinition(new AppUser
            {
                UnitId = TemplateId,
                Roles = new List<string> { $"MANAGER_UNIT:{OtherTemplateId}" }
            }),
            "a manager role for another unit must not create definitions");
        Require(
            !DynamicFlowTemplateReadAccess.CanCreateDefinition(new AppUser
            {
                UnitId = TemplateId,
                Roles = new List<string> { "MANAGER_UNIT" }
            }),
            "an unscoped manager-unit prefix must fail closed");

        var template = new DynamicFlowTemplate { CreatedByUserId = ActorUserId };
        Require(
            DynamicFlowTemplateReadAccess.IsOwner(template, ActorUserId),
            "template creator must be recognized as owner");
        Require(
            !DynamicFlowTemplateReadAccess.IsOwner(template, OtherUserId),
            "unrelated user must not be recognized as owner");
    }

    private static void AssertAssignmentParticipantRules()
    {
        RequireParticipant(new WorkAssignment { CreatedByUserId = ActorUserId }, "assignment creator");
        RequireParticipant(
            new WorkAssignment
            {
                Assignees = new List<UserRef> { new() { UserId = ActorUserId } }
            },
            "assignment assignee");
        RequireParticipant(
            new WorkAssignment
            {
                LeaderWatcherUserIds = new List<string> { ActorUserId }
            },
            "assignment watcher");

        var unrelated = Assignment(new WorkAssignment
        {
            CreatedByUserId = OtherUserId,
            Assignees = new List<UserRef> { new() { UserId = OtherUserId } },
            LeaderWatcherUserIds = new List<string> { OtherUserId }
        });
        Require(
            !DynamicFlowTemplateReadAccess.IsAssignmentParticipant(unrelated, TemplateId, ActorUserId),
            "unrelated user must not receive assignment-based template access");

        var deleted = Assignment(new WorkAssignment { CreatedByUserId = ActorUserId });
        deleted.IsDeleted = true;
        Require(
            !DynamicFlowTemplateReadAccess.IsAssignmentParticipant(deleted, TemplateId, ActorUserId),
            "deleted assignments must not grant template access");

        var inactive = Assignment(new WorkAssignment { CreatedByUserId = ActorUserId });
        inactive.IsActive = false;
        Require(
            !DynamicFlowTemplateReadAccess.IsAssignmentParticipant(inactive, TemplateId, ActorUserId),
            "inactive assignments must not grant template access");

        var invalidated = Assignment(new WorkAssignment { CreatedByUserId = ActorUserId });
        invalidated.FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Invalidated;
        Require(
            !DynamicFlowTemplateReadAccess.IsAssignmentParticipant(invalidated, TemplateId, ActorUserId),
            "invalidated flow branches must not grant template access");

        var terminated = Assignment(new WorkAssignment { CreatedByUserId = ActorUserId });
        terminated.FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Terminated;
        Require(
            !DynamicFlowTemplateReadAccess.IsAssignmentParticipant(terminated, TemplateId, ActorUserId),
            "terminated flow branches must not grant template access");

        var missingVersion = Assignment(new WorkAssignment { CreatedByUserId = ActorUserId });
        missingVersion.FlowTemplateVersionNo = null;
        Require(
            !DynamicFlowTemplateReadAccess.IsAssignmentParticipant(missingVersion, TemplateId, ActorUserId),
            "assignments without a flow version must fail closed");

        var wrongVersion = Assignment(new WorkAssignment { CreatedByUserId = ActorUserId });
        Require(
            !DynamicFlowTemplateReadAccess.IsAssignmentParticipant(
                wrongVersion,
                TemplateId,
                ActorUserId,
                VersionNo + 1),
            "an assignment must not grant access to a different flow version");

        var wrongTemplate = Assignment(new WorkAssignment { CreatedByUserId = ActorUserId });
        Require(
            !DynamicFlowTemplateReadAccess.IsAssignmentParticipant(wrongTemplate, OtherTemplateId, ActorUserId),
            "an assignment must not grant access to a different template");
    }

    private static void AssertLaunchRules()
    {
        var template = new DynamicFlowTemplate { Id = TemplateId, CreatedByUserId = ActorUserId };
        var version = new DynamicFlowTemplateVersion
        {
            Id = OtherTemplateId,
            TemplateId = TemplateId,
            VersionNo = VersionNo
        };
        var owner = new AppUser { Id = ActorUserId, Roles = new List<string> { "USER" } };
        var administrator = new AppUser { Id = OtherUserId, Roles = new List<string> { "SYSTEM_ADMIN" } };
        var outsider = new AppUser { Id = OtherUserId, Roles = new List<string> { "USER" } };
        var participantParent = Assignment(new WorkAssignment
        {
            Id = "507f1f77bcf86cd799439014",
            Assignees = new List<UserRef> { new() { UserId = OtherUserId } }
        });

        Require(
            !DynamicFlowRuntimeService.CanLaunchTemplate(template, version, parent: null, actor: owner),
            "P4 must not let a template owner launch a definition");
        Require(
            !DynamicFlowRuntimeService.CanLaunchTemplate(template, version, parent: null, actor: administrator),
            "P4 must not let an administrator launch a definition");
        Require(
            !DynamicFlowRuntimeService.CanLaunchTemplate(template, version, parent: null, actor: outsider),
            "an unrelated actor must not launch a root flow");
        Require(
            !DynamicFlowRuntimeService.CanLaunchTemplate(template, version, participantParent, outsider),
            "P4 eligibility must block even an effective exact-version participant");
        Require(
            DynamicFlowTemplateReadAccess.HasBusinessExecuteGrant(participantParent, TemplateId, OtherUserId, VersionNo),
            "an effective exact-version participant must retain a separate business execute grant");

        participantParent.FlowTemplateVersionNo = VersionNo + 1;
        Require(
            !DynamicFlowTemplateReadAccess.HasBusinessExecuteGrant(participantParent, TemplateId, OtherUserId, VersionNo),
            "a business execute grant must match the exact flow version");
    }

    private static void AssertSearchBulkProjectsReadableVersions()
    {
        var source = ReadSource("Services/DynamicFlows/DynamicFlowTemplateService.cs");
        var search = Slice(
            source,
            "public async Task<PagedResult<DynamicFlowTemplateDto>> SearchAsync(",
            "public async Task<DynamicFlowTemplateDto> GetAsync(");

        Require(
            search.Contains("versionFilterBuilder.In(version => version.TemplateId, pageTemplateIds)", StringComparison.Ordinal),
            "search must fetch versions for the bounded current-page family ids in one bulk query");
        Require(
            search.Contains("versionFilterBuilder.Eq(version => version.Status, DynamicFlowTemplateVersionStatuses.Draft)", StringComparison.Ordinal) &&
            search.Contains("versionFilterBuilder.In(version => version.Id, unrestrictedCurrentVersionIds)", StringComparison.Ordinal),
            "owner/admin search must project only current and draft summaries instead of unbounded history");
        Require(
            search.Contains("assignmentAccessFilter", StringComparison.Ordinal) &&
            search.Contains("assignmentFilterBuilder.Or(assignmentFilters)", StringComparison.Ordinal),
            "search must bulk-project used-version metadata for the current page");
        Require(
            search.Contains("versionFilterBuilder.Eq(version => version.Status, DynamicFlowTemplateVersionStatuses.Locked)", StringComparison.Ordinal) &&
            search.Contains("versionFilterBuilder.In(version => version.VersionNo, grantedVersionNos)", StringComparison.Ordinal) &&
            search.Contains("grantedVersionNos.Contains(version.VersionNo)", StringComparison.Ordinal),
            "participant search projection must enforce exact locked-version scoping in Mongo and in memory");
        Require(
            search.Contains("ApplySearchTemplatePermissions(", StringComparison.Ordinal) &&
            !search.Contains("ApplyTemplatePermissionsAsync(", StringComparison.Ordinal),
            "search permissions must use preloaded page/grant state instead of per-row queries");
        Require(
            search.Contains("MapTemplate(", StringComparison.Ordinal) &&
            search.Contains("visibleVersions", StringComparison.Ordinal),
            "owner/admin search rows must receive their readable draft/current version projection");
    }

    private static void AssertControllerForwardsActorToEveryRead()
    {
        var service = new CapturingService();
        var controller = new DynamicFlowTemplatesController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(new[] { new Claim("sub", ActorUserId) }, "contract-test"))
                }
            }
        };

        _ = controller.Search(new DynamicFlowTemplateSearchRequest(), CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Require(service.SearchActorUserId == ActorUserId, "search must forward the authenticated actor");

        _ = controller.Get(TemplateId, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Require(service.GetActorUserId == ActorUserId, "get must forward the authenticated actor");

        _ = controller.ListVersions(TemplateId, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Require(service.ListVersionsActorUserId == ActorUserId, "version listing must forward the authenticated actor");

        _ = controller.GetVersion(TemplateId, "507f1f77bcf86cd799439015", CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Require(service.GetVersionActorUserId == ActorUserId, "version detail must forward the authenticated actor");

        _ = controller.DiffVersions(
                TemplateId,
                new DiffDynamicFlowTemplateVersionsRequest(),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Require(service.DiffVersionsActorUserId == ActorUserId, "version diff must forward the authenticated actor");
    }

    private static void AssertCreateIdempotencyHeaderContract()
    {
        var service = new CapturingService();
        var controller = new DynamicFlowTemplatesController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(new[] { new Claim("sub", ActorUserId) }, "contract-test"))
                }
            }
        };

        _ = controller.Create(
                new CreateDynamicFlowTemplateRequest(),
                CancellationToken.None,
                " command-from-header ")
            .GetAwaiter()
            .GetResult();
        Require(
            service.CreateRequest?.CommandId == "command-from-header",
            "Idempotency-Key must populate an omitted create commandId");

        _ = controller.Create(
                new CreateDynamicFlowTemplateRequest { CommandId = " same-command " },
                CancellationToken.None,
                "same-command")
            .GetAwaiter()
            .GetResult();
        Require(
            service.CreateRequest?.CommandId == "same-command",
            "equal body/header command ids must normalize to one command id");

        try
        {
            _ = controller.Create(
                    new CreateDynamicFlowTemplateRequest { CommandId = "body-command" },
                    CancellationToken.None,
                    "header-command")
                .GetAwaiter()
                .GetResult();
            throw new InvalidOperationException("different create body/header command ids must be rejected");
        }
        catch (tdtd_be.Common.Errors.AppException ex)
        {
            Require(
                ex.Code == tdtd_be.Common.Errors.AppErrorCode.DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT,
                "different body/header command ids must use the stable replay-conflict envelope");
        }
    }

    private static void RequireParticipant(WorkAssignment assignment, string relationship)
    {
        assignment = Assignment(assignment);
        Require(
            DynamicFlowTemplateReadAccess.IsAssignmentParticipant(assignment, TemplateId, ActorUserId),
            $"{relationship} must receive assignment-based template access");
    }

    private static WorkAssignment Assignment(WorkAssignment assignment)
    {
        assignment.FlowTemplateId = TemplateId;
        assignment.FlowTemplateVersionNo = VersionNo;
        assignment.FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Effective;
        assignment.IsActive = true;
        assignment.IsDeleted = false;
        return assignment;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex + Math.Max(start.Length, 1), StringComparison.Ordinal);
        Require(startIndex >= 0 && endIndex > startIndex, $"source slice not found: {start} .. {end}");
        return source[startIndex..endIndex];
    }

    private static string ReadSource(string relativePath)
    {
        var relative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, relative);
            if (File.Exists(direct))
                return File.ReadAllText(direct);

            var nested = Path.Combine(directory.FullName, "tdtd-be", relative);
            if (File.Exists(nested))
                return File.ReadAllText(nested);
        }
        throw new FileNotFoundException($"Unable to locate backend source '{relativePath}'.");
    }

    private sealed class CapturingService : IDynamicFlowTemplateService
    {
        public string? SearchActorUserId { get; private set; }
        public string? GetActorUserId { get; private set; }
        public string? ListVersionsActorUserId { get; private set; }
        public string? GetVersionActorUserId { get; private set; }
        public string? DiffVersionsActorUserId { get; private set; }
        public CreateDynamicFlowTemplateRequest? CreateRequest { get; private set; }

        public Task<PagedResult<DynamicFlowTemplateDto>> SearchAsync(
            DynamicFlowTemplateSearchRequest req,
            string actorUserId,
            CancellationToken ct = default)
        {
            SearchActorUserId = actorUserId;
            return Task.FromResult(new PagedResult<DynamicFlowTemplateDto>(
                new List<DynamicFlowTemplateDto>(),
                0,
                0,
                20));
        }

        public Task<DynamicFlowTemplateDto> GetAsync(
            string id,
            string actorUserId,
            CancellationToken ct = default)
        {
            GetActorUserId = actorUserId;
            return Task.FromResult(new DynamicFlowTemplateDto());
        }

        public Task<List<DynamicFlowTemplateVersionDto>> ListVersionsAsync(
            string templateId,
            string actorUserId,
            CancellationToken ct = default)
        {
            ListVersionsActorUserId = actorUserId;
            return Task.FromResult(new List<DynamicFlowTemplateVersionDto>());
        }

        public Task<DynamicFlowTemplateVersionDto> GetVersionAsync(
            string templateId,
            string versionId,
            string actorUserId,
            CancellationToken ct = default)
        {
            GetVersionActorUserId = actorUserId;
            return Task.FromResult(new DynamicFlowTemplateVersionDto());
        }

        public Task<DynamicFlowTemplateDto> CreateAsync(
            CreateDynamicFlowTemplateRequest req,
            string actorUserId,
            CancellationToken ct = default)
        {
            CreateRequest = req;
            return Task.FromResult(new DynamicFlowTemplateDto());
        }

        public Task<DynamicFlowTemplateDto> UpdateAsync(
            string id,
            UpdateDynamicFlowTemplateRequest req,
            string actorUserId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DynamicFlowTemplateVersionDto> SaveDraftVersionAsync(
            string templateId,
            string versionId,
            SaveDynamicFlowTemplateVersionDraftRequest req,
            string actorUserId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DynamicFlowTemplateVersionDto> SaveDraftVersionAsync(
            string templateId,
            SaveDynamicFlowTemplateVersionDraftRequest req,
            string actorUserId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DynamicFlowTemplateVersionDto> LockVersionAsync(
            string templateId,
            string versionId,
            LockDynamicFlowTemplateVersionRequest req,
            string actorUserId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DynamicFlowTemplateVersionDto> LockVersionAsync(
            string versionId,
            LockDynamicFlowTemplateVersionRequest req,
            string actorUserId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DynamicFlowTemplateDto> ArchiveAsync(
            string templateId,
            ArchiveDynamicFlowTemplateRequest req,
            string actorUserId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DynamicFlowTemplateDto> CloneAsync(
            string templateId,
            CloneDynamicFlowTemplateRequest req,
            string actorUserId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DynamicFlowTemplateVersionDto> ReopenVersionAsync(
            string templateId,
            string versionId,
            ReopenDynamicFlowTemplateVersionRequest req,
            string actorUserId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DiffDynamicFlowTemplateVersionsDto> DiffVersionsAsync(
            string templateId,
            DiffDynamicFlowTemplateVersionsRequest req,
            string actorUserId,
            CancellationToken ct = default)
        {
            DiffVersionsActorUserId = actorUserId;
            return Task.FromResult(new DiffDynamicFlowTemplateVersionsDto());
        }

        public Task DeleteAsync(
            string id,
            DeleteDynamicFlowTemplateRequest req,
            string actorUserId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(
            string id,
            string actorUserId,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
