using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class PaymentGatewayClient
{
    private readonly HttpClient _httpClient;
    private readonly string _gatewayBaseUrl;
    private readonly string _apiKey;

    public PaymentGatewayClient(HttpClient httpClient, string gatewayBaseUrl, string apiKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _gatewayBaseUrl = gatewayBaseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(gatewayBaseUrl));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    /// <summary>
    /// Charges a card by calling an external payment gateway over HTTP.
    /// </summary>
    /// <param name="cardToken">Tokenized card identifier (never raw card data)</param>
    /// <param name="amountInCents">Amount to charge in the smallest currency unit (e.g., cents)</param>
    /// <param name="currency">ISO 4217 currency code (e.g., "USD")</param>
    /// <param name="description">Human-readable description of the charge</param>
    /// <returns>A ChargeResult containing the transaction ID and status</returns>
    public async Task<ChargeResult> ChargeCardAsync(
        string cardToken,
        long amountInCents,
        string currency,
        string description)
    {
        if (string.IsNullOrWhiteSpace(cardToken))
            throw new ArgumentException("Card token must not be empty.", nameof(cardToken));
        if (amountInCents <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountInCents), "Amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency must not be empty.", nameof(currency));

        var requestBody = new
        {
            source = cardToken,
            amount = amountInCents,
            currency = currency.ToUpperInvariant(),
            description = description
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_gatewayBaseUrl}/v1/charges")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request);
        }
        catch (HttpRequestException ex)
        {
            throw new PaymentGatewayException("Failed to reach the payment gateway.", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            // Attempt to parse a structured error from the gateway
            string gatewayMessage = null;
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("error", out var errorEl) &&
                    errorEl.TryGetProperty("message", out var msgEl))
                {
                    gatewayMessage = msgEl.GetString();
                }
            }
            catch (JsonException) { /* ignore parse errors on error responses */ }

            throw new PaymentGatewayException(
                $"Payment gateway returned {(int)response.StatusCode}: {gatewayMessage ?? response.ReasonPhrase}");
        }

        using var responseDoc = JsonDocument.Parse(responseBody);
        var root = responseDoc.RootElement;

        return new ChargeResult
        {
            TransactionId = root.GetProperty("id").GetString(),
            Status = root.GetProperty("status").GetString(),
            AmountInCents = root.GetProperty("amount").GetInt64(),
            Currency = root.GetProperty("currency").GetString(),
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("created").GetInt64())
        };
    }
}

public class ChargeResult
{
    public string TransactionId { get; init; }
    public string Status { get; init; }
    public long AmountInCents { get; init; }
    public string Currency { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public class PaymentGatewayException : Exception
{
    public PaymentGatewayException(string message) : base(message) { }
    public PaymentGatewayException(string message, Exception innerException) : base(message, innerException) { }
}
