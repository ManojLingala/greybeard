using System;
using System.Net.Http;
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
        _httpClient = httpClient;
        _gatewayBaseUrl = gatewayBaseUrl.TrimEnd('/');
        _apiKey = apiKey;
    }

    public async Task<ChargeResult> ChargeCardAsync(string cardToken, decimal amount, string currency, string description)
    {
        var requestBody = new
        {
            card_token = cardToken,
            amount = (long)(amount * 100), // convert to smallest currency unit (e.g. cents)
            currency = currency.ToUpperInvariant(),
            description = description
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_gatewayBaseUrl}/charges")
        {
            Content = content
        };
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");

        using var response = await _httpClient.SendAsync(request);

        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new PaymentException(
                $"Payment gateway returned {(int)response.StatusCode}: {responseJson}");
        }

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        return new ChargeResult
        {
            ChargeId = root.GetProperty("id").GetString(),
            Status = root.GetProperty("status").GetString(),
            Amount = amount,
            Currency = currency
        };
    }
}

public class ChargeResult
{
    public string ChargeId { get; set; }
    public string Status { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; }
}

public class PaymentException : Exception
{
    public PaymentException(string message) : base(message) { }
}
