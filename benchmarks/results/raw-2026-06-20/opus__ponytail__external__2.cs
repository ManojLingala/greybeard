using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

// Reuse a single HttpClient instance (HttpClient is designed to be shared).
static readonly HttpClient Http = new() { BaseAddress = new System.Uri("https://api.payments.example.com") };

public record ChargeRequest(string CardToken, long AmountCents, string Currency);
public record ChargeResult(string ChargeId, string Status);

/// <summary>
/// Charges a card by POSTing to the payment gateway. Returns the gateway's
/// charge result, or throws if the gateway responds with a non-success status.
/// </summary>
public static async Task<ChargeResult> ChargeCardAsync(ChargeRequest request)
{
    using var response = await Http.PostAsJsonAsync("/v1/charges", request);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadFromJsonAsync<ChargeResult>();
}
