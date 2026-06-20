using Microsoft.AspNetCore.Mvc;
using Stripe;
using System;
using System.Threading.Tasks;

namespace PaymentWebhooks.Controllers
{
    [ApiController]
    [Route("webhooks")]
    public class StripeWebhookController : ControllerBase
    {
        private readonly IStripeWebhookVerifier _webhookVerifier;
        private readonly IAccountService _accountService;
        private readonly IWebhookEventStore _eventStore;
        private readonly ILogger<StripeWebhookController> _logger;

        public StripeWebhookController(
            IStripeWebhookVerifier webhookVerifier,
            IAccountService accountService,
            IWebhookEventStore eventStore,
            ILogger<StripeWebhookController> logger)
        {
            _webhookVerifier = webhookVerifier;
            _accountService = accountService;
            _eventStore = eventStore;
            _logger = logger;
        }

        [HttpPost("stripe/payment")]
        public async Task<IActionResult> HandlePaymentSucceeded()
        {
            // greybeard: Trust boundary — verify webhook signature before processing
            var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
            var stripeSignature = Request.Headers["Stripe-Signature"].ToString();

            if (string.IsNullOrEmpty(stripeSignature))
            {
                _logger.LogWarning("Webhook missing signature header");
                return Unauthorized();
            }

            if (!_webhookVerifier.VerifySignature(json, stripeSignature))
            {
                _logger.LogWarning("Webhook signature verification failed");
                return Unauthorized();
            }

            Event stripeEvent;
            try
            {
                // greybeard: Parse safely; never log raw event body (contains secrets)
                stripeEvent = EventUtility.ParseEvent(json);
            }
            catch (JsonException ex)
            {
                _logger.LogError("Webhook event parse failed: {ErrorType}", ex.GetType().Name);
                return BadRequest();
            }

            // greybeard: Idempotency — check if this event was already processed
            // Stripe can redeliver; we must not credit twice for the same payment_succeeded
            var alreadyProcessed = await _eventStore.EventProcessedAsync(stripeEvent.Id);
            if (alreadyProcessed)
            {
                _logger.LogInformation("Webhook event {EventId} already processed, skipping", stripeEvent.Id);
                return Ok(); // Return 200 to tell Stripe we handled it
            }

            // greybeard: Only handle payment_succeeded events
            if (stripeEvent.Type != "payment_intent.succeeded")
            {
                _logger.LogInformation("Webhook type {Type} ignored", stripeEvent.Type);
                return Ok();
            }

            var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
            if (paymentIntent == null)
            {
                _logger.LogError("Webhook payload missing PaymentIntent object");
                return BadRequest();
            }

            // greybeard: Money — extract amount as integer minor units (cents), never float
            // Always validate currency code travels with amount
            if (string.IsNullOrEmpty(paymentIntent.Currency) || paymentIntent.Amount == null)
            {
                _logger.LogError("Webhook amount or currency missing");
                return BadRequest();
            }

            var amountMinorUnits = (long)paymentIntent.Amount; // Stripe delivers in minor units
            var currencyCode = paymentIntent.Currency.ToUpperInvariant();

            // greybeard: Extract customer ID; validate presence before lookup
            var customerId = paymentIntent.CustomerId;
            if (string.IsNullOrEmpty(customerId))
            {
                _logger.LogError("Webhook payment has no customer ID");
                return BadRequest();
            }

            // greybeard: Concurrency + Mutation — wrap in transaction with row lock
            // Exactly-once effect: idempotency key (Stripe event ID) ensures safe retry
            try
            {
                await _accountService.CreditAccountAsync(
                    customerId: customerId,
                    amountMinorUnits: amountMinorUnits,
                    currencyCode: currencyCode,
                    idempotencyKey: stripeEvent.Id,
                    sourceReference: paymentIntent.Id);

                // greybeard: Graceful degradation — record successful processing atomically
                // If this fails, we retry the entire webhook (Stripe will redeliver)
                await _eventStore.MarkEventProcessedAsync(stripeEvent.Id);

                _logger.LogInformation(
                    "Payment credited: customer={CustomerId}, amount={Amount}{Currency}, ref={Ref}",
                    customerId,
                    amountMinorUnits,
                    currencyCode,
                    paymentIntent.Id);

                return Ok();
            }
            catch (ConcurrencyException ex)
            {
                // greybeard: Idempotency — another request already credited this payment
                // Stripe will retry; we idempotently return success
                _logger.LogInformation("Payment already credited (race condition): {Message}", ex.Message);
                return Ok();
            }
            catch (CustomerNotFoundException ex)
            {
                // greybeard: Fail half-way — customer vanished but payment succeeded
                // Log loudly; return 400 to tell Stripe to redeliver (manual intervention needed)
                _logger.LogError("Customer not found after payment: {CustomerId}", customerId);
                return BadRequest("Customer not found");
            }
            catch (Exception ex)
            {
                // greybeard: Never expose internal errors to webhook (no stack traces)
                _logger.LogError(ex, "Webhook processing failed: {EventId}", stripeEvent.Id);
                // Return 500 to trigger Stripe retry
                return StatusCode(500);
            }
        }
    }

    // Supporting interfaces (implementation responsibility of caller)
    public interface IStripeWebhookVerifier
    {
        // greybeard: Verify signature using Stripe's public key; never trust raw event
        bool VerifySignature(string json, string signature);
    }

    public interface IAccountService
    {
        // greybeard: Idempotency key ensures exactly-once credit
        // Throws ConcurrencyException if amount already credited for this idempotency key
        Task CreditAccountAsync(
            string customerId,
            long amountMinorUnits,
            string currencyCode,
            string idempotencyKey,
            string sourceReference);
    }

    public interface IWebhookEventStore
    {
        // greybeard: Atomically track processed events to prevent double-processing
        Task<bool> EventProcessedAsync(string stripeEventId);
        Task MarkEventProcessedAsync(string stripeEventId);
    }

    public class ConcurrencyException : Exception
    {
        public ConcurrencyException(string message) : base(message) { }
    }

    public class CustomerNotFoundException : Exception
    {
        public CustomerNotFoundException(string message) : base(message) { }
    }
}
