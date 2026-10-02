using tdtd_be.Enum;
using tdtd_be.Models;

namespace tdtd_be.Common.Auth;

public static class AccountAdministrationRules
{
    public static bool ChildUnitTypeAllowed(string? managerType, string? childType)
        => (managerType ?? "").Trim().ToUpperInvariant() switch
        {
            "TINH" => (childType ?? "").Trim().ToUpperInvariant() is "PHONG" or "PHUONG_XA" or "XA" or "PHUONG",
            "PHONG" => string.Equals(childType?.Trim(), "DOI", StringComparison.OrdinalIgnoreCase),
            "DOI" or "XA" or "PHUONG" or "PHUONG_XA" => string.Equals(childType?.Trim(), "TO", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    public static bool PositionFitsUnitType(Position position, string? unitTypeCode)
    {
        var type = (unitTypeCode ?? "").Trim().ToUpperInvariant();
        var ceiling = type switch
        {
            "TINH" => 100,
            "PHONG" => 80,
            "DOI" => 60,
            "XA" or "PHUONG" or "PHUONG_XA" => 40,
            "TO" => 20,
            _ => (int?)null
        };
        // Older seeds rank commune leaders differently. Known titles use the canonical hierarchy.
        var rank = Positions.IsValid(position.Code) ? Positions.GetRank(position.Code) : position.Rank;
        if (ceiling.HasValue && rank > ceiling.Value)
            return false;
        var catalogCeiling = type is "XA" or "PHUONG" or "PHUONG_XA" ? 60
            : type == "TO" ? 40 : ceiling;
        if (catalogCeiling.HasValue && position.Rank > catalogCeiling.Value) return false;

        var code = (position.Code ?? "").Trim().ToUpperInvariant();
        if (type == "XA" && code.Contains("CONG_AN_PHUONG", StringComparison.Ordinal)) return false;
        if (type == "PHUONG" && code.Contains("CONG_AN_XA", StringComparison.Ordinal)) return false;
        return true;
    }

    public static bool PositionAllowed(Position position, UnitType type, Unit? unit = null)
    {
        if (position.IsDeleted || type.IsDeleted || !PositionFitsUnitType(position, type.Code)) return false;
        if (type.Code == "PHUONG_XA" && unit is not null)
        {
            var symbol = (unit.Symbol ?? "").Trim().ToUpperInvariant();
            var localType = symbol.StartsWith("CAX_", StringComparison.Ordinal) ? "XA"
                : symbol.StartsWith("CAP_", StringComparison.Ordinal) ? "PHUONG" : null;
            if (localType is not null && !PositionFitsUnitType(position, localType)) return false;
        }
        var rules = type.PositionRules ?? new();
        return rules.Count > 0
            ? rules.Any(rule => rule.IsEnabled && string.Equals(rule.PositionCode, position.Code, StringComparison.OrdinalIgnoreCase))
            : position.UnitTypeCodes.Any(code => string.Equals(code, type.Code, StringComparison.OrdinalIgnoreCase));
    }
}
