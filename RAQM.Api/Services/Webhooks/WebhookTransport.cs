using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RAQM.Api.Services.Integrations;

namespace RAQM.Api.Services.Webhooks;

public sealed class WebhookTransport(IConfiguration config)
{
    public static readonly string[] EventTypes = ["journal.posted", "invoice.posted", "supplier-bill.posted", "payment.settled",
        "bank-reconciliation.finalized", "inventory-receipt.posted", "inventory-issue.posted", "asset-capitalized", "depreciation.posted",
        "period.closed", "fx-revaluation.posted", "consolidation.published", "tax-return.approved"];

    public Uri Endpoint(string input)
    {
        if (input is null || input.Length > 500 || !Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.HostNameType != UriHostNameType.Dns ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0 || uri.IdnHost.EndsWith('.') ||
            !config.GetSection("Integrations:WebhookHosts").GetChildren().Any(x => string.Equals(x.Value, uri.IdnHost, StringComparison.OrdinalIgnoreCase)))
            throw IntegrationAdministration.Invalid("Use an allowlisted HTTPS DNS endpoint on port 443, without credentials, query or fragment.");
        return uri;
    }

    public static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            var (a,b,c) = (bytes[0], bytes[1], bytes[2]);
            return a is > 0 and < 224 and not (10 or 127) &&
                !(a == 100 && b is >= 64 and <= 127 || a == 169 && b == 254 || a == 172 && b is >= 16 and <= 31 ||
                  a == 192 && (b == 168 || b == 0 || b == 88 && c == 99) || a == 198 && b is 18 or 19 ||
                  a == 198 && b == 51 && c == 100 || a == 203 && b == 0 && c == 113);
        }
        // Global unicast only, with transition, documentation and special-purpose ranges excluded.
        return (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && (bytes[1] == 0x02 ||
            bytes[1] == 0x01 && (bytes[2] < 2 || bytes[2] == 0x0d && bytes[3] == 0xb8))) &&
            !(bytes[0] == 0x3f && (bytes[1] & 0xf0) == 0xf0);
    }

    public static string Signature(string secret, long unix, Guid eventId, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes(unix.ToString(CultureInfo.InvariantCulture) + "." + eventId.ToString("D") + ".");
        var key = Convert.FromHexString(secret);
        try
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            hmac.AppendData(prefix);
            hmac.AppendData(body);
            return "v1=" + Convert.ToHexString(hmac.GetHashAndReset()).ToLowerInvariant();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static bool Verify(string secret, long unix, Guid eventId, byte[] body, string signature, long now) =>
        unix >= now - 300 && unix <= now + 300 && signature.Length == 67 &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Signature(secret, unix, eventId, body)), Encoding.ASCII.GetBytes(signature));

    public async Task<int> Send(string endpoint, string secret, int version, Guid eventId, Guid deliveryId, long unix, string payload, CancellationToken ct)
    {
        var uri = Endpoint(endpoint);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false, MaxResponseHeadersLength = 16, MaxResponseDrainSize = 0,
            ConnectCallback = async (context, token) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host, uri.IdnHost, StringComparison.OrdinalIgnoreCase) || context.DnsEndPoint.Port != 443)
                    throw new HttpRequestException("Unexpected webhook destination.");
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                if (addresses.Length == 0 || addresses.Any(x => !PublicAddress(x)))
                    throw new HttpRequestException("Webhook DNS contains a prohibited address.");
                var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(addresses[0], 443), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        var body = Encoding.UTF8.GetBytes(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        request.Headers.Add("X-RAQM-Event", eventId.ToString("D"));
        request.Headers.Add("X-RAQM-Delivery", deliveryId.ToString("D"));
        request.Headers.Add("X-RAQM-Timestamp", unix.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-RAQM-Secret-Version", version.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-RAQM-Signature", Signature(secret, unix, eventId, body));
        using var result = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return (int)result.StatusCode;
    }
}
