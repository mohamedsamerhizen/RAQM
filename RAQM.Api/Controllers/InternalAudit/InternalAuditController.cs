using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Governance;
using RAQM.Api.Entities.InternalControls;
using RAQM.Api.Helpers;
using RAQM.Api.Services.Governance;
using RAQM.Api.Services.InternalAudit;
using RAQM.Api.Services.InternalControls;

namespace RAQM.Api.Controllers.InternalAudit;

[ApiController, Authorize, Tags("Internal audit and controls")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/governance")]
public sealed class InternalAuditController(ControlService controls, InternalAuditService audit, GovernanceReporting reports) : ControllerBase
{
    private Guid Actor => CurrentUser.Id(User);
    [HttpGet("controls")]
    public Task<IReadOnlyList<InternalControl>> Controls(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        reports.List<InternalControl>(Actor, orgId, companyId, page, pageSize, ct);
    [HttpPost("controls")]
    public Task<InternalControl> Define(Guid orgId, Guid companyId, InternalControlInput input, CancellationToken ct) =>
        controls.Define(Actor, orgId, companyId, input, ct);
    [HttpPost("controls/{id:guid}/approve")]
    public Task<InternalControl> Approve(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        controls.Approve(Actor, orgId, companyId, id, ct);
    [HttpPost("controls/{id:guid}/evaluate")]
    public Task<ControlEvaluation> Evaluate(Guid orgId, Guid companyId, Guid id, EvaluateControlInput input, CancellationToken ct) =>
        controls.Evaluate(Actor, orgId, companyId, id, input, ct);
    [HttpGet("evaluations")]
    public Task<IReadOnlyList<ControlEvaluation>> Evaluations(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        reports.List<ControlEvaluation>(Actor, orgId, companyId, page, pageSize, ct);
    [HttpPost("evaluations/{id:guid}/review")]
    public Task<ControlEvaluation> Review(Guid orgId, Guid companyId, Guid id, ReviewControlInput input, CancellationToken ct) =>
        controls.Review(Actor, orgId, companyId, id, input, ct);
    [HttpGet("work")]
    public Task<IReadOnlyList<GovernanceWork>> Work(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        reports.List<GovernanceWork>(Actor, orgId, companyId, page, pageSize, ct);
    [HttpPost("work")]
    public Task<GovernanceWork> Prepare(Guid orgId, Guid companyId, WorkInput input, CancellationToken ct) =>
        audit.Create(Actor, orgId, companyId, input, ct);
    [HttpPost("work/{id:guid}/{workflowAction}")]
    public Task<GovernanceWork> Change(Guid orgId, Guid companyId, Guid id, string workflowAction, WorkAction input, CancellationToken ct) =>
        audit.Change(Actor, orgId, companyId, id, workflowAction, input, ct);
    [HttpGet("evidence")]
    public Task<IReadOnlyList<EvidenceReference>> Evidence(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        reports.List<EvidenceReference>(Actor, orgId, companyId, page, pageSize, ct);
    [HttpPost("evidence")]
    public Task<EvidenceReference> Link(Guid orgId, Guid companyId, EvidenceInput input, CancellationToken ct) =>
        audit.Evidence(Actor, orgId, companyId, input, ct);
}
