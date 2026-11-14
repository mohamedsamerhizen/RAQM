namespace RAQM.Api.Middleware;

public sealed class RequestCorrelationMiddleware(RequestDelegate next, ILogger<RequestCorrelationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
        context.TraceIdentifier = supplied is { Length: > 0 and <= 64 } &&
            supplied.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? supplied : Guid.NewGuid().ToString("N");
        context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.CacheControl = "no-store";
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = context.TraceIdentifier });
        await next(context);
        logger.LogInformation("HTTP {Method} {Path} {StatusCode}", context.Request.Method, context.Request.Path, context.Response.StatusCode);
    }
}
