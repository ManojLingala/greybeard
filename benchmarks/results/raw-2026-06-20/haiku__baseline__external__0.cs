using System;
using System.Net.Http;
using System.Threading.Tasks;

public class PaymentProcessor
{
    private readonly HttpClient _httpClient;
    private readonly string _gatewayUrl;

    public PaymentProcessor(string gatewayUrl)
    {
        _httpClient = new HttpClient();
        _gatewayUrl = gatewayUrl;
    }

    public async Task<bool> ChargeCard(string cardNumber, string expiryDate, string cvv, decimal amount, string currency = "USD")
    {
        try
        {
            var chargeRequest = new
            {
                card_number = cardNumber,
                expiry_date = expiryDate,
                cvv = cvv,
                amount = amount,
                currency = currency,
                timestamp = DateTime.UtcNow.ToString("O")
            };

            var jsonContent = System.Text.Json.JsonSerializer.Serialize(chargeRequest);
            var httpContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(_gatewayUrl, httpContent);

            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                var jsonResponse = System.Text.Json.JsonDocument.Parse(responseBody);
                var root = jsonResponse.RootElement;

                if (root.TryGetProperty("success", out var successProperty) && successProperty.GetBoolean())
                {
                    return true;
                }
            }

            return false;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"HTTP request error: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing charge: {ex.Message}");
            return false;
        }
    }
}
