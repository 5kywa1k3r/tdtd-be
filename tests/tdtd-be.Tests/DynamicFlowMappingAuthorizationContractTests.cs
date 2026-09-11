using System.Reflection;
using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports;

internal static class DynamicFlowMappingAuthorizationContractTests
{
    private sealed record ContractCase(string Id, string Semantic, Action Run);

    private const string PolicyPayload = """
    {
      "steps": [{ "stepId": "source", "stepCode": "SOURCE" }],
      "fieldPolicies": [
        { "policyId": "all-denied", "stepId": "source", "stepCode": "*", "actorRole": "*", "fieldKey": "amount", "read": false, "write": false, "locked": true },
        { "policyId": "assignee-amount", "stepId": "source", "stepCode": "*", "actorRole": "ASSIGNEE", "fieldKey": "amount", "read": true, "write": true, "required": true, "locked": false },
        { "policyId": "assignee-secret", "stepId": "source", "stepCode": "*", "actorRole": "ASSIGNEE", "fieldKey": "secret", "read": true, "write": true, "required": true, "hidden": true },
        { "policyId": "review-score", "stepId": "source", "stepCode": "*", "actorRole": "REVIEWER", "fieldKey": "score", "read": true, "write": true, "required": true, "lockedAfterSubmit": true }
      ],
      "tableColumnPolicies": [
        { "policyId": "assignee-amount-col", "stepId": "source", "stepCode": "*", "actorRole": "ASSIGNEE", "blockId": "b1", "columnKey": "amount", "read": true, "write": true, "required": true },
        { "policyId": "assignee-secret-col", "stepId": "source", "stepCode": "*", "actorRole": "ASSIGNEE", "blockId": "b1", "columnKey": "secret", "read": true, "write": true, "required": true, "hidden": true },
        { "policyId": "review-score-col", "stepId": "source", "stepCode": "*", "actorRole": "REVIEWER", "blockId": "b1", "columnKey": "score", "read": true, "write": true, "required": true, "lockedAfterSubmit": true }
      ]
    }
    """;

    private static readonly IReadOnlyList<ContractCase> Cases =
    [
        new("MAP-AUTH-01", "Server derives assignee, issuer, and coordinator roles from canonical report state", ServerDerivedRoles),
        new("MAP-AUTH-02", "An outsider has no implicit ASSIGNEE fallback", OutsiderHasNoRole),
        new("MAP-AUTH-03", "Missing policy coverage denies fields and tables by default", MissingPolicyFailsClosed),
        new("MAP-AUTH-04", "Exact server role wins over a wildcard policy at higher specificity", ExactRoleSpecificity),
        new("MAP-AUTH-05", "Hidden source and target fields are unreadable, unwritable, and non-required", HiddenFieldFailsClosed),
        new("MAP-AUTH-06", "lockedAfterSubmit makes a field readonly after lifecycle submit", LockedFieldAfterSubmit),
        new("MAP-AUTH-07", "Hidden table columns are unreadable, unwritable, and non-required", HiddenTableColumnFailsClosed),
        new("MAP-AUTH-08", "lockedAfterSubmit makes a table column readonly after lifecycle submit", LockedTableColumnAfterSubmit),
        new("MAP-AUTH-09", "Caller preview redaction removes hidden target values and all raw source values", PreviewValuesAreRedacted),
        new("MAP-AUTH-10", "Required validation runs against the effective mapped draft and accepts false/zero", RequiredEffectiveDraft),
        new("MAP-AUTH-11", "Mapping-purpose access does not expose raw source identities or revision metadata", RawSourceIdentityIsRedacted),
        new("MAP-AUTH-12", "Caller-owned source, rule, conflict, and contribution selectors are rejected", ForgedCallerConfigurationRejected)
    ];

    public static IReadOnlyDictionary<string, string> SemanticRegistry { get; } =
        Cases.ToDictionary(item => item.Id, item => item.Semantic, StringComparer.Ordinal);

    public static void Run()
    {
        Require(Cases.Count == 12, "MAP-AUTH registry must contain 12 cases");
        Require(SemanticRegistry.Count == 12, "MAP-AUTH ids must be unique");
        for (var number = 1; number <= 12; number++)
            Require(SemanticRegistry.ContainsKey($"MAP-AUTH-{number:00}"), $"missing MAP-AUTH-{number:00}");

        foreach (var contractCase in Cases)
        {
            try
            {
                contractCase.Run();
                Console.WriteLine($"PASS {contractCase.Id} {contractCase.Semantic}");
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"{contractCase.Id} ({contractCase.Semantic}) failed: {error.Message}",
                    error);
            }
        }
    }

    private static void ServerDerivedRoles()
    {
        var assignment = Assignment();
        var report = Report();
        Require(
            WorkAssignmentReportService.ResolveDirectDynamicFlowActorRole(
                assignment,
                report,
                "assignee") == "ASSIGNEE",
            "report assignee role");
        Require(
            WorkAssignmentReportService.ResolveDirectDynamicFlowActorRole(
                assignment,
                report,
                "issuer") == "ISSUER",
            "issuer role");
        Require(
            WorkAssignmentReportService.ResolveDirectDynamicFlowActorRole(
                assignment,
                report,
                "coordinator") == "COORDINATOR",
            "coordinator role");
    }

    private static void OutsiderHasNoRole()
    {
        Require(
            WorkAssignmentReportService.ResolveDirectDynamicFlowActorRole(
                Assignment(),
                Report(),
                "outsider") is null,
            "outsider must not receive a role");
    }

    private static void MissingPolicyFailsClosed()
    {
        var permissions = new DynamicFlowPolicyEvaluator().Evaluate(
            """
            {
              "steps": [{ "stepId": "source", "stepCode": "SOURCE" }],
              "fieldPolicies": [],
              "tableColumnPolicies": []
            }
            """,
            Context("ASSIGNEE"));
        Require(permissions.DenyAllFields, "missing field policy must deny");
        Require(permissions.DenyAllTableColumns, "missing table policy must deny");
        var violations = DynamicFlowReportPermissionEnforcer.FindWriteViolations(
            permissions,
            """{"values":{"amount":1}}""",
            """{"values":{"amount":2}}""",
            """{"blocks":[{"blockId":"b1","rows":[{"rowKey":"r1","amount":1}]}]}""",
            """{"blocks":[{"blockId":"b1","rows":[{"rowKey":"r1","amount":2}]}]}""");
        Require(
            violations.Any(item => item.TargetKind == "FIELD") &&
            violations.Any(item => item.TargetKind == "TABLE_COLUMN"),
            "default deny must block both target kinds");

        var partial = new DynamicFlowPolicyEvaluator().Evaluate(
            """
            {
              "steps": [{ "stepId": "source", "stepCode": "SOURCE" }],
              "fieldPolicies": [
                { "policyId": "allowed-field", "stepId": "source", "stepCode": "*", "actorRole": "ASSIGNEE", "fieldKey": "allowed", "read": true, "write": true }
              ],
              "tableColumnPolicies": [
                { "policyId": "allowed-column", "stepId": "source", "stepCode": "*", "actorRole": "ASSIGNEE", "blockId": "b1", "columnKey": "allowed", "read": true, "write": true }
              ]
            }
            """,
            Context("ASSIGNEE"));
        var partialViolations =
            DynamicFlowReportPermissionEnforcer.FindWriteViolations(
                partial,
                """{"values":{"allowed":1,"unconfigured":1}}""",
                """{"values":{"allowed":2,"unconfigured":2}}""",
                """{"blocks":[{"blockId":"b1","rows":[{"rowKey":"r1","allowed":1,"unconfigured":1}]}]}""",
                """{"blocks":[{"blockId":"b1","rows":[{"rowKey":"r1","allowed":2,"unconfigured":2}]}]}""");
        Require(
            partialViolations.Count(item =>
                item.TargetKind == "FIELD" &&
                item.TargetKey == "unconfigured") == 1,
            "partial field coverage must deny an unconfigured changed key");
        Require(
            partialViolations.Count(item =>
                item.TargetKind == "TABLE_COLUMN" &&
                item.TargetKey == "b1:unconfigured") == 1,
            "partial table coverage must deny an unconfigured changed column");
        Require(
            partialViolations.All(item =>
                item.TargetKey.Contains("unconfigured", StringComparison.Ordinal)),
            "explicitly allowed partial-policy targets must remain writable");

        var readable =
            DynamicFlowReportPermissionEnforcer.ApplyReadRestrictions(
                partial,
                null,
                """{"values":{"allowed":"visible","unconfigured":"field-secret"}}""",
                """{"blocks":[{"blockId":"b1","rows":[{"rowKey":"r1","allowed":"visible","unconfigured":"table-secret"}]}]}""",
                null);
        var readableJson =
            $"{readable.FieldValuesJson}\n{readable.TableValuesJson}";
        Require(
            readableJson.Contains("visible", StringComparison.Ordinal) &&
            !readableJson.Contains("field-secret", StringComparison.Ordinal) &&
            !readableJson.Contains("table-secret", StringComparison.Ordinal),
            "partial policy reads must redact every unconfigured field and table column");
    }

    private static void ExactRoleSpecificity()
    {
        var evaluator = new DynamicFlowPolicyEvaluator();
        var assignee = evaluator.Evaluate(PolicyPayload, Context("ASSIGNEE"));
        var reviewer = evaluator.Evaluate(PolicyPayload, Context("REVIEWER"));
        Require(assignee.Fields["amount"].Write, "assignee-specific rule must allow");
        Require(
            assignee.Fields["amount"].SourcePolicyId == "assignee-amount",
            "specific rule provenance");
        Require(!reviewer.Fields["amount"].Write, "wildcard deny must remain for reviewer");
    }

    private static void HiddenFieldFailsClosed()
    {
        var permission = new DynamicFlowPolicyEvaluator()
            .Evaluate(PolicyPayload, Context("ASSIGNEE"))
            .Fields["secret"];
        Require(permission.Hidden, "field hidden flag");
        Require(!permission.Read && !permission.Write && !permission.Required, "hidden field effective flags");
    }

    private static void LockedFieldAfterSubmit()
    {
        var evaluator = new DynamicFlowPolicyEvaluator();
        var beforeSubmit = evaluator
            .Evaluate(PolicyPayload, Context("REVIEWER"))
            .Fields["score"];
        Require(
            beforeSubmit.Write &&
            !beforeSubmit.Locked &&
            beforeSubmit.LockedAfterSubmit,
            "lockedAfterSubmit field must remain writable on a never-submitted draft");
        var permission = evaluator
            .Evaluate(PolicyPayload, Context("REVIEWER", isAfterSubmit: true))
            .Fields["score"];
        Require(permission.Read, "locked field remains readable");
        Require(permission.Locked && permission.LockedAfterSubmit, "field lock flags");
        Require(!permission.Write, "locked field must be readonly");
    }

    private static void HiddenTableColumnFailsClosed()
    {
        var permission = new DynamicFlowPolicyEvaluator()
            .Evaluate(PolicyPayload, Context("ASSIGNEE"))
            .TableColumns["b1:secret"];
        Require(permission.Hidden, "column hidden flag");
        Require(!permission.Read && !permission.Write && !permission.Required, "hidden column effective flags");
    }

    private static void LockedTableColumnAfterSubmit()
    {
        var evaluator = new DynamicFlowPolicyEvaluator();
        var beforeSubmit = evaluator
            .Evaluate(PolicyPayload, Context("REVIEWER"))
            .TableColumns["b1:score"];
        Require(
            beforeSubmit.Write &&
            !beforeSubmit.Locked &&
            beforeSubmit.LockedAfterSubmit,
            "lockedAfterSubmit column must remain writable on a never-submitted draft");
        var permission = evaluator
            .Evaluate(PolicyPayload, Context("REVIEWER", isAfterSubmit: true))
            .TableColumns["b1:score"];
        Require(permission.Read, "locked column remains readable");
        Require(permission.Locked && permission.LockedAfterSubmit, "column lock flags");
        Require(!permission.Write, "locked column must be readonly");
    }

    private static void PreviewValuesAreRedacted()
    {
        const string forbidden = "P7_FORBIDDEN_RAW_VALUE_9B85";
        var permissions = new DynamicFlowPolicyEvaluationResult();
        permissions.Fields["secret"] = new DynamicFlowFieldPermissionDto
        {
            TargetKey = "secret",
            FieldKey = "secret",
            Hidden = true
        };
        permissions.Fields["public"] = new DynamicFlowFieldPermissionDto
        {
            TargetKey = "public",
            FieldKey = "public",
            Read = true
        };
        var preview = new DynamicFlowMappingPreviewResponse
        {
            FieldValuesJson = JsonSerializer.Serialize(new
            {
                values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["secret"] = forbidden,
                    ["public"] = "visible"
                }
            }),
            SummarySourceJson = JsonSerializer.Serialize(new
            {
                kind = "DYNAMIC_FLOW_MAPPING",
                changes = new[]
                {
                    new
                    {
                        sources = new[]
                        {
                            new { valueJson = forbidden }
                        }
                    }
                }
            }),
            Changes =
            [
                new DynamicFlowMappingChangeDto
                {
                    MappingId = "hidden",
                    TargetKind = "FIELD",
                    TargetKey = "secret",
                    NextValueJson = JsonSerializer.Serialize(forbidden),
                    Sources =
                    [
                        new DynamicFlowMappingInputProvenanceDto
                        {
                            ValueJson = JsonSerializer.Serialize(forbidden)
                        }
                    ]
                },
                new DynamicFlowMappingChangeDto
                {
                    MappingId = "visible",
                    TargetKind = "FIELD",
                    TargetKey = "public",
                    NextValueJson = "\"visible\"",
                    Sources =
                    [
                        new DynamicFlowMappingInputProvenanceDto
                        {
                            ValueJson = JsonSerializer.Serialize(forbidden)
                        }
                    ]
                }
            ]
        };

        var redacted =
            DynamicFlowReportPermissionEnforcer.ApplyMappingPreviewReadRestrictions(
                permissions,
                preview);
        var serialized = JsonSerializer.Serialize(redacted);
        Require(redacted.Changes.Count == 1, "hidden target change must be omitted");
        Require(redacted.Changes[0].Sources[0].ValueJson is null, "source value must be null");
        Require(!serialized.Contains(forbidden, StringComparison.Ordinal), "caller payload must not contain raw value");
    }

    private static void RequiredEffectiveDraft()
    {
        var permissions = new DynamicFlowPolicyEvaluationResult();
        permissions.Fields["confirmed"] = new DynamicFlowFieldPermissionDto
        {
            TargetKey = "confirmed",
            FieldKey = "confirmed",
            Read = false,
            Required = true
        };
        permissions.TableColumns["b1:amount"] = new DynamicFlowTableColumnPermissionDto
        {
            TargetKey = "b1:amount",
            BlockId = "b1",
            ColumnKey = "amount",
            Read = false,
            Required = true
        };
        var violations = DynamicFlowReportPermissionEnforcer.FindRequiredViolations(
            permissions,
            """{"values":{"confirmed":false}}""",
            """{"blocks":[{"blockId":"B1","tableMode":"APPEND_ROWS","rows":[{"rowKey":"r1","amount":0}]}]}""");
        Require(
            violations.Count == 0,
            "false and zero satisfy required values even when read is denied");

        var missing = DynamicFlowReportPermissionEnforcer.FindRequiredViolations(
            permissions,
            """{"values":{"confirmed":""}}""",
            """{"blocks":[{"blockId":"b1","tableMode":"APPEND_ROWS","rows":[{"rowKey":"r1","amount":null}]}]}""");
        Require(
            missing.Any(item =>
                item.TargetKind == "FIELD" &&
                item.Reason == "DYNAMIC_FLOW_FIELD_REQUIRED") &&
            missing.Any(item =>
                item.TargetKind == "TABLE_COLUMN" &&
                item.Reason == "DYNAMIC_FLOW_TABLE_COLUMN_REQUIRED"),
            "required validation must reject blank effective field and table values");
    }

    private static void RawSourceIdentityIsRedacted()
    {
        const string reportId = "507f1f77bcf86cd799439011";
        const string forbidden = "P7_FORBIDDEN_SOURCE_VALUE_A24C";
        var internalSourceFacts = new
        {
            ReportId = reportId,
            RawValue = forbidden,
            PayloadHash = new string('a', 64)
        };
        var preview = new DynamicFlowMappingPreviewResponse
        {
            SummarySourceJson = JsonSerializer.Serialize(new { value = forbidden }),
            SourceReports =
            [
                new DynamicFlowMappingSourceReportDto
                {
                    ReportId = reportId,
                    WorkAssignmentId = "507f1f77bcf86cd799439012",
                    FlowInstanceId = "507f1f77bcf86cd799439013",
                    ExecutionEpoch = 4,
                    StepInstanceId = "507f1f77bcf86cd799439014",
                    BranchId = forbidden,
                    AttemptNo = 2,
                    FlowStepId = forbidden,
                    FlowStepCode = forbidden,
                    DynamicFormTemplateId = forbidden,
                    FormFamilyId = forbidden,
                    FormVersionId = forbidden,
                    FormVersionNo = 3,
                    FormSchemaHash = new string('b', 64),
                    PayloadRevision = 8,
                    PayloadHash = new string('a', 64),
                    LifecycleRevision = 3,
                    LifecycleStatus = forbidden,
                    PeriodInstanceKey = forbidden
                }
            ],
            Changes =
            [
                new DynamicFlowMappingChangeDto
                {
                    MappingId = "m1",
                    TargetKind = "FIELD",
                    TargetKey = "public",
                    SourceReportId = reportId,
                    SourceKey = forbidden,
                    Sources =
                    [
                        new DynamicFlowMappingInputProvenanceDto
                        {
                            InputKey = forbidden,
                            SourceDynamicFormTemplateId = forbidden,
                            SourceStepId = forbidden,
                            SourceStepCode = forbidden,
                            SourceReportId = reportId,
                            SourceAssignmentId = "507f1f77bcf86cd799439012",
                            SourcePayloadRevision = 8,
                            SourcePayloadHash = new string('a', 64),
                            SourceLifecycleRevision = 3,
                            SourceKey = forbidden,
                            RowKey = forbidden,
                            ValueJson = JsonSerializer.Serialize(forbidden)
                        }
                    ]
                }
            ]
        };

        DynamicFlowMappingCallerRedaction.RedactSourceIdentities(
            preview,
            new HashSet<string>(StringComparer.Ordinal) { reportId });
        var serialized = JsonSerializer.Serialize(preview);
        var callerSource = preview.SourceReports.Single();
        Require(callerSource.IdentityRedacted, "identity redaction marker");
        foreach (var property in typeof(DynamicFlowMappingSourceReportDto)
                     .GetProperties()
                     .Where(property =>
                         property.Name !=
                         nameof(DynamicFlowMappingSourceReportDto.IdentityRedacted)))
        {
            Require(
                property.GetValue(callerSource) is null,
                $"source report caller field {property.Name}");
        }

        var callerChange = preview.Changes.Single();
        Require(callerChange.SourceReportId is null, "change source report id");
        Require(callerChange.SourceKey is null, "change source key");
        var callerProvenance = callerChange.Sources.Single();
        foreach (var property in typeof(DynamicFlowMappingInputProvenanceDto)
                     .GetProperties())
        {
            Require(
                property.GetValue(callerProvenance) is null,
                $"provenance caller field {property.Name}");
        }

        Require(preview.SummarySourceJson is null, "summary source provenance");
        Require(!serialized.Contains(forbidden, StringComparison.Ordinal), "redacted caller payload");
        Require(!serialized.Contains(reportId, StringComparison.Ordinal), "redacted identity payload");
        Require(
            internalSourceFacts.ReportId == reportId &&
            internalSourceFacts.RawValue == forbidden &&
            internalSourceFacts.PayloadHash == new string('a', 64),
            "caller redaction must not mutate server-owned source facts");
    }

    private static void ForgedCallerConfigurationRejected()
    {
        var forgedInputs =
            new (string Field, Func<DynamicFlowMappingRequest> Request)[]
            {
                ("actorRole", () => new() { ActorRole = "COORDINATOR" }),
                ("provenance", () => new()
                {
                    Provenance = JsonSerializer.SerializeToNode(
                        new { sourceReportId = "forged" })
                }),
                ("changes", () => new() { Changes = [] }),
                ("sourceReports", () => new() { SourceReports = [] }),
                ("sourceMode", () => new() { SourceMode = string.Empty }),
                ("sourceReportIds", () => new() { SourceReportIds = [] }),
                ("flowTemplateVersionId", () => new()
                {
                    FlowTemplateVersionId = string.Empty
                }),
                ("flowTemplateId", () => new() { FlowTemplateId = string.Empty }),
                ("flowTemplateVersionNo", () => new() { FlowTemplateVersionNo = 1 }),
                ("mappingRulesJson", () => new() { MappingRulesJson = string.Empty }),
                ("mappingRules", () => new() { MappingRules = [] }),
                ("conflictPolicy", () => new() { ConflictPolicy = string.Empty }),
                ("contributionPolicy", () => new()
                {
                    ContributionPolicy = string.Empty
                }),
                ("requireSourceReport", () => new() { RequireSourceReport = false }),
                ("change", () =>
                    JsonSerializer.Deserialize<DynamicFlowMappingRequest>(
                        """{"change":{"sourceReportId":"forged"}}""")
                    ?? throw new InvalidOperationException(
                        "forged change request did not deserialize")),
                ("forgedFutureSelector", () =>
                    JsonSerializer.Deserialize<DynamicFlowMappingRequest>(
                        """{"forgedFutureSelector":true}""")
                    ?? throw new InvalidOperationException(
                        "unknown forged selector did not deserialize"))
            };
        foreach (var forged in forgedInputs)
        {
            try
            {
                DynamicFlowMappingSecurityContract.ValidateFlowOwnedRequestInputs(
                    forged.Request());
                throw new InvalidOperationException(
                    $"forged caller field {forged.Field} was accepted");
            }
            catch (DynamicFlowMappingSecurityException error)
            {
                Require(
                    error.Reason ==
                    DynamicFlowMappingSecurityContract
                        .FlowOwnedConfigurationReason,
                    $"stable reason for {forged.Field}");
                Require(
                    error.Field == forged.Field,
                    $"stable field for {forged.Field}; actual={error.Field}");
            }
        }

        DynamicFlowMappingSecurityContract.ValidateFlowOwnedRequestInputs(
            new DynamicFlowMappingRequest
            {
                FlowVersionId = "caller-assertion",
                FlowPayloadHash = new string('a', 64),
                ExpectedPayloadRevision = 1,
                ExpectedLifecycleRevision = 0
            });

        var method = typeof(WorkAssignmentReportService).GetMethod(
            "EnsureDynamicFlowMappingRequestDoesNotOverrideConfig",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("mapping request authority guard missing");
        var request = new DynamicFlowMappingRequest
        {
            SourceReportIds = ["507f1f77bcf86cd799439011"],
            SourceMode = "CALLER_SELECTED",
            ConflictPolicy = "LAST_WRITE_WINS",
            ContributionPolicy = "INCLUDE",
            MappingRules = [new DynamicFlowMappingRuleDto { MappingId = "forged" }]
        };
        try
        {
            method.Invoke(null, [request]);
            throw new InvalidOperationException("forged mapping configuration was accepted");
        }
        catch (TargetInvocationException error)
            when (error.InnerException is AppException appError)
        {
            Require(
                appError.Code == AppErrorCode.COMMON_VALIDATION_FAILED,
                "authority guard must return a stable validation error");
            var details = JsonSerializer.Serialize(appError.Details);
            Require(
                details.Contains(
                    "DYNAMIC_FLOW_MAPPING_CONFIG_MUST_BE_FLOW_OWNED",
                    StringComparison.Ordinal),
                "authority guard reason");
        }
    }

    private static DynamicFlowPolicyEvaluationContext Context(
        string role,
        bool isAfterSubmit = false)
        => new()
        {
            StepId = "source",
            StepCode = "SOURCE",
            ActorRole = role,
            IsAfterSubmit = isAfterSubmit
        };

    private static WorkAssignment Assignment()
        => new()
        {
            Id = "assignment",
            CreatedByUserId = "issuer",
            FlowRole = "ASSIGNEE",
            LeaderWatcherUserIds = ["coordinator"],
            Assignees = []
        };

    private static WorkAssignmentReport Report()
        => new()
        {
            Id = "report",
            WorkAssignmentId = "assignment",
            AssigneeUserId = "assignee"
        };

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
