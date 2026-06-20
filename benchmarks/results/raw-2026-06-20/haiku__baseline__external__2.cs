using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

public class PaymentGateway
{
    private readonly HttpClient _httpClient;
    private readonly string _gatewayUrl;
    private readonly string _apiKey;

    public PaymentGateway(string gatewayUrl, string apiKey)
    {
        _gatewayUrl = gatewayUrl;
        _apiKey = apiKey;
        _httpClient = new HttpClient();
    }

    public async Task<PaymentResult> ChargeCardAsync(string cardNumber, string expiryDate, string cvv, decimal amount, string currency)
    {
        try
        {
            var chargeRequest = new
            {
                card_number = cardNumber,
                expiry_date = expiryDate,
                cvv = cvv,
                amount = amount,
                currency = currency
            };

            var requestContent = new StringContent(
                JsonConvert.SerializeObject(chargeRequest),
                System.Text.Encoding.UTF8,
                "application/json"
            );

            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");

            var response = await _httpClient.PostAsync(
                $"{_gatewayUrl}/charge",
                requestContent
            );

            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var result = JsonConvert.DeserializeObject<PaymentResult>(responseContent);
                result.Success = true;
                return result;
            }
            else
            {
                return new PaymentResult
                {
                    Success = false,
                    ErrorMessage = $"Payment gateway returned status {response.StatusCode}: {responseContent}"
                };
            }
        }
        catch (Exception ex)
        {
            return new PaymentResult
            {
                Success = false,
                ErrorMessage = $"Exception occurred during payment: {ex.Message}"
            };
        }
    }
}

public class PaymentResult
{
    [JsonProperty("transaction_id")]
    public string TransactionId { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    public bool Success { get; set; }

    public string ErrorMessage { get; set; }
}
