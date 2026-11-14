using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RAQM.Api.Data;
namespace RAQM.Api.Services.Health;

public sealed class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            if (!await db.Database.CanConnectAsync(ct)) return HealthCheckResult.Unhealthy("Database unavailable.");
            return (await db.Database.GetPendingMigrationsAsync(ct)).Any()
                ? HealthCheckResult.Unhealthy("Database migrations pending.") : HealthCheckResult.Healthy();
        }
        catch { return HealthCheckResult.Unhealthy("Database unavailable."); }
    }
}


