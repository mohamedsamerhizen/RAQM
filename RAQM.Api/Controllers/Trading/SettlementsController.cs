using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Trading;

namespace RAQM.Api.Controllers.Trading;
[ApiController, Authorize, Tags("AR/AP - Settlements")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/trading")]
public sealed class SettlementsController(ISettlementService service) : ControllerBase
{
    [HttpGet("payments")]
    public async Task<IResult> List(Guid orgId, Guid companyId, CancellationToken ct, [FromQuery] Guid? party = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) => Results.Ok(await service.Payments(CurrentUser.Id(User), orgId, companyId, party, page, pageSize, ct));
    [HttpGet("payments/{id:guid}")]
    public async Task<IResult> Get(Guid orgId, Guid companyId, Guid id, CancellationToken ct) => Results.Ok(await service.GetPayment(CurrentUser.Id(User), orgId, companyId, id, ct));
    [HttpPost("payments")]
    public async Task<IResult> Pay(Guid orgId, Guid companyId, PaymentRequest r, CancellationToken ct) => Results.Ok(await service.ReceiveOrPay(CurrentUser.Id(User), orgId, companyId, r, ct));
    [HttpPost("refunds")]
    public async Task<IResult> Refund(Guid orgId, Guid companyId, RefundRequest r, CancellationToken ct) => Results.Ok(await service.Refund(CurrentUser.Id(User), orgId, companyId, r, ct));
    [HttpPost("payments/{id:guid}/reverse")]
    public async Task<IResult> Reverse(Guid orgId, Guid companyId, Guid id, ReversePaymentRequest r, CancellationToken ct) => Results.Ok(await service.ReversePayment(CurrentUser.Id(User), orgId, companyId, id, r, ct));
    [HttpGet("allocations")]
    public async Task<IResult> Allocations(Guid orgId, Guid companyId, CancellationToken ct, [FromQuery] Guid? party = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) => Results.Ok(await service.Allocations(CurrentUser.Id(User), orgId, companyId, party, page, pageSize, ct));
    [HttpPost("allocations")]
    public async Task<IResult> Allocate(Guid orgId, Guid companyId, AllocationRequest r, CancellationToken ct) => Results.Ok(await service.Allocate(CurrentUser.Id(User), orgId, companyId, r, ct));
    [HttpPost("allocations/{id:guid}/reverse")]
    public async Task<IResult> Deallocate(Guid orgId, Guid companyId, Guid id, ReverseAllocationRequest r, CancellationToken ct) => Results.Ok(await service.ReverseAllocation(CurrentUser.Id(User), orgId, companyId, id, r, ct));
}
