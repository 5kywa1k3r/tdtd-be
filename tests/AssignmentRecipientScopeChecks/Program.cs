using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using tdtd_be.Common.Errors;
using tdtd_be.Controllers;
using tdtd_be.DTOs.Pickers;
using tdtd_be.Models;
using tdtd_be.Options;
using tdtd_be.Services;
using tdtd_be.Services.WorkAssignments;
using tdtd_be.Services.WorkAssignments.Internal;

if (args.Length == 2 && args[0] == "--live-read") { await LiveRead.RunAsync(args[1]); return; }
if (args.Length == 2 && args[0] == "--runtime-source-audit") { RuntimeSourceAudit.Run(args[1]); return; }

int checks = 0;
void Check(bool result, string name) { if (!result) throw new Exception(name); checks++; }
Unit U(string id, string code, bool deleted = false) => new() { Id = id, Code = code, FullName = id, IsDeleted = deleted };
AppUser Person(string id, string unit) => new() { Id = id, UnitId = unit, FullName = id, Username = id, AccountKind = ManagementAccountKind.NormalUser };
AppUser Manager(string id, string unit) => new() { Id = id, UnitId = unit, FullName = id, Username = id, AccountKind = ManagementAccountKind.UnitManager };
var root = U("pv01", "100001002");
var units = new[] { root, U("team1", "100001002001"), U("team2", "100001002002"),
    U("leaf", "100001002001003"), U("outside", "100001003"), U("bad", "1000010020010"),
    U("blank", ""), U("inactive", "100001002004", true), U("zeros", "001002003") }.ToDictionary(x => x.Id);
var actor = Manager("actor", root.Id);
var own = Person("own", root.Id);
var child = Person("child", "team1");
var leaf = Person("grandchild", "leaf");
var other = Person("other", "team2");
var a = Manager("unitA", "team1");
var policy = new WorkAssignmentTargetScopePolicy(Options.Create(new WorkAssignmentScopeOptions {
    UnitTypeAssignmentRules = [new() { ActorUnitTypeCodes = ["PHONG"], TargetUnitTypeCodes = ["PHOI_HOP"],
        TargetAccountKinds = [ManagementAccountKind.UnitManager, ManagementAccountKind.NormalUser] }] }));
root.PrimaryUnitTypeCode = "PHONG";
units["outside"].PrimaryUnitTypeCode = "PHOI_HOP";
bool Allowed(AppUser target) => WorkAssignmentTargetScopeValidator.CanAssignTarget(actor, root, target, units, true, policy);
void Valid(params AppUser[] targets) => WorkAssignmentTargetScopeValidator.EnsureCanAssignTargets(actor, root, targets, units, true, policy);
void Rejected(string name, params AppUser[] targets) {
    try { Valid(targets); throw new Exception("unexpected acceptance: " + name); }
    catch (AppException) { checks++; }
}
Valid(a); checks++;
Valid(own); checks++;
Valid(child, leaf); checks++;
Valid(a, other); checks++;
Rejected("direct overlap", a, child);
Rejected("deep overlap", leaf, a);
Rejected("self", actor);
Rejected("outside", Person("foreign", "outside"));
Valid(Manager("coordination", "outside")); checks++;
Check(!Allowed(Person("foreign", "outside")), "coordination never grants personnel scope");
foreach (var kind in new[] { ManagementAccountKind.NormalUser, ManagementAccountKind.LevelManager, "SYSTEM_ADMIN" }) {
    actor.AccountKind = kind;
    actor.Username = "actor";
    Check(Allowed(own) && Allowed(leaf) && !Allowed(Person("foreign", "outside")), "all actors have bounded personnel scope " + kind);
    Rejected("old user-list manager overlap", a, child);
}
actor.AccountKind = ManagementAccountKind.UnitManager;
foreach (var id in new[] { "bad", "blank", "inactive", "missing" }) Check(!Allowed(Person("invalid", id)), "invalid unit " + id);
child.IsDeleted = true; Check(!Allowed(child), "inactive account"); child.IsDeleted = false;
Check(!Allowed(new AppUser { Id = "unsupported", UnitId = root.Id, AccountKind = ManagementAccountKind.LevelManager }), "level account is not direct personnel");
var explicitPerson = Person("explicit-kind", "team1"); explicitPerson.Username = "mu_personal";
Check(Allowed(explicitPerson) && !WorkAssignmentTargetScopeValidator.IsUnitRecipient(explicitPerson), "actual explicit kind wins over username");
var legacyUnit = Manager("legacy-unit", "team1"); legacyUnit.AccountKind = null; legacyUnit.Username = "mu_legacy";
Rejected("legacy unit account in user array", legacyUnit, leaf);
Check(WorkAssignmentTargetScopeValidator.FindOverlaps([a, child, leaf, other], units).Count == 2, "prefix-segment exclusion");
Check(WorkAssignmentTargetScopeValidator.FindOverlaps([child, leaf, other], units).Count == 0, "removal clears conflicts");
var zeroActor = Manager("zeroActor", "zeros");
Check(WorkAssignmentTargetScopeValidator.CanAssignTarget(zeroActor, units["zeros"], Person("zero", "zeros"), units, false), "leading zeros retained");
var unauthorizedBranchActor = Person("staff", root.Id); unauthorizedBranchActor.PositionCode = "CAN_BO";
Check(!WorkAssignmentCurrentAuthority.CanLeadChild(unauthorizedBranchActor), "subtree membership never grants child authority");
var parent = new WorkAssignment { CreatedByUserId = "former", CurrentReviewerUserId = "current" };
try { WorkAssignmentCreateScopeGuard.EnsureCanCreateBranch(parent, "former"); throw new Exception("former reviewer authority"); }
catch(AppException) { checks++; }
WorkAssignmentCreateScopeGuard.EnsureCanCreateBranch(parent, "current"); checks++;

// Read/write parity independent of presentation order, roles and malformed near-prefix codes.
foreach (var actorKind in new[] { ManagementAccountKind.UnitManager, ManagementAccountKind.NormalUser, ManagementAccountKind.LevelManager })
foreach (var unit in units.Keys)
foreach (var targetKind in new[] { ManagementAccountKind.UnitManager, ManagementAccountKind.NormalUser, ManagementAccountKind.LevelManager, "UNKNOWN" }) {
    actor.AccountKind = actorKind;
    var target = new AppUser { Id = "candidate", UnitId = unit, AccountKind = targetKind };
    var writeAllowed = true;
    try { Valid(target); } catch(AppException) { writeAllowed = false; }
    Check(writeAllowed == Allowed(target), "read/write parity");
}

string Id(int i) => i.ToString("x24");
var group = new Unit { Id = Id(1), Code = "100001", IsVirtual = true, FullName = "Nhóm" };
var expanded = Enumerable.Range(2, 166).Select(i => new Unit { Id = Id(i), Code = "100001" + i.ToString("D3"),
    ParentUnitId = group.Id, FullName = "Đơn vị " + i }).Prepend(group).ToList();
var scope = new AssignmentPickerScope(expanded, [], expanded.Select(x => x.Id).ToHashSet());
Check(WorkAssignmentUnitRecipients.TryExpand([group], expanded, out var allMembers, out _) && allMembers.Count == 166,
    "assignment-only expansion includes every concrete member");
Check(!WorkAssignmentUnitRecipients.TryExpand([group], [new Unit { Id = "bad", Code = "1000010010" }], out _, out _),
    "malformed group member rejects whole group");
Check(!WorkAssignmentUnitRecipients.TryExpand([group], [], out _, out _), "empty group rejected");
Check(!WorkAssignmentUnitRecipients.TryExpand([U("invalidRoot", "1000010")], expanded, out _, out _), "invalid selected code rejected");
Check(WorkAssignmentUnitRecipients.RequireSingleManagers(["A"], [Manager("unitA", "A")]).SequenceEqual(["unitA"]), "unit resolves only its representative");
foreach (var accounts in new[] { Array.Empty<AppUser>(), new[] { Person("chief", "A") },
    new[] { Manager("one", "A"), Manager("two", "A") } }) {
    try { WorkAssignmentUnitRecipients.RequireSingleManagers(["A"], accounts); throw new Exception("ambiguous/missing manager accepted"); }
    catch (AppException ex) { Check(ex.Code == AppErrorCode.UNIT_MANAGER_MISSING, "no manager fallback"); }
}
var service = DispatchProxy.Create<IWorkAssignmentService, ScopeProxy>();
((ScopeProxy)(object)service).Scope = scope;
var controller = new ScopedPickerController(null!, null!, null!, service);
var request = new ScopedPickerRequest { Context = new() { Purpose = PickerPurpose.AssignmentRecipients, WorkId = Id(999) },
    Operation = "expandUnits", Ids = [group.Id] };
var response = await controller.Query(request, default);
Check(response is OkObjectResult, "full expansion accepted");
var json = JsonSerializer.SerializeToElement(((OkObjectResult)response).Value);
Check(json.GetProperty("rows").GetArrayLength() == 166, "no first-page truncation");
scope.SelectableUnitIds.Remove(group.Id);
Check(await controller.Query(request, default) is BadRequestObjectResult, "invalid whole group rejected");

var personId = Id(1000);
scope = new AssignmentPickerScope([group, expanded[1]], [Person(personId, expanded[1].Id)], []) {
    UserTotalRows = 100,
    UserConflicts = new() { [personId] = expanded[1] }
};
((ScopeProxy)(object)service).Scope = scope;
response = await controller.Query(new() { Context = request.Context, Operation = "restoreUsers", Ids = [personId] }, default);
json = JsonSerializer.SerializeToElement(((OkObjectResult)response).Value);
var row = json.GetProperty("rows")[0];
Check(!row.GetProperty("Selectable").GetBoolean() && row.GetProperty("ConflictUnitId").GetString() == expanded[1].Id,
    "conflicting draft stays readable with safe metadata");
Check(json.GetProperty("totalRows").GetInt32() == 100, "server paging total retained");
Console.WriteLine($"PASS {checks} assignment-recipient policy/controller checks; synthetic accounts only; no DB/HTTP or notifications.");

public class ScopeProxy : DispatchProxy {
    public AssignmentPickerScope Scope { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == nameof(IWorkAssignmentService.GetPickerScopeAsync)
        ? Task.FromResult(Scope) : throw new NotSupportedException();
}
