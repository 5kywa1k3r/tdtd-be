using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

internal sealed record RuntimeFixture(string Actor, string Outsider, string WorkId, WorkTemplateAssignee Binding,
    WorkAssignmentReport Report, WorkAssignmentReport Source, DynamicFormTemplate TargetForm, DynamicFormTemplate SourceForm)
{
    static string Id() => ObjectId.GenerateNewId().ToString();
    internal static async Task<RuntimeFixture> Seed(MongoDbContext db, string run, CancellationToken ct, bool requiredTarget = false,
        Action<DynamicFormTemplate, bool>? configureForm = null,
        Func<DynamicFormTemplate, CancellationToken, Task<DynamicFormTemplate>>? publishForm = null)
    {
        var actor = Id(); var outsider = Id(); var childActor = Id(); var unit = Id(); var work = Id(); var parent = Id(); var child = Id();
        foreach (var user in new[] { actor, outsider, childActor })
            await db.Users.InsertOneAsync(new AppUser { Id = user, Username = run + "-" + user, UnitId = unit, CreatedByUserId = actor }, cancellationToken: ct);
        await db.Works.InsertOneAsync(new Work { Id = work, AutoCode = run, Name = run, CreatedByUserId = actor, LeaderDirectiveUserId = actor }, cancellationToken: ct);
        foreach (var pair in new[] { (parent, actor, (string?)null), (child, childActor, (string?)parent) })
            await db.WorkAssignments.InsertOneAsync(new WorkAssignment { Id = pair.Item1, WorkId = work, RootAssignmentId = parent,
                ParentAssignmentId = pair.Item3, CreatedByUserId = actor, IssuedByUnitId = unit, TargetUnitIds = [unit],
                Assignees = [new UserRef { UserId = pair.Item2, UnitId = unit }],
                Code = run + pair.Item1, Name = run, Path = "/" + parent + "/" + pair.Item1, AssignmentType = "ONCE", IsActive = true }, cancellationToken: ct);
        DynamicFormTemplate Form(string member)
        {
            var form = new DynamicFormTemplate { Id = Id(), FamilyId = Id(), Name = run, Code = run + member, IsPublished = publishForm == null,
                CreatedByUserId = actor, CreatedByUsername = run,
                SectionsJson = "[{\"id\":\"main\",\"title\":\"Phần\",\"order\":0,\"tagCodes\":[]}]",
                FieldsJson = JsonSerializer.Serialize(new[] { new { id = member, key = member, name = member, label = member, sectionId = "main", type = "number", required = requiredTarget && member == "total" } }) };
            // Final structure must exist before any published snapshot or runtime pin.
            configureForm?.Invoke(form, member == "total");
            _ = DynamicFormSectionSnapshotBuilder.Build(form);
            var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(form);
            form.PublishedSchemaSnapshotJson = snapshot.Json; form.PublishedSchemaHash = snapshot.Sha256;
            return form;
        }
        var target = Form("total"); var source = Form("n");
        await db.DynamicFormTemplates.InsertManyAsync(new[] { target, source }, cancellationToken: ct);
        if(publishForm!=null){target=await publishForm(target,ct);source=await publishForm(source,ct);}
        foreach (var pin in new[] { (Assignment: parent, Form: target), (Assignment: child, Form: source) })
            await db.WorkAssignments.UpdateOneAsync(a => a.Id == pin.Assignment, Builders<WorkAssignment>.Update
                .Set(a => a.DynamicFormTemplateId, pin.Form.Id).Set(a => a.DynamicFormFamilyId, pin.Form.FamilyId)
                .Set(a => a.DynamicFormVersionNo, pin.Form.VersionNo).Set(a => a.DynamicFormSchemaHash, pin.Form.PublishedSchemaHash), cancellationToken: ct);
        WorkTemplateAssignee Binding(string assignment, string assignee, DynamicFormTemplate form) => new() {
            Id = Id(), WorkId = work, WorkAssignmentId = assignment, AssigneeUserId = assignee, AssigneeUnitId = unit,
            AssignmentType = "ONCE", IsActive = true, DynamicFormTemplateId = form.Id, DynamicFormSchemaHash = form.PublishedSchemaHash,
            DynamicFormFamilyId = form.FamilyId, DynamicFormVersionNo = form.VersionNo,
            StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), DueDate = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc),
            CreatedByUserId = actor };
        var binding = Binding(parent, actor, target); var childBinding = Binding(child, childActor, source);
        await db.WorkTemplateAssignees.InsertManyAsync(new[] { binding, childBinding }, cancellationToken: ct);
        async Task<WorkAssignmentReport> Report(WorkTemplateAssignee b, DynamicFormTemplate form, string fields, WorkAssignmentReportStatus status)
        {
            var report = new WorkAssignmentReport { Id = Id(), WorkId = work, WorkAssignmentId = b.WorkAssignmentId,
                WorkReportPeriodId = Id(), AssigneeUserId = b.AssigneeUserId, PeriodKey = "ONCE", PeriodInstanceKey = b.Id + ":ONCE",
                DynamicFormTemplateId = form.Id, DynamicFormFamilyId = form.FamilyId, DynamicFormVersionNo = form.VersionNo,
                DynamicFormSchemaHash = form.PublishedSchemaHash, IsCurrent = true, IsActive = true, Status = status,
                VersionNo = 1, LifecycleRevision = 1, CreatedByUserId = b.AssigneeUserId, ReportTitle = run,
                Values1DJson = "[]", ScheduleSnapshotJson = "{}", SpecJson = string.Empty };
            string? tables = null;
            if (form.NativeTablesVersion is 1 or 2)
            {
                var definitions = DynamicFormNativeTableDefinition.ReadStored(form.NativeTablesVersion, form.TablesJson)!;
                tables = JsonSerializer.Serialize(new { nativeTables = new { version = 1, schemaHash = form.PublishedSchemaHash,
                    tables = definitions.Select(t => new Dictionary<string, object?> { ["tableId"] = t.Id,
                        [t.Layout == "matrix" ? "rows" : "records"] = t.Layout == "matrix"
                            ? (t.Rows ?? throw new InvalidOperationException("Matrix fixture requires fixed rows")).Select(r => new { rowId = r.Id, cells = new Dictionary<string, object>() }).ToArray() : (object)Array.Empty<object>() }) } });
                DynamicFormNativeTableValues.Validate(form, form.PublishedSchemaHash, tables, false);
            }
            var saved = await new WorkReportPayloadService(db).SaveReportPayloadAsync(report, "[]", fields, tables, null, actor, DateTime.UtcNow, ct);
            report.PayloadRevision = saved.PayloadRevision; report.PayloadHash = saved.PayloadHash;
            report.PayloadSizeBytes = saved.PayloadSizeBytes; report.PayloadStatus = saved.PayloadStatus;
            await db.WorkAssignmentReports.InsertOneAsync(report, cancellationToken: ct);
            await db.WorkReportPeriods.InsertOneAsync(new WorkReportPeriod { Id = report.WorkReportPeriodId, WorkId = work,
                WorkAssignmentId = b.WorkAssignmentId, WorkTemplateAssigneeId = b.Id, AssigneeUserId = b.AssigneeUserId,
                CurrentReportId = report.Id, AssigneeUnitId = b.AssigneeUnitId, PeriodKey = report.PeriodKey, PeriodInstanceKey = report.PeriodInstanceKey,
                DynamicFormTemplateId = form.Id, DynamicFormFamilyId = form.FamilyId, DynamicFormVersionNo = form.VersionNo,
                DynamicFormSchemaHash = form.PublishedSchemaHash, CreatedByUserId = actor }, cancellationToken: ct);
            return report;
        }
        var report = await Report(binding, target, "{}", WorkAssignmentReportStatus.Draft);
        var sourceReport = await Report(childBinding, source, configureForm == null ? "{\"n\":30}" : "{}", WorkAssignmentReportStatus.Approved);
        Console.WriteLine($"FIXTURE work={work} actor={actor} outsider={outsider} report={report.Id} source={sourceReport.Id}");
        return new(actor, outsider, work, binding, report, sourceReport, target, source);
    }
}
