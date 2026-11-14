using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Treasury;

namespace RAQM.Api.Controllers.Treasury;

[ApiController, Authorize, Tags("Treasury inquiries")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/treasury")]
public sealed class TreasuryController(ITreasuryReportService service) : ControllerBase
{
    [HttpGet("position")]
    public async Task<IResult> Position(Guid orgId, Guid companyId, DateOnly from, DateOnly to, CancellationToken ct) =>
        Results.Ok(await service.Position(CurrentUser.Id(User), orgId, companyId, from, to, ct));
    [HttpGet("accounts/{id:guid}/activity")]
    public async Task<IResult> Activity(Guid orgId, Guid companyId, Guid id, DateOnly from, DateOnly to, CancellationToken ct, int page = 1, int pageSize = 50) =>
        Results.Ok(await service.Activity(CurrentUser.Id(User), orgId, companyId, id, from, to, page, pageSize, ct));
}
