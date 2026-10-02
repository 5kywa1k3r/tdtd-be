using tdtd_be.DashboardModel.Services;
using tdtd_be.DTOs.Auth;

internal static class DashboardAccessPolicyContractTests
{
    public static void Run()
    {
        Assert(
            DashboardAccessPolicy.HasGlobalReadAccess(CreateMe("director-any-username", "GIAM_DOC_CAT", ["TINH"])),
            "The provincial police director must have dashboard global read access regardless of username.");

        Assert(
            !DashboardAccessPolicy.HasGlobalReadAccess(CreateMe("deputy", "PHO_GIAM_DOC_CAT", ["TINH"])),
            "A provincial deputy director must remain on the normal scoped access path.");

        Assert(
            !DashboardAccessPolicy.HasGlobalReadAccess(CreateMe("wrong-unit", "GIAM_DOC_CAT", ["PHONG"])),
            "The director position outside the provincial unit type must remain scoped.");

        Assert(
            !DashboardAccessPolicy.HasGlobalReadAccess(CreateMe("wrong-position", "TRUONG_PHONG", ["TINH"])),
            "A different position in the provincial unit must remain scoped.");

        Assert(
            !DashboardAccessPolicy.HasGlobalReadAccess(CreateMe("deleted", "GIAM_DOC_CAT", ["TINH"], isDeleted: true)),
            "A deleted director account must not receive dashboard global read access.");
    }

    private static MeResponse CreateMe(
        string username,
        string positionCode,
        List<string> unitTypeCodes,
        bool isDeleted = false)
        => new(
            "64f000000000000000000001",
            username,
            username,
            unitTypeCodes,
            "64f000000000000000000002",
            null,
            null,
            "CAT",
            [],
            positionCode,
            isDeleted);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
