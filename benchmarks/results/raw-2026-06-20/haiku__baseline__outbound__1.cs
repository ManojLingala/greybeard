using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class OrderShippingWebhook
{
    private readonly HttpClient _httpClient;
    private readonly string _webhookSecret;
    private const int MaxRetries = 3;
    private const int InitialDelayMs = 1000;

    public OrderShippingWebhook(HttpClient httpClient, string webhookSecret)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _webhookSecret = webhookSecret ?? throw new ArgumentNullException(nameof(webhookSecret));
    }

    public async Task SendOrderShippedWebhookAsync(string customerWebhookUrl, OrderShippedPayload payload)
    {
        if (string.IsNullOrEmpty(customerWebhookUrl))
            throw new ArgumentException("Webhook URL cannot be empty", nameof(customerWebhookUrl));

        if (!Uri.TryCreate(customerWebhookUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException("Invalid webhook URL", nameof(customerWebhookUrl));

        // Only allow https for security
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Webhook URL must use HTTPS", nameof(customerWebhookUrl));

        var jsonPayload = JsonSerializer.Serialize(payload);
        var signature = GenerateSignature(jsonPayload);

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                using (var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json"))
                {
                    content.Headers.Add("X-Webhook-Signature", signature);
                    content.Headers.Add("X-Webhook-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());

                    var request = new HttpRequestMessage(HttpMethod.Post, customerWebhookUrl)
                    {
                        Content = content
                    };

                    var response = await _httpClient.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }

                    // Retry on server errors (5xx), but not on client errors (4xx)
                    if ((int)response.StatusCode < 500)
                    {
                        throw new HttpRequestException($"Webhook delivery failed with status {response.StatusCode}");
                    }
                }
            }
            catch (HttpRequestException) when (attempt < MaxRetries - 1)
            {
                // Exponential backoff before retry
                await Task.Delay(InitialDelayMs * (1 << attempt));
            }
        }

        throw new InvalidOperationException($"Failed to deliver webhook after {MaxRetries} attempts");
    }

    private string GenerateSignature(string payload)
    {
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_webhookSecret)))
        {
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            return "sha256=" + Convert.ToHexString(hash).ToLower();
        }
    }
}

public class OrderShippedPayload
{
    public string OrderId { get; set; }
    public string TrackingNumber { get; set; }
    public string Carrier { get; set; }
    public DateTime ShippedAt { get; set; }
    public decimal Amount { get; set; }
}
