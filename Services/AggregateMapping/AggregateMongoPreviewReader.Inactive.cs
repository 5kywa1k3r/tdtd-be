using MongoDB.Driver;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;

namespace tdtd_be.Services.AggregateMapping;

internal sealed partial class AggregateMongoPreviewReader
{
    private readonly Dictionary<string, (AggregateFormPinDto Form, string[] Ids, string Digest)> _inactiveCaptures = new();
    internal readonly Dictionary<string, AggregateSourceHeader> InactiveHeaders = new();
    internal IEnumerable<AggregateSourceHeader> CapturedInactiveSources => InactiveHeaders.Values;

    public async Task<IReadOnlyList<AggregateSourceHeader>> ReadInactiveSourcesAsync(AggregateReadContext context,
        AggregateFormPinDto form, IReadOnlyList<string> ids, string actor, CancellationToken ct)
    {
        if (ids.Count > 10_000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        var selected = ids.Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray();
        foreach (var id in selected) Id(id);
        var children = (await Children(context, actor, ct)).ToDictionary(c => c.Id);
        var childIds = children.Keys.ToArray();
        var reports = await db.WorkAssignmentReports.Find(r => selected.Contains(r.Id) && r.WorkId == context.Context.WorkId
            && childIds.Contains(r.WorkAssignmentId) && r.DynamicFormTemplateId == form.FormId
            && !r.IsActive && !r.IsCurrent && !r.IsDeleted).Project<WorkAssignmentReport>(HeaderOnly).ToListAsync(ct);
        var result = new List<AggregateSourceHeader>();
        foreach (var report in reports.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            var child = children[report.WorkAssignmentId];
            if (!await CanRead(child, report, actor, ct) || Pin(report) != form)
                throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
            WorkReportPeriod? period = report.WorkReportPeriodId == null ? null : await db.WorkReportPeriods.Find(p =>
                p.Id == report.WorkReportPeriodId && p.WorkId == report.WorkId && p.WorkAssignmentId == child.Id && !p.IsDeleted).FirstOrDefaultAsync(ct);
            if (report.WorkReportPeriodId != null && period == null) throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
            var bindings = await db.WorkTemplateAssignees.Find(b => b.WorkId == report.WorkId && b.WorkAssignmentId == child.Id
                && b.AssigneeUserId == report.AssigneeUserId && b.DynamicFormTemplateId == form.FormId && !b.IsDeleted).ToListAsync(ct);
            var matching = bindings.Where(b => period == null || b.Id == period.WorkTemplateAssigneeId).ToArray();
            if (matching.Length != 1) throw new AggregatePreviewException("AGG_SOURCE_BINDING_AMBIGUOUS");
            result.Add(await Header(report, child, matching[0], actor, ct, period, inactiveMetadata: true));
        }
        var key = AggregateDigest.Of(new { form, selected });
        var digest = AggregateDigest.Of(result);
        if (_inactiveCaptures.TryGetValue(key, out var old) && old.Digest != digest) throw new AggregatePreviewException("AGG_INPUT_STALE");
        _inactiveCaptures[key] = (form, selected, digest);
        foreach (var header in result) InactiveHeaders[header.Pin.ReportId] = header;
        return result;
    }
}
