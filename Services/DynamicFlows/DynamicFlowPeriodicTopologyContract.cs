using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using tdtd_be.DTOs.DynamicFlows;

namespace tdtd_be.Services.DynamicFlows;

public sealed record DynamicFlowPeriodicTopology(
    DynamicFlowTemplatePayloadDto Payload,
    DynamicFlowTopologyNodeDto EntryNode,
    DynamicFlowTopologyNodeDto ScheduleGateway,
    DynamicFlowFormNodeDto EntryForm,
    string ScheduleKey,
    string CanonicalJson,
    string TopologyHash);

public sealed record DynamicFlowPeriodicLaunchContext(
    string ScheduleId,
    string OccurrenceId,
    string PeriodKey,
    string TimeZoneId,
    string PolicyVersion,
    string ScheduleIdentityJson,
    string ScheduleIdentityHash,
    string LeaseId,
    long ExpectedOccurrenceRevision);

public static class DynamicFlowPeriodicTopologyContract
{
    public const string ArchetypeId = "FLOW-T10";
    public const string PolicyVersion = "P6-PERIODIC-1";
    public const string OccurrenceLaunchedEvent = "PERIODIC_OCCURRENCE_LAUNCHED";
    public const string OccurrenceMissedEvent = "PERIODIC_OCCURRENCE_MISSED";
    public const string MissedNoCatchUp = "MISSED_NO_CATCH_UP";
    public const string MissedOverlap = "MISSED_ACTIVE_OVERLAP";
    public const string TimeZoneRequired = "DYNAMIC_FLOW_PERIODIC_TIMEZONE_REQUIRED";
    public const string TimeZoneInvalid = "DYNAMIC_FLOW_PERIODIC_TIMEZONE_INVALID";
    public const string LocalTimeInvalid = "DYNAMIC_FLOW_PERIODIC_LOCAL_TIME_INVALID";
    public const string DstInvalidLocalTime = "DYNAMIC_FLOW_PERIODIC_DST_INVALID_LOCAL_TIME";

    public static DynamicFlowPeriodicTopology Require(
        string lockedDefinitionJson,
        string expectedHash)
    {
        var canonical =
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                lockedDefinitionJson,
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true));
        if (!FixedEquals(canonical.PayloadHash, expectedHash))
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_TOPOLOGY_SNAPSHOT_HASH_MISMATCH");

        var payload = canonical.Payload;
        if (payload.ArchetypeId != ArchetypeId ||
            payload.Nodes.Count != 2 ||
            payload.Edges.Count != 1 ||
            payload.FormNodes.Count != 1)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_PERIODIC_TOPOLOGY_INVALID");
        }

        var entry = payload.Nodes.Single(candidate =>
            candidate.NodeKind == DynamicFlowNodeKinds.FormStep);
        var gateway = payload.Nodes.Single(candidate =>
            candidate.NodeKind == DynamicFlowNodeKinds.Gateway);
        var edge = payload.Edges.Single();
        var definition = gateway.Gateway;
        if (payload.EntryStepId != entry.NodeId ||
            entry.Gateway is not null ||
            string.IsNullOrWhiteSpace(entry.FormNodeId) ||
            definition?.Kind != DynamicFlowGatewayKinds.Schedule ||
            string.IsNullOrWhiteSpace(definition.ScheduleKey) ||
            definition.ScheduleKey.Length > 128 ||
            edge.FromNodeId != entry.NodeId ||
            edge.ToNodeId != gateway.NodeId ||
            edge.Condition is not null ||
            payload.Edges.Any(candidate =>
                candidate.FromNodeId == gateway.NodeId) ||
            definition.ExpectedIncomingNodeIds.Count != 0 ||
            definition.RequiredIncomingCount is not null ||
            definition.ReviewRole is not null ||
            definition.SubflowFamilyId is not null ||
            definition.SubflowVersionId is not null ||
            definition.RollbackTargetNodeId is not null)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_PERIODIC_GATEWAY_INVALID");
        }

        var form = payload.FormNodes.Single();
        if (form.FormNodeId != entry.FormNodeId ||
            !ObjectId.TryParse(form.DynamicFormTemplateId, out _) ||
            !ObjectId.TryParse(form.DynamicFormFamilyId, out _) ||
            form.DynamicFormVersionNo is null or < 1 ||
            !IsSha256(form.DynamicFormSchemaHash) ||
            !IsSha256(form.DynamicFormSnapshotHash))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_PERIODIC_FORM_PIN_INVALID");
        }

        return new DynamicFlowPeriodicTopology(
            payload,
            entry,
            gateway,
            form,
            definition.ScheduleKey.Trim(),
            canonical.CanonicalJson,
            canonical.PayloadHash);
    }

    public static string NormalizeTimeZoneId(string? requestedId)
    {
        var value = requestedId?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(TimeZoneRequired);

        try
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(
                    value,
                    out var ianaId) &&
                !string.IsNullOrWhiteSpace(ianaId))
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(ianaId);
                return ianaId;
            }

            var zone = TimeZoneInfo.FindSystemTimeZoneById(value);
            return zone.Id;
        }
        catch (TimeZoneNotFoundException error)
        {
            throw new InvalidOperationException(TimeZoneInvalid, error);
        }
        catch (InvalidTimeZoneException error)
        {
            throw new InvalidOperationException(TimeZoneInvalid, error);
        }
    }

    public static TimeOnly ParseLocalTime(string? value)
    {
        if (!TimeOnly.TryParseExact(
                value?.Trim(),
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTime))
        {
            throw new InvalidOperationException(LocalTimeInvalid);
        }
        return localTime;
    }

    public static DateTime ResolveScheduledAtUtc(
        DateOnly localDate,
        TimeOnly localTime,
        string normalizedTimeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(
            NormalizeTimeZoneId(normalizedTimeZoneId));
        var local = DateTime.SpecifyKind(
            localDate.ToDateTime(localTime),
            DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            throw new InvalidOperationException(DstInvalidLocalTime);

        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
    }

    public static DateTime NextDueAtUtc(
        DateTime observedAtUtc,
        TimeOnly localTime,
        string normalizedTimeZoneId)
    {
        observedAtUtc = observedAtUtc.Kind == DateTimeKind.Utc
            ? observedAtUtc
            : observedAtUtc.ToUniversalTime();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(
            NormalizeTimeZoneId(normalizedTimeZoneId));
        var localObserved = TimeZoneInfo.ConvertTimeFromUtc(
            observedAtUtc,
            zone);
        var candidateDate = DateOnly.FromDateTime(localObserved);
        DateTime candidate;
        try
        {
            candidate = ResolveScheduledAtUtc(
                candidateDate,
                localTime,
                normalizedTimeZoneId);
        }
        catch (InvalidOperationException error)
            when (error.Message == DstInvalidLocalTime)
        {
            candidate = ResolveScheduledAtUtc(
                candidateDate.AddDays(1),
                localTime,
                normalizedTimeZoneId);
        }
        if (candidate <= observedAtUtc)
        {
            candidate = ResolveScheduledAtUtc(
                candidateDate.AddDays(1),
                localTime,
                normalizedTimeZoneId);
        }
        return candidate;
    }

    public static string PeriodKey(
        DateTime scheduledAtUtc,
        string normalizedTimeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(
            NormalizeTimeZoneId(normalizedTimeZoneId));
        return TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(scheduledAtUtc, DateTimeKind.Utc),
                zone)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public static string BuildScheduleId(
        string workId,
        string flowTemplateVersionId,
        string scheduleKey)
        => StableObjectId(
            $"{workId}\n{flowTemplateVersionId}\n{scheduleKey.Trim()}\n{PolicyVersion}");

    public static string BuildOccurrenceId(
        string scheduleId,
        string periodKey)
        => StableObjectId($"{scheduleId}\n{periodKey}\noccurrence");

    public static string BuildLaunchCommandId(
        string scheduleId,
        string periodKey)
        => $"periodic:{scheduleId}:{periodKey}";

    public static string BuildInstanceId(
        string scheduleId,
        string periodKey)
        => StableObjectId($"{scheduleId}\n{periodKey}\ninstance");

    private static string StableObjectId(string seed)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return new ObjectId(bytes[..12]).ToString();
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } &&
           value.All(character =>
               character is >= '0' and <= '9' or
                   >= 'a' and <= 'f');

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
        var rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(
                   leftBytes,
                   rightBytes);
    }
}
