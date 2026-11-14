using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Trading;

namespace RAQM.Api.Controllers.Trading;
[ApiController, Authorize, Tags("AR/AP - Invoices bills and credits")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/trading/documents")]
public sealed class TradeDocumentsController(ITradeDocumentService service) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> Search(Guid orgId, Guid companyId, CancellationToken ct, [FromQuery] Guid? party = null, [FromQuery] TradeKind? kind = null, [FromQuery] TradeState? state = null, [FromQuery] DateOnly? dueBefore = null, [FromQuery] string? search = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) => Results.Ok(await service.Search(CurrentUser.Id(User), orgId, companyId, party, kind, state, dueBefore, search, page, pageSize, ct));
    [HttpGet("{id:guid}")]
    public async Task<IResult> Get(Guid orgId, Guid companyId, Guid id, CancellationToken ct) => Results.Ok(await service.Get(CurrentUser.Id(User), orgId, companyId, id, ct));
    [HttpPost]
    public async Task<IResult> Create(Guid orgId, Guid companyId, TradeDocumentRequest r, CancellationToken ct)
    {
        var doc = await service.Create(CurrentUser.Id(User), orgId, companyId, r, ct);
        return Results.Created($"/api/v1/organizations/{orgId}/companies/{companyId}/trading/documents/{doc.Id}", doc);
    }

    [HttpPut("{id:guid}")]
    public async Task<IResult> Update(Guid orgId, Guid companyId, Guid id, UpdateTradeDocumentRequest r, CancellationToken ct) => Results.Ok(await service.Update(CurrentUser.Id(User), orgId, companyId, id, r, ct));
    [HttpPost("{id:guid}/approve")]
    public async Task<IResult> Approve(Guid orgId, Guid companyId, Guid id, DocumentTransitionRequest r, CancellationToken ct) => Results.Ok(await service.Transition(CurrentUser.Id(User), orgId, companyId, id, "approve", r, ct));
    [HttpPost("{id:guid}/reopen")]
    public async Task<IResult> Reopen(Guid orgId, Guid companyId, Guid id, DocumentTransitionRequest r, CancellationToken ct) => Results.Ok(await service.Transition(CurrentUser.Id(User), orgId, companyId, id, "reopen", r, ct));
    [HttpPost("{id:guid}/cancel")]
    public async Task<IResult> Cancel(Guid orgId, Guid companyId, Guid id, DocumentTransitionRequest r, CancellationToken ct) => Results.Ok(await service.Transition(CurrentUser.Id(User), orgId, companyId, id, "cancel", r, ct));
    [HttpPost("{id:guid}/post")]
    public async Task<IResult> Post(Guid orgId, Guid companyId, Guid id, PostTradeDocumentRequest r, CancellationToken ct) => Results.Ok(await service.Post(CurrentUser.Id(User), orgId, companyId, id, r, ct));
}
