using System.Net;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationRollbackProbe
{
    private async Task<P10RollbackInventorySnapshot>
        CaptureUnknownCollectionInventoryAsync(CancellationToken ct)
    {
        var names = (await (await Database().ListCollectionNamesAsync(
                cancellationToken: ct)).ToListAsync(ct))
            .Where(value => !InventoryCollections.Contains(
                value, StringComparer.Ordinal))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var rows = new List<P10RollbackCollectionState>(names.Length);
        foreach (var name in names)
        {
            var documents = await Database()
                .GetCollection<BsonDocument>(name)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .ToListAsync(ct);
            using var hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            foreach (var document in documents)
            {
                var bytes = document.ToBson();
                hash.AppendData(BitConverter.GetBytes(
                    IPAddress.HostToNetworkOrder(bytes.Length)));
                hash.AppendData(bytes);
            }
            rows.Add(new(
                name,
                true,
                documents.Count,
                Convert.ToHexString(hash.GetHashAndReset())
                    .ToLowerInvariant()));
        }
        var canonical = string.Join("\n", rows.Select(value =>
            $"{value.Collection}|{value.Count}|{value.DocumentSetSha256}"));
        return new(rows, HashBytes(Encoding.UTF8.GetBytes(canonical)));
    }
}
