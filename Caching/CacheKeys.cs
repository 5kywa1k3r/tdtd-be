// Caching/CacheKeys.cs
namespace tdtd_be.Caching
{
    public static class CacheKeys
    {
        public static string UserByUsername(string username) => $"user:username:{username}";
        public static string UserById(string userId) => $"user:id:{userId}";
        public static string LoginFail(string username) => $"login:fail:{username}";

        public static string DashboardMyWorksSummary(
            string userId,
            DateTime fromUtc,
            DateTime toUtc,
            string keyword,
            string unitHash)
            => $"dashboard:myworks:{userId}:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMdd}:{keyword}:{unitHash}";

        public static string DashboardWorkDetail(
            string userId,
            string workId,
            DateTime fromUtc,
            DateTime toUtc,
            string unitHash,
            bool includeRootAssignments,
            bool includeReportSummary)
            => $"dashboard:workdetail:{userId}:{workId}:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMdd}:{unitHash}:{includeRootAssignments}:{includeReportSummary}";
    }
}