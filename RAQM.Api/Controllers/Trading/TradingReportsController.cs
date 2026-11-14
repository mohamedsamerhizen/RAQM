using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Trading;

namespace RAQM.Api.Controllers.Trading;
[ApiController, Authorize, Tags("AR/AP - Statements aging reconciliation")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/trading")]
public sealed class TradingReportsController(ITradingReportService service) : ControllerBase
{
    [HttpGet("parties/{id:guid}/statement")]
    public async Task<IResult> Statement(Guid orgId, Guid companyId, Guid id, [FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct) => Results.Ok(await service.Statement(CurrentUser.Id(User), orgId, companyId, id, from, to, ct));
    [HttpGet("aging")]
    public async Task<IResult> Aging(Guid orgId, Guid companyId, [FromQuery] DateOnly asOf, CancellationToken ct, [FromQuery] PartyKind? kind = null) => Results.Ok(await service.Aging(CurrentUser.Id(User), orgId, companyId, kind, asOf, ct));
    [HttpGet("reconciliation")]
    public async Task<IResult> Reconcile(Guid orgId, Guid companyId, [FromQuery] DateOnly asOf, CancellationToken ct) => Results.Ok(await service.Reconcile(CurrentUser.Id(User), orgId, companyId, asOf, ct));
    [HttpGet("audit/{id:guid}")]
    public async Task<IResult> Audit(Guid orgId, Guid companyId, Guid id, CancellationToken ct) => Results.Ok(await service.Audit(CurrentUser.Id(User), orgId, companyId, id, ct));
}
