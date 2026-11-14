using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Governance;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Helpers;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Services.Governance;

namespace RAQM.Api.Controllers.Governance;

[ApiController, Authorize, Tags("Financial governance")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/governance")]
public sealed class GovernanceController(GovernanceAdministration admin, GovernanceApprovalService approvals, GovernanceReporting reports) : ControllerBase
{
    private Guid Actor => CurrentUser.Id(User);
    [HttpGet("overview")]
    public Task<object> Overview(Guid orgId, Guid companyId, CancellationToken ct) => reports.Overview(Actor, orgId, companyId, ct);
    [HttpGet("effective-access")]
    public Task<object> Effective(Guid orgId, Guid companyId, FinancialDuty duty, Guid? user, CancellationToken ct) =>
        admin.Effective(Actor, orgId, companyId, user, duty, ct);
    [HttpGet("policies")]
    public Task<IReadOnlyList<GovernancePolicy>> Policies(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        reports.List<GovernancePolicy>(Actor, orgId, companyId, page, pageSize, ct);
    [HttpPost("policies")]
    public Task<GovernancePolicy> Policy(Guid orgId, Guid companyId, PolicyInput input, CancellationToken ct) =>
        admin.Prepare(Actor, orgId, companyId, input, ct);
    [HttpGet("policies/{id:guid}/impact")]
    public Task<object> Impact(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        admin.PolicyImpact(Actor, orgId, companyId, id, ct);
    [HttpPost("policies/{id:guid}/approve")]
    public Task<GovernancePolicy> Approve(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        admin.Approve(Actor, orgId, companyId, id, ct);
    [HttpGet("grants")]
    public Task<IReadOnlyList<GovernanceGrant>> Grants(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        reports.List<GovernanceGrant>(Actor, orgId, companyId, page, pageSize, ct);
    [HttpPost("grants")]
    public Task<GovernanceGrant> Grant(Guid orgId, Guid companyId, GrantInput input, CancellationToken ct) =>
        admin.RequestGrant(Actor, orgId, companyId, input, ct);
    [HttpPost("grants/{id:guid}/{workflowAction}")]
    public Task<GovernanceGrant> ChangeGrant(Guid orgId, Guid companyId, Guid id, string workflowAction, CancellationToken ct) =>
        admin.GrantAction(Actor, orgId, companyId, id, workflowAction, ct);
    [HttpGet("delegations")]
    public Task<IReadOnlyList<GovernanceDelegation>> Delegations(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        reports.List<GovernanceDelegation>(Actor, orgId, companyId, page, pageSize, ct);
    [HttpPost("delegations")]
    public Task<GovernanceDelegation> Delegate(Guid orgId, Guid companyId, DelegationInput input, CancellationToken ct) =>
        admin.Delegate(Actor, orgId, companyId, input, ct);
    [HttpPost("delegations/{id:guid}/{workflowAction}")]
    public Task<GovernanceDelegation> ChangeDelegation(Guid orgId, Guid companyId, Guid id, string workflowAction, CancellationToken ct) =>
        admin.DelegationAction(Actor, orgId, companyId, id, workflowAction, ct);
    [HttpGet("authorizations")]
    public Task<IReadOnlyList<GovernanceAuthorization>> Authorizations(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        reports.List<GovernanceAuthorization>(Actor, orgId, companyId, page, pageSize, ct);
    [HttpPost("authorizations")]
    public Task<GovernanceAuthorization> AuthorizeSource(Guid orgId, Guid companyId, AuthorizationInput input, CancellationToken ct) =>
        approvals.Prepare(Actor, orgId, companyId, input, ct);
    [HttpPost("authorizations/journal-reversals/{id:guid}")]
    public Task<GovernanceAuthorization> AuthorizeReversal(Guid orgId, Guid companyId, Guid id,
        RAQM.Api.DTOs.Accounting.ReverseJournalRequest input, CancellationToken ct) =>
        approvals.PrepareReversal(Actor, orgId, companyId, id, input, ct);
    [HttpGet("authorizations/{id:guid}")]
    public Task<object> ViewAuthorization(Guid orgId, Guid companyId, Guid id, Guid? delegation, CancellationToken ct) =>
        approvals.View(Actor, orgId, companyId, id, delegation, ct);
    [HttpPost("authorizations/{id:guid}/review")]
    public Task<GovernanceReview> Review(Guid orgId, Guid companyId, Guid id, ReviewInput input, CancellationToken ct) =>
        approvals.Review(Actor, orgId, companyId, id, input, ct);
    [HttpPost("authorizations/{id:guid}/cancel")]
    public Task<GovernanceAuthorization> Cancel(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        approvals.Cancel(Actor, orgId, companyId, id, ct);
    [HttpPost("authorizations/{id:guid}/exception")]
    public Task<GovernanceException> Exception(Guid orgId, Guid companyId, Guid id, ExceptionInput input, CancellationToken ct) =>
        approvals.RequestException(Actor, orgId, companyId, id, input, ct);
    [HttpPost("exceptions/{id:guid}/{workflowAction}")]
    public Task<GovernanceException> ChangeException(Guid orgId, Guid companyId, Guid id, string workflowAction, CancellationToken ct) =>
        approvals.ExceptionAction(Actor, orgId, companyId, id, workflowAction, ct);
    [HttpGet("events")]
    public Task<object> Events(Guid orgId, Guid companyId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct,
        GovernanceOutcome? outcome = null, int page = 1, int pageSize = 50) =>
        reports.History(Actor, orgId, companyId, from, to, outcome, page, pageSize, ct);
    [HttpGet("events/verify")]
    public Task<object> Verify(Guid orgId, Guid companyId, CancellationToken ct, long first = 1, int size = 1000, string? anchor = null) =>
        reports.Verify(Actor, orgId, companyId, first, size, anchor, ct);
    [HttpGet("events/export")]
    public async Task<IResult> Export(Guid orgId, Guid companyId, CancellationToken ct, long first = 1, int size = 1000) =>
        Results.Text(await reports.Export(Actor, orgId, companyId, first, size, ct), "text/csv");
}
