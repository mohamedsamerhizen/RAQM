using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.CashManagement;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.CashManagement;

namespace RAQM.Api.Controllers.CashManagement;

[ApiController, Authorize, Tags("Cash management")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/cash-management")]
public sealed class CashManagementController(ICashManagementService service) : ControllerBase
{
    [HttpPost("transfers")]
    public async Task<IResult> Prepare(Guid orgId, Guid companyId, TransferRequest request, CancellationToken ct) =>
        Results.Ok(await service.PrepareTransfer(CurrentUser.Id(User), orgId, companyId, request, ct));
    [HttpPost("transfers/{id:guid}/approve")]
    public async Task<IResult> Approve(Guid orgId, Guid companyId, Guid id, MovementAction request, CancellationToken ct) =>
        Results.Ok(await service.ApproveTransfer(CurrentUser.Id(User), orgId, companyId, id, request, ct));
    [HttpPost("movements")]
    public async Task<IResult> Record(Guid orgId, Guid companyId, CashMovementRequest request, CancellationToken ct) =>
        Results.Ok(await service.Record(CurrentUser.Id(User), orgId, companyId, request, ct));
    [HttpGet("movements")]
    public async Task<IResult> Movements(Guid orgId, Guid companyId, CancellationToken ct, Guid? account = null, int page = 1, int pageSize = 50) =>
        Results.Ok(await service.Movements(CurrentUser.Id(User), orgId, companyId, account, page, pageSize, ct));
    [HttpPost("movements/{id:guid}/reverse")]
    public async Task<IResult> Reverse(Guid orgId, Guid companyId, Guid id, MovementAction request, CancellationToken ct) =>
        Results.Ok(await service.Reverse(CurrentUser.Id(User), orgId, companyId, id, request, ct));
    [HttpPost("counts")]
    public async Task<IResult> Count(Guid orgId, Guid companyId, CashCountRequest request, CancellationToken ct) =>
        Results.Ok(await service.Count(CurrentUser.Id(User), orgId, companyId, request, ct));
    [HttpGet("accounts/{accountId:guid}/counts")]
    public async Task<IResult> Counts(Guid orgId, Guid companyId, Guid accountId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        Results.Ok(await service.Counts(CurrentUser.Id(User), orgId, companyId, accountId, page, pageSize, ct));
    [HttpPost("counts/{id:guid}/correct")]
    public async Task<IResult> Correct(Guid orgId, Guid companyId, Guid id, CountCorrectionRequest request, CancellationToken ct) =>
        Results.Ok(await service.Correct(CurrentUser.Id(User), orgId, companyId, id, request, ct));
}
