using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.SaaS;
using RAQM.Api.Entities.SaaS;
using RAQM.Api.Helpers;
using RAQM.Api.Services.FinancialReporting;
using RAQM.Api.Services.SaaS;

namespace RAQM.Api.Controllers.SaaS;

[ApiController, Authorize, RequestSizeLimit(16000)]
[Route("api/v1/saas")]
public sealed class CommercialCatalogController(CommercialService service) : ControllerBase
{
    [HttpGet("catalog")]
    public Task<CommercialPlan[]> Catalog(CancellationToken ct) => service.Catalog(ct);

    [HttpPost("provision")]
    public Task<CommercialAccount> Provision(TenantProvisionInput input, CancellationToken ct) =>
        service.Provision(CurrentUser.Id(User), input, ct);
}

[ApiController, Authorize, RequestSizeLimit(16000)]
[Route("api/v1/organizations/{orgId:guid}/commercial")]
public sealed class CommercialAccountController(CommercialService service, FinancialLedger ledger) : ControllerBase
{
    private Guid Actor => CurrentUser.Id(User);
    [HttpGet]
    public Task<object> Overview(Guid orgId, CancellationToken ct) => service.Overview(Actor, orgId, ct);

    [HttpPost("enroll")]
    public Task<CommercialAccount> Enroll(Guid orgId, LegacyEnrollmentInput input, CancellationToken ct) =>
        service.Enroll(Actor, orgId, input.PlanId, input.BillingContact, ct);

    [HttpGet("billing")]
    public Task<object> Billing(Guid orgId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        service.BillingHistory(Actor, orgId, page, pageSize, ct);

    [HttpGet("plan-quote")]
    public Task<BillingQuote> Quote(Guid orgId, Guid planId, bool immediate, CancellationToken ct) =>
        service.PreviewChange(Actor, orgId, planId, immediate, ct);

    [HttpPost("plan")]
    public Task<CommercialSubscription> ChangePlan(Guid orgId, CommercialPlanChange input, CancellationToken ct) =>
        service.ChangePlan(Actor, orgId, input, ct);

    [HttpPost("subscription")]
    public Task<CommercialSubscription> Subscription(Guid orgId, CommercialAction input, CancellationToken ct) =>
        service.ChangeSubscription(Actor, orgId, input, ct);

    [HttpGet("cases")]
    public Task<object> Cases(Guid orgId, CancellationToken ct) => service.Cases(Actor, orgId, ct);

    [HttpPost("cases")]
    public Task<CommercialCase> OpenCase(Guid orgId, CommercialCaseInput input, CancellationToken ct) =>
        service.OpenCase(Actor, orgId, input, ct);

    [HttpPost("portability")]
    public Task<object> Export(Guid orgId, PortabilityInput input, CancellationToken ct) => service.Export(Actor, orgId, input, ledger, ct);

    [HttpGet("portability/{id:guid}")]
    public async Task<IResult> Download(Guid orgId, Guid id, CancellationToken ct) =>
        Results.Text(await service.Download(Actor, orgId, id, ct), "text/csv");
}

[ApiController, Authorize, RequestSizeLimit(16000)]
[Route("api/v1/platform-commercial")]
public sealed class CommercialOperatorController(CommercialService service) : ControllerBase
{
    private Guid Actor => CurrentUser.Id(User);

    [HttpPost("plans")]
    public Task<CommercialPlan> Plan(CommercialPlanInput input, CancellationToken ct) => service.PreparePlan(Actor, input, ct);

    [HttpPost("plans/{id:guid}/publish")]
    public Task<CommercialPlan> Publish(Guid id, CancellationToken ct) => service.Publish(Actor, id, ct);

    [HttpGet("tenants")]
    public Task<object> Report(CancellationToken ct, int page = 1, int pageSize = 50) => service.OperatorReport(Actor, page, pageSize, ct);

    [HttpPost("tenants/{tenantId:guid}/operations")]
    public Task<CommercialAccount> Operate(Guid tenantId, TenantOperationInput input, CancellationToken ct) => service.Operate(Actor, tenantId, input, ct);

    [HttpPost("tenants/{tenantId:guid}/movements")]
    public Task<CommercialMovement> Movement(Guid tenantId, CommercialMovementInput input, CancellationToken ct) =>
        service.PrepareMovement(Actor, tenantId, input, ct);

    [HttpPost("tenants/{tenantId:guid}/movements/{id:guid}/verify")]
    public Task<CommercialMovement> Verify(Guid tenantId, Guid id, CancellationToken ct) => service.VerifyMovement(Actor, tenantId, id, ct);

    [HttpGet("tenants/{tenantId:guid}/cases")]
    public Task<object> Cases(Guid tenantId, CancellationToken ct) => service.OperatorCases(Actor, tenantId, ct);

    [HttpPost("tenants/{tenantId:guid}/cases/{id:guid}")]
    public Task<CommercialCase> ChangeCase(Guid tenantId, Guid id, CommercialCaseAction input, CancellationToken ct) =>
        service.ChangeCase(Actor, tenantId, id, input, ct);
}
