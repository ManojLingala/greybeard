using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class PaymentProcessor
{
    private readonly HttpClient _httpClient;
    private readonly string _gatewayUrl;
    private readonly string _apiKey;

    public PaymentProcessor(string gatewayUrl, string apiKey)
    {
        _gatewayUrl = gatewayUrl;
        _apiKey = apiKey;
        _httpClient = new HttpClient();
    }

    public async Task<bool> ChargeCardAsync(string cardNumber, string cardHolder, string expiryDate, string cvv, decimal amount, string currency)
    {
        var request = new
        {
            card_number = cardNumber,
            cardholder = cardHolder,
            expiry_date = expiryDate,
            cvv = cvv,
            amount = amount,
            currency = currency
        };

        var jsonContent = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json"
        );

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_gatewayUrl}/charge")
        {
            Content = jsonContent
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");

        try
        {
            var response = await _httpClient.SendAsync(httpRequest);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<JsonElement>(responseContent);
                return result.TryGetProperty("success", out var successProperty) && successProperty.GetBoolean();
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Payment error: {ex.Message}");
            return false;
        }
    }
}
