using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.FixedAssets;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.FixedAssets;

namespace RAQM.Api.Controllers.FixedAssets;

[ApiController, Authorize, Tags("Fixed assets and capital expenditure")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/fixed-assets")]
public sealed class FixedAssetsController(IAssetRegister register, IAssetWorkflow workflow) : ControllerBase
{
    [HttpGet("categories")]
    public async Task<IResult> Categories(Guid orgId, Guid companyId, CancellationToken ct) =>
        Results.Ok(await register.Categories(CurrentUser.Id(User), orgId, companyId, ct));

    [HttpPost("categories")]
    public async Task<IResult> CreateCategory(Guid orgId, Guid companyId, CategoryRequest request, CancellationToken ct) =>
        Results.Ok(await register.CreateCategory(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpPost("register")]
    public async Task<IResult> Register(Guid orgId, Guid companyId, RegisterAssetRequest request, CancellationToken ct) =>
        Results.Ok(await register.Register(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpGet("register")]
    public async Task<IResult> Search(Guid orgId, Guid companyId, DateOnly at, CancellationToken ct, Guid? category = null,
        AssetStatus? status = null, string? search = null, string? branch = null, int page = 1, int pageSize = 50) =>
        Results.Ok(await register.Search(CurrentUser.Id(User), orgId, companyId, at, category, status, search, branch, page, pageSize, ct));

    [HttpGet("register/{assetId:guid}/movements")]
    public async Task<IResult> Movements(Guid orgId, Guid companyId, Guid assetId, CancellationToken ct) =>
        Results.Ok(await register.Movements(CurrentUser.Id(User), orgId, companyId, assetId, ct));

    [HttpGet("register/{assetId:guid}/schedule")]
    public async Task<IResult> Schedule(Guid orgId, Guid companyId, Guid assetId, DateOnly at, DateOnly through, CancellationToken ct) =>
        Results.Ok(await register.Schedule(CurrentUser.Id(User), orgId, companyId, assetId, at, through, ct));

    [HttpGet("audit/{entityId:guid}")]
    public async Task<IResult> Audit(Guid orgId, Guid companyId, Guid entityId, CancellationToken ct) =>
        Results.Ok(await register.Audit(CurrentUser.Id(User), orgId, companyId, entityId, ct));

    [HttpGet("report")]
    public async Task<IResult> Report(Guid orgId, Guid companyId, CancellationToken ct, DateOnly? from = null, DateOnly? to = null, Guid? period = null) =>
        Results.Ok(await register.Report(CurrentUser.Id(User), orgId, companyId, from, to, period, ct));

    [HttpPost("operations")]
    public async Task<IResult> Prepare(Guid orgId, Guid companyId, AssetOperationRequest request, CancellationToken ct) =>
        Results.Ok(await workflow.Prepare(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpGet("operations")]
    public async Task<IResult> History(Guid orgId, Guid companyId, CancellationToken ct, Guid? asset = null,
        AssetOperationKind? kind = null, int page = 1, int pageSize = 50) =>
        Results.Ok(await workflow.History(CurrentUser.Id(User), orgId, companyId, asset, kind, page, pageSize, ct));

    [HttpGet("operations/{id:guid}/preview")]
    public async Task<IResult> Preview(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        Results.Ok(await workflow.Preview(CurrentUser.Id(User), orgId, companyId, id, ct));

    [HttpPost("operations/{id:guid}/{workflowAction}")]
    public async Task<IResult> Change(Guid orgId, Guid companyId, Guid id, string workflowAction, AssetAction request, CancellationToken ct) =>
        Results.Ok(await workflow.Change(CurrentUser.Id(User), orgId, companyId, id, workflowAction, request, ct));
}
