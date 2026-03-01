namespace tdtd_be.Common.Auth;

public static class Roles
{
    public const string ADMIN = "ADMIN";
    public const string SYSTEM_ADMIN = "SYSTEM_ADMIN";
    public const string MANAGER_LEVEL = "MANAGER_LEVEL";

    private const string ManagerUnitPrefix = "MANAGER_UNIT:";

    public static string ManagerUnit(string unitId) => $"{ManagerUnitPrefix}{unitId}";

    public static bool IsManagerUnit(string role, out string unitId)
    {
        unitId = "";
        if (string.IsNullOrWhiteSpace(role)) return false;

        role = role.Trim();
        if (!role.StartsWith(ManagerUnitPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var raw = role.Substring(ManagerUnitPrefix.Length).Trim();
        if (string.IsNullOrWhiteSpace(raw)) return false;

        unitId = raw;
        return true;
    }
}