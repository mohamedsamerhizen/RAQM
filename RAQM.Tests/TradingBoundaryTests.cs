using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.Trading;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.Trading;
using Xunit;

namespace RAQM.Tests;
public sealed class TradingBoundaryTests
{
    [Fact]
    public async Task TradingEndpointsRequireCompanyScopeAuthenticationAndResolvableServices()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test");
        using var client = factory.CreateClient();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Where(x => x.RoutePattern.RawText!.Contains("/trading")).ToList();
        Assert.True(routes.Count >= 25);
        foreach (var route in routes)
        {
            Assert.NotEmpty(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Null(route.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.Contains("{orgId:guid}", route.RoutePattern.RawText);
            Assert.Contains("{companyId:guid}", route.RoutePattern.RawText);
        }

        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        Assert.IsType<TradingSetupService>(services.GetRequiredService<ITradingSetupService>());
        Assert.IsType<TradeDocumentService>(services.GetRequiredService<ITradeDocumentService>());
        Assert.IsType<SettlementService>(services.GetRequiredService<ISettlementService>());
        Assert.IsType<TradingReportService>(services.GetRequiredService<ITradingReportService>());
        Assert.Same(services.GetRequiredService<FinancialTransaction>(), services.GetRequiredService<IFinancialTransaction>());
    }
}
