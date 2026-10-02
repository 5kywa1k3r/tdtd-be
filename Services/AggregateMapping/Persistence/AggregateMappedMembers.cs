using System.Text.Json.Nodes;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal static class AggregateMappedMembers
{
    internal static void EnsureUnchanged(IEnumerable<string> members, string? oldFields, string? oldTables, string? newFields, string? newTables)
    {
        var beforeFields = Parse(oldFields); var afterFields = Parse(newFields);
        var beforeTables = Parse(oldTables); var afterTables = Parse(newTables);
        foreach (var member in members)
        {
            var oldField = (beforeFields?["values"] ?? beforeFields)?[member];
            var newField = (afterFields?["values"] ?? afterFields)?[member];
            var oldTable = beforeTables?["nativeTables"]?[member];
            var newTable = afterTables?["nativeTables"]?[member];
            if (!JsonNode.DeepEquals(oldField, newField) || !JsonNode.DeepEquals(oldTable, newTable))
                throw new AggregatePreviewException("AGG_TARGET_MAPPED_READ_ONLY");
        }
    }
    private static JsonObject? Parse(string? json) => string.IsNullOrWhiteSpace(json) ? null
        : JsonNode.Parse(json) as JsonObject ?? throw new AggregatePreviewException("AGG_PAYLOAD_INVALID");
}
