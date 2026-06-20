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

    public async Task<PaymentResult> ChargeCard(string cardNumber, string cardHolder, string expiryDate, string cvv, decimal amount)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"{_gatewayUrl}/charge"))
        {
            var payload = new
            {
                cardNumber,
                cardHolder,
                expiryDate,
                cvv,
                amount
            };

            request.Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var result = System.Text.Json.JsonSerializer.Deserialize<dynamic>(responseContent);
                return new PaymentResult { Success = true, TransactionId = result.ToString() };
            }

            return new PaymentResult { Success = false, Error = responseContent };
        }
    }
}

public class PaymentResult
{
    public bool Success { get; set; }
    public string TransactionId { get; set; }
    public string Error { get; set; }
}
