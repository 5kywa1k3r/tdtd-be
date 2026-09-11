using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicFlows;

namespace tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowTemplatePayloadContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    internal static string ResolveInput(
        DynamicFlowTemplatePayloadDto? payload,
        string? payloadJson)
    {
        if (payload is not null && payloadJson is not null)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FLOW_TEMPLATE_PAYLOAD_AMBIGUOUS,
                new
                {
                    path = "payload",
                    legacyField = "payloadJson",
                    reason = "DYNAMIC_FLOW_TEMPLATE_PAYLOAD_AMBIGUOUS"
                });
        }

        if (payload is null)
            return payloadJson ?? "{}";

        var root = JsonSerializer.SerializeToNode(payload, JsonOptions)?.AsObject()
                   ?? new JsonObject();
#pragma warning disable CS0618
        if (payload.Nodes.Count == 0 && payload.Steps.Count > 0)
        {
            root["schemaVersion"] = 1;
            root.Remove("nodes");
            root.Remove("edges");
            if (string.IsNullOrWhiteSpace(payload.ArchetypeId))
                root.Remove("archetypeId");
            if (string.IsNullOrWhiteSpace(payload.EntryStepId))
                root.Remove("entryStepId");
        }
        else
        {
            if (payload.Steps.Count == 0)
                root.Remove("steps");
            if (payload.Transitions.Count == 0)
                root.Remove("transitions");
        }
#pragma warning restore CS0618
        return root.ToJsonString(JsonOptions);
    }

    internal static DynamicFlowTemplatePayloadDto ReadCanonical(
        string? payloadJson,
        string? rootDynamicFormTemplateId,
        bool tolerateRequiresReview = false)
    {
        try
        {
            var result = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                payloadJson,
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: true,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: false,
                    AllowHistoricalCatalogPins: true));
            if (!string.IsNullOrWhiteSpace(rootDynamicFormTemplateId) &&
                string.IsNullOrWhiteSpace(result.Payload.RootDynamicFormTemplateId))
            {
                result.Payload.RootDynamicFormTemplateId = rootDynamicFormTemplateId;
            }
            return result.Payload;
        }
        catch (Exception error) when (tolerateRequiresReview && IsStoredPayloadValidationFailure(error))
        {
            // A review-required legacy snapshot is intentionally not trusted or
            // rewritten by startup migration. Keep the raw payloadJson in the
            // response and provide only a best-effort typed view for an
            // authorized repair UI. Canonical rows continue to fail closed.
            return ReadReviewRequiredPayload(payloadJson, rootDynamicFormTemplateId);
        }
    }

    private static DynamicFlowTemplatePayloadDto ReadReviewRequiredPayload(
        string? payloadJson,
        string? rootDynamicFormTemplateId)
    {
        var fallback = new DynamicFlowTemplatePayloadDto
        {
            SchemaVersion = 0,
            RootDynamicFormTemplateId = rootDynamicFormTemplateId
        };
        if (string.IsNullOrWhiteSpace(payloadJson))
            return fallback;

        try
        {
            var root = JsonNode.Parse(payloadJson) as JsonObject;
            if (root is null)
                return fallback;
            var payload = root.Deserialize<DynamicFlowTemplatePayloadDto>(JsonOptions) ?? fallback;
            if (!root.TryGetPropertyValue("schemaVersion", out var schemaNode) ||
                schemaNode is not JsonValue schemaValue ||
                !schemaValue.TryGetValue<int>(out var schemaVersion))
            {
                payload.SchemaVersion = 0;
            }
            else
            {
                payload.SchemaVersion = schemaVersion;
            }
            if (string.IsNullOrWhiteSpace(payload.RootDynamicFormTemplateId))
                payload.RootDynamicFormTemplateId = rootDynamicFormTemplateId;
            return payload;
        }
        catch (Exception error) when (IsStoredPayloadValidationFailure(error))
        {
            return fallback;
        }
    }

    private static bool IsStoredPayloadValidationFailure(Exception error)
        => error is AppException or JsonException or NotSupportedException or InvalidOperationException;
}
