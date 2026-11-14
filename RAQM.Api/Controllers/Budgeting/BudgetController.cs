using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Budgeting;
using RAQM.Api.Entities.Budgeting;
using RAQM.Api.Helpers;
using RAQM.Api.Services.BudgetControl;
using RAQM.Api.Services.Budgeting;
using RAQM.Api.Services.FinancialAnalytics;

namespace RAQM.Api.Controllers.Budgeting;

[ApiController, Authorize, Tags("Budget and budgetary control")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/budgets")]
public sealed class BudgetController(BudgetService budgets, BudgetControlService control, BudgetReporting reporting) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> List(Guid orgId, Guid companyId, CancellationToken ct, Guid? year = null, BudgetKind? kind = null,
        string? search = null, int page = 1, int pageSize = 50) =>
        Results.Ok(await budgets.List(CurrentUser.Id(User), orgId, companyId, year, kind, search, page, pageSize, ct));

    [HttpPost]
    public async Task<IResult> Create(Guid orgId, Guid companyId, BudgetRequest request, CancellationToken ct) =>
        Results.Ok(await budgets.Create(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpGet("{id:guid}/versions")]
    public async Task<IResult> Versions(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        Results.Ok(await budgets.Versions(CurrentUser.Id(User), orgId, companyId, id, ct));

    [HttpPost("{id:guid}/versions")]
    public async Task<IResult> Prepare(Guid orgId, Guid companyId, Guid id, BudgetVersionRequest request, CancellationToken ct) =>
        Results.Ok(await budgets.Prepare(CurrentUser.Id(User), orgId, companyId, id, request, ct));

    [HttpPost("{id:guid}/transfers")]
    public async Task<IResult> Transfer(Guid orgId, Guid companyId, Guid id, BudgetTransferRequest request, CancellationToken ct) =>
        Results.Ok(await budgets.Transfer(CurrentUser.Id(User), orgId, companyId, id, request, ct));

    [HttpPost("versions/{id:guid}/{workflowAction}")]
    public async Task<IResult> Change(Guid orgId, Guid companyId, Guid id, string workflowAction, BudgetAction request, CancellationToken ct) =>
        Results.Ok(await budgets.Change(CurrentUser.Id(User), orgId, companyId, id, workflowAction, request, ct));

    [HttpGet("controls")]
    public async Task<IResult> Controls(Guid orgId, Guid companyId, CancellationToken ct) =>
        Results.Ok(await control.Settings(CurrentUser.Id(User), orgId, companyId, ct));

    [HttpPost("controls")]
    public async Task<IResult> Configure(Guid orgId, Guid companyId, PolicyRequest request, CancellationToken ct) =>
        Results.Ok(await control.Configure(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpGet("controls/{accountId:guid}/availability")]
    public Task<BudgetAvailability> Availability(Guid orgId, Guid companyId, Guid accountId, DateOnly cutoff, CancellationToken ct) =>
        control.Inquiry(CurrentUser.Id(User), orgId, companyId, accountId, cutoff, ct);

    [HttpPost("overrides")]
    public async Task<IResult> Override(Guid orgId, Guid companyId, OverrideRequest request, CancellationToken ct) =>
        Results.Ok(await control.Grant(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpGet("audit/{id:guid}")]
    public async Task<IResult> Audit(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        Results.Ok(await budgets.Audit(CurrentUser.Id(User), orgId, companyId, id, ct));

    [HttpGet("{id:guid}/report")]
    public Task<BudgetReport> Report(Guid orgId, Guid companyId, Guid id, DateOnly cutoff, CancellationToken ct,
        DateOnly? approvalAsOf = null, Guid? version = null, Guid? account = null) =>
        reporting.Report(CurrentUser.Id(User), orgId, companyId, id, cutoff, approvalAsOf, version, account, ct);

    [HttpGet("{id:guid}/export")]
    public async Task<IResult> Export(Guid orgId, Guid companyId, Guid id, DateOnly cutoff, CancellationToken ct,
        DateOnly? approvalAsOf = null, Guid? version = null) =>
        Results.File(BudgetReporting.Csv(await reporting.Report(CurrentUser.Id(User), orgId, companyId, id, cutoff, approvalAsOf, version, null, ct)),
            "text/csv; charset=utf-8", "budget-actual.csv");
}
