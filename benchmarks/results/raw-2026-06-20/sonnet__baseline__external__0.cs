using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class PaymentGatewayClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _gatewayBaseUrl;

    public PaymentGatewayClient(HttpClient httpClient, string apiKey, string gatewayBaseUrl)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _gatewayBaseUrl = gatewayBaseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(gatewayBaseUrl));
    }

    /// <summary>
    /// Charges a card by calling an external payment gateway over HTTP.
    /// </summary>
    /// <param name="cardToken">A tokenized card reference (never raw card data).</param>
    /// <param name="amountInCents">Amount to charge in the smallest currency unit (e.g. cents).</param>
    /// <param name="currency">ISO 4217 currency code, e.g. "USD".</param>
    /// <param name="idempotencyKey">Unique key to prevent duplicate charges on retry.</param>
    /// <returns>A <see cref="ChargeResult"/> describing the outcome.</returns>
    public async Task<ChargeResult> ChargeCardAsync(
        string cardToken,
        long amountInCents,
        string currency,
        string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(cardToken))
            throw new ArgumentException("Card token must not be null or empty.", nameof(cardToken));
        if (amountInCents <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountInCents), "Amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency must not be null or empty.", nameof(currency));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key must not be null or empty.", nameof(idempotencyKey));

        var requestBody = new
        {
            card_token = cardToken,
            amount = amountInCents,
            currency = currency.ToUpperInvariant()
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_gatewayBaseUrl}/v1/charges")
        {
            Content = content
        };

        // Bearer-token authentication
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        // Idempotency header prevents duplicate charges if the request is retried
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PaymentGatewayException("Network error while contacting the payment gateway.", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new PaymentGatewayException(
                $"Payment gateway returned {(int)response.StatusCode} {response.ReasonPhrase}. Body: {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        return new ChargeResult
        {
            ChargeId = root.GetProperty("id").GetString(),
            Status = root.GetProperty("status").GetString(),
            AmountCharged = root.GetProperty("amount").GetInt64(),
            Currency = root.GetProperty("currency").GetString(),
            CreatedAt = root.TryGetProperty("created_at", out var ts)
                ? ts.GetDateTimeOffset()
                : DateTimeOffset.UtcNow
        };
    }
}

public class ChargeResult
{
    public string ChargeId { get; init; }
    public string Status { get; init; }
    public long AmountCharged { get; init; }
    public string Currency { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public class PaymentGatewayException : Exception
{
    public PaymentGatewayException(string message) : base(message) { }
    public PaymentGatewayException(string message, Exception inner) : base(message, inner) { }
}
