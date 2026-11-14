using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Identity;
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.Entities.Organizations;
using Xunit;

namespace RAQM.Tests;

public sealed class ControllerCompatibilityTests
{
    private static string Contract(string name) => Path.Combine(AppContext.BaseDirectory, "Contracts", name);

    internal static bool SchemaMatches(string name, JsonNode? expected, JsonNode? actual)
    {
        if (name is not ("TradeLineRequest" or "TradeLineResponse" or "TaxOperationRequest" or "ReportQuery")) return JsonNode.DeepEquals(expected, actual);
        var updated = actual?.DeepClone();
        if (name == "ReportQuery")
        {
            var sequence = updated?["properties"]?["asOfSequence"];
            Assert.NotNull(sequence);
            Assert.DoesNotContain(updated?["required"]?.AsArray() ?? [], x => x?.GetValue<string>() == "asOfSequence");
            Assert.True(updated!["properties"]!.AsObject().Remove("asOfSequence"));
            return JsonNode.DeepEquals(expected, updated);
        }
        if (name == "TaxOperationRequest")
        {
            var attestation = updated?["properties"]?["priorOrdinarySettlementsIneligible"];
            Assert.NotNull(attestation);
            Assert.Equal("boolean", attestation["type"]?.GetValue<string>());
            Assert.False(attestation["default"]?.GetValue<bool>());
            Assert.DoesNotContain(updated?["required"]?.AsArray() ?? [], x => x?.GetValue<string>() == "priorOrdinarySettlementsIneligible");
            Assert.True(updated!["properties"]!.AsObject().Remove("priorOrdinarySettlementsIneligible"));
            return JsonNode.DeepEquals(expected, updated);
        }
        var cancellation = updated?["properties"]?["cancelUnissuedStock"];
        Assert.NotNull(cancellation);
        Assert.Equal("boolean", cancellation["type"]?.GetValue<string>());
        Assert.False(cancellation["default"]?.GetValue<bool>());
        Assert.DoesNotContain(updated?["required"]?.AsArray() ?? [], x => x?.GetValue<string>() == "cancelUnissuedStock");
        Assert.True(updated!["properties"]!.AsObject().Remove("cancelUnissuedStock"));
        if (name == "TradeLineRequest")
        {
            var stockItem = updated["properties"]?["stockItemId"];
            Assert.NotNull(stockItem);
            Assert.Equal("uuid", stockItem["format"]?.GetValue<string>());
            Assert.DoesNotContain(updated["required"]?.AsArray() ?? [], x => x?.GetValue<string>() == "stockItemId");
            Assert.True(updated["properties"]!.AsObject().Remove("stockItemId"));
        }
        return JsonNode.DeepEquals(expected, updated);
    }

    [Fact]
    public void ExistingMigrationIdentityAndModelAreUnchanged()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        var migrations = db.Database.GetMigrations().ToList();
        Assert.Equal("20261106064719_InitialFoundation", migrations[0]);
        Assert.Equal(22, migrations.Count);
        Assert.EndsWith("_AccountingCoreV1", migrations[1]);
        Assert.Equal("20261106142951_TradingSubledgerV1", migrations[2]);
        Assert.Equal("20261107072607_BankingTreasuryV1", migrations[3]);
        Assert.Equal("20261107080456_FinancialCloseV1", migrations[4]);
        Assert.Equal("20261107084241_FixedAssetsV1", migrations[5]);
        Assert.Equal("20261108153638_InventoryProcurementV1", migrations[6]);
        Assert.Equal("20261108161020_BudgetPlanningV1", migrations[7]);
        Assert.Equal("20261108172927_MultiCurrencyV1", migrations[8]);
        Assert.Equal("20261109135334_FinancialDimensionsV1", migrations[9]);
        Assert.EndsWith("_CorporateConsolidationV1", migrations[10]);
        Assert.Equal("20261109165011_TaxAccountingV1", migrations[11]);
        Assert.Equal("20261109175325_GovernanceControlsV1", migrations[12]);
        Assert.Equal("20261110110401_IntegrationPlatformV1", migrations[13]);
        Assert.Equal("20261110112632_SaaSCommercialV1", migrations[14]);
        Assert.Equal("20261111110000_IntegrationStabilization", migrations[15]);
        Assert.Equal("20261113110000_GovernedJournalCorrections", migrations[16]);
        Assert.Equal("20261113120000_StockedSalesIntegrity", migrations[17]);
        Assert.Equal("20261113130000_HardBudgetCommitmentAdoption", migrations[18]);
        Assert.Equal("20261114095147_Wp07OffboardingEvidence", migrations[19]);
        Assert.Equal("20261114150000_StockedSaleCancellation", migrations[20]);
        Assert.Equal("20261114160000_StockCancellationPrecision", migrations[21]);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task ControllerAuthorizationAndAuthenticationRateLimitsArePreserved()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test");
        using var client = factory.CreateClient();
        await client.GetAsync("/openapi/v1.json");
        var groupEndpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.Contains("/corporate-groups") == true).ToArray();
        Assert.NotEmpty(groupEndpoints);
        Assert.All(groupEndpoints, e =>
        {
            Assert.NotEmpty(e.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Null(e.Metadata.GetMetadata<IAllowAnonymous>());
        });
        var taxEndpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.Contains("/tax-accounting") == true).ToArray();
        Assert.NotEmpty(taxEndpoints);
        Assert.All(taxEndpoints, e =>
        {
            Assert.NotEmpty(e.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Null(e.Metadata.GetMetadata<IAllowAnonymous>());
        });
        var commercialEndpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } path &&
                (path.StartsWith("api/v1/saas/") || path.StartsWith("api/v1/platform-commercial/") ||
                 path.StartsWith("api/v1/organizations/{orgId:guid}/commercial"))).ToArray();
        Assert.Equal(20, commercialEndpoints.Length);
        Assert.All(commercialEndpoints, e =>
        {
            Assert.NotEmpty(e.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Null(e.Metadata.GetMetadata<IAllowAnonymous>());
        });
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Where(e => e.RoutePattern.RawText!.StartsWith("api/v1/") &&
                !e.RoutePattern.RawText.Contains("/accounting") && !e.RoutePattern.RawText.Contains("/trading") &&
                !e.RoutePattern.RawText.Contains("/banking") && !e.RoutePattern.RawText.Contains("/cash-management") &&
                !e.RoutePattern.RawText.Contains("/treasury") && !e.RoutePattern.RawText.Contains("/financial-reporting") &&
                !e.RoutePattern.RawText.Contains("/financial-close") && !e.RoutePattern.RawText.Contains("/recognition") &&
                !e.RoutePattern.RawText.Contains("/fixed-assets") && !e.RoutePattern.RawText.Contains("/inventory") &&
                !e.RoutePattern.RawText.Contains("/budgets") && !e.RoutePattern.RawText.Contains("/forecasts") &&
                !e.RoutePattern.RawText.Contains("/foreign-exchange") && !e.RoutePattern.RawText.Contains("/dimensions") &&
                !e.RoutePattern.RawText.Contains("/corporate-groups") && !e.RoutePattern.RawText.Contains("/tax-accounting") &&
                !e.RoutePattern.RawText.Contains("/governance") && !e.RoutePattern.RawText.Contains("/integrations") &&
                !e.RoutePattern.RawText.Contains("/machine") && !e.RoutePattern.RawText.Contains("/commercial") &&
                !e.RoutePattern.RawText.Contains("/platform-commercial") && !e.RoutePattern.RawText.Contains("/saas")).ToList();
        Assert.Equal(15, endpoints.Count);
        foreach (var endpoint in endpoints)
        {
            var anonymous = new[] { "api/v1/auth/onboard", "api/v1/auth/login", "api/v1/auth/refresh" }
                .Contains(endpoint.RoutePattern.RawText);
            Assert.Equal(!anonymous, endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0);
            Assert.Equal(anonymous, endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == "auth");
        }
    }

    [Theory]
    [InlineData("application/vnd.raqm+json", 403)]
    [InlineData("text/plain", 400)]
    [InlineData("text/json", 400)]
    public async Task JsonMediaTypeHandlingRemainsCompatible(string contentType, int expectedStatus)
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test");
        using var client = factory.CreateClient();
        using var body = new StringContent("""{"onboardingKey":"wrong"}""", Encoding.UTF8, contentType);
        using var response = await client.PostAsync("/api/v1/auth/onboard", body);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
    }

    [Fact]
    public async Task RecordedHttpBehaviorIsPreserved()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Timeout=1");
        using var client = factory.CreateClient();
        var cases = JsonNode.Parse(await File.ReadAllTextAsync(Contract("phase0-http-before.json")))!.AsArray();
        foreach (var testCase in cases)
        {
            using var request = new HttpRequestMessage(new HttpMethod(testCase!["Method"]!.GetValue<string>()),
                testCase["Path"]!.GetValue<string>());
            request.Headers.Add("X-Correlation-ID", "structural-contract");
            if (testCase["Body"] is { } body) request.Content = new StringContent(body.GetValue<string>(), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            Assert.Equal(testCase["Status"]!.GetValue<int>(), (int)response.StatusCode);
            Assert.Equal(testCase["ContentType"]!.GetValue<string>(), response.Content.Headers.ContentType!.ToString());
            var expected = JsonNode.Parse(testCase["Response"]!.GetValue<string>())!.AsObject();
            var actual = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            expected.Remove("traceId"); actual.Remove("traceId");
            Assert.True(JsonNode.DeepEquals(expected, actual), $"{request.Method} {request.RequestUri}: {actual}");
        }
    }

    [Fact]
    public async Task OpenApiPathsRequestsAndResponsesMatchRecordedContract()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Timeout=1");
        using var client = factory.CreateClient();
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(Contract("phase0-openapi-before.json")))!;
        var actual = (await client.GetFromJsonAsync<JsonObject>("/openapi/v1.json"))!;
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "openapi-after.json"), actual.ToJsonString());
        foreach (var path in expected["paths"]!.AsObject())
            Assert.True(JsonNode.DeepEquals(path.Value, actual["paths"]![path.Key]),
                $"OpenAPI difference at {path.Key}: expected {path.Value}, actual {actual["paths"]![path.Key]}");
        foreach (var schema in expected["components"]!["schemas"]!.AsObject())
            Assert.True(ControllerCompatibilityTests.SchemaMatches(schema.Key, schema.Value, actual["components"]!["schemas"]![schema.Key]), $"Phase 0 request schema changed: {schema.Key}");
        var phaseFive = JsonNode.Parse(await File.ReadAllTextAsync(Contract("phase05-openapi-before.json")))!;
        foreach (var path in phaseFive["paths"]!.AsObject())
            Assert.True(JsonNode.DeepEquals(path.Value, actual["paths"]![path.Key]), $"Existing OpenAPI path changed: {path.Key}");
        foreach (var schema in phaseFive["components"]!["schemas"]!.AsObject())
            Assert.True(ControllerCompatibilityTests.SchemaMatches(schema.Key, schema.Value, actual["components"]!["schemas"]![schema.Key]), $"Existing OpenAPI schema changed: {schema.Key}");
        var phaseSix = JsonNode.Parse(await File.ReadAllTextAsync(Contract("phase06-openapi-before.json")))!;
        foreach (var path in phaseSix["paths"]!.AsObject())
            Assert.True(JsonNode.DeepEquals(path.Value, actual["paths"]![path.Key]), $"Phase 06 OpenAPI path changed: {path.Key}");
        foreach (var schema in phaseSix["components"]!["schemas"]!.AsObject())
            Assert.True(ControllerCompatibilityTests.SchemaMatches(schema.Key, schema.Value, actual["components"]!["schemas"]![schema.Key]), $"Phase 06 OpenAPI schema changed: {schema.Key}");
        var phaseSeven = JsonNode.Parse(await File.ReadAllTextAsync(Contract("phase07-openapi-before.json")))!;
        foreach (var path in phaseSeven["paths"]!.AsObject())
            Assert.True(JsonNode.DeepEquals(path.Value, actual["paths"]![path.Key]), $"Phase 07 OpenAPI path changed: {path.Key}");
        foreach (var schema in phaseSeven["components"]!["schemas"]!.AsObject())
            Assert.True(ControllerCompatibilityTests.SchemaMatches(schema.Key, schema.Value, actual["components"]!["schemas"]![schema.Key]), $"Phase 07 OpenAPI schema changed: {schema.Key}");
        var phaseEight = JsonNode.Parse(await File.ReadAllTextAsync(Contract("phase08-openapi-before.json")))!;
        foreach (var path in phaseEight["paths"]!.AsObject())
            Assert.True(JsonNode.DeepEquals(path.Value, actual["paths"]![path.Key]), $"Phase 08 OpenAPI path changed: {path.Key}");
        foreach (var schema in phaseEight["components"]!["schemas"]!.AsObject())
            Assert.True(ControllerCompatibilityTests.SchemaMatches(schema.Key, schema.Value, actual["components"]!["schemas"]![schema.Key]), $"Phase 08 OpenAPI schema changed: {schema.Key}");
        var phaseNine = JsonNode.Parse(await File.ReadAllTextAsync(Contract("phase09-openapi-before.json")))!;
        foreach (var path in phaseNine["paths"]!.AsObject())
            Assert.True(JsonNode.DeepEquals(path.Value, actual["paths"]![path.Key]), $"Phase 09 OpenAPI path changed: {path.Key}");
        foreach (var schema in phaseNine["components"]!["schemas"]!.AsObject())
            Assert.True(ControllerCompatibilityTests.SchemaMatches(schema.Key, schema.Value, actual["components"]!["schemas"]![schema.Key]), $"Phase 09 OpenAPI schema changed: {schema.Key}");
        var phaseTen = JsonNode.Parse(await File.ReadAllTextAsync(Contract("phase10-openapi-before.json")))!;
        foreach (var path in phaseTen["paths"]!.AsObject())
            Assert.True(JsonNode.DeepEquals(path.Value, actual["paths"]![path.Key]), $"Phase 10 OpenAPI path changed: {path.Key}");
        foreach (var schema in phaseTen["components"]!["schemas"]!.AsObject())
            Assert.True(ControllerCompatibilityTests.SchemaMatches(schema.Key, schema.Value, actual["components"]!["schemas"]![schema.Key]), $"Phase 10 OpenAPI schema changed: {schema.Key}");
        var phaseEleven = JsonNode.Parse(await File.ReadAllTextAsync(Contract("phase11-openapi-before.json")))!;
        foreach (var path in phaseEleven["paths"]!.AsObject())
            Assert.True(JsonNode.DeepEquals(path.Value, actual["paths"]![path.Key]), $"Phase 11 OpenAPI path changed: {path.Key}");
        foreach (var schema in phaseEleven["components"]!["schemas"]!.AsObject())
            Assert.True(ControllerCompatibilityTests.SchemaMatches(schema.Key, schema.Value, actual["components"]!["schemas"]![schema.Key]), $"Phase 11 OpenAPI schema changed: {schema.Key}");
        var phaseTwelve = JsonNode.Parse(await File.ReadAllTextAsync(Contract("phase12-openapi-before.json")))!;
        foreach (var path in phaseTwelve["paths"]!.AsObject())
            Assert.True(JsonNode.DeepEquals(path.Value, actual["paths"]![path.Key]), $"Phase 12 OpenAPI path changed: {path.Key}");
        foreach (var schema in phaseTwelve["components"]!["schemas"]!.AsObject())
            Assert.True(ControllerCompatibilityTests.SchemaMatches(schema.Key, schema.Value, actual["components"]!["schemas"]![schema.Key]), $"Phase 12 OpenAPI schema changed: {schema.Key}");
        var phaseThirteen = JsonNode.Parse(await File.ReadAllTextAsync(Contract("phase13-openapi-before.json")))!;
        foreach (var path in phaseThirteen["paths"]!.AsObject())
            Assert.True(JsonNode.DeepEquals(path.Value, actual["paths"]![path.Key]), $"Phase 13 OpenAPI path changed: {path.Key}");
        foreach (var schema in phaseThirteen["components"]!["schemas"]!.AsObject())
            Assert.True(ControllerCompatibilityTests.SchemaMatches(schema.Key, schema.Value, actual["components"]!["schemas"]![schema.Key]), $"Phase 13 OpenAPI schema changed: {schema.Key}");
    }

    [Fact]
    public void ExplicitResponseDtosPreserveEntityJsonShape()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var company = new Company {
            Name = "Company", RegistrationNumber = "REG", BaseCurrency = "IQD",
            OrganizationId = Guid.NewGuid(), TenantId = Guid.NewGuid(), CreatedBy = Guid.NewGuid(), UpdatedBy = Guid.NewGuid()
        };
        var organization = new Organization { Name = "Organization", TenantId = company.TenantId, CreatedBy = company.CreatedBy };
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(company, options), JsonSerializer.SerializeToNode(CompanyResponse.From(company), options)));
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(organization, options), JsonSerializer.SerializeToNode(OrganizationResponse.From(organization), options)));
        var me = new CurrentUserResponse(company.CreatedBy, [new OrganizationMembershipResponse(organization.Id, "Owner")]);
        var priorShape = new { UserId = company.CreatedBy, Organizations = new[] { new { OrganizationId = organization.Id, Role = "Owner" } } };
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(priorShape, options), JsonSerializer.SerializeToNode(me, options)));
    }
}
