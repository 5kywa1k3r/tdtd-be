namespace tdtd_be.Services.EvaluationTemplates;

public static class EvaluationTemplatePermissionPolicy
{
    public static readonly HashSet<string> AllowedRolePrefixes = new (StringComparer.OrdinalIgnoreCase)
    {
        "SYSTEM_ADMIN",
        "MANAGER_UNIT",
        "MANAGER_LEVEL"
    };

    // Unit codes use three-character hierarchy segments. Match exact ancestor
    // codes with an indexed IN query, not a collection-wide prefix scan.
    public static IReadOnlyList<string> VisibleUnitCodes(string? unitCode)
    {
        var code = (unitCode ?? string.Empty).Trim();
        if (code.Length < 3 || code.Length % 3 != 0 || !code.All(char.IsDigit))
            return Array.Empty<string>();

        var result = new List<string>();
        for (var length = code.Length; length >= 3; length -= 3)
            result.Add(code[..length]);
        return result;
    }
}
