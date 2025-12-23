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

        public MongoDbContext(IOptions<MongoOptions> opt)
        {
            var client = new MongoClient(opt.Value.ConnectionString);
            Db = client.GetDatabase(opt.Value.Database);
            Users = Db.GetCollection<AppUser>(opt.Value.UserCollection);
            RefreshTokens = Db.GetCollection<RefreshTokenDoc>(opt.Value.RefreshTokenCollection);
        }

        public IMongoCollection<Unit> Units(IOptions<MongoOptions> opt) =>
            Db.GetCollection<Unit>(opt.Value.UnitCollection);

        public IMongoCollection<UnitReportRelation> UnitReportRelations(IOptions<MongoOptions> opt) =>
            Db.GetCollection<UnitReportRelation>(opt.Value.UnitReportRelationCollection);

        public IMongoCollection<Models.Tag> Tags(IOptions<MongoOptions> opt) =>
            Db.GetCollection<Models.Tag>(opt.Value.TagCollection);

        public IMongoCollection<TagLink> TagLinks(IOptions<MongoOptions> opt) =>
            Db.GetCollection<TagLink>(opt.Value.TagLinkCollection);
    }
}