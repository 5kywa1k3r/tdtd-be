//Caching/CacheKeys.cs
namespace tdtd_be.Caching
{
    public static class CacheKeys
    {
        public static string UserByUsername(string username) => $"user:username:{username}";
        public static string UserById(string userId) => $"user:id:{userId}";
        public static string LoginFail(string username) => $"login:fail:{username}";
    }
}
