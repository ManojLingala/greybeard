using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

public class PaymentGatewayClient
{
    private static readonly HttpClient _httpClient = new HttpClient();

    public async Task<PaymentResult> ChargeCardAsync(string gatewayUrl, string apiKey, ChargeRequest request)
    {
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        var response = await _httpClient.PostAsJsonAsync($"{gatewayUrl}/charges", request);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<PaymentResult>();
        return result ?? throw new InvalidOperationException("Empty response from payment gateway.");
    }
}

public record ChargeRequest(string CardToken, long AmountCents, string Currency, string Description);

public record PaymentResult(string ChargeId, string Status, long AmountCents, string Currency);
