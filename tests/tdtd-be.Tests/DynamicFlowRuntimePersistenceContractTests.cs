using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowRuntimePersistenceContractTests
{
    public static void Run()
    {
        AssertCollectionAndSerializationContract();
        AssertStepInstanceIdentityIsIndependent();
        AssertFrozenStateTransitions();
        AssertRevisionCasContract();
        AssertReceiptEventAndOutboxGrain();
        AssertContextOptionsAndIndexesMatchQueryShapes();
        AssertPersistenceIsAppendOnlyForEventsAndDoesNotWriteLegacyArtifacts();
    }

    private static void AssertCollectionAndSerializationContract()
    {
        Require(Collection<DynamicFlowInstance>() == "dynamic_flow_instances", "instance collection drift");
        Require(Collection<DynamicFlowStepInstance>() == "dynamic_flow_step_instances", "step collection drift");
        Require(Collection<DynamicFlowParticipantSnapshot>() == "dynamic_flow_participant_snapshots", "participant collection drift");
        Require(Collection<DynamicFlowRuntimeCommandReceipt>() == "dynamic_flow_runtime_command_receipts", "receipt collection drift");
        Require(Collection<DynamicFlowRuntimeEvent>() == "dynamic_flow_runtime_events", "event collection drift");
        Require(Collection<DynamicFlowRuntimeOutboxItem>() == "dynamic_flow_runtime_outbox", "outbox collection drift");

        var instance = new DynamicFlowInstance
        {
            Id = Oid(),
            WorkId = Oid(),
            FlowTemplateId = Oid(),
            FlowTemplateVersionId = Oid(),
            FlowTemplateVersionNo = 4,
            FlowPayloadHash = Hash('a'),
            CatalogVersion = "1.2",
            CatalogSemanticHash = Hash('b'),
            EntryFlowStepId = "entry-step",
            ResultOwnerUserId = Oid(),
            ResultOwnerUnitId = Oid(),
            StatisticOwnerIdentity = "owner-only-not-executor",
            PeriodKey = "2026-07",
            ScheduleIdentityHash = Hash('c'),
            ParticipantSnapshotId = Oid(),
            ParticipantSnapshotHash = Hash('d'),
            IssuerUserId = Oid(),
            IssuerUnitId = Oid(),
            LaunchCommandId = "launch-001",
            State = DynamicFlowInstanceStates.Pending,
            Revision = 0
        };
        var bson = instance.ToBsonDocument();
        foreach (var field in new[]
                 {
                     "workId", "flowTemplateId", "flowTemplateVersionId", "flowTemplateVersionNo",
                     "flowPayloadHash", "catalogVersion", "catalogSemanticHash", "entryFlowStepId",
                     "resultOwnerUserId", "resultOwnerUnitId", "statisticOwnerIdentity", "periodKey",
                     "scheduleIdentityHash", "participantSnapshotId", "participantSnapshotHash",
                     "issuerUserId", "issuerUnitId", "launchCommandId", "state", "revision",
                     "nextEventSequence"
                 })
            Require(bson.Contains(field), $"instance BSON missing {field}");

        var roundTrip = BsonSerializer.Deserialize<DynamicFlowInstance>(bson);
        Require(roundTrip.Id == instance.Id && roundTrip.Revision == 0, "instance BSON round trip drift");
        var withUnknown = bson.DeepClone().AsBsonDocument;
        withUnknown["futureField"] = true;
        Require(BsonSerializer.Deserialize<DynamicFlowInstance>(withUnknown).Id == instance.Id, "BSON forward compatibility lost");
    }

    private static void AssertStepInstanceIdentityIsIndependent()
    {
        var step = new DynamicFlowStepInstance
        {
            Id = Oid(),
            FlowInstanceId = Oid(),
            FlowStepId = "definition-step-id",
            FlowStepCode = "ENTRY",
            FormNodeId = "form-node-a",
            FormFamilyId = Oid(),
            FormVersionId = Oid(),
            FormVersionNo = 2,
            FormSchemaHash = Hash('e'),
            TargetUnitId = Oid(),
            ParticipantSnapshotId = Oid(),
            BranchId = Oid(),
            AttemptNo = 1
        };
        Require(step.Id != step.FlowStepId, "step-instance id must not alias definition step id");
        var bson = step.ToBsonDocument();
        Require(bson["_id"].IsObjectId, "step-instance _id must be ObjectId");
        Require(bson["flowStepId"].IsString, "definition step id must retain canonical string identity");
        foreach (var field in new[]
                 {
                     "flowInstanceId", "flowStepId", "targetUnitId", "attemptNo", "participantSnapshotId",
                     "assignmentId", "reportId", "formFamilyId", "formVersionId", "formVersionNo", "formSchemaHash"
                 })
            Require(bson.Contains(field), $"step BSON missing {field}");
    }

    private static void AssertFrozenStateTransitions()
    {
        Require(
            DynamicFlowRuntimeStateContract.CanTransitionInstance(
                DynamicFlowInstanceStates.Pending,
                DynamicFlowInstanceStates.Materializing),
            "instance must enter materialization");
        Require(
            DynamicFlowRuntimeStateContract.CanTransitionInstance(
                DynamicFlowInstanceStates.Partial,
                DynamicFlowInstanceStates.Retrying),
            "partial instance must be retryable");
        Require(
            !DynamicFlowRuntimeStateContract.CanTransitionInstance(
                DynamicFlowInstanceStates.Pending,
                DynamicFlowInstanceStates.Completed),
            "instance must reject skipped transitions");
        Require(
            DynamicFlowRuntimeStateContract.CanTransitionInstance(
                DynamicFlowInstanceStates.Active,
                DynamicFlowInstanceStates.Terminated),
            "P6-07 parent must project a terminated child deterministically");
        Require(
            DynamicFlowRuntimeStateContract.CanTransitionStep(
                DynamicFlowStepStates.Submitted,
                DynamicFlowStepStates.Approved),
            "submitted step must project approval");
        Require(
            !DynamicFlowRuntimeStateContract.CanTransitionStep(
                DynamicFlowStepStates.Pending,
                DynamicFlowStepStates.Approved),
            "step must reject skipped transitions");

        AssertThrows(
            () => DynamicFlowRuntimeStateContract.RequireInstanceTransition(
                DynamicFlowInstanceStates.Pending,
                DynamicFlowInstanceStates.Completed),
            "invalid instance transition must fail closed");
        AssertThrows(
            () => DynamicFlowRuntimeStateContract.RequireStepTransition("UNKNOWN", DynamicFlowStepStates.Assigned),
            "unknown step state must fail closed");
    }

    private static void AssertRevisionCasContract()
    {
        var registry = BsonSerializer.SerializerRegistry;
        var instanceFilter = DynamicFlowRuntimeRevisionContract
            .InstanceCas(Oid(), 0, DynamicFlowInstanceStates.Pending)
            .Render(new RenderArgs<DynamicFlowInstance>(
                BsonSerializer.LookupSerializer<DynamicFlowInstance>(),
                registry))
            .ToJson();
        Require(instanceFilter.Contains("revision", StringComparison.Ordinal), "instance CAS must include revision");
        Require(instanceFilter.Contains("state", StringComparison.Ordinal), "instance CAS must include state");
        Require(instanceFilter.Contains("isDeleted", StringComparison.Ordinal), "instance CAS must reject soft-deleted rows");

        var stepFilter = DynamicFlowRuntimeRevisionContract
            .StepCas(Oid(), 7, DynamicFlowStepStates.Submitted)
            .Render(new RenderArgs<DynamicFlowStepInstance>(
                BsonSerializer.LookupSerializer<DynamicFlowStepInstance>(),
                registry))
            .ToJson();
        Require(stepFilter.Contains("7", StringComparison.Ordinal), "step CAS must bind expected revision");
        AssertThrows(
            () => DynamicFlowRuntimeRevisionContract.StepCas(Oid(), -1, DynamicFlowStepStates.Pending),
            "negative revision must fail closed");
        AssertThrows(
            () => DynamicFlowRuntimeRevisionContract.StepCas(Oid(), 0, "UNKNOWN"),
            "unknown expected state must fail closed");
    }

    private static void AssertReceiptEventAndOutboxGrain()
    {
        var receipt = new DynamicFlowRuntimeCommandReceipt
        {
            Id = Oid(),
            ScopeKind = DynamicFlowCommandScopeKinds.Launch,
            ScopeId = $"{Oid()}:{Oid()}",
            WorkId = Oid(),
            FlowTemplateVersionId = Oid(),
            CommandType = "LAUNCH",
            CommandId = "command-001",
            RequestHash = Hash('f'),
            Status = DynamicFlowRuntimeCommandStatuses.Pending,
            CreatedAtUtc = DateTime.UtcNow
        }.ToBsonDocument();
        foreach (var field in new[]
                 {
                     "scopeKind", "scopeId", "commandType", "commandId", "requestHash",
                     "expectedRevision", "status", "resultSnapshot", "resultSnapshotHash", "errorCode"
                 })
            Require(receipt.Contains(field), $"receipt BSON missing {field}");

        var flowEvent = typeof(DynamicFlowRuntimeEvent);
        Require(flowEvent.GetProperty("sequence") is null, "BSON names must not leak into CLR property lookup");
        foreach (var property in new[] { "FlowInstanceId", "StepInstanceId", "Sequence", "EventType", "CommandId", "PayloadHash" })
            Require(flowEvent.GetProperty(property) is not null, $"event contract missing {property}");

        var outbox = new DynamicFlowRuntimeOutboxItem
        {
            Id = Oid(),
            FlowInstanceId = Oid(),
            StepInstanceId = Oid(),
            Operation = "MATERIALIZE_ASSIGNMENT",
            DedupeKey = $"{Oid()}:entry:{Oid()}:1",
            PayloadHash = Hash('1'),
            NextAttemptAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        }.ToBsonDocument();
        foreach (var field in new[] { "dedupeKey", "status", "attemptCount", "nextAttemptAtUtc", "leaseId", "leaseUntilUtc" })
            Require(outbox.Contains(field), $"outbox BSON missing {field}");
    }

    private static void AssertContextOptionsAndIndexesMatchQueryShapes()
    {
        foreach (var property in new[]
                 {
                     "DynamicFlowInstanceCollection", "DynamicFlowStepInstanceCollection",
                     "DynamicFlowParticipantSnapshotCollection", "DynamicFlowRuntimeCommandReceiptCollection",
                     "DynamicFlowRuntimeEventCollection", "DynamicFlowRuntimeOutboxCollection"
                 })
            Require(typeof(MongoOptions).GetProperty(property) is not null, $"MongoOptions missing {property}");

        var context = ReadSource("Data/MongoDbContext.cs");
        foreach (var collection in new[]
                 {
                     "DynamicFlowInstances", "DynamicFlowStepInstances", "DynamicFlowParticipantSnapshots",
                     "DynamicFlowRuntimeCommandReceipts", "DynamicFlowRuntimeEvents", "DynamicFlowRuntimeOutbox"
                 })
            Require(context.Contains(collection, StringComparison.Ordinal), $"MongoDbContext missing {collection}");

        var indexes = ReadSource("Data/Indexes/MongoIndexInitializer.cs");
        foreach (var name in new[]
                 {
                     "ux_dynamicFlowInstances_launch_identity",
                     "ix_dynamicFlowInstances_issuer_updated",
                     "ux_dynamicFlowStepInstances_business_key",
                     "ux_dynamicFlowStepInstances_topology_identity",
                     "ix_dynamicFlowStepInstances_epoch_branch_order",
                     "ix_dynamicFlowStepInstances_inbox",
                     "ux_dynamicFlowStepInstances_assignment",
                     "ix_dynamicFlowStepInstances_instance_state",
                     "ux_dynamicFlowParticipantSnapshots_instance",
                     "ux_dynamicFlowRuntimeCommandReceipts_scope_command",
                     "ux_dynamicFlowRuntimeEvents_instance_sequence",
                     "ux_dynamicFlowRuntimeOutbox_dedupe",
                     "ix_dynamicFlowRuntimeOutbox_due_lease"
                 })
            Require(indexes.Contains(name, StringComparison.Ordinal), $"runtime index missing {name}");
        Require(Count(indexes, "ux_dynamicFlowStepInstances_business_key") == 1, "business unique index must be declared once");
        Require(
            indexes.Contains("{ \"flowInstanceId\", 1 },\r\n                    { \"executionEpoch\", 1 },\r\n                    { \"flowStepId\", 1 },\r\n                    { \"targetUnitId\", 1 },\r\n                    { \"branchId\", 1 },\r\n                    { \"attemptNo\", 1 }", StringComparison.Ordinal) ||
            indexes.Contains("{ \"flowInstanceId\", 1 },\n                    { \"executionEpoch\", 1 },\n                    { \"flowStepId\", 1 },\n                    { \"targetUnitId\", 1 },\n                    { \"branchId\", 1 },\n                    { \"attemptNo\", 1 }", StringComparison.Ordinal),
            "step business index field order drift");
    }

    private static void AssertPersistenceIsAppendOnlyForEventsAndDoesNotWriteLegacyArtifacts()
    {
        var contract = typeof(IDynamicFlowRuntimePersistence);
        Require(contract.GetMethod("AppendEventAsync") is not null, "event append operation missing");
        Require(
            contract.GetMethods().All(method => !method.Name.Contains("UpdateEvent", StringComparison.Ordinal)),
            "runtime event contract must be append-only");
        var source = ReadSource("Services/DynamicFlows/DynamicFlowRuntimePersistence.cs");
        Require(!source.Contains("WorkAssignments.", StringComparison.Ordinal), "P5-01 persistence must not write assignments");
        Require(!source.Contains("WorkAssignmentReports.", StringComparison.Ordinal), "P5-01 persistence must not write reports");
        Require(!source.Contains("DynamicFlowEvents.", StringComparison.Ordinal), "canonical events must not reuse legacy event collection");

        var runtime = ReadSource("Services/DynamicFlows/DynamicFlowRuntimeService.cs");
        var barrier = runtime.IndexOf("throw ExecutionBlocked(version);", StringComparison.Ordinal);
        var firstAssignmentWriter = runtime.IndexOf("_assignments.CreateAsync", StringComparison.Ordinal);
        Require(barrier >= 0 && firstAssignmentWriter > barrier, "P4 launch barrier moved behind a writer");
    }

    private static string Collection<T>()
        => typeof(T).GetCustomAttribute<BsonCollectionAttribute>()?.Name
           ?? throw new InvalidOperationException($"{typeof(T).Name} has no collection contract");

    private static string Oid() => MongoDB.Bson.ObjectId.GenerateNewId().ToString();
    private static string Hash(char value) => new(value, 64);

    private static void AssertThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static int Count(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }
        return count;
    }

    private static string ReadSource(string relativePath)
    {
        var relative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, relative);
            if (File.Exists(direct))
                return File.ReadAllText(direct);
            var nested = Path.Combine(directory.FullName, "tdtd-be", relative);
            if (File.Exists(nested))
                return File.ReadAllText(nested);
        }
        throw new FileNotFoundException($"Unable to locate backend source '{relativePath}'.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
