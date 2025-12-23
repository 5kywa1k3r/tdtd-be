//Options/MongoOptions.cs
namespace tdtd_be.Data.Infrastructure
{
    public sealed class MongoOptions
    {
        public string ConnectionString { get; init; } = null!;
        public string Database { get; init; } = null!;
        public string RefreshTokenCollection { get; set; } = "refresh_token";
        public string UnitCollection { get; set; } = "units";
        public string UnitReportRelationCollection { get; set; } = "unit_report_relations";
        public string TagCollection { get; set; } = "tags";
        public string TagLinkCollection { get; set; } = "tag_links";
        public string UserCollection { get; set; } = "users";
    }
}
