//Data/MongoDbContext.cs
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Options;

namespace tdtd_be.Data
{
    public sealed class MongoDbContext
    {
        public IMongoDatabase Db { get; }

        public MongoDbContext(IMongoClient client, IOptions<MongoOptions> opt)
            => Db = client.GetDatabase(opt.Value.Database);

        public IMongoCollection<AppUser> Users => Db.GetCollection<AppUser>("users");
        public IMongoCollection<RefreshTokenDoc> RefreshTokens => Db.GetCollection<RefreshTokenDoc>("refresh_tokens");
    }
}
