using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsConfiguration;
using tdtd_be.Services;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.WorkAssignments.BasicSummary;

namespace tdtd_be.Services.StatisticsConfiguration;

/// <summary>
/// Canonical, read-only P8 bundle projection. It resolves exact persisted
/// domain-owner identities and never persists a universal configuration row.
/// </summary>
public sealed class StatConfigBundleService : IStatConfigBundleService
{
    public const string SchemaVersion = "P8-BUNDLE-1";
    public const string Fresh = "FRESH";
    public const string EmptyValid = "EMPTY_VALID";

    private static readonly Regex TokenRegex = new(
        "^[A-Z][A-Z0-9_]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CommandIdRegex = new(
        "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Sha256Regex = new(
        "^[a-f0-9]{64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentBasicSummaryService? _basicConfigs;
    private readonly IWorkAssignmentAdvancedSummaryConfigService? _advancedConfigs;
    private readonly IWorkReportStatisticDiffService? _diffConfigs;

    public StatConfigBundleService(
        MongoDbContext ctx,
        IWorkAssignmentBasicSummaryService? basicConfigs = null,
        IWorkAssignmentAdvancedSummaryConfigService? advancedConfigs = null,
        IWorkReportStatisticDiffService? diffConfigs = null)
    {
        _ctx = ctx;
        _basicConfigs = basicConfigs;
        _advancedConfigs = advancedConfigs;
        _diffConfigs = diffConfigs;
    }

    public StatConfigBundleReadback ReadEmpty(
        string ownerKind,
        string ownerId)
    {
        var scope = NormalizeScope(ownerKind, ownerId);
        return BuildReadback(scope.OwnerKind, scope.OwnerId, []);
    }

    public async Task<StatConfigBundleReadback> ReadAsync(
        StatConfigBundleReadRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = NormalizeScope(request.OwnerKind, request.OwnerId);
        var references = NormalizeReferences(request.DependencyPins);
        if (references.Count == 0)
            return BuildReadback(scope.OwnerKind, scope.OwnerId, []);

        var pins = new List<StatConfigBundleDependencyPin>(
            StatConfigBundleDependencyKinds.Ordered.Count);
        foreach (var kind in StatConfigBundleDependencyKinds.Ordered)
        {
            var reference = references[kind];
            var pin = await ResolveAsync(reference, ct);
            EnsureRequestMatches(reference, pin);
            pins.Add(pin);
        }

        EnsureScopeMatches(scope.OwnerKind, scope.OwnerId, pins);
        await EnsureReadinessSourceMatchesAsync(pins, ct);
        EnsureNoMixedDependencies(pins);
        return BuildReadback(scope.OwnerKind, scope.OwnerId, pins);
    }

    public async Task<StatConfigBundleReadback> ValidateAsync(
        StatConfigBundleValidateRequest request,
        CancellationToken ct = default)
    {
        if (request is null)
            throw Schema("$", "BUNDLE_VALIDATE_REQUEST_REQUIRED");
        var commandId = request.CommandId?.Trim();
        if (commandId is null || !CommandIdRegex.IsMatch(commandId))
            throw Schema("$.commandId", "CANONICAL_COMMAND_ID_REQUIRED");
        var expectedHash = RequireHash(
            request.ExpectedBundleHash,
            "$.expectedBundleHash");
        if (request.Bundle is null)
            throw Schema("$.bundle", "BUNDLE_REQUIRED");

        var readback = await ReadAsync(request.Bundle, ct);
        if (!string.Equals(
                expectedHash,
                readback.BundleHash,
                StringComparison.Ordinal))
        {
            throw Stale(
                "BUNDLE_HASH_STALE",
                kind: null,
                expected: expectedHash,
                actual: readback.BundleHash);
        }
        return readback;
    }

    private async Task<StatConfigBundleDependencyPin> ResolveAsync(
        NormalizedReference reference,
        CancellationToken ct)
        => reference.Kind switch
        {
            StatConfigBundleDependencyKinds.Label =>
                await ResolveLabelAsync(reference, ct),
            StatConfigBundleDependencyKinds.Field =>
                await ResolveDynamicFormAsync(reference, isField: true, ct),
            StatConfigBundleDependencyKinds.Table =>
                await ResolveDynamicFormAsync(reference, isField: false, ct),
            StatConfigBundleDependencyKinds.Basic =>
                await ResolveBasicAsync(reference, ct),
            StatConfigBundleDependencyKinds.Advanced =>
                await ResolveAdvancedAsync(reference, ct),
            StatConfigBundleDependencyKinds.Diff =>
                await ResolveDiffAsync(reference, ct),
            StatConfigBundleDependencyKinds.FlowContribution =>
                await ResolveFlowContributionAsync(reference, ct),
            StatConfigBundleDependencyKinds.Readiness =>
                await ResolveReadinessAsync(reference, ct),
            _ => throw Schema("$.dependencyPins[].kind", "BUNDLE_KIND_INVALID")
        };

    private async Task<StatConfigBundleDependencyPin> ResolveLabelAsync(
        NormalizedReference reference,
        CancellationToken ct)
    {
        var owner = await _ctx.Labels
            .Find(item =>
                item.Id == reference.OwnerId &&
                item.IsActive &&
                !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (owner is null)
            throw Stale("BUNDLE_LABEL_NOT_CURRENT", reference.Kind);

        try
        {
            LabelConfigCommandService.ValidateTrustedConfigOwner(owner);
        }
        catch (AppException error)
        {
            throw Stale("BUNDLE_LABEL_INTEGRITY", reference.Kind, inner: error);
        }

        return Pin(
            reference.Kind,
            StatConfigOwnerKinds.Label,
            owner.Id,
            RequireObjectId(owner.ConfigId, "labels.configId"),
            RequireObjectId(owner.VersionId, "labels.versionId"),
            owner.VersionNo,
            owner.Revision,
            owner.IsActive ? StatConfigStatuses.Active : StatConfigStatuses.Inactive,
            RequireHash(owner.ConfigHash, "labels.configHash"),
            RequireHash(owner.ConfigHash, "labels.configHash"),
            CanonicalPins(owner.DependencyPins, "labels.dependencyPins"));
    }

    private async Task<StatConfigBundleDependencyPin> ResolveDynamicFormAsync(
        NormalizedReference reference,
        bool isField,
        CancellationToken ct)
    {
        var owner = await _ctx.DynamicFormTemplates
            .Find(item =>
                item.Id == reference.OwnerId &&
                item.IsActive &&
                item.IsPublished &&
                !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (owner is null)
            throw Stale("BUNDLE_DYNAMIC_FORM_NOT_CURRENT", reference.Kind);

        DynamicFormStatisticConfigCommandService.P804TrustedPersistedView? trusted;
        try
        {
            trusted = DynamicFormStatisticConfigCommandService
                .GetP804TrustedPersistedView(owner);
        }
        catch (Exception error) when (
            error is AppException or InvalidOperationException)
        {
            throw Stale(
                "BUNDLE_DYNAMIC_FORM_INTEGRITY",
                reference.Kind,
                inner: error);
        }
        if (trusted is null)
            throw Stale("BUNDLE_DYNAMIC_FORM_EMPTY", reference.Kind);

        var sectionJson = isField
            ? trusted.FieldSectionJson
            : trusted.TableSectionJson;
        var contributionHash = HashCanonicalJson(
            sectionJson,
            isField
                ? "dynamicForm.fieldSectionJson"
                : "dynamicForm.tableSectionJson");
        var formDependencyPins = trusted.DependencyPins.Concat(
        [
            $"DYNAMIC_FORM_SCHEMA:{owner.Id}:{owner.VersionNo}:" +
            RequireHash(
                owner.PublishedSchemaHash,
                "dynamicForm.publishedSchemaHash")
        ])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return Pin(
            reference.Kind,
            StatConfigOwnerKinds.DynamicForm,
            owner.Id,
            trusted.ConfigId,
            trusted.VersionId,
            trusted.VersionNo,
            trusted.Revision,
            trusted.Status,
            trusted.ConfigHash,
            contributionHash,
            CanonicalPins(
                formDependencyPins,
                "dynamicForm.dependencyPins"));
    }

    private async Task<StatConfigBundleDependencyPin> ResolveBasicAsync(
        NormalizedReference reference,
        CancellationToken ct)
    {
        var owner = await _ctx.WorkAssignmentBasicSummaryConfigs
            .Find(item =>
                item.VersionId == reference.VersionId &&
                item.IsActive &&
                !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (owner is null)
            throw Stale("BUNDLE_BASIC_NOT_CURRENT", reference.Kind);

        var expectedOwnerId = $"{owner.AssignmentId}:{owner.DynamicFormTemplateId}";
        if (!string.Equals(expectedOwnerId, reference.OwnerId, StringComparison.Ordinal))
            throw Stale("BUNDLE_BASIC_OWNER_MIXED", reference.Kind);

        var service = _basicConfigs ??
            throw new InvalidOperationException("Basic Summary config resolver is unavailable.");
        tdtd_be.DTOs.WorkAssignments.BasicSummary.WorkAssignmentBasicSummaryConfigReadback trusted;
        try
        {
            trusted = await service.GetP8ConfigAsync(
                owner.AssignmentId,
                owner.DynamicFormTemplateId,
                ct);
        }
        catch (AppException error)
        {
            throw Stale("BUNDLE_BASIC_INTEGRITY", reference.Kind, inner: error);
        }
        EnsureActiveConfigStatus(trusted.Identity.Status, reference.Kind);
        return Pin(
            reference.Kind,
            trusted.Identity.OwnerKind,
            trusted.Identity.OwnerId,
            trusted.Identity.ConfigId,
            trusted.Identity.VersionId,
            trusted.Identity.VersionNo,
            trusted.Identity.Revision,
            trusted.Identity.Status,
            trusted.Identity.ConfigHash,
            trusted.Identity.ConfigHash,
            CanonicalPins(trusted.Identity.DependencyPins, "basic.dependencyPins"));
    }

    private async Task<StatConfigBundleDependencyPin> ResolveAdvancedAsync(
        NormalizedReference reference,
        CancellationToken ct)
    {
        var owner = await _ctx.WorkAssignmentAdvancedSummaryConfigs
            .Find(item => item.Id == reference.VersionId && !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (owner is null)
            throw Stale("BUNDLE_ADVANCED_NOT_CURRENT", reference.Kind);

        var expectedOwnerId =
            $"{owner.AssignmentId}:{owner.DynamicFormTemplateId}:{owner.SectionId}";
        if (!string.Equals(expectedOwnerId, reference.OwnerId, StringComparison.Ordinal))
            throw Stale("BUNDLE_ADVANCED_OWNER_MIXED", reference.Kind);

        var service = _advancedConfigs ??
            throw new InvalidOperationException("Advanced Summary config resolver is unavailable.");
        tdtd_be.DTOs.WorkAssignments.AdvancedSummary.WorkAssignmentAdvancedSummaryConfigReadback trusted;
        try
        {
            trusted = await service.GetP8ConfigAsync(
                owner.AssignmentId,
                owner.DynamicFormTemplateId,
                owner.SectionId,
                ct);
        }
        catch (AppException error)
        {
            throw Stale("BUNDLE_ADVANCED_INTEGRITY", reference.Kind, inner: error);
        }
        EnsureActiveConfigStatus(trusted.Identity.Status, reference.Kind);
        return Pin(
            reference.Kind,
            trusted.Identity.OwnerKind,
            trusted.Identity.OwnerId,
            trusted.Identity.ConfigId,
            trusted.Identity.VersionId,
            trusted.Identity.VersionNo,
            trusted.Identity.Revision,
            trusted.Identity.Status,
            trusted.Identity.ConfigHash,
            trusted.Identity.ConfigHash,
            CanonicalPins(trusted.Identity.DependencyPins, "advanced.dependencyPins"));
    }

    private async Task<StatConfigBundleDependencyPin> ResolveDiffAsync(
        NormalizedReference reference,
        CancellationToken ct)
    {
        var owner = await _ctx.WorkReportStatisticDiffConfigs
            .Find(item =>
                item.Id == reference.VersionId &&
                item.IsActive &&
                !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (owner is null)
            throw Stale("BUNDLE_DIFF_NOT_CURRENT", reference.Kind);
        var templateId = owner.DynamicFormTemplateId;
        if (string.IsNullOrWhiteSpace(templateId))
            throw Stale("BUNDLE_DIFF_OWNER_INVALID", reference.Kind);
        var expectedOwnerId = $"{owner.AssignmentId}:{templateId}";
        if (!string.Equals(expectedOwnerId, reference.OwnerId, StringComparison.Ordinal))
            throw Stale("BUNDLE_DIFF_OWNER_MIXED", reference.Kind);

        var service = _diffConfigs ??
            throw new InvalidOperationException("Diff config resolver is unavailable.");
        tdtd_be.DTOs.Statistics.WorkReportStatisticDiffConfigReadback trusted;
        try
        {
            trusted = await service.GetP8ConfigAsync(
                owner.AssignmentId,
                templateId,
                ct);
        }
        catch (AppException error)
        {
            throw Stale("BUNDLE_DIFF_INTEGRITY", reference.Kind, inner: error);
        }
        EnsureActiveConfigStatus(trusted.Identity.Status, reference.Kind);
        return Pin(
            reference.Kind,
            trusted.Identity.OwnerKind,
            trusted.Identity.OwnerId,
            trusted.Identity.ConfigId,
            trusted.Identity.VersionId,
            trusted.Identity.VersionNo,
            trusted.Identity.Revision,
            trusted.Identity.Status,
            trusted.Identity.ConfigHash,
            trusted.Identity.ConfigHash,
            CanonicalPins(trusted.Identity.DependencyPins, "diff.dependencyPins"));
    }

    private async Task<StatConfigBundleDependencyPin> ResolveFlowContributionAsync(
        NormalizedReference reference,
        CancellationToken ct)
    {
        var owner = await _ctx.DynamicFlowTemplateVersions
            .Find(item => item.Id == reference.VersionId && !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (owner is null)
            throw Stale("BUNDLE_FLOW_CONTRIBUTION_NOT_CURRENT", reference.Kind);
        if (!string.Equals(
                owner.Status,
                DynamicFlowTemplateVersionStatuses.Locked,
                StringComparison.Ordinal) ||
            owner.ContributionPolicy is not (
                DynamicFlowContributionPolicyContract.Exclude or
                DynamicFlowContributionPolicyContract.Include))
        {
            throw Stale("BUNDLE_FLOW_CONTRIBUTION_INACTIVE", reference.Kind);
        }
        if (!string.Equals(
                owner.TemplateId,
                reference.OwnerId,
                StringComparison.Ordinal))
        {
            throw Stale("BUNDLE_FLOW_OWNER_MIXED", reference.Kind);
        }

        var family = await _ctx.DynamicFlowTemplates
            .Find(item => item.Id == owner.TemplateId && !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (family is null ||
            !string.Equals(
                family.Status,
                DynamicFlowTemplateStatuses.Active,
                StringComparison.Ordinal) ||
            !string.Equals(family.CurrentVersionId, owner.Id, StringComparison.Ordinal) ||
            family.CurrentVersionNo != owner.VersionNo ||
            !string.Equals(family.CurrentVersionHash, owner.PayloadHash, StringComparison.Ordinal))
        {
            throw Stale("BUNDLE_FLOW_FAMILY_NOT_CURRENT", reference.Kind);
        }

        DynamicFlowTemplateVersion? origin = null;
        var integrityVersions = new List<DynamicFlowTemplateVersion> { owner };
        if (string.Equals(
                owner.ContributionPolicy,
                DynamicFlowContributionPolicyContract.Include,
                StringComparison.Ordinal))
        {
            origin = await _ctx.DynamicFlowTemplateVersions
                .Find(item =>
                    item.Id == owner.OriginVersionId &&
                    item.TemplateId == owner.TemplateId &&
                    item.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                    !item.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (origin is null)
            {
                throw Stale(
                    "BUNDLE_FLOW_CONTRIBUTION_ORIGIN_INVALID",
                    reference.Kind);
            }
            integrityVersions.Add(origin);
        }

        try
        {
            var formIds = DynamicFlowLockedSnapshotIntegrity
                .CollectReferencedFormIds(integrityVersions);
            List<DynamicFormTemplate> forms;
            if (formIds.Count == 0)
            {
                forms = [];
            }
            else
            {
                forms = await _ctx.DynamicFormTemplates
                    .Find(Builders<DynamicFormTemplate>.Filter.In(
                        item => item.Id,
                        formIds))
                    .ToListAsync(ct);
            }
            var formsById = forms.ToDictionary(
                item => item.Id,
                StringComparer.Ordinal);
            DynamicFlowLockedSnapshotIntegrity.Validate(
                family,
                integrityVersions,
                formsById);
            DynamicFlowContributionPolicyContract.ValidateLockedPolicy(owner);
            if (string.Equals(
                    owner.ContributionPolicy,
                    DynamicFlowContributionPolicyContract.Include,
                    StringComparison.Ordinal))
            {
                DynamicFlowContributionPolicyContract.EnsureIncludeOrigin(
                    owner,
                    origin);
            }
        }
        catch (Exception error) when (
            error is AppException or InvalidOperationException)
        {
            throw Stale(
                "BUNDLE_FLOW_CONTRIBUTION_INTEGRITY",
                reference.Kind,
                inner: error);
        }

        var pins = CanonicalPins(
        [
            $"CATALOG_SEMANTIC_HASH:{RequireHash(owner.CatalogSemanticHash, "flow.catalogSemanticHash")}",
            $"CATALOG_VERSION:{RequireToken(owner.CatalogVersion, "flow.catalogVersion")}",
            $"PAYLOAD_HASH:{RequireHash(owner.PayloadHash, "flow.payloadHash")}"
        ], "flow.dependencyPins");
        return Pin(
            reference.Kind,
            StatConfigOwnerKinds.FlowContribution,
            owner.TemplateId,
            owner.TemplateId,
            owner.Id,
            owner.VersionNo,
            owner.DraftRevision,
            owner.Status,
            RequireHash(owner.PayloadHash, "flow.payloadHash"),
            RequireHash(
                owner.ContributionPolicyHash,
                "flow.contributionPolicyHash"),
            pins);
    }

    private async Task<StatConfigBundleDependencyPin> ResolveReadinessAsync(
        NormalizedReference reference,
        CancellationToken ct)
    {
        var owner = await _ctx.StatConfigValidationJobs
            .Find(item => item.Id == reference.VersionId && !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (owner is null ||
            !string.Equals(owner.OwnerId, reference.OwnerId, StringComparison.Ordinal))
        {
            throw Stale("BUNDLE_READINESS_NOT_CURRENT", reference.Kind);
        }

        var terminal = owner.Status is
            StatConfigValidationJobStatuses.Completed or
            StatConfigValidationJobStatuses.Failed or
            StatConfigValidationJobStatuses.Cancelled;
        if (!string.Equals(
                owner.QueueName,
                StatConfigValidationQueue.Name,
                StringComparison.Ordinal) ||
            !StatConfigValidationJobStatuses.All.Contains(owner.Status) ||
            owner.StateRevision < 1 ||
            owner.VersionNo < 1 ||
            owner.ConfigRevision < 0 ||
            owner.IsActive == terminal)
        {
            throw Stale("BUNDLE_READINESS_STATE_INVALID", reference.Kind);
        }

        var receiptFilter = Builders<StatConfigCommandReceipt>.Filter;
        var source = await _ctx.StatConfigCommandReceipts
            .Find(receiptFilter.And(
                receiptFilter.Eq(item => item.OwnerKind, owner.OwnerKind),
                receiptFilter.Eq(item => item.OwnerId, owner.OwnerId),
                receiptFilter.Eq(item => item.ResultConfigId, owner.ConfigId),
                receiptFilter.Eq(item => item.ResultVersionId, owner.VersionId),
                receiptFilter.Eq(item => item.ResultVersionNo, owner.VersionNo),
                receiptFilter.Eq(item => item.ResultRevision, owner.ConfigRevision),
                receiptFilter.Eq(item => item.ResultConfigHash, owner.ConfigHash),
                receiptFilter.Nin(
                    item => item.CommandKind,
                    new[]
                    {
                        StatConfigOperationsService.EnqueueCommandKind,
                        StatConfigOperationsService.ResetCommandKind,
                        StatConfigOperationsService.CancelCommandKind,
                        StatConfigOperationsService.CleanupCommandKind
                    })))
            .SortByDescending(item => item.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (source is null ||
            source.ResultStatus.Contains("TOMBSTONE", StringComparison.OrdinalIgnoreCase) ||
            source.ResultStatus.Contains("DELETED", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                source.ResponseHash,
                StatConfigCanonicalJson.HashUtf8(source.ResponseJson),
                StringComparison.Ordinal))
        {
            throw Stale("BUNDLE_READINESS_SOURCE_INTEGRITY", reference.Kind);
        }

        var sourcePins = StatConfigOperationsService.ExtractDependencyPins(
            source.ResponseJson);
        var storedPins = CanonicalPins(
            owner.DependencyPins,
            "readiness.dependencyPins");
        var dependencyPinsHash = StatConfigCanonicalJson.HashObject(sourcePins);
        var expectedBundleHash = StatConfigOperationsService.ComputeBundleHash(
            owner.OwnerKind,
            owner.OwnerId,
            source,
            dependencyPinsHash);
        var expectedStateHash =
            StatConfigOperationsService.ComputeStateHash(owner);
        if (!storedPins.SequenceEqual(sourcePins, StringComparer.Ordinal) ||
            !string.Equals(owner.DependencyPinsHash, dependencyPinsHash, StringComparison.Ordinal) ||
            !string.Equals(owner.BundleHash, expectedBundleHash, StringComparison.Ordinal) ||
            !string.Equals(owner.StateHash, expectedStateHash, StringComparison.Ordinal))
        {
            throw Stale("BUNDLE_READINESS_INTEGRITY", reference.Kind);
        }

        var contributionHash = StatConfigCanonicalJson.HashObject(new
        {
            bundleHash = expectedBundleHash,
            dependencyPinsHash,
            stateHash = expectedStateHash
        });
        return Pin(
            reference.Kind,
            RequireToken(owner.OwnerKind, "readiness.ownerKind"),
            owner.OwnerId,
            RequireObjectId(owner.ConfigId, "readiness.configId"),
            owner.Id,
            owner.VersionNo,
            owner.StateRevision,
            owner.Status,
            RequireHash(owner.ConfigHash, "readiness.configHash"),
            contributionHash,
            storedPins);
    }

    private static void EnsureRequestMatches(
        NormalizedReference request,
        StatConfigBundleDependencyPin actual)
    {
        var matches =
            request.Kind == actual.Kind &&
            request.OwnerId == actual.OwnerId &&
            request.ConfigId == actual.ConfigId &&
            request.VersionId == actual.VersionId &&
            request.VersionNo == actual.VersionNo &&
            request.Revision == actual.Revision &&
            request.ConfigHash == actual.ConfigHash &&
            request.ContributionHash == actual.ContributionHash;
        if (!matches)
        {
            throw Stale(
                "BUNDLE_DEPENDENCY_STALE",
                request.Kind,
                expected: StatConfigCanonicalJson.HashObject(request),
                actual: StatConfigCanonicalJson.HashObject(actual));
        }
    }

    private static void EnsureScopeMatches(
        string ownerKind,
        string ownerId,
        IReadOnlyList<StatConfigBundleDependencyPin> pins)
    {
        var readiness = pins.Single(item =>
            item.Kind == StatConfigBundleDependencyKinds.Readiness);
        if (!string.Equals(readiness.OwnerKind, ownerKind, StringComparison.Ordinal) ||
            !string.Equals(readiness.OwnerId, ownerId, StringComparison.Ordinal))
        {
            throw Stale(
                "BUNDLE_SCOPE_MIXED",
                StatConfigBundleDependencyKinds.Readiness);
        }
    }

    private async Task EnsureReadinessSourceMatchesAsync(
        IReadOnlyList<StatConfigBundleDependencyPin> pins,
        CancellationToken ct)
    {
        var readiness = pins.Single(item =>
            item.Kind == StatConfigBundleDependencyKinds.Readiness);
        var job = await _ctx.StatConfigValidationJobs
            .Find(item => item.Id == readiness.VersionId && !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (job is null)
        {
            throw Stale(
                "BUNDLE_READINESS_SOURCE_JOB_MISSING",
                StatConfigBundleDependencyKinds.Readiness);
        }

        IReadOnlyList<StatConfigBundleDependencyPin> sourcePins =
            job.OwnerKind switch
            {
                StatConfigOwnerKinds.Label =>
                    [pins.Single(item =>
                        item.Kind == StatConfigBundleDependencyKinds.Label)],
                StatConfigOwnerKinds.DynamicForm =>
                    [
                        pins.Single(item =>
                            item.Kind == StatConfigBundleDependencyKinds.Field),
                        pins.Single(item =>
                            item.Kind == StatConfigBundleDependencyKinds.Table)
                    ],
                StatConfigOwnerKinds.BasicSummary =>
                    [pins.Single(item =>
                        item.Kind == StatConfigBundleDependencyKinds.Basic)],
                StatConfigOwnerKinds.AdvancedSummary =>
                    [pins.Single(item =>
                        item.Kind == StatConfigBundleDependencyKinds.Advanced)],
                StatConfigOwnerKinds.Diff =>
                    [pins.Single(item =>
                        item.Kind == StatConfigBundleDependencyKinds.Diff)],
                StatConfigOwnerKinds.FlowContribution =>
                    [pins.Single(item =>
                        item.Kind == StatConfigBundleDependencyKinds.FlowContribution)],
                _ => throw Stale(
                    "BUNDLE_READINESS_SOURCE_KIND_INVALID",
                    StatConfigBundleDependencyKinds.Readiness)
            };

        var sourceMatches = sourcePins.All(pin =>
            string.Equals(pin.OwnerKind, job.OwnerKind, StringComparison.Ordinal) &&
            string.Equals(pin.OwnerId, job.OwnerId, StringComparison.Ordinal) &&
            string.Equals(pin.ConfigId, job.ConfigId, StringComparison.Ordinal) &&
            string.Equals(pin.VersionId, job.VersionId, StringComparison.Ordinal) &&
            pin.VersionNo == job.VersionNo &&
            pin.Revision == job.ConfigRevision &&
            string.Equals(pin.ConfigHash, job.ConfigHash, StringComparison.Ordinal));
        if (!sourceMatches)
        {
            throw Stale(
                "BUNDLE_READINESS_SOURCE_STALE",
                StatConfigBundleDependencyKinds.Readiness,
                expected: StatConfigCanonicalJson.HashObject(new
                {
                    job.OwnerKind,
                    job.OwnerId,
                    job.ConfigId,
                    job.VersionId,
                    job.VersionNo,
                    revision = job.ConfigRevision,
                    job.ConfigHash
                }),
                actual: StatConfigCanonicalJson.HashObject(sourcePins.Select(pin => new
                {
                    pin.OwnerKind,
                    pin.OwnerId,
                    pin.ConfigId,
                    pin.VersionId,
                    pin.VersionNo,
                    pin.Revision,
                    pin.ConfigHash
                }).ToArray()));
        }
    }

    private static void EnsureNoMixedDependencies(
        IReadOnlyList<StatConfigBundleDependencyPin> pins)
    {
        var field = pins.Single(item =>
            item.Kind == StatConfigBundleDependencyKinds.Field);
        var table = pins.Single(item =>
            item.Kind == StatConfigBundleDependencyKinds.Table);
        if (field.OwnerId != table.OwnerId ||
            field.ConfigId != table.ConfigId ||
            field.VersionId != table.VersionId ||
            field.VersionNo != table.VersionNo ||
            field.Revision != table.Revision ||
            field.ConfigHash != table.ConfigHash)
        {
            throw Stale("BUNDLE_DYNAMIC_FORM_MIXED", "FIELD+TABLE");
        }

        var label = pins.Single(item =>
            item.Kind == StatConfigBundleDependencyKinds.Label);
        var expectedLabelPin =
            $"LABEL:{label.OwnerId}:{label.VersionId}:" +
            $"{label.VersionNo}:{label.ConfigHash}";
        if (!field.DependencyPins.Contains(
                expectedLabelPin,
                StringComparer.Ordinal) ||
            !table.DependencyPins.Contains(
                expectedLabelPin,
                StringComparer.Ordinal))
        {
            throw Stale(
                "BUNDLE_LABEL_DEPENDENCY_MIXED",
                StatConfigBundleDependencyKinds.Label);
        }

        var basicOwner = pins.Single(item =>
            item.Kind == StatConfigBundleDependencyKinds.Basic);
        var advancedOwner = pins.Single(item =>
            item.Kind == StatConfigBundleDependencyKinds.Advanced);
        var diffOwner = pins.Single(item =>
            item.Kind == StatConfigBundleDependencyKinds.Diff);
        var basicOwnerParts = basicOwner.OwnerId.Split(':');
        var assignmentIdIsCanonical =
            basicOwnerParts.Length == 2 &&
            ObjectId.TryParse(basicOwnerParts[0], out var assignmentId) &&
            string.Equals(
                assignmentId.ToString(),
                basicOwnerParts[0],
                StringComparison.Ordinal);
        var expectedAssignmentFormOwner = assignmentIdIsCanonical
            ? $"{basicOwnerParts[0]}:{field.OwnerId}"
            : string.Empty;
        var advancedOwnerPrefix = $"{expectedAssignmentFormOwner}:";
        if (!assignmentIdIsCanonical ||
            !string.Equals(
                basicOwner.OwnerId,
                expectedAssignmentFormOwner,
                StringComparison.Ordinal) ||
            !string.Equals(
                diffOwner.OwnerId,
                expectedAssignmentFormOwner,
                StringComparison.Ordinal) ||
            !advancedOwner.OwnerId.StartsWith(
                advancedOwnerPrefix,
                StringComparison.Ordinal) ||
            advancedOwner.OwnerId.Length <= advancedOwnerPrefix.Length)
        {
            throw Stale("BUNDLE_ASSIGNMENT_OWNER_MIXED", "BASIC+ADVANCED+DIFF");
        }

        var expectedFormConfigPins = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            [StatConfigBundleDependencyKinds.Basic] =
                $"DYNAMIC_FORM_STAT_CONFIG:{field.OwnerId}:{field.ConfigId}:" +
                $"{field.VersionId}:" +
                $"{field.VersionNo}:{field.Revision}:{field.ConfigHash}",
            [StatConfigBundleDependencyKinds.Diff] =
                $"DYNAMIC_FORM_STAT_CONFIG:{field.ConfigId}:{field.VersionId}:" +
                $"{field.VersionNo}:{field.Revision}:{field.ConfigHash}"
        };
        foreach (var expected in expectedFormConfigPins)
        {
            var dependent = pins.Single(item => item.Kind == expected.Key);
            var configPins = dependent.DependencyPins
                .Where(value => value.StartsWith(
                    "DYNAMIC_FORM_STAT_CONFIG:",
                    StringComparison.Ordinal))
                .ToArray();
            if (configPins.Length != 1 || configPins[0] != expected.Value)
            {
                throw Stale(
                    "BUNDLE_DYNAMIC_FORM_DEPENDENCY_MIXED",
                    expected.Key);
            }
        }

        var schemaPins = field.DependencyPins
            .Where(value => value.StartsWith(
                "DYNAMIC_FORM_SCHEMA:",
                StringComparison.Ordinal))
            .ToArray();
        var tableSchemaPins = table.DependencyPins
            .Where(value => value.StartsWith(
                "DYNAMIC_FORM_SCHEMA:",
                StringComparison.Ordinal))
            .ToArray();
        var advanced = pins.Single(item =>
            item.Kind == StatConfigBundleDependencyKinds.Advanced);
        var advancedSchemaPins = advanced.DependencyPins
            .Where(value => value.StartsWith(
                "DYNAMIC_FORM_SCHEMA:",
                StringComparison.Ordinal))
            .ToArray();
        if (schemaPins.Length != 1 ||
            !schemaPins.SequenceEqual(tableSchemaPins, StringComparer.Ordinal) ||
            !schemaPins.SequenceEqual(advancedSchemaPins, StringComparer.Ordinal))
        {
            throw Stale(
                "BUNDLE_DYNAMIC_FORM_SCHEMA_MIXED",
                StatConfigBundleDependencyKinds.Advanced);
        }
    }

    private static StatConfigBundleReadback BuildReadback(
        string ownerKind,
        string ownerId,
        IReadOnlyList<StatConfigBundleDependencyPin> pins)
    {
        var isEmpty = pins.Count == 0;
        var eligibility = new StatConfigBundleEligibility(
            isEmpty ? EmptyValid : "ELIGIBLE",
            EmptyValid,
            "UNSUPPORTED",
            "P9");
        var freshness = isEmpty ? EmptyValid : Fresh;
        var canonicalValue = new
        {
            schemaVersion = SchemaVersion,
            ownerKind,
            ownerId,
            isEmpty,
            pins,
            eligibility,
            freshness
        };
        var canonicalJson = StatConfigCanonicalJson.Canonicalize(canonicalValue);
        return new StatConfigBundleReadback(
            SchemaVersion,
            ownerKind,
            ownerId,
            isEmpty,
            pins,
            eligibility,
            freshness,
            canonicalJson,
            StatConfigCanonicalJson.HashUtf8(canonicalJson));
    }

    private static IReadOnlyDictionary<string, NormalizedReference>
        NormalizeReferences(
            IReadOnlyList<StatConfigBundleDependencyReferenceRequest>? values)
    {
        values ??= [];
        if (values.Count == 0)
            return new Dictionary<string, NormalizedReference>(StringComparer.Ordinal);
        if (values.Count != StatConfigBundleDependencyKinds.Ordered.Count)
            throw Schema("$.dependencyPins", "BUNDLE_PIN_SET_INCOMPLETE");

        var normalized = new Dictionary<string, NormalizedReference>(
            StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index]
                        ?? throw Schema(
                            $"$.dependencyPins[{index}]",
                            "BUNDLE_PIN_REQUIRED");
            var path = $"$.dependencyPins[{index}]";
            var kind = RequireToken(value.Kind, $"{path}.kind");
            if (!StatConfigBundleDependencyKinds.All.Contains(kind))
                throw Schema($"{path}.kind", "BUNDLE_KIND_INVALID");
            if (!normalized.TryAdd(kind, new NormalizedReference(
                    kind,
                    RequireDependencyOwnerId(kind, value.OwnerId, $"{path}.ownerId"),
                    RequireObjectId(value.ConfigId, $"{path}.configId"),
                    RequireObjectId(value.VersionId, $"{path}.versionId"),
                    value.VersionNo is > 0
                        ? value.VersionNo.Value
                        : throw Schema($"{path}.versionNo", "POSITIVE_VERSION_REQUIRED"),
                    value.Revision is >= 0
                        ? value.Revision.Value
                        : throw Schema($"{path}.revision", "NON_NEGATIVE_REVISION_REQUIRED"),
                    RequireHash(value.ConfigHash, $"{path}.configHash"),
                    RequireHash(
                        value.ContributionHash,
                        $"{path}.contributionHash"))))
            {
                throw Schema($"{path}.kind", "BUNDLE_KIND_DUPLICATE");
            }
        }

        if (!StatConfigBundleDependencyKinds.Ordered.All(normalized.ContainsKey))
            throw Schema("$.dependencyPins", "BUNDLE_PIN_SET_INCOMPLETE");
        return normalized;
    }

    private static (string OwnerKind, string OwnerId) NormalizeScope(
        string? ownerKind,
        string? ownerId)
    {
        ownerKind = RequireToken(ownerKind, "$.ownerKind");
        if (!TokenRegex.IsMatch(ownerKind))
            throw Schema("$.ownerKind", "CANONICAL_OWNER_KIND_REQUIRED");
        return (ownerKind, RequireCanonicalOwnerId(ownerId, "$.ownerId"));
    }

    private static StatConfigBundleDependencyPin Pin(
        string kind,
        string ownerKind,
        string ownerId,
        string configId,
        string versionId,
        int versionNo,
        long revision,
        string status,
        string configHash,
        string contributionHash,
        IReadOnlyList<string> dependencyPins)
    {
        if (versionNo < 1 || revision < 0)
            throw Stale("BUNDLE_PERSISTED_IDENTITY_INVALID", kind);
        return new StatConfigBundleDependencyPin(
            kind,
            RequireToken(ownerKind, $"{kind}.ownerKind"),
            RequireDependencyOwnerId(kind, ownerId, $"{kind}.ownerId"),
            RequireObjectId(configId, $"{kind}.configId"),
            RequireObjectId(versionId, $"{kind}.versionId"),
            versionNo,
            revision,
            RequireToken(status, $"{kind}.status"),
            RequireHash(configHash, $"{kind}.configHash"),
            RequireHash(contributionHash, $"{kind}.contributionHash"),
            dependencyPins);
    }

    private static IReadOnlyList<string> CanonicalPins(
        IEnumerable<string>? values,
        string path)
    {
        if (values is null)
            throw Stale("BUNDLE_PERSISTED_PINS_MISSING", path);
        var source = values
            .Select(value => value?.Trim() ?? string.Empty)
            .ToArray();
        if (source.Any(string.IsNullOrWhiteSpace))
            throw Stale("BUNDLE_PERSISTED_PINS_INVALID", path);
        var canonical = source
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (!source.SequenceEqual(canonical, StringComparer.Ordinal))
            throw Stale("BUNDLE_PERSISTED_PINS_NON_CANONICAL", path);
        return canonical;
    }

    private static string HashCanonicalJson(string json, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return StatConfigCanonicalJson.HashUtf8(
                StatConfigCanonicalJson.CanonicalizeElement(
                    document.RootElement));
        }
        catch (JsonException error)
        {
            throw Stale("BUNDLE_PERSISTED_JSON_INVALID", path, inner: error);
        }
    }

    private static string RequireToken(string? value, string path)
    {
        value = value?.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(value)
            ? throw Schema(path, "NON_EMPTY_TOKEN_REQUIRED")
            : value;
    }

    private static string RequireObjectId(string? value, string path)
    {
        value = value?.Trim().ToLowerInvariant();
        if (value is null ||
            !ObjectId.TryParse(value, out var parsed) ||
            !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            throw Schema(path, "CANONICAL_OBJECT_ID_REQUIRED");
        }
        return value;
    }

    private static string RequireDependencyOwnerId(
        string kind,
        string? value,
        string path)
        => kind is
            StatConfigBundleDependencyKinds.Label or
            StatConfigBundleDependencyKinds.Field or
            StatConfigBundleDependencyKinds.Table or
            StatConfigBundleDependencyKinds.FlowContribution
            ? RequireObjectId(value, path)
            : RequireCanonicalOwnerId(value, path);

    private static string RequireCanonicalOwnerId(
        string? value,
        string path)
    {
        var normalized = value?.Trim();
        if (normalized is null ||
            normalized.Length == 0 ||
            normalized.Length > 768 ||
            normalized.Any(char.IsControl) ||
            !string.Equals(value, normalized, StringComparison.Ordinal))
        {
            throw Schema(path, "CANONICAL_OWNER_ID_REQUIRED");
        }
        return normalized;
    }

    private static void EnsureActiveConfigStatus(string status, string kind)
    {
        if (status is not (StatConfigStatuses.Draft or StatConfigStatuses.Locked))
            throw Stale("BUNDLE_CONFIG_INACTIVE", kind);
    }

    private static string RequireHash(string? value, string path)
    {
        value = value?.Trim().ToLowerInvariant();
        if (value is null || !Sha256Regex.IsMatch(value))
            throw Schema(path, "SHA256_REQUIRED");
        return value;
    }

    private static AppException Schema(string path, string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            new { path, reason });

    private static AppException Stale(
        string reason,
        string? kind = null,
        string? expected = null,
        string? actual = null,
        Exception? inner = null)
        => new(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                reason,
                kind,
                freshness = "STALE",
                expected,
                actual,
                autoUpgrade = false
            },
            innerException: inner);

    private sealed record NormalizedReference(
        string Kind,
        string OwnerId,
        string ConfigId,
        string VersionId,
        int VersionNo,
        long Revision,
        string ConfigHash,
        string ContributionHash);
}
