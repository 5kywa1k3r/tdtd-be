//Options/MongoOptions.cs
namespace tdtd_be.Options
{
    public sealed class MongoOptions
    {
        public string ConnectionString { get; init; } = null!;
        public string Database { get; init; } = null!;
    }
}
