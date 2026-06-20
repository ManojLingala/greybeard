using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Payments;

/// <summary>
/// Request payload sent to the external payment gateway.
/// </summary>
public sealed record ChargeRequest(
    string CardToken,
    long AmountMinorUnits,
    string Currency,
    string IdempotencyKey);

/// <summary>
/// Response returned by the external payment gateway.
/// </summary>
public sealed record ChargeResponse(
    string TransactionId,
    string Status,
    string? FailureReason);

/// <summary>
/// Result of attempting to charge a card.
/// </summary>
public sealed record ChargeResult(
    bool Succeeded,
    string? TransactionId,
    string? ErrorMessage);

public sealed class PaymentGatewayClient
{
    private readonly HttpClient _httpClient;

    // HttpClient should be injected (e.g. via IHttpClientFactory) and reused,
    // not created per-call, to avoid socket exhaustion.
    public PaymentGatewayClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Charges a card by calling the external payment gateway over HTTP.
    /// </summary>
    /// <param name="request">The charge details, including a card token and idempotency key.</param>
    /// <param name="cancellationToken">Token used to cancel the outbound request.</param>
    /// <returns>A <see cref="ChargeResult"/> describing success or failure.</returns>
    public async Task<ChargeResult> ChargeCardAsync(
        ChargeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (request.AmountMinorUnits <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.AmountMinorUnits));
        if (string.IsNullOrWhiteSpace(request.CardToken))
            throw new ArgumentException("Card token is required.", nameof(request.CardToken));
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(request.IdempotencyKey));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/charges")
        {
            Content = JsonContent.Create(request)
        };

        // Idempotency key lets the gateway dedupe retries so a card is never
        // charged twice for the same logical operation.
        httpRequest.Headers.Add("Idempotency-Key", request.IdempotencyKey);

        try
        {
            using var httpResponse = await _httpClient
                .SendAsync(httpRequest, cancellationToken)
                .ConfigureAwait(false);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var body = await httpResponse.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

                return new ChargeResult(
                    Succeeded: false,
                    TransactionId: null,
                    ErrorMessage: $"Gateway returned {(int)httpResponse.StatusCode}: {body}");
            }

            var charge = await httpResponse.Content
                .ReadFromJsonAsync<ChargeResponse>(cancellationToken)
                .ConfigureAwait(false);

            if (charge is null)
            {
                return new ChargeResult(
                    Succeeded: false,
                    TransactionId: null,
                    ErrorMessage: "Gateway returned an empty or unparseable response.");
            }

            var approved = string.Equals(charge.Status, "succeeded", StringComparison.OrdinalIgnoreCase);

            return new ChargeResult(
                Succeeded: approved,
                TransactionId: charge.TransactionId,
                ErrorMessage: approved ? null : charge.FailureReason ?? "Charge was declined.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // SendAsync surfaces timeouts as TaskCanceledException when the caller
            // did not request cancellation.
            return new ChargeResult(
                Succeeded: false,
                TransactionId: null,
                ErrorMessage: "The payment gateway request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return new ChargeResult(
                Succeeded: false,
                TransactionId: null,
                ErrorMessage: $"Failed to reach the payment gateway: {ex.Message}");
        }
    }
}
