// Sends a webhook to a customer's URL when an order ships.
//
// Reliability + security with the laziest correct approach (YAGNI):
//   - HMAC-SHA256 signature so the customer can verify authenticity (no extra deps).
//   - Timestamp in the signed payload to let the customer reject replays.
//   - Idempotency key so a retried delivery is recognized as the same event.
//   - HTTPS-only + IHttpClientFactory's typed client with a built-in retry/timeout
//     pipeline (Microsoft.Extensions.Http.Resilience -> Polly) instead of a
//     hand-rolled retry loop.
//
// Wiring (Program.cs):
//   builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection("Webhook"));
//   builder.Services
//       .AddHttpClient<WebhookSender>()
//       .AddStandardResilienceHandler();   // timeout + retry w/ backoff + circuit breaker
//
// Config (appsettings.json):
//   "Webhook": { "SigningSecret": "<from secret store, not source control>" }

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

public sealed class WebhookOptions
{
    // Pull from a secret store / env var; never commit the real value.
    public string SigningSecret { get; set; } = "";
}

public sealed record OrderShippedEvent(
    string OrderId,
    string TrackingNumber,
    string Carrier,
    DateTimeOffset ShippedAt);

public sealed class WebhookSender
{
    private readonly HttpClient _http;
    private readonly byte[] _secret;

    // HttpClient is configured (and resilience added) by IHttpClientFactory.
    public WebhookSender(HttpClient http, IOptions<WebhookOptions> options)
    {
        _http = http;

        var secret = options.Value.SigningSecret;
        if (string.IsNullOrEmpty(secret))
            throw new InvalidOperationException("Webhook:SigningSecret is not configured.");
        _secret = Encoding.UTF8.GetBytes(secret);
    }

    /// <summary>
    /// Delivers the "order shipped" event to the customer's webhook URL.
    /// Retries/timeouts/backoff are handled by the resilience handler on HttpClient.
    /// </summary>
    public async Task SendOrderShippedAsync(
        Uri customerUrl,
        OrderShippedEvent payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customerUrl);
        ArgumentNullException.ThrowIfNull(payload);

        // Security: only ever POST secrets/signatures over TLS.
        if (customerUrl.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Webhook URL must use HTTPS.", nameof(customerUrl));

        // Serialize once so the bytes we sign are exactly the bytes we send.
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);

        // Stable id for this delivery -> customer can dedupe safe retries.
        var idempotencyKey = $"order-shipped:{payload.OrderId}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        // Sign timestamp + body; binding the timestamp lets the customer
        // reject stale/replayed deliveries.
        var signature = ComputeSignature(timestamp, body);

        using var request = new HttpRequestMessage(HttpMethod.Post, customerUrl)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.TryAddWithoutValidation("X-Webhook-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Webhook-Signature", $"sha256={signature}");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private string ComputeSignature(string timestamp, byte[] body)
    {
        // Signed message = "<timestamp>." + raw JSON body.
        using var hmac = new HMACSHA256(_secret);
        hmac.TransformBlock(Encoding.UTF8.GetBytes(timestamp + "."), 0, timestamp.Length + 1, null, 0);
        hmac.TransformFinalBlock(body, 0, body.Length);
        return Convert.ToHexStringLower(hmac.Hash!);
    }
}
