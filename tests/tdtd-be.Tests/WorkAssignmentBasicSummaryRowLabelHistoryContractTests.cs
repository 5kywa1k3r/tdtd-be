using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Services;
using tdtd_be.Services.StatisticsConfiguration;

internal static class
    WorkAssignmentBasicSummaryRowLabelHistoryContractTests
{
    private const string LabelId = "720000000000000000000001";
    private const string HistoricalVersionId =
        "720000000000000000000002";
    private const string CurrentVersionId =
        "720000000000000000000003";
    private const string AlternateHistoricalVersionId =
        "720000000000000000000004";
    private const string ActorId = "720000000000000000000005";

    public static void Run()
    {
        CurrentAndHistoricalLabelViewsAreStrong();
        CurrentLabelIntegrityRejectsEveryPartialState();
        HistoricalSnapshotRejectsMissingDuplicateAndMismatch();
    }

    private static void CurrentAndHistoricalLabelViewsAreStrong()
    {
        var fixture = CreateTrustedHistory();

        LabelConfigCommandService
            .ValidateP804TrustedActiveTableTarget(fixture.Owner);
        LabelConfigCommandService
            .ValidateP804TrustedTableTargetSnapshot(
                fixture.Owner,
                fixture.Snapshot);

        AssertEqual(
            "row_label_current",
            fixture.Owner.Code,
            "current label code changed after the frozen version");
        AssertEqual(
            LabelDataTypes.Number,
            fixture.Owner.DataType,
            "current label data type changed after the frozen version");
        AssertEqual(
            "row_label_frozen",
            fixture.Snapshot.Code,
            "modern statistic retains historical label code");
        AssertEqual(
            LabelDataTypes.ShortText,
            fixture.Snapshot.DataType,
            "modern statistic retains historical label data type");
        AssertEqual(
            HistoricalVersionId,
            fixture.Snapshot.VersionId,
            "modern statistic binds the historical version id");
        AssertEqual(
            1,
            fixture.Snapshot.VersionNo,
            "modern statistic binds the historical version number");
    }

    private static void CurrentLabelIntegrityRejectsEveryPartialState()
    {
        var partialIdentity = CreateTrustedHistory();
        partialIdentity.Owner.ConfigId = null;
        ExpectLabelCas(
            () => LabelConfigCommandService
                .ValidateP804TrustedActiveTableTarget(
                    partialIdentity.Owner),
            "partial current identity",
            "BASIC_SUMMARY_ROW_LABEL_IDENTITY_INVALID");

        var nonCanonicalHash = CreateTrustedHistory();
        nonCanonicalHash.Owner.ConfigHash =
            nonCanonicalHash.Owner.ConfigHash!.ToUpperInvariant();
        ExpectLabelCas(
            () => LabelConfigCommandService
                .ValidateP804TrustedActiveTableTarget(
                    nonCanonicalHash.Owner),
            "uppercase current hash",
            "BASIC_SUMMARY_ROW_LABEL_IDENTITY_INVALID");

        var hashMismatch = CreateTrustedHistory();
        hashMismatch.Owner.ConfigHash = new string('a', 64);
        ExpectLabelCas(
            () => LabelConfigCommandService
                .ValidateP804TrustedActiveTableTarget(
                    hashMismatch.Owner),
            "current hash mismatch",
            "BASIC_SUMMARY_ROW_LABEL_HASH_MISMATCH");

        var brokenLineage = CreateTrustedHistory();
        brokenLineage.Owner.VersionSnapshots.RemoveAt(0);
        ExpectLabelCas(
            () => LabelConfigCommandService
                .ValidateP804TrustedActiveTableTarget(
                    brokenLineage.Owner),
            "missing lineage version",
            "BASIC_SUMMARY_ROW_LABEL_LINEAGE_INVALID");

        var currentSnapshotMismatch = CreateTrustedHistory();
        currentSnapshotMismatch.Owner.VersionSnapshots[^1].Name =
            "tampered current name";
        ExpectLabelCas(
            () => LabelConfigCommandService
                .ValidateP804TrustedActiveTableTarget(
                    currentSnapshotMismatch.Owner),
            "current snapshot mismatch",
            "BASIC_SUMMARY_ROW_LABEL_CURRENT_SNAPSHOT_INVALID");
    }

    private static void
        HistoricalSnapshotRejectsMissingDuplicateAndMismatch()
    {
        var exactHistoricalVersionMissing = CreateTrustedHistory();
        var historical =
            exactHistoricalVersionMissing.Owner.VersionSnapshots[0];
        var current =
            exactHistoricalVersionMissing.Owner.VersionSnapshots[^1];
        historical.VersionId = AlternateHistoricalVersionId;
        current.PreviousVersionId = AlternateHistoricalVersionId;
        exactHistoricalVersionMissing.Owner.PreviousVersionId =
            AlternateHistoricalVersionId;
        ExpectLabelCas(
            () => LabelConfigCommandService
                .ValidateP804TrustedTableTargetSnapshot(
                    exactHistoricalVersionMissing.Owner,
                    exactHistoricalVersionMissing.Snapshot),
            "exact historical version missing",
            "BASIC_SUMMARY_ROW_LABEL_HISTORICAL_SNAPSHOT_INVALID");

        var duplicateHistoricalVersion = CreateTrustedHistory();
        duplicateHistoricalVersion.Owner.VersionSnapshots.Insert(
            0,
            CloneVersion(
                duplicateHistoricalVersion.Owner.VersionSnapshots[0]));
        ExpectLabelCas(
            () => LabelConfigCommandService
                .ValidateP804TrustedTableTargetSnapshot(
                    duplicateHistoricalVersion.Owner,
                    duplicateHistoricalVersion.Snapshot),
            "duplicate historical version",
            "BASIC_SUMMARY_ROW_LABEL_LINEAGE_INVALID",
            "BASIC_SUMMARY_ROW_LABEL_HISTORICAL_SNAPSHOT_INVALID");

        var snapshotCodeMismatch = CreateTrustedHistory();
        ExpectLabelCas(
            () => LabelConfigCommandService
                .ValidateP804TrustedTableTargetSnapshot(
                    snapshotCodeMismatch.Owner,
                    snapshotCodeMismatch.Snapshot with
                    {
                        Code = "row_label_other"
                    }),
            "historical code mismatch",
            "BASIC_SUMMARY_ROW_LABEL_HISTORICAL_SNAPSHOT_INVALID");

        var snapshotDataTypeMismatch = CreateTrustedHistory();
        ExpectLabelCas(
            () => LabelConfigCommandService
                .ValidateP804TrustedTableTargetSnapshot(
                    snapshotDataTypeMismatch.Owner,
                    snapshotDataTypeMismatch.Snapshot with
                    {
                        DataType = LabelDataTypes.Boolean
                    }),
            "historical data type mismatch",
            "BASIC_SUMMARY_ROW_LABEL_HISTORICAL_SNAPSHOT_INVALID");

        var snapshotHashMismatch = CreateTrustedHistory();
        ExpectLabelCas(
            () => LabelConfigCommandService
                .ValidateP804TrustedTableTargetSnapshot(
                    snapshotHashMismatch.Owner,
                    snapshotHashMismatch.Snapshot with
                    {
                        ConfigHash = new string('b', 64)
                    }),
            "historical hash mismatch",
            "BASIC_SUMMARY_ROW_LABEL_HISTORICAL_SNAPSHOT_INVALID");

        var historicalStatusMismatch = CreateTrustedHistory();
        historicalStatusMismatch.Owner.VersionSnapshots[0].Status =
            StatConfigStatuses.Inactive;
        ExpectLabelCas(
            () => LabelConfigCommandService
                .ValidateP804TrustedTableTargetSnapshot(
                    historicalStatusMismatch.Owner,
                    historicalStatusMismatch.Snapshot),
            "historical status mismatch",
            "BASIC_SUMMARY_ROW_LABEL_HISTORICAL_SNAPSHOT_INVALID");

        var historicalFieldMismatch = CreateTrustedHistory();
        historicalFieldMismatch.Owner.VersionSnapshots[0].ScopeType =
            LabelScopeTypes.Level;
        historicalFieldMismatch.Owner.VersionSnapshots[0].ScopeId =
            ActorId;
        ExpectLabelCas(
            () => LabelConfigCommandService
                .ValidateP804TrustedTableTargetSnapshot(
                    historicalFieldMismatch.Owner,
                    historicalFieldMismatch.Snapshot),
            "historical scope mismatch",
            "BASIC_SUMMARY_ROW_LABEL_HISTORICAL_SNAPSHOT_INVALID");
    }

    private static LabelHistoryFixture CreateTrustedHistory()
    {
        var historicalOwner = CreateLabelOwner(
            code: "row_label_frozen",
            name: "Frozen row label",
            dataType: LabelDataTypes.ShortText,
            versionId: HistoricalVersionId,
            previousVersionId: null,
            versionNo: 1,
            revision: 1);
        historicalOwner.ConfigHash = ComputeLabelConfigHash(
            historicalOwner,
            StatConfigStatuses.Active);
        var historical = CreateVersion(
            historicalOwner,
            StatConfigStatuses.Active);

        var owner = CreateLabelOwner(
            code: "row_label_current",
            name: "Current row label",
            dataType: LabelDataTypes.Number,
            versionId: CurrentVersionId,
            previousVersionId: HistoricalVersionId,
            versionNo: 2,
            revision: 2);
        owner.ConfigHash = ComputeLabelConfigHash(
            owner,
            StatConfigStatuses.Active);
        var current = CreateVersion(owner, StatConfigStatuses.Active);
        owner.VersionSnapshots = [historical, current];

        var snapshot = new DynamicFormStatisticLabelSnapshotDto(
            historical.LabelId,
            historical.Code,
            historical.DataType,
            historical.Usage,
            historical.ScopeType,
            historical.ScopeId,
            historical.IsActive,
            historical.VersionNo,
            historical.VersionId,
            historical.ConfigHash);
        return new LabelHistoryFixture(owner, snapshot);
    }

    private static LabelCatalogItem CreateLabelOwner(
        string code,
        string name,
        string dataType,
        string versionId,
        string? previousVersionId,
        int versionNo,
        long revision)
        => new()
        {
            Id = LabelId,
            ConfigId = LabelId,
            VersionId = versionId,
            PreviousVersionId = previousVersionId,
            VersionNo = versionNo,
            Revision = revision,
            Code = code,
            Name = name,
            NameLower = name.ToLowerInvariant(),
            Usage = LabelUsages.TableTarget,
            DataType = dataType,
            ValueSourceType = LabelValueSourceTypes.None,
            ValueOptions = new(),
            ScopeType = LabelScopeTypes.Global,
            ScopeId = null,
            IsActive = true,
            IsDeleted = false,
            DependencyPins = new(),
            VersionSnapshots = new()
        };

    private static string ComputeLabelConfigHash(
        LabelCatalogItem label,
        string status)
        => StatConfigCanonicalJson.HashObject(new
        {
            labelId = label.Id,
            label.Code,
            label.Name,
            label.Description,
            label.Color,
            label.GroupCode,
            label.Usage,
            label.DataType,
            label.ValueSourceType,
            valueOptions = label.ValueOptions
                .Select(item => new
                {
                    item.Code,
                    item.Label
                })
                .ToList(),
            label.ValueSourceCatalogId,
            label.ValueSourceCatalogCode,
            label.ValueSourceCatalogName,
            label.ScopeType,
            label.ScopeId,
            label.IsActive,
            status,
            dependencyPins = label.DependencyPins
        });

    private static LabelConfigVersionSnapshot CreateVersion(
        LabelCatalogItem label,
        string status)
        => new()
        {
            LabelId = label.Id,
            VersionId = label.VersionId!,
            PreviousVersionId = label.PreviousVersionId,
            VersionNo = label.VersionNo,
            Revision = label.Revision,
            Status = status,
            ConfigHash = label.ConfigHash!,
            Code = label.Code,
            Name = label.Name,
            Usage = label.Usage,
            DataType = label.DataType,
            ValueSourceType = label.ValueSourceType,
            ScopeType = label.ScopeType,
            ScopeId = label.ScopeId,
            IsActive = label.IsActive,
            DependencyPins = label.DependencyPins.ToList(),
            CreatedAtUtc = new DateTime(
                2026,
                8,
                2,
                0,
                0,
                0,
                DateTimeKind.Utc),
            CreatedByUserId = ActorId
        };

    private static LabelConfigVersionSnapshot CloneVersion(
        LabelConfigVersionSnapshot source)
        => new()
        {
            LabelId = source.LabelId,
            VersionId = source.VersionId,
            PreviousVersionId = source.PreviousVersionId,
            VersionNo = source.VersionNo,
            Revision = source.Revision,
            Status = source.Status,
            ConfigHash = source.ConfigHash,
            Code = source.Code,
            Name = source.Name,
            Usage = source.Usage,
            DataType = source.DataType,
            ValueSourceType = source.ValueSourceType,
            ScopeType = source.ScopeType,
            ScopeId = source.ScopeId,
            IsActive = source.IsActive,
            DependencyPins = source.DependencyPins.ToList(),
            CreatedAtUtc = source.CreatedAtUtc,
            CreatedByUserId = source.CreatedByUserId
        };

    private static void ExpectLabelCas(
        Action action,
        string context,
        params string[] acceptedReasons)
    {
        try
        {
            action();
        }
        catch (AppException error)
        {
            AssertEqual(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                error.Code,
                $"{context} error code");
            var details = JsonSerializer.Serialize(error.Details);
            if (!acceptedReasons.Any(reason =>
                    details.Contains(reason, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"{context}: expected one of " +
                    $"'{string.Join("', '", acceptedReasons)}', " +
                    $"got '{details}'.");
            }
            return;
        }

        throw new InvalidOperationException(
            $"{context}: expected an AppException.");
    }

    private static void AssertEqual<T>(
        T expected,
        T actual,
        string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{context}: expected '{expected}', got '{actual}'.");
        }
    }

    private sealed record LabelHistoryFixture(
        LabelCatalogItem Owner,
        DynamicFormStatisticLabelSnapshotDto Snapshot);
}
