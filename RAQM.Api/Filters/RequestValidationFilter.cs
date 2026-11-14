using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using RAQM.Api.Helpers;
namespace RAQM.Api.Filters;

// Preserve minimal-API binding errors without introducing new DTO validation rules.
public sealed class RequestValidationFilter : IActionFilter, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (context.ActionDescriptor.Parameters.Any(p => p.BindingInfo?.BindingSource == BindingSource.Body) &&
            !context.HttpContext.Request.HasJsonContentType())
            throw new ApiException(400, "Invalid request.");
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid || context.ActionDescriptor.Parameters.Any(p =>
            p.BindingInfo?.BindingSource == BindingSource.Body &&
            (!context.ActionArguments.TryGetValue(p.Name, out var value) || value is null)))
            throw new ApiException(400, "Invalid request.");
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
