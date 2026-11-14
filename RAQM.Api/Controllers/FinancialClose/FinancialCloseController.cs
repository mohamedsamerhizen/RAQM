using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.FinancialClose;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.FinancialClose;

namespace RAQM.Api.Controllers.FinancialClose;

[ApiController, Authorize, Tags("Financial close")]
[Route("api/v1/organizations/{orgId:guid}/companies/{companyId:guid}/financial-close")]
public sealed class FinancialCloseController(IFinancialCloseService service) : ControllerBase
{
    [HttpPost("periods/{periodId:guid}/sessions")]
    public async Task<IResult> Start(Guid orgId, Guid companyId, Guid periodId, CancellationToken ct) =>
        Results.Ok(await service.Start(CurrentUser.Id(User), orgId, companyId, periodId, ct));

    [HttpGet("periods/{periodId:guid}/sessions")]
    public async Task<IResult> History(Guid orgId, Guid companyId, Guid periodId, CancellationToken ct) =>
        Results.Ok(await service.History(CurrentUser.Id(User), orgId, companyId, periodId, ct));

    [HttpGet("sessions/{id:guid}/readiness")]
    public async Task<IResult> Readiness(Guid orgId, Guid companyId, Guid id, CancellationToken ct) =>
        Results.Ok(await service.Readiness(CurrentUser.Id(User), orgId, companyId, id, ct));

    [HttpPost("sessions/{id:guid}/tasks")]
    public async Task<IResult> Task(Guid orgId, Guid companyId, Guid id, CloseTaskRequest request, CancellationToken ct) =>
        Results.Ok(await service.AddTask(CurrentUser.Id(User), orgId, companyId, id, request, ct));

    [HttpPost("sessions/{id:guid}/tasks/{taskId:guid}/complete")]
    public async Task<IResult> Complete(Guid orgId, Guid companyId, Guid id, Guid taskId, CloseAction request, CancellationToken ct) =>
        Results.Ok(await service.CompleteTask(CurrentUser.Id(User), orgId, companyId, id, taskId, request, ct));

    [HttpPost("sessions/{id:guid}/exceptions")]
    public async Task<IResult> Raise(Guid orgId, Guid companyId, Guid id, ExceptionRequest request, CancellationToken ct) =>
        Results.Ok(await service.Raise(CurrentUser.Id(User), orgId, companyId, id, request, ct));

    [HttpPost("sessions/{id:guid}/exceptions/{exceptionId:guid}/resolve")]
    public async Task<IResult> Resolve(Guid orgId, Guid companyId, Guid id, Guid exceptionId, CloseAction request, CancellationToken ct) =>
        Results.Ok(await service.Resolve(CurrentUser.Id(User), orgId, companyId, id, exceptionId, request, ct));

    [HttpPost("sessions/{id:guid}/state/{workflowAction}")]
    public async Task<IResult> Transition(Guid orgId, Guid companyId, Guid id, string workflowAction, CloseAction request, CancellationToken ct) =>
        Results.Ok(await service.Transition(CurrentUser.Id(User), orgId, companyId, id, workflowAction, request, ct));

    [HttpPost("years/{yearId:guid}/close")]
    public async Task<IResult> YearEnd(Guid orgId, Guid companyId, Guid yearId, YearEndRequest request, CancellationToken ct) =>
        Results.Ok(await service.YearEnd(CurrentUser.Id(User), orgId, companyId, yearId, request, ct));

    [HttpGet("years")]
    public async Task<IResult> Years(Guid orgId, Guid companyId, CancellationToken ct) =>
        Results.Ok(await service.Years(CurrentUser.Id(User), orgId, companyId, ct));
}
