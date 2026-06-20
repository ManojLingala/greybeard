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
        private readonly StripeSettings _stripeSettings;
        private readonly IAccountService _accountService;
        private readonly IIdempotencyService _idempotencyService;

        public StripeWebhookController(
            ILogger<StripeWebhookController> logger,
            IOptions<StripeSettings> stripeSettings,
            IAccountService accountService,
            IIdempotencyService idempotencyService)
        {
            _logger = logger;
            _stripeSettings = stripeSettings.Value;
            _accountService = accountService;
            _idempotencyService = idempotencyService;
        }

        [HttpPost("stripe")]
        public async Task<IActionResult> HandleStripeWebhook()
        {
            string payload;
            using (var reader = new StreamReader(HttpContext.Request.Body))
            {
                payload = await reader.ReadToEndAsync();
            }

            // Retrieve the Stripe-Signature header for verification
            if (!Request.Headers.TryGetValue("Stripe-Signature", out var stripeSignatureHeader))
            {
                _logger.LogWarning("Stripe webhook received without Stripe-Signature header.");
                return BadRequest("Missing Stripe-Signature header.");
            }

            Event stripeEvent;
            try
            {
                // Verify the webhook signature using the endpoint secret
                // This prevents replay attacks and ensures the request came from Stripe
                stripeEvent = EventUtility.ConstructEvent(
                    payload,
                    stripeSignatureHeader,
                    _stripeSettings.WebhookSecret,
                    throwOnApiVersionMismatch: false
                );
            }
            catch (StripeException ex)
            {
                _logger.LogWarning(ex, "Stripe webhook signature verification failed.");
                return BadRequest("Invalid webhook signature.");
            }

            _logger.LogInformation("Received Stripe event: {EventType} with ID: {EventId}", stripeEvent.Type, stripeEvent.Id);

            // Use the event ID for idempotency to handle redeliveries correctly
            // Stripe may deliver the same event multiple times; we must process it only once
            if (await _idempotencyService.HasBeenProcessedAsync(stripeEvent.Id))
            {
                _logger.LogInformation("Stripe event {EventId} has already been processed. Skipping.", stripeEvent.Id);
                return Ok(new { message = "Event already processed." });
            }

            try
            {
                switch (stripeEvent.Type)
                {
                    case Events.PaymentIntentSucceeded:
                        await HandlePaymentIntentSucceededAsync(stripeEvent);
                        break;

                    case Events.ChargeSucceeded:
                        // Handle charge.succeeded if using older Stripe integration
                        await HandleChargeSucceededAsync(stripeEvent);
                        break;

                    default:
                        // Log and ignore unhandled event types; still return 200 to acknowledge receipt
                        _logger.LogInformation("Unhandled Stripe event type: {EventType}", stripeEvent.Type);
                        break;
                }

                // Mark event as processed to prevent duplicate processing on redelivery
                await _idempotencyService.MarkAsProcessedAsync(stripeEvent.Id);

                return Ok(new { message = "Webhook processed successfully." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Stripe event {EventId} of type {EventType}.", stripeEvent.Id, stripeEvent.Type);
                // Return 500 so Stripe will retry the webhook delivery
                return StatusCode(500, "Internal server error processing webhook.");
            }
        }

        private async Task HandlePaymentIntentSucceededAsync(Event stripeEvent)
        {
            if (stripeEvent.Data.Object is not PaymentIntent paymentIntent)
            {
                _logger.LogError("Could not deserialize PaymentIntent from event {EventId}.", stripeEvent.Id);
                throw new InvalidOperationException("Invalid PaymentIntent data in event.");
            }

            _logger.LogInformation(
                "PaymentIntent succeeded: {PaymentIntentId}, Amount: {Amount}, Currency: {Currency}, Customer: {CustomerId}",
                paymentIntent.Id,
                paymentIntent.Amount,
                paymentIntent.Currency,
                paymentIntent.CustomerId);

            if (string.IsNullOrEmpty(paymentIntent.CustomerId))
            {
                _logger.LogWarning("PaymentIntent {PaymentIntentId} has no associated customer.", paymentIntent.Id);
                return;
            }

            // Convert amount from smallest currency unit (e.g., cents) to major unit (e.g., dollars)
            decimal creditAmount = paymentIntent.Amount / 100m;

            await _accountService.CreditCustomerAccountAsync(
                customerId: paymentIntent.CustomerId,
                amount: creditAmount,
                currency: paymentIntent.Currency,
                referenceId: paymentIntent.Id,
                description: $"Payment succeeded for PaymentIntent {paymentIntent.Id}"
            );

            _logger.LogInformation(
                "Successfully credited {Amount} {Currency} to customer {CustomerId} for PaymentIntent {PaymentIntentId}.",
                creditAmount,
                paymentIntent.Currency,
                paymentIntent.CustomerId,
                paymentIntent.Id);
        }

        private async Task HandleChargeSucceededAsync(Event stripeEvent)
        {
            if (stripeEvent.Data.Object is not Charge charge)
            {
                _logger.LogError("Could not deserialize Charge from event {EventId}.", stripeEvent.Id);
                throw new InvalidOperationException("Invalid Charge data in event.");
            }

            _logger.LogInformation(
                "Charge succeeded: {ChargeId}, Amount: {Amount}, Currency: {Currency}, Customer: {CustomerId}",
                charge.Id,
                charge.Amount,
                charge.Currency,
                charge.CustomerId);

            if (string.IsNullOrEmpty(charge.CustomerId))
            {
                _logger.LogWarning("Charge {ChargeId} has no associated customer.", charge.Id);
                return;
            }

            decimal creditAmount = charge.Amount / 100m;

            await _accountService.CreditCustomerAccountAsync(
                customerId: charge.CustomerId,
                amount: creditAmount,
                currency: charge.Currency,
                referenceId: charge.Id,
                description: $"Payment succeeded for Charge {charge.Id}"
            );

            _logger.LogInformation(
                "Successfully credited {Amount} {Currency} to customer {CustomerId} for Charge {ChargeId}.",
                creditAmount,
                charge.Currency,
                charge.CustomerId,
                charge.Id);
        }
    }

    // Settings model for Stripe configuration
    public class StripeSettings
    {
        public string SecretKey { get; set; } = string.Empty;
        public string WebhookSecret { get; set; } = string.Empty;
    }

    // Service interfaces (implementations would be in separate files)
    public interface IAccountService
    {
        Task CreditCustomerAccountAsync(
            string customerId,
            decimal amount,
            string currency,
            string referenceId,
            string description);
    }

    public interface IIdempotencyService
    {
        Task<bool> HasBeenProcessedAsync(string eventId);
        Task MarkAsProcessedAsync(string eventId);
    }
}
