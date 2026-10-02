using System.Reflection;
using System.Collections;
using System.Text.Json;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

var checks=0;
void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
var fields=new[]{new ResolvedDynamicFormRuntimeFieldDefinition("proof","evidence",Required:true)};
var valid=DynamicFormRuntimeFieldCanonicalizer.Canonicalize("{\"values\":{\"proof\":[\"6abc6ee948300dbc10106f31\"]}}",fields,true);
Check(JsonDocument.Parse(valid.CanonicalFieldValuesJson).RootElement.GetProperty("values").GetProperty("proof").GetArrayLength()==1,"file IDs preserved");
foreach(var invalid in new[]{"[]","\"not an array\"","[1]","[\"a\",\"a\"]"}){
 try{DynamicFormRuntimeFieldCanonicalizer.Canonicalize("{\"proof\":"+invalid+"}",fields,true);throw new Exception("Accepted invalid evidence "+invalid);}
 catch(DynamicFormRuntimeFieldValidationException){checks++;}
}
Console.WriteLine($"PASS {checks} evidence canonicalization checks (no DB/API).");
var form=new DynamicFormTemplate { Id="form",FamilyId="form",VersionNo=1,IsPublished=true,SchemaVersion=1,
 SectionsJson="[{\"id\":\"s\",\"title\":\"Section\"}]",
 FieldsJson="[{\"id\":\"proof\",\"sectionId\":\"s\",\"name\":\"Căn cứ\",\"type\":\"evidence\"},{\"id\":\"count\",\"sectionId\":\"s\",\"name\":\"Count\",\"type\":\"number\"}]",BlocksJson="[]" };
var snapshot=DynamicFormPublishedSchemaSnapshotBuilder.Build(form);
form.PublishedSchemaSnapshotJson=snapshot.Json;form.PublishedSchemaHash=snapshot.Sha256;
var adapter=typeof(DynamicFormTemplate).Assembly.GetType("tdtd_be.Services.AggregateMapping.AggregateNativePayloadAdapter")!;
var schema=adapter.GetMethod("Schema",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[form])!;
var members=(IDictionary)schema.GetType().GetProperty("Members")!.GetValue(schema)!;
Check(members.Contains("count") && !members.Contains("proof"),"Evidence excluded from aggregate sources/targets");
Console.WriteLine("PASS aggregate schema excludes evidence and keeps number field (no DB/API).");
