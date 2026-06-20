using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Sends secure webhooks to customer URLs when orders ship.
/// Implements reliability through retries and security through HMAC signing.
/// </summary>
public class OrderShipmentWebhookService
{
    private readonly HttpClient _httpClient;
    private readonly string _webhookSecret;
    private readonly int _maxRetries;
    private readonly int _timeoutSeconds;

    public OrderShipmentWebhookService(
        HttpClient httpClient,
        string webhookSecret,
        int maxRetries = 3,
        int timeoutSeconds = 30)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _webhookSecret = webhookSecret ?? throw new ArgumentNullException(nameof(webhookSecret));
        _maxRetries = maxRetries;
        _timeoutSeconds = timeoutSeconds;
    }

    /// <summary>
    /// Sends a webhook notification when an order ships.
    /// </summary>
    public async Task<WebhookResult> SendOrderShipmentWebhookAsync(
        string customerWebhookUrl,
        OrderShipmentEvent shipmentEvent)
    {
        if (string.IsNullOrWhiteSpace(customerWebhookUrl))
            throw new ArgumentException("Webhook URL cannot be empty", nameof(customerWebhookUrl));

        if (shipmentEvent == null)
            throw new ArgumentNullException(nameof(shipmentEvent));

        // Validate URL to prevent SSRF attacks
        if (!IsValidWebhookUrl(customerWebhookUrl))
            return WebhookResult.Failure("Invalid or unsafe webhook URL");

        var payload = JsonSerializer.Serialize(shipmentEvent);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = GenerateSignature(payload, timestamp);

        var attempt = 0;
        while (attempt < _maxRetries)
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(
                    TimeSpan.FromSeconds(_timeoutSeconds));

                var request = new HttpRequestMessage(HttpMethod.Post, customerWebhookUrl)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };

                // Add security headers
                request.Headers.Add("X-Webhook-Signature", $"sha256={signature}");
                request.Headers.Add("X-Webhook-Timestamp", timestamp);
                request.Headers.Add("X-Event-Type", "order.shipped");

                var response = await _httpClient.SendAsync(request, cts.Token);

                if (response.IsSuccessStatusCode)
                    return WebhookResult.Success();

                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                    return WebhookResult.Failure($"Client error: {response.StatusCode}");

                attempt++;
            }
            catch (System.Threading.Tasks.TaskCanceledException)
            {
                attempt++;
                if (attempt >= _maxRetries)
                    return WebhookResult.Failure("Request timeout after retries");
            }
            catch (HttpRequestException ex)
            {
                attempt++;
                if (attempt >= _maxRetries)
                    return WebhookResult.Failure($"HTTP error: {ex.Message}");
            }

            if (attempt < _maxRetries)
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
        }

        return WebhookResult.Failure("Failed after maximum retries");
    }

    /// <summary>
    /// Generates HMAC-SHA256 signature for webhook payload.
    /// </summary>
    private string GenerateSignature(string payload, string timestamp)
    {
        var message = $"{timestamp}.{payload}";
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_webhookSecret)))
        {
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
            return Convert.ToHexString(hash).ToLower();
        }
    }

    /// <summary>
    /// Validates webhook URL to prevent SSRF attacks.
    /// </summary>
    private bool IsValidWebhookUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        // Only allow HTTPS for security
        if (uri.Scheme != "https")
            return false;

        // Block private IP ranges
        var host = uri.Host.ToLowerInvariant();
        if (host == "localhost" || host == "127.0.0.1" ||
            host.StartsWith("192.168.") || host.StartsWith("10.") ||
            host.StartsWith("172.") || host == "::1")
            return false;

        return true;
    }
}

/// <summary>
/// Represents a shipment event to send to the customer.
/// </summary>
public class OrderShipmentEvent
{
    public string OrderId { get; set; }
    public string TrackingNumber { get; set; }
    public string Carrier { get; set; }
    public DateTime ShippedAt { get; set; }
    public List<string> TrackingUrls { get; set; }
    public string Status { get; set; }
}

/// <summary>
/// Result of a webhook delivery attempt.
/// </summary>
public class WebhookResult
{
    public bool Success { get; private set; }
    public string Message { get; private set; }

    public static WebhookResult Success() =>
        new WebhookResult { Success = true, Message = "Webhook delivered successfully" };

    public static WebhookResult Failure(string message) =>
        new WebhookResult { Success = false, Message = message };
}
