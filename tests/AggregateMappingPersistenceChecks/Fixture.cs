using System.Text.Json;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;

internal sealed record TestReport(int Payload = 1, int Lifecycle = 1, string Status = "Draft", string? Value = null);
internal sealed record TestRow(long Version, string Body, string Work, string? Target, string[] Keys);
internal sealed class TestStore : IAggregateTransactionStore
{
    internal Dictionary<(string Collection, string Id), TestRow> Rows = [];
    internal Dictionary<string, TestReport> Reports = [];
    internal int FailAtWrite, WriteCount;
    internal T? Value<T>(string collection, string id) => Rows.TryGetValue((collection, id), out var row) ? JsonSerializer.Deserialize<T>(row.Body, AggregateCanonical.Json) : default;
    public async Task<T> ExecuteAsync<T>(Func<IAggregateTransaction, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var tx = new Transaction(this); var result = await action(tx, ct);
        Rows = tx.Rows; Reports = tx.Reports; return result;
    }
    internal sealed class Transaction(TestStore owner) : IAggregateTransaction
    {
        internal Dictionary<(string Collection, string Id), TestRow> Rows = new(owner.Rows);
        internal Dictionary<string, TestReport> Reports = new(owner.Reports);
        public Task<AggregateStored<T>?> GetAsync<T>(string collection, string id, CancellationToken ct)
            => Task.FromResult(Rows.TryGetValue((collection, id), out var row)
                ? new AggregateStored<T>(row.Version, JsonSerializer.Deserialize<T>(row.Body, AggregateCanonical.Json)!) : null);
        public Task<IReadOnlyList<AggregateStored<T>>> QueryAsync<T>(string collection, AggregateQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AggregateStored<T>>>(Rows.Where(p => p.Key.Collection == collection && p.Value.Work == query.WorkId
                && (query.Target == null || p.Value.Target == query.Target) && (query.DependencyKey == null || p.Value.Keys.Contains(query.DependencyKey)))
                .OrderBy(p => p.Key.Id, StringComparer.Ordinal).Select(p => new AggregateStored<T>(p.Value.Version, JsonSerializer.Deserialize<T>(p.Value.Body, AggregateCanonical.Json)!)).ToArray());
        public Task PutAsync<T>(string collection, string id, long expectedVersion, T value, string workId, string? target, IReadOnlyList<string> keys, CancellationToken ct)
        {
            Touch();
            if ((Rows.TryGetValue((collection, id), out var old) ? old.Version : 0) != expectedVersion) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
            Rows[(collection, id)] = new(expectedVersion + 1, JsonSerializer.Serialize(value, AggregateCanonical.Json), workId, target, keys.ToArray());
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string collection, string id, long expectedVersion, CancellationToken ct)
        {
            Touch(); if (!Rows.TryGetValue((collection, id), out var old) || old.Version != expectedVersion) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
            Rows.Remove((collection, id)); return Task.CompletedTask;
        }
        public Task FenceAsync(AggregateCommitAuthority authority, IReadOnlyList<AggregateSourcePinDto> sources, IReadOnlyList<AggregateCoverageSlotDto> slots, CancellationToken ct)
        {
            if (authority.Read.Context.ReportId == null) return Task.CompletedTask; // No fake report for a declared occurrence.
            var report = Reports[authority.Read.Context.ReportId!];
            if (report.Payload != authority.Read.Revisions.PayloadRevision || report.Lifecycle != authority.Read.Revisions.LifecycleRevision) throw new AggregatePreviewException("AGG_INPUT_STALE");
            return Task.CompletedTask;
        }
        public Task<long> WriteTargetAsync(AggregateCommitAuthority authority, AggregateTargetWrite write, CancellationToken ct)
        {
            Touch(); var id = authority.Read.Context.ReportId!; var report = Reports[id];
            if (report.Status != "Draft" || report.Payload != authority.Read.Revisions.PayloadRevision) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
            Reports[id] = report with { Payload = report.Payload + 1, Value = write.Preview.Preview.Results.Single().Value?.GetString() };
            return Task.FromResult((long)Reports[id].Payload);
        }
        private void Touch()
        { owner.WriteCount++; if (owner.FailAtWrite > 0 && owner.WriteCount == owner.FailAtWrite) throw new InvalidOperationException("INJECTED_WRITE_FAILURE"); }
    }
}

internal sealed class TestReader(TestStore store) : IAggregateCommandReader
{
    internal bool Authorized = true, Fresh = true;
    internal int SourceRevision = 1;
    internal long SourceValue = 30;
    internal bool Missing;
    internal static AggregateFormPinDto SourceForm = new("formC", "familyC", 1, "hashC"), TargetForm = new("formB", "familyB", 1, "hashB");
    internal static AggregatePeriodContextDto Context(string id = "reportB") => new("PERIODIC", "work", "B", "binding" + id, id, "period" + id, "pi" + id, "20260930", "2026-09-01", "2026-09-30", null, "schedule");
    internal static AggregateExpressionDto Input() => new() { Kind = "INPUT", Ref = "in" };
    internal static AggregateExpressionDto Sum() => new() { Kind = "CALL", Name = "SUM", Arguments = [Input()] };
    internal static AggregateRecipeDto Recipe(AggregateExpressionDto? expression = null) => new()
    {
        SchemaVersion = 1, SemanticProfile = "REPORT_MAPPING_V1",
        Nodes = [new() { Id = "s", Kind = "SOURCE", Form = SourceForm, Origin = "DIRECT_CHILD_REPORTS", SourceCardinality = "SET", Inputs = [], Outputs = [new("out", "NUMBER", "SET", "n", "w")] },
            new() { Id = "c", Kind = "CALCULATION", Inputs = [new("in", "NUMBER", "SET")], Outputs = [new("out", "NUMBER", "SINGLE")], Expressions = [new("out", expression ?? Sum())] },
            new() { Id = "t", Kind = "TARGET", Form = TargetForm, Inputs = [new("in", "NUMBER", "SINGLE", "total")], Outputs = [] }],
        Edges = [new("e1", new("s", "out"), new("c", "in")), new("e2", new("c", "out"), new("t", "in"))],
        TimeRules = [new() { Id = "w", Mode = "TARGET_DATA_WINDOW", SourceDateBasis = "DECLARED_DATA_WINDOW", Match = "CONTAINED" }]
    };
    public Task<AggregateCommitAuthority> AuthorizeAsync(AggregatePeriodContextDto context, string actor, string sessionKey, CancellationToken ct)
    {
        if (!Authorized || actor != "B") throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        var report = context.ReportId == null ? new TestReport(Payload: 0, Lifecycle: 0) : store.Reports[context.ReportId];
        var authority = new AggregateAuthorityFacts(true, true, true, true, true, false, true, true, true, true, true, true,
            report.Status, false, true, true, true, true);
        var values = new Dictionary<string, AggregateValue>(); if (report.Value != null) values["total"] = AggregateValue.Numeric(AggregateNumber.Parse(report.Value));
        var read = new AggregateReadContext(context, new(TargetForm, new Dictionary<string, AggregateMember> { ["total"] = new("total", "NUMBER") }), authority,
            new(report.Payload, report.Lifecycle, 0, 0, "hashB", ""), "authorityB", new("2026-09-01", "2026-09-30", "USER_DECLARED", "owned", 1), values);
        return Task.FromResult(new AggregateCommitAuthority(actor, sessionKey, read, []));
    }
    public Task<AggregatePreviewEnvelope> PreviewAsync(AggregateInstanceState instance, AggregateRecipeDto recipe, AggregateCommitAuthority authority, CancellationToken ct)
    {
        var expected = authority.Read.Revisions with { InstanceRevision = instance.Revision, ConfigRevision = instance.ConfigRevision };
        var read = authority.Read with { Revisions = expected };
        return new AggregatePreviewService(new SourceReader(this, read)).PreviewAsync(new(read.Context, instance.Id, instance.ConfigId, expected, recipe, instance.Selection), authority.Actor, ct);
    }
    private sealed class SourceReader(TestReader owner, AggregateReadContext read) : IAggregatePreviewReader
    {
        private AggregateSourcePinDto Pin => new("work", "C", "bindingC", "periodC", "piC", "r1", 1, owner.SourceRevision, 1, "payload" + owner.SourceRevision, "hashC", "Approved", true, "relation", "authority");
        public Task<AggregateReadContext> ReadContextAsync(AggregatePeriodContextDto selector, string actor, CancellationToken ct) => Task.FromResult(read);
        public Task<AggregateSchema> ReadSchemaAsync(AggregateFormPinDto pin, AggregateReadContext context, string actor, CancellationToken ct)
            => Task.FromResult(new AggregateSchema(SourceForm, new Dictionary<string, AggregateMember> { ["n"] = new("n", "NUMBER") }));
        public Task<AggregateSourceListing> ListSourcesAsync(AggregateReadContext context, AggregateFormPinDto form, string actor, CancellationToken ct)
        {
            var date = new AggregateDataWindowDeclarationDto("2026-09-01", "2026-09-30", "USER_DECLARED", "C", 1);
            return Task.FromResult(new AggregateSourceListing([new(Pin, "B", SourceForm, "unitC", "20260930", true, true, false, date)],
                [new("bindingC:20260930", "bindingC", "scheduleC", "20260930", "periodC", "r1", SourceForm, true, new(2026, 1, 1), null, new(2026, 9, 30), date),
                 ..owner.Missing ? new AggregateSlot[] { new("bindingD:20260930", "bindingD", "scheduleD", "20260930", null, null, SourceForm, true, new(2026, 1, 1), null, new(2026, 9, 30), date) } : []],
                true, "membership", ["unitC"]));
        }
        public Task<AggregatePayload> ReadPayloadAsync(AggregateReadContext context, AggregateSourceHeader header, AggregateSchema schema, string actor, CancellationToken ct)
            => Task.FromResult(new AggregatePayload(Pin, new Dictionary<string, AggregateValue> { ["n"] = AggregateValue.Numeric(AggregateNumber.From(owner.SourceValue)) }));
        public Task<bool> IsCurrentAsync(AggregateReadContext context, IReadOnlyList<AggregateSourcePinDto> pins, string membershipRevision, string actor, CancellationToken ct) => Task.FromResult(owner.Fresh);
    }
}
