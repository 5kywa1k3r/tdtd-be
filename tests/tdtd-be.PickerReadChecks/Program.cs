using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using tdtd_be.Controllers;
using tdtd_be.DTOs.Pickers;
using tdtd_be.Models;
using tdtd_be.Options;
using tdtd_be.Services;
using tdtd_be.Services.WorkAssignments;
using tdtd_be.Services.WorkAssignments.Internal;

static void Check(bool result, string name) { if (!result) throw new Exception(name); }
var actorUnit = new Unit { Id = "a", Code = "100001", Level = 3, ParentUnitId = "root", PrimaryUnitTypeCode = "PHONG" };
var units = new[] { actorUnit,
    new Unit { Id = "peer", Code = "100002", Level = 3, ParentUnitId = "root" },
    new Unit { Id = "child", Code = "100001001", Level = 4, ParentUnitId = "a" },
    new Unit { Id = "outside", Code = "200001", Level = 3, ParentUnitId = "other" },
    new Unit { Id = "commune", Code = "300001", Level = 4, ParentUnitId = "virtual", PrimaryUnitTypeCode = "PHUONG_XA" },
}.ToDictionary(x => x.Id);
var actor = new AppUser { Id = "actor", AccountKind = ManagementAccountKind.UnitManager, UnitId = "a" };
var policy = new WorkAssignmentTargetScopePolicy(Options.Create(new WorkAssignmentScopeOptions {
    UnitTypeAssignmentRules = [new() { ActorUnitTypeCodes = ["PHONG"], TargetUnitTypeCodes = ["PHUONG_XA"], TargetAccountKinds = [ManagementAccountKind.UnitManager] }] }));
int cases = 0;
foreach (var actorKind in new[] { ManagementAccountKind.UnitManager, ManagementAccountKind.NormalUser })
foreach (var targetKind in new[] { ManagementAccountKind.UnitManager, ManagementAccountKind.NormalUser, ManagementAccountKind.LevelManager, "UNKNOWN", "" })
foreach (var targetUnit in new[] { "a", "peer", "child", "outside", "commune", "missing" })
foreach (var descendants in new[] { true, false })
foreach (var legacy in new[] { true, false }) {
    actor.AccountKind = actorKind;
    var target = new AppUser { Id = "target", UnitId = targetUnit, AccountKind = targetKind, Username = legacy ? "mu_target" : "target" };
    bool write = true;
    try { WorkAssignmentTargetScopeValidator.EnsureCanAssignTargets(actor, actorUnit, [target], units, descendants, policy); }
    catch (tdtd_be.Common.Errors.AppException) { write = false; }
    Check(write == WorkAssignmentTargetScopeValidator.CanAssignTarget(actor, actorUnit, target, units, descendants, policy), $"parity {targetKind}/{targetUnit}/{legacy}");
    cases++;
}
actor.AccountKind = ManagementAccountKind.UnitManager;
Check(!WorkAssignmentTargetScopeValidator.CanAssignTarget(actor, actorUnit, actor, units, true, policy), "self");
Check(!WorkAssignmentTargetScopeValidator.CanAssignTarget(actor, null, new AppUser(), units, true, policy), "actor unit missing");
foreach (var (unit, kind, expected) in new[] { ("peer", ManagementAccountKind.UnitManager, true), ("commune", ManagementAccountKind.UnitManager, true), ("outside", ManagementAccountKind.UnitManager, false), ("child", ManagementAccountKind.NormalUser, true) })
    Check(WorkAssignmentTargetScopeValidator.CanAssignTarget(actor, actorUnit, new AppUser { Id = "target", UnitId = unit, AccountKind = kind }, units, true, policy) == expected, $"expected {unit}/{kind}");

var grandchild = new Unit { Id = "grandchild", Code = "100001001001", Level = 5, ParentUnitId = "child" };
units[grandchild.Id] = grandchild;
Check(WorkAssignmentUnitHierarchy.IsStrictAncestor(actorUnit, units["child"], units), "PA02 above team");
Check(WorkAssignmentUnitHierarchy.IsStrictAncestor(actorUnit, grandchild, units), "PA02 above grandchild");
Check(!WorkAssignmentUnitHierarchy.IsStrictAncestor(units["child"], actorUnit, units), "team not above PA02");
Check(!WorkAssignmentUnitHierarchy.IsStrictAncestor(actorUnit, units["peer"], units), "peer not child");
Check(!WorkAssignmentUnitHierarchy.IsStrictAncestor(actorUnit, actorUnit, units), "same unit not enough");
var virtualUnit = new Unit { Id = "virtual", IsVirtual = true, ParentUnitId = "a" };
units[virtualUnit.Id] = virtualUnit;
Check(!WorkAssignmentUnitHierarchy.IsStrictAncestor(actorUnit, new Unit { Id = "virtual-child", ParentUnitId = "virtual" }, units), "virtual does not confer authority");
var childAssignment = new WorkAssignment { Id = "child-assignment", CreatedByUserId = "pa02" };
Check(WorkAssignmentCurrentAuthority.IsReviewer(childAssignment, "pa02"), "legacy creator reviews");
childAssignment.CurrentReviewerUserId = "leader";
Check(WorkAssignmentCurrentAuthority.IsReviewer(childAssignment, "leader") &&
    !WorkAssignmentCurrentAuthority.IsReviewer(childAssignment, "pa02"), "handover moves current review without audit rewrite");
bool formerReviewerCanCreateGrandchild = true;
try { WorkAssignmentCreateScopeGuard.EnsureCanCreateBranch(childAssignment, "pa02"); }
catch (tdtd_be.Common.Errors.AppException) { formerReviewerCanCreateGrandchild = false; }
Check(!formerReviewerCanCreateGrandchild, "former creator cannot create a grandchild after handover");
WorkAssignmentCreateScopeGuard.EnsureCanCreateBranch(childAssignment, "leader");
Check(!WorkAssignmentCurrentAuthority.CanLeadChild(new AppUser { AccountKind = ManagementAccountKind.NormalUser, PositionCode = "CAN_BO" }), "staff cannot create child");
Check(WorkAssignmentCurrentAuthority.CanLeadChild(new AppUser { AccountKind = ManagementAccountKind.NormalUser, PositionCode = "PHO_TRUONG_PHONG" }), "deputy can manage a child unit");
var completedChild = new WorkAssignment { Id = "completed", CreatedByUserId = "pa02", CompletedAtUtc = DateTime.UtcNow,
    Assignees = [new UserRef { UserId = "team-chief", UnitId = "child" }] };
Check(WorkAssignmentHandoverChildScope.Allows("pa02", actorUnit, [completedChild], units), "completed child still requires higher unit");
Check(!WorkAssignmentHandoverChildScope.Allows("pa02", units["child"], [completedChild], units), "same team cannot receive completed child");
var siblingChild = new WorkAssignment { Id = "sibling", CreatedByUserId = "pa02", Assignees = [new UserRef { UserId = "peer", UnitId = "peer" }] };
Check(!WorkAssignmentHandoverChildScope.Allows("pa02", actorUnit, [completedChild, siblingChild], units), "one invalid child rejects whole handover");
completedChild.CurrentReviewerUserId = "other-leader";
Check(!WorkAssignmentHandoverChildScope.Allows("pa02", actorUnit, [completedChild], units), "cannot take over another reviewer's child");

int leaderCases = 0;
foreach (var position in new[] { "TRUONG_PHONG", "PHO_TRUONG_PHONG", "PHO_TRUONG_PHONG_PHU_TRACH", " pho_truong_phong ", "CAN_BO", "DOI_TRUONG", "" })
foreach (var unit in new[] { "a", "peer", "child", "outside" })
foreach (var descendants in new[] { true, false })
{
    var target = new AppUser { Id = "leader-target", UnitId = unit, AccountKind = ManagementAccountKind.NormalUser, PositionCode = position };
    var expected = unit is "a" or "child";
    Check(WorkAssignmentTargetScopeValidator.CanAssignTarget(actor, actorUnit, target, units, descendants, policy) == expected, $"leader read {unit}/{position}/{descendants}");
    bool write = true;
    try { WorkAssignmentTargetScopeValidator.EnsureCanAssignTargets(actor, actorUnit, [target], units, descendants, policy); }
    catch (tdtd_be.Common.Errors.AppException) { write = false; }
    Check(write == expected, $"leader write {unit}/{position}/{descendants}");
    leaderCases++;
}

string Id(int i) => i.ToString("x24");
var group = new Unit { Id = Id(1), Code = "100001", IsVirtual = true, FullName = "Công an xã, phường" };
var all = Enumerable.Range(2, 166).Select(i => new Unit { Id = Id(i), Code = "100001" + i.ToString("D3"), ParentUnitId = group.Id, FullName = "CAX " + i }).Prepend(group).ToList();
var scope = new AssignmentPickerScope(all, [], all.Select(x => x.Id).ToHashSet());
var service = DispatchProxy.Create<IWorkAssignmentService, ScopeProxy>();
((ScopeProxy)(object)service).Scope = scope;
var controller = new ScopedPickerController(null!, null!, null!, service);
var request = new ScopedPickerRequest { Context = new() { Purpose = PickerPurpose.AssignmentRecipients, WorkId = Id(999) }, Operation = "expandUnits", Ids = [group.Id] };
var response = await controller.Query(request, default);
Check(response is OkObjectResult, "expand ok");
var data = JsonSerializer.SerializeToElement(((OkObjectResult)response).Value);
Check(data.GetProperty("totalRows").GetInt32() == 166 && data.GetProperty("rows").GetArrayLength() == 166, "full group, no page truncation");
scope.SelectableUnitIds.Remove(group.Id);
Check(await controller.Query(request, default) is BadRequestObjectResult, "forbidden whole group rejected");
Console.WriteLine($"PASS {cases} read/write policy cases, {leaderCases} department-leader read/write cases, 6 explicit boundary cases, full 166-unit expansion and forbidden-group rejection. Synthetic scope; no DB writes or HTTP authentication test.");

public class ScopeProxy : DispatchProxy {
    public AssignmentPickerScope Scope { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == nameof(IWorkAssignmentService.GetPickerScopeAsync) ? Task.FromResult(Scope) : throw new NotSupportedException();
}
