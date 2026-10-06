using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.Services.WorkAssignments.Review;

namespace tdtd_be.Controllers;

[ApiController, Authorize, Route("api/work-assignment-review/reports")]
public sealed class WorkReportReviewActionsController(MongoDbContext db, MeAccessor me) : ControllerBase
{
    [HttpGet("{reportId}/actions")]
    public async Task<ActionResult<ReviewActionContext>> Read(string reportId, CancellationToken ct)
        => Ok(await WorkReportReviewActionReader.ReadAsync(db, reportId, me.RequireMe(), ct));
}
