using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.Services.WorkAssignments;

namespace tdtd_be.Controllers;

[ApiController, Authorize, Route("api/works/{workId}/assignment-forms")]
public sealed class AssignmentFormPickerController : ControllerBase
{
    private readonly IWorkAssignmentService _assignments;

    public AssignmentFormPickerController(IWorkAssignmentService assignments)
        => _assignments = assignments;

    // Read-only POST keeps Work, parent, search and selected ID in one scoped request.
    [HttpPost("query")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<AssignmentFormPickerResult> Query(
        [FromRoute] string workId,
        [FromBody] AssignmentFormPickerQueryRequest request,
        CancellationToken ct)
        => _assignments.QueryAssignmentFormsAsync(workId, request, ct);
}
