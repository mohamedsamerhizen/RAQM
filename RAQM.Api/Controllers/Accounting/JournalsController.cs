using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Accounting;

namespace RAQM.Api.Controllers.Accounting;
[ApiController, Authorize, Tags("Accounting - Journals")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/accounting/journals")]
public sealed class JournalsController(IJournalService service) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> Search(Guid orgId, Guid companyId, CancellationToken ct, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null, [FromQuery] JournalState? state = null, [FromQuery] string? search = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) => Results.Ok(await service.Search(CurrentUser.Id(User), orgId, companyId, from, to, state, search, page, pageSize, ct));
    [HttpGet("{id:guid}")]
    public async Task<IResult> Get(Guid orgId, Guid companyId, Guid id, CancellationToken ct) => Results.Ok(await service.Get(CurrentUser.Id(User), orgId, companyId, id, ct));
    [HttpPost]
    public async Task<IResult> Create(Guid orgId, Guid companyId, JournalRequest r, CancellationToken ct)
    {
        var j = await service.Create(CurrentUser.Id(User), orgId, companyId, r, ct);
        return Results.Created($"/api/v1/organizations/{orgId}/companies/{companyId}/accounting/journals/{j.Id}", j);
    }

    [HttpPut("{id:guid}")]
    public async Task<IResult> Update(Guid orgId, Guid companyId, Guid id, UpdateJournalRequest r, CancellationToken ct) => Results.Ok(await service.Update(CurrentUser.Id(User), orgId, companyId, id, r, ct));
    [HttpDelete("{id:guid}")]
    public async Task<IResult> Delete(Guid orgId, Guid companyId, Guid id, CancellationToken ct)
    {
        await service.Delete(CurrentUser.Id(User), orgId, companyId, id, ct);
        return Results.NoContent();
    }

    [HttpPost("{id:guid}/validate")]
    public async Task<IResult> Validate(Guid orgId, Guid companyId, Guid id, PostJournalRequest r, CancellationToken ct) => Results.Ok(await service.Validate(CurrentUser.Id(User), orgId, companyId, id, r, ct));
    [HttpPost("{id:guid}/post")]
    public async Task<IResult> Post(Guid orgId, Guid companyId, Guid id, PostJournalRequest r, CancellationToken ct) => Results.Ok(await service.Post(CurrentUser.Id(User), orgId, companyId, id, r, ct));
    [HttpPost("{id:guid}/reverse")]
    public async Task<IResult> Reverse(Guid orgId, Guid companyId, Guid id, ReverseJournalRequest r, CancellationToken ct) => Results.Ok(await service.Reverse(CurrentUser.Id(User), orgId, companyId, id, r, ct));
    [HttpGet("{id:guid}/audit")]
    public async Task<IResult> Audit(Guid orgId, Guid companyId, Guid id, CancellationToken ct) => Results.Ok(await service.Audit(CurrentUser.Id(User), orgId, companyId, id, ct));
}
