using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Accounting;

namespace RAQM.Api.Controllers.Accounting;
[ApiController, Authorize, Tags("Accounting - Ledger")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/accounting")]
public sealed class LedgerController(ILedgerService service) : ControllerBase
{
    [HttpGet("accounts/{accountId:guid}/ledger")]
    public async Task<IResult> Account(Guid orgId, Guid companyId, Guid accountId, [FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) => Results.Ok(await service.AccountLedger(CurrentUser.Id(User), orgId, companyId, accountId, from, to, page, pageSize, ct));
    [HttpGet("trial-balance")]
    public async Task<IResult> TrialBalance(Guid orgId, Guid companyId, [FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct, [FromQuery] bool includeHierarchy = false) => Results.Ok(await service.TrialBalance(CurrentUser.Id(User), orgId, companyId, from, to, includeHierarchy, ct));
}
