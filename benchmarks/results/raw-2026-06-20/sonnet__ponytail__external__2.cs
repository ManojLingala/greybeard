using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

public class PaymentGatewayClient
{
    private static readonly HttpClient _httpClient = new HttpClient();

    public async Task<PaymentResult> ChargeCardAsync(string gatewayUrl, ChargeRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync(gatewayUrl, request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PaymentResult>()
            ?? throw new InvalidOperationException("Empty response from payment gateway.");
    }
}

public record ChargeRequest(string CardToken, decimal Amount, string Currency);

public record PaymentResult(string TransactionId, string Status);
