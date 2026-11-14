using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RAQM.Api.DTOs.Integrations;
using RAQM.Api.Entities.Integrations;
using Xunit;

namespace RAQM.Tests;

public sealed partial class TradingIntegrationTests
{
    [Fact]
    public async Task ConcurrentImportDeduplicatesAndApprovedExecutionPreservesExternalIdentity()
    {
        var s = await Arrange();
        using var owner = s.Client;
        using var reviewer = await TaxReviewer(s);
        var root = s.Root.Replace("/trading", "/integrations", StringComparison.Ordinal);
        var machineRoot = s.Root.Replace("/trading", "/machine", StringComparison.Ordinal);
        var app = await Send<IntegrationApplication>(owner, HttpMethod.Post, root + "/applications", new ApplicationRequest("CRM", ["master.import"]));
        await Send<IntegrationApplication>(reviewer, HttpMethod.Post, $"{root}/applications/{app.Id}/approve", new { });
        var key = await Send<CredentialIssue>(owner, HttpMethod.Post, $"{root}/applications/{app.Id}/credentials", new CredentialRequest());
        using var machine = fixture.Factory.CreateClient();
        machine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("RAQM-Key", key.Key);
        var input = new ImportRequest(1, "external_id,code,name\nx-1,CRM1,Customer One");
        var path = machineRoot + "/imports/source-1";
        var retries = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Send<JsonElement>(machine, HttpMethod.Post, path, input)));
        var id = retries[0].GetProperty("id").GetGuid();
        Assert.Equal(id, retries[1].GetProperty("id").GetGuid());
        await Denied(machine, path, input with { Csv = input.Csv + " changed" }, HttpStatusCode.Conflict);
        await Denied(owner, $"{root}/imports/{id}/execute", new { }, HttpStatusCode.Conflict);
        await Send<JsonElement>(reviewer, HttpMethod.Post, $"{root}/imports/{id}/approve", new { });
        var applied = await Send<JsonElement>(owner, HttpMethod.Post, $"{root}/imports/{id}/execute", new { });
        var replay = await Send<JsonElement>(owner, HttpMethod.Post, $"{root}/imports/{id}/execute", new { });
        Assert.Equal(applied.GetProperty("result").GetString(), replay.GetProperty("result").GetString());
        using var rows = JsonDocument.Parse(applied.GetProperty("result").GetString()!);
        Assert.Equal("X-1", rows.RootElement[0].GetProperty("ExternalId").GetString());
        var duplicate = await Send<JsonElement>(machine, HttpMethod.Post, machineRoot + "/imports/source-2", input);
        var another = duplicate.GetProperty("id").GetGuid();
        await Send<JsonElement>(reviewer, HttpMethod.Post, $"{root}/imports/{another}/approve", new { });
        await Denied(owner, $"{root}/imports/{another}/execute", new { }, HttpStatusCode.Conflict);
    }
}
