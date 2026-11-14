using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Forecasting;
using RAQM.Api.Helpers;
using RAQM.Api.Services.Forecasting;

namespace RAQM.Api.Controllers.Forecasting;

[ApiController, Authorize, Tags("Forecasts and financial scenarios")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/forecasts")]
public sealed class ForecastController(ForecastService forecasts) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> List(Guid orgId, Guid companyId, CancellationToken ct, string? code = null, bool? scenario = null,
        int page = 1, int pageSize = 50) =>
        Results.Ok(await forecasts.List(CurrentUser.Id(User), orgId, companyId, code, scenario, page, pageSize, ct));

    [HttpPost]
    public async Task<IResult> Prepare(Guid orgId, Guid companyId, ForecastRequest request, CancellationToken ct) =>
        Results.Ok(await forecasts.Prepare(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpPost("scenarios")]
    public async Task<IResult> Scenario(Guid orgId, Guid companyId, ScenarioRequest request, CancellationToken ct) =>
        Results.Ok(await forecasts.Scenario(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpPost("{id:guid}/{workflowAction}")]
    public async Task<IResult> Change(Guid orgId, Guid companyId, Guid id, string workflowAction, ForecastAction request, CancellationToken ct) =>
        Results.Ok(await forecasts.Change(CurrentUser.Id(User), orgId, companyId, id, workflowAction, request, ct));

    [HttpGet("{id:guid}/comparison")]
    public Task<object> Compare(Guid orgId, Guid companyId, Guid id, DateOnly cutoff, CancellationToken ct, Guid? compare = null) =>
        forecasts.Compare(CurrentUser.Id(User), orgId, companyId, id, cutoff, compare, ct);

    [HttpGet("{id:guid}/export")]
    public async Task<IResult> Export(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        Results.File(await forecasts.Export(CurrentUser.Id(User), orgId, companyId, id, ct), "text/csv; charset=utf-8", "forecast.csv");
}
