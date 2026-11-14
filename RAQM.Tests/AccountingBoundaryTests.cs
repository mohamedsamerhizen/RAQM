using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using Xunit;

namespace RAQM.Tests;
public sealed class AccountingBoundaryTests
{
    [Fact]
    public async Task EveryAccountingRouteRequiresAuthenticationAndExplicitCompanyScope()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test");
        using var client = factory.CreateClient();
        await client.GetAsync("/openapi/v1.json");
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Where(x => x.RoutePattern.RawText!.Contains("/accounting")).ToList();
        Assert.NotEmpty(routes);
        foreach (var route in routes)
        {
            Assert.NotEmpty(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Null(route.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.Contains("{orgId:guid}", route.RoutePattern.RawText);
            Assert.Contains("{companyId:guid}", route.RoutePattern.RawText);
        }

        using var scope = factory.Services.CreateScope();
        Assert.IsType<PostingEngine>(scope.ServiceProvider.GetRequiredService<IPostingEngine>());
        Assert.IsType<LedgerService>(scope.ServiceProvider.GetRequiredService<ILedgerService>());
    }
}
