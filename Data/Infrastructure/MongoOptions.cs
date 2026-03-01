//Options/MongoOptions.cs
namespace tdtd_be.Data.Infrastructure
{
    public sealed class MongoOptions
    {
        public string ConnectionString { get; init; } = null!;
        public string Database { get; init; } = null!;
        public string RefreshTokenCollection { get; set; } = "refresh_tokens";
        public string UnitCollection { get; set; } = "units";
        public string UserCollection { get; set; } = "users";
        public string UnitTypeCollection { get; set; } = "unit_types";
        public string UnitHistoryCollection { get; set; } = "unit_histories";
        public string FileDocCollection { get; set; } = "file_doc";
    }
}
