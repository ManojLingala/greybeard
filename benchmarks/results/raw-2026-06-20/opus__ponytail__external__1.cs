using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

// Reuse a single HttpClient across the app (creating one per call exhausts sockets).
public sealed class PaymentGateway
{
    private readonly HttpClient _http;

    // Inject a configured HttpClient (BaseAddress + auth header set at registration).
    public PaymentGateway(HttpClient http) => _http = http;

    public record ChargeRequest(string CardToken, long AmountCents, string Currency);
    public record ChargeResult(bool Approved, string TransactionId, string? Message);

    /// <summary>
    /// Charges a card by POSTing to the gateway's /charges endpoint.
    /// Throws HttpRequestException on transport/5xx failure; returns the gateway's decision otherwise.
    /// </summary>
    public async Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct = default)
    {
        using HttpResponseMessage response =
            await _http.PostAsJsonAsync("charges", request, ct);

        response.EnsureSuccessStatusCode();

        ChargeResult result =
            await response.Content.ReadFromJsonAsync<ChargeResult>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty response from payment gateway.");

        return result;
    }
}
