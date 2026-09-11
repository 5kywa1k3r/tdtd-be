using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string FlowFamiliesCollection = "dynamic_flow_templates";
    private const string FlowVersionsCollection = "dynamic_flow_template_versions";
    private const string FlowDefinitionReceiptsCollection =
        "dynamic_flow_definition_command_receipts";
    private static readonly string[] FlowDefinitionWrites =
    [
        FlowFamiliesCollection,
        FlowVersionsCollection,
        FlowDefinitionReceiptsCollection,
        "user_action_logs"
    ];

    private static readonly string[] P7OwnerSourcePaths =
    [
        "tdtd-be/Services/DynamicFlows/DynamicFlowMappingEngine.cs",
        "tdtd-be/Services/DynamicFlows/DynamicFlowPolicyEvaluator.cs",
        "tdtd-be/Services/DynamicFlows/DynamicFlowMappingCanonicalSourceContract.cs",
        "tdtd-be/Services/DynamicFlows/DynamicFlowMappingRuntimeContract.cs",
        "tdtd-be/Services/DynamicFlows/DynamicFlowMappingSecurityContract.cs",
        "tdtd-be/Models/DynamicFlowMappingPersistence.cs",
        "tdtd-be/Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingPersistence.cs",
        "tdtd-be/Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingActivation.cs",
        "tdtd-be/Services/DynamicFlows/DynamicFlowLockedSnapshotIntegrity.cs",
        "tdtd-be/Services/DynamicFlows/DynamicFlowMappingCallerRedaction.cs",
        "tdtd-be/Services/DynamicFlows/DynamicFlowMappingExpressionEvaluator.cs",
        "tdtd-be/Services/DynamicFlows/DynamicFlowMappingLifecycleContract.cs",
        "tdtd-be/Services/DynamicFlows/DynamicFlowRuntimeMappingLifecycle.cs",
        "tdtd-be/Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingLifecycle.cs"
    ];

    private string _flowRootFormId = string.Empty;
    private string _flowChildFormId = string.Empty;
    private P8FlowDraftFixture? _mainFlow;
    private P8FlowVersionIdentity? _excludeLocked;
    private P8FlowDraftFixture? _includeDraft;
    private P8FlowVersionIdentity? _includeLocked;
    private P8FlowDraftFixture _invalidPolicyDraft = default!;
    private P8FlowDraftFixture _warningDraft = default!;
    private P8FlowDraftFixture _profileDraft = default!;
    private byte[] _excludeLockedBson = [];
    private IReadOnlyDictionary<string, string> _p7SourceFingerprintsBefore =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private async Task SeedP807FixturesAsync(CancellationToken ct)
    {
        var actor = Actor("system_admin");
        var fixedAt = new DateTime(2026, 8, 2, 0, 7, 0, DateTimeKind.Utc);
        var form = BuildP807PublishedForm(
            actor,
            fixedAt,
            "P8_FLOW_CONTRIBUTION_ROOT",
            "field_note",
            "note");
        var childForm = BuildP807PublishedForm(
            actor,
            fixedAt.AddSeconds(1),
            "P8_FLOW_CONTRIBUTION_CHILD",
            "field_child_value",
            "child_value");
        _flowRootFormId = form.Id;
        _flowChildFormId = childForm.Id;
        await _database.GetCollection<DynamicFormTemplate>(DynamicFormsCollection)
            .InsertManyAsync([form, childForm], cancellationToken: ct);

        _p7SourceFingerprintsBefore = CaptureP7OwnerSourceFingerprints();
        _invalidPolicyDraft = await CreateFlowDraftAsync(
            actor,
            "setup-invalid-policy",
            "p8-flw-setup-invalid-policy",
            BuildP807MappedFlowPayload(),
            ct);
        _warningDraft = await CreateFlowDraftAsync(
            actor,
            "setup-warning",
            "p8-flw-setup-warning",
            BuildP807MappedFlowPayload(),
            ct);
        var blockedProfile = BuildP807MappedFlowPayload();
        blockedProfile["statisticProfile"] = new JsonObject
        {
            ["diffMode"] = "COUNT"
        };
        _profileDraft = await CreateFlowDraftAsync(
            actor,
            "setup-profile",
            "p8-flw-setup-profile",
            blockedProfile,
            ct);

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-07-fixtures.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                rootForm = new
                {
                    form.Id,
                    form.FamilyId,
                    form.VersionNo,
                    form.Revision,
                    form.PublishedSchemaHash,
                    publishedSnapshotSha256 = Sha256(
                        System.Text.Encoding.UTF8.GetBytes(
                            form.PublishedSchemaSnapshotJson ?? string.Empty))
                },
                childForm = new
                {
                    childForm.Id,
                    childForm.FamilyId,
                    childForm.VersionNo,
                    childForm.Revision,
                    childForm.PublishedSchemaHash,
                    publishedSnapshotSha256 = Sha256(
                        System.Text.Encoding.UTF8.GetBytes(
                            childForm.PublishedSchemaSnapshotJson ??
                            string.Empty))
                },
                rejectionDrafts = new[]
                {
                    FixtureProjection(_invalidPolicyDraft),
                    FixtureProjection(_warningDraft),
                    FixtureProjection(_profileDraft)
                },
                p7SourceFingerprints = _p7SourceFingerprintsBefore,
                fixtureWritesOutsideCaseDeltas = true,
                previewApplyRunInvoked = false
            },
            ct);
    }

    private DynamicFormTemplate BuildP807PublishedForm(
        P8Actor actor,
        DateTime fixedAt,
        string code,
        string fieldId,
        string fieldKey)
    {
        var formId = ObjectId.GenerateNewId().ToString();
        var form = new DynamicFormTemplate
        {
            Id = formId,
            Code = code,
            Name = $"P8 Flow Contribution {fieldKey}",
            Description = "P8-07 configuration-only published form fixture",
            TagCodes = [],
            CreatedByUsername = actor.Username,
            SchemaVersion = 1,
            VersionNo = 1,
            FamilyId = formId,
            LineageStatus = DynamicFormLineageStatuses.Root,
            Revision = 1,
            IsActive = true,
            IsPublished = true,
            PublishedAtUtc = fixedAt,
            PublishedByUserId = actor.Id,
            SectionsJson = new JsonArray(
                new JsonObject
                {
                    ["id"] = "main",
                    ["title"] = "P8 Flow Root",
                    ["description"] = null,
                    ["tagCodes"] = new JsonArray(),
                    ["order"] = 0
                }).ToJsonString(),
            FieldsJson = new JsonArray(
                new JsonObject
                {
                    ["id"] = fieldId,
                    ["sectionId"] = "main",
                    ["key"] = fieldKey,
                    ["name"] = $"Flow {fieldKey}",
                    ["type"] = "longText",
                    ["required"] = false,
                    ["order"] = 0
                }).ToJsonString(),
            BlocksJson = "[]",
            CreatedByUserId = actor.Id,
            UpdatedByUserId = actor.Id,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };
        var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(form);
        form.PublishedSchemaSnapshotJson = snapshot.Json;
        form.PublishedSchemaHash = snapshot.Sha256;
        return form;
    }

    private JsonObject BuildP807FlowPayload()
        => new()
        {
            ["schemaVersion"] = 2,
            ["archetypeId"] = "FLOW-T01",
            ["entryStepId"] = "step_root",
            ["rootDynamicFormTemplateId"] = _flowRootFormId,
            ["formNodes"] = new JsonArray
            {
                new JsonObject
                {
                    ["formNodeId"] = "root_form",
                    ["role"] = "ROOT",
                    ["dynamicFormTemplateId"] = _flowRootFormId
                }
            },
            ["nodes"] = new JsonArray
            {
                new JsonObject
                {
                    ["nodeId"] = "step_root",
                    ["nodeCode"] = "ROOT",
                    ["nodeKind"] = "FORM_STEP",
                    ["formNodeId"] = "root_form",
                    ["declaredRoles"] = new JsonArray("OWNER")
                }
            },
            ["edges"] = new JsonArray(),
            ["actorPolicies"] = new JsonArray
            {
                new JsonObject
                {
                    ["policyId"] = "actor-owner-root",
                    ["stepId"] = "step_root",
                    ["stepCode"] = "*",
                    ["actorRole"] = "OWNER",
                    ["allowForward"] = true
                }
            },
            ["fieldPolicies"] = new JsonArray
            {
                new JsonObject
                {
                    ["policyId"] = "fields-owner-root",
                    ["dynamicFormTemplateId"] = "*",
                    ["stepId"] = "step_root",
                    ["stepCode"] = "*",
                    ["actorRole"] = "OWNER",
                    ["fieldId"] = "*",
                    ["fieldKey"] = "*",
                    ["read"] = true,
                    ["write"] = true
                }
            },
            ["tableColumnPolicies"] = new JsonArray(),
            ["mappingRules"] = new JsonArray(),
            ["rollbackPolicy"] = new JsonObject(),
            ["finalResultPolicy"] = new JsonObject(),
            ["statisticProfile"] = new JsonObject()
        };

    private IReadOnlyDictionary<string, string> CaptureP7OwnerSourceFingerprints()
        => P7OwnerSourcePaths.ToDictionary(
            path => path,
            path => Sha256(File.ReadAllBytes(
                Path.Combine(
                    _paths.WorkspaceRoot,
                    path.Replace('/', Path.DirectorySeparatorChar)))),
            StringComparer.Ordinal);

    private static object FixtureProjection(P8FlowDraftFixture fixture)
        => new
        {
            fixture.Key,
            fixture.FamilyId,
            fixture.FamilyRevision,
            fixture.Version.Id,
            fixture.Version.VersionNo,
            fixture.Version.DraftRevision,
            fixture.Version.PayloadHash
        };

    private P8FlowDraftFixture RequireMainFlow()
        => _mainFlow ?? throw new HarnessCaseNotRunnableException(
            "P8-07 main Flow draft has not been authored.");

    private P8FlowVersionIdentity RequireExcludeLocked()
        => _excludeLocked ?? throw new HarnessCaseNotRunnableException(
            "P8-07 V_EXCLUDE has not been locked.");

    private P8FlowDraftFixture RequireIncludeDraft()
        => _includeDraft ?? throw new HarnessCaseNotRunnableException(
            "P8-07 V_INCLUDE draft has not been authored.");

    private P8FlowVersionIdentity RequireIncludeLocked()
        => _includeLocked ?? throw new HarnessCaseNotRunnableException(
            "P8-07 V_INCLUDE has not been locked.");
}

internal sealed record P8FlowDraftFixture(
    string Key,
    string FamilyId,
    int FamilyRevision,
    P8FlowVersionIdentity Version);

internal sealed record P8FlowVersionIdentity(
    string Id,
    string FamilyId,
    int VersionNo,
    string Status,
    int DraftRevision,
    int SchemaVersion,
    int AdapterVersion,
    string CatalogVersion,
    string CatalogSemanticHash,
    JsonObject Payload,
    string PayloadJson,
    string PayloadHash,
    bool DefinitionLockable,
    string? ContributionPolicy,
    string? ContributionPolicyHash,
    string? ContributionWarning,
    DateTime? LockedAtUtc,
    string? LockedByUserId,
    JsonObject Raw);
