using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Labels;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Services;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignments.Lookups;
using tdtd_be.CanvasRoundtripTests;

// Starts ONLY an owned mongod child with a random port and fresh data directory.
// Does not run production startup, seeders, workers, jobs, login, or UAT databases.
await using var mongo = await CanvasMongo.StartAsync(args[0], args[1]);
var db = new MongoDbContext(Options.Create(new MongoOptions { ConnectionString = mongo.Connection, Database = mongo.Database }));
var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
var me = new MeAccessor(http);
var catalogs = new LabelEnumCatalogService(db, me);
await db.LabelEnumCatalogs.Indexes.CreateOneAsync(new CreateIndexModel<LabelEnumCatalog>(
    Builders<LabelEnumCatalog>.IndexKeys.Ascending(x=>x.ScopeType).Ascending(x=>x.ScopeId).Ascending(x=>x.Code),
    new CreateIndexOptions<LabelEnumCatalog> { Unique=true, PartialFilterExpression=Builders<LabelEnumCatalog>.Filter.Eq(x=>x.IsDeleted,false) }));
var passed = new List<string>();
string Id() => ObjectId.GenerateNewId().ToString();
var parent = new Unit { Id = Id(), Code = "01", FullName = "Parent" };
var pa = new Unit { Id = Id(), Code = "0101", FullName = "PA02", ParentUnitId = parent.Id };
var child = new Unit { Id = Id(), Code = "010101", FullName = "PA child", ParentUnitId = pa.Id };
var xa = new Unit { Id = Id(), Code = "0102", FullName = "Xa other branch", ParentUnitId = parent.Id };
await db.Units.InsertManyAsync([parent, pa, child, xa]);
MeResponse Actor(string unit, string role) => new(Id(), role, role, [], unit, null, null, null, [role], null, false);
var admin = Actor(parent.Id, "SYSTEM_ADMIN");
var manager = Actor(pa.Id, "MANAGER_UNIT:" + pa.Id);
var lower = Actor(child.Id, "MANAGER_UNIT:" + child.Id);
var officer = Actor(xa.Id, "USER");
var outsider = Actor(xa.Id, "USER");
void As(MeResponse actor) => http.HttpContext!.Items[MeAccessor.MeItemKey] = actor;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed.Add(name); Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> action, string name) {
  try { await action(); } catch (AppException ex) when (ex.Descriptor.HttpStatus is >= 400 and < 500) { Check(true, name); return; }
  throw new Exception("Expected rejection: " + name);
}
CreateLabelEnumCatalogReq Request(string code, IReadOnlyList<LabelEnumOptionDto>? options = null) =>
    new(code, "Catalog " + code, null, options ?? [new("dat", "Đạt", 1), new("khong", "Không đạt", 2)], null, null);
As(manager);
var cat = await catalogs.CreateAsync(Request("pa_choices"), default);
Check(cat.ScopeType == "UNIT" && cat.ScopeId == pa.Id, "manager creates own UNIT catalog");
await Reject(() => catalogs.CreateAsync(Request("pa_choices"), default), "BE unique scope/code rejects duplicate catalog");
await Reject(() => catalogs.CreateAsync(Request("bad", [new("", "Missing")]), default), "BE rejects missing option code");
await Reject(() => catalogs.CreateAsync(Request("bad", [new("a", "")]), default), "BE rejects missing option label");
await Reject(() => catalogs.CreateAsync(Request("bad", [new("a", "A"), new("A", "B")]), default), "BE rejects duplicate option codes");
await Reject(() => catalogs.CreateAsync(Request("bad", [new("a", "Same"), new("b", "Same")]), default), "BE rejects duplicate option labels");
await Reject(() => catalogs.CreateAsync(Request("bad", [new("a", "A", -1)]), default), "BE rejects negative order");
await Reject(() => catalogs.CreateAsync(Request("bad_zero", [new("a", "A", 0)]), default), "BE create rejects zero order");
await Reject(() => catalogs.CreateAsync(Request("bad_order", [new("a", "A", 1), new("b", "B", 1, false)]), default), "BE create rejects duplicate orders including inactive options");
await Reject(() => catalogs.QuickCreateAsync(new("quick_bad", "Quick bad", null, "DYNAMIC_FORM", "fixture", [new("a", "A", 1), new("b", "B", 1)], null, null), default), "BE quick-create rejects duplicate orders");
var quick = await catalogs.QuickCreateAsync(new("quick_ok", "Quick ok", null, "DYNAMIC_FORM", "fixture", [new("a", "A", 1), new("b", "B", 2)], null, null), default);
Check(quick.Options.Select(option => option.Order).SequenceEqual(new[] { 1, 2 }), "BE quick-create accepts distinct positive row orders");
var unused = await catalogs.CreateAsync(Request("unused"), default);
await Reject(() => catalogs.UpdateAsync(unused.Id, new(unused.Name, null, [new("a", "A", 1), new("b", "B", 1)]), default), "BE update rejects duplicate orders");
await Reject(() => catalogs.UpdateAsync(unused.Id, new(unused.Name, null, [new("a", "A", 0)]), default), "BE update rejects zero order");
var ordered = await catalogs.UpdateAsync(unused.Id, new(unused.Name, null, [new("b", "B", 9), new("a", "A", 1)]), default);
Check(ordered.Options.Select(option => option.Code).SequenceEqual(new[] { "a", "b" }), "BE readback sorts positive manual orders and preserves gaps");
// Historical data fixture only: deactivation must not rewrite formerly accepted order ties.
await db.LabelEnumCatalogs.UpdateOneAsync(x => x.Id == unused.Id, Builders<LabelEnumCatalog>.Update.Set("Options.0.Order", 0).Set("Options.1.Order", 0));
var legacy = await catalogs.GetByIdAsync(unused.Id, default);
var deactivatedLegacy = await catalogs.UpdateAsync(unused.Id, new(legacy.Name, null, legacy.Options, false), default);
Check(!deactivatedLegacy.IsActive && deactivatedLegacy.Options.All(option => option.Order == 0), "BE preserves legacy tied orders on unchanged deactivation");
await Reject(() => catalogs.UpdateAsync(unused.Id, new("Changed", null, legacy.Options, false), default), "BE disallows content edits with legacy tied orders even when inactive");
unused = await catalogs.UpdateAsync(unused.Id, new("Renamed", null, [new("new", "New", 2)]), default);
Check(unused.Name == "Renamed" && unused.Options[0].Order == 2, "unused catalog content can be edited");
await catalogs.DeleteAsync(unused.Id, default);
await Reject(() => catalogs.GetByIdAsync(unused.Id, default), "soft-deleted catalog cannot be read");
As(lower);
Check(!(await catalogs.GetByIdAsync(cat.Id, default)).CanManage, "descendant can read but cannot manage ancestor");
await Reject(() => catalogs.UpdateAsync(cat.Id, new(cat.Name, null, cat.Options), default), "descendant edit denied by BE");
await Reject(() => catalogs.DeleteAsync(cat.Id, default), "descendant delete denied by BE");
var lowCat = await catalogs.CreateAsync(Request("child"), default);
As(manager);
await Reject(() => catalogs.GetByIdAsync(lowCat.Id, default), "ancestor cannot read child catalog");
As(officer);
await Reject(() => catalogs.GetByIdAsync(cat.Id, default), "sibling catalog detail denied");
await Reject(() => catalogs.SearchOptionsAsync(cat.Id, null, 0, 50, default), "general picker cross-branch denied");
Check((await catalogs.SearchAsync(new(null,null,null,null,null,null),default)).Rows.All(x => x.Id != cat.Id), "general search does not leak PA catalog");
await Reject(() => catalogs.CreateAsync(Request("forbidden"), default), "ordinary officer cannot create catalogs");
As(admin);
Check((await catalogs.GetByIdAsync(cat.Id, default)).CanManage, "SYSTEM_ADMIN retains management");
var global = await catalogs.CreateAsync(Request("global"),default);
var levelManager = Actor(parent.Id,"MANAGER_LEVEL"); As(levelManager);
var level = await catalogs.CreateAsync(Request("level"),default);
Check(level.ScopeType=="LEVEL" && level.ScopeId==parent.Id,"level manager creates only own LEVEL scope");
await Reject(()=>catalogs.UpdateAsync(cat.Id,new(cat.Name,null,cat.Options),default),"level manager cannot edit descendant UNIT catalog");
As(officer);
Check((await catalogs.GetByIdAsync(global.Id,default)).ScopeType=="GLOBAL","officer reads GLOBAL catalog");
Check(!(await catalogs.GetByIdAsync(level.Id,default)).CanManage,"descendant inherits LEVEL read only");

var form = new DynamicFormTemplate { Id=Id(), Code="enum_fixture", Name="Fixture", VersionNo=1, SchemaVersion=1, IsPublished=true,
  SectionsJson="[{\"id\":\"s\",\"title\":\"Section\",\"order\":0}]", BlocksJson="[]" };
form.FamilyId=form.Id;
form.FieldsJson=JsonSerializer.Serialize(new[] { ("single", "singleSelect"), ("multi", "multiSelect"), ("legacy", "shortText"),
  ("locality", "singleSelect"), ("localities", "multiSelect"), ("unit", "singleSelect"), ("units", "multiSelect") }.Select(f => new {
  id=f.Item1, key=f.Item1, sectionId="s", name=f.Item1, type=f.Item2, required=false, colSpan=12, minHeight=40, order=0,
  valueSource=new { sourceType=f.Item1.StartsWith("localit") ? "SYSTEM_LOCALITY" : f.Item1.StartsWith("unit") ? "SYSTEM_UNIT" : "ENUM_CATALOG",
    catalogId=f.Item1.StartsWith("localit") || f.Item1.StartsWith("unit") ? null : cat.Id } }));
As(manager);
var forms = new DynamicFormService(db,me,null!,catalogs);
var draft = await forms.CreateAsync(new("enum_fixture","Fixture",null,[],1,form.SectionsJson,form.FieldsJson,null,"[]"),default);
var reopened = await forms.GetByIdAsync(draft.Id,default);
Check(reopened.FieldsJson.Contains(cat.Id),"form create and readback preserve catalog reference");
var published = await forms.PublishAsync(draft.Id,new(draft.Revision),default);
Check(published.IsPublished,"form with single/multi/legacy catalog fields publishes");
form = await db.DynamicFormTemplates.Find(x=>x.Id==draft.Id).SingleAsync();
var snapshot=DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(form);
var communeManager=Actor(xa.Id,"MANAGER_UNIT:"+xa.Id);
await db.Users.InsertOneAsync(new() { Id=communeManager.Id, Username="enum_test_xa", UnitId=xa.Id, Roles=communeManager.Roles });
var parentAssignment=new WorkAssignment { Id=Id(), WorkId=Id(), CreatedByUserId=manager.Id, IsActive=true,
 Assignees=[new() { UserId=communeManager.Id }], DynamicFormTemplateId=form.Id, DynamicFormFamilyId=form.Id,
 DynamicFormVersionNo=1, DynamicFormSchemaHash=snapshot.Sha256 };
await db.WorkAssignments.InsertOneAsync(parentAssignment);
var childResolution=await new WorkAssignmentTemplateResolver(db).ResolveForChildAsync(form.Id,communeManager.Id,parentAssignment.WorkId,parentAssignment.Id);
Check(childResolution.DynamicFormTemplateId==form.Id,"PA02 to cross-branch commune inherits exact form for officer assignment");
var assignment=new WorkAssignment { Id=Id(), WorkId=parentAssignment.WorkId, ParentAssignmentId=parentAssignment.Id, CreatedByUserId=communeManager.Id, IsActive=true,
 DynamicFormTemplateId=form.Id, DynamicFormFamilyId=form.Id, DynamicFormVersionNo=1, DynamicFormSchemaHash=snapshot.Sha256 };
await db.WorkAssignments.InsertOneAsync(assignment);
var report=new WorkAssignmentReport { Id=Id(), WorkId=assignment.WorkId, WorkAssignmentId=assignment.Id, AssigneeUserId=officer.Id,
 DynamicFormTemplateId=form.Id, DynamicFormFamilyId=form.Id, DynamicFormVersionNo=1, DynamicFormSchemaHash=snapshot.Sha256, IsActive=true };
await db.WorkAssignmentReports.InsertOneAsync(report);
var submitted = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<WorkAssignmentReport>(report.ToBson());
submitted.Id = Id(); submitted.Status = tdtd_be.Models.Enums.WorkAssignmentReportStatus.Submitted;
submitted.FieldValuesJson = "{\"single\":\"dat\",\"multi\":[\"dat\",\"khong\"]}";
await db.WorkAssignmentReports.InsertOneAsync(submitted);
var submittedBefore = (await db.WorkAssignmentReports.Find(x=>x.Id==submitted.Id).SingleAsync()).ToBson();
await db.WorkTemplateAssignees.InsertOneAsync(new() { Id=Id(), WorkId=assignment.WorkId, WorkAssignmentId=assignment.Id,
 AssigneeUserId=officer.Id, DynamicFormTemplateId=form.Id, DynamicFormFamilyId=form.Id, DynamicFormVersionNo=1, DynamicFormSchemaHash=snapshot.Sha256, IsActive=true });
// Use real methods, injecting only the two dependencies these paths consume.
var service=(WorkAssignmentReportService)RuntimeHelpers.GetUninitializedObject(typeof(WorkAssignmentReportService));
typeof(WorkAssignmentReportService).GetField("_ctx",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(service,db);
typeof(WorkAssignmentReportService).GetField("_enumCatalogs",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(service,catalogs);
Task<tdtd_be.DTOs.Common.PagedResult<LabelEnumOptionPickRow>> Read(string actor, string field="single", string? catalog=null, string? reportId=null) =>
 service.SearchFieldEnumOptionsAsync(reportId ?? report.Id,null,field,catalog ?? cat.Id,null,0,50,actor,default);
As(officer);
Check((await Read(officer.Id)).Rows.Count==2,"cross-branch assigned officer reads exact field options");
Check((await service.SearchFieldEnumOptionsAsync(null,assignment.Id,"multi",cat.Id,null,0,50,officer.Id,default)).Rows.Count==2,"pre-report binding authorizes exact form options");
await Reject(()=>Read(outsider.Id),"outsider report access denied");
await Reject(()=>Read(communeManager.Id),"assignment owner without input permission denied");
await Reject(()=>Read(officer.Id,"fake"),"forged field denied");
await Reject(()=>Read(officer.Id,"single",lowCat.Id),"forged catalog denied");
await Reject(()=>Read(officer.Id,reportId:Id()),"forged report denied");
await Reject(()=>Read(officer.Id,reportId:"malformed"),"malformed report id rejected without server error");
await Reject(()=>service.SearchFieldEnumOptionsAsync(null,assignment.Id,"single",cat.Id,null,0,50,outsider.Id,default),"outsider assignment binding denied");
var validate=typeof(WorkAssignmentReportService).GetMethod("ValidateRuntimeDataPayloadAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
async Task<string?> Validate(string json) => await (Task<string?>)validate.Invoke(service,[report,Array.Empty<object>(),json,null,true,CancellationToken.None])!;
var canonical=await Validate("{\"single\":\"dat\",\"multi\":[\"dat\",\"khong\"],\"legacy\":\"khong\"}");
Check(canonical!.Contains("dat"),"save/submit validator accepts single, multi and legacy values across branches");
Check(await Validate(canonical)==canonical,"canonical payload roundtrip stable");
var systemValues=JsonSerializer.Serialize(new { locality="14797",localities=new[]{"14758","16636"},unit=pa.Id,units=new[]{pa.Id,xa.Id} });
var canonicalSystem=await Validate(systemValues);
Check(await Validate(canonicalSystem!)==canonicalSystem,"unit and locality single/multi values roundtrip through authoritative report validator");
await Reject(()=>Validate("{\"locality\":\"99999\"}"),"unknown locality denied by report save validator");
await Reject(()=>Validate("{\"localities\":[\"14797\",\"01001\"]}"),"other province locality denied by report save validator");
await Reject(()=>Validate("{\"locality\":\"Phường Hạc Thành\"}"),"locality display name cannot replace code");
await Reject(()=>Validate(JsonSerializer.Serialize(new {unit=Id()})),"unknown unit denied by report save validator");
await Reject(()=>Validate("{\"single\":\"foreign-code\"}"),"BE save validator rejects foreign catalog value");
await Reject(()=>Validate("{\"single\":[\"dat\"]}"),"single rejects array");
await Reject(()=>Validate("{\"multi\":\"dat\"}"),"multi rejects scalar");
As(manager);
Check((await catalogs.GetByIdAsync(cat.Id,default)).IsInUse,"reference exposed to manager");
await Reject(()=>catalogs.UpdateAsync(cat.Id,new("Changed",null,cat.Options),default),"referenced catalog name edit blocked");
await Reject(()=>catalogs.UpdateAsync(cat.Id,new(cat.Name,null,[new("dat","Changed")]),default),"referenced options cannot be changed or removed");
await Reject(()=>catalogs.DeleteAsync(cat.Id,default),"referenced catalog deletion blocked");
await catalogs.UpdateAsync(cat.Id,new(cat.Name,null,cat.Options,false),default);
await Reject(()=>catalogs.EnsureVisibleActiveCatalogAsync(cat.Id,default),"inactive catalog cannot be newly selected");
await Reject(()=>catalogs.UpdateAsync(cat.Id,new(cat.Name,null,cat.Options,true),default),"referenced inactive catalog cannot be reactivated");
Check((await Read(officer.Id)).Rows.Count==2,"deactivated catalog still serves bound report");
Check(await Validate(canonical)==canonical,"deactivation preserves bound save/submit validation");
var stored=await db.WorkAssignmentReports.Find(x=>x.Id==report.Id).SingleAsync();
Check(stored.FieldValuesJson==report.FieldValuesJson,"catalog deactivation does not mutate stored report");
Check((await db.WorkAssignmentReports.Find(x=>x.Id==submitted.Id).SingleAsync()).ToBson().SequenceEqual(submittedBefore),
    "submitted historical report remains byte-identical after catalog deactivation");
report.DynamicFormSchemaHash=new string('0',64);
await Reject(()=>Validate(canonical!),"forged schema binding rejected by save validator");
await db.WorkAssignmentReports.UpdateOneAsync(x=>x.Id==report.Id,Builders<WorkAssignmentReport>.Update.Set(x=>x.DynamicFormSchemaHash,report.DynamicFormSchemaHash));
await Reject(()=>Read(officer.Id),"forged schema binding rejected by option reader");
// Public system catalogs do not grant any unit write capability.
var unitService = new UnitService(db, me, null!);
var importService = new AdminImportService(db, me, unitService, null!);
var picker = new tdtd_be.Controllers.PickersController(db, me, null!, catalogs, unitService);
var localityPicker = new tdtd_be.Controllers.SystemLocalitiesController(me);
var baseline = SystemLocalityCatalog.Official;
using(var excelSource=JsonDocument.Parse("{\"valueSource\":{\"sourceType\":\"SYSTEM_LOCALITY\"}}")) {
  var excelGuard=typeof(DynamicExcelService).GetMethod("ValidateShortTextOptionsForDataType",BindingFlags.NonPublic|BindingFlags.Static)!;
  try { excelGuard.Invoke(null,[excelSource.RootElement,"SHORT_TEXT","field","options"]); throw new Exception("Localities must not leak into standalone Excel."); }
  catch(TargetInvocationException ex) when(ex.InnerException is AppException) { Check(true,"locality source does not silently enable unsupported standalone Excel"); }
}
Check(baseline.Rows.Count==166 && baseline.Rows.Count(x=>x.Kind=="WARD")==19 && baseline.Rows.Count(x=>x.Kind=="COMMUNE")==147,
    "official Thanh Hoa baseline has 166 distinct localities: 147 communes and 19 wards");
foreach(var actor in new[]{officer,manager,Actor(parent.Id,"MANAGER_LEVEL"),Actor(parent.Id,"ADMIN"),Actor("","USER"),admin}) {
  As(actor);
  var unitRead=(tdtd_be.DTOs.Common.PagedResult<tdtd_be.DTOs.Pickers.UnitPickRow>)((Microsoft.AspNetCore.Mvc.OkObjectResult)(await picker.SearchUnitsByCode(null,pa.Id,false,0,50)).Result!).Value!;
  Check(unitRead.Rows.Any(x=>x.Id==pa.Id),"public unit picker restores cross-branch ID for "+actor.Username+"/"+actor.Id);
  var localityRead=(tdtd_be.DTOs.Common.PagedResult<SystemLocalityCatalog.Locality>)((Microsoft.AspNetCore.Mvc.OkObjectResult)localityPicker.Search("hac thanh").Result!).Value!;
  Check(localityRead.Rows.Single().Code=="14797","public locality accent-insensitive search for "+actor.Username+"/"+actor.Id);
  if(RoleGuard.IsSystemAdmin(actor)) continue;
  async Task MustBeSystemAdmin(Func<Task> action,string label) {
    try { await action(); } catch(AppException ex) when(ex.Code==AppErrorCode.AUTH_SYSTEM_ADMIN_REQUIRED) { Check(true,label+" denied for "+actor.Username+"/"+actor.Id); return; }
    throw new Exception("Expected exact system-admin gate: "+label);
  }
  await MustBeSystemAdmin(()=>unitService.CreateAsync(null!,default),"unit create");
  await MustBeSystemAdmin(()=>unitService.UpdateAsync(pa.Id,null!,default),"unit update");
  await MustBeSystemAdmin(()=>unitService.DeleteAsync(pa.Id,default),"unit delete");
  await MustBeSystemAdmin(()=>importService.ImportUnitsAsync(null!,false,default),"unit import");
}
As(admin);
await db.UnitTypes.InsertOneAsync(new() { Id=Id(), Code="TEST", Name="Test" });
var managedUnit=await unitService.CreateAsync(new() { FullName="System admin test",PrimaryUnitTypeCode="TEST",IsVirtual=true },default);
var updatedUnit=await unitService.UpdateAsync(managedUnit.Id,new() { FullName="Updated",PrimaryUnitTypeCode="TEST",IsVirtual=true },default);
Check(updatedUnit.FullName=="Updated","SYSTEM_ADMIN can create and update units");
await unitService.DeleteAsync(managedUnit.Id,default);
Check((await db.Units.Find(x=>x.Id==managedUnit.Id).SingleAsync()).IsDeleted,"SYSTEM_ADMIN can soft delete units");
http.HttpContext!.Items.Remove(MeAccessor.MeItemKey);
await Reject(()=>Task.FromResult(localityPicker.Search()),"unauthenticated locality read denied");
await File.WriteAllTextAsync(Path.Combine(mongo.Root,"results.json"),JsonSerializer.Serialize(new { passed=passed.Count, checks=passed },new JsonSerializerOptions { WriteIndented=true }));
Console.WriteLine($"PASS {passed.Count} checks; isolated data retained at {mongo.Root}");
