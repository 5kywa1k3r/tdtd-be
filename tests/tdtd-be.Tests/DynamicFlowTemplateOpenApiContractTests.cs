using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using System.Reflection;
using System.Text.Json;
using Swashbuckle.AspNetCore.Swagger;
using tdtd_be.Common.Errors;
using tdtd_be.Controllers;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.Models;
using tdtd_be.OpenApi;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowTemplateOpenApiContractTests
{
    public static void Run()
    {
        AssertTypedAction<PagedResult<DynamicFlowTemplateDto>>(
            nameof(DynamicFlowTemplatesController.Search));
        AssertTypedAction<DynamicFlowTemplateDto>(
            nameof(DynamicFlowTemplatesController.Get));
        AssertTypedAction<DynamicFlowTemplateDto>(
            nameof(DynamicFlowTemplatesController.Create));
        AssertTypedAction<DynamicFlowTemplateDto>(
            nameof(DynamicFlowTemplatesController.Update));
        AssertTypedAction<List<DynamicFlowTemplateVersionDto>>(
            nameof(DynamicFlowTemplatesController.ListVersions));
        AssertTypedAction<DynamicFlowTemplateVersionDto>(
            nameof(DynamicFlowTemplatesController.GetVersion));
        AssertTypedAction<DynamicFlowTemplateVersionDto>(
            nameof(DynamicFlowTemplatesController.SaveDraftVersion));
        AssertTypedAction<DynamicFlowTemplateVersionDto>(
            nameof(DynamicFlowTemplatesController.LockVersion));
        AssertTypedAction<DynamicFlowTemplateDto>(
            nameof(DynamicFlowTemplatesController.Archive));
        AssertTypedAction<DynamicFlowTemplateDto>(
            nameof(DynamicFlowTemplatesController.Clone));
        AssertTypedAction<DynamicFlowTemplateVersionDto>(
            nameof(DynamicFlowTemplatesController.ReopenVersion));
        AssertTypedAction<DiffDynamicFlowTemplateVersionsDto>(
            nameof(DynamicFlowTemplatesController.DiffVersions));
        AssertP4MutationErrorContracts();
        AssertDeleteContract();
        AssertWorkAssignmentMappingContracts();
        AssertTypedPayloadCompanionContract();
        AssertGeneratedOpenApiSchemas();
    }

    private static void AssertTypedPayloadCompanionContract()
    {
        const string rootFormId = "100000000000000000000088";
        var payload = new DynamicFlowTemplatePayloadDto
        {
            RootDynamicFormTemplateId = rootFormId,
            FormNodes =
            {
                new DynamicFlowFormNodeDto
                {
                    FormNodeId = "root",
                    Role = "ROOT",
                    DynamicFormTemplateId = rootFormId,
                    DynamicFormFamilyId = rootFormId,
                    DynamicFormVersionNo = 1,
                    DynamicFormSchemaHash = "schema-hash"
                }
            },
            Steps =
            {
                new DynamicFlowStepDefinitionDto
                {
                    StepId = "draft",
                    StepCode = "DRAFT",
                    StepOrder = 1,
                    FormNodeId = "root",
                    DynamicFormTemplateId = rootFormId
                }
            },
            ActorPolicies =
            {
                new DynamicFlowActorPolicyDto
                {
                    PolicyId = "actor-draft",
                    StepId = "draft",
                    ActorRole = "ASSIGNEE",
                    AllowSubFlow = true
                }
            },
            FieldPolicies =
            {
                new DynamicFlowFieldPolicyDto
                {
                    PolicyId = "field-draft",
                    StepId = "draft",
                    ActorRole = "ASSIGNEE",
                    FieldKey = "amount",
                    Read = true,
                    Write = true
                }
            },
            TableColumnPolicies =
            {
                new DynamicFlowTableColumnPolicyDto
                {
                    PolicyId = "column-draft",
                    StepId = "draft",
                    ActorRole = "ASSIGNEE",
                    BlockId = "details",
                    ColumnKey = "amount",
                    Read = true
                }
            },
            MappingRules =
            {
                new DynamicFlowMappingRuleDto
                {
                    MappingId = "copy-amount",
                    MappingVersion = 1,
                    MappingKind = "FIELD",
                    SourceStepId = "draft",
                    TargetStepId = "draft",
                    SourceFieldKey = "amount",
                    TargetFieldKey = "total",
                    ValueTransform = "COPY"
                }
            }
        };
        payload.RollbackPolicy["mode"] = JsonValue("REJECT");
        payload.FinalResultPolicy["mode"] = JsonValue("ROOT_REPORT");

        var typedJson = DynamicFlowTemplatePayloadContract.ResolveInput(payload, payloadJson: null);
        var normalized = DynamicFlowTemplateService.NormalizePayloadJson(
            typedJson,
            requireLockable: false,
            rootFormId,
            dynamicFormTemplates: null);
        var roundTrip = DynamicFlowTemplatePayloadContract.ReadCanonical(normalized, rootFormId);

        Require(roundTrip.FormNodes.Count == 1, "Typed payload should preserve canonical formNodes");
        Require(
            roundTrip.FormNodes[0].DynamicFormFamilyId == rootFormId &&
            roundTrip.FormNodes[0].DynamicFormVersionNo == 1 &&
            roundTrip.FormNodes[0].DynamicFormSchemaHash == "schema-hash",
            "Typed payload should preserve Dynamic Form version provenance");
        Require(roundTrip.SchemaVersion == 2, "Legacy typed payload should adapt once to canonical schema v2");
        Require(roundTrip.Nodes.Count == 1, "Typed payload should adapt legacy steps to canonical nodes");
        Require(roundTrip.Edges.Count == 0, "Typed payload should adapt legacy transitions to canonical edges");
        Require(roundTrip.ActorPolicies.Count == 1, "Typed payload should preserve actorPolicies");
        Require(roundTrip.FieldPolicies.Count == 1, "Typed payload should preserve fieldPolicies");
        Require(roundTrip.TableColumnPolicies.Count == 1, "Typed payload should preserve tableColumnPolicies");
        Require(roundTrip.MappingRules.Count == 1, "Typed payload should preserve mappingRules");
        Require(roundTrip.RollbackPolicy.ContainsKey("mode"), "Typed payload should preserve rollbackPolicy");
        Require(roundTrip.FinalResultPolicy.ContainsKey("mode"), "Typed payload should preserve finalResultPolicy");

        var mapVersion = typeof(DynamicFlowTemplateService).GetMethod(
            "MapVersion",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(DynamicFlowTemplateService), "MapVersion");
        var versionResponse = (DynamicFlowTemplateVersionDto)mapVersion.Invoke(
            null,
            new object?[]
            {
                new DynamicFlowTemplateVersion
                {
                    Id = "100000000000000000000089",
                    TemplateId = "100000000000000000000090",
                    RootDynamicFormTemplateId = rootFormId,
                    PayloadJson = normalized,
                    PayloadHash = "payload-hash"
                },
                false
            })!;
        Require(
            versionResponse.PayloadJson == normalized,
            "Version responses must retain the exact legacy payloadJson companion");
        Require(
            versionResponse.Payload.Nodes.Count == 1,
            "Version responses must project the canonical typed payload companion");

        const string legacyJson = "{\"steps\":[]}";
        Require(
            DynamicFlowTemplatePayloadContract.ResolveInput(payload: null, legacyJson) == legacyJson,
            "Legacy payloadJson-only requests must keep their existing input contract");
        Require(
            DynamicFlowTemplatePayloadContract.ResolveInput(payload: null, payloadJson: null) == "{}",
            "Omitted payload input must keep the existing empty-object default");

        try
        {
            _ = DynamicFlowTemplatePayloadContract.ResolveInput(payload, legacyJson);
            throw new InvalidOperationException("Typed payload and payloadJson should be rejected as ambiguous");
        }
        catch (AppException ex)
        {
            Require(
                ex.Code == AppErrorCode.DYNAMIC_FLOW_TEMPLATE_PAYLOAD_AMBIGUOUS,
                "Ambiguous payload should use DYNAMIC_FLOW_TEMPLATE_PAYLOAD_AMBIGUOUS");
            var detailsJson = JsonSerializer.Serialize(ex.Details);
            Require(
                detailsJson.Contains("DYNAMIC_FLOW_TEMPLATE_PAYLOAD_AMBIGUOUS", StringComparison.Ordinal),
                "Ambiguous payload should expose the stable reason code");
        }
    }

    private static void AssertTypedAction<TResponse>(string methodName)
        => AssertTypedAction<TResponse>(typeof(DynamicFlowTemplatesController), methodName);

    private static void AssertTypedAction<TResponse>(Type controllerType, string methodName)
    {
        var method = RequireMethod(controllerType, methodName);
        var expectedReturnType = typeof(Task<ActionResult<TResponse>>);
        Require(
            method.ReturnType == expectedReturnType,
            $"{methodName} should return {expectedReturnType}, actual {method.ReturnType}");

        AssertResponseMetadata(
            controllerType,
            methodName,
            StatusCodes.Status200OK,
            typeof(TResponse));
        AssertResponseMetadata(
            controllerType,
            methodName,
            StatusCodes.Status401Unauthorized,
            typeof(AppErrorResponse));
        AssertResponseMetadata(
            controllerType,
            methodName,
            StatusCodes.Status403Forbidden,
            typeof(AppErrorResponse));
    }

    private static void AssertWorkAssignmentMappingContracts()
    {
        var controllerType = typeof(WorkAssignmentReportsController);
        var actions = new[]
        {
            nameof(WorkAssignmentReportsController.PreviewDynamicFlowMapping),
            nameof(WorkAssignmentReportsController.ApplyDynamicFlowMapping)
        };

        AssertTypedAction<DynamicFlowMappingPreviewResponse>(controllerType, actions[0]);
        AssertTypedAction<WorkAssignmentReportResponse>(controllerType, actions[1]);

        foreach (var action in actions)
        {
            AssertResponseMetadata(
                controllerType,
                action,
                StatusCodes.Status400BadRequest,
                typeof(AppErrorResponse));
            AssertResponseMetadata(
                controllerType,
                action,
                StatusCodes.Status404NotFound,
                typeof(AppErrorResponse));
        }
    }

    private static void AssertP4MutationErrorContracts()
    {
        var actions = new[]
        {
            nameof(DynamicFlowTemplatesController.Create),
            nameof(DynamicFlowTemplatesController.Update),
            nameof(DynamicFlowTemplatesController.Archive),
            nameof(DynamicFlowTemplatesController.Clone),
            nameof(DynamicFlowTemplatesController.SaveDraftVersion),
            nameof(DynamicFlowTemplatesController.SaveDraftVersionLegacy),
            nameof(DynamicFlowTemplatesController.LockVersion),
            nameof(DynamicFlowTemplatesController.LockVersionLegacy),
            nameof(DynamicFlowTemplatesController.ReopenVersion)
        };

        foreach (var action in actions)
        {
            AssertResponseMetadata(
                action,
                StatusCodes.Status400BadRequest,
                typeof(AppErrorResponse));
            AssertResponseMetadata(
                action,
                StatusCodes.Status409Conflict,
                typeof(AppErrorResponse));
            AssertResponseMetadata(
                action,
                StatusCodes.Status503ServiceUnavailable,
                typeof(AppErrorResponse));
        }
    }

    private static void AssertDeleteContract()
    {
        var methodName = nameof(DynamicFlowTemplatesController.Delete);
        var method = RequireMethod(methodName);
        Require(
            method.ReturnType == typeof(Task<IActionResult>),
            $"{methodName} should keep its no-content IActionResult contract");
        AssertResponseMetadata(methodName, StatusCodes.Status204NoContent, expectedType: null);
        AssertResponseMetadata(
            methodName,
            StatusCodes.Status401Unauthorized,
            typeof(AppErrorResponse));
        AssertResponseMetadata(
            methodName,
            StatusCodes.Status403Forbidden,
            typeof(AppErrorResponse));
        AssertResponseMetadata(
            methodName,
            StatusCodes.Status409Conflict,
            typeof(AppErrorResponse));
        AssertResponseMetadata(
            methodName,
            StatusCodes.Status503ServiceUnavailable,
            typeof(AppErrorResponse));
    }

    private static void AssertResponseMetadata(
        string methodName,
        int statusCode,
        Type? expectedType)
        => AssertResponseMetadata(
            typeof(DynamicFlowTemplatesController),
            methodName,
            statusCode,
            expectedType);

    private static void AssertResponseMetadata(
        Type controllerType,
        string methodName,
        int statusCode,
        Type? expectedType)
    {
        var attributes = RequireMethod(controllerType, methodName)
            .GetCustomAttributes(typeof(ProducesResponseTypeAttribute), inherit: true)
            .Cast<ProducesResponseTypeAttribute>()
            .Where(attribute => attribute.StatusCode == statusCode)
            .ToList();
        Require(
            attributes.Count == 1,
            $"{methodName} should declare one OpenAPI response for HTTP {statusCode}");

        if (expectedType is not null)
        {
            Require(
                attributes[0].Type == expectedType,
                $"{methodName} HTTP {statusCode} should expose {expectedType}, actual {attributes[0].Type}");
        }
    }

    private static void AssertGeneratedOpenApiSchemas()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "OpenApiContractTest"
        });
        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(DynamicFlowTemplatesController).Assembly);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Dynamic Flow OpenAPI contract test",
                Version = "v1"
            });
            options.SchemaFilter<DeprecatedSchemaPropertyFilter>();
        });

        using var app = builder.Build();
        var document = app.Services
            .GetRequiredService<ISwaggerProvider>()
            .GetSwagger("v1");

        if (typeof(DynamicFlowTemplatesController).GetCustomAttribute<NonControllerAttribute>() is not null)
        {
            Require(
                !document.Paths.Keys.Any(path => path.StartsWith(
                    "/api/dynamic-flow-templates", StringComparison.Ordinal)),
                "Temporarily disabled Dynamic Flow design must not be advertised in OpenAPI");
            return;
        }

        var searchSchema = ResolveResponseSchema(
            document,
            "/api/dynamic-flow-templates/search",
            OperationType.Post,
            StatusCodes.Status200OK);
        AssertProperties(searchSchema, "rows", "totalRows", "page", "pageSize");

        var detailSchema = ResolveResponseSchema(
            document,
            "/api/dynamic-flow-templates/{familyId}",
            OperationType.Get,
            StatusCodes.Status200OK);
        AssertProperties(detailSchema, "id", "status", "versions");
        Require(
            detailSchema.Properties["status"].Type == "string",
            "Dynamic Flow status must keep its existing string wire contract");

        var versionSchema = ResolveResponseSchema(
            document,
            "/api/dynamic-flow-templates/{familyId}/versions/draft",
            OperationType.Put,
            StatusCodes.Status200OK);
        AssertProperties(versionSchema, "id", "draftRevision", "payload", "payloadJson");
        Require(
            versionSchema.Properties["payloadJson"].Type == "string",
            "Legacy payloadJson must remain a string for existing clients");
        Require(
            versionSchema.Properties["payloadJson"].Deprecated,
            "Legacy response payloadJson should be marked deprecated in OpenAPI");
        AssertCanonicalPayloadSchema(document, versionSchema.Properties["payload"]);

        var exactVersionSchema = ResolveResponseSchema(
            document,
            "/api/dynamic-flow-templates/{familyId}/versions/{versionId}",
            OperationType.Get,
            StatusCodes.Status200OK);
        AssertProperties(
            exactVersionSchema,
            "id",
            "familyId",
            "draftRevision",
            "payloadHash",
            "definitionLockable",
            "executionEligibility",
            "canExecute");

        _ = RequireOperation(
            document,
            "/api/dynamic-flow-templates/{familyId}/versions/{versionId}/draft",
            OperationType.Put);
        _ = RequireOperation(
            document,
            "/api/dynamic-flow-templates/{familyId}/versions/{versionId}/lock",
            OperationType.Post);
        _ = RequireOperation(
            document,
            "/api/dynamic-flow-templates/{familyId}/versions/{versionId}/reopen",
            OperationType.Post);
        _ = RequireOperation(
            document,
            "/api/dynamic-flow-templates/{familyId}/versions/diff",
            OperationType.Post);
        _ = RequireOperation(
            document,
            "/api/dynamic-flow-templates/{familyId}/archive",
            OperationType.Post);
        _ = RequireOperation(
            document,
            "/api/dynamic-flow-templates/{familyId}/clone",
            OperationType.Post);

        var createRequestSchema = ResolveRequestSchema(
            document,
            "/api/dynamic-flow-templates",
            OperationType.Post);
        AssertProperties(createRequestSchema, "commandId", "payload", "payloadJson");
        Require(
            createRequestSchema.Properties["payloadJson"].Deprecated,
            "Legacy create payloadJson should be marked deprecated in OpenAPI");
        AssertCanonicalPayloadSchema(document, createRequestSchema.Properties["payload"]);
        var createOperation = RequireOperation(
            document,
            "/api/dynamic-flow-templates",
            OperationType.Post);
        Require(
            createOperation.Parameters.Any(parameter =>
                parameter.In == ParameterLocation.Header &&
                string.Equals(parameter.Name, "Idempotency-Key", StringComparison.OrdinalIgnoreCase)),
            "Create OpenAPI contract must expose the Idempotency-Key header alias");

        var saveDraftRequestSchema = ResolveRequestSchema(
            document,
            "/api/dynamic-flow-templates/{familyId}/versions/draft",
            OperationType.Put);
        AssertProperties(saveDraftRequestSchema, "payload", "payloadJson");
        Require(
            saveDraftRequestSchema.Properties["payloadJson"].Deprecated,
            "Legacy save payloadJson should be marked deprecated in OpenAPI");
        AssertCanonicalPayloadSchema(document, saveDraftRequestSchema.Properties["payload"]);

        var createInstanceRequestSchema = ResolveRequestSchema(
            document,
            "/api/works/{workId}/dynamic-flows/instances",
            OperationType.Post);
        Require(
            !createInstanceRequestSchema.Properties.ContainsKey("flowRole"),
            "CreateDynamicFlowInstanceRequest.flowRole is server-derived and must stay out of OpenAPI");

        var errorSchema = ResolveResponseSchema(
            document,
            "/api/dynamic-flow-templates/{familyId}",
            OperationType.Get,
            StatusCodes.Status401Unauthorized);
        AssertProperties(errorSchema, "errorCode", "service", "message", "traceId");

        var deleteOperation = RequireOperation(
            document,
            "/api/dynamic-flow-templates/{familyId}",
            OperationType.Delete);
        Require(
            deleteOperation.Responses.ContainsKey(StatusCodes.Status204NoContent.ToString()),
            "Delete OpenAPI contract should expose HTTP 204 without changing runtime behavior");
        var deleteRequestSchema = ResolveRequestSchema(
            document,
            "/api/dynamic-flow-templates/{familyId}",
            OperationType.Delete);
        AssertProperties(deleteRequestSchema, "commandId", "expectedFamilyRevision");

        var mappingPreviewSchema = ResolveResponseSchema(
            document,
            "/api/work-assignment-reports/{id}/draft/preview-dynamic-flow-mapping",
            OperationType.Post,
            StatusCodes.Status200OK);
        AssertProperties(
            mappingPreviewSchema,
            "targetReportId",
            "targetAssignmentId",
            "fieldValuesJson",
            "tableValuesJson",
            "sourceReports",
            "changes",
            "hasBlockingConflicts");
        Require(
            mappingPreviewSchema.Properties["fieldValuesJson"].Type == "string" &&
            mappingPreviewSchema.Properties["tableValuesJson"].Type == "string",
            "Mapping preview JSON payload fields must keep their existing string wire contract");

        var mappingApplySchema = ResolveResponseSchema(
            document,
            "/api/work-assignment-reports/{id}/draft/apply-dynamic-flow-mapping",
            OperationType.Post,
            StatusCodes.Status200OK);
        AssertProperties(
            mappingApplySchema,
            "id",
            "workAssignmentId",
            "status",
            "fieldValuesJson",
            "tableValuesJson",
            "dynamicFlowPermissions");

        var mappingPreviewErrorSchema = ResolveResponseSchema(
            document,
            "/api/work-assignment-reports/{id}/draft/preview-dynamic-flow-mapping",
            OperationType.Post,
            StatusCodes.Status400BadRequest);
        AssertProperties(mappingPreviewErrorSchema, "errorCode", "service", "message", "traceId");

        var mappingApplyErrorSchema = ResolveResponseSchema(
            document,
            "/api/work-assignment-reports/{id}/draft/apply-dynamic-flow-mapping",
            OperationType.Post,
            StatusCodes.Status404NotFound);
        AssertProperties(mappingApplyErrorSchema, "errorCode", "service", "message", "traceId");
    }

    private static OpenApiSchema ResolveResponseSchema(
        OpenApiDocument document,
        string path,
        OperationType operationType,
        int statusCode)
    {
        var operation = RequireOperation(document, path, operationType);
        var responseKey = statusCode.ToString();
        Require(
            operation.Responses.TryGetValue(responseKey, out var response),
            $"OpenAPI {operationType} {path} should expose HTTP {responseKey}");
        var content = response!.Content
            .FirstOrDefault(item => item.Key.Contains("json", StringComparison.OrdinalIgnoreCase))
            .Value
            ?? response.Content.Values.FirstOrDefault();
        Require(content?.Schema is not null, $"OpenAPI {operationType} {path} HTTP {responseKey} should have a schema");
        return ResolveSchema(document, content!.Schema);
    }

    private static OpenApiSchema ResolveRequestSchema(
        OpenApiDocument document,
        string path,
        OperationType operationType)
    {
        var operation = RequireOperation(document, path, operationType);
        Require(operation.RequestBody is not null, $"OpenAPI {operationType} {path} should have a request body");
        var content = operation.RequestBody!.Content
            .FirstOrDefault(item => item.Key.Contains("json", StringComparison.OrdinalIgnoreCase))
            .Value
            ?? operation.RequestBody.Content.Values.FirstOrDefault();
        Require(content?.Schema is not null, $"OpenAPI {operationType} {path} request should have a schema");
        return ResolveSchema(document, content!.Schema);
    }

    private static void AssertCanonicalPayloadSchema(
        OpenApiDocument document,
        OpenApiSchema payloadPropertySchema)
    {
        var payloadSchema = ResolveSchema(document, payloadPropertySchema);
        AssertProperties(
            payloadSchema,
            "rootDynamicFormTemplateId",
            "formNodes",
            "steps",
            "transitions",
            "actorPolicies",
            "fieldPolicies",
            "tableColumnPolicies",
            "mappingRules",
            "rollbackPolicy",
            "finalResultPolicy",
            "statisticProfile");

        foreach (var propertyName in new[]
                 {
                     "formNodes",
                     "steps",
                     "transitions",
                     "actorPolicies",
                     "fieldPolicies",
                     "tableColumnPolicies",
                     "mappingRules"
                 })
        {
            Require(
                payloadSchema.Properties[propertyName].Type == "array",
                $"Typed payload {propertyName} should be an OpenAPI array");
        }

        foreach (var propertyName in new[] { "rollbackPolicy", "finalResultPolicy", "statisticProfile" })
        {
            Require(
                payloadSchema.Properties[propertyName].Type == "object",
                $"Typed payload {propertyName} should be an OpenAPI object");
        }

        AssertProperties(
            ResolveSchema(document, payloadSchema.Properties["formNodes"].Items),
            "formNodeId",
            "role",
            "dynamicFormTemplateId",
            "dynamicFormFamilyId",
            "dynamicFormVersionNo",
            "dynamicFormSchemaHash");
        AssertProperties(
            ResolveSchema(document, payloadSchema.Properties["steps"].Items),
            "stepId",
            "stepCode",
            "stepOrder",
            "formNodeId",
            "dynamicFormTemplateId");
        AssertProperties(
            ResolveSchema(document, payloadSchema.Properties["transitions"].Items),
            "fromStepId",
            "fromStepCode",
            "toStepId",
            "toStepCode");
        AssertProperties(
            ResolveSchema(document, payloadSchema.Properties["actorPolicies"].Items),
            "policyId",
            "stepId",
            "stepCode",
            "actorRole",
            "allowSubFlow",
            "allowForward",
            "canFinalize");
        AssertProperties(
            ResolveSchema(document, payloadSchema.Properties["fieldPolicies"].Items),
            "policyId",
            "stepId",
            "stepCode",
            "actorRole",
            "fieldId",
            "fieldKey",
            "read",
            "write",
            "required",
            "hidden",
            "locked",
            "lockedAfterSubmit");
        AssertProperties(
            ResolveSchema(document, payloadSchema.Properties["tableColumnPolicies"].Items),
            "policyId",
            "stepId",
            "stepCode",
            "actorRole",
            "blockId",
            "columnKey",
            "read",
            "write",
            "required",
            "hidden",
            "locked",
            "lockedAfterSubmit");
        var mappingRuleSchema = ResolveSchema(document, payloadSchema.Properties["mappingRules"].Items);
        AssertProperties(mappingRuleSchema, "mappingId", "mappingVersion", "inputs", "target", "calculation");
    }

    private static OpenApiOperation RequireOperation(
        OpenApiDocument document,
        string path,
        OperationType operationType)
    {
        Require(document.Paths.TryGetValue(path, out var pathItem), $"OpenAPI path missing: {path}");
        Require(
            pathItem!.Operations.TryGetValue(operationType, out var operation),
            $"OpenAPI operation missing: {operationType} {path}");
        return operation!;
    }

    private static OpenApiSchema ResolveSchema(OpenApiDocument document, OpenApiSchema schema)
    {
        if (schema.Reference is null)
        {
            if (schema.AllOf.Count == 1)
                return ResolveSchema(document, schema.AllOf[0]);
            return schema;
        }

        Require(
            document.Components.Schemas.TryGetValue(schema.Reference.Id, out var resolved),
            $"OpenAPI schema reference missing: {schema.Reference.Id}");
        return resolved!;
    }

    private static JsonElement JsonValue(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static void AssertProperties(OpenApiSchema schema, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            Require(
                schema.Properties.ContainsKey(propertyName),
                $"OpenAPI schema should contain property '{propertyName}'");
        }
    }

    private static System.Reflection.MethodInfo RequireMethod(string methodName)
        => RequireMethod(typeof(DynamicFlowTemplatesController), methodName);

    private static System.Reflection.MethodInfo RequireMethod(Type controllerType, string methodName)
        => controllerType.GetMethod(methodName)
           ?? throw new InvalidOperationException($"Controller action missing: {controllerType.Name}.{methodName}");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
