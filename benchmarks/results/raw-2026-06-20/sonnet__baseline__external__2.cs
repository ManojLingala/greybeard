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
    private readonly string _baseUrl;

    public PaymentGatewayClient(HttpClient httpClient, string apiKey, string baseUrl)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
    }

    /// <summary>
    /// Charges a card by calling an external payment gateway over HTTP.
    /// </summary>
    /// <param name="cardNumber">The card number to charge.</param>
    /// <param name="expiryMonth">Card expiry month (1-12).</param>
    /// <param name="expiryYear">Card expiry year (4-digit).</param>
    /// <param name="cvv">Card CVV/CVC code.</param>
    /// <param name="amountInCents">Amount to charge in cents (e.g., 1999 = $19.99).</param>
    /// <param name="currency">ISO 4217 currency code (e.g., "USD").</param>
    /// <param name="description">Optional description for the charge.</param>
    /// <returns>A ChargeResult containing the transaction ID and status.</returns>
    public async Task<ChargeResult> ChargeCardAsync(
        string cardNumber,
        int expiryMonth,
        int expiryYear,
        string cvv,
        long amountInCents,
        string currency,
        string description = null)
    {
        if (string.IsNullOrWhiteSpace(cardNumber))
            throw new ArgumentException("Card number is required.", nameof(cardNumber));
        if (expiryMonth < 1 || expiryMonth > 12)
            throw new ArgumentOutOfRangeException(nameof(expiryMonth), "Expiry month must be between 1 and 12.");
        if (expiryYear < DateTime.UtcNow.Year)
            throw new ArgumentOutOfRangeException(nameof(expiryYear), "Card is expired.");
        if (string.IsNullOrWhiteSpace(cvv))
            throw new ArgumentException("CVV is required.", nameof(cvv));
        if (amountInCents <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountInCents), "Amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency is required.", nameof(currency));

        var requestBody = new
        {
            card = new
            {
                number = cardNumber,
                exp_month = expiryMonth,
                exp_year = expiryYear,
                cvc = cvv
            },
            amount = amountInCents,
            currency = currency.ToUpperInvariant(),
            description = description
        };

        var jsonContent = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/charges")
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
            throw new PaymentGatewayException("Failed to connect to payment gateway.", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            GatewayErrorResponse errorResponse = null;
            try
            {
                errorResponse = JsonSerializer.Deserialize<GatewayErrorResponse>(responseBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException) { /* ignore parse errors on error responses */ }

            throw new PaymentGatewayException(
                $"Payment gateway returned {(int)response.StatusCode}: {errorResponse?.Error?.Message ?? responseBody}");
        }

        GatewayChargeResponse chargeResponse;
        try
        {
            chargeResponse = JsonSerializer.Deserialize<GatewayChargeResponse>(responseBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new PaymentGatewayException("Failed to parse payment gateway response.", ex);
        }

        return new ChargeResult
        {
            TransactionId = chargeResponse.Id,
            Status = chargeResponse.Status,
            AmountInCents = chargeResponse.Amount,
            Currency = chargeResponse.Currency,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(chargeResponse.Created).UtcDateTime
        };
    }
}

public class ChargeResult
{
    public string TransactionId { get; set; }
    public string Status { get; set; }
    public long AmountInCents { get; set; }
    public string Currency { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsSuccessful => Status == "succeeded";
}

public class GatewayChargeResponse
{
    public string Id { get; set; }
    public string Status { get; set; }
    public long Amount { get; set; }
    public string Currency { get; set; }
    public long Created { get; set; }
}

public class GatewayErrorResponse
{
    public GatewayError Error { get; set; }
}

public class GatewayError
{
    public string Message { get; set; }
    public string Code { get; set; }
    public string Type { get; set; }
}

public class PaymentGatewayException : Exception
{
    public PaymentGatewayException(string message) : base(message) { }
    public PaymentGatewayException(string message, Exception innerException) : base(message, innerException) { }
}
