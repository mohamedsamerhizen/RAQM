using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RAQM.Api.Helpers;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Services.AuditEvidence;
namespace RAQM.Api.Middleware;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception.Data[nameof(DeniedGovernanceAction)] is DeniedGovernanceAction denial)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await using var scope = context.RequestServices.CreateAsyncScope();
                var transactions = scope.ServiceProvider.GetRequiredService<IFinancialTransaction>();
                var audit = scope.ServiceProvider.GetRequiredService<GovernanceAudit>();
                await transactions.Execute(denial.Actor, denial.Organization, denial.Company, async (company, token) =>
                {
                    await audit.RecordDenial(company, denial.Actor,
                        new("security", "GovernanceDenied", denial.Source, denial.Reason, GovernanceOutcome.Denied,
                            denial.Duty, Policy: denial.Policy), token);
                    return true;
                }, timeout.Token);
            }
            catch (Exception auditError)
            {
                logger.LogWarning("Governance denial evidence could not be retained ({ErrorType})", auditError.GetType().Name);
            }
        }
        var (status, title) = exception switch
        {
            ApiException e => (e.Status, e.Message),
            BadHttpRequestException => (400, "Invalid request."),
            DbUpdateConcurrencyException => (409, "Resource changed; retrieve its current version."),
            PostgresException { SqlState: "P1401" } or DbUpdateException { InnerException: PostgresException { SqlState: "P1401" } }
                => (403, "Tenant financial writes are restricted by commercial status."),
            PostgresException { SqlState: "P1402" } or DbUpdateException { InnerException: PostgresException { SqlState: "P1402" } }
                => (409, "The tenant quota for this resource is exhausted."),
            PostgresException { SqlState: "P1403" } or DbUpdateException { InnerException: PostgresException { SqlState: "P1403" } }
                => (403, "This feature is not enabled for the current plan."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation } } => (409, "Fiscal dates overlap existing configuration."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } => (409, "A resource with this unique value already exists."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation } } => (400, "Database relationship or configuration constraint violated."),
            _ => (500, "An unexpected error occurred.")
        };
        if (status == 500) logger.LogError(exception, "Request failed {CorrelationId}", context.TraceIdentifier);
        context.Response.StatusCode = status;
        var extensions = new Dictionary<string, object?>
        {
            ["correlationId"] = context.TraceIdentifier
        };
        if (exception is ApiException { Code: not null } error) extensions["code"] = error.Code;
        await Results.Problem(statusCode: status, title: title, extensions: extensions).ExecuteAsync(context);
        return true;
    }
}


