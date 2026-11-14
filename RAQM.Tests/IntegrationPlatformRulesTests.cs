using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using RAQM.Api.DTOs.Integrations;
using RAQM.Api.Helpers;
using RAQM.Api.Services.DataExchange;
using RAQM.Api.Services.EventProcessing;
using RAQM.Api.Services.Webhooks;
using Xunit;

namespace RAQM.Tests;

public sealed class IntegrationPlatformRulesTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("100.64.0.2", false)]
    [InlineData("192.168.1.12", false)]
    [InlineData("::ffff:172.16.0.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700::1111", true)]
    public void WebhookEgressRejectsNonPublicAddressRanges(string address, bool expected) =>
        Assert.Equal(expected, WebhookTransport.PublicAddress(IPAddress.Parse(address)));

    [Fact]
    public void HmacMatchesIndependentVectorAndRejectsChangedBytesAndStaleTimestamp()
    {
        const string key = "0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20";
        const string expected = "v1=531369b0edaec6d9cd001411ec3ec2dcaed4083c6a7bf36fb6dff8460f0799e3";
        const long unix = 1700000000;
        var id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var body = Encoding.UTF8.GetBytes("""{"total":12}""");
        Assert.Equal(expected, WebhookTransport.Signature(key, unix, id, body));
        Assert.True(WebhookTransport.Verify(key, unix, id, body, expected, unix + 300));
        Assert.False(WebhookTransport.Verify(key, unix, id, body, expected, unix + 301));
        Assert.False(WebhookTransport.Verify(key, unix, id, Encoding.UTF8.GetBytes("""{"total":13}"""), expected, unix));
    }

    [Fact]
    public void EgressRequiresAnExactAllowedHttpsHostAndRejectsSecretsInUrls()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Integrations:WebhookHosts:0"] = "hooks.example" }).Build();
        var transport = new WebhookTransport(configuration);
        Assert.Equal("hooks.example", transport.Endpoint("https://hooks.example/receiver").Host);
        foreach (var invalid in new[] { "http://hooks.example/", "https://hooks.example:444/", "https://hooks.example.evil/",
            "https://name@hooks.example/", "https://hooks.example/?token=sample", "https://127.0.0.1/" })
            Assert.Throws<ApiException>(() => transport.Endpoint(invalid));
    }

    [Fact]
    public void CsvPreviewParsesQuotedNamesAndRejectsDuplicateKeysAndOversizedBatches()
    {
        var adapter = new CustomerCsvAdapter();
        var csv = adapter.Parse(new ImportRequest(1, "external_id,code,name\nA,one,\"Company, Inc.\"\na,two,Duplicate"));
        Assert.Equal("Company, Inc.", Assert.Single(csv.Rows).Name);
        Assert.Equal("ONE", csv.Rows[0].Code);
        Assert.Single(csv.Errors);
        Assert.Throws<ApiException>(() => adapter.Parse(new ImportRequest(1, new string('a', 65537))));
    }

    [Fact]
    public void RetryBackoffUsesABoundedExponentialDelay()
    {
        Assert.Equal(new[] { 15, 30, 60, 120, 240, 480 }, Enumerable.Range(1, 6).Select(DeliveryDispatcher.Delay));
        Assert.Equal(960, DeliveryDispatcher.Delay(20));
    }
}
