using System.Text;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RequireP804SourceIsolationAsync(CancellationToken ct)
    {
        var serviceRoot = Path.Combine(
            _paths.BackendRoot,
            "Services",
            "WorkAssignments",
            "BasicSummary");
        var serviceFiles = Directory.EnumerateFiles(
                serviceRoot,
                "WorkAssignmentBasicSummaryService.P804.Config*.cs",
                SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        HarnessAssert.Equal(3, serviceFiles.Length,
            "P8-04 configuration partial set is incomplete or expanded unexpectedly");
        var serviceSources = serviceFiles.ToDictionary(
            path => path,
            File.ReadAllText,
            StringComparer.Ordinal);
        var forbiddenDependencies = new[]
        {
            "_payloadReader",
            "_backgroundJobs",
            "_ctx.WorkAssignmentReports",
            "_ctx.WorkAssignmentBasicSummarySnapshots",
            "GetSummaryAsync(",
            "RefreshSnapshotJobAsync(",
            "ResetSnapshotJobAsync("
        };
        foreach (var source in serviceSources)
        {
            foreach (var token in forbiddenDependencies)
            {
                HarnessAssert.True(
                    !source.Value.Contains(token, StringComparison.Ordinal),
                    $"P8-04 config partial {Path.GetFileName(source.Key)} references forbidden runtime dependency {token}");
            }
        }

        var combinedConfigSource = string.Join(
            "\n",
            serviceSources.Values);
        var requiredTrustedBindings = new[]
        {
            "GetP804TrustedPersistedView(",
            "GetP804LabelVisibilityFilter(",
            "BASIC_SUMMARY_ROW_LABEL_AMBIGUOUS"
        };
        foreach (var token in requiredTrustedBindings)
        {
            HarnessAssert.True(
                combinedConfigSource.Contains(
                    token,
                    StringComparison.Ordinal),
                $"P8-04 config partials lack trusted statistic/ROW_LABEL binding {token}");
        }

        var trustedViewPath = Path.Combine(
            _paths.BackendRoot,
            "Services",
            "DynamicForms",
            "DynamicFormStatisticConfigCommandService.P804.TrustedView.cs");
        HarnessAssert.True(
            File.Exists(trustedViewPath),
            "P8-04 trusted Dynamic Form statistic view partial is absent");
        var trustedViewSource = await File.ReadAllTextAsync(
            trustedViewPath,
            ct);
        foreach (var token in new[]
                 {
                     "ValidateAgainstTemplate(owner)",
                     "ValidateTrustedPersistedState(owner)",
                     "TableSectionJson"
                 })
        {
            HarnessAssert.True(
                trustedViewSource.Contains(token, StringComparison.Ordinal),
                $"P8-04 trusted statistic view lacks integrity binding {token}");
        }

        var controllerPath = Path.Combine(
            _paths.BackendRoot,
            "Controllers",
            "WorkAssignmentBasicSummaryController.cs");
        var controller = await File.ReadAllTextAsync(controllerPath, ct);
        var bindings = new[]
        {
            ("[HttpGet(\"assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config\")]", "GetP8ConfigAsync("),
            ("[HttpGet(\"assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config/versions\")]", "ListP8ConfigVersionsAsync("),
            ("[HttpGet(\"assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config/versions/{versionNo:int}\")]", "GetP8ConfigVersionAsync("),
            ("[HttpPut(\"assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config\")]", "PutP8ConfigAsync("),
            ("[HttpPost(\"assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config/lock\")]", "LockP8ConfigAsync("),
            ("[HttpPost(\"assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config/next-draft\")]", "CreateNextP8DraftAsync(")
        };
        foreach (var binding in bindings)
        {
            var start = controller.IndexOf(binding.Item1, StringComparison.Ordinal);
            HarnessAssert.True(start >= 0,
                $"P8-04 controller route is absent: {binding.Item1}");
            var next = controller.IndexOf("\n    [Http", start + binding.Item1.Length,
                StringComparison.Ordinal);
            var end = next >= 0 ? next : controller.Length;
            var action = controller[start..end];
            HarnessAssert.True(action.Contains(binding.Item2, StringComparison.Ordinal),
                $"P8-04 controller route does not bind {binding.Item2}");
            HarnessAssert.True(!action.Contains("GetSummaryAsync(", StringComparison.Ordinal),
                $"P8-04 config route leaked into summary executor: {binding.Item1}");
            HarnessAssert.True(!action.Contains("RefreshSnapshot", StringComparison.Ordinal),
                $"P8-04 config route leaked into snapshot refresh: {binding.Item1}");
        }

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-04-source-isolation.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                configurationPartialFiles = serviceSources.Select(item => new
                {
                    file = Path.GetRelativePath(_paths.BackendRoot, item.Key),
                    sha256 = Sha256(Encoding.UTF8.GetBytes(item.Value))
                }),
                forbiddenRuntimeDependencies = forbiddenDependencies,
                forbiddenRuntimeDependencyCount = 0,
                trustedStatisticView = new
                {
                    file = Path.GetRelativePath(
                        _paths.BackendRoot,
                        trustedViewPath),
                    sha256 = Sha256(Encoding.UTF8.GetBytes(
                        trustedViewSource)),
                    modernRowLabelSnapshotAuthoritative = true,
                    legacyVisibilityFilterBound = true,
                    legacyAmbiguityDeterministic = true
                },
                controllerBindings = bindings.Select(item => new
                {
                    route = item.Item1,
                    serviceMethod = item.Item2,
                    executorFree = true
                }),
                realApiAndMongoOraclesRemainPrimary = true
            },
            ct);
    }
}

