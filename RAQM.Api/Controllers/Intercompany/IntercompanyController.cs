using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.CorporateGroups;
using RAQM.Api.Entities.Intercompany;
using RAQM.Api.Helpers;
using RAQM.Api.Services.Intercompany;

namespace RAQM.Api.Controllers.Intercompany;

[ApiController, Authorize, Tags("Intercompany accounting")]
[Route("api/v1/organizations/{orgId:guid}/corporate-groups/{groupId:guid}/intercompany")]
public sealed class IntercompanyController(IntercompanyService intercompany) : ControllerBase
{
    private Guid Actor => CurrentUser.Id(User);
    [HttpPost("partners")]
    public Task<IntercompanyPolicy> Register(Guid orgId, Guid groupId, PartnerRequest input, CancellationToken ct) =>
        intercompany.Register(Actor, orgId, groupId, input, ct);
    [HttpPost("partners/{id:guid}/approve")]
    public Task<IntercompanyPolicy> Approve(Guid orgId, Guid groupId, Guid id, CancellationToken ct) =>
        intercompany.ApprovePolicy(Actor, orgId, groupId, id, ct);
    [HttpPost("pairs")]
    public Task<IntercompanyPair> Prepare(Guid orgId, Guid groupId, PairRequest input, CancellationToken ct) =>
        intercompany.Prepare(Actor, orgId, groupId, input, ct);
    [HttpGet("pairs")]
    public Task<IReadOnlyList<IntercompanyPair>> List(Guid orgId, Guid groupId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        intercompany.List(Actor, orgId, groupId, page, pageSize, ct);
    [HttpPost("pairs/{id:guid}/{workflowAction}")]
    public Task<IntercompanyPair> Change(Guid orgId, Guid groupId, Guid id, string workflowAction, CancellationToken ct) =>
        intercompany.Change(Actor, orgId, groupId, id, workflowAction, ct);
    [HttpGet("partners/{id:guid}/reconciliation")]
    public Task<object> Reconcile(Guid orgId, Guid groupId, Guid id, DateOnly cutoff, CancellationToken ct) =>
        intercompany.Reconcile(Actor, orgId, groupId, id, cutoff, ct);
    [HttpPost("partners/{id:guid}/reconciliation")]
    public Task<ReciprocalReview> Finalize(Guid orgId, Guid groupId, Guid id, DateOnly cutoff, GroupEvidenceRequest input, CancellationToken ct) =>
        intercompany.Finalize(Actor, orgId, groupId, id, cutoff, input, ct);
}
