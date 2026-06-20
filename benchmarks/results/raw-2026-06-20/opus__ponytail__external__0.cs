using System.Net.Http.Json;

// Reuse a single HttpClient (it's designed to be shared and thread-safe).
private static readonly HttpClient _http = new()
{
    BaseAddress = new Uri("https://api.paymentgateway.example/v1/"),
};

/// <summary>
/// Charges a card via the external payment gateway.
/// Returns the gateway's charge id on success; throws on a non-success status.
/// </summary>
public async Task<string> ChargeCardAsync(
    string cardToken,
    long amountInCents,
    string currency,
    CancellationToken cancellationToken = default)
{
    // Send the charge request. Use JSON helpers from the BCL so we don't
    // hand-roll any (de)serialization.
    using HttpResponseMessage response = await _http.PostAsJsonAsync(
        "charges",
        new { card_token = cardToken, amount = amountInCents, currency },
        cancellationToken);

    // Throws HttpRequestException on 4xx/5xx — let it bubble up to the caller.
    response.EnsureSuccessStatusCode();

    ChargeResult? result = await response.Content.ReadFromJsonAsync<ChargeResult>(cancellationToken);
    return result?.Id ?? throw new InvalidOperationException("Gateway returned no charge id.");
}

private sealed record ChargeResult(string Id);
