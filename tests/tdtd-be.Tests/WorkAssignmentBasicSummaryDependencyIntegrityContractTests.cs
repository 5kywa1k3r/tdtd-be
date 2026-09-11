using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

internal static class
    WorkAssignmentBasicSummaryDependencyIntegrityContractTests
{
    private const string FormId = "710000000000000000000001";
    private const string ConfigId = "710000000000000000000002";
    private const string VersionId = "710000000000000000000003";

    public static void Run()
    {
        TrustedModernViewValidatesPublishedSchemaAndCurrentState();
        TrustedModernViewRejectsEveryPartialOrTamperedState();
        ReopenedBasicOwnerAndRowLabelSourceContractsAreFrozen();
    }

    private static void
        TrustedModernViewValidatesPublishedSchemaAndCurrentState()
    {
        var owner = CreateTrustedModernOwner();
        var view = DynamicFormStatisticConfigCommandService
            .GetP804TrustedPersistedView(owner);

        AssertNotNull(view, "modern persisted statistic view");
        AssertEqual(ConfigId, view!.ConfigId, "modern config id");
        AssertEqual(VersionId, view.VersionId, "modern version id");
        AssertEqual(1, view.VersionNo, "modern version number");
        AssertEqual(1L, view.Revision, "modern revision");
        AssertEqual("LOCKED", view.Status, "published modern status");
        AssertEqual("[]", view.FieldSectionJson, "modern field section");
        AssertEqual("[]", view.TableSectionJson, "modern table section");
        AssertEqual(0, view.DependencyPins.Count, "modern dependency pins");
        AssertEqual(
            owner.StatisticConfigHash,
            view.ConfigHash,
            "modern recomputed config hash");
    }

    private static void
        TrustedModernViewRejectsEveryPartialOrTamperedState()
    {
        var publishedDrift = CreateTrustedModernOwner();
        publishedDrift.FieldsJson =
            "[{\"id\":\"late-field\",\"type\":\"number\"}]";
        ExpectException<InvalidOperationException>(
            () => DynamicFormStatisticConfigCommandService
                .GetP804TrustedPersistedView(publishedDrift),
            "published live schema drift");

        var partialIdentity = CreatePublishedOwner();
        partialIdentity.StatisticConfigId = ConfigId;
        ExpectCasReason(
            partialIdentity,
            "DYNAMIC_FORM_STATISTIC_CONFIG_IDENTITY_INTEGRITY");

        var partialSections = CreatePublishedOwner();
        partialSections.StatisticConfigSections = new()
        {
            FieldSectionJson = "[{}]",
            TableSectionJson = "[]"
        };
        ExpectCasReason(
            partialSections,
            "DYNAMIC_FORM_STATISTIC_CONFIG_IDENTITY_INTEGRITY");

        var missingCurrent = CreateTrustedModernOwner();
        missingCurrent.StatisticConfigSnapshots.Clear();
        ExpectCasReason(
            missingCurrent,
            "DYNAMIC_FORM_STATISTIC_CURRENT_SNAPSHOT_INTEGRITY");

        var duplicateCurrent = CreateTrustedModernOwner();
        duplicateCurrent.StatisticConfigSnapshots.Add(
            CloneSnapshot(duplicateCurrent.StatisticConfigSnapshots[0]));
        ExpectCasReason(
            duplicateCurrent,
            "DYNAMIC_FORM_STATISTIC_CURRENT_SNAPSHOT_INTEGRITY");

        var currentIdentityDrift = CreateTrustedModernOwner();
        currentIdentityDrift.StatisticConfigSnapshots[0].Revision++;
        ExpectCasReason(
            currentIdentityDrift,
            "DYNAMIC_FORM_STATISTIC_CURRENT_SNAPSHOT_INTEGRITY");

        var sectionDrift = CreateTrustedModernOwner();
        sectionDrift.StatisticConfigSnapshots[0].Sections = new()
        {
            FieldSectionJson = "[]",
            TableSectionJson = "[{}]"
        };
        ExpectCasReason(
            sectionDrift,
            "DYNAMIC_FORM_STATISTIC_CURRENT_SNAPSHOT_SECTIONS_INTEGRITY");

        var dependencyDrift = CreateTrustedModernOwner();
        dependencyDrift.StatisticConfigDependencyPins = ["forged-pin"];
        dependencyDrift.StatisticConfigSnapshots[0].DependencyPins =
            ["forged-pin"];
        ExpectCasReason(
            dependencyDrift,
            "DYNAMIC_FORM_STATISTIC_DEPENDENCY_PINS_INTEGRITY");

        var hashDrift = CreateTrustedModernOwner();
        var forgedHash = new string('a', 64);
        hashDrift.StatisticConfigHash = forgedHash;
        hashDrift.StatisticConfigSnapshots[0].ConfigHash = forgedHash;
        ExpectCasReason(
            hashDrift,
            "DYNAMIC_FORM_STATISTIC_CONFIG_HASH_INTEGRITY");

        var uppercaseIdentity = CreateTrustedModernOwner();
        uppercaseIdentity.StatisticConfigId =
            "ABCDEFABCDEFABCDEFABCDEF";
        ExpectCasReason(
            uppercaseIdentity,
            "DYNAMIC_FORM_STATISTIC_CONFIG_IDENTITY_INTEGRITY");

        var uppercaseHash = CreateTrustedModernOwner();
        uppercaseHash.StatisticConfigHash =
            uppercaseHash.StatisticConfigHash!.ToUpperInvariant();
        uppercaseHash.StatisticConfigSnapshots[0].ConfigHash =
            uppercaseHash.StatisticConfigHash;
        ExpectCasReason(
            uppercaseHash,
            "DYNAMIC_FORM_STATISTIC_CONFIG_IDENTITY_INTEGRITY");
    }

    private static void
        ReopenedBasicOwnerAndRowLabelSourceContractsAreFrozen()
    {
        var state = ReadBackendSource(
            "Services/WorkAssignments/BasicSummary/" +
            "WorkAssignmentBasicSummaryService.P804.Config.State.cs");
        var trustedView = ReadBackendSource(
            "Services/DynamicForms/" +
            "DynamicFormStatisticConfigCommandService.P804.TrustedView.cs");
        var labelService = ReadBackendSource(
            "Services/" +
            "LabelConfigCommandService.P804.TrustedView.cs");

        AssertContains(
            state,
            "if (entity is null)",
            "only an absent Basic owner is virtual");
        AssertNotContains(
            state,
            "if (entity is null ||",
            "persisted partial Basic owner cannot be virtual");
        AssertContains(
            state,
            "BASIC_SUMMARY_CONFIG_IDENTITY_INVALID",
            "persisted partial Basic owner fails integrity");

        foreach (var required in new[]
                 {
                     "GetP804TrustedPersistedView(template)",
                     "P804ResolveModernRowLabelSnapshots",
                     "P804ValidateModernRowLabelSnapshot",
                     "GetP804LabelVisibilityFilter(me)",
                     "ValidateP804TrustedActiveTableTarget",
                     "ValidateP804TrustedTableTargetSnapshot",
                     "BASIC_SUMMARY_ROW_LABEL_AMBIGUOUS"
                 })
        {
            AssertContains(state, required, $"ROW_LABEL dependency {required}");
        }

        AssertContains(
            trustedView,
            "DynamicFormPublishedSchemaSnapshotBuilder",
            "modern view validates published schema");
        AssertContains(
            trustedView,
            ".ValidateAgainstTemplate(owner)",
            "modern view validates live schema against snapshot");
        foreach (var required in new[]
                 {
                     "CURRENT_SNAPSHOT",
                     "CURRENT_SNAPSHOT_SECTIONS",
                     "DEPENDENCY_PINS",
                     "CONFIG_HASH",
                     "IsCanonicalObjectId",
                     "IsCanonicalSha256"
                 })
        {
            AssertContains(
                trustedView,
                required,
                $"modern trusted-view guard {required}");
        }
        AssertContains(
            labelService,
            "ValidateP804TrustedActiveTableTarget",
            "legacy label uses shared strong identity oracle");

        foreach (var required in new[]
                 {
                     "ValidateP804TrustedTableTargetSnapshot",
                     "VersionSnapshots",
                     "matches.Count != 1",
                     "snapshot.LabelId",
                     "snapshot.VersionId",
                     "snapshot.VersionNo",
                     "BASIC_SUMMARY_ROW_LABEL_HISTORICAL_SNAPSHOT_INVALID"
                 })
        {
            AssertContains(
                labelService,
                required,
                $"historical label guard {required}");
        }

        var modernOwnerSelection = Slice(
            state,
            "if (modernRowLabels is not null &&",
            "else if (modernRowLabels is null &&");
        AssertContains(
            modernOwnerSelection,
            "label => label.Id",
            "modern ROW_LABEL selects owner only by frozen LabelId");
        AssertNotContains(
            modernOwnerSelection,
            "label => label.Code",
            "modern ROW_LABEL never selects owner by live code");
        AssertNotContains(
            modernOwnerSelection,
            "label => label.VersionId",
            "modern ROW_LABEL never selects the current live version");
        AssertContains(
            state,
            "TableSectionJson",
            "modern ROW_LABEL reads the trusted table section");
        AssertContains(
            state,
            "RowLabelSnapshots",
            "modern ROW_LABEL binds a strong snapshot");
        AssertContains(
            state,
            ".ValidateP804TrustedTableTargetSnapshot(",
            "modern ROW_LABEL validates exact label history");

        var legacySelection = Slice(
            state,
            "else if (modernRowLabels is null &&",
            "var selectedLegacyLabels");
        AssertContains(
            legacySelection,
            "label => label.Code",
            "legacy ROW_LABEL selects by visible code");
        AssertContains(
            legacySelection,
            "GetP804LabelVisibilityFilter(me)",
            "legacy ROW_LABEL applies visibility before selection");
        AssertContains(
            legacySelection,
            ".GroupBy(label => label.Code",
            "legacy ROW_LABEL preserves ambiguity for exactly-one checks");
    }

    private static DynamicFormTemplate CreatePublishedOwner()
    {
        var owner = new DynamicFormTemplate
        {
            Id = FormId,
            SchemaVersion = 1,
            VersionNo = 1,
            IsPublished = true,
            SectionsJson = "[]",
            FieldsJson = "[]",
            BlocksJson = "[]",
            StatisticConfigSections = new()
            {
                FieldSectionJson = "[]",
                TableSectionJson = "[]"
            },
            StatisticConfigDependencyPins = new(),
            StatisticConfigSnapshots = new(),
            IsDeleted = false
        };
        var published =
            DynamicFormPublishedSchemaSnapshotBuilder.Build(owner);
        owner.PublishedSchemaSnapshotJson = published.Json;
        owner.PublishedSchemaHash = published.Sha256;
        return owner;
    }

    private static DynamicFormTemplate CreateTrustedModernOwner()
    {
        var owner = CreatePublishedOwner();
        const string fieldSection = "[]";
        const string tableSection = "[]";
        var pins = new List<string>();
        var hash = ComputeDynamicFormConfigHash(
            owner.Id,
            fieldSection,
            tableSection,
            pins);
        owner.StatisticConfigId = ConfigId;
        owner.StatisticConfigVersionId = VersionId;
        owner.StatisticConfigPreviousVersionId = null;
        owner.StatisticConfigVersionNo = 1;
        owner.StatisticConfigRevision = 1;
        owner.StatisticConfigStatus = "LOCKED";
        owner.StatisticConfigHash = hash;
        owner.StatisticConfigDependencyPins = pins;
        owner.StatisticConfigSections = new()
        {
            FieldSectionJson = fieldSection,
            TableSectionJson = tableSection
        };
        owner.StatisticConfigSnapshots =
        [
            new DynamicFormStatisticConfigVersionSnapshot
            {
                VersionId = VersionId,
                PreviousVersionId = null,
                VersionNo = 1,
                Revision = 1,
                Status = "LOCKED",
                ConfigHash = hash,
                DependencyPins = new(),
                Sections = new DynamicFormStatisticConfigSections
                {
                    FieldSectionJson = fieldSection,
                    TableSectionJson = tableSection
                },
                CreatedAtUtc = DateTime.UtcNow
            }
        ];
        return owner;
    }

    private static string ComputeDynamicFormConfigHash(
        string ownerId,
        string fieldSectionJson,
        string tableSectionJson,
        IReadOnlyList<string> dependencyPins)
    {
        using var fields = JsonDocument.Parse(fieldSectionJson);
        using var tables = JsonDocument.Parse(tableSectionJson);
        return StatConfigCanonicalJson.HashObject(new
        {
            ownerKind = StatConfigOwnerKinds.DynamicForm,
            ownerId,
            fieldConfig = fields.RootElement.Clone(),
            tableConfig = tables.RootElement.Clone(),
            dependencyPins
        });
    }

    private static DynamicFormStatisticConfigVersionSnapshot CloneSnapshot(
        DynamicFormStatisticConfigVersionSnapshot source)
        => new()
        {
            VersionId = source.VersionId,
            PreviousVersionId = source.PreviousVersionId,
            VersionNo = source.VersionNo,
            Revision = source.Revision,
            Status = source.Status,
            ConfigHash = source.ConfigHash,
            DependencyPins = source.DependencyPins.ToList(),
            Sections = new DynamicFormStatisticConfigSections
            {
                FieldSectionJson = source.Sections.FieldSectionJson,
                TableSectionJson = source.Sections.TableSectionJson
            },
            CreatedAtUtc = source.CreatedAtUtc,
            CreatedByUserId = source.CreatedByUserId
        };

    private static void ExpectCasReason(
        DynamicFormTemplate owner,
        string expectedReason)
    {
        try
        {
            _ = DynamicFormStatisticConfigCommandService
                .GetP804TrustedPersistedView(owner);
        }
        catch (AppException error)
        {
            AssertEqual(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                error.Code,
                $"{expectedReason} code");
            var details = JsonSerializer.Serialize(error.Details);
            AssertContains(details, expectedReason, expectedReason);
            return;
        }

        throw new InvalidOperationException(
            $"Expected {expectedReason}, but no AppException was thrown.");
    }

    private static void ExpectException<T>(Action action, string context)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException(
            $"{context}: expected {typeof(T).Name}.");
    }

    private static string ReadBackendSource(string relativePath)
    {
        var root = FindBackendRoot();
        var path = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Backend source file was not found: {path}");
        }
        return File.ReadAllText(path);
    }

    private static string FindBackendRoot()
    {
        var seeds = new[]
            {
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory
            }
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in seeds)
        {
            for (var directory = new DirectoryInfo(seed);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(
                        Path.Combine(directory.FullName, "tdtd-be.csproj")))
                {
                    return directory.FullName;
                }
                var nested = Path.Combine(directory.FullName, "tdtd-be");
                if (File.Exists(Path.Combine(nested, "tdtd-be.csproj")))
                {
                    return nested;
                }
            }
        }
        throw new InvalidOperationException(
            "Could not locate the tdtd-be source root.");
    }

    private static string Slice(
        string source,
        string start,
        string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        if (startIndex < 0)
            throw new InvalidOperationException($"Slice start '{start}' was not found.");
        var endIndex = source.IndexOf(
            end,
            startIndex + start.Length,
            StringComparison.Ordinal);
        if (endIndex < 0)
            throw new InvalidOperationException($"Slice end '{end}' was not found.");
        return source[startIndex..endIndex];
    }

    private static void AssertContains(
        string source,
        string expected,
        string context)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{context}: expected '{expected}'.");
        }
    }

    private static void AssertNotContains(
        string source,
        string forbidden,
        string context)
    {
        if (source.Contains(forbidden, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{context}: forbidden '{forbidden}'.");
        }
    }

    private static void AssertNotNull(object? value, string context)
    {
        if (value is null)
            throw new InvalidOperationException($"{context}: expected non-null.");
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
}
