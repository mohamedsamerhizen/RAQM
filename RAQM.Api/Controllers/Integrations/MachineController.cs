using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Integrations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Services.ApiAccess;
using RAQM.Api.Services.Automation;
using RAQM.Api.Services.DataExchange;

namespace RAQM.Api.Controllers.Integrations;

[ApiController, Authorize(AuthenticationSchemes = MachineIdentity.Scheme), EnableRateLimiting("integration"), RequestSizeLimit(100_000)]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/machine")]
public sealed class MachineController(RaqmDbContext db, MasterDataExchange exchange, LedgerExportJobs jobs, TimeProvider clock) : ControllerBase
{
    private MachineIdentity Identity => MachineIdentity.From(User);

    [HttpGet("invoices")]
    public async Task<object> Invoices(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50)
    {
        AccountingRules.Page(page, pageSize);
        await Identity.Require(db, orgId, companyId, "invoices.read", clock.GetUtcNow(), ct);
        return await db.TradeDocuments.Where(x => x.CompanyId == companyId && x.State == TradeState.Posted && x.Kind == TradeKind.Invoice)
            .OrderByDescending(x => x.PostingDate).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.Number, x.Currency, x.PostingDate, x.Total, x.JournalId }).ToArrayAsync(ct);
    }

    [HttpGet("suppliers")]
    public async Task<object> Suppliers(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50)
    {
        AccountingRules.Page(page, pageSize);
        await Identity.Require(db, orgId, companyId, "suppliers.read", clock.GetUtcNow(), ct);
        return await db.TradingParties.Where(x => x.CompanyId == companyId && x.Kind == PartyKind.Supplier)
            .OrderBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(x => new { x.Id, x.Code, x.Name, x.IsActive }).ToArrayAsync(ct);
    }

    [HttpPost("imports/{externalId}")]
    public Task<object> Import(Guid orgId, Guid companyId, string externalId, ImportRequest input, CancellationToken ct) =>
        exchange.Receive(Identity, orgId, companyId, externalId, input, ct);

    [HttpGet("exports/{id:guid}")]
    public async Task<IResult> Export(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        Results.Text(await jobs.Retrieve(Identity, orgId, companyId, id, ct), "text/csv");
}
