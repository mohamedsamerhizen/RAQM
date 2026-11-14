using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Helpers;
using RAQM.Api.Services.AnalyticalReporting;
using RAQM.Api.Services.CostAccounting;
using RAQM.Api.Services.FinancialDimensions;
using RAQM.Api.Services.ProjectAccounting;

namespace RAQM.Api.Controllers.FinancialDimensions;

[ApiController, Authorize, Tags("Financial dimensions and analytical accounting")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/dimensions")]
public sealed class DimensionsController(DimensionRegister register, DimensionBindings bindings, DimensionBudgetService budgets,
    ProjectService projects, CostAllocationService allocations, DimensionReporting reporting) : ControllerBase
{
    private Guid Actor => CurrentUser.Id(User);

    [HttpGet("settings")]
    public Task<object> Settings(Guid orgId, Guid companyId, CancellationToken ct) => register.Settings(Actor, orgId, companyId, ct);

    [HttpPost("definitions")]
    public async Task<IResult> Definition(Guid orgId, Guid companyId, DefinitionRequest request, CancellationToken ct) =>
        Results.Ok(await register.Definition(Actor, orgId, companyId, request, ct));

    [HttpGet("values")]
    public async Task<IResult> Values(Guid orgId, Guid companyId, CancellationToken ct, Guid? definition = null,
        string? search = null, int page = 1, int pageSize = 50) =>
        Results.Ok(await register.Values(Actor, orgId, companyId, definition, search, page, pageSize, ct));

    [HttpPost("values")]
    public async Task<IResult> Value(Guid orgId, Guid companyId, ValueRequest request, CancellationToken ct) =>
        Results.Ok(await register.Value(Actor, orgId, companyId, request, ct));

    [HttpPost("values/{id:guid}/deactivate")]
    public async Task<IResult> Deactivate(Guid orgId, Guid companyId, Guid id, DimensionDeactivate request, CancellationToken ct) =>
        Results.Ok(await register.Deactivate(Actor, orgId, companyId, id, request, ct));

    [HttpPost("rules")]
    public async Task<IResult> Rule(Guid orgId, Guid companyId, DimensionRuleRequest request, CancellationToken ct) =>
        Results.Ok(await register.Rule(Actor, orgId, companyId, request, ct));

    [HttpGet("sources/{source}/{id:guid}")]
    public async Task<IResult> BindingHistory(Guid orgId, Guid companyId, DimensionSource source, Guid id, CancellationToken ct) =>
        Results.Ok(await bindings.History(Actor, orgId, companyId, source, id, ct));

    [HttpPost("sources/{source}/{id:guid}")]
    public async Task<IResult> Bind(Guid orgId, Guid companyId, DimensionSource source, Guid id, DimensionBindingRequest request, CancellationToken ct) =>
        Results.Ok(await bindings.Attach(Actor, orgId, companyId, source, id, request, ct));

    [HttpGet("projects")]
    public async Task<IResult> Projects(Guid orgId, Guid companyId, CancellationToken ct, Guid? customer = null,
        string? search = null, int page = 1, int pageSize = 50) =>
        Results.Ok(await projects.List(Actor, orgId, companyId, customer, search, page, pageSize, ct));

    [HttpPost("projects")]
    public async Task<IResult> Project(Guid orgId, Guid companyId, ProjectRequest request, CancellationToken ct) =>
        Results.Ok(await projects.Create(Actor, orgId, companyId, request, ct));

    [HttpPost("projects/{id:guid}/state")]
    public async Task<IResult> ProjectState(Guid orgId, Guid companyId, Guid id, ProjectAction request, CancellationToken ct) =>
        Results.Ok(await projects.Change(Actor, orgId, companyId, id, request, ct));

    [HttpGet("projects/{id:guid}/report")]
    public Task<object> ProjectReport(Guid orgId, Guid companyId, Guid id, DateOnly at, CancellationToken ct) =>
        projects.Report(Actor, orgId, companyId, id, at, ct);

    [HttpPost("projects/{id:guid}/forecasts")]
    public async Task<IResult> Forecast(Guid orgId, Guid companyId, Guid id, ProjectForecastRequest request, CancellationToken ct) =>
        Results.Ok(await projects.Forecast(Actor, orgId, companyId, id, request, ct));

    [HttpPost("budget-versions/{id:guid}/slices")]
    public async Task<IResult> Slice(Guid orgId, Guid companyId, Guid id, BudgetSliceRequest request, CancellationToken ct) =>
        Results.Ok(await budgets.Add(Actor, orgId, companyId, id, request, ct));

    [HttpGet("budget-report")]
    public async Task<IResult> BudgetReport(Guid orgId, Guid companyId, Guid value, DateOnly cutoff, CancellationToken ct, Guid? version = null) =>
        Results.Ok(await budgets.Report(Actor, orgId, companyId, value, cutoff, version, ct));

    [HttpGet("allocations")]
    public async Task<IResult> Allocations(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        Results.Ok(await allocations.List(Actor, orgId, companyId, page, pageSize, ct));

    [HttpPost("allocations")]
    public async Task<IResult> Allocation(Guid orgId, Guid companyId, CostAllocationRequest request, CancellationToken ct) =>
        Results.Ok(await allocations.Prepare(Actor, orgId, companyId, request, ct));

    [HttpGet("allocations/{id:guid}/preview")]
    public Task<AllocationPlan> Preview(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        allocations.Preview(Actor, orgId, companyId, id, ct);

    [HttpPost("allocations/{id:guid}/{workflowAction}")]
    public async Task<IResult> ChangeAllocation(Guid orgId, Guid companyId, Guid id, string workflowAction, AllocationAction request, CancellationToken ct) =>
        Results.Ok(await allocations.Change(Actor, orgId, companyId, id, workflowAction, request, ct));

    [HttpGet("report")]
    public Task<object> Report(Guid orgId, Guid companyId, Guid definition, DateOnly from, DateOnly to, CancellationToken ct,
        Guid? secondary = null, Guid? value = null, bool unassigned = false, bool nonNominal = false, bool management = false,
        DateOnly? compareFrom = null, DateOnly? compareTo = null, int page = 1, int pageSize = 50) =>
        reporting.Report(Actor, orgId, companyId, definition, from, to, secondary, value, unassigned, nonNominal, management,
            compareFrom, compareTo, page, pageSize, ct);

    [HttpGet("audit/{id:guid}")]
    public async Task<IResult> Audit(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        Results.Ok(await register.Audit(Actor, orgId, companyId, id, ct));
}
