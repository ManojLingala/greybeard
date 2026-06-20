using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

namespace Payments.Webhooks
{
    /// <summary>
    /// Configuration for the Stripe webhook endpoint.
    /// </summary>
    public sealed class StripeWebhookOptions
    {
        /// <summary>
        /// The signing secret for this endpoint (starts with "whsec_").
        /// Obtain it from the Stripe Dashboard / API per-endpoint. Never hard-code it.
        /// </summary>
        public string SigningSecret { get; set; } = string.Empty;
    }

    /// <summary>
    /// Records which Stripe events have already been processed, so that
    /// Stripe's at-least-once redelivery does not double-credit accounts.
    /// </summary>
    public interface IProcessedEventStore
    {
        /// <summary>
        /// Atomically marks the event id as seen. Returns <c>true</c> if this call
        /// is the first to claim the id (i.e. we should process it), or <c>false</c>
        /// if it had already been recorded (a redelivery / duplicate).
        /// Implementations MUST be atomic (e.g. an INSERT with a unique constraint
        /// on the event id, treating a duplicate-key violation as "already seen").
        /// </summary>
        Task<bool> TryMarkProcessedAsync(string eventId, CancellationToken cancellationToken);

        /// <summary>
        /// Removes the record for an event id. Used to roll back the claim if the
        /// business operation throws, so Stripe's retry can be processed cleanly.
        /// </summary>
        Task UnmarkAsync(string eventId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Applies account credits. Implementations should themselves be idempotent
    /// where possible (e.g. keyed on the PaymentIntent / Charge id) as defence in depth.
    /// </summary>
    public interface IAccountCreditService
    {
        Task CreditAsync(
            string customerId,
            long amountInMinorUnits,
            string currency,
            string paymentIntentId,
            CancellationToken cancellationToken);
    }

    [ApiController]
    [Route("api/webhooks/stripe")]
    public sealed class StripeWebhookController : ControllerBase
    {
        private readonly string _signingSecret;
        private readonly IProcessedEventStore _processedEvents;
        private readonly IAccountCreditService _credits;
        private readonly ILogger<StripeWebhookController> _logger;

        public StripeWebhookController(
            IOptions<StripeWebhookOptions> options,
            IProcessedEventStore processedEvents,
            IAccountCreditService credits,
            ILogger<StripeWebhookController> logger)
        {
            _signingSecret = options?.Value?.SigningSecret
                ?? throw new ArgumentNullException(nameof(options));
            _processedEvents = processedEvents ?? throw new ArgumentNullException(nameof(processedEvents));
            _credits = credits ?? throw new ArgumentNullException(nameof(credits));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpPost]
        public async Task<IActionResult> Handle(CancellationToken cancellationToken)
        {
            // 1) Read the RAW request body. Signature verification is computed over
            //    the exact bytes Stripe sent, so we must not use a model-bound DTO.
            string payload;
            using (var reader = new StreamReader(Request.Body))
            {
                payload = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            // 2) Verify the signature. This authenticates the payload AND, because
            //    ConstructEvent enforces the timestamp tolerance (default 300s),
            //    protects against replay of captured requests.
            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ConstructEvent(
                    payload,
                    Request.Headers["Stripe-Signature"],
                    _signingSecret,
                    throwOnApiVersionMismatch: false);
            }
            catch (StripeException ex)
            {
                // Signature invalid / missing header / outside tolerance.
                // Do NOT 500 here: a 400 tells Stripe the request is bad and not
                // worth retrying. Never log the secret or the raw signature.
                _logger.LogWarning(ex, "Rejected Stripe webhook: signature verification failed.");
                return BadRequest();
            }

            // 3) Only act on the event type we care about. Acknowledge everything
            //    else with 200 so Stripe stops retrying events we ignore.
            if (stripeEvent.Type != Events.PaymentIntentSucceeded)
            {
                _logger.LogDebug("Ignoring Stripe event {EventId} of type {EventType}.",
                    stripeEvent.Id, stripeEvent.Type);
                return Ok();
            }

            // 4) Idempotency: Stripe guarantees at-least-once delivery, so the same
            //    event id can arrive multiple times (retries / dashboard redelivery).
            //    Claim the id atomically; if someone already claimed it, ack and stop.
            if (!await _processedEvents.TryMarkProcessedAsync(stripeEvent.Id, cancellationToken)
                    .ConfigureAwait(false))
            {
                _logger.LogInformation("Duplicate Stripe event {EventId} ignored.", stripeEvent.Id);
                return Ok();
            }

            try
            {
                if (stripeEvent.Data.Object is not PaymentIntent intent)
                {
                    _logger.LogError("Stripe event {EventId} had no PaymentIntent payload.", stripeEvent.Id);
                    // Malformed payload that we can't action. Drop our claim and
                    // return 200 so Stripe doesn't hammer us; this needs human review.
                    await _processedEvents.UnmarkAsync(stripeEvent.Id, cancellationToken).ConfigureAwait(false);
                    return Ok();
                }

                if (string.IsNullOrEmpty(intent.CustomerId))
                {
                    _logger.LogWarning(
                        "PaymentIntent {PaymentIntentId} (event {EventId}) has no customer; nothing to credit.",
                        intent.Id, stripeEvent.Id);
                    return Ok();
                }

                // Use the amount Stripe actually received, in the smallest currency unit.
                await _credits.CreditAsync(
                    intent.CustomerId,
                    intent.AmountReceived,
                    intent.Currency,
                    intent.Id,
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Credited customer {CustomerId} with {Amount} {Currency} for PaymentIntent {PaymentIntentId} (event {EventId}).",
                    intent.CustomerId, intent.AmountReceived, intent.Currency, intent.Id, stripeEvent.Id);

                return Ok();
            }
            catch (Exception ex)
            {
                // The business operation failed. Roll back the idempotency claim so
                // Stripe's automatic retry can re-attempt cleanly, and return 500 so
                // Stripe knows to redeliver.
                _logger.LogError(ex, "Failed to process Stripe event {EventId}; rolling back claim for retry.",
                    stripeEvent.Id);

                try
                {
                    await _processedEvents.UnmarkAsync(stripeEvent.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(rollbackEx,
                        "Failed to roll back idempotency claim for event {EventId}.", stripeEvent.Id);
                }

                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }
    }
}
