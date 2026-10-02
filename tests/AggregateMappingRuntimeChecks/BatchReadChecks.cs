using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class BatchReadChecks
{
    internal static async Task Run(MongoDbContext db, string run, CancellationToken ct)
    {
        var f = await RuntimeFixture.Seed(db, run + "-batch", ct);
        var payloads = new WorkReportPayloadService(db);
        var dates = new AggregateMongoDataWindows(db);
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); Console.WriteLine("PASS batch " + label); }
        AggregateMongoPreviewReader Reader() => new(db, payloads, dates, true);
        async Task<(AggregateMongoPreviewReader Reader, AggregateReadContext Context, AggregateSourceHeader Header, AggregateSchema Schema)> Prepare()
        {
            var reader = Reader(); var context = await reader.BootstrapAsync(f.Report.Id, f.Actor, ct);
            var pin = new AggregateFormPinDto(f.SourceForm.Id, f.SourceForm.FamilyId!, f.SourceForm.VersionNo, f.SourceForm.PublishedSchemaHash!);
            var schema = await reader.ReadSchemaAsync(pin, context, f.Actor, ct);
            var listing = await reader.ListSourcesAsync(context, pin, f.Actor, ct);
            await reader.PreparePayloadBatchAsync(context, listing.Headers, f.Actor, ct);
            return (reader, context, listing.Headers.Single(), schema);
        }
        var p = await Prepare();
        var value = await p.Reader.ReadPayloadAsync(p.Context, p.Header, p.Schema, f.Actor, ct);
        Check(value.Values["n"].Number!.ToWire() == "30" && await p.Reader.IsCurrentAsync(p.Context, [p.Header.Pin], "", f.Actor, ct),
            "bounded batch returns verified native value and completes current-input revalidation");
        p = await Prepare();
        try
        {
            await p.Reader.ReadPayloadAsync(p.Context, p.Header, p.Schema, f.Outsider, ct);
            throw new InvalidOperationException("batch crossed actor boundary");
        }
        catch (AggregatePreviewException e) when (e.Code == "AGG_SOURCE_UNAVAILABLE") { Check(true, "captured batch cannot grant another actor access"); }
        await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Source.Id,
            Builders<WorkAssignmentReport>.Update.Set(r => r.Status, WorkAssignmentReportStatus.Draft), cancellationToken: ct);
        try
        {
            var stale = false;
            try { stale = !await p.Reader.IsCurrentAsync(p.Context, [p.Header.Pin], "", f.Actor, ct); }
            catch (AggregatePreviewException e) when (e.Code == "AGG_INPUT_STALE") { stale = true; }
            Check(stale, "source lifecycle drift after batch capture prevents final result publication");
        }
        finally { await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Source.Id,
            Builders<WorkAssignmentReport>.Update.Set(r => r.Status, WorkAssignmentReportStatus.Approved), cancellationToken: ct); }
        p = await Prepare();
        try
        {
            await db.DynamicFormTemplates.UpdateOneAsync(t => t.Id == f.SourceForm.Id,
                Builders<DynamicFormTemplate>.Update.Set(t => t.FieldsJson, "[]"), cancellationToken: ct);
            Check(!await p.Reader.IsCurrentAsync(p.Context, [p.Header.Pin], "", f.Actor, ct),
                "same-hash Form structure corruption cannot hide behind per-request template reuse");
        }
        finally {
            var restored = await db.DynamicFormTemplates.UpdateOneAsync(t => t.Id == f.SourceForm.Id && t.FieldsJson == "[]",
                Builders<DynamicFormTemplate>.Update.Set(t => t.FieldsJson, f.SourceForm.FieldsJson), cancellationToken: CancellationToken.None);
            if (restored.MatchedCount != 1 && !await db.DynamicFormTemplates.Find(t => t.Id == f.SourceForm.Id && t.FieldsJson == f.SourceForm.FieldsJson).AnyAsync(CancellationToken.None))
                throw new InvalidOperationException("Fixture restore conflict; preserve newer Form changes");
            await FixtureProvenanceChecks.Migration(db, "after corrupt fields restored", CancellationToken.None);
        }
        var missing = await dates.ReadManyAsync("REPORT", [f.Report.Id, f.Source.Id, f.Source.Id], ct);
        Check(missing.Count == 2 && missing.Values.All(v => v == null), "batch declaration read preserves unknown windows and deduplicates identities");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await p.Reader.PreparePayloadBatchAsync(p.Context, [p.Header], f.Actor, cancelled.Token); throw new InvalidOperationException("cancelled batch ran"); }
        catch (OperationCanceledException) { Check(true, "cancelled batch stops before exposing payload results"); }
    }
}
