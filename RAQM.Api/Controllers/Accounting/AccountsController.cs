using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Accounting;

namespace RAQM.Api.Controllers.Accounting;
[ApiController, Authorize, Tags("Accounting - Accounts")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/accounting/accounts")]
public sealed class AccountsController(IAccountService service) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> Search(Guid orgId, Guid companyId, CancellationToken ct, [FromQuery] string? search = null, [FromQuery] AccountClass? classification = null, [FromQuery] bool? active = null, [FromQuery] bool? posting = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) => Results.Ok(await service.Search(CurrentUser.Id(User), orgId, companyId, search, classification, active, posting, page, pageSize, ct));
    [HttpGet("{id:guid}")]
    public async Task<IResult> Get(Guid orgId, Guid companyId, Guid id, CancellationToken ct) => Results.Ok(await service.Get(CurrentUser.Id(User), orgId, companyId, id, ct));
    [HttpPost]
    public async Task<IResult> Create(Guid orgId, Guid companyId, AccountRequest r, CancellationToken ct)
    {
        var account = await service.Create(CurrentUser.Id(User), orgId, companyId, r, ct);
        return Results.Created($"/api/v1/organizations/{orgId}/companies/{companyId}/accounting/accounts/{account.Id}", account);
    }

    [HttpPut("{id:guid}")]
    public async Task<IResult> Update(Guid orgId, Guid companyId, Guid id, UpdateAccountRequest r, CancellationToken ct) => Results.Ok(await service.Update(CurrentUser.Id(User), orgId, companyId, id, r, ct));
    [HttpPost("initialize")]
    public async Task<IResult> Initialize(Guid orgId, Guid companyId, InitializeAccountsRequest r, CancellationToken ct) => Results.Ok(await service.Initialize(CurrentUser.Id(User), orgId, companyId, r.Template, ct));
    [HttpGet("currency-policy")]
    public async Task<IResult> CurrencyPolicy(Guid orgId, Guid companyId, CancellationToken ct) => Results.Ok(await service.Policy(CurrentUser.Id(User), orgId, companyId, ct));
}
