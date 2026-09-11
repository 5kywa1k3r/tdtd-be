using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using MongoDB.Bson;
using tdtd_be.Common.Capabilities;
using tdtd_be.Common.Errors;
using tdtd_be.Controllers;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowRuntimePreflightContractTests
{
    public static void Run()
    {
        AssertTypedApiContract();
        AssertDeterministicTargetDedupe();
        AssertEligibilityMatrix();
        AssertConfirmAndReplayContract();
        AssertCanonicalConfirmConflictMapping();
        AssertCanonicalPlannerAndExactPins();
        AssertAuthorizationPrecedesSensitiveLoads();
        AssertLegacyBranchCommandsStopBeforeReadsAndWrites();
        AssertNoP502BusinessWriter();
    }

    private static void AssertTypedApiContract()
    {
        AssertRoute(nameof(DynamicFlowRuntimeController.Preflight), "works/{workId}/dynamic-flows/preflight");
        AssertRoute(nameof(DynamicFlowRuntimeController.Confirm), "works/{workId}/dynamic-flows/confirm");
        AssertProperties<DynamicFlowPreflightRequest>(
            "FlowTemplateVersionId", "CommandId", "TargetUnitIds", "PeriodKey", "ScheduleIdentityJson");
        AssertProperties<DynamicFlowConfirmRequest>("SnapshotToken");
        AssertProperties<DynamicFlowPreflightResponse>(
            "RequestHash", "SnapshotToken", "Eligibility", "FlowPin", "EntryStep", "FormPins", "Targets",
            "PeriodKey", "ScheduleIdentityHash");
        AssertProperties<DynamicFlowConfirmResponse>(
            "CommandId", "RequestHash", "SnapshotToken", "Status", "BusinessWritePerformed");
    }

    private static void AssertDeterministicTargetDedupe()
    {
        var a = Oid(1);
        var b = Oid(2);
        var result = DynamicFlowRuntimePreflightContract.NormalizeTargets(new[] { b, a, b, $" {a} " });
        Require(result.SequenceEqual(new[] { a, b }, StringComparer.Ordinal), "targets must trim, dedupe, and sort before write");
        AssertThrows(
            () => DynamicFlowRuntimePreflightContract.NormalizeTargets(Array.Empty<string>()),
            "empty targets must fail closed");
        AssertThrows(
            () => DynamicFlowRuntimePreflightContract.NormalizeTargets(new[] { "not-object-id" }),
            "invalid target identity must fail closed");
    }

    private static void AssertEligibilityMatrix()
    {
        var v11 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            "1.1",
            "e8a0b15bb5c7cab81ed49ec5c213105366a1194faa2d78168a46226d9cc505cf",
            "FLOW-T01");
        Require(v11.Eligibility == DynamicFlowRuntimeEligibilityPolicy.BlockedCatalog, "catalog v1.1 must stay blocked");
        foreach (var archetype in new[] { "FLOW-T01", "FLOW-T02" })
        {
            var result = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
                DynamicFlowRuntimeCatalogCandidate.Version,
                DynamicFlowRuntimeCatalogCandidate.SemanticHash,
                archetype);
            Require(result.Eligibility == DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate, $"{archetype} candidate eligibility drift");
            Require(result.BlockedUntilPhase is null, "promoted candidate must be active after the P5-08 gate");
        }
        for (var number = 3; number <= 12; number++)
        {
            var result = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
                DynamicFlowRuntimeCatalogCandidate.Version,
                DynamicFlowRuntimeCatalogCandidate.SemanticHash,
                $"FLOW-T{number:00}");
            Require(result.Eligibility == DynamicFlowRuntimeEligibilityPolicy.BlockedPhase, $"FLOW-T{number:00} must remain P6 blocked");
            Require(result.BlockedUntilPhase == "P6", $"FLOW-T{number:00} phase drift");
        }
        var p6T03 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            "FLOW-T03");
        Require(
            p6T03.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "FLOW-T03 must be eligible only on the explicit non-current P6 candidate");
        Require(
            p6T03.BlockedUntilPhase is null,
            "testing fixture policy, not catalog inference, owns candidate activation");
        var p6T04 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            "FLOW-T04");
        Require(
            p6T04.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "FLOW-T04 must be eligible only on the explicit non-current P6 candidate");
        Require(
            p6T04.BlockedUntilPhase is null,
            "testing fixture policy, not catalog inference, owns FLOW-T04 candidate activation");
        var p6T05 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            "FLOW-T05");
        Require(
            p6T05.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "FLOW-T05 must be eligible only on the explicit non-current P6 candidate");
        Require(
            p6T05.BlockedUntilPhase is null,
            "testing fixture policy, not catalog inference, owns FLOW-T05 candidate activation");
        var p6T06 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            "FLOW-T06");
        Require(
            p6T06.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "FLOW-T06 must be eligible only on the explicit non-current P6 candidate");
        Require(
            p6T06.BlockedUntilPhase is null,
            "testing fixture policy, not catalog inference, owns FLOW-T06 candidate activation");
        var p6T07 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            "FLOW-T07");
        Require(
            p6T07.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "FLOW-T07 must be eligible only on the explicit non-current P6 candidate");
        var p6T08 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            "FLOW-T08");
        Require(
            p6T08.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "FLOW-T08 must be eligible only on the explicit non-current P6 candidate");
        var p6T09 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            "FLOW-T09");
        Require(
            p6T09.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "FLOW-T09 must be eligible only on the explicit non-current P6 candidate");
        var p6T12 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            "FLOW-T12");
        Require(
            p6T12.Eligibility ==
                DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate &&
            p6T12.BlockedUntilPhase is null,
            "P6-10 must open the final owned FLOW-T12 candidate");
        var future = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            "FLOW-T13");
        Require(
            future.Eligibility ==
                DynamicFlowRuntimeEligibilityPolicy.BlockedPhase &&
            future.BlockedUntilPhase == "P6",
            "future flow archetypes must remain blocked");
        Require(DynamicFlowRuntimeCatalogCandidate.ActivationEnabled, "catalog successor must activate at P5-08 closeout");
        Require(
            DynamicFlowP8CatalogCandidate.IsCompatibleCurrent(
                "1.5",
                "e3c335617721bbf8cd09c62c5e76377f23f3d024b20c848bf66c8c539f0c9d2f") &&
            DynamicFlowP8CatalogCandidate.IsCompatibleCurrent(
                "1.6",
                "39cdb98dda168f5901f48a94640fe5d50943c5bd78ed32d5e05e8b719b23d13b") &&
            DynamicFlowP8CatalogCandidate.IsCompatibleCurrent(
                "1.7",
                "ccb28afafc068ac1b720c046a25276a35d9d828b14f9cc9c9bc690077ca204c1"),
            "P8 compatibility must retain only exact v1.5/v1.6/v1.7 successors");
        Require(
            !DynamicFlowP8CatalogCandidate.IsCompatibleCurrent(null, null) &&
            !DynamicFlowP8CatalogCandidate.IsCompatibleCurrent(
                "1.8",
                "ccb28afafc068ac1b720c046a25276a35d9d828b14f9cc9c9bc690077ca204c1") &&
            !DynamicFlowP8CatalogCandidate.IsCompatibleCurrent(
                "1.7",
                "39cdb98dda168f5901f48a94640fe5d50943c5bd78ed32d5e05e8b719b23d13b") &&
            !DynamicFlowP8CatalogCandidate.IsCompatibleCurrent(
                "1.7",
                new string('0', 64)),
            "unknown, crossed and tampered P8 catalog pins must fail closed");
        Require(
            DynamicFlowP8CatalogCandidate.ActivationEnabled ==
                DynamicFlowP8CatalogCandidate.IsCompatibleCurrent(
                    DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                    DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256),
            "P8 activation must derive only from the exact generated CURRENT pair");
        for (var number = 1; number <= 12; number++)
        {
            var currentSuccessor = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
                DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
                $"FLOW-T{number:00}");
            Require(
                currentSuccessor.Eligibility ==
                    DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate &&
                currentSuccessor.BlockedUntilPhase is null,
                $"generated CURRENT successor must execute FLOW-T{number:00}");
        }
        var tamperedCurrent = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
            new string('0', 64),
            "FLOW-T01");
        Require(
            tamperedCurrent.Eligibility ==
                DynamicFlowRuntimeEligibilityPolicy.BlockedCatalog,
            "generated CURRENT version with a tampered hash must stay blocked");
        Require(
            DynamicFlowP6CatalogCandidate.SemanticHash ==
                "55cfa0a4420e01db6707011ffc7a0271088c5b01b63edb8f2d21978edd3e2497",
            "sealed v1.3 semantic SHA-256 drift");
        var generatedCurrentMatchesV13 =
            DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion ==
                DynamicFlowP6CatalogCandidate.Version &&
            DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256 ==
                DynamicFlowP6CatalogCandidate.SemanticHash;
        Require(
            DynamicFlowP6CatalogCandidate.ActivationEnabled ==
                generatedCurrentMatchesV13,
            "v1.3 activation must exact-match the generated CURRENT version and semantic SHA-256");
        Require(
            typeof(DynamicFlowP6CatalogCandidate).GetProperty(
                nameof(DynamicFlowP6CatalogCandidate.ActivationEnabled),
                BindingFlags.Public | BindingFlags.Static) is not null,
            "v1.3 activation must remain a generated-CURRENT-derived property");
    }

    private static void AssertConfirmAndReplayContract()
    {
        var preflight = new DynamicFlowPreflightResponse
        {
            CommandId = "command-1",
            RequestHash = new string('a', 64),
            SnapshotToken = new string('b', 64),
            Eligibility = DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate
        };
        var exact = DynamicFlowRuntimePreflightContract.Confirm(preflight, new DynamicFlowConfirmRequest
        {
            SnapshotToken = preflight.SnapshotToken
        });
        Require(exact.Status == "PENDING_MATERIALIZATION_NOT_READY", "P5-02 confirm must not claim materialization");
        Require(!exact.BusinessWritePerformed, "P5-02 confirm must be zero-write");
        preflight.Eligibility = DynamicFlowRuntimeEligibilityPolicy.BlockedCatalog;
        var blocked = DynamicFlowRuntimePreflightContract.Confirm(preflight, new DynamicFlowConfirmRequest
        {
            SnapshotToken = preflight.SnapshotToken
        });
        Require(blocked.Status == "BLOCKED_UNTIL_TARGET_PHASE", "v1.1 confirm must remain blocked");
        preflight.Eligibility = DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate;
        AssertThrows(
            () => DynamicFlowRuntimePreflightContract.Confirm(preflight, new DynamicFlowConfirmRequest
            {
                SnapshotToken = new string('c', 64)
            }),
            "stale snapshot token must conflict");

        var receipt = new DynamicFlowRuntimeCommandReceipt
        {
            RequestHash = preflight.RequestHash,
            ResultSnapshot = new BsonDocument("status", "PENDING_MATERIALIZATION_NOT_READY")
        };
        var replayA = DynamicFlowRuntimePreflightContract.ResolveReplay(receipt, preflight.RequestHash);
        var replayB = DynamicFlowRuntimePreflightContract.ResolveReplay(receipt, preflight.RequestHash);
        Require(replayA == replayB, "exact replay must return the same result snapshot");
        AssertThrows(
            () => DynamicFlowRuntimePreflightContract.ResolveReplay(receipt, new string('d', 64)),
            "changed replay must conflict");
    }

    private static void AssertCanonicalConfirmConflictMapping()
    {
        var changedReplay = DynamicFlowRuntimeService.MapCanonicalRuntimeConfirmConflict(
            new InvalidOperationException("DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT"));
        Require(
            changedReplay.Code == AppErrorCode.DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT,
            "changed runtime replay must preserve the canonical error code");
        Require(
            changedReplay.Descriptor.HttpStatus == StatusCodes.Status409Conflict,
            "changed runtime replay must map to HTTP 409");
        Require(
            DetailReason(changedReplay) == "DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT",
            "changed runtime replay must expose the canonical reason");

        var stalePreflight = DynamicFlowRuntimeService.MapCanonicalRuntimeConfirmConflict(
            new InvalidOperationException("DYNAMIC_FLOW_PREFLIGHT_STALE"));
        Require(
            stalePreflight.Code == AppErrorCode.DYNAMIC_FLOW_REVISION_CONFLICT,
            "stale runtime preflight must map to the revision-conflict code");
        Require(
            stalePreflight.Descriptor.HttpStatus == StatusCodes.Status409Conflict,
            "stale runtime preflight must map to HTTP 409");
        Require(
            DetailReason(stalePreflight) == "DYNAMIC_FLOW_PREFLIGHT_STALE",
            "stale runtime preflight must preserve the canonical reason");

        var service = ReadSource("Services/DynamicFlows/DynamicFlowRuntimeService.cs");
        var confirm = Slice(
            service,
            "public async Task<DynamicFlowConfirmResponse> ConfirmAsync(",
            "public async Task<DynamicFlowInstanceLaunchResponse> CreateInstanceAsync(");
        Require(
            confirm.Contains(
                "catch (InvalidOperationException error) when (IsCanonicalRuntimeConfirmConflict(error))",
                StringComparison.Ordinal),
            "canonical confirm API must translate runtime conflict signals before middleware");
    }

    private static string? DetailReason(AppException error)
        => error.Details?
            .GetType()
            .GetProperty("reason", BindingFlags.Instance | BindingFlags.Public)?
            .GetValue(error.Details) as string;

    private static void AssertCanonicalPlannerAndExactPins()
    {
        var source = ReadSource("Services/DynamicFlows/DynamicFlowRuntimePreflightContract.cs");
        Require(source.Contains("payload.Nodes.SingleOrDefault", StringComparison.Ordinal), "preflight must select canonical nodes");
        Require(source.Contains("payload.EntryStepId", StringComparison.Ordinal), "preflight must use canonical entryStepId");
        Require(source.Contains("entry.FormNodeId", StringComparison.Ordinal), "preflight must bind canonical formNodeId");
        Require(
            source.Contains("sequentialTopology.OrderedNodes", StringComparison.Ordinal),
            "T03 step order must come from pinned graph traversal, not serialized node order");
        Require(!source.Contains("payload.Steps", StringComparison.Ordinal), "preflight must not reopen legacy steps");
        Require(!source.Contains("payload.Transitions", StringComparison.Ordinal), "preflight must not reopen legacy transitions");
        foreach (var pin in new[]
                 {
                     "FlowTemplateVersionId", "PayloadHash", "CatalogVersion", "CatalogSemanticHash",
                     "DefinitionRevision", "TopologyHash", "NextStepIds", "IsTerminalNode",
                     "DynamicFormTemplateId", "DynamicFormFamilyId", "DynamicFormVersionNo", "DynamicFormSchemaHash"
                 })
            Require(source.Contains(pin, StringComparison.Ordinal), $"preflight exact pin missing {pin}");
    }

    private static void AssertAuthorizationPrecedesSensitiveLoads()
    {
        var source = ReadSource("Services/DynamicFlows/DynamicFlowRuntimeService.cs");
        var method = Slice(source, "public async Task<DynamicFlowPreflightResponse> PreflightAsync(", "public async Task<DynamicFlowConfirmResponse> ConfirmAsync(");
        var grant = method.IndexOf("if (!hasParticipantGrant && !hasRootIssuerGrant)", StringComparison.Ordinal);
        var familyLoad = method.IndexOf("var family = await _ctx.DynamicFlowTemplates", StringComparison.Ordinal);
        var integrity = method.IndexOf("ValidateLockedSnapshotIntegrityAsync", StringComparison.Ordinal);
        Require(grant >= 0 && familyLoad > grant && integrity > familyLoad, "authorization must precede family/payload/integrity detail");
        Require(method.Contains("ownsWork && DynamicFlowTemplateReadAccess.CanCreateDefinition(actor)", StringComparison.Ordinal),
            "root issuer grant must require both work scope and server-owned capability");
        Require(!method.Contains("req.FlowRole", StringComparison.Ordinal), "client role must not influence authorization");
    }

    private static void AssertLegacyBranchCommandsStopBeforeReadsAndWrites()
    {
        var source = ReadSource("Services/DynamicFlows/DynamicFlowRuntimeService.cs");
        var method = Slice(source, "private async Task<DynamicFlowBranchActionResponse> MutateBranchAsync(", "private async Task WriteFlowEventAsync(");
        var guard = method.IndexOf("throw P6CommandBlocked(action);", StringComparison.Ordinal);
        var firstRead = method.IndexOf("_ctx.WorkAssignments", StringComparison.Ordinal);
        var eventWrite = method.IndexOf("WriteFlowEventAsync", StringComparison.Ordinal);
        Require(guard >= 0 && firstRead > guard && eventWrite > guard, "P6 guard must precede branch reads/writes");
    }

    private static void AssertNoP502BusinessWriter()
    {
        var source = ReadSource("Services/DynamicFlows/DynamicFlowRuntimeService.cs");
        var confirm = Slice(
            source,
            "public async Task<DynamicFlowConfirmResponse> ConfirmAsync(",
            "public async Task<DynamicFlowSupplementalCommandResponse>");
        foreach (var writer in new[]
                 {
                     "_assignments.CreateAsync", "DynamicFlowInstances.Insert", "DynamicFlowStepInstances.Insert",
                     "DynamicFlowRuntimeCommandReceipts.Insert", "DynamicFlowRuntimeEvents.Insert",
                     "DynamicFlowRuntimeOutbox.Insert", "WorkAssignmentReports"
                 })
            Require(!confirm.Contains(writer, StringComparison.Ordinal), $"P5-02 confirm contains forbidden writer {writer}");
        Require(confirm.Contains("BusinessWritePerformed", StringComparison.Ordinal) ||
                confirm.Contains("DynamicFlowRuntimePreflightContract.Confirm", StringComparison.Ordinal),
            "confirm must return explicit zero-write pending result");
    }

    private static void AssertRoute(string name, string template)
    {
        var method = typeof(DynamicFlowRuntimeController).GetMethod(name)
                     ?? throw new MissingMethodException(name);
        var route = method.GetCustomAttributes<HttpPostAttribute>().Single();
        Require(route.Template == template, $"{name} route drift");
    }

    private static void AssertProperties<T>(params string[] names)
    {
        foreach (var name in names)
            Require(typeof(T).GetProperty(name, BindingFlags.Instance | BindingFlags.Public) is not null,
                $"{typeof(T).Name} missing {name}");
    }

    private static string Oid(int seed) => seed.ToString("x24");

    private static void AssertThrows(Action action, string message)
    {
        try { action(); }
        catch { return; }
        throw new InvalidOperationException(message);
    }

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, from + Math.Max(1, start.Length), StringComparison.Ordinal);
        Require(from >= 0 && to > from, $"source slice not found: {start} .. {end}");
        return source[from..to];
    }

    private static string ReadSource(string relativePath)
    {
        var relative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, relative);
            if (File.Exists(direct)) return File.ReadAllText(direct);
            var nested = Path.Combine(directory.FullName, "tdtd-be", relative);
            if (File.Exists(nested)) return File.ReadAllText(nested);
        }
        throw new FileNotFoundException(relativePath);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
