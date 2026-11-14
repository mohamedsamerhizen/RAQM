using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RAQM.Api.DTOs.FinancialReporting;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.FinancialReporting;

namespace RAQM.Api.Controllers.FinancialReporting;

[ApiController, Authorize, Tags("Financial reporting")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/financial-reporting")]
public sealed class FinancialReportsController(IFinancialReportService service) : ControllerBase
{
    [HttpGet("definitions")]
    public async Task<IResult> Definitions(Guid orgId, Guid companyId, CancellationToken ct) =>
        Results.Ok(await service.Definitions(CurrentUser.Id(User), orgId, companyId, ct));

    [HttpPost("definitions")]
    public async Task<IResult> Publish(Guid orgId, Guid companyId, DefinitionRequest request, CancellationToken ct) =>
        Results.Ok(await service.Publish(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpPost("query")]
    [EnableRateLimiting("financial-reports")]
    public async Task<IResult> Query(Guid orgId, Guid companyId, ReportQuery request, CancellationToken ct) =>
        Results.Ok(await service.Report(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpPost("accounts/{accountId:guid}/drill")]
    public async Task<IResult> Drill(Guid orgId, Guid companyId, Guid accountId, ReportQuery request, CancellationToken ct,
        bool ytd = false, int page = 1, int pageSize = 50) =>
        Results.Ok(await service.Drill(CurrentUser.Id(User), orgId, companyId, request, accountId, ytd, page, pageSize, ct));

    [HttpPost("export/{format}")]
    [EnableRateLimiting("financial-reports")]
    public async Task<IResult> Export(Guid orgId, Guid companyId, string format, ReportQuery request, CancellationToken ct)
    {
        var export = await service.Export(CurrentUser.Id(User), orgId, companyId, request, format, ct);
        return Results.File(export.Content, export.MediaType, export.FileName);
    }
}
