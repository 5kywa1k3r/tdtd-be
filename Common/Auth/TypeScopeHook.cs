namespace tdtd_be.Common.Auth;
public static class TypeScopeHook
{
    // meTypes empty => type0 => allow all
    public static void RequireAllowed(IReadOnlyList<string> meUnitTypeCodes, IReadOnlyList<string> targetUnitTypeCodes)
    {
        if (meUnitTypeCodes is null || meUnitTypeCodes.Count == 0) return; // type0 full
        // TODO: tự định nghĩa rule, ví dụ subset:
        // if (!targetUnitTypeCodes.All(t => meUnitTypeCodes.Contains(t))) throw ...
    }
}