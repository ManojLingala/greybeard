using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Events;

namespace GreybeardPayments.Controllers
{
    [ApiController]
    [Route("api/webhooks")]
    public class StripeWebhookController : ControllerBase
    {
        private readonly IStripeSignatureValidator _signatureValidator;
        private readonly IAccountCreditService _creditService;
        private readonly IWebhookDeliveryService _deliveryService;
        private readonly ILogger<StripeWebhookController> _logger;

        public StripeWebhookController(
            IStripeSignatureValidator signatureValidator,
            IAccountCreditService creditService,
            IWebhookDeliveryService deliveryService,
            ILogger<StripeWebhookController> logger)
        {
            _signatureValidator = signatureValidator;
            _creditService = creditService;
            _deliveryService = deliveryService;
            _logger = logger;
        }

        [HttpPost("stripe")]
        public async Task<IActionResult> HandleStripeWebhook()
        {
            string json;
            try
            {
                using (var reader = new System.IO.StreamReader(Request.Body))
                {
                    json = await reader.ReadToEndAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read webhook body");
                return BadRequest();
            }

            // greybeard: Trust boundary — verify Stripe signature before parsing JSON
            string signatureHeader = Request.Headers["Stripe-Signature"].ToString();
            if (!_signatureValidator.VerifySignature(json, signatureHeader))
            {
                _logger.LogWarning("Webhook signature validation failed");
                return Unauthorized();
            }

            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ParseEvent(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse Stripe event");
                return BadRequest();
            }

            // greybeard: Mutation + Concurrency — check idempotency by event ID before proceeding
            // Ensures exactly-once semantics even if webhook is redelivered
            bool alreadyProcessed = await _deliveryService.HasEventBeenProcessedAsync(stripeEvent.Id);
            if (alreadyProcessed)
            {
                _logger.LogInformation("Webhook event {EventId} already processed, returning 200 OK", stripeEvent.Id);
                return Ok();
            }

            if (stripeEvent.Type != "payment_intent.succeeded")
            {
                // Not our event type; acknowledge but don't process
                return Ok();
            }

            PaymentIntent paymentIntent = stripeEvent.Data.Object as PaymentIntent;
            if (paymentIntent == null)
            {
                _logger.LogError("payment_intent.succeeded event missing PaymentIntent object");
                return BadRequest();
            }

            // greybeard: Mutation — idempotency key travels with the payment
            // Use PaymentIntent.Id as the idempotency key for credit operation
            string idempotencyKey = paymentIntent.Id;

            // greybeard: Trust boundary — validate payment amount came from Stripe, not client
            // Never trust amounts in headers or metadata; only the PaymentIntent amount
            if (paymentIntent.Amount <= 0)
            {
                _logger.LogError("PaymentIntent {PaymentId} has invalid amount: {Amount}", paymentIntent.Id, paymentIntent.Amount);
                return BadRequest();
            }

            // greybeard: Money — extract amount as integer minor units (cents for USD)
            // Currency code travels with the amount; validate it matches expected
            long amountMinorUnits = paymentIntent.Amount;
            string currency = paymentIntent.Currency;

            if (string.IsNullOrEmpty(currency) || currency.Length != 3)
            {
                _logger.LogError("PaymentIntent {PaymentId} has invalid currency: {Currency}", paymentIntent.Id, currency);
                return BadRequest();
            }

            // greybeard: Trust boundary — extract customer ID from PaymentIntent, not from webhook metadata
            string stripeCustomerId = paymentIntent.CustomerId;
            if (string.IsNullOrEmpty(stripeCustomerId))
            {
                _logger.LogError("PaymentIntent {PaymentId} missing customer ID", paymentIntent.Id);
                return BadRequest();
            }

            // greybeard: Concurrency — record delivery attempt before mutation to prevent double-processing
            // Mark this event as being processed atomically with the credit
            try
            {
                await _creditService.CreditAccountAsync(
                    stripeCustomerId,
                    amountMinorUnits,
                    currency,
                    idempotencyKey,
                    paymentIntent.Id
                );

                // greybeard: Mutation + Concurrency — mark event as successfully processed only after credit completes
                await _deliveryService.MarkEventProcessedAsync(stripeEvent.Id);

                _logger.LogInformation(
                    "Successfully credited customer {CustomerId} {Amount} {Currency} for PaymentIntent {PaymentId}",
                    stripeCustomerId,
                    amountMinorUnits,
                    currency,
                    paymentIntent.Id
                );

                return Ok();
            }
            catch (CreditAlreadyGrantedException ex)
            {
                // greybeard: Mutation — idempotent credit was already applied for this PaymentIntent
                _logger.LogInformation("Credit already granted for PaymentIntent {PaymentId}: {Message}", paymentIntent.Id, ex.Message);
                await _deliveryService.MarkEventProcessedAsync(stripeEvent.Id);
                return Ok();
            }
            catch (Exception ex)
            {
                // greybeard: Can it fail half-way? — credit operation failed; do not mark event processed
                // Webhook will be retried; customer account is not corrupted
                _logger.LogError(
                    ex,
                    "Failed to credit account for PaymentIntent {PaymentId}, customer {CustomerId}, amount {Amount} {Currency}. Webhook will be retried.",
                    paymentIntent.Id,
                    stripeCustomerId,
                    amountMinorUnits,
                    currency
                );
                return StatusCode(500);
            }
        }
    }

    // Supporting interfaces for dependency injection
    public interface IStripeSignatureValidator
    {
        bool VerifySignature(string json, string signatureHeader);
    }

    public interface IAccountCreditService
    {
        Task CreditAccountAsync(string stripeCustomerId, long amountMinorUnits, string currency, string idempotencyKey, string paymentIntentId);
    }

    public interface IWebhookDeliveryService
    {
        Task<bool> HasEventBeenProcessedAsync(string eventId);
        Task MarkEventProcessedAsync(string eventId);
    }

    public class CreditAlreadyGrantedException : Exception
    {
        public CreditAlreadyGrantedException(string message) : base(message) { }
    }
}
