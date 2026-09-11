using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Services;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignments.Lookups;

internal static class DynamicFormBindingAccessPolicyContractTests
{
    private const string OwnerId = "64b64c10aafbc4a5ec000021";
    private const string OtherId = "64b64c10aafbc4a5ec000022";
    private const string FormId = "64b64c10aafbc4a5ec000023";

    public static void Run()
    {
        OwnerAndSystemAdministratorMayBind();
        DeletedActorAndInactiveOrDeletedFormCannotBind();
        AdminRuntimeReaderAndCloneReaderDoNotGainBindPermission();
        ForbiddenFailureDoesNotLeakFormMetadata();
        HiddenFormAclWinsBeforeSnapshotValidation();
        PublishedFormIntegrityIsValidatedForAuthorizedActor();
        ActorIsThreadedThroughAssignmentAndFlowResolvers();
        ResolversProjectBindingAclBeforeMaterializingForms();
    }

    private static void OwnerAndSystemAdministratorMayBind()
    {
        var form = DraftForm();
        AssertTrue(
            DynamicFormBindingAccessPolicy.MayBind(User(OwnerId), form),
            "Dynamic Form owner must be allowed to mint a binding");
        AssertTrue(
            DynamicFormBindingAccessPolicy.MayBind(
                User(OtherId, roles: new[] { Roles.SYSTEM_ADMIN }),
                form),
            "SYSTEM_ADMIN role must be allowed to mint a binding");
        AssertTrue(
            DynamicFormBindingAccessPolicy.MayBind(
                User(OtherId, accountKind: ManagementAccountKind.SystemAdmin),
                form),
            "SYSTEM_ADMIN account kind must be allowed to mint a binding");
    }

    private static void AdminRuntimeReaderAndCloneReaderDoNotGainBindPermission()
    {
        var form = DraftForm();
        AssertFalse(
            DynamicFormBindingAccessPolicy.MayBind(
                User(OtherId, roles: new[] { Roles.ADMIN }),
                form),
            "ADMIN is not a Dynamic Form design/use grant");
        AssertFalse(
            DynamicFormBindingAccessPolicy.MayBind(User(OtherId), form),
            "runtime-read or clone-only users must remain unable to mint a binding");
    }

    private static void DeletedActorAndInactiveOrDeletedFormCannotBind()
    {
        var deletedActor = User(OwnerId);
        deletedActor.IsDeleted = true;
        AssertFalse(
            DynamicFormBindingAccessPolicy.MayBind(deletedActor, DraftForm()),
            "deleted actor must not mint a binding");

        var inactive = DraftForm();
        inactive.IsActive = false;
        AssertFalse(
            DynamicFormBindingAccessPolicy.MayBind(User(OwnerId), inactive),
            "inactive form must not be bound");

        var deleted = DraftForm();
        deleted.IsDeleted = true;
        AssertFalse(
            DynamicFormBindingAccessPolicy.MayBind(User(OwnerId), deleted),
            "deleted form must not be bound");
    }

    private static void ForbiddenFailureDoesNotLeakFormMetadata()
    {
        var form = DraftForm();
        var error = AssertThrows<AppException>(() =>
            DynamicFormBindingAccessPolicy.EnsureMayBind(User(OtherId), new[] { form }));

        AssertEqual(AppErrorCode.AUTH_FORBIDDEN, error.Code, "forbidden bind error code");
        AssertEqual(
            StatusCodes.Status403Forbidden,
            error.Descriptor.HttpStatus,
            "forbidden bind HTTP status");

        var detailsJson = JsonSerializer.Serialize(error.Details);
        AssertContains(
            detailsJson,
            DynamicFormBindingAccessPolicy.ForbiddenReason,
            "generic bind failure reason");
        AssertNotContains(detailsJson, form.Id, "hidden form id");
        AssertNotContains(detailsJson, form.Code, "hidden form code");
        AssertNotContains(detailsJson, form.Name, "hidden form name");
        AssertNotContains(detailsJson, form.CreatedByUserId!, "hidden form owner");
    }

    private static void HiddenFormAclWinsBeforeSnapshotValidation()
    {
        var hidden = DraftForm();
        hidden.IsPublished = true;
        hidden.PublishedSchemaSnapshotJson = "{\"invalid\":true}";
        hidden.PublishedSchemaHash = new string('0', 64);

        var error = AssertThrows<AppException>(() =>
            DynamicFormBindingAccessPolicy.EnsureMayBind(User(OtherId), new[] { hidden }));
        AssertEqual(
            AppErrorCode.AUTH_FORBIDDEN,
            error.Code,
            "hidden form must fail ACL before snapshot validation");
    }

    private static void PublishedFormIntegrityIsValidatedForAuthorizedActor()
    {
        var form = DraftForm();
        form.IsPublished = true;
        var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(
            schemaVersion: 1,
            sectionsJson: "[]",
            fieldsJson: "[]",
            blocksJson: "[]");
        form.PublishedSchemaSnapshotJson = snapshot.Json;
        form.PublishedSchemaHash = snapshot.Sha256;
        form.BlocksJson = "{not-json";

        var error = AssertThrows<AppException>(() =>
            DynamicFormBindingAccessPolicy.EnsureMayBind(User(OwnerId), new[] { form }));
        AssertEqual(
            AppErrorCode.DYNAMIC_FORM_PUBLISHED_SCHEMA_INTEGRITY_FAILED,
            error.Code,
            "authorized malformed published form integrity code");
        AssertEqual(
            StatusCodes.Status409Conflict,
            error.Descriptor.HttpStatus,
            "authorized malformed published form integrity HTTP status");
    }

    private static void ActorIsThreadedThroughAssignmentAndFlowResolvers()
    {
        var assignmentResolve = typeof(IWorkAssignmentTemplateResolver)
            .GetMethod(nameof(IWorkAssignmentTemplateResolver.ResolveAsync))
            ?? throw new InvalidOperationException("Assignment template resolver contract is missing.");
        AssertTrue(
            assignmentResolve.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(string) && parameter.Name == "actorUserId"),
            "assignment template resolution must receive the binding actor");

        var flowLoad = typeof(DynamicFlowTemplateService).GetMethod(
            "LoadDynamicFormTemplatesForPayloadAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Dynamic Flow form resolver contract is missing.");
        AssertTrue(
            flowLoad.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(string) && parameter.Name == "actorUserId"),
            "Dynamic Flow create/save/lock form resolution must receive the binding actor");
    }

    private static void ResolversProjectBindingAclBeforeMaterializingForms()
    {
        var flow = ReadSource("Services/DynamicFlows/DynamicFlowTemplateService.cs");
        var flowResolver = Slice(
            flow,
            "private async Task<Dictionary<string, DynamicFormTemplate>> LoadDynamicFormTemplatesForPayloadAsync(",
            "private static IEnumerable<string?> CollectDynamicFormTemplateIds(");
        AssertContains(
            flowResolver,
            "BuildMayBindFilter(",
            "Flow Form resolver Mongo ACL projection");
        AssertContains(
            flowResolver,
            "throw DynamicFormBindingAccessPolicy.Forbidden()",
            "Flow missing/hidden generic denial");
        AssertNotContains(
            flowResolver,
            "dynamicFormTemplateIds = missing",
            "Flow missing Form id oracle");

        var assignment = ReadSource(
            "Services/WorkAssignments/Lookups/WorkAssignmentTemplateResolver.cs");
        var assignmentResolver = Slice(
            assignment,
            "private async Task<WorkAssignmentTemplateResolution> ResolveDynamicFormAsync(",
            "private static string? ExtractExcelTemplateId(");
        AssertContains(
            assignmentResolver,
            "BuildMayBindFilter(",
            "assignment Form resolver Mongo ACL projection");
        AssertContains(
            assignmentResolver,
            "throw DynamicFormBindingAccessPolicy.Forbidden()",
            "assignment missing/hidden generic denial");
        var beforeExcelResolution = assignmentResolver[
            ..assignmentResolver.IndexOf("var excelId", StringComparison.Ordinal)];
        AssertNotContains(
            beforeExcelResolution,
            "AppExceptionFactory.NotFound",
            "assignment Form existence oracle before binding ACL");
    }

    private static DynamicFormTemplate DraftForm()
        => new()
        {
            Id = FormId,
            Code = "HIDDEN-FORM",
            Name = "Hidden form metadata",
            CreatedByUserId = OwnerId,
            IsActive = true,
            IsPublished = false,
            IsDeleted = false
        };

    private static AppUser User(
        string id,
        IEnumerable<string>? roles = null,
        string? accountKind = null)
        => new()
        {
            Id = id,
            Roles = roles?.ToList() ?? new List<string>(),
            AccountKind = accountKind,
            IsDeleted = false
        };

    private static Exception AssertThrowsAny(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            return error;
        }

        throw new InvalidOperationException("Expected an exception was not thrown.");
    }

    private static TException AssertThrows<TException>(Action action)
        where TException : Exception
    {
        var error = AssertThrowsAny(action);
        if (error is TException expected)
            return expected;

        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name}, got {error.GetType().Name}.",
            error);
    }

    private static void AssertContains(string actual, string expected, string context)
    {
        if (!actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{context}: expected '{actual}' to contain '{expected}'.");
    }

    private static void AssertNotContains(string actual, string expected, string context)
    {
        if (actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{context}: '{actual}' must not contain '{expected}'.");
    }

    private static void AssertEqual<T>(T expected, T actual, string context)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected '{expected}', got '{actual}'.");
    }

    private static void AssertTrue(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool value, string message)
        => AssertTrue(!value, message);

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex + Math.Max(start.Length, 1), StringComparison.Ordinal);
        AssertTrue(startIndex >= 0 && endIndex > startIndex, $"source slice not found: {start} .. {end}");
        return source[startIndex..endIndex];
    }

    private static string ReadSource(string relativePath)
    {
        var relative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
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
}
