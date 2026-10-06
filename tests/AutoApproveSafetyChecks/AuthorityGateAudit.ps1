$ErrorActionPreference = 'Stop'
$auditRoot = $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Join-Path $auditRoot '../..'))
$source = [IO.File]::ReadAllText((Join-Path $repoRoot 'Services/WorkAssignments/WorkAssignmentService.cs'))
$authority = [IO.File]::ReadAllText((Join-Path $repoRoot 'Services/WorkAssignments/Internal/WorkAssignmentCurrentAuthority.cs'))
# Compile the gate from the service verbatim, rather than copying a second implementation.
$gateStart = $source.IndexOf('    private static bool CanConfigureDataSourceRules(')
$gateEnd = $source.IndexOf('    private ', $gateStart + 20)
$gate = $source.Substring($gateStart, $gateEnd - $gateStart).Trim()
$authorityStart = $authority.IndexOf('    public static string? ReviewerId(')
$authorityEnd = $authority.IndexOf('    public static bool CanLeadChild(', $authorityStart)
$reviewer = $authority.Substring($authorityStart, $authorityEnd - $authorityStart).Trim()
$endpointStart = $source.IndexOf('    public async Task<WorkAssignmentResponse?> UpdateAutoApproveConditionAsync(')
$endpointEnd = $source.IndexOf('    public async Task<WorkAssignmentResponse?> CompleteAsync(', $endpointStart)
$endpoint = $source.Substring($endpointStart, $endpointEnd - $endpointStart)
if (!$endpoint.Contains('if (!CanConfigureDataSourceRules(entity, actorUserId))')) { throw 'Endpoint gate changed; reassess audit instead of reporting stale results.' }
$scopeStart = $source.IndexOf('    private async Task EnsureAssignmentMutationScopeOpenAsync(')
$scopeEnd = $source.IndexOf('    private async Task EnsureNoCompletedAncestorAsync(', $scopeStart)
$scope = $source.Substring($scopeStart, $scopeEnd - $scopeStart)
if ($scope.Contains('IsReviewer(') -or $scope.Contains('EnsureCanReview')) { throw 'Additional scope authority changed; reassess.' }
$outDir = Join-Path $auditRoot '.authority-audit'
[IO.Directory]::CreateDirectory($outDir) | Out-Null
[IO.File]::WriteAllText((Join-Path $outDir 'GateAudit.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
$harness = @'
using System;
using System.Collections.Generic;
using System.Linq;
var node = new WorkAssignment { CreatedByUserId = "old-reviewer", CurrentReviewerUserId = "reviewer", Assignees = new() { new() { UserId = "recipient" }, new() { UserId = "reviewer" } } };
Check(Gate.Actual(node, "recipient"), "CURRENT service gate allows assignee-only");
Check(!Gate.Candidate(node, "recipient"), "PROPOSED reviewer-only gate denies assignee-only");
Check(Gate.Actual(node, "reviewer") && Gate.Candidate(node, "reviewer"), "current reviewer also assignee stays allowed");
Check(!Gate.Actual(node, "old-reviewer") && !Gate.Candidate(node, "old-reviewer"), "old creator after handover stays denied");
Check(!Gate.Actual(node, "unit-admin") && !Gate.Candidate(node, "unit-admin"), "unit admin without assignment role denied");
Check(!Gate.Actual(node, "parent-reviewer") && !Gate.Candidate(node, "parent-reviewer"), "parent reviewer without role on node denied");
node.CurrentReviewerUserId = null;
Check(Gate.Actual(node, "old-reviewer") && Gate.Candidate(node, "old-reviewer"), "legacy creator fallback remains");
Console.WriteLine("PASS 7 compiled service/authority gate checks; not full endpoint or DB integration.");
void Check(bool result, string label) { if (!result) throw new Exception(label); Console.WriteLine(label); }
class UserRef { public string? UserId { get; set; } }
class WorkAssignment { public string? CreatedByUserId { get; set; } public string? CurrentReviewerUserId { get; set; } public List<UserRef>? Assignees { get; set; } }
static class WorkAssignmentCurrentAuthority {
__AUTHORITY__
}
static class Gate {
public static bool Actual(WorkAssignment node, string actor) => CanConfigureDataSourceRules(node, actor);
public static bool Candidate(WorkAssignment node, string actor) => WorkAssignmentCurrentAuthority.IsReviewer(node, actor);
__SERVICE_GATE__
}
'@
$harness = $harness.Replace('__AUTHORITY__', $reviewer).Replace('__SERVICE_GATE__', $gate)
[IO.File]::WriteAllText((Join-Path $outDir 'Program.cs'), $harness)
[IO.File]::WriteAllText((Join-Path $outDir 'NuGet.Config'), '<configuration><packageSources><clear /></packageSources></configuration>')
dotnet restore (Join-Path $outDir 'GateAudit.csproj') --configfile (Join-Path $outDir 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Isolated offline restore failed.' }
dotnet run --no-restore --project (Join-Path $outDir 'GateAudit.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Gate audit failed.' }
