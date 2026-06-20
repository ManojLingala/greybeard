using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;

public class PaymentProcessor
{
    private readonly HttpClient _httpClient;
    private readonly string _paymentGatewayUrl;
    private readonly string _apiKey;

    public PaymentProcessor(string paymentGatewayUrl, string apiKey)
    {
        _paymentGatewayUrl = paymentGatewayUrl;
        _apiKey = apiKey;
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Charges a card by calling an external payment gateway over HTTP.
    /// </summary>
    /// <param name="cardNumber">The credit card number</param>
    /// <param name="expiryMonth">Card expiry month (1-12)</param>
    /// <param name="expiryYear">Card expiry year</param>
    /// <param name="cvv">Card CVV security code</param>
    /// <param name="amount">Amount to charge in cents</param>
    /// <param name="currency">Currency code (e.g., "USD")</param>
    /// <returns>Payment result containing transaction ID and status</returns>
    public async Task<PaymentResult> ChargeCardAsync(
        string cardNumber,
        int expiryMonth,
        int expiryYear,
        string cvv,
        decimal amount,
        string currency)
    {
        try
        {
            var chargeRequest = new
            {
                card_number = cardNumber,
                expiry_month = expiryMonth,
                expiry_year = expiryYear,
                cvv = cvv,
                amount = (int)(amount * 100), // Convert to cents
                currency = currency
            };

            var json = JsonSerializer.Serialize(chargeRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Add authorization header
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");

            var response = await _httpClient.PostAsync(_paymentGatewayUrl, content);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var paymentResponse = JsonSerializer.Deserialize<dynamic>(responseContent);

                return new PaymentResult
                {
                    Success = true,
                    TransactionId = paymentResponse.GetProperty("transaction_id").GetString(),
                    Message = "Payment processed successfully"
                };
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                return new PaymentResult
                {
                    Success = false,
                    TransactionId = null,
                    Message = $"Payment failed: {response.StatusCode} - {errorContent}"
                };
            }
        }
        catch (Exception ex)
        {
            return new PaymentResult
            {
                Success = false,
                TransactionId = null,
                Message = $"Error charging card: {ex.Message}"
            };
        }
    }
}

public class PaymentResult
{
    public bool Success { get; set; }
    public string TransactionId { get; set; }
    public string Message { get; set; }
}
