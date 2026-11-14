using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Banking;
using RAQM.Api.DTOs.CashManagement;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Banking;
using RAQM.Api.Interfaces.CashManagement;

namespace RAQM.Api.Controllers.Banking;

[ApiController, Authorize, Tags("Bank reconciliation")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/banking/accounts/{bankId:guid}/reconciliations")]
public sealed class ReconciliationController(IReconciliationService service, ICashManagementService cash) : ControllerBase
{
    [HttpPost("statements/{statementId:guid}")]
    public async Task<IResult> Start(Guid orgId, Guid companyId, Guid bankId, Guid statementId, CancellationToken ct) =>
        Results.Ok(await service.Start(CurrentUser.Id(User), orgId, companyId, bankId, statementId, ct));
    [HttpGet]
    public async Task<IResult> History(Guid orgId, Guid companyId, Guid bankId, CancellationToken ct) =>
        Results.Ok(await service.History(CurrentUser.Id(User), orgId, companyId, bankId, ct));
    [HttpGet("{id:guid}")]
    public async Task<IResult> Get(Guid orgId, Guid companyId, Guid bankId, Guid id, CancellationToken ct) =>
        Results.Ok(await service.Get(CurrentUser.Id(User), orgId, companyId, bankId, id, ct));
    [HttpGet("{id:guid}/matches")]
    public async Task<IResult> Matches(Guid orgId, Guid companyId, Guid bankId, Guid id, CancellationToken ct) =>
        Results.Ok(await service.Matches(CurrentUser.Id(User), orgId, companyId, bankId, id, ct));
    [HttpPost("{id:guid}/matches")]
    public async Task<IResult> Match(Guid orgId, Guid companyId, Guid bankId, Guid id, ConfirmMatch request, CancellationToken ct) =>
        Results.Ok(await service.Confirm(CurrentUser.Id(User), orgId, companyId, bankId, id, request, ct));
    [HttpPost("{id:guid}/matches/{matchId:guid}/release")]
    public async Task<IResult> Release(Guid orgId, Guid companyId, Guid bankId, Guid id, Guid matchId, ReviewAction request, CancellationToken ct) =>
        Results.Ok(await service.Release(CurrentUser.Id(User), orgId, companyId, bankId, id, matchId, request, ct));
    [HttpPost("{id:guid}/state/{transition}")]
    public async Task<IResult> Transition(Guid orgId, Guid companyId, Guid bankId, Guid id, string transition, ReviewAction request, CancellationToken ct) =>
        Results.Ok(await service.Transition(CurrentUser.Id(User), orgId, companyId, bankId, id, transition, request, ct));
    [HttpPost("{id:guid}/adjustments")]
    public async Task<IResult> Adjust(Guid orgId, Guid companyId, Guid bankId, Guid id, AdjustmentRequest request, CancellationToken ct) =>
        Results.Ok(await cash.Adjust(CurrentUser.Id(User), orgId, companyId, bankId, id, request, ct));
}
