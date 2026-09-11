using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private string _excludeLockOriginalRoute = string.Empty;
    private JsonObject? _excludeLockOriginalRequest;

    private JsonObject RequireExcludeLockOriginalRequest()
        => _excludeLockOriginalRequest
           ?? throw new HarnessCaseNotRunnableException(
               "P8-07 original V_EXCLUDE lock request was not captured.");
}
