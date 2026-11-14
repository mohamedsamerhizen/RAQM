using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Integrations;
using RAQM.Api.Entities.Integrations;
using RAQM.Api.Helpers;
using RAQM.Api.Services.Automation;
using RAQM.Api.Services.DataExchange;
using RAQM.Api.Services.Integrations;

namespace RAQM.Api.Controllers.Integrations;

[ApiController, Authorize, RequestSizeLimit(100_000)]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/integrations")]
public sealed class IntegrationsController(IntegrationAdministration admin, IntegrationOperations operations,
    MasterDataExchange exchange, LedgerExportJobs jobs) : ControllerBase
{
    private Guid Actor => CurrentUser.Id(User);
    [HttpGet("applications")]
    public Task<object> Applications(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        admin.List(Actor, orgId, companyId, page, pageSize, ct);
    [HttpPost("applications")]
    public Task<IntegrationApplication> Create(Guid orgId, Guid companyId, ApplicationRequest input, CancellationToken ct) =>
        admin.Create(Actor, orgId, companyId, input, ct);
    [HttpPost("applications/{id:guid}/{workflowAction}")]
    public Task<IntegrationApplication> Change(Guid orgId, Guid companyId, Guid id, string workflowAction, CancellationToken ct) =>
        admin.Change(Actor, orgId, companyId, id, workflowAction, ct);
    [HttpPost("applications/{id:guid}/credentials")]
    public Task<CredentialIssue> Issue(Guid orgId, Guid companyId, Guid id, CredentialRequest input, CancellationToken ct) =>
        admin.Issue(Actor, orgId, companyId, id, input, ct);
    [HttpDelete("applications/{id:guid}/credentials/{key:guid}")]
    public Task<bool> Revoke(Guid orgId, Guid companyId, Guid id, Guid key, CancellationToken ct) =>
        admin.RevokeKey(Actor, orgId, companyId, id, key, ct);
    [HttpPost("applications/{id:guid}/webhooks")]
    public Task<WebhookSecret> Subscribe(Guid orgId, Guid companyId, Guid id, WebhookRequest input, CancellationToken ct) =>
        admin.Subscribe(Actor, orgId, companyId, id, input, ct);
    [HttpPost("webhooks/{id:guid}/rotate")]
    public Task<WebhookSecret> Rotate(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        admin.RotateWebhook(Actor, orgId, companyId, id, ct);
    [HttpPut("webhooks/{id:guid}")]
    public Task<bool> Webhook(Guid orgId, Guid companyId, Guid id, [FromBody] bool active, CancellationToken ct) =>
        admin.ChangeWebhook(Actor, orgId, companyId, id, active, ct);
    [HttpGet("status")]
    public Task<object> Status(Guid orgId, Guid companyId, CancellationToken ct) => operations.Status(Actor, orgId, companyId, ct);
    [HttpGet("events")]
    public Task<object> Events(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        operations.Events(Actor, orgId, companyId, page, pageSize, ct);
    [HttpPost("deliveries/{id:guid}/replay")]
    public Task<bool> Replay(Guid orgId, Guid companyId, Guid id, CancellationToken ct) => operations.Replay(Actor, orgId, companyId, id, ct);
    [HttpGet("imports/{id:guid}")]
    public Task<object> Inspect(Guid orgId, Guid companyId, Guid id, CancellationToken ct) => exchange.Inspect(Actor, orgId, companyId, id, ct);
    [HttpPost("imports/{id:guid}/{workflowAction}")]
    public Task<object> Process(Guid orgId, Guid companyId, Guid id, string workflowAction, CancellationToken ct) =>
        exchange.Change(Actor, orgId, companyId, id, workflowAction, ct);
    [HttpPost("applications/{id:guid}/schedules")]
    public Task<ExportSchedule> Schedule(Guid orgId, Guid companyId, Guid id, IntegrationExportScheduleRequest input, CancellationToken ct) =>
        jobs.Prepare(Actor, orgId, companyId, id, input, ct);
    [HttpPut("schedules/{id:guid}/{workflowAction}")]
    public Task<ExportSchedule> ChangeSchedule(Guid orgId, Guid companyId, Guid id, string workflowAction, CancellationToken ct) =>
        jobs.Change(Actor, orgId, companyId, id, workflowAction, ct);
    [HttpPost("schedules/{id:guid}/run")]
    public Task<Guid> ManualRun(Guid orgId, Guid companyId, Guid id, [FromBody] DateTimeOffset slot, CancellationToken ct) =>
        jobs.Manual(Actor, orgId, companyId, id, slot, ct);
    [HttpGet("schedules")]
    public Task<object> Schedules(Guid orgId, Guid companyId, CancellationToken ct) => jobs.Status(Actor, orgId, companyId, ct);
    [HttpPut("occurrences/{id:guid}")]
    public Task<bool> Recover(Guid orgId, Guid companyId, Guid id, [FromBody] bool cancel, CancellationToken ct) =>
        jobs.Recover(Actor, orgId, companyId, id, cancel, ct);
}
