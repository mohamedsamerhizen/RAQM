using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Accounting;

namespace RAQM.Api.Controllers.Accounting;
[ApiController, Authorize, Tags("Accounting - Fiscal periods")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/accounting")]
public sealed class FiscalPeriodsController(IFiscalPeriodService service) : ControllerBase
{
    [HttpGet("fiscal-years")]
    public async Task<IResult> List(Guid orgId, Guid companyId, CancellationToken ct) => Results.Ok(await service.List(CurrentUser.Id(User), orgId, companyId, ct));
    [HttpPost("fiscal-years")]
    public async Task<IResult> Create(Guid orgId, Guid companyId, FiscalYearRequest r, CancellationToken ct) => Results.Created($"/api/v1/organizations/{orgId}/companies/{companyId}/accounting/fiscal-years", await service.Create(CurrentUser.Id(User), orgId, companyId, r, ct));
    [HttpPut("periods/{id:guid}/state")]
    public async Task<IResult> State(Guid orgId, Guid companyId, Guid id, PeriodStateRequest r, CancellationToken ct) => Results.Ok(await service.ChangeState(CurrentUser.Id(User), orgId, companyId, id, r, ct));
}
