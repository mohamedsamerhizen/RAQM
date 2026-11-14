using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.CorporateGroups;
using RAQM.Api.Entities.Consolidation;
using RAQM.Api.Helpers;
using RAQM.Api.Services.Consolidation;
using RAQM.Api.Services.CurrencyTranslation;
using RAQM.Api.Services.GroupReporting;

namespace RAQM.Api.Controllers.Consolidation;

[ApiController, Authorize, Tags("Consolidation and group reporting")]
[Route("api/v1/organizations/{orgId:guid}/corporate-groups/{groupId:guid}/consolidation")]
public sealed class ConsolidationController(ConsolidationService consolidation, GroupRateService rates, GroupReportingService reports) : ControllerBase
{
    private Guid Actor => CurrentUser.Id(User);
    [HttpGet("rates")]
    public Task<IReadOnlyList<GroupTranslationRate>> Rates(Guid orgId, Guid groupId, Guid member, CancellationToken ct,
        int page = 1, int pageSize = 50) => rates.List(Actor, orgId, groupId, member, page, pageSize, ct);
    [HttpPost("rates")]
    public Task<GroupTranslationRate> Rate(Guid orgId, Guid groupId, TranslationRateRequest input, CancellationToken ct) =>
        rates.Prepare(Actor, orgId, groupId, input, ct);
    [HttpPost("rates/{id:guid}/approve")]
    public Task<GroupTranslationRate> ApproveRate(Guid orgId, Guid groupId, Guid id, CancellationToken ct) =>
        rates.Approve(Actor, orgId, groupId, id, ct);
    [HttpPost("runs")]
    public Task<ConsolidationRun> Prepare(Guid orgId, Guid groupId, GroupRunRequest input, CancellationToken ct) =>
        consolidation.Prepare(Actor, orgId, groupId, input, ct);
    [HttpPost("runs/{id:guid}/{workflowAction}")]
    public Task<ConsolidationRun> Change(Guid orgId, Guid groupId, Guid id, string workflowAction, CancellationToken ct) =>
        consolidation.Change(Actor, orgId, groupId, id, workflowAction, ct);
    [HttpGet("runs")]
    public Task<object> History(Guid orgId, Guid groupId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        reports.History(Actor, orgId, groupId, page, pageSize, ct);
    [HttpGet("runs/{id:guid}")]
    public Task<ConsolidationRun> Review(Guid orgId, Guid groupId, Guid id, CancellationToken ct) =>
        consolidation.Review(Actor, orgId, groupId, id, ct);
    [HttpGet("runs/{id:guid}/report")]
    public Task<object> Report(Guid orgId, Guid groupId, Guid id, CancellationToken ct, Guid? compare = null) =>
        reports.Report(Actor, orgId, groupId, id, compare, ct);
    [HttpGet("runs/{id:guid}/accounts/{account:guid}")]
    public Task<object> Drill(Guid orgId, Guid groupId, Guid id, Guid account, CancellationToken ct, int page = 1, int pageSize = 50) =>
        reports.Drill(Actor, orgId, groupId, id, account, page, pageSize, ct);
    [HttpGet("runs/{id:guid}/export")]
    public async Task<IResult> Export(Guid orgId, Guid groupId, Guid id, CancellationToken ct) =>
        Results.Text(await reports.Export(Actor, orgId, groupId, id, ct), "text/csv");
}
