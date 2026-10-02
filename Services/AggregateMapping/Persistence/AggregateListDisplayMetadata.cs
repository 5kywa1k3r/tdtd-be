using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.Services.AggregateMapping.Persistence;

// Called only between the two snapshot authority checks. Metadata is resolved from
// the immutable source pin/field lineage, never from a client-supplied catalog or
// the current target field (whose code may have a different label).
internal sealed class AggregateListDisplayMetadata(MongoDbContext db)
{
    private sealed record Binding(JsonObject Cell, string FieldName, string? CatalogId,
        IReadOnlyDictionary<string,string> Inline, string[] Codes);
    internal async Task Enrich(JsonNode response, string mode, bool lineage, CancellationToken ct)
    {
        if (mode == "COUNTS") return;
        var templates = new Dictionary<AggregateFormPinDto,DynamicFormTemplate>();
        var bindings = new List<Binding>();
        var items = mode == "PAGE" ? response["items"]!.AsArray().ToArray() : new[] { response };
        var metadata = mode == "PAGE" ? response["metadata"]!.AsArray().ToArray() : new[] { response["metadata"] };
        for (var i=0;i<items.Length;i++)
        {
            var info = metadata[i]!;
            var origin = info["origin"]!.Deserialize<AggregateListOrigin>(AggregateCanonical.Json)!;
            DynamicFormNativeTableDto? table = null;
            if (origin.Form is { } pin)
            {
                if (!templates.TryGetValue(pin,out var template))
                {
                    template = await db.DynamicFormTemplates.Find(f=>f.Id==pin.FormId&&!f.IsDeleted).FirstOrDefaultAsync(ct)
                        ?? throw new AggregatePreviewException("AGG_SCHEMA_PIN_UNAVAILABLE");
                    if (template.FamilyId!=pin.FamilyId || template.VersionNo!=pin.VersionNo || template.PublishedSchemaHash!=pin.SchemaHash)
                        throw new AggregatePreviewException("AGG_SCHEMA_PIN_UNAVAILABLE");
                    DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(template);
                    templates[pin]=template;
                }
                table=DynamicFormNativeTableDefinition.ReadStored(template.NativeTablesVersion,template.TablesJson)?
                    .SingleOrDefault(t=>t.Id==origin.ListId&&t.Presentation?.Kind=="LIST");
            }
            var cells = mode=="PAGE" ? items[i]!["cells"]!.AsObject().ToArray()
                : new[] { new KeyValuePair<string,JsonNode?>(response["fieldId"]!.GetValue<string>(),response["cell"]) };
            foreach(var pair in cells)
            {
                var cell=pair.Value!.AsObject();
                var fields=info["fields"]?[pair.Key]?.Deserialize<string[]>(AggregateCanonical.Json)??[];
                var fieldId=fields.Length==1?fields[0]:origin.Pin==null?pair.Key:null;
                var field=table?.Fields?.SingleOrDefault(f=>f.Id==fieldId);
                cell["fieldLabel"]=field?.Name;
                if(cell["type"]?.GetValue<string>() is not ("CHOICE_ONE" or "CHOICE_MANY") || cell["state"]?.GetValue<string>()!="VALUE")continue;
                var codes=cell["value"] is JsonArray array ? array.Select(c=>c!.GetValue<string>()).ToArray()
                    : new[] {cell["value"]!.GetValue<string>()};
                if(field==null){Unavailable(cell,codes);continue;}
                var spec=DynamicFormNativeTableDefinition.CompileCellTypes(table!)(field.Id!,null);
                if(spec.Type is not ("singleSelect" or "multiSelect")){Unavailable(cell,codes);continue;}
                bindings.Add(new(cell,field.Name??field.Id!,spec.ValueSource?.SourceType=="ENUM_CATALOG"?spec.ValueSource.CatalogId:null,
                    (spec.Options??[]).ToDictionary(o=>o.Code!,o=>o.Label??o.Code!,StringComparer.Ordinal),codes));
            }
            if(mode=="DETAIL"&&lineage)
            {
                var name=string.IsNullOrEmpty(origin.UnitId)?null:await db.Units.Find(u=>u.Id==origin.UnitId&&!u.IsDeleted)
                    .Project(u=>u.FullName).FirstOrDefaultAsync(ct);
                response["origin"]!["unitName"]=name;
                response["origin"]!["unitNameState"]=string.IsNullOrWhiteSpace(name)?"UNAVAILABLE":"AVAILABLE";
                response["origin"]!["listName"]=table?.Name;
            }
        }
        // One bounded projection per bound catalog, containing only codes occurring
        // on this page/detail. Deactivation does not revoke existing field bindings.
        var catalogs=new Dictionary<string,Dictionary<string,string>>(StringComparer.Ordinal);
        foreach(var group in bindings.Where(b=>b.CatalogId!=null).GroupBy(b=>b.CatalogId!))
        {
            var codes=group.SelectMany(b=>b.Codes).Distinct(StringComparer.Ordinal).ToArray();
            var catalog=await db.LabelEnumCatalogs.Find(c=>c.Id==group.Key&&!c.IsDeleted)
                .Project(c=>new {Options=c.Options.Where(o=>codes.Contains(o.Code)).Select(o=>new {o.Code,o.Label}).ToList()}).FirstOrDefaultAsync(ct);
            catalogs[group.Key]=catalog?.Options.ToDictionary(o=>o.Code,o=>o.Label,StringComparer.Ordinal)??new();
        }
        foreach(var binding in bindings)
        {
            var options=binding.CatalogId==null?binding.Inline:catalogs[binding.CatalogId];
            binding.Cell["displayOptions"]=JsonSerializer.SerializeToNode(binding.Codes.Select(code=>new {
                code,label=options.TryGetValue(code,out var name)?name:null }),AggregateCanonical.Json);
            binding.Cell["labelState"]=binding.Codes.All(code=>options.ContainsKey(code))?"AVAILABLE":"UNAVAILABLE";
        }
        // The compact index is internal, including when includeLineage=false.
        response.AsObject().Remove("metadata");
    }
    private static void Unavailable(JsonObject cell,string[] codes)
    {
        cell["labelState"]="UNAVAILABLE";
        cell["displayOptions"]=JsonSerializer.SerializeToNode(codes.Select(code=>new {code,label=(string?)null}),AggregateCanonical.Json);
    }
}
