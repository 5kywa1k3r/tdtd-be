using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

internal static class AggregateCoverageResolver
{
    internal static IReadOnlyList<AggregateCoverageSlotDto> Resolve(AggregateSourceListing listing,
        AggregateFormPinDto form, IReadOnlyList<AggregateResolvedWindowDto> windows, AggregateBudget budget, AggregateReportFilterDto? metadata = null)
    {
        var result = new List<AggregateCoverageSlotDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in listing.Slots)
        {
            budget.Spend();
            if (slot.Form != form || slot.OccurrenceDate < slot.EffectiveStart
                || (slot.EffectiveEnd != null && slot.OccurrenceDate > slot.EffectiveEnd)) continue;
            if (!seen.Add(slot.Key)) throw new AggregatePreviewException("AGG_SLOT_DUPLICATE");
            // Never disclose the identity/count of inaccessible slots.
            if (!slot.Readable) throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
            if (slot.DataWindow is not { Revision: > 0 } data)
            {
                result.Add(new(slot.Key, slot.BindingId, slot.ScheduleRevision, slot.OccurrenceKey,
                    slot.PeriodId, slot.CurrentReportId, "UNKNOWN", "AGG_SLOT_DATE_UNAVAILABLE"));
                continue;
            }
            if (!windows.Any(w => AggregateTimeResolver.Matches(w, AggregateTimeResolver.Date(data.StartDate), AggregateTimeResolver.Date(data.EndDate)))) continue;
            var headers = listing.Headers.Where(h => h.Pin.BindingId == slot.BindingId && h.OccurrenceKey == slot.OccurrenceKey && h.Pin.IsCurrent && !h.Deleted).ToArray();
            if (headers.Length > 1) throw new AggregatePreviewException("AGG_CURRENT_REPORT_AMBIGUOUS");
            var header = headers.SingleOrDefault();
            if (slot.CurrentReportId != null && header?.Pin.ReportId != slot.CurrentReportId)
                throw new AggregatePreviewException("AGG_INPUT_STALE");
            if (header != null && !header.WholeReportReadable) throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
            if (header != null && !AggregateReportMetadataFilter.Matches(metadata, header)) continue;
            if (header == null && metadata != null)
            {
                result.Add(new(slot.Key, slot.BindingId, slot.ScheduleRevision, slot.OccurrenceKey,
                    slot.PeriodId, null, "UNKNOWN", "AGG_METADATA_DATE_UNAVAILABLE"));
                continue;
            }
            var state = header == null ? "MISSING" : !header.Active ? "UNKNOWN" : header.Pin.Status switch
            { "Approved" => "ELIGIBLE", "Submitted" => "SUBMITTED", "Draft" => "DRAFT", _ => "UNKNOWN" };
            result.Add(new(slot.Key, slot.BindingId, slot.ScheduleRevision, slot.OccurrenceKey,
                slot.PeriodId, header?.Pin.ReportId, state, state == "ELIGIBLE" ? "OK" : "AGG_SOURCE_" + state));
        }
        return result;
    }
}
