using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

namespace MyApp.Controllers
{
    [ApiController]
    [Route("api/webhooks")]
    public class StripeWebhookController : ControllerBase
    {
        private readonly ILogger<StripeWebhookController> _logger;
        private readonly IAccountCreditService _accountCreditService;
        private readonly IIdempotencyStore _idempotencyStore;
        private readonly StripeSettings _stripeSettings;

        public StripeWebhookController(
            ILogger<StripeWebhookController> logger,
            IAccountCreditService accountCreditService,
            IIdempotencyStore idempotencyStore,
            IOptions<StripeSettings> stripeSettings)
        {
            _logger = logger;
            _accountCreditService = accountCreditService;
            _idempotencyStore = idempotencyStore;
            _stripeSettings = stripeSettings.Value;
        }

        [HttpPost("stripe")]
        public async Task<IActionResult> HandleStripeWebhook()
        {
            // Read the raw request body for signature verification
            string json;
            using (var reader = new StreamReader(HttpContext.Request.Body))
            {
                json = await reader.ReadToEndAsync();
            }

            // Retrieve the Stripe-Signature header
            if (!Request.Headers.TryGetValue("Stripe-Signature", out var signatureHeader))
            {
                _logger.LogWarning("Missing Stripe-Signature header.");
                return BadRequest("Missing Stripe-Signature header.");
            }

            Event stripeEvent;
            try
            {
                // Verify the webhook signature using the webhook secret
                stripeEvent = EventUtility.ConstructEvent(
                    json,
                    signatureHeader,
                    _stripeSettings.WebhookSecret,
                    throwOnApiVersionMismatch: false
                );
            }
            catch (StripeException ex)
            {
                _logger.LogWarning(ex, "Stripe webhook signature verification failed.");
                return BadRequest("Webhook signature verification failed.");
            }

            _logger.LogInformation("Received Stripe event: {EventType}, ID: {EventId}", stripeEvent.Type, stripeEvent.Id);

            // Idempotency check: ensure we don't process the same event twice (handles redelivery)
            if (await _idempotencyStore.HasBeenProcessedAsync(stripeEvent.Id))
            {
                _logger.LogInformation("Duplicate Stripe event {EventId} — skipping.", stripeEvent.Id);
                return Ok(new { status = "already_processed" });
            }

            try
            {
                switch (stripeEvent.Type)
                {
                    case Events.PaymentIntentSucceeded:
                        await HandlePaymentIntentSucceededAsync(stripeEvent);
                        break;

                    case Events.ChargeSucceeded:
                        // Some integrations use charge.succeeded; handle if needed
                        await HandleChargeSucceededAsync(stripeEvent);
                        break;

                    default:
                        // Unhandled event type — return 200 to acknowledge receipt
                        _logger.LogInformation("Unhandled Stripe event type: {EventType}", stripeEvent.Type);
                        return Ok(new { status = "unhandled_event" });
                }

                // Mark the event as processed after successful handling
                await _idempotencyStore.MarkAsProcessedAsync(stripeEvent.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Stripe event {EventId}.", stripeEvent.Id);
                // Return 500 so Stripe will retry the webhook
                return StatusCode(500, "Internal error processing webhook.");
            }

            return Ok(new { status = "success" });
        }

        private async Task HandlePaymentIntentSucceededAsync(Event stripeEvent)
        {
            if (stripeEvent.Data.Object is not PaymentIntent paymentIntent)
            {
                _logger.LogWarning("Could not deserialize PaymentIntent from event {EventId}.", stripeEvent.Id);
                throw new InvalidOperationException("Invalid PaymentIntent payload.");
            }

            // Extract the customer ID; this may be a Customer ID or email depending on your setup
            string customerId = paymentIntent.CustomerId
                ?? paymentIntent.Metadata?.GetValueOrDefault("customer_id");

            if (string.IsNullOrEmpty(customerId))
            {
                _logger.LogWarning(
                    "PaymentIntent {PaymentIntentId} has no associated customer — cannot credit account.",
                    paymentIntent.Id);
                throw new InvalidOperationException("No customer ID found on PaymentIntent.");
            }

            // Amount is in the smallest currency unit (e.g., cents for USD)
            long amountReceived = paymentIntent.AmountReceived;
            string currency = paymentIntent.Currency;

            _logger.LogInformation(
                "Crediting customer {CustomerId} with {Amount} {Currency} for PaymentIntent {PaymentIntentId}.",
                customerId, amountReceived, currency.ToUpperInvariant(), paymentIntent.Id);

            await _accountCreditService.CreditAccountAsync(
                customerId: customerId,
                amountInSmallestUnit: amountReceived,
                currency: currency,
                referenceId: paymentIntent.Id,
                description: $"Payment succeeded: {paymentIntent.Id}"
            );
        }

        private async Task HandleChargeSucceededAsync(Event stripeEvent)
        {
            if (stripeEvent.Data.Object is not Charge charge)
            {
                _logger.LogWarning("Could not deserialize Charge from event {EventId}.", stripeEvent.Id);
                throw new InvalidOperationException("Invalid Charge payload.");
            }

            string customerId = charge.CustomerId
                ?? charge.Metadata?.GetValueOrDefault("customer_id");

            if (string.IsNullOrEmpty(customerId))
            {
                _logger.LogWarning(
                    "Charge {ChargeId} has no associated customer — cannot credit account.",
                    charge.Id);
                throw new InvalidOperationException("No customer ID found on Charge.");
            }

            long amountCaptured = charge.AmountCaptured;
            string currency = charge.Currency;

            _logger.LogInformation(
                "Crediting customer {CustomerId} with {Amount} {Currency} for Charge {ChargeId}.",
                customerId, amountCaptured, currency.ToUpperInvariant(), charge.Id);

            await _accountCreditService.CreditAccountAsync(
                customerId: customerId,
                amountInSmallestUnit: amountCaptured,
                currency: currency,
                referenceId: charge.Id,
                description: $"Charge succeeded: {charge.Id}"
            );
        }
    }

    // -----------------------------------------------------------------------
    // Supporting types (interfaces and settings — implement as needed)
    // -----------------------------------------------------------------------

    public class StripeSettings
    {
        /// <summary>
        /// The webhook signing secret from the Stripe dashboard (whsec_...).
        /// Store this in a secrets manager or environment variable, never in source code.
        /// </summary>
        public string WebhookSecret { get; set; } = string.Empty;
    }

    /// <summary>
    /// Persists processed Stripe event IDs to prevent double-crediting on redelivery.
    /// Back this with a database table or distributed cache (e.g., Redis) in production.
    /// </summary>
    public interface IIdempotencyStore
    {
        Task<bool> HasBeenProcessedAsync(string eventId);
        Task MarkAsProcessedAsync(string eventId);
    }

    /// <summary>
    /// Credits a customer's account for a successful payment.
    /// Implement with your own business logic / database writes.
    /// </summary>
    public interface IAccountCreditService
    {
        Task CreditAccountAsync(
            string customerId,
            long amountInSmallestUnit,
            string currency,
            string referenceId,
            string description);
    }
}
