using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Inventory;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Helpers;
using RAQM.Api.Services.Inventory;

namespace RAQM.Api.Controllers.Inventory;

[ApiController, Authorize, Tags("Procurement and inventory")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/inventory")]
public sealed class InventoryController(InventoryRegister register, ProcurementService procurement, StockWorkflow workflow,
    InventoryReporting reporting) : ControllerBase
{
    [HttpGet("register")]
    public Task<object> Register(Guid orgId, Guid companyId, CancellationToken ct) =>
        register.Get(CurrentUser.Id(User), orgId, companyId, ct);

    [HttpPost("legacy-sales-lines/{id:guid}/confirm-non-stock")]
    public Task<Guid> ConfirmLegacyNonStock(Guid orgId, Guid companyId, Guid id, LegacySaleClassification input, CancellationToken ct) =>
        reporting.ConfirmLegacyNonStock(CurrentUser.Id(User), orgId, companyId, id, input.Reason, ct);

    [HttpPost("policy")]
    public async Task<IResult> Policy(Guid orgId, Guid companyId, InventoryPolicyRequest request, CancellationToken ct) =>
        Results.Ok(await register.Configure(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpPost("categories")]
    public async Task<IResult> Category(Guid orgId, Guid companyId, InventoryCategoryRequest request, CancellationToken ct) =>
        Results.Ok(await register.AddCategory(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpPost("items")]
    public async Task<IResult> Item(Guid orgId, Guid companyId, InventoryItemRequest request, CancellationToken ct) =>
        Results.Ok(await register.AddItem(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpGet("items")]
    public async Task<IResult> Items(Guid orgId, Guid companyId, CancellationToken ct, string? search = null,
        ItemKind? kind = null, bool? active = null, Guid? category = null, int page = 1, int pageSize = 50) =>
        Results.Ok(await register.Items(CurrentUser.Id(User), orgId, companyId, page, pageSize, search, kind, active, category, ct));

    [HttpPost("items/{id:guid}/status")]
    public async Task<IResult> ItemStatus(Guid orgId, Guid companyId, Guid id, InventoryStatusRequest request, CancellationToken ct) =>
        Results.Ok(new { IsActive = await register.SetStatus(CurrentUser.Id(User), orgId, companyId, id, false, request, ct) });

    [HttpPost("warehouses")]
    public async Task<IResult> Warehouse(Guid orgId, Guid companyId, WarehouseRequest request, CancellationToken ct) =>
        Results.Ok(await register.AddWarehouse(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpPost("warehouses/{id:guid}/status")]
    public async Task<IResult> WarehouseStatus(Guid orgId, Guid companyId, Guid id, InventoryStatusRequest request, CancellationToken ct) =>
        Results.Ok(new { IsActive = await register.SetStatus(CurrentUser.Id(User), orgId, companyId, id, true, request, ct) });

    [HttpGet("audit/{id:guid}")]
    public async Task<IResult> Audit(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        Results.Ok(await register.Audit(CurrentUser.Id(User), orgId, companyId, id, ct));

    [HttpGet("purchasing")]
    public async Task<IResult> Purchasing(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50,
        string? search = null, ProcurementState? state = null, bool? order = null) =>
        Results.Ok(await procurement.List(CurrentUser.Id(User), orgId, companyId, page, pageSize, search, state, order, ct));

    [HttpPost("purchasing")]
    public async Task<IResult> CreatePurchase(Guid orgId, Guid companyId, ProcurementRequest request, CancellationToken ct) =>
        Results.Ok(await procurement.Prepare(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpPost("purchasing/{id:guid}/{workflowAction}")]
    public async Task<IResult> PurchaseAction(Guid orgId, Guid companyId, Guid id, string workflowAction, InventoryAction request, CancellationToken ct) =>
        Results.Ok(await procurement.Change(CurrentUser.Id(User), orgId, companyId, id, workflowAction, request, ct));

    [HttpGet("purchasing/discrepancies")]
    public async Task<IResult> Discrepancies(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        Results.Ok(await procurement.Discrepancies(CurrentUser.Id(User), orgId, companyId, page, pageSize, ct));

    [HttpPost("purchasing/discrepancies")]
    public async Task<IResult> RecordDiscrepancy(Guid orgId, Guid companyId, DiscrepancyRequest request, CancellationToken ct) =>
        Results.Ok(await procurement.RecordDiscrepancy(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpPost("purchasing/discrepancies/{id:guid}/resolve")]
    public async Task<IResult> ResolveDiscrepancy(Guid orgId, Guid companyId, Guid id, DiscrepancyResolution request, CancellationToken ct) =>
        Results.Ok(await procurement.ResolveDiscrepancy(CurrentUser.Id(User), orgId, companyId, id, request, ct));

    [HttpGet("operations")]
    public async Task<IResult> Operations(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        Results.Ok(await workflow.List(CurrentUser.Id(User), orgId, companyId, page, pageSize, ct));

    [HttpPost("operations")]
    public async Task<IResult> CreateOperation(Guid orgId, Guid companyId, StockRequest request, CancellationToken ct) =>
        Results.Ok(await workflow.Prepare(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpGet("operations/{id:guid}/preview")]
    public async Task<IResult> Preview(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        Results.Ok(await workflow.Preview(CurrentUser.Id(User), orgId, companyId, id, ct));

    [HttpPost("operations/{id:guid}/{workflowAction}")]
    public async Task<IResult> OperationAction(Guid orgId, Guid companyId, Guid id, string workflowAction, InventoryAction request, CancellationToken ct) =>
        Results.Ok(await workflow.Change(CurrentUser.Id(User), orgId, companyId, id, workflowAction, request, ct));

    [HttpGet("report")]
    public Task<object> Report(Guid orgId, Guid companyId, DateOnly at, DateOnly? from, CancellationToken ct) =>
        reporting.Report(CurrentUser.Id(User), orgId, companyId, at, from, ct);
}
