using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;

namespace RAQM.Api.Configurations;

public static class WebhookKeyRing
{
    public static void Configure(IServiceCollection services, IConfiguration config, bool required)
    {
        var protection = services.AddDataProtection();
        var directory = config["WebhookKeys:Directory"];
        if (string.IsNullOrWhiteSpace(directory))
        {
            if (required) throw new InvalidOperationException("Configure a shared encrypted webhook key ring.");
            return;
        }

        var name = config["WebhookKeys:ApplicationName"];
        if (string.IsNullOrWhiteSpace(name) || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new InvalidOperationException("Configure a webhook application name and an existing absolute key directory.");

        var current = Load(config.GetSection("WebhookKeys:Certificate"));
        var previous = config.GetSection("WebhookKeys:PreviousCertificates").GetChildren().Select(Load).ToArray();
        protection.SetApplicationName(name)
            .PersistKeysToFileSystem(new DirectoryInfo(directory))
            .ProtectKeysWithCertificate(current)
            .UnprotectKeysWithAnyCertificate([current, .. previous]);
    }

    private static X509Certificate2 Load(IConfiguration section)
    {
        var path = section["Path"];
        var passwordPath = section["PasswordFile"];
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(passwordPath))
            throw new InvalidOperationException("Configure webhook certificate and password files.");
        var password = File.ReadAllText(passwordPath).TrimEnd('\r', '\n');
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException("The webhook key-ring certificate requires a private key.");
        return certificate;
    }
}
