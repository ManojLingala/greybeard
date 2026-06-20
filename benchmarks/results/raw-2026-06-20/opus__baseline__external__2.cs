using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Payments;

/// <summary>
/// Request payload sent to the payment gateway.
/// </summary>
public sealed record ChargeRequest(
    string CardToken,
    long AmountMinorUnits,
    string Currency,
    string IdempotencyKey,
    string? Description = null);

/// <summary>
/// Response returned by the payment gateway after a charge attempt.
/// </summary>
public sealed record ChargeResponse(
    string TransactionId,
    string Status,
    long AmountMinorUnits,
    string Currency);

/// <summary>
/// Result wrapper so callers can branch on success/failure without exceptions
/// for expected decline scenarios.
/// </summary>
public sealed record ChargeResult(
    bool Succeeded,
    ChargeResponse? Response,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ChargeResult Ok(ChargeResponse response) =>
        new(true, response, null, null);

    public static ChargeResult Failed(string code, string message) =>
        new(false, null, code, message);
}

/// <summary>
/// Thrown when the gateway call fails in a way that is not a normal decline
/// (e.g. network failure, 5xx, timeout, or malformed response).
/// </summary>
public sealed class PaymentGatewayException : Exception
{
    public PaymentGatewayException(string message, Exception? inner = null)
        : base(message, inner) { }
}

public sealed class PaymentGatewayClient
{
    private readonly HttpClient _httpClient;

    /// <summary>
    /// The <see cref="HttpClient"/> should be supplied by the caller, ideally
    /// via IHttpClientFactory, with BaseAddress and the Authorization header
    /// (gateway API key/secret) already configured. This keeps secrets out of
    /// this method and lets the platform manage connection pooling and resilience.
    /// </summary>
    public PaymentGatewayClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Charges a card by calling the external payment gateway over HTTP.
    /// </summary>
    /// <param name="request">The charge details. Pass a stable
    /// <see cref="ChargeRequest.IdempotencyKey"/> so that retries do not
    /// double-charge the customer.</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    /// <returns>A <see cref="ChargeResult"/> describing success or a decline.</returns>
    /// <exception cref="PaymentGatewayException">
    /// Thrown for transport-level or unexpected gateway errors.
    /// </exception>
    public async Task<ChargeResult> ChargeAsync(
        ChargeRequest request,
        CancellationToken cancellationToken = default)
    {
        // Validate input before spending a network round-trip on it.
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.CardToken))
            throw new ArgumentException("Card token is required.", nameof(request));

        if (request.AmountMinorUnits <= 0)
            throw new ArgumentException("Amount must be greater than zero.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Currency))
            throw new ArgumentException("Currency is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(request));

        // Build the request explicitly so we can attach the idempotency header,
        // which the gateway uses to deduplicate retried charges.
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/charges")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("Idempotency-Key", request.IdempotencyKey);

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await _httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller-initiated cancellation: let it propagate as cancellation.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Network failure or timeout. Because the charge may or may not have
            // been applied on the gateway side, callers should retry with the
            // SAME idempotency key rather than issuing a fresh charge.
            throw new PaymentGatewayException(
                "Failed to reach the payment gateway. Retry with the same idempotency key.", ex);
        }

        using (httpResponse)
        {
            // A successful HTTP status means the gateway processed the request.
            if (httpResponse.IsSuccessStatusCode)
            {
                var charge = await httpResponse.Content
                    .ReadFromJsonAsync<ChargeResponse>(cancellationToken)
                    .ConfigureAwait(false);

                if (charge is null)
                    throw new PaymentGatewayException("Gateway returned an empty response body.");

                return ChargeResult.Ok(charge);
            }

            // 4xx (other than 402) typically indicates a bad request on our side.
            // 402 Payment Required is the conventional "card declined" signal.
            var status = (int)httpResponse.StatusCode;
            var body = await httpResponse.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (status == 402 || (status >= 400 && status < 500))
            {
                // Expected, non-transient outcome (declined / invalid request).
                // Return a result instead of throwing so callers can handle it
                // as normal control flow. Avoid logging the raw body verbatim if
                // it can contain sensitive data; this is left to the caller.
                return ChargeResult.Failed(
                    code: httpResponse.StatusCode.ToString(),
                    message: $"Charge was not completed (HTTP {status}).");
            }

            // 5xx or anything else unexpected: treat as a transport-level failure.
            throw new PaymentGatewayException(
                $"Payment gateway returned an unexpected status {status}. Body: {body}");
        }
    }
}
