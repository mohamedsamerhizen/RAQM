using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Banking;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Banking;

namespace RAQM.Api.Controllers.Banking;

[ApiController, Authorize, Tags("Bank statements")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/banking/accounts/{bankId:guid}/statements")]
public sealed class StatementsController(IStatementService service) : ControllerBase
{
    [HttpPost("preview")]
    public async Task<IResult> Preview(Guid orgId, Guid companyId, Guid bankId, StatementRequest request, CancellationToken ct) =>
        Results.Ok(await service.Preview(CurrentUser.Id(User), orgId, companyId, bankId, request, ct));
    [HttpPost]
    public async Task<IResult> Import(Guid orgId, Guid companyId, Guid bankId, StatementRequest request, CancellationToken ct) =>
        Results.Ok(await service.Import(CurrentUser.Id(User), orgId, companyId, bankId, request, ct));
    [HttpGet]
    public async Task<IResult> List(Guid orgId, Guid companyId, Guid bankId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        Results.Ok(await service.List(CurrentUser.Id(User), orgId, companyId, bankId, page, pageSize, ct));
    [HttpGet("{statementId:guid}/lines")]
    public async Task<IResult> Lines(Guid orgId, Guid companyId, Guid bankId, Guid statementId, CancellationToken ct) =>
        Results.Ok(await service.Lines(CurrentUser.Id(User), orgId, companyId, bankId, statementId, ct));
}
