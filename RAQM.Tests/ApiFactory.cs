using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
namespace RAQM.Tests;

public sealed class ApiFactory(string connection, int requestsPerMinute = 1000, IReadOnlyDictionary<string, string?>? extra = null) : WebApplicationFactory<Program>
{
    public const string OnboardingKey = "test-onboarding-key-with-256-bits-minimum";
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = connection,
            ["Auth:SigningKey"] = "test-signing-key-with-at-least-256-bits-entropy",
            ["Auth:OnboardingKey"] = OnboardingKey,
            ["Auth:RequestsPerMinute"] = requestsPerMinute.ToString(),
            ["Integrations:WebhookHosts:0"] = "hooks.example"
        }));
        if (extra is not null) builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(extra));
    }
}



