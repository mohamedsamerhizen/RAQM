using RAQM.Api.Services.Organizations;
using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using RAQM.Api.Helpers;
using RAQM.Api.Entities.Organizations;
namespace RAQM.Tests;

public sealed class FoundationTests
{
    [Fact]
    public void SharedWebhookKeyRingSurvivesProviderReplacementAndCertificateRotation()
    {
        var directory = Directory.CreateTempSubdirectory("raqm-key-test-");
        try
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            using var nextRsa = System.Security.Cryptography.RSA.Create(2048);
            var oldRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=RAQM old test", rsa,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            var nextRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=RAQM next test", nextRsa,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            var now = DateTimeOffset.UtcNow;
            using var old = oldRequest.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
            using var next = nextRequest.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
            var password = Guid.NewGuid().ToString("N");
            var passwordPath = Path.Combine(directory.FullName, "password");
            File.WriteAllText(passwordPath, password);
            var oldPath = Path.Combine(directory.FullName, "old.pfx");
            var nextPath = Path.Combine(directory.FullName, "next.pfx");
            File.WriteAllBytes(oldPath, old.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, password));
            File.WriteAllBytes(nextPath, next.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, password));
            var values = new Dictionary<string, string?>
            {
                ["WebhookKeys:Directory"] = directory.FullName,
                ["WebhookKeys:ApplicationName"] = "RAQM-key-ring-test",
                ["WebhookKeys:Certificate:Path"] = oldPath,
                ["WebhookKeys:Certificate:PasswordFile"] = passwordPath
            };
            using var api = KeyProvider(values);
            var ciphertext = api.GetRequiredService<IDataProtectionProvider>().CreateProtector("webhook").Protect("source-secret");
            var key = Assert.Single(directory.GetFiles("key-*.xml"));
            Assert.Contains("encryptedSecret", File.ReadAllText(key.FullName));
            values["WebhookKeys:Certificate:Path"] = nextPath;
            values["WebhookKeys:PreviousCertificates:0:Path"] = oldPath;
            values["WebhookKeys:PreviousCertificates:0:PasswordFile"] = passwordPath;
            using var worker = KeyProvider(values);
            Assert.Equal("source-secret",
                worker.GetRequiredService<IDataProtectionProvider>().CreateProtector("webhook").Unprotect(ciphertext));
            values["WebhookKeys:ApplicationName"] = "other-application";
            using var unrelated = KeyProvider(values);
            Assert.Throws<System.Security.Cryptography.CryptographicException>(() =>
                unrelated.GetRequiredService<IDataProtectionProvider>().CreateProtector("webhook").Unprotect(ciphertext));
        }
        finally
        {
            foreach (var file in directory.GetFiles()) file.Delete();
            directory.Delete();
        }
    }

    private static ServiceProvider KeyProvider(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        RAQM.Api.Configurations.WebhookKeyRing.Configure(services,
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(), required: true);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void StartupRejectsMissingSigningKey()
    {
        using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=test;Username=test");
        using var invalid = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(config,
                new Dictionary<string, string?> { ["Auth:SigningKey"] = "" })));
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => invalid.CreateClient());
    }
    [Theory]
    [InlineData("USD", 1, 1)]
    [InlineData("IQD", 4, 1)]
    [InlineData("EUR", 12, 31)]
    public void AcceptsValidCompanySettings(string currency, int month, int day) =>
        OrganizationService.ValidateSettings(currency, month, day);
    [Theory]
    [InlineData("usd", 1, 1)]
    [InlineData("ZZZ", 1, 1)]
    [InlineData("INR", 1, 1)]
    [InlineData("USD", 0, 1)]
    [InlineData("USD", 13, 1)]
    [InlineData("USD", 2, 29)]
    [InlineData("USD", 4, 31)]
    [InlineData("USD", 1, 0)]
    public void RejectsInvalidSettings(string currency, int month, int day) =>
        Assert.Equal(400, Assert.Throws<ApiException>(() => OrganizationService.ValidateSettings(currency, month, day)).Status);
    [Fact]
    public async Task StartsAndReportsDatabaseOutageWithoutFailingLiveness()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Timeout=1");
        using var client = factory.CreateClient();
        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.True(live.Headers.Contains("X-Correlation-ID"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }
    [Fact]
    public async Task AuthenticationRateLimitReturnsProblemDetails()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=test;Username=test", requestsPerMinute: 1);
        using var client = factory.CreateClient();
        var body = new RAQM.Api.DTOs.Identity.OnboardRequest("test@example.com", "Invalid", "Org", "wrong");
        Assert.Equal(HttpStatusCode.Forbidden, (await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client, "/api/v1/auth/onboard", body)).StatusCode);
        var limited = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client, "/api/v1/auth/onboard", body);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType!.MediaType);
    }
}


