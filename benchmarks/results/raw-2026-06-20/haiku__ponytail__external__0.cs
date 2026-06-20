using System;
using System.Net.Http;
using System.Threading.Tasks;

public class PaymentService
{
    private readonly HttpClient _httpClient;
    private readonly string _gatewayUrl;

    public PaymentService(string gatewayUrl)
    {
        _gatewayUrl = gatewayUrl;
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Charges a card by calling an external payment gateway over HTTP.
    /// </summary>
    public async Task<PaymentResult> ChargeCardAsync(string cardToken, decimal amount, string currency)
    {
        var request = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                token = cardToken,
                amount = amount,
                currency = currency
            }),
            System.Text.Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.PostAsync(_gatewayUrl, request);
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            return new PaymentResult
            {
                Success = false,
                ErrorMessage = $"Payment gateway returned {response.StatusCode}: {content}"
            };
        }

        var result = System.Text.Json.JsonSerializer.Deserialize<PaymentResult>(content);
        return result ?? new PaymentResult { Success = false, ErrorMessage = "Invalid response" };
    }
}

public class PaymentResult
{
    public bool Success { get; set; }
    public string TransactionId { get; set; }
    public string ErrorMessage { get; set; }
}
