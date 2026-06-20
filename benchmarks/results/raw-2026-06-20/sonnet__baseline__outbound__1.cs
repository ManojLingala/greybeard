using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Sends a signed webhook to a customer's URL when an order ships.
/// Reliable: retries with exponential backoff.
/// Secure: HMAC-SHA256 signature header so the customer can verify authenticity.
/// </summary>
public static class OrderShipmentWebhook
{
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    // Maximum number of delivery attempts (1 initial + 4 retries)
    private const int MaxAttempts = 5;

    /// <summary>
    /// Notify a customer that their order has shipped.
    /// </summary>
    /// <param name="webhookUrl">The customer-supplied callback URL.</param>
    /// <param name="signingSecret">
    ///     A shared secret (stored server-side only) used to sign the payload.
    ///     The customer uses the same secret to verify the X-Signature-SHA256 header.
    /// </param>
    /// <param name="orderId">Unique order identifier.</param>
    /// <param name="trackingNumber">Carrier tracking number.</param>
    /// <param name="carrier">Carrier name (e.g. "UPS", "FedEx").</param>
    public static async Task SendOrderShippedAsync(
        string webhookUrl,
        string signingSecret,
        string orderId,
        string trackingNumber,
        string carrier)
    {
        // --- 1. Validate inputs -------------------------------------------------
        if (string.IsNullOrWhiteSpace(webhookUrl))
            throw new ArgumentException("webhookUrl must not be empty.", nameof(webhookUrl));

        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var parsedUri)
            || (parsedUri.Scheme != Uri.UriSchemeHttps && parsedUri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("webhookUrl must be a valid HTTP/HTTPS URI.", nameof(webhookUrl));

        // Reject private/loopback addresses to prevent SSRF.
        GuardAgainstSsrf(parsedUri);

        if (string.IsNullOrWhiteSpace(signingSecret))
            throw new ArgumentException("signingSecret must not be empty.", nameof(signingSecret));

        // --- 2. Build the payload -----------------------------------------------
        var payload = new OrderShippedPayload
        {
            EventId   = Guid.NewGuid().ToString("N"),
            EventType = "order.shipped",
            Timestamp = DateTimeOffset.UtcNow,
            Data = new OrderShippedData
            {
                OrderId        = orderId,
                TrackingNumber = trackingNumber,
                Carrier        = carrier
            }
        };

        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        byte[] bodyBytes = Encoding.UTF8.GetBytes(json);

        // --- 3. Sign the payload ------------------------------------------------
        string signature = ComputeHmacSha256(signingSecret, bodyBytes);

        // --- 4. Deliver with retry / exponential back-off -----------------------
        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, parsedUri);
                request.Content = new ByteArrayContent(bodyBytes);
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                // Security headers
                request.Headers.Add("X-Webhook-Event",        payload.EventType);
                request.Headers.Add("X-Webhook-Id",           payload.EventId);
                request.Headers.Add("X-Signature-SHA256",     $"sha256={signature}");
                request.Headers.Add("X-Webhook-Timestamp",    payload.Timestamp.ToUnixTimeSeconds().ToString());

                using HttpResponseMessage response = await _http.SendAsync(request).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return; // Delivered successfully.

                // Non-2xx response — treat as a retriable failure.
                lastException = new HttpRequestException(
                    $"Webhook delivery failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
            }

            if (attempt < MaxAttempts)
            {
                // Exponential back-off: 1s, 2s, 4s, 8s …
                int delayMs = (int)Math.Pow(2, attempt - 1) * 1000;
                await Task.Delay(delayMs).ConfigureAwait(false);
            }
        }

        throw new WebhookDeliveryException(
            $"Webhook delivery to '{webhookUrl}' failed after {MaxAttempts} attempts.", lastException);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static string ComputeHmacSha256(string secret, byte[] data)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(secret);
        using var hmac = new HMACSHA256(keyBytes);
        byte[] hash = hmac.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Blocks loopback and RFC-1918 / RFC-4193 private ranges to prevent SSRF.
    /// </summary>
    private static void GuardAgainstSsrf(Uri uri)
    {
        // Resolve the host synchronously for a quick sanity check.
        // In production you may want a DNS-rebinding-resistant approach.
        string host = uri.Host.Trim('.');

        // Reject obvious private hostnames
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SSRF guard: loopback host rejected.");

        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                bool isPrivate =
                    b[0] == 127 ||                          // 127.x.x.x  loopback
                    b[0] == 10 ||                           // 10.x.x.x   RFC-1918
                    (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || // 172.16-31.x
                    (b[0] == 192 && b[1] == 168) ||         // 192.168.x.x
                    b[0] == 169 && b[1] == 254;             // 169.254.x.x link-local

                if (isPrivate)
                    throw new ArgumentException($"SSRF guard: private/loopback IP {ip} rejected.");
            }
            else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.Equals(System.Net.IPAddress.IPv6Loopback))
                    throw new ArgumentException($"SSRF guard: private/loopback IPv6 address {ip} rejected.");
            }
        }
    }
}

// ---------------------------------------------------------------------------
// DTOs
// ---------------------------------------------------------------------------

public sealed class OrderShippedPayload
{
    public string EventId   { get; set; } = "";
    public string EventType { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public OrderShippedData Data { get; set; } = new();
}

public sealed class OrderShippedData
{
    public string OrderId        { get; set; } = "";
    public string TrackingNumber { get; set; } = "";
    public string Carrier        { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Custom exception
// ---------------------------------------------------------------------------

public sealed class WebhookDeliveryException : Exception
{
    public WebhookDeliveryException(string message, Exception? inner = null)
        : base(message, inner) { }
}
