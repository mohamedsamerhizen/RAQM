using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Trading;

namespace RAQM.Api.Controllers.Trading;
[ApiController, Authorize, Tags("AR/AP - Configuration")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/trading")]
public sealed class TradingSetupController(ITradingSetupService service) : ControllerBase
{
    [HttpGet("configuration")]
    public async Task<IResult> Get(Guid orgId, Guid companyId, CancellationToken ct) => Results.Ok(await service.Configuration(CurrentUser.Id(User), orgId, companyId, ct));
    [HttpPut("configuration")]
    public async Task<IResult> Configure(Guid orgId, Guid companyId, ControlAccountRequest r, CancellationToken ct) => Results.Ok(await service.Configure(CurrentUser.Id(User), orgId, companyId, r, ct));
    [HttpGet("tax-codes")]
    public async Task<IResult> Codes(Guid orgId, Guid companyId, CancellationToken ct) => Results.Ok(await service.TaxCodes(CurrentUser.Id(User), orgId, companyId, ct));
    [HttpPost("tax-codes")]
    public async Task<IResult> CreateCode(Guid orgId, Guid companyId, TaxCodeRequest r, CancellationToken ct) => Results.Created($"/api/v1/organizations/{orgId}/companies/{companyId}/trading/tax-codes", await service.CreateTaxCode(CurrentUser.Id(User), orgId, companyId, r, ct));
    [HttpPut("tax-codes/{id:guid}")]
    public async Task<IResult> UpdateCode(Guid orgId, Guid companyId, Guid id, TaxCodeRequest r, CancellationToken ct) => Results.Ok(await service.UpdateTaxCode(CurrentUser.Id(User), orgId, companyId, id, r, ct));
    [HttpGet("tax-codes/{id:guid}/rates")]
    public async Task<IResult> Rates(Guid orgId, Guid companyId, Guid id, CancellationToken ct) => Results.Ok(await service.TaxRates(CurrentUser.Id(User), orgId, companyId, id, ct));
    [HttpPost("tax-codes/{id:guid}/rates")]
    public async Task<IResult> AddRate(Guid orgId, Guid companyId, Guid id, TaxRateRequest r, CancellationToken ct) => Results.Created($"/api/v1/organizations/{orgId}/companies/{companyId}/trading/tax-codes/{id}/rates", await service.AddTaxRate(CurrentUser.Id(User), orgId, companyId, id, r, ct));
}
