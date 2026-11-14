using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Trading;

namespace RAQM.Api.Controllers.Trading;
[ApiController, Authorize, Tags("AR/AP - Customers and suppliers")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/trading/parties")]
public sealed class PartiesController(ITradingSetupService service) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> Search(Guid orgId, Guid companyId, CancellationToken ct, [FromQuery] PartyKind? kind = null, [FromQuery] string? search = null, [FromQuery] bool? active = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) => Results.Ok(await service.Parties(CurrentUser.Id(User), orgId, companyId, kind, search, active, page, pageSize, ct));
    [HttpGet("{id:guid}")]
    public async Task<IResult> Get(Guid orgId, Guid companyId, Guid id, CancellationToken ct) => Results.Ok(await service.GetParty(CurrentUser.Id(User), orgId, companyId, id, ct));
    [HttpPost]
    public async Task<IResult> Create(Guid orgId, Guid companyId, PartyRequest r, CancellationToken ct)
    {
        var party = await service.CreateParty(CurrentUser.Id(User), orgId, companyId, r, ct);
        return Results.Created($"/api/v1/organizations/{orgId}/companies/{companyId}/trading/parties/{party.Id}", party);
    }

    [HttpPut("{id:guid}")]
    public async Task<IResult> Update(Guid orgId, Guid companyId, Guid id, UpdatePartyRequest r, CancellationToken ct) => Results.Ok(await service.UpdateParty(CurrentUser.Id(User), orgId, companyId, id, r, ct));
}
