using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Stripe;
using System;
using System.IO;
using System.Threading.Tasks;

namespace MyApp.Controllers
{
    [ApiController]
    [Route("webhooks")]
    public class StripeWebhookController : ControllerBase
    {
        private readonly ILogger<StripeWebhookController> _logger;
        private readonly IAccountCreditService _accountCreditService;
        private readonly IIdempotencyService _idempotencyService;
        private readonly string _webhookSecret;

        public StripeWebhookController(
            ILogger<StripeWebhookController> logger,
            IAccountCreditService accountCreditService,
            IIdempotencyService idempotencyService,
            IConfiguration configuration)
        {
            _logger = logger;
            _accountCreditService = accountCreditService;
            _idempotencyService = idempotencyService;
            // Store the webhook signing secret in configuration, not hardcoded
            _webhookSecret = configuration["Stripe:WebhookSecret"]
                ?? throw new InvalidOperationException("Stripe:WebhookSecret is not configured.");
        }

        [HttpPost("stripe")]
        public async Task<IActionResult> HandleStripeWebhook()
        {
            // Read the raw request body as a string for signature verification
            string json;
            using (var reader = new StreamReader(HttpContext.Request.Body))
            {
                json = await reader.ReadToEndAsync();
            }

            // Retrieve the Stripe-Signature header
            if (!Request.Headers.TryGetValue("Stripe-Signature", out var signatureHeader))
            {
                _logger.LogWarning("Stripe webhook received without Stripe-Signature header.");
                return BadRequest("Missing Stripe-Signature header.");
            }

            Event stripeEvent;
            try
            {
                // Verify the webhook signature using Stripe's SDK
                // This protects against forged or replayed webhook events
                stripeEvent = EventUtility.ConstructEvent(
                    json,
                    signatureHeader,
                    _webhookSecret,
                    throwOnApiVersionMismatch: false);
            }
            catch (StripeException ex)
            {
                _logger.LogWarning(ex, "Stripe webhook signature verification failed.");
                return BadRequest("Invalid webhook signature.");
            }

            // Handle the payment_intent.succeeded event
            // Note: Stripe sends "payment_intent.succeeded" for PaymentIntents
            // and "charge.succeeded" for Charges. Adjust as needed for your integration.
            if (stripeEvent.Type == Events.PaymentIntentSucceeded)
            {
                var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
                if (paymentIntent == null)
                {
                    _logger.LogError("Could not deserialize PaymentIntent from event {EventId}.", stripeEvent.Id);
                    return BadRequest("Invalid event data.");
                }

                // Use the Stripe event ID for idempotency to handle redelivery safely.
                // Stripe may deliver the same webhook more than once; we must not
                // credit the account twice for the same payment.
                bool alreadyProcessed = await _idempotencyService.HasBeenProcessedAsync(stripeEvent.Id);
                if (alreadyProcessed)
                {
                    _logger.LogInformation(
                        "Webhook event {EventId} already processed. Skipping duplicate.", stripeEvent.Id);
                    // Return 200 so Stripe stops retrying this event
                    return Ok(new { status = "already_processed" });
                }

                try
                {
                    // Extract the customer ID from the PaymentIntent metadata or Customer field
                    string? customerId = paymentIntent.CustomerId;
                    if (string.IsNullOrEmpty(customerId))
                    {
                        // Fall back to metadata if Customer is not set
                        paymentIntent.Metadata.TryGetValue("customer_id", out customerId);
                    }

                    if (string.IsNullOrEmpty(customerId))
                    {
                        _logger.LogError(
                            "PaymentIntent {PaymentIntentId} has no associated customer. Event {EventId}.",
                            paymentIntent.Id, stripeEvent.Id);
                        // Return 400 so we can investigate; Stripe will retry.
                        // Alternatively return 200 if you want to suppress retries.
                        return BadRequest("No customer associated with payment.");
                    }

                    // Amount in smallest currency unit (e.g., cents for USD)
                    long amountReceived = paymentIntent.AmountReceived;
                    string currency = paymentIntent.Currency;

                    _logger.LogInformation(
                        "Processing payment_intent.succeeded for customer {CustomerId}, amount {Amount} {Currency}, event {EventId}.",
                        customerId, amountReceived, currency, stripeEvent.Id);

                    // Credit the customer's account inside a transaction that also
                    // records the idempotency key, so both succeed or fail together.
                    await _accountCreditService.CreditAsync(
                        customerId,
                        amountReceived,
                        currency,
                        paymentIntent.Id,
                        stripeEvent.Id);

                    // Mark the event as processed only after successful crediting
                    await _idempotencyService.MarkAsProcessedAsync(stripeEvent.Id);

                    _logger.LogInformation(
                        "Successfully credited customer {CustomerId} for event {EventId}.", customerId, stripeEvent.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to process payment_intent.succeeded for event {EventId}.", stripeEvent.Id);
                    // Return 500 so Stripe retries the webhook later
                    return StatusCode(500, "Internal error processing payment.");
                }
            }
            else
            {
                // Log but ignore other event types; return 200 so Stripe doesn't retry them
                _logger.LogDebug("Unhandled Stripe event type {EventType} (event {EventId}).",
                    stripeEvent.Type, stripeEvent.Id);
            }

            return Ok(new { status = "success" });
        }
    }

    // ---------------------------------------------------------------------------
    // Contracts — implement these against your database / business logic layer
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Credits a customer's account balance for a completed payment.
    /// Implementations should be idempotent on (stripePaymentIntentId) and run
    /// inside a database transaction together with idempotency-key recording.
    /// </summary>
    public interface IAccountCreditService
    {
        Task CreditAsync(
            string customerId,
            long amountInSmallestUnit,
            string currency,
            string stripePaymentIntentId,
            string stripeEventId);
    }

    /// <summary>
    /// Tracks which Stripe event IDs have already been successfully processed
    /// to prevent duplicate credits on webhook redelivery.
    /// </summary>
    public interface IIdempotencyService
    {
        Task<bool> HasBeenProcessedAsync(string stripeEventId);
        Task MarkAsProcessedAsync(string stripeEventId);
    }
}
