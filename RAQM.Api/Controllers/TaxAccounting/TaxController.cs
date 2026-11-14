using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.TaxAccounting;
using RAQM.Api.Entities.RegulatoryDocuments;
using RAQM.Api.Entities.TaxAccounting;
using RAQM.Api.Helpers;
using RAQM.Api.Services.IncomeTax;
using RAQM.Api.Services.RegulatoryDocuments;
using RAQM.Api.Services.TaxAccounting;
using RAQM.Api.Services.TaxCalculation;
using RAQM.Api.Services.TaxJurisdictions;
using RAQM.Api.Services.TaxReporting;

namespace RAQM.Api.Controllers.TaxAccounting;

[ApiController, Authorize, Tags("Tax accounting")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/tax-accounting")]
public sealed class TaxController(TaxConfigurationService setup, TaxCalculationService calculation, TaxOperationService operations,
    TaxReportingService reporting, TaxInvoiceService invoices) : ControllerBase
{
    private Guid Actor => CurrentUser.Id(User);
    [HttpGet("settings")]
    public Task<object> Settings(Guid orgId, Guid companyId, Guid? registration, CancellationToken ct) => setup.List(Actor, orgId, companyId, registration, ct);

    [HttpPost("registrations")]
    public Task<TaxRegistration> Register(Guid orgId, Guid companyId, TaxRegistrationRequest input, CancellationToken ct) => setup.Register(Actor, orgId, companyId, input, ct);

    [HttpPost("registrations/{id:guid}/approve")]
    public Task<TaxRegistration> ApproveRegistration(Guid orgId, Guid companyId, Guid id, CancellationToken ct) => setup.ApproveRegistration(Actor, orgId, companyId, id, ct);

    [HttpPost("registrations/{id:guid}/controls")]
    public Task<TaxControl> Control(Guid orgId, Guid companyId, Guid id, TaxControlRequest input, CancellationToken ct) => setup.Control(Actor, orgId, companyId, id, input, ct);

    [HttpPost("registrations/{id:guid}/rules")]
    public Task<TaxRule> Rule(Guid orgId, Guid companyId, Guid id, TaxRuleRequest input, CancellationToken ct) => setup.Rule(Actor, orgId, companyId, id, input, ct);

    [HttpPost("registrations/{id:guid}/rules/{rule:guid}/approve")]
    public Task<TaxRule> ApproveRule(Guid orgId, Guid companyId, Guid id, Guid rule, CancellationToken ct) => setup.ApproveRule(Actor, orgId, companyId, id, rule, ct);

    [HttpPost("registrations/{id:guid}/preview")]
    public Task<TaxQuote> Preview(Guid orgId, Guid companyId, Guid id, TaxQuoteRequest input, CancellationToken ct) => calculation.Preview(Actor, orgId, companyId, id, input, ct);

    [HttpPost("registrations/{id:guid}/documents")]
    public Task<TaxCommercialResponse> Create(Guid orgId, Guid companyId, Guid id, TaxCommercialRequest input, CancellationToken ct) => calculation.Create(Actor, orgId, companyId, id, input, ct);

    [HttpGet("registrations/{id:guid}/operations")]
    public Task<IReadOnlyList<TaxOperation>> Operations(Guid orgId, Guid companyId, Guid id, CancellationToken ct, int page = 1, int pageSize = 50) =>
        operations.List(Actor, orgId, companyId, id, page, pageSize, ct);

    [HttpPost("registrations/{id:guid}/operations")]
    public Task<TaxOperation> PrepareOperation(Guid orgId, Guid companyId, Guid id, TaxOperationRequest input, CancellationToken ct) =>
        operations.Prepare(Actor, orgId, companyId, id, input, ct);

    [HttpPost("registrations/{id:guid}/operations/{operation:guid}/{workflowAction}")]
    public Task<TaxOperation> ChangeOperation(Guid orgId, Guid companyId, Guid id, Guid operation, string workflowAction, CancellationToken ct) =>
        operations.Change(Actor, orgId, companyId, id, operation, workflowAction, ct);
    [HttpPost("registrations/{id:guid}/deferred-preview")]
    public Task<DeferredMeasurement> DeferredPreview(Guid orgId, Guid companyId, Guid id, DeferredPreviewRequest input, CancellationToken ct) =>
        operations.DeferredPreview(Actor, orgId, companyId, id, input, ct);
    [HttpGet("registrations/{id:guid}/withholding/{operation:guid}/certificate")]
    public Task<object> WithholdingCertificate(Guid orgId, Guid companyId, Guid id, Guid operation, CancellationToken ct) =>
        operations.Certificate(Actor, orgId, companyId, id, operation, ct);

    [HttpGet("registrations/{id:guid}/report")]
    public Task<TaxReportSnapshot> Report(Guid orgId, Guid companyId, Guid id, DateOnly from, DateOnly to, CancellationToken ct) =>
        reporting.Report(Actor, orgId, companyId, id, from, to, ct);
    [HttpGet("registrations/{id:guid}/comparison")]
    public Task<object> Compare(Guid orgId, Guid companyId, Guid id, DateOnly from, DateOnly to, DateOnly priorFrom, DateOnly priorTo,
        CancellationToken ct) => reporting.Compare(Actor, orgId, companyId, id, from, to, priorFrom, priorTo, ct);

    [HttpGet("registrations/{id:guid}/periods")]
    public Task<object> Periods(Guid orgId, Guid companyId, Guid id, CancellationToken ct) => reporting.Periods(Actor, orgId, companyId, id, ct);

    [HttpPost("registrations/{id:guid}/periods")]
    public Task<TaxPeriod> Open(Guid orgId, Guid companyId, Guid id, TaxPeriodRequest input, CancellationToken ct) =>
        reporting.Open(Actor, orgId, companyId, id, input, ct);

    [HttpGet("registrations/{id:guid}/periods/{period:guid}/returns")]
    public Task<IReadOnlyList<TaxReturnVersion>> History(Guid orgId, Guid companyId, Guid id, Guid period, CancellationToken ct) =>
        reporting.History(Actor, orgId, companyId, id, period, ct);

    [HttpPost("registrations/{id:guid}/periods/{period:guid}/returns")]
    public Task<TaxReturnVersion> PrepareReturn(Guid orgId, Guid companyId, Guid id, Guid period, TaxReturnRequest input, CancellationToken ct) =>
        reporting.Prepare(Actor, orgId, companyId, id, period, input, ct);

    [HttpPost("registrations/{id:guid}/periods/{period:guid}/returns/{version:guid}/approve")]
    public Task<TaxReturnVersion> ApproveReturn(Guid orgId, Guid companyId, Guid id, Guid period, Guid version, CancellationToken ct) =>
        reporting.Approve(Actor, orgId, companyId, id, period, version, ct);

    [HttpGet("registrations/{id:guid}/periods/{period:guid}/returns/{version:guid}/export")]
    public async Task<IResult> Export(Guid orgId, Guid companyId, Guid id, Guid period, Guid version, CancellationToken ct) =>
        Results.Text(await reporting.Export(Actor, orgId, companyId, id, period, version, ct), "text/csv");

    [HttpGet("foreign")]
    public Task<object> Foreign(Guid orgId, Guid companyId, DateOnly from, DateOnly to, CancellationToken ct, int page = 1, int pageSize = 50) =>
        reporting.Foreign(Actor, orgId, companyId, from, to, page, pageSize, ct);

    [HttpPost("structured-invoices/source/{document:guid}")]
    public Task<RegulatoryDocument> Issue(Guid orgId, Guid companyId, Guid document, CancellationToken ct) => invoices.Create(Actor, orgId, companyId, document, ct);

    [HttpGet("structured-invoices/{id:guid}")]
    public Task<RegulatoryDocument> Invoice(Guid orgId, Guid companyId, Guid id, CancellationToken ct) => invoices.Get(Actor, orgId, companyId, id, ct);

    [HttpGet("structured-invoices/{id:guid}/export")]
    public async Task<IResult> InvoiceExport(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        Results.Text((await invoices.Get(Actor, orgId, companyId, id, ct)).ContentJson, "application/json");
}
