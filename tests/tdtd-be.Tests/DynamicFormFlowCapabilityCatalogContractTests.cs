using tdtd_be.Common.Capabilities;

internal static class DynamicFormFlowCapabilityCatalogContractTests
{
    private const string V16CatalogSha256 =
        "39cdb98dda168f5901f48a94640fe5d50943c5bd78ed32d5e05e8b719b23d13b";
    private const string V16SchemaSha256 =
        "da0c80f265845f24aaf282e0a0369272273b1986520dae07171cda85b28b3fed";
    private const string V17CatalogSha256 =
        "ccb28afafc068ac1b720c046a25276a35d9d828b14f9cc9c9bc690077ca204c1";
    private const string V17SchemaSha256 =
        "5842baf176bf1eec453b718d07e50da55a417aa9036f4f097ccfbb6fc58c1978";

    public static void Run()
    {
        var expectedIdentity = DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion switch
        {
            "1.6" => (Catalog: V16CatalogSha256, Schema: V16SchemaSha256, ApprovedAt: "2026-08-04"),
            "1.7" => (Catalog: V17CatalogSha256, Schema: V17SchemaSha256, ApprovedAt: "2026-08-10"),
            var version => throw new InvalidOperationException($"Unknown catalog version '{version}'."),
        };
        AssertEqual(expectedIdentity.Catalog, DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256, "catalog SHA-256");
        AssertEqual(expectedIdentity.Schema, DynamicFormFlowCapabilityCatalogMetadata.SchemaSha256, "schema SHA-256");
        AssertEqual(expectedIdentity.ApprovedAt, DynamicFormFlowCapabilityCatalogMetadata.ApprovedAt, "catalog approval date");
        AssertEqual("BPMN_LITE_12_ARCHETYPES", DynamicFormFlowCapabilityCatalogMetadata.FlowScopeName, "flow scope");
        AssertFalse(DynamicFormFlowCapabilityCatalogMetadata.FullBpmnClaimAllowed, "full BPMN claim must remain disabled");
        AssertTrue(DynamicFormFlowCapabilityCatalogMetadata.UiGateContinuous, "UI gate must remain continuous");

        var defaults = DynamicFormFlowCapabilityCatalogMetadata.Defaults;
        AssertFalse(defaults.PublishedFormSchemaMutable, "published form schemas must remain immutable");
        AssertTrue(defaults.AssignmentBindsFormVersionId, "assignments must bind immutable form versions");
        AssertEqual("DENY", defaults.EmptyFlowPolicy, "empty flow policy");
        AssertEqual("SERVER_DERIVED", defaults.RuntimeRoleSource, "runtime role source");
        AssertEqual("EXPLICIT_PERMISSION_ONLY", defaults.RawSourceReportAccess, "raw source report access");
        AssertEqual("EXCLUDE", defaults.MappedTargetStatisticContribution, "mapped target statistic contribution");
        AssertEqual("INTENTIONAL_BLOCK", defaults.FlowStatisticProfile, "flow statistic profile default");

        AssertIds(
            DynamicFormFlowCapabilityCatalogMetadata.DynamicFormFieldTypes,
            "shortText", "longText", "richText", "stringList", "number", "date", "fullDate", "singleSelect", "multiSelect", "boolean");
        AssertIds(
            DynamicFormFlowCapabilityCatalogMetadata.DynamicFormValueSources,
            "FIXED_ENUM", "ENUM_CATALOG", "SYSTEM_UNIT", "SYSTEM_USER", "SYSTEM_POSITION", "SYSTEM_UNIT_TYPE");
        AssertIds(
            DynamicFormFlowCapabilityCatalogMetadata.DynamicFormTableModes,
            "FIXED_GRID", "APPEND_ROWS", "APPEND_COLUMNS", "MATRIX", "SUMMARY_TEMPLATE");
        AssertAllSupported(DynamicFormFlowCapabilityCatalogMetadata.DynamicFormFieldTypes, "P3 field types");
        AssertAllSupported(DynamicFormFlowCapabilityCatalogMetadata.DynamicFormValueSources, "P3 value sources");
        AssertAllSupported(DynamicFormFlowCapabilityCatalogMetadata.DynamicFormTableModes, "P3 table modes");
        AssertIds(
            DynamicFormFlowCapabilityCatalogMetadata.DynamicFlowArchetypes,
            "FLOW-T01", "FLOW-T02", "FLOW-T03", "FLOW-T04", "FLOW-T05", "FLOW-T06",
            "FLOW-T07", "FLOW-T08", "FLOW-T09", "FLOW-T10", "FLOW-T11", "FLOW-T12");
        AssertCapability(
            DynamicFormFlowCapabilityCatalogMetadata.DynamicFlowArchetypes,
            "FLOW-T01", "SUPPORTED", "P5");
        AssertCapability(
            DynamicFormFlowCapabilityCatalogMetadata.DynamicFlowArchetypes,
            "FLOW-T02", "SUPPORTED", "P5");
        for (var number = 3; number <= 12; number++)
        {
            AssertCapability(
                DynamicFormFlowCapabilityCatalogMetadata.DynamicFlowArchetypes,
                $"FLOW-T{number:00}", "SUPPORTED", "P6");
        }
        AssertIds(
            DynamicFormFlowCapabilityCatalogMetadata.StatisticsCapabilities,
            "DIRECT_FIELD_TABLE_LABEL", "BASIC_SUMMARY", "ADVANCED_SUMMARY", "DIFF", "FLOW_SCOPES", "FLOW_STATISTIC_PROFILE");
        AssertIds(
            DynamicFormFlowCapabilityCatalogMetadata.DynamicFlowMappingCapabilities,
            "FIELD_TYPED", "FIXED_GRID", "APPEND_ROWS", "MATRIX_SPARSE",
            "SOURCE_REPORT_GRAIN", "GROUP_GRAIN", "CUSTOM_JOIN_KEY",
            "APPEND_COLUMNS_TARGET", "SCALAR_TO_ROW", "ROW_TO_REPORT");
        foreach (var id in new[]
        {
            "FIELD_TYPED", "FIXED_GRID", "APPEND_ROWS", "MATRIX_SPARSE",
            "SOURCE_REPORT_GRAIN"
        })
        {
            AssertCapability(
                DynamicFormFlowCapabilityCatalogMetadata.DynamicFlowMappingCapabilities,
                id, "SUPPORTED", "P7");
        }
        foreach (var id in new[]
        {
            "GROUP_GRAIN", "CUSTOM_JOIN_KEY", "APPEND_COLUMNS_TARGET",
            "SCALAR_TO_ROW", "ROW_TO_REPORT"
        })
        {
            AssertCapability(
                DynamicFormFlowCapabilityCatalogMetadata.DynamicFlowMappingCapabilities,
                id, "INTENTIONAL_BLOCK", null);
        }

        var expectedP8Ids = new[]
        {
            "LABEL_TAXONOMY_CONFIG", "FIELD_METADATA_CONFIG", "TABLE_METADATA_CONFIG",
            "BASIC_SUMMARY_CONFIG", "FLOW_SCOPE_CONFIG", "ADVANCED_SUMMARY_CONFIG",
            "DIFF_CONFIG", "FLOW_CONTRIBUTION_CONFIG", "FLOW_STATISTIC_PROFILE_BARRIER",
            "CONFIG_OPERATIONS_READINESS", "CONFIG_BUNDLE_READBACK"
        };
        AssertEqual(
            string.Join("|", expectedP8Ids),
            string.Join("|", DynamicFormFlowCapabilityCatalogMetadata.StatisticsConfigurationCapabilities.Select(item => item.Id)),
            "P8 configuration capability IDs");
        AssertTrue(
            DynamicFormFlowCapabilityCatalogMetadata.StatisticsConfigurationCapabilities.All(
                item => item.Status == "SUPPORTED" && item.TargetPhase == "P8" && item.Notes.Length > 0),
            "all P8 configuration capabilities must be SUPPORTED/P8 with notes");

        var reconciliation =
            DynamicFormFlowCapabilityCatalogMetadata.StatisticsReconciliationCapabilities;
        if (DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion == "1.6")
        {
            AssertEqual(0, reconciliation.Count, "v1.6 P10 reconciliation capability count");
        }
        else
        {
            AssertIds(
                reconciliation,
                "SOURCE_TO_RESULT_RECONCILIATION",
                "EXPECTED_ACTUAL_DELTA",
                "INDEPENDENT_REVIEW_SIGNOFF",
                "RECONCILIATION_EVIDENCE_EXPORT");
            var expectedP10 = new[]
            {
                ("SOURCE_TO_RESULT_RECONCILIATION", "Source-to-result statistics reconciliation", "P10-RECONCILE", "STAT_RECONCILIATION"),
                ("EXPECTED_ACTUAL_DELTA", "Expected-versus-actual typed delta", "P10-DELTA", "STAT_RECONCILIATION"),
                ("INDEPENDENT_REVIEW_SIGNOFF", "Independent reconciliation review sign-off", "P10-REVIEW", "STAT_RECONCILIATION_REVIEW"),
                ("RECONCILIATION_EVIDENCE_EXPORT", "Deterministic reconciliation evidence export", "P10-EVIDENCE", "STAT_RECONCILIATION_EVIDENCE"),
            };
            var actualP10 = reconciliation
                .Select(item => (item.Id, item.Name, item.TestPrefix, item.UiSurface))
                .ToArray();
            if (!actualP10.SequenceEqual(expectedP10))
            {
                throw new InvalidOperationException("P10 reconciliation capability metadata drifted.");
            }
            AssertTrue(
                reconciliation.All(item =>
                    item.Status == "SUPPORTED" &&
                    item.TargetPhase == "P10" &&
                    item.Notes is null),
                "all P10 reconciliation capabilities must be SUPPORTED/P10 without notes");
        }

        var profile = DynamicFormFlowCapabilityCatalogMetadata.StatisticsCapabilities.Single(
            capability => capability.Id == "FLOW_STATISTIC_PROFILE");
        AssertEqual("INTENTIONAL_BLOCK", profile.Status, "flow statistic profile status");
        AssertEqual<string?>(null, profile.TargetPhase, "flow statistic profile target phase");
    }

    private static void AssertIds(
        IReadOnlyList<DynamicFormFlowCapabilityItemMetadata> actual,
        params string[] expected)
    {
        var actualIds = actual.Select(capability => capability.Id).ToArray();
        if (!actualIds.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"Capability ids differ. Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actualIds)}].");
        }
    }

    private static void AssertAllSupported(
        IReadOnlyList<DynamicFormFlowCapabilityItemMetadata> capabilities,
        string label)
    {
        var unsupported = capabilities
            .Where(capability => capability.Status != "SUPPORTED")
            .Select(capability => $"{capability.Id}:{capability.Status}")
            .ToArray();
        if (unsupported.Length > 0)
            throw new InvalidOperationException($"{label} are not all SUPPORTED: {string.Join(", ", unsupported)}.");
    }

    private static void AssertCapability(
        IReadOnlyList<DynamicFormFlowCapabilityItemMetadata> capabilities,
        string id,
        string status,
        string? targetPhase)
    {
        var capability = capabilities.Single(item => item.Id == id);
        AssertEqual(status, capability.Status, $"{id} status");
        AssertEqual(targetPhase, capability.TargetPhase, $"{id} target phase");
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool condition, string message)
    {
        if (condition) throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}. Expected {expected}, got {actual}.");
    }
}
