using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Driver;
using StackExchange.Redis;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Cache;
using tdtd_be.Common.Errors;
using tdtd_be.Controllers;
using tdtd_be.DashboardModel.Services;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Services.Common;

if (!args.SequenceEqual(new[] { "--database", "tdtd", "--own-dashboard-fixture" }))
    throw new ArgumentException("Use --database tdtd --own-dashboard-fixture; only creates clearly named independent fixture rows.");
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); var ct = timeout.Token;
var db = new MongoDbContext(Options.Create(new MongoOptions { ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000", Database = "tdtd" }));
await tdtd_be.Data.Indexes.DynamicFormRuntimeProvenanceBackfill.RunAsync(db.Db, new MongoOptions(), ct);
var run = "uat-dashboard-20261005-" + Guid.NewGuid().ToString("N")[..8]; var checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
string Id() => ObjectId.GenerateNewId().ToString();
var prefix = "998" + RandomNumberGenerator.GetInt32(100000, 999999);
if (await db.Units.Find(x => x.Code != null && x.Code.StartsWith(prefix)).AnyAsync(ct)) throw new Exception("Fixture code prefix collision; rerun with another prefix.");
Unit U(string code, string type) => new() { Id = Id(), Code = code, FullName = "[THá»¬ RIÃŠNG DASHBOARD] " + code, UnitTypeCodes = [type], PrimaryUnitTypeCode = type, Note = run };
var dept = U(prefix, "PHONG"); var team = U(prefix + "001", "DOI"); team.ParentUnitId = dept.Id;
var commune = U(prefix + "002", "PHUONG_XA"); commune.ParentUnitId = dept.Id;
var other = U("997" + prefix[3..], "PHONG"); var province = U("996" + prefix[3..], "TINH");
await db.Units.InsertManyAsync(new[] { dept, team, commune, other, province }, cancellationToken: ct);
AppUser User(Unit unit, string suffix, string position) => new() { Id = Id(), Username = run + "-" + suffix, FullName = "[THá»¬ RIÃŠNG DASHBOARD] " + suffix,
    UnitId = unit.Id, PositionCode = position, AccountKind = "NORMAL", PasswordHash = "disabled-fixture-login", Note = run };
var head = User(dept, "head", "TRUONG_PHONG"); var deputy = User(dept, "deputy", "PHO_TRUONG_PHONG");
var manager = User(dept, "manager", "CAN_BO"); var officer = User(team, "officer", "CAN_BO");
var commHead = User(commune, "commune-head", "TRUONG_CONG_AN_XA"); var commOfficer = User(commune, "commune-officer", "CAN_BO");
var otherOfficer = User(other, "other", "CAN_BO"); var director = User(province, "director", "GIAM_DOC_CAT");
await db.Users.InsertManyAsync(new[] { head, deputy, manager, officer, commHead, commOfficer, otherOfficer, director }, cancellationToken: ct);
UserRef Ref(AppUser user) => new() { UserId = user.Id, UnitId = user.UnitId, Username = user.Username, FullName = user.FullName, UnitName = user.UnitId };
Work W(AppUser owner, string label, WorkType type = WorkType.TASK) => new() { Id = Id(), CreatedByUserId = owner.Id, Owner = Ref(owner),
    LeaderDirectiveUserId = owner.Id, AutoCode = run + "-" + label, Name = "[THá»¬ RIÃŠNG DASHBOARD] " + label, Type = type,
    StartDate = DateTime.UtcNow.Date.AddDays(-1), DueDate = DateTime.UtcNow.Date.AddDays(10), Note = run };
var own = W(manager, "phong"); var teamWork = W(officer, "doi"); var commWork = W(commOfficer, "xa");
var outside = W(otherOfficer, "ngoai-pham-vi"); var received = W(otherOfficer, "giao-xuong"); var indicator = W(officer, "chi-tieu", WorkType.INDICATOR);
await db.Works.InsertManyAsync(new[] { own, teamWork, commWork, outside, received, indicator }, cancellationToken: ct);
WorkAssignment A(Work work, AppUser creator, string label, WorkAssignment? parent, params AppUser[] assignees)
{
    var id = Id(); return new() { Id = id, WorkId = work.Id, CreatedByUserId = creator.Id, IssuedByUnitId = creator.UnitId,
        Name = label, Code = label, Path = (parent?.Path ?? "") + "/" + id, ParentAssignmentId = parent?.Id, RootAssignmentId = parent?.RootAssignmentId ?? id,
        Assignees = assignees.Select(Ref).ToList(), Level = parent is null ? 0 : parent.Level + 1, AssignmentType = "ONCE", AggregationType = "MANUAL",
        StartDate = DateTime.UtcNow.Date, DueDate = DateTime.UtcNow.Date.AddDays(1), Note = run };
}
var root = A(received, otherOfficer, "shared-upstream", null, manager, otherOfficer);
var ourChild = A(received, manager, "our-child", root, officer);
var theirChild = A(received, otherOfficer, "their-child", root, otherOfficer);
var commReceived = A(received, manager, "commune-received", ourChild, commOfficer);
await db.WorkAssignments.InsertManyAsync(new[] { root, ourChild, theirChild, commReceived }, cancellationToken: ct);
var templateId = Id();
var fixtureForm = DashboardFixtureForm.Create(templateId, run, received.CreatedByUserId!);
await db.DynamicFormTemplates.InsertOneAsync(fixtureForm, cancellationToken: ct);
foreach (var user in new[] { manager, otherOfficer })
{
    var bindingId = Id();
    await db.WorkTemplateAssignees.InsertOneAsync(new() { Id = bindingId, WorkId = received.Id, WorkAssignmentId = root.Id,
        DynamicFormTemplateId = templateId, DynamicFormFamilyId = fixtureForm.FamilyId, DynamicFormVersionNo = fixtureForm.VersionNo, DynamicFormSchemaHash = fixtureForm.PublishedSchemaHash,
        AssigneeUserId = user.Id, AssigneeUnitId = user.UnitId, AssigneeUsername = user.Username, Note = run }, cancellationToken: ct);
    await db.WorkReportPeriods.InsertOneAsync(new() { Id = Id(), WorkId = received.Id, WorkAssignmentId = root.Id, WorkTemplateAssigneeId = bindingId,
        DynamicFormTemplateId = templateId, DynamicFormFamilyId = fixtureForm.FamilyId, DynamicFormVersionNo = fixtureForm.VersionNo, DynamicFormSchemaHash = fixtureForm.PublishedSchemaHash,
        AssigneeUserId = user.Id, AssigneeUnitId = user.UnitId, PeriodKey = "ONCE", PeriodInstanceKey = "ONCE",
        DueAtUtc = DateTime.UtcNow.Date.AddDays(1), IsActive = true, Note = run }, cancellationToken: ct);
}
await tdtd_be.Data.Indexes.DynamicFormRuntimeProvenanceBackfill.RunAsync(db.Db, new MongoOptions(), ct);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "DashboardChecks" });
builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Redis:DashboardWaitRetryCount"] = "2", ["Redis:DashboardWaitDelayMs"] = "10" });
using var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379,abortConnect=true,connectTimeout=3000");
builder.Services.AddSingleton<IConnectionMultiplexer>(redis); builder.Services.AddSingleton(db);
builder.Services.AddHttpContextAccessor(); builder.Services.AddScoped<MeAccessor>(); builder.Services.AddScoped<RedisDashboardCache>();
builder.Services.AddScoped<IDashboardOverviewService, DashboardOverviewService>(); builder.Services.AddScoped<IDashboardQueryService, DashboardQueryService>();
builder.Services.AddScoped<IDashboardMindMapQueryService, DashboardMindMapQueryService>();
builder.Services.AddScoped<IDocRoleReadModelProjectionService, DocRoleReadModelProjectionService>(); builder.Services.AddScoped<IDocRoleService, DocRoleService>();
builder.Services.AddControllers().AddApplicationPart(typeof(DashboardController).Assembly);
var signing = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => o.TokenValidationParameters = new() {
    ValidateIssuer = true, ValidIssuer = run, ValidateAudience = true, ValidAudience = run, ValidateLifetime = true,
    IssuerSigningKey = signing, ValidateIssuerSigningKey = true });
builder.Services.AddAuthorization();
await using var app = builder.Build();
app.Use(async (context, next) => { try { await next(); } catch (AppException ex) { context.Response.StatusCode = ex.Descriptor.HttpStatus;
    await context.Response.WriteAsJsonAsync(new { code = ex.Code.ToString() }); } });
app.UseAuthentication();
app.Use(async (context, next) => {
    var id = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    var user = await db.Users.Find(x => x.Id == id && !x.IsDeleted).FirstOrDefaultAsync(context.RequestAborted);
    if (user is not null) {
        var unit = await db.Units.Find(x => x.Id == user.UnitId).FirstOrDefaultAsync(context.RequestAborted);
        context.Items[MeAccessor.MeItemKey] = new MeResponse(user.Id, user.Username, user.FullName, unit?.UnitTypeCodes ?? [],
            user.UnitId ?? "", unit?.Symbol, unit?.FullName, unit?.Code, user.Roles, user.PositionCode, user.IsDeleted, user.AccountKind);
    }
    await next();
});
app.UseAuthorization(); app.MapControllers(); await app.StartAsync(ct);
try {
    var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    Console.WriteLine("OWN_HOST " + address + " FIXTURE " + run);
    using var http = new HttpClient { BaseAddress = new Uri(address + "/api/") };
    void Actor(AppUser user) => http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(run, run, [new Claim(ClaimTypes.NameIdentifier, user.Id)],
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new(signing, SecurityAlgorithms.HmacSha256))));
    async Task<JsonElement> Read(string path) { var response = await http.GetAsync(path, ct); var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new Exception(path + " " + response.StatusCode + " " + body); return JsonDocument.Parse(body).RootElement.Clone(); }
    async Task<JsonElement> Overview(string mode, object? data = null) { var r = await http.PostAsJsonAsync("dashboard/overview", data ?? new { mode }, ct);
        var body = await r.Content.ReadAsStringAsync(ct); if (!r.IsSuccessStatusCode) throw new Exception(mode + " " + r.StatusCode + " " + body);
        return JsonDocument.Parse(body).RootElement.Clone(); }
    Actor(head);
    var list = await Read("dashboard-mindmap/works?type=1&q=" + Uri.EscapeDataString("THá»¬ RIÃŠNG DASHBOARD"));
    var ids = list.GetProperty("rows").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToHashSet();
    Check(ids.SetEquals(new[] { own.Id, teamWork.Id, commWork.Id, received.Id }), "department head sees own/subunit/received Works only");
    var tree = await Read("dashboard-mindmap/works/" + received.Id);
    Check(!tree.ToString().Contains(otherOfficer.Username), "shared root masks other recipient");
    Check(tree.GetProperty("rootAssignments").GetProperty("rows")[0].GetProperty("hasChildren").GetBoolean(), "shared root keeps expansion for visible child");
    var siblingResult = await http.GetAsync("dashboard-mindmap/nodes/" + theirChild.Id + "/children", ct);
    Check(siblingResult.StatusCode == HttpStatusCode.Forbidden, "sibling branch denied by direct ID: " + siblingResult.StatusCode + " " + await siblingResult.Content.ReadAsStringAsync(ct));
    var children = await Read("dashboard-mindmap/nodes/" + root.Id + "/children");
    Check(children.GetProperty("rows").GetArrayLength() == 1, "shared root children exclude sibling branch");
    var groups = await Read("dashboard-mindmap/nodes/" + root.Id + "/template-groups");
    Check(groups[0].GetProperty("userCount").GetInt32() == 1 && groups[0].GetProperty("reportCount").GetInt32() == 1, "template counters use authorized recipients");
    var usersResult = await Read($"dashboard-mindmap/nodes/{root.Id}/forms/{templateId}/users");
    Check(usersResult.GetProperty("totalRows").GetInt64() == 1, "template users exclude sibling recipient");
    var reportsResult = await http.PostAsJsonAsync($"dashboard-mindmap/nodes/{root.Id}/forms/{templateId}/reports/search", new { }, ct);
    Check(reportsResult.IsSuccessStatusCode, "template report search endpoint accepts unit-head scope");
    var detail = await Read("dashboard/works/" + received.Id);
    Check(detail.GetProperty("reportSummary").GetProperty("total").GetInt32() == 1, "cached detail excludes sibling recipient periods");
    var tasks = await Overview("WORK_TASK");
    Check(tasks.GetProperty("rows").GetArrayLength() == 4, "overview includes unit-created and received tasks");
    var indicators = await Overview("WORK_TARGET");
    Check(indicators.GetProperty("rows").GetArrayLength() == 1, "INDICATOR enum reaches indicator overview");
    Check((await http.GetAsync("dashboard/works/" + outside.Id, ct)).StatusCode == HttpStatusCode.NotFound, "outside Work detail denied");
    Actor(deputy);
    Check((await http.GetAsync("dashboard/works/" + own.Id, ct)).StatusCode == HttpStatusCode.NotFound, "deputy does not inherit head authority");
    Actor(commHead);
    var commList = await Read("dashboard-mindmap/works?type=1");
    Check(commList.GetProperty("rows").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToHashSet().SetEquals(new[] { commWork.Id, received.Id }), "commune head sees own and received Works, not parent/sibling units");
    Check((await http.GetAsync("dashboard-mindmap/nodes/" + root.Id + "/children", ct)).StatusCode == HttpStatusCode.Forbidden, "commune cannot drill into upstream parent");
    Actor(head);
    await Read("dashboard/works/" + received.Id); // Warm Redis before recipient revocation.
    await db.WorkAssignments.UpdateOneAsync(x => x.Id == root.Id, Builders<WorkAssignment>.Update.Set(x => x.Assignees, new List<UserRef> { Ref(otherOfficer) }), cancellationToken: ct);
    await db.WorkAssignments.UpdateOneAsync(x => x.Id == ourChild.Id, Builders<WorkAssignment>.Update.Set(x => x.IsActive, false), cancellationToken: ct);
    await db.WorkAssignments.UpdateOneAsync(x => x.Id == commReceived.Id, Builders<WorkAssignment>.Update.Set(x => x.IsActive, false), cancellationToken: ct);
    Check((await http.GetAsync("dashboard/works/" + received.Id, ct)).StatusCode == HttpStatusCode.NotFound, "revoked assignment cannot replay cached detail");
    await db.Users.UpdateOneAsync(x => x.Id == head.Id, Builders<AppUser>.Update.Set(x => x.UnitId, other.Id), cancellationToken: ct);
    Check((await http.GetAsync("dashboard/works/" + own.Id, ct)).StatusCode == HttpStatusCode.NotFound, "changed unit cannot replay previous scope cache");
    Actor(director);
    Check((await http.GetAsync("dashboard-mindmap/works/" + outside.Id, ct)).IsSuccessStatusCode, "existing director global-read policy preserved");
    var cache = new RedisDashboardCache(redis, builder.Configuration); var key = run + ":lock-refresh";
    await cache.SetAsync(key, "old", ct: ct); await cache.TryAcquireLockAsync(key, "fixture-holder");
    var fresh = await cache.GetOrCreateAsync(key, _ => Task.FromResult("fresh"), ct, forceRefresh: true);
    Check(fresh == "fresh", "force refresh never returns old cached value behind held lock");
    Check(await cache.GetAsync<string>(key, ct) == "fresh", "refresh stores the new value even when lock waiting expires");
    await cache.ReleaseLockAsync(key, "fixture-holder"); await redis.GetDatabase().KeyDeleteAsync(key);
    var raceKey = run + ":refresh-race";
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var older = cache.GetOrCreateAsync(raceKey, async _ => { started.SetResult(); await finish.Task; return "older"; }, ct);
    await started.Task;
    Check(await cache.GetOrCreateAsync(raceKey, _ => Task.FromResult("newer"), ct, forceRefresh: true) == "newer", "refresh proceeds during an earlier computation");
    finish.SetResult(); await older;
    Check(await cache.GetAsync<string>(raceKey, ct) == "newer", "earlier writer cannot overwrite the refreshed cache");
    await redis.GetDatabase().KeyDeleteAsync(new RedisKey[] { raceKey, raceKey + ":refresh-generation", key + ":refresh-generation" });
    Console.WriteLine($"TOTAL {checks} PASS; fixture remains named {run}; no acceptance data changed.");
} finally { await app.StopAsync(); }
