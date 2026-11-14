using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.MultiCurrency;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Helpers;
using RAQM.Api.Services.ExchangeRates;
using RAQM.Api.Services.ForeignExchange;
using RAQM.Api.Services.MultiCurrency;

namespace RAQM.Api.Controllers.MultiCurrency;

[ApiController, Authorize, Tags("Foreign exchange")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/foreign-exchange")]
public sealed class ForeignExchangeController(FxSetup setup, ExchangeRateService rates, FxWorkflow workflow, FxReporting reports) : ControllerBase
{
    [HttpGet("settings")]
    public Task<object> Settings(Guid orgId, Guid companyId, CancellationToken ct) => setup.Settings(CurrentUser.Id(User), orgId, companyId, ct);

    [HttpPost("settings")]
    public async Task<IResult> Configure(Guid orgId, Guid companyId, FxConfigurationRequest request, CancellationToken ct) =>
        Results.Ok(await setup.Configure(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpPost("currencies")]
    public async Task<IResult> Enable(Guid orgId, Guid companyId, EnableCurrencyRequest request, CancellationToken ct) =>
        Results.Ok(await setup.Enable(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpGet("rates")]
    public async Task<IResult> Rates(Guid orgId, Guid companyId, CancellationToken ct, Guid? currency = null, FxRateType? type = null,
        DateOnly? date = null, int page = 1, int pageSize = 50) =>
        Results.Ok(await rates.List(CurrentUser.Id(User), orgId, companyId, currency, type, date, page, pageSize, ct));

    [HttpPost("rates")]
    public async Task<IResult> Rate(Guid orgId, Guid companyId, FxRateRequest request, CancellationToken ct) =>
        Results.Ok(await rates.Prepare(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpGet("rates/{id:guid}")]
    public Task<FxRate> RateHistory(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        rates.Get(CurrentUser.Id(User), orgId, companyId, id, ct);

    [HttpPost("rates/{id:guid}/approve")]
    public async Task<IResult> Approve(Guid orgId, Guid companyId, Guid id, FxAction request, CancellationToken ct) =>
        Results.Ok(await rates.Approve(CurrentUser.Id(User), orgId, companyId, id, request, ct));

    [HttpPost("rates/{id:guid}/cancel")]
    public async Task<IResult> Cancel(Guid orgId, Guid companyId, Guid id, FxAction request, CancellationToken ct) =>
        Results.Ok(await rates.Cancel(CurrentUser.Id(User), orgId, companyId, id, request, ct));

    [HttpPost("banks")]
    public async Task<IResult> Bank(Guid orgId, Guid companyId, FxBankRequest request, CancellationToken ct) =>
        Results.Ok(await setup.Bank(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpPost("banks/{id:guid}/statements")]
    public async Task<IResult> Statement(Guid orgId, Guid companyId, Guid id, FxBankStatementRequest request, CancellationToken ct) =>
        Results.Ok(await setup.Statement(CurrentUser.Id(User), orgId, companyId, id, request, ct));

    [HttpGet("operations")]
    public async Task<IResult> Operations(Guid orgId, Guid companyId, CancellationToken ct, FxKind? kind = null, Guid? party = null,
        int page = 1, int pageSize = 50) =>
        Results.Ok(await workflow.List(CurrentUser.Id(User), orgId, companyId, kind, party, page, pageSize, ct));

    [HttpPost("operations")]
    public async Task<IResult> Prepare(Guid orgId, Guid companyId, FxRequest request, CancellationToken ct) =>
        Results.Ok(await workflow.Prepare(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpGet("operations/{id:guid}/preview")]
    public Task<FxPlan> Preview(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        workflow.Preview(CurrentUser.Id(User), orgId, companyId, id, ct);

    [HttpPost("operations/{id:guid}/{workflowAction}")]
    public async Task<IResult> Change(Guid orgId, Guid companyId, Guid id, string workflowAction, FxAction request, CancellationToken ct) =>
        Results.Ok(await workflow.Change(CurrentUser.Id(User), orgId, companyId, id, workflowAction, request, ct));

    [HttpGet("report")]
    public Task<object> Report(Guid orgId, Guid companyId, DateOnly at, CancellationToken ct, DateOnly? from = null,
        DateOnly? compare = null, Guid? party = null, string? currency = null) =>
        reports.Report(CurrentUser.Id(User), orgId, companyId, at, from, compare, party, currency, ct);

    [HttpGet("audit/{id:guid}")]
    public async Task<IResult> Audit(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        Results.Ok(await reports.Audit(CurrentUser.Id(User), orgId, companyId, id, ct));
}
