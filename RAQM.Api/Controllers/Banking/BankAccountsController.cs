using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Banking;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Banking;

namespace RAQM.Api.Controllers.Banking;

[ApiController, Authorize, Tags("Banking")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/banking")]
public sealed class BankAccountsController(IBankAccountService service) : ControllerBase
{
    [HttpGet("banks")]
    public async Task<IResult> Banks(Guid orgId, Guid companyId, CancellationToken ct) =>
        Results.Ok(await service.Banks(CurrentUser.Id(User), orgId, companyId, ct));
    [HttpPost("banks")]
    public async Task<IResult> Create(Guid orgId, Guid companyId, BankProfileRequest request, CancellationToken ct) =>
        Results.Ok(await service.CreateBank(CurrentUser.Id(User), orgId, companyId, request, ct));
    [HttpPut("banks/{id:guid}")]
    public async Task<IResult> ChangeBank(Guid orgId, Guid companyId, Guid id, AccountChange request, CancellationToken ct) =>
        Results.Ok(await service.ChangeBank(CurrentUser.Id(User), orgId, companyId, id, request, ct));
    [HttpGet("accounts")]
    public async Task<IResult> Search(Guid orgId, Guid companyId, CancellationToken ct, TreasuryAccountKind? kind = null, bool? active = null,
        string? search = null, int page = 1, int pageSize = 50) =>
        Results.Ok(await service.Search(CurrentUser.Id(User), orgId, companyId, kind, active, search, page, pageSize, ct));
    [HttpPost("accounts")]
    public async Task<IResult> Register(Guid orgId, Guid companyId, TreasuryAccountRequest request, CancellationToken ct) =>
        Results.Ok(await service.Register(CurrentUser.Id(User), orgId, companyId, request, ct));
    [HttpPut("accounts/{id:guid}")]
    public async Task<IResult> Change(Guid orgId, Guid companyId, Guid id, AccountChange request, CancellationToken ct) =>
        Results.Ok(await service.Change(CurrentUser.Id(User), orgId, companyId, id, request, ct));
    [HttpGet("audit/{id:guid}")]
    public async Task<IResult> Audit(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        Results.Ok(await service.Audit(CurrentUser.Id(User), orgId, companyId, id, ct));
}
