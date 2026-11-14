using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
namespace RAQM.Api.Configurations;

// Keep the existing document's application/json contract; +json remains accepted at runtime.
public sealed class ControllerContractOpenApiTransformer : IOpenApiOperationTransformer, IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        if (operation.RequestBody?.Content is { } content)
            foreach (var mediaType in content.Keys.Where(x => x != "application/json").ToArray())
                content.Remove(mediaType);
        if (operation.Parameters is { } parameters)
            foreach (var parameter in parameters.OfType<OpenApiParameter>())
                if (parameter.In == ParameterLocation.Path && parameter.Name == "workflowAction")
                    parameter.Name = "action";
        return Task.CompletedTask;
    }

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        // Keep the published placeholder name independent of MVC's reserved route values.
        const string workflowToken = "/{workflowAction}";
        foreach (var (path, item) in document.Paths.Where(entry => entry.Key.EndsWith(workflowToken, StringComparison.Ordinal)).ToArray())
        {
            document.Paths.Add(path[..^workflowToken.Length] + "/{action}", item);
            document.Paths.Remove(path);
        }
        return Task.CompletedTask;
    }
}
