using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services;

namespace tdtd_be.Services.DynamicForms;

/// <summary>
/// Authorizes design-time use of an exact Dynamic Form version by assignments
/// and Dynamic Flow templates. Runtime participation and clone approval are read
/// or copy grants only. Exact inherited child bindings are authorized separately
/// by WorkAssignmentTemplateResolver within the same parent/work scope.
/// </summary>
internal static class DynamicFormBindingAccessPolicy
{
    internal const string ForbiddenReason = "DYNAMIC_FORM_BIND_FORBIDDEN";

    internal static bool MayBind(AppUser? actor, DynamicFormTemplate? form)
    {
        if (actor is null ||
            form is null ||
            actor.IsDeleted ||
            form.IsDeleted ||
            !form.IsActive ||
            string.IsNullOrWhiteSpace(actor.Id))
            return false;

        return string.Equals(form.CreatedByUserId, actor.Id, StringComparison.Ordinal) ||
               IsSystemAdministrator(actor);
    }

    internal static void EnsureMayBind(
        AppUser actor,
        IEnumerable<DynamicFormTemplate> forms)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(forms);

        var materialized = forms
            .Where(form => form is not null)
            .DistinctBy(form => form.Id, StringComparer.Ordinal)
            .ToList();

        // Check the complete set before inspecting snapshot integrity. A mixed
        // payload must not reveal metadata about a hidden form through a more
        // specific validation error.
        if (materialized.Any(form => !MayBind(actor, form)))
            throw Forbidden();

        // Published snapshot integrity is validated here, after ACL and before
        // any caller can enrich provenance or persist a new runtime binding.
        foreach (var form in materialized.Where(form => form.IsPublished))
            EnsurePublishedIntegrity(form);
    }

    // The caller must establish binding authorization before inspecting integrity.
    internal static void EnsurePublishedIntegrity(DynamicFormTemplate form)
    {
        try
            {
                DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(form);
            }
            catch (InvalidOperationException ex)
            {
                throw new AppException(
                    AppErrorCode.DYNAMIC_FORM_PUBLISHED_SCHEMA_INTEGRITY_FAILED,
                    new
                    {
                        dynamicFormTemplateId = form.Id,
                        reason = "PUBLISHED_SCHEMA_SNAPSHOT_OR_LIVE_STRUCTURE_MISMATCH"
                    },
                    innerException: ex);
            }
    }

    /// <summary>
    /// Builds the ACL projection used before a Form document is materialized.
    /// A missing identifier and an existing-but-hidden identifier therefore
    /// produce the same generic denial envelope.
    /// </summary>
    internal static FilterDefinition<DynamicFormTemplate> BuildMayBindFilter(
        AppUser actor,
        IReadOnlyCollection<string> formIds,
        bool requirePublished)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(formIds);

        var fb = Builders<DynamicFormTemplate>.Filter;
        var filter = fb.In(form => form.Id, formIds) &
                     fb.Eq(form => form.IsDeleted, false) &
                     fb.Eq(form => form.IsActive, true);
        if (requirePublished)
            filter &= fb.Eq(form => form.IsPublished, true);
        if (!IsSystemAdministrator(actor))
            filter &= fb.Eq(form => form.CreatedByUserId, actor.Id);
        return filter;
    }

    // Apply bind ACL before count/skip/limit in assignment Form searches.
    internal static FilterDefinition<DynamicFormTemplate> BuildMayBindSearchFilter(
        AppUser actor,
        bool requirePublished)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var fb = Builders<DynamicFormTemplate>.Filter;
        var filter = fb.Eq(form => form.IsDeleted, false) &
                     fb.Eq(form => form.IsActive, true);
        if (requirePublished)
            filter &= fb.Eq(form => form.IsPublished, true);
        if (!IsSystemAdministrator(actor))
            filter &= fb.Eq(form => form.CreatedByUserId, actor.Id);
        return filter;
    }

    internal static AppException Forbidden()
        => AppExceptionFactory.Forbidden(
            AppErrorCode.AUTH_FORBIDDEN,
            new { reason = ForbiddenReason });

    internal static bool IsSystemAdministrator(AppUser actor)
        => (actor.Roles ?? new List<string>())
               .Any(role => string.Equals(role, Roles.SYSTEM_ADMIN, StringComparison.OrdinalIgnoreCase)) ||
           string.Equals(
               actor.AccountKind,
               ManagementAccountKind.SystemAdmin,
               StringComparison.OrdinalIgnoreCase);
}
