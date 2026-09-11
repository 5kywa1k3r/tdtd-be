using MongoDB.Bson;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation;

internal static class StatisticReconciliationActualCapturePlanIntegrity
{
    internal const string BoundaryRegistryVersion =
        "P10_ACTUAL_BOUNDARY_REGISTRY_V1";
    private const int MaxExactNodeIds = 4096;
    internal const int ApiPageSize = 200;
    internal const int MaxApiPageCount = 32;
    internal const long MaxApiTotalRows = ApiPageSize * MaxApiPageCount;
    internal const string BasicFamily = "BASIC";
    internal const string AdvancedFamily = "ADVANCED";
    internal const string DiffFamily = "DIFF";

    internal static void RequireValid(
        StatisticReconciliationActualCapturePlan? plan,
        string? runSha)
    {
        if (plan is null ||
            !PlanVersion(plan.SchemaVersion) ||
            !StringComparer.Ordinal.Equals(
                plan.BoundaryRegistryVersion,
                BoundaryRegistryVersion) ||
            !Sha(plan.ActualConfigurationBundleSha256) ||
            !SummaryTargets(plan) ||
            plan.Api is null ||
            !ApiSurface(plan.Api.Surface) ||
            !CanonicalObjectId(plan.Api.OwnerResultId) ||
            !ValidApiPagePlan(
                plan.Api.ExpectedTotalRows,
                plan.Api.PageSize,
                plan.Api.PageCount) ||
            plan.Export is null ||
            !Required(plan.Export.ExportId) ||
            !ExportResultKind(plan.SchemaVersion, plan.Export.ResultKind) ||
            !CanonicalObjectId(plan.Export.WorkId) ||
            !Required(plan.Export.ScopeType) ||
            !Required(plan.Export.ScopeId) ||
            !Required(plan.Export.ResultId) ||
            !ExportFilter(plan.SchemaVersion, plan.Export.FilterSha256) ||
            !Sha(plan.Export.RequestSha256) ||
            !Sha(plan.Export.AuthorizationSnapshotSha256) ||
            !Sha(plan.Export.ContentSha256) ||
            !Sha(plan.Export.ColumnManifestSha256) ||
            !Sha(plan.Export.OwnerSemanticSha256) ||
            !SurfaceApplicability(plan) ||
            !Sha(plan.PlanSha256) ||
            !Sha(runSha) ||
            !StringComparer.Ordinal.Equals(plan.PlanSha256, runSha) ||
            !StringComparer.Ordinal.Equals(plan.PlanSha256, PlanSha(plan)))
        {
            throw new InvalidOperationException(
                "P10_ACTUAL_CAPTURE_PLAN_INTEGRITY_INVALID");
        }
    }

    internal static string PlanSha(
        StatisticReconciliationActualCapturePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var digestDomain = plan.SchemaVersion switch
        {
            StatisticReconciliationActualCapturePlanVersions.V1 =>
                "P10_ACTUAL_CAPTURE_PLAN_DIGEST_V1",
            StatisticReconciliationActualCapturePlanVersions.V2 =>
                "P10_ACTUAL_CAPTURE_PLAN_DIGEST_V2",
            StatisticReconciliationActualCapturePlanVersions.V3 =>
                "P10_ACTUAL_CAPTURE_PLAN_DIGEST_V3",
            StatisticReconciliationActualCapturePlanVersions.V4 =>
                "P10_ACTUAL_CAPTURE_PLAN_DIGEST_V4",
            _ => throw new InvalidOperationException(
                "P10_ACTUAL_CAPTURE_PLAN_VERSION_INVALID")
        };
        if (StringComparer.Ordinal.Equals(
                plan.SchemaVersion,
                StatisticReconciliationActualCapturePlanVersions.V4))
        {
            return StatisticReconciliationCanonicalJson.HashObject(new
            {
                schema = digestDomain,
                plan.SchemaVersion,
                plan.BoundaryRegistryVersion,
                plan.ActualConfigurationBundleSha256,
                plan.P8ConfigurationOwnerId,
                plan.P8ConfigurationBundleSha256,
                basic = new
                {
                    plan.Basic.Disposition,
                    plan.Basic.ApplicabilityProofSha256,
                    plan.Basic.SnapshotId,
                    plan.Basic.Mode,
                    plan.Basic.ImmutableSelectorSha256
                },
                advanced = new
                {
                    plan.Advanced.Disposition,
                    plan.Advanced.ApplicabilityProofSha256,
                    plan.Advanced.SectionId,
                    dayNodeIds = plan.Advanced.DayNodeIds,
                    monthNodeIds = plan.Advanced.MonthNodeIds,
                    yearNodeIds = plan.Advanced.YearNodeIds,
                    plan.Advanced.ImmutableSelectorSha256
                },
                diff = new
                {
                    plan.Diff.Disposition,
                    plan.Diff.ApplicabilityProofSha256,
                    plan.Diff.ResultId,
                    plan.Diff.RunId,
                    plan.Diff.ImmutableSelectorSha256
                },
                api = new
                {
                    plan.Api.Surface,
                    plan.Api.OwnerResultId,
                    plan.Api.ExpectedTotalRows,
                    plan.Api.PageSize,
                    plan.Api.PageCount
                },
                export = new
                {
                    plan.Export.ExportId,
                    plan.Export.ResultKind,
                    plan.Export.WorkId,
                    plan.Export.ScopeType,
                    plan.Export.ScopeId,
                    plan.Export.ResultId,
                    plan.Export.FilterSha256,
                    plan.Export.RequestSha256,
                    plan.Export.AuthorizationSnapshotSha256,
                    plan.Export.ContentSha256,
                    plan.Export.ColumnManifestSha256,
                    plan.Export.OwnerSemanticSha256
                }
            });
        }
        if (StringComparer.Ordinal.Equals(
                plan.SchemaVersion,
                StatisticReconciliationActualCapturePlanVersions.V3))
        {
            return StatisticReconciliationCanonicalJson.HashObject(new
            {
                schema = digestDomain,
                plan.SchemaVersion,
                plan.BoundaryRegistryVersion,
                plan.ActualConfigurationBundleSha256,
                basic = new
                {
                    plan.Basic.SnapshotId,
                    plan.Basic.Mode,
                    plan.Basic.ImmutableSelectorSha256
                },
                advanced = new
                {
                    plan.Advanced.SectionId,
                    dayNodeIds = plan.Advanced.DayNodeIds,
                    monthNodeIds = plan.Advanced.MonthNodeIds,
                    yearNodeIds = plan.Advanced.YearNodeIds,
                    plan.Advanced.ImmutableSelectorSha256
                },
                diff = new
                {
                    plan.Diff.ResultId,
                    plan.Diff.RunId,
                    plan.Diff.ImmutableSelectorSha256
                },
                api = new
                {
                    plan.Api.Surface,
                    plan.Api.OwnerResultId,
                    plan.Api.ExpectedTotalRows,
                    plan.Api.PageSize,
                    plan.Api.PageCount
                },
                export = new
                {
                    plan.Export.ExportId,
                    plan.Export.ResultKind,
                    plan.Export.WorkId,
                    plan.Export.ScopeType,
                    plan.Export.ScopeId,
                    plan.Export.ResultId,
                    plan.Export.FilterSha256,
                    plan.Export.RequestSha256,
                    plan.Export.AuthorizationSnapshotSha256,
                    plan.Export.ContentSha256,
                    plan.Export.ColumnManifestSha256,
                    plan.Export.OwnerSemanticSha256
                }
            });
        }
        return StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = digestDomain,
            plan.SchemaVersion,
            plan.BoundaryRegistryVersion,
            plan.ActualConfigurationBundleSha256,
            basic = new
            {
                plan.Basic.SnapshotId,
                plan.Basic.Mode,
                plan.Basic.ImmutableSelectorSha256
            },
            advanced = new
            {
                plan.Advanced.SectionId,
                dayNodeIds = plan.Advanced.DayNodeIds,
                monthNodeIds = plan.Advanced.MonthNodeIds,
                yearNodeIds = plan.Advanced.YearNodeIds,
                plan.Advanced.ImmutableSelectorSha256
            },
            diff = new
            {
                plan.Diff.ResultId,
                plan.Diff.RunId,
                plan.Diff.ImmutableSelectorSha256
            },
            api = new
            {
                plan.Api.Surface,
                plan.Api.OwnerResultId,
                plan.Api.ExpectedTotalRows,
                plan.Api.PageSize,
                plan.Api.PageCount
            },
            export = new
            {
                plan.Export.ExportId,
                plan.Export.ResultKind,
                plan.Export.WorkId,
                plan.Export.ScopeType,
                plan.Export.ScopeId,
                plan.Export.ResultId,
                plan.Export.RequestSha256,
                plan.Export.AuthorizationSnapshotSha256,
                plan.Export.ContentSha256,
                plan.Export.ColumnManifestSha256,
                plan.Export.OwnerSemanticSha256
            }
        });
    }

    internal static bool ValidApiPagePlan(
        long expectedTotalRows,
        int pageSize,
        int pageCount)
        => expectedTotalRows is >= 0 and <= MaxApiTotalRows &&
           pageSize == ApiPageSize &&
           pageCount is >= 1 and <= MaxApiPageCount &&
           pageCount == Math.Max(
               1,
               checked((int)((expectedTotalRows + pageSize - 1) / pageSize)));

    internal static string BasicSelectorSha(
        WorkAssignmentBasicSummarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_ACTUAL_BASIC_SELECTOR_V1",
            snapshot.Id,
            snapshot.WorkId,
            snapshot.ScopeAssignmentId,
            snapshot.DynamicFormTemplateId,
            snapshot.SourceScopeMode,
            snapshot.SourceFlowInstanceId,
            snapshot.SourceFlowStepId,
            snapshot.SourceFlowBranchId,
            snapshot.SourceFlowEffectiveStatus,
            snapshot.RequestHash,
            snapshot.ConfigId,
            snapshot.ConfigVersionId,
            snapshot.ConfigVersionNo,
            snapshot.ConfigRevision,
            snapshot.ConfigHash,
            configDependencyPins = snapshot.ConfigDependencyPins,
            snapshot.CandidateChainId,
            snapshot.CandidatePromptId,
            snapshot.CandidateStage,
            snapshot.CandidateCatalogRawSha256,
            snapshot.CandidateCatalogSemanticSha256,
            snapshot.CandidateStageLockSha256,
            requestJsonSha256 = StatisticReconciliationCanonicalJson.HashText(
                snapshot.RequestJson ?? string.Empty),
            sourceAssignmentIds = snapshot.SourceAssignmentIds,
            sourceReportIds = snapshot.SourceReportIds,
            snapshot.SourceSignatureHash,
            snapshotJsonSha256 = StatisticReconciliationCanonicalJson.HashText(
                snapshot.SnapshotJson ?? string.Empty),
            snapshot.SnapshotDirty,
            snapshot.SnapshotDirtyAtUtc,
            snapshot.SnapshotRefreshedAtUtc,
            snapshot.RefreshStatus,
            snapshot.RefreshJobId,
            snapshot.RefreshCorrelationId,
            snapshot.RefreshQueuedAtUtc,
            snapshot.RefreshStartedAtUtc,
            snapshot.RefreshFinishedAtUtc,
            snapshot.RefreshResetAtUtc
        });
    }

    internal static string AdvancedSelectorSha(
        string sectionId,
        IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode> days,
        IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode> months,
        IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode> years)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(months);
        ArgumentNullException.ThrowIfNull(years);
        var dayRows = days.OrderBy(row => row.Id, StringComparer.Ordinal).ToArray();
        var monthRows = months.OrderBy(row => row.Id, StringComparer.Ordinal).ToArray();
        var yearRows = years.OrderBy(row => row.Id, StringComparer.Ordinal).ToArray();
        var all = dayRows.Cast<WorkAssignmentAdvancedSummaryHierarchyNodeBase>()
            .Concat(monthRows)
            .Concat(yearRows)
            .ToArray();
        if (all.Length is < 1 or > MaxExactNodeIds ||
            all.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != all.Length)
        {
            throw new InvalidOperationException(
                "P10_ACTUAL_ADVANCED_SELECTOR_SET_INVALID");
        }
        var anchor = all[0];
        if (!Required(sectionId) ||
            all.Any(row => !SameAdvancedBoundary(anchor, row, sectionId)) ||
            dayRows.Any(row => row.Grain != WorkAssignmentAdvancedSummaryHierarchyGrains.Day) ||
            monthRows.Any(row => row.Grain != WorkAssignmentAdvancedSummaryHierarchyGrains.Month) ||
            yearRows.Any(row => row.Grain != WorkAssignmentAdvancedSummaryHierarchyGrains.Year))
        {
            throw new InvalidOperationException(
                "P10_ACTUAL_ADVANCED_SELECTOR_BOUNDARY_INVALID");
        }
        return StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_ACTUAL_ADVANCED_SELECTOR_V1",
            anchor.WorkId,
            anchor.AssignmentId,
            anchor.DynamicFormTemplateId,
            sectionId,
            anchor.ConfigId,
            anchor.ConfigVersionId,
            anchor.ConfigVersionNo,
            anchor.ConfigRevision,
            anchor.ConfigHash,
            dependencyPins = anchor.DependencyPins,
            anchor.TimeAxis,
            anchor.CandidateChainId,
            anchor.CandidatePromptId,
            anchor.CandidateStage,
            anchor.CandidateCatalogRawSha256,
            anchor.CandidateCatalogSemanticSha256,
            anchor.CandidateStageLockSha256,
            dayNodes = dayRows.Select(NodeSelector).ToArray(),
            monthNodes = monthRows.Select(NodeSelector).ToArray(),
            yearNodes = yearRows.Select(NodeSelector).ToArray()
        });
    }

    internal static string DiffSelectorSha(
        WorkReportStatisticDiffResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_ACTUAL_DIFF_SELECTOR_V1",
            result.Id,
            result.RunId,
            result.WorkId,
            result.AssignmentId,
            result.DynamicFormTemplateId,
            result.ConfigId,
            result.ConfigVersionId,
            result.ConfigVersionNo,
            result.ConfigRevision,
            result.ConfigHash,
            dependencyPins = result.DependencyPins,
            result.CandidateChainId,
            result.CandidatePromptId,
            result.CandidateStage,
            result.CandidateCatalogRawSha256,
            result.CandidateCatalogSemanticSha256,
            result.CandidateStageLockSha256,
            result.LeftConceptKind,
            result.LeftConceptKey,
            result.LeftConceptCode,
            result.LeftDataType,
            result.LeftPeriodJson,
            result.RightConceptKind,
            result.RightConceptKey,
            result.RightConceptCode,
            result.RightDataType,
            result.RightPeriodJson,
            result.Direction,
            result.MissingPolicy,
            result.EmptyPolicy,
            result.TimeAxis,
            result.CommandId,
            result.RequestHash,
            result.ReceiptId,
            result.Status,
            result.JobId,
            result.AttemptNo,
            result.LeaseOwner,
            result.LeaseExpiresAtUtc,
            result.FenceToken,
            sourcePinsSha256 = StatisticReconciliationCanonicalJson.HashObject(
                result.SourcePins),
            rowsSha256 = StatisticReconciliationCanonicalJson.HashObject(
                result.Rows),
            result.TotalRowCount,
            result.EqualRowCount,
            result.ChangedRowCount,
            result.ResultHash,
            result.IsCurrent,
            result.IsFresh,
            result.IsDirty,
            result.FailureCode,
            result.FailureMessage,
            result.CompletedAtUtc,
            result.ExpiresAtUtc
        });
    }

    internal static string ConfigurationBundleSha(
        string directP8ConfigBundleSha256,
        WorkReportStatisticRebuildJob direct,
        WorkAssignmentBasicSummarySnapshot basic,
        IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode> days,
        IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode> months,
        IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode> years,
        WorkReportStatisticDiffResult diff)
    {
        ArgumentNullException.ThrowIfNull(direct);
        ArgumentNullException.ThrowIfNull(basic);
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(months);
        ArgumentNullException.ThrowIfNull(years);
        ArgumentNullException.ThrowIfNull(diff);
        if (!Sha(directP8ConfigBundleSha256))
            throw new InvalidOperationException(
                "P10_ACTUAL_DIRECT_CONFIG_BUNDLE_INVALID");
        var advanced = days
            .Cast<WorkAssignmentAdvancedSummaryHierarchyNodeBase>()
            .Concat(months)
            .Concat(years)
            .OrderBy(row => row.Id, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "P10_ACTUAL_ADVANCED_CONFIG_MISSING");
        return StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_ACTUAL_CONFIGURATION_BUNDLE_V1",
            direct = new
            {
                direct.DynamicFormTemplateId,
                direct.ConfigId,
                direct.ConfigVersionId,
                direct.ConfigVersionNo,
                direct.ConfigRevision,
                direct.ConfigHash,
                directP8ConfigBundleSha256
            },
            basic = new
            {
                basic.ConfigId,
                basic.ConfigVersionId,
                basic.ConfigVersionNo,
                basic.ConfigRevision,
                basic.ConfigHash,
                dependencyPins = basic.ConfigDependencyPins
            },
            advanced = new
            {
                advanced.ConfigId,
                advanced.ConfigVersionId,
                advanced.ConfigVersionNo,
                advanced.ConfigRevision,
                advanced.ConfigHash,
                dependencyPins = advanced.DependencyPins
            },
            diff = new
            {
                diff.ConfigId,
                diff.ConfigVersionId,
                diff.ConfigVersionNo,
                diff.ConfigRevision,
                diff.ConfigHash,
                dependencyPins = diff.DependencyPins
            }
        });
    }
    internal static bool IsV4(
        StatisticReconciliationActualCapturePlan? plan)
        => StringComparer.Ordinal.Equals(
            plan?.SchemaVersion,
            StatisticReconciliationActualCapturePlanVersions.V4);

    internal static bool IsConfigured(string? disposition)
        => StringComparer.Ordinal.Equals(
            disposition,
            StatisticReconciliationActualSummaryTargetDispositions.Configured);

    internal static bool IsNotApplicable(string? disposition)
        => StringComparer.Ordinal.Equals(
            disposition,
            StatisticReconciliationActualSummaryTargetDispositions.NotApplicable);

    internal static string ApplicabilityProofSha(
        string family,
        string disposition,
        string p8ConfigurationOwnerId,
        string p8ConfigurationBundleSha256,
        string? immutableSelectorSha256)
    {
        if (family is not (BasicFamily or AdvancedFamily or DiffFamily) ||
            disposition is not (
                StatisticReconciliationActualSummaryTargetDispositions.Configured or
                StatisticReconciliationActualSummaryTargetDispositions.NotApplicable) ||
            !CanonicalObjectId(p8ConfigurationOwnerId) ||
            !Sha(p8ConfigurationBundleSha256) ||
            IsConfigured(disposition) != Sha(immutableSelectorSha256) ||
            IsNotApplicable(disposition) != (immutableSelectorSha256 is null))
        {
            throw new InvalidOperationException(
                "P10_ACTUAL_SUMMARY_APPLICABILITY_PROOF_INPUT_INVALID");
        }

        return StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_ACTUAL_SUMMARY_APPLICABILITY_PROOF_V1",
            family,
            disposition,
            p8ConfigurationOwnerId,
            p8ConfigurationBundleSha256,
            immutableSelectorSha256
        });
    }

    internal static string V4ConfigurationBundleSha(
        StatisticReconciliationActualCapturePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!IsV4(plan) ||
            !CanonicalObjectId(plan.P8ConfigurationOwnerId) ||
            !Sha(plan.P8ConfigurationBundleSha256) ||
            !Sha(plan.Basic?.ApplicabilityProofSha256) ||
            !Sha(plan.Advanced?.ApplicabilityProofSha256) ||
            !Sha(plan.Diff?.ApplicabilityProofSha256))
        {
            throw new InvalidOperationException(
                "P10_ACTUAL_CONFIGURATION_BUNDLE_V4_INPUT_INVALID");
        }

        return StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_ACTUAL_CONFIGURATION_BUNDLE_V4",
            p8ConfigurationOwnerId = plan.P8ConfigurationOwnerId,
            p8ConfigurationBundleSha256 = plan.P8ConfigurationBundleSha256,
            basicApplicabilityProofSha256 = plan.Basic.ApplicabilityProofSha256,
            advancedApplicabilityProofSha256 = plan.Advanced.ApplicabilityProofSha256,
            diffApplicabilityProofSha256 = plan.Diff.ApplicabilityProofSha256
        });
    }

    internal static string NotApplicableOwnerId(
        StatisticReconciliationActualCapturePlan plan,
        string family)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!IsV4(plan) ||
            family is not (BasicFamily or AdvancedFamily or DiffFamily) ||
            !CanonicalObjectId(plan.P8ConfigurationOwnerId))
        {
            throw new InvalidOperationException(
                "P10_ACTUAL_SUMMARY_NOT_APPLICABLE_OWNER_INVALID");
        }
        return $"NOT_APPLICABLE:{family}:{plan.P8ConfigurationOwnerId}";
    }

    internal static string NotApplicableCaptureSha(
        StatisticReconciliationActualCapturePlan plan,
        string family)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var proof = family switch
        {
            BasicFamily => plan.Basic?.ApplicabilityProofSha256,
            AdvancedFamily => plan.Advanced?.ApplicabilityProofSha256,
            DiffFamily => plan.Diff?.ApplicabilityProofSha256,
            _ => null
        };
        if (!IsV4(plan) || !Sha(plan.PlanSha256) ||
            !Sha(plan.P8ConfigurationBundleSha256) || !Sha(proof))
        {
            throw new InvalidOperationException(
                "P10_ACTUAL_SUMMARY_NOT_APPLICABLE_CAPTURE_INVALID");
        }
        return StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_ACTUAL_SUMMARY_NOT_APPLICABLE_CAPTURE_V1",
            planSha256 = plan.PlanSha256,
            family,
            applicabilityProofSha256 = proof,
            p8ConfigurationBundleSha256 = plan.P8ConfigurationBundleSha256
        });
    }

    private static bool SummaryTargets(
        StatisticReconciliationActualCapturePlan plan)
    {
        if (plan.Basic is null || plan.Advanced is null || plan.Diff is null)
            return false;
        if (!IsV4(plan))
        {
            return CanonicalObjectId(plan.Basic.SnapshotId) &&
                   BasicMode(plan.SchemaVersion, plan.Basic.Mode) &&
                   Sha(plan.Basic.ImmutableSelectorSha256) &&
                   Required(plan.Advanced.SectionId) &&
                   CanonicalIds(plan.Advanced.DayNodeIds) &&
                   CanonicalIds(plan.Advanced.MonthNodeIds) &&
                   CanonicalIds(plan.Advanced.YearNodeIds) &&
                   plan.Advanced.DayNodeIds.Count > 0 &&
                   plan.Advanced.MonthNodeIds.Count > 0 &&
                   plan.Advanced.YearNodeIds.Count > 0 &&
                   checked(plan.Advanced.DayNodeIds.Count +
                           plan.Advanced.MonthNodeIds.Count +
                           plan.Advanced.YearNodeIds.Count) is >= 1 and <= MaxExactNodeIds &&
                   Sha(plan.Advanced.ImmutableSelectorSha256) &&
                   CanonicalObjectId(plan.Diff.ResultId) &&
                   CanonicalObjectId(plan.Diff.RunId) &&
                   Sha(plan.Diff.ImmutableSelectorSha256);
        }

        if (!CanonicalObjectId(plan.P8ConfigurationOwnerId) ||
            !Sha(plan.P8ConfigurationBundleSha256))
            return false;
        return BasicV4(plan) && AdvancedV4(plan) && DiffV4(plan) &&
               StringComparer.Ordinal.Equals(
                   plan.ActualConfigurationBundleSha256,
                   V4ConfigurationBundleSha(plan));
    }

    private static bool BasicV4(StatisticReconciliationActualCapturePlan plan)
        => SummaryV4(
            plan,
            BasicFamily,
            plan.Basic.Disposition,
            plan.Basic.ApplicabilityProofSha256,
            plan.Basic.ImmutableSelectorSha256,
            configured: CanonicalObjectId(plan.Basic.SnapshotId) &&
                        BasicMode(plan.SchemaVersion, plan.Basic.Mode),
            notApplicable: Empty(plan.Basic.SnapshotId) && Empty(plan.Basic.Mode));

    private static bool AdvancedV4(StatisticReconciliationActualCapturePlan plan)
    {
        var idsValid = CanonicalIds(plan.Advanced.DayNodeIds) &&
                       CanonicalIds(plan.Advanced.MonthNodeIds) &&
                       CanonicalIds(plan.Advanced.YearNodeIds);
        var count = idsValid
            ? checked(plan.Advanced.DayNodeIds.Count +
                      plan.Advanced.MonthNodeIds.Count +
                      plan.Advanced.YearNodeIds.Count)
            : -1;
        return SummaryV4(
            plan,
            AdvancedFamily,
            plan.Advanced.Disposition,
            plan.Advanced.ApplicabilityProofSha256,
            plan.Advanced.ImmutableSelectorSha256,
            configured: Required(plan.Advanced.SectionId) && idsValid &&
                        plan.Advanced.DayNodeIds.Count > 0 &&
                        plan.Advanced.MonthNodeIds.Count > 0 &&
                        plan.Advanced.YearNodeIds.Count > 0 &&
                        count is >= 1 and <= MaxExactNodeIds,
            notApplicable: Empty(plan.Advanced.SectionId) && idsValid && count == 0);
    }

    private static bool DiffV4(StatisticReconciliationActualCapturePlan plan)
        => SummaryV4(
            plan,
            DiffFamily,
            plan.Diff.Disposition,
            plan.Diff.ApplicabilityProofSha256,
            plan.Diff.ImmutableSelectorSha256,
            configured: CanonicalObjectId(plan.Diff.ResultId) &&
                        CanonicalObjectId(plan.Diff.RunId),
            notApplicable: Empty(plan.Diff.ResultId) && Empty(plan.Diff.RunId));

    private static bool SummaryV4(
        StatisticReconciliationActualCapturePlan plan,
        string family,
        string? disposition,
        string? proof,
        string? selector,
        bool configured,
        bool notApplicable)
    {
        if (!Sha(proof))
            return false;
        var shape = IsConfigured(disposition)
            ? configured && Sha(selector)
            : IsNotApplicable(disposition) && notApplicable && selector is null;
        if (!shape)
            return false;
        return StringComparer.Ordinal.Equals(
            proof,
            ApplicabilityProofSha(
                family,
                disposition!,
                plan.P8ConfigurationOwnerId!,
                plan.P8ConfigurationBundleSha256!,
                selector));
    }

    private static bool SurfaceApplicability(
        StatisticReconciliationActualCapturePlan plan)
    {
        if (!IsV4(plan))
            return true;
        if ((plan.Api.Surface == "BASIC_SOURCE" &&
             !IsConfigured(plan.Basic.Disposition)) ||
            (plan.Api.Surface == "P9_DIFF" &&
             !IsConfigured(plan.Diff.Disposition)))
            return false;
        return plan.Export.ResultKind switch
        {
            "BASIC" or "FLOW" => IsConfigured(plan.Basic.Disposition),
            "ADVANCED" => IsConfigured(plan.Advanced.Disposition),
            "DIFF" => IsConfigured(plan.Diff.Disposition),
            _ => true
        };
    }

    private static bool Empty(string? value)
        => string.IsNullOrEmpty(value);
    private static object NodeSelector(
        WorkAssignmentAdvancedSummaryHierarchyNodeBase row)
    {
        if (row.WindowStartUtc.Kind != DateTimeKind.Utc ||
            row.WindowEndExclusiveUtc.Kind != DateTimeKind.Utc)
        {
            throw new InvalidOperationException(
                "P10_ACTUAL_ADVANCED_SELECTOR_TIME_INVALID");
        }
        return new
        {
            row.Id,
            row.Grain,
            row.GrainKey,
            windowStartUtc = row.WindowStartUtc.ToString("O"),
            windowEndExclusiveUtc = row.WindowEndExclusiveUtc.ToString("O"),
            row.Status,
            row.IsDirty,
            row.DirtyReason,
            row.SourceSignatureHash,
            row.SourceReportCount,
            sourceReportIds = row.SourceReportIds,
            inputNodeKeys = row.InputNodeKeys,
            valueJsonSha256 = StatisticReconciliationCanonicalJson.HashText(
                row.ValueJson ?? string.Empty),
            row.ValueHash,
            row.BuiltAtUtc,
            row.BuildJobId,
            row.BuildCorrelationId,
            row.BuildCommandId,
            row.BuildRequestHash,
            row.BuildReceiptId,
            row.BuildAttemptNo,
            row.LeaseOwner,
            row.LeaseExpiresAtUtc,
            row.FenceToken,
            row.BuildError
        };
    }

    private static bool SameAdvancedBoundary(
        WorkAssignmentAdvancedSummaryHierarchyNodeBase anchor,
        WorkAssignmentAdvancedSummaryHierarchyNodeBase row,
        string sectionId)
        => StringComparer.Ordinal.Equals(row.WorkId, anchor.WorkId) &&
           StringComparer.Ordinal.Equals(row.AssignmentId, anchor.AssignmentId) &&
           StringComparer.Ordinal.Equals(
               row.DynamicFormTemplateId,
               anchor.DynamicFormTemplateId) &&
           StringComparer.Ordinal.Equals(row.SectionId, sectionId) &&
           StringComparer.Ordinal.Equals(row.ConfigId, anchor.ConfigId) &&
           StringComparer.Ordinal.Equals(
               row.ConfigVersionId,
               anchor.ConfigVersionId) &&
           row.ConfigVersionNo == anchor.ConfigVersionNo &&
           row.ConfigRevision == anchor.ConfigRevision &&
           StringComparer.Ordinal.Equals(row.ConfigHash, anchor.ConfigHash) &&
           row.DependencyPins.SequenceEqual(
               anchor.DependencyPins,
               StringComparer.Ordinal) &&
           StringComparer.Ordinal.Equals(row.TimeAxis, anchor.TimeAxis) &&
           StringComparer.Ordinal.Equals(
               row.CandidateChainId,
               anchor.CandidateChainId) &&
           StringComparer.Ordinal.Equals(
               row.CandidatePromptId,
               anchor.CandidatePromptId) &&
           row.CandidateStage == anchor.CandidateStage &&
           StringComparer.Ordinal.Equals(
               row.CandidateCatalogRawSha256,
               anchor.CandidateCatalogRawSha256) &&
           StringComparer.Ordinal.Equals(
               row.CandidateCatalogSemanticSha256,
               anchor.CandidateCatalogSemanticSha256) &&
           StringComparer.Ordinal.Equals(
               row.CandidateStageLockSha256,
               anchor.CandidateStageLockSha256);

    private static bool CanonicalIds(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count > MaxExactNodeIds)
            return false;
        string? prior = null;
        foreach (var value in values)
        {
            if (!ObjectId.TryParse(value, out var parsed) ||
                !StringComparer.Ordinal.Equals(value, parsed.ToString()) ||
                (prior is not null &&
                 StringComparer.Ordinal.Compare(prior, value) >= 0))
            {
                return false;
            }
            prior = value;
        }
        return true;
    }

    private static bool PlanVersion(string? value)
        => value is StatisticReconciliationActualCapturePlanVersions.V1 or
            StatisticReconciliationActualCapturePlanVersions.V2 or
            StatisticReconciliationActualCapturePlanVersions.V3 or
            StatisticReconciliationActualCapturePlanVersions.V4;

    private static bool BasicMode(string? schemaVersion, string? value)
        => schemaVersion switch
        {
            StatisticReconciliationActualCapturePlanVersions.V1 =>
                value is "FLOW_BRANCH" or "FLOW_STEP" or
                    "FLOW_EFFECTIVE_PATH" or "FLOW_FINAL",
            StatisticReconciliationActualCapturePlanVersions.V2 or
            StatisticReconciliationActualCapturePlanVersions.V3 or
            StatisticReconciliationActualCapturePlanVersions.V4 =>
                value is "DIRECT_CHILDREN_OR_SELF" or "DIRECT_CHILDREN" or
                    "FLOW_BRANCH" or "FLOW_STEP" or
                    "FLOW_EFFECTIVE_PATH" or "FLOW_FINAL",
            _ => false
        };

    private static bool ExportResultKind(
        string? schemaVersion,
        string? value)
        => schemaVersion switch
        {
            StatisticReconciliationActualCapturePlanVersions.V1 or
            StatisticReconciliationActualCapturePlanVersions.V2 =>
                Required(value),
            StatisticReconciliationActualCapturePlanVersions.V3 or
            StatisticReconciliationActualCapturePlanVersions.V4 =>
                value is "DIRECT_FIELD" or "DIRECT_TABLE" or "DIRECT_LABEL" or
                    "BASIC" or "FLOW" or "ADVANCED" or "DIFF",
            _ => false
        };
    private static bool ExportFilter(string? schemaVersion, string? value)
        => schemaVersion switch
        {
            StatisticReconciliationActualCapturePlanVersions.V1 or
            StatisticReconciliationActualCapturePlanVersions.V2 =>
                value is null,
            StatisticReconciliationActualCapturePlanVersions.V3 or
            StatisticReconciliationActualCapturePlanVersions.V4 =>
                Sha(value),
            _ => false
        };
    private static bool ApiSurface(string? value)
        => value is "DIRECT_FIELD" or "DIRECT_TABLE" or "DIRECT_LABEL" or
            "BASIC_SOURCE" or "P9_DIFF";

    private static bool Sha(string? value)
        => StatisticReconciliationCanonicalJson.IsCanonicalSha256(value);

    private static bool CanonicalObjectId(string? value)
        => ObjectId.TryParse(value, out var parsed) &&
           StringComparer.Ordinal.Equals(value, parsed.ToString());

    private static bool Required(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Length <= 256 &&
           StringComparer.Ordinal.Equals(value, value.Trim()) &&
           !value.Any(char.IsControl);
}
