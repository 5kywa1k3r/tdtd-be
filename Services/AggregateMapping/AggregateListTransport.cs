using System.Text.Json;
using System.Text.Json.Nodes;

namespace tdtd_be.Services.AggregateMapping;

internal static class AggregateListTransport
{
    // Full evidence is persisted server-side. List detail is requested through an
    // authorized, revision-bound page/detail read instead of the polling response.
    internal static JsonElement Compact<T>(T value)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var root = JsonSerializer.SerializeToNode(value, options)!;
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["listOperations"] is JsonArray { Count: > 0 } operations)
                {
                    obj["lineage"] = new JsonObject();
                    if (obj["sourceValues"] is JsonArray sources)
                        foreach (var source in sources.OfType<JsonObject>()) source["trace"] = new JsonArray();
                    foreach (var operation in operations.OfType<JsonObject>())
                    {
                        operation["reportCount"] = (operation["reports"] as JsonArray)?.Count ?? 0;
                        operation["reports"] = new JsonArray();
                        operation["reportCountsPaged"] = true;
                    }
                }
                foreach (var property in obj.ToArray()) Visit(property.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) Visit(child);
        }
        Visit(root);
        return JsonSerializer.SerializeToElement(root, options);
    }
}
