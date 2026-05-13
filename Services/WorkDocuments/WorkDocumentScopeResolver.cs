using tdtd_be.Models;

namespace tdtd_be.Services.WorkDocuments;

public sealed record WorkDocumentScopeInfo(
    string Scope,
    string? WorkId,
    string? AssignmentId,
    string? AssignmentCode,
    string? AssignmentPath);

public static class WorkDocumentScopeResolver
{
    public static WorkDocumentScopeInfo Resolve(FileDoc file)
    {
        var sourceType = Normalize(file.SourceType);
        var scope = Normalize(file.DocumentScope);

        if (string.Equals(scope, WorkDocumentConstants.ScopeWork, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceType, WorkDocumentConstants.SourceTypeWorkDocument, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceType, WorkDocumentConstants.SourceTypeWorkBasis, StringComparison.OrdinalIgnoreCase))
        {
            return new WorkDocumentScopeInfo(
                WorkDocumentConstants.ScopeWork,
                NullIfWhiteSpace(file.WorkId) ?? NullIfWhiteSpace(file.SourceId),
                null,
                null,
                null);
        }

        if (string.Equals(scope, WorkDocumentConstants.ScopeAssignmentBranch, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceType, WorkDocumentConstants.SourceTypeAssignmentDocument, StringComparison.OrdinalIgnoreCase))
        {
            return new WorkDocumentScopeInfo(
                WorkDocumentConstants.ScopeAssignmentBranch,
                NullIfWhiteSpace(file.WorkId),
                NullIfWhiteSpace(file.AssignmentId) ?? NullIfWhiteSpace(file.SourceId),
                NullIfWhiteSpace(file.AssignmentCode),
                NullIfWhiteSpace(file.AssignmentPath));
        }

        return new WorkDocumentScopeInfo(scope ?? sourceType ?? "UPLOAD", file.WorkId, file.AssignmentId, file.AssignmentCode, file.AssignmentPath);
    }

    public static IReadOnlyList<string> ParseAssignmentPath(string? path, string fallbackAssignmentId)
    {
        var ids = (path ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (!ids.Contains(fallbackAssignmentId, StringComparer.Ordinal))
            ids.Add(fallbackAssignmentId);

        return ids;
    }

    private static string? Normalize(string? value)
        => NullIfWhiteSpace(value)?.Trim().ToUpperInvariant();

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
