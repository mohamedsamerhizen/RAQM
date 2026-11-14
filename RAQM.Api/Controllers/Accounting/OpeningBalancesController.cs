using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Accounting;

namespace RAQM.Api.Controllers.Accounting;
[ApiController, Authorize, Tags("Accounting - Opening balances")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/accounting/opening-balances")]
public sealed class OpeningBalancesController(IOpeningBalanceService service) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> Get(Guid orgId, Guid companyId, CancellationToken ct) => Results.Ok(await service.Get(CurrentUser.Id(User), orgId, companyId, ct));
    [HttpPost]
    public async Task<IResult> Create(Guid orgId, Guid companyId, OpeningBalanceRequest r, CancellationToken ct) => Results.Created($"/api/v1/organizations/{orgId}/companies/{companyId}/accounting/opening-balances", await service.Create(CurrentUser.Id(User), orgId, companyId, r, ct));
    [HttpPost("{id:guid}/post")]
    public async Task<IResult> Post(Guid orgId, Guid companyId, Guid id, PostJournalRequest r, CancellationToken ct) => Results.Ok(await service.Post(CurrentUser.Id(User), orgId, companyId, id, r, ct));
}
