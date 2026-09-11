namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal static class
    StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity
{
    internal static StatisticReconciliationExpectedAuthoritativeRuntimePin Create(
        string runtimeKind,
        string? flowTemplateVersionId,
        string? flowPayloadSha256,
        string? flowInstanceId,
        string? flowBranchId,
        string? flowStepId,
        string? flowStepInstanceId,
        long? flowStepRevision,
        int? flowAttemptNo,
        string? executionEpochId,
        int? executionEpoch,
        string? currentExecutionEpochId,
        int? currentExecutionEpoch,
        long? executionEpochRevision,
        bool? isCanonicalEpoch,
        string? approvalCommandId,
        string? approvalEventKey,
        string? mappingReceiptId,
        string? mappingProvenanceId,
        string? mappingProvenanceSha256,
        string? mappingResultSemanticSha256,
        int? mappingResultPayloadRevision,
        string? mappingResultPayloadSha256,
        string? mappingFlowVersionId,
        int? mappingFlowVersionNo,
        string? mappingFlowPayloadSha256,
        bool? mappingLocked,
        string? configVersionId,
        string? configSha256,
        string membershipSignatureSha256,
        string contributionPolicy,
        string contributionPolicySha256,
        string contributionProvenanceId,
        string contributionProvenanceSha256,
        string lifecycleOwnerSha256)
    {
        var value = new StatisticReconciliationExpectedAuthoritativeRuntimePin(
            runtimeKind,
            flowTemplateVersionId,
            flowPayloadSha256,
            flowInstanceId,
            flowBranchId,
            flowStepId,
            flowStepInstanceId,
            flowStepRevision,
            flowAttemptNo,
            executionEpochId,
            executionEpoch,
            currentExecutionEpochId,
            currentExecutionEpoch,
            executionEpochRevision,
            isCanonicalEpoch,
            approvalCommandId,
            approvalEventKey,
            mappingReceiptId,
            mappingProvenanceId,
            mappingProvenanceSha256,
            mappingResultSemanticSha256,
            mappingResultPayloadRevision,
            mappingResultPayloadSha256,
            mappingFlowVersionId,
            mappingFlowVersionNo,
            mappingFlowPayloadSha256,
            mappingLocked,
            configVersionId,
            configSha256,
            membershipSignatureSha256,
            contributionPolicy,
            contributionPolicySha256,
            contributionProvenanceId,
            contributionProvenanceSha256,
            lifecycleOwnerSha256,
            string.Empty);
        return value with { RuntimeSemanticSha256 = BuildSemanticSha256(value) };
    }

    internal static void Validate(
        StatisticReconciliationExpectedAuthoritativeRuntimePin value,
        ExpectedLedgerCompilationContextPin context)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(context);
        if (!StringComparer.Ordinal.Equals(value.RuntimeKind, context.RuntimeKind) ||
            value.ContributionPolicy is not (
                StatisticReconciliationExpectedContributionPolicies.Exclude or
                StatisticReconciliationExpectedContributionPolicies.Include) ||
            !Token(value.ConfigVersionId) ||
            !Sha(value.ConfigSha256) ||
            !Sha(value.MembershipSignatureSha256) ||
            !Sha(value.ContributionPolicySha256) ||
            !Token(value.ContributionProvenanceId) ||
            !Sha(value.ContributionProvenanceSha256) ||
            !Sha(value.LifecycleOwnerSha256) ||
            ((value.ApprovalCommandId is null) !=
             (value.ApprovalEventKey is null)) ||
            (value.ApprovalCommandId is not null &&
             (!Token(value.ApprovalCommandId) || !Sha(value.ApprovalEventKey))))
            throw Invalid();

        var mappingValues = new object?[]
        {
            value.MappingReceiptId,
            value.MappingProvenanceId,
            value.MappingProvenanceSha256,
            value.MappingResultSemanticSha256,
            value.MappingResultPayloadRevision,
            value.MappingResultPayloadSha256,
            value.MappingFlowVersionId,
            value.MappingFlowVersionNo,
            value.MappingFlowPayloadSha256,
            value.MappingLocked
        };
        var mappingAny = mappingValues.Any(item => item is not null);
        var mappingAll = Token(value.MappingReceiptId) &&
            Token(value.MappingProvenanceId) &&
            Sha(value.MappingProvenanceSha256) &&
            Sha(value.MappingResultSemanticSha256) &&
            value.MappingResultPayloadRevision is > 0 &&
            Sha(value.MappingResultPayloadSha256) &&
            Token(value.MappingFlowVersionId) &&
            value.MappingFlowVersionNo is > 0 &&
            Sha(value.MappingFlowPayloadSha256) &&
            value.MappingLocked == true;
        if (mappingAny != mappingAll ||
            (value.RuntimeKind ==
                 StatisticReconciliationExpectedRuntimeKinds.Flow &&
             value.ContributionPolicy ==
                 StatisticReconciliationExpectedContributionPolicies.Include &&
             !mappingAll))
            throw Invalid();

        if (value.RuntimeKind ==
            StatisticReconciliationExpectedRuntimeKinds.NonFlow)
        {
            if (new object?[]
                {
                    value.FlowTemplateVersionId,
                    value.FlowPayloadSha256,
                    value.FlowInstanceId,
                    value.FlowBranchId,
                    value.FlowStepId,
                    value.FlowStepInstanceId,
                    value.FlowStepRevision,
                    value.FlowAttemptNo,
                    value.ExecutionEpochId,
                    value.ExecutionEpoch,
                    value.CurrentExecutionEpochId,
                    value.CurrentExecutionEpoch,
                    value.ExecutionEpochRevision,
                    value.IsCanonicalEpoch
                }.Any(item => item is not null) || mappingAny)
                throw Invalid();
        }
        else if (value.RuntimeKind ==
                 StatisticReconciliationExpectedRuntimeKinds.Flow)
        {
            if (!StringComparer.Ordinal.Equals(
                    value.FlowTemplateVersionId,
                    context.FlowTemplateVersionId) ||
                !StringComparer.Ordinal.Equals(
                    value.FlowPayloadSha256,
                    context.FlowPayloadSha256) ||
                !StringComparer.Ordinal.Equals(
                    value.FlowInstanceId,
                    context.FlowInstanceId) ||
                !StringComparer.Ordinal.Equals(
                    value.ExecutionEpochId,
                    context.ExecutionEpochId) ||
                !Token(value.FlowBranchId) ||
                !Token(value.FlowStepId) ||
                !Token(value.FlowStepInstanceId) ||
                value.FlowStepRevision is not > 0 ||
                value.FlowAttemptNo is not > 0 ||
                value.ExecutionEpoch is not > 0 ||
                !Token(value.CurrentExecutionEpochId) ||
                value.CurrentExecutionEpoch is not > 0 ||
                value.ExecutionEpochRevision is not > 0 ||
                value.IsCanonicalEpoch != true ||
                !StringComparer.Ordinal.Equals(
                    value.ExecutionEpochId,
                    value.CurrentExecutionEpochId) ||
                value.ExecutionEpoch != value.CurrentExecutionEpoch ||
                !Sha(value.FlowPayloadSha256))
                throw Invalid();
        }
        else
            throw Invalid();

        if (!StringComparer.Ordinal.Equals(
                value.RuntimeSemanticSha256,
                BuildSemanticSha256(value)))
            throw Invalid();
    }

    internal static string BuildSemanticSha256(
        StatisticReconciliationExpectedAuthoritativeRuntimePin value)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            "P10_EXPECTED_AUTHORITATIVE_RUNTIME_PIN_V2",
            [value.RuntimeKind,
             S(value.FlowTemplateVersionId),
             S(value.FlowPayloadSha256),
             S(value.FlowInstanceId),
             S(value.FlowBranchId),
             S(value.FlowStepId),
             S(value.FlowStepInstanceId),
             L(value.FlowStepRevision),
             I(value.FlowAttemptNo),
             S(value.ExecutionEpochId),
             I(value.ExecutionEpoch),
             S(value.CurrentExecutionEpochId),
             I(value.CurrentExecutionEpoch),
             L(value.ExecutionEpochRevision),
             B(value.IsCanonicalEpoch),
             S(value.ApprovalCommandId),
             S(value.ApprovalEventKey),
             S(value.MappingReceiptId),
             S(value.MappingProvenanceId),
             S(value.MappingProvenanceSha256),
             S(value.MappingResultSemanticSha256),
             I(value.MappingResultPayloadRevision),
             S(value.MappingResultPayloadSha256),
             S(value.MappingFlowVersionId),
             I(value.MappingFlowVersionNo),
             S(value.MappingFlowPayloadSha256),
             B(value.MappingLocked),
             S(value.ConfigVersionId),
             S(value.ConfigSha256),
             value.MembershipSignatureSha256,
             value.ContributionPolicy,
             value.ContributionPolicySha256,
             value.ContributionProvenanceId,
             value.ContributionProvenanceSha256,
             value.LifecycleOwnerSha256]);

    private static string S(string? value) => value ?? "~";
    private static string I(int? value) => value?.ToString(
        System.Globalization.CultureInfo.InvariantCulture) ?? "~";
    private static string L(long? value) => value?.ToString(
        System.Globalization.CultureInfo.InvariantCulture) ?? "~";
    private static string B(bool? value) => value.HasValue
        ? value.Value ? "1" : "0"
        : "~";
    private static bool Token(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value == value.Trim() &&
        value.Length <= 512 &&
        !value.Any(char.IsControl);
    private static bool Sha(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or
            >= 'a' and <= 'f');
    private static StatisticReconciliationExpectedLedgerInputException Invalid()
        => new(
            StatisticReconciliationExpectedSourcePlanningFailureReasons
                .LifecycleCandidateInvalid,
            "$.authoritativeRuntime",
            "Integrity-bound authoritative runtime pin required.");
}