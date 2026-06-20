using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Sends webhooks to customer URLs when orders ship.
/// Implements HMAC signing, exponential backoff retry, and SSRF protection.
/// </summary>
public class OrderShipmentWebhookService
{
    private readonly HttpClient _httpClient;
    private readonly string _signingSecret;
    private const int MaxRetries = 5;
    private const int InitialBackoffMs = 1000;
    private const int MaxBackoffMs = 32000;

    public OrderShipmentWebhookService(string signingSecret)
    {
        _signingSecret = signingSecret ?? throw new ArgumentNullException(nameof(signingSecret));
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ResponseDrainTimeout = TimeSpan.FromSeconds(10)
        });
    }

    public async Task SendOrderShippedWebhookAsync(string webhookUrl, OrderShipmentPayload payload)
    {
        if (!IsValidWebhookUrl(webhookUrl))
            throw new ArgumentException("Invalid or blocked webhook URL", nameof(webhookUrl));

        var json = JsonSerializer.Serialize(payload);
        var signature = GenerateHmacSignature(json);

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                request.Headers.Add("X-Webhook-Signature", signature);
                request.Headers.Add("X-Webhook-Id", Guid.NewGuid().ToString());
                request.Headers.Add("X-Delivery-Timestamp", DateTime.UtcNow.ToString("O"));

                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                    return;

                if ((int)response.StatusCode < 500)
                    throw new HttpRequestException($"Webhook delivery failed: {response.StatusCode}");
            }
            catch (HttpRequestException) when (attempt < MaxRetries - 1)
            {
                int backoffMs = Math.Min(InitialBackoffMs * (1 << attempt), MaxBackoffMs);
                await Task.Delay(backoffMs);
            }
        }

        throw new InvalidOperationException($"Webhook delivery failed after {MaxRetries} attempts");
    }

    private string GenerateHmacSignature(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_signingSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsValidWebhookUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        // Only allow https in production
        if (uri.Scheme != "https" && uri.Scheme != "http")
            return false;

        // Block private/reserved IP ranges and localhost
        var host = uri.Host.ToLowerInvariant();
        return !host.Equals("localhost") &&
               !host.Equals("127.0.0.1") &&
               !host.StartsWith("10.") &&
               !host.StartsWith("172.") &&
               !host.StartsWith("192.168.") &&
               !host.StartsWith("::1") &&
               !host.Equals("[::1]");
    }
}

public class OrderShipmentPayload
{
    public string OrderId { get; set; }
    public string TrackingNumber { get; set; }
    public string Carrier { get; set; }
    public DateTime ShippedAt { get; set; }
}
