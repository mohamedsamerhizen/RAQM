using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Accruals;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Accruals;

namespace RAQM.Api.Controllers.Accruals;

[ApiController, Authorize, Tags("Accruals and recognition")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/recognition")]
public sealed class RecognitionController(IRecognitionService service) : ControllerBase
{
    [HttpPost("schedules")]
    public async Task<IResult> Create(Guid orgId, Guid companyId, ScheduleRequest request, CancellationToken ct) =>
        Results.Ok(await service.Create(CurrentUser.Id(User), orgId, companyId, request, ct));

    [HttpGet("schedules")]
    public async Task<IResult> List(Guid orgId, Guid companyId, CancellationToken ct, int page = 1, int pageSize = 50) =>
        Results.Ok(await service.List(CurrentUser.Id(User), orgId, companyId, page, pageSize, ct));

    [HttpGet("schedules/{id:guid}/preview")]
    public async Task<IResult> Preview(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        Results.Ok(await service.Preview(CurrentUser.Id(User), orgId, companyId, id, ct));

    [HttpPost("schedules/{id:guid}/approve")]
    public async Task<IResult> Approve(Guid orgId, Guid companyId, Guid id, ScheduleAction request, CancellationToken ct) =>
        Results.Ok(await service.Transition(CurrentUser.Id(User), orgId, companyId, id, true, request, ct));

    [HttpPost("schedules/{id:guid}/cancel")]
    public async Task<IResult> Cancel(Guid orgId, Guid companyId, Guid id, ScheduleAction request, CancellationToken ct) =>
        Results.Ok(await service.Transition(CurrentUser.Id(User), orgId, companyId, id, false, request, ct));

    [HttpPost("schedules/{id:guid}/occurrences/{occurrenceId:guid}/post")]
    public async Task<IResult> Post(Guid orgId, Guid companyId, Guid id, Guid occurrenceId, RecognitionExecution request, CancellationToken ct) =>
        Results.Ok(await service.Post(CurrentUser.Id(User), orgId, companyId, id, occurrenceId, request, ct));

    [HttpPost("schedules/{id:guid}/occurrences/{occurrenceId:guid}/reverse")]
    public async Task<IResult> Reverse(Guid orgId, Guid companyId, Guid id, Guid occurrenceId, RecognitionReversal request, CancellationToken ct) =>
        Results.Ok(await service.Reverse(CurrentUser.Id(User), orgId, companyId, id, occurrenceId, request, ct));

    [HttpPost("schedules/{id:guid}/run")]
    public async Task<IResult> Run(Guid orgId, Guid companyId, Guid id, RecognitionExecution request, CancellationToken ct) =>
        Results.Ok(await service.Run(CurrentUser.Id(User), orgId, companyId, id, request, ct));

    [HttpGet("schedules/{id:guid}/reconcile")]
    public async Task<IResult> Reconcile(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        Results.Ok(await service.Reconcile(CurrentUser.Id(User), orgId, companyId, id, ct));
}
