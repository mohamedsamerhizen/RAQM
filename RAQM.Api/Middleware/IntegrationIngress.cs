using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using RAQM.Api.Services.ApiAccess;

namespace RAQM.Api.Middleware;

public sealed class IntegrationIngress(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IntegrationIngressLimits limits)
    {
        using var ip = limits.ByAddress.AttemptAcquire(context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        using var concurrent = limits.Concurrent.AttemptAcquire(1);
        if (!ip.IsAcquired || !concurrent.IsAcquired)
        {
            context.Response.Headers.RetryAfter = "60";
            await Results.Problem(statusCode: 429, title: "Integration ingress limit exceeded.").ExecuteAsync(context);
            return;
        }
        var result = await context.AuthenticateAsync(MachineIdentity.Scheme);
        if (result.Succeeded) context.User = result.Principal!;
        if (System.Diagnostics.Activity.Current is { } trace)
            context.Response.Headers["X-Trace-ID"] = trace.TraceId.ToHexString();
        await next(context);
    }
}

public sealed class IntegrationIngressLimits(IConfiguration config) : IDisposable
{
    public PartitionedRateLimiter<string> ByAddress { get; } = PartitionedRateLimiter.Create<string, string>(
        address => RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Clamp(config.GetValue("Integrations:IngressPerMinute", 240), 1, 10000),
            Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
    public ConcurrencyLimiter Concurrent { get; } = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = Math.Clamp(config.GetValue("Integrations:ConcurrentRequests", 16), 1, 128), QueueLimit = 0
    });
    public void Dispose()
    {
        ByAddress.Dispose();
        Concurrent.Dispose();
    }
}
