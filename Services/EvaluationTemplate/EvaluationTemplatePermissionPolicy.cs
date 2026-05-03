namespace tdtd_be.Services.EvaluationTemplates;

public static class EvaluationTemplatePermissionPolicy
{
    public const string AllowedUnitCode = "PV01";

    public static readonly HashSet<string> AllowedRolePrefixes = new (StringComparer.OrdinalIgnoreCase)
    {
        "ADMIN",
        "SYSTEM_ADMIN",
        "MANAGER_UNIT",
        "MANAGER_LEVEL"
    };

    public static readonly HashSet<string> AllowedPositionCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TRUONG_PHONG",
        "PHO_PHONG",
        "PHO_TRUONG_PHONG"
    };
}
