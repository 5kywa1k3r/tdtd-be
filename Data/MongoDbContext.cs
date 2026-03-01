using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models;

namespace tdtd_be.Data
{

    public sealed class MongoDbContext
    {
        public IMongoDatabase Db { get; }
        public IMongoCollection<AppUser> Users { get; }
        public IMongoCollection<RefreshTokenDoc> RefreshTokens { get; }
        public IMongoCollection<Unit> Units { get; }
        public IMongoCollection<UnitType> UnitTypes { get; }
        public IMongoCollection<UnitVersionHistory> UnitHistories { get; }
        public IMongoCollection<FileDoc> Files { get; }

        public MongoDbContext(IOptions<MongoOptions> opt)
        {
            var o = opt.Value;
            var client = new MongoClient(o.ConnectionString);
            Db = client.GetDatabase(o.Database);
            Users = Db.GetCollection<AppUser>(o.UserCollection);
            RefreshTokens = Db.GetCollection<RefreshTokenDoc>(o.RefreshTokenCollection);
            Units = Db.GetCollection<Unit>(o.UnitCollection);
            UnitTypes = Db.GetCollection<UnitType>(o.UnitTypeCollection);
            UnitHistories = Db.GetCollection<UnitVersionHistory>(o.UnitHistoryCollection);
            Files = Db.GetCollection<FileDoc>(o.FileDocCollection);
        }
    }
}