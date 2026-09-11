using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Production;

var results = new List<object>();
foreach (var action in new[] { "HEARTBEAT_ACTUAL_CONSUMER", "FAIL_ACTUAL_CONSUMER" })
{
    var service = DispatchProxy.Create<IStatisticReconciliationRunService, StrictBodyService>();
    var captured = (StrictBodyService)service;
    var identity = new StatisticReconciliationInternalWorkerIdentity(
        Options.Create(new StatisticReconciliationProductionWorkerOptions { WorkerId = "p11-body-regression" }));
    var runtime = new StatisticReconciliationProductionRuntime(null!, null!, service, null!, null!, identity,
        NullLogger<StatisticReconciliationProductionRuntime>.Instance);
    var lease = new StatisticReconciliationProductionLease("1234567890abcdef12345678", identity.WorkerId,
        "synthetic-claim-token", DateTime.UtcNow.AddMinutes(2), true);
    try
    {
        if (action.StartsWith("HEARTBEAT")) await runtime.HeartbeatAsync(lease, CancellationToken.None);
        else await runtime.FailTransientAsync(lease, "SYNTHETIC_RETRY", CancellationToken.None);
        if (captured.Calls != 1) throw new Exception("Expected one actual service call");
        results.Add(new { action, status = "PASS", keys = captured.Keys, error = (string?)null });
    }
    catch (Exception ex) { results.Add(new { action, status = "FAIL", keys = captured.Keys, error = ex.GetType().Name + ": " + ex.Message }); }
}
foreach (var body in new[] { "{\"WorkerId\":\"test\"}", "{\"workerId\":\"test\",\"extra\":true}" })
{
    var rejected = false;
    using var document = JsonDocument.Parse(body);
    try { StatisticReconciliationCanonicalJson.DeserializeStrict<StatisticReconciliationRecheckClaimRequest>(document.RootElement); }
    catch { rejected = true; }
    results.Add(new { action = "STRICT_BAD_CLAIM_REMAINS_REJECTED", status = rejected ? "PASS" : "FAIL", keys = Array.Empty<string>(), error = (string?)null });
}
var json = JsonSerializer.Serialize(new { schemaVersion = "P11_ACTUAL_RECHECK_BODY_REGRESSION_V1", results,
    actualRuntimeConsumersCalled = true, mockedSerializer = false, databaseOrNetworkUsed = false },
    new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
return json.Contains("\"FAIL\"") ? 1 : 0;

public class StrictBodyService : DispatchProxy
{
    public int Calls;
    public string[] Keys = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        Calls++;
        using var document = JsonDocument.Parse((Stream)args![1]!);
        Keys = document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();
        if (method!.Name == "HeartbeatRecheckAsync")
        {
            var request = StatisticReconciliationCanonicalJson.DeserializeStrict<StatisticReconciliationWorkerFenceRequest>(document.RootElement);
            if (request.WorkerId != "p11-body-regression" || request.ClaimToken != "synthetic-claim-token")
                throw new Exception("Heartbeat values drifted");
        }
        else if (method.Name == "FailRecheckAsync")
        {
            var request = StatisticReconciliationCanonicalJson.DeserializeStrict<StatisticReconciliationWorkerFailRequest>(document.RootElement);
            if (request.WorkerId != "p11-body-regression" || request.ClaimToken != "synthetic-claim-token" ||
                request.FailureCode != "SYNTHETIC_RETRY" || !request.Transient) throw new Exception("Failure values drifted");
        }
        else throw new Exception("Unexpected service call: " + method.Name);
        return Task.FromResult<StatisticReconciliationDetailResponse>(null!);
    }
}
