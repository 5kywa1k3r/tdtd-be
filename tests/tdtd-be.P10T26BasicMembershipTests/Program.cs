using Microsoft.AspNetCore.Http;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.WorkAssignments.BasicSummary;

var cases = new (string Id, Action Run)[]
{
    ("P10-T26-OWNER-BASIC-MEMBERSHIP-01", EligibleSourceAddedIsStale),
    ("P10-T26-OWNER-BASIC-REQUEST-02", MalformedRequestFailsClosed)
};

foreach (var test in cases)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Id}");
    }
    catch (Exception error)
    {
        Console.WriteLine(
            $"FAIL {test.Id} {error.GetType().Name}:{error.Message}");
        return 1;
    }
}

Console.WriteLine(
    "P10_T26_BASIC_MEMBERSHIP_OK cases=2 eligibleSourceAddedStale=true " +
    "malformedRequestClosed=true zeroWrite=true");
return 0;

static void EligibleSourceAddedIsStale()
{
    string[] frozenAssignments = [
        "507f1f77bcf86cd799439041"
    ];
    string[] frozenReports = [
        "507f1f77bcf86cd799439042"
    ];
    string[] currentAssignments = [
        "507f1f77bcf86cd799439043",
        "507f1f77bcf86cd799439041"
    ];

    ExpectStale(
        () => WorkAssignmentBasicSummaryService
            .P10ApiRequireExactSourceMembership(
                frozenAssignments,
                frozenReports,
                currentAssignments,
                frozenReports),
        "API_BASIC_SOURCE_MEMBERSHIP_DRIFT");
    ExpectStale(
        () => WorkAssignmentBasicSummaryService
            .P10ApiRequireExactSourceMembership(
                ["507F1F77BCF86CD799439041"],
                frozenReports,
                ["507F1F77BCF86CD799439041"],
                frozenReports),
        "API_BASIC_SOURCE_ASSIGNMENT_IDS_INVALID");

    var source = File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "tdtd-be",
        "Services",
        "WorkAssignments",
        "BasicSummary",
        "WorkAssignmentBasicSummaryService.P10ApiReadOwner.cs"));
    foreach (var forbidden in new[]
             {
                 "GetSummaryAsync(", "InsertOne", "UpdateOne", "UpdateMany",
                 "ReplaceOne", "DeleteOne", "FindOneAnd", "BulkWrite",
                 "Enqueue", "RefreshSnapshot", "MarkSnapshotDirty"
             })
    {
        Require(!source.Contains(forbidden, StringComparison.Ordinal),
            $"BASIC_MEMBERSHIP_ZERO_WRITE:{forbidden}");
    }
}

static void MalformedRequestFailsClosed()
{
    ExpectStale(
        () => WorkAssignmentBasicSummaryService
            .P10ApiRequireValidSnapshotRequestJson("{malformed"),
        "API_BASIC_REQUEST_JSON_INVALID");
    ExpectStale(
        () => WorkAssignmentBasicSummaryService
            .P10ApiRequireValidSnapshotRequestJson(
                "{\"scopeAssignmentId\":\"a\"," +
                "\"scopeAssignmentId\":\"b\"}"),
        "API_BASIC_REQUEST_JSON_INVALID");
}

static void ExpectStale(Action action, string reason)
{
    try
    {
        action();
    }
    catch (StatisticReconciliationActualApiEndpointException error)
    {
        Equal(StatusCodes.Status409Conflict, error.StatusCode,
            "BASIC_STALE_STATUS");
        Equal(reason, error.Reason, "BASIC_STALE_REASON");
        return;
    }

    throw new InvalidOperationException("BASIC_STALE_REQUIRED");
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (Directory.Exists(Path.Combine(directory.FullName, "tdtd-be")))
            return directory.FullName;
        directory = directory.Parent;
    }

    throw new InvalidOperationException("REPOSITORY_ROOT_NOT_FOUND");
}

static void Require(bool condition, string reason)
{
    if (!condition)
        throw new InvalidOperationException(reason);
}

static void Equal<T>(T expected, T actual, string reason)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{reason}:expected={expected};actual={actual}");
    }
}
