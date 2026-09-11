using System.Reflection;
using System.Text.Json;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Services;

internal static class UserAdminRolePrecedenceContractTests
{
    private const string ActorId = "64f000000000000000000001";
    private const string TargetId = "64f000000000000000000002";

    public static void Run()
    {
        var dualRoleSystemAdmin = CreateActor([Roles.ADMIN, Roles.SYSTEM_ADMIN], "SYSTEM_ADMIN");
        AssertAllowed(
            dualRoleSystemAdmin,
            CreateTarget([Roles.MANAGER_LEVEL]),
            "A dual-role SYSTEM_ADMIN must use SYSTEM_ADMIN management authority.");

        var adminOnly = CreateActor([Roles.ADMIN]);
        var forbidden = AssertForbidden(
            adminOnly,
            CreateTarget([Roles.MANAGER_LEVEL]));
        Assert(
            forbidden.Code == AppErrorCode.USER_ADMIN_MANAGE_FORBIDDEN,
            "ADMIN-only denial must retain USER_ADMIN_MANAGE_FORBIDDEN.");
        Assert(
            JsonSerializer.Serialize(forbidden.Details)
                .Contains("adminCanOnlyManageSystemAdmin", StringComparison.Ordinal),
            "ADMIN-only denial must retain adminCanOnlyManageSystemAdmin reason.");

        AssertAllowed(
            adminOnly,
            CreateTarget([Roles.SYSTEM_ADMIN], "SYSTEM_ADMIN"),
            "ADMIN-only authority must continue to manage SYSTEM_ADMIN accounts.");
    }

    private static MeResponse CreateActor(List<string> roles, string? accountKind = null)
        => new(
            ActorId,
            "owner",
            "Owner",
            [],
            "",
            null,
            null,
            null,
            roles,
            null,
            false,
            accountKind);

    private static AppUser CreateTarget(List<string> roles, string? accountKind = null)
        => new()
        {
            Id = TargetId,
            Username = "target",
            FullName = "Target",
            Roles = roles,
            AccountKind = accountKind
        };

    private static void AssertAllowed(MeResponse actor, AppUser target, string message)
    {
        try
        {
            InvokeGuard(actor, target);
        }
        catch (TargetInvocationException error) when (error.InnerException is AppException appError)
        {
            throw new InvalidOperationException(
                $"{message} Unexpected {appError.Code}: {JsonSerializer.Serialize(appError.Details)}",
                appError);
        }
    }

    private static AppException AssertForbidden(MeResponse actor, AppUser target)
    {
        try
        {
            InvokeGuard(actor, target);
        }
        catch (TargetInvocationException error) when (error.InnerException is AppException appError)
        {
            return appError;
        }

        throw new InvalidOperationException("Expected user-management denial was not thrown.");
    }

    private static void InvokeGuard(MeResponse actor, AppUser target)
    {
        var guard = typeof(UserAdminService).GetMethod(
            "RequireCanManageUser",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(UserAdminService), "RequireCanManageUser");
        guard.Invoke(null, [actor, target]);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
