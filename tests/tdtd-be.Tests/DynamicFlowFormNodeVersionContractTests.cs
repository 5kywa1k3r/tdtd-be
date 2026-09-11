using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.DynamicForms;

internal static class DynamicFlowFormNodeVersionContractTests
{
    public static void Run()
    {
        ServerCanonicalizesV1AndV2NodeMetadata();
        LockRejectsMissingPublishedSchemaHash();
        LockRejectsMismatchedPublishedSchemaHash();
    }

    private static void ServerCanonicalizesV1AndV2NodeMetadata()
    {
        var familyId = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
        var v1 = PublishedForm(familyId, versionNo: 1, "field-v1");
        var v2 = PublishedForm(familyId, versionNo: 2, "field-v2");
        var forms = new Dictionary<string, DynamicFormTemplate>(StringComparer.Ordinal)
        {
            [v1.Id] = v1,
            [v2.Id] = v2
        };

        var normalized = DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "rootDynamicFormTemplateId": "{{v1.Id}}",
              "formNodes": [
                {
                  "formNodeId": "root",
                  "role": "ROOT",
                  "dynamicFormTemplateId": "{{v1.Id}}",
                  "dynamicFormFamilyId": "{{MongoDB.Bson.ObjectId.GenerateNewId()}}",
                  "dynamicFormVersionNo": 999,
                  "dynamicFormSchemaHash": "client-forged-hash"
                },
                {
                  "formNodeId": "next",
                  "role": "CHILD",
                  "dynamicFormTemplateId": "{{v2.Id}}"
                }
              ],
              "steps": [
                {
                  "stepId": "root-step",
                  "stepCode": "ROOT",
                  "formNodeId": "root",
                  "dynamicFormTemplateId": "{{v1.Id}}"
                },
                {
                  "stepId": "next-step",
                  "stepCode": "NEXT",
                  "formNodeId": "next",
                  "dynamicFormTemplateId": "{{v2.Id}}"
                }
              ],
              "transitions": [
                { "fromStepId": "root-step", "toStepId": "next-step" }
              ]
            }
            """,
            requireLockable: false,
            v1.Id,
            forms);

        using var document = JsonDocument.Parse(normalized);
        var nodes = document.RootElement.GetProperty("formNodes");
        AssertNode(nodes[0], v1, "root v1 node");
        AssertNode(nodes[1], v2, "child v2 node");

        var steps = document.RootElement.GetProperty("steps");
        AssertEqual(v1.Id, steps[0].GetProperty("dynamicFormTemplateId").GetString(), "root step exact form id");
        AssertEqual(v2.Id, steps[1].GetProperty("dynamicFormTemplateId").GetString(), "child step exact form id");
        AssertFalse(steps[0].TryGetProperty("dynamicFormFamilyId", out _), "step must not duplicate family metadata");
        AssertFalse(steps[0].TryGetProperty("dynamicFormVersionNo", out _), "step must not duplicate version metadata");
        AssertFalse(steps[0].TryGetProperty("dynamicFormSchemaHash", out _), "step must not duplicate schema hash");
    }

    private static void LockRejectsMissingPublishedSchemaHash()
    {
        var form = PublishedForm(MongoDB.Bson.ObjectId.GenerateNewId().ToString(), versionNo: 1, "field");
        form.PublishedSchemaHash = null;

        var error = AssertThrowsAppException(
            () => DynamicFlowTemplateService.EnsureLockableDynamicFormVersions(new[] { form }));

        AssertReason(error, "DYNAMIC_FLOW_PUBLISHED_FORM_SCHEMA_HASH_REQUIRED");
    }

    private static void LockRejectsMismatchedPublishedSchemaHash()
    {
        var form = PublishedForm(MongoDB.Bson.ObjectId.GenerateNewId().ToString(), versionNo: 1, "field");
        form.PublishedSchemaHash = new string('f', 64);

        var error = AssertThrowsAppException(
            () => DynamicFlowTemplateService.EnsureLockableDynamicFormVersions(new[] { form }));

        AssertReason(error, "DYNAMIC_FLOW_PUBLISHED_FORM_SCHEMA_HASH_MISMATCH");
    }

    private static DynamicFormTemplate PublishedForm(
        string familyId,
        int versionNo,
        string fieldId)
    {
        var form = new DynamicFormTemplate
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
            FamilyId = familyId,
            VersionNo = versionNo,
            Code = $"FORM_V{versionNo}",
            Name = $"Form v{versionNo}",
            IsActive = true,
            IsPublished = true,
            SectionsJson = """[{"id":"main","title":"Main","order":1}]""",
            FieldsJson = $$"""[{"id":"{{fieldId}}","sectionId":"main","name":"Value","type":"shortText"}]""",
            BlocksJson = "[]"
        };
        var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(form);
        form.PublishedSchemaSnapshotJson = snapshot.Json;
        form.PublishedSchemaHash = snapshot.Sha256;
        return form;
    }

    private static void AssertNode(JsonElement node, DynamicFormTemplate form, string context)
    {
        AssertEqual(form.Id, node.GetProperty("dynamicFormTemplateId").GetString(), $"{context} exact id");
        AssertEqual(form.FamilyId, node.GetProperty("dynamicFormFamilyId").GetString(), $"{context} family id");
        AssertEqual(form.VersionNo, node.GetProperty("dynamicFormVersionNo").GetInt32(), $"{context} version");
        AssertEqual(form.PublishedSchemaHash, node.GetProperty("dynamicFormSchemaHash").GetString(), $"{context} hash");
    }

    private static AppException AssertThrowsAppException(Action action)
    {
        try
        {
            action();
        }
        catch (AppException error) when (error.Code == AppErrorCode.COMMON_VALIDATION_FAILED)
        {
            return error;
        }

        throw new InvalidOperationException("Expected COMMON_VALIDATION_FAILED was not thrown.");
    }

    private static void AssertReason(AppException error, string reason)
    {
        var details = JsonSerializer.Serialize(error.Details);
        if (!details.Contains(reason, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected reason '{reason}', got '{details}'.");
    }

    private static void AssertEqual<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected '{expected}', got '{actual}'.");
    }

    private static void AssertFalse(bool value, string message)
    {
        if (value)
            throw new InvalidOperationException(message);
    }
}
