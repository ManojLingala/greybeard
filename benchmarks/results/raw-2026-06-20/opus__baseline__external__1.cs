using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Payments
{
    /// <summary>
    /// Request payload sent to the external payment gateway.
    /// </summary>
    public sealed record ChargeRequest
    {
        public required string CardToken { get; init; }
        public required long AmountMinorUnits { get; init; }
        public required string Currency { get; init; }
        public string? Description { get; init; }
        public string? IdempotencyKey { get; init; }
    }

    /// <summary>
    /// Response returned by the external payment gateway.
    /// </summary>
    public sealed record ChargeResult
    {
        public required bool Success { get; init; }
        public string? TransactionId { get; init; }
        public string? Status { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed class PaymentGatewayException : Exception
    {
        public PaymentGatewayException(string message, Exception? inner = null)
            : base(message, inner) { }
    }

    public sealed class PaymentGatewayClient
    {
        private readonly HttpClient _httpClient;

        // Reuse a single HttpClient instance (typically injected via IHttpClientFactory)
        // to avoid socket exhaustion.
        public PaymentGatewayClient(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        /// <summary>
        /// Charges a card by calling the external payment gateway over HTTP.
        /// </summary>
        /// <param name="request">The charge details (tokenized card, amount, currency, etc.).</param>
        /// <param name="cancellationToken">Token used to cancel the in-flight request.</param>
        /// <returns>The result of the charge attempt.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the request is invalid.</exception>
        /// <exception cref="PaymentGatewayException">Thrown when the gateway call fails.</exception>
        public async Task<ChargeResult> ChargeCardAsync(
            ChargeRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request is null)
                throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.CardToken))
                throw new ArgumentException("Card token is required.", nameof(request));
            if (request.AmountMinorUnits <= 0)
                throw new ArgumentException("Amount must be positive.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.Currency))
                throw new ArgumentException("Currency is required.", nameof(request));

            // An idempotency key prevents a retried request from charging the card twice.
            var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                ? Guid.NewGuid().ToString("N")
                : request.IdempotencyKey;

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/charges")
            {
                Content = JsonContent.Create(new
                {
                    card_token = request.CardToken,
                    amount = request.AmountMinorUnits,
                    currency = request.Currency,
                    description = request.Description
                })
            };
            httpRequest.Headers.Add("Idempotency-Key", idempotencyKey);

            try
            {
                using HttpResponseMessage response =
                    await _httpClient.SendAsync(httpRequest, cancellationToken)
                                     .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // Try to read a structured error body; fall back to status code.
                    var failure = await TryReadResultAsync(response, cancellationToken)
                                        .ConfigureAwait(false);

                    return failure ?? new ChargeResult
                    {
                        Success = false,
                        Status = "failed",
                        ErrorCode = ((int)response.StatusCode).ToString(),
                        ErrorMessage = $"Gateway returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
                    };
                }

                var result = await response.Content
                    .ReadFromJsonAsync<ChargeResult>(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                return result ?? throw new PaymentGatewayException(
                    "Gateway returned an empty or unparseable success response.");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // TaskCanceledException without caller cancellation indicates a timeout.
                throw new PaymentGatewayException("The payment gateway request timed out.");
            }
            catch (HttpRequestException ex)
            {
                throw new PaymentGatewayException(
                    "A network error occurred while contacting the payment gateway.", ex);
            }
        }

        private static async Task<ChargeResult?> TryReadResultAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            try
            {
                return await response.Content
                    .ReadFromJsonAsync<ChargeResult>(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }
    }
}
