using tdtd_be.DTOs.Auth;

namespace tdtd_be.DashboardModel.Services;

internal static class DashboardAccessPolicy
{
    private const string ProvincialPoliceDirectorPositionCode = "GIAM_DOC_CAT";
    private const string ProvincialUnitTypeCode = "TINH";

    internal static bool HasGlobalReadAccess(MeResponse me)
        => !me.IsDeleted
           && string.Equals(
               me.PositionCode?.Trim(),
               ProvincialPoliceDirectorPositionCode,
               StringComparison.OrdinalIgnoreCase)
           && me.UnitTypeCodes.Any(code => string.Equals(
               code?.Trim(),
               ProvincialUnitTypeCode,
               StringComparison.OrdinalIgnoreCase));
}
