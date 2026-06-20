using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

namespace Payments.Webhooks;

/// <summary>
/// Configuration bound from app settings / secrets store.
/// The signing secret is the per-endpoint secret shown in the Stripe Dashboard
/// (starts with "whsec_"). Never hard-code it.
/// </summary>
public sealed class StripeWebhookOptions
{
    public string SigningSecret { get; init; } = string.Empty;
}

/// <summary>
/// Abstraction over the account-crediting logic so the controller stays thin
/// and the credit operation can be made idempotent at the data layer.
/// </summary>
public interface IAccountCreditingService
{
    /// <summary>
    /// Credits the customer's account for a succeeded payment.
    ///
    /// MUST be idempotent: Stripe guarantees *at-least-once* delivery, so the
    /// same event can arrive multiple times (network retries, redeliveries from
    /// the Dashboard, etc.). Use <paramref name="eventId"/> (the Stripe Event ID,
    /// which is stable across redeliveries) as the idempotency key — typically a
    /// unique constraint on a "processed_events" table inside the same DB
    /// transaction that performs the credit.
    /// </summary>
    /// <returns>
    /// true  if the credit was applied for the first time;
    /// false if this event was already processed (duplicate / redelivery).
    /// </returns>
    Task<bool> CreditAccountAsync(
        string eventId,
        string customerId,
        string paymentIntentId,
        long amountReceived,
        string currency,
        CancellationToken cancellationToken);
}

[ApiController]
[Route("api/webhooks/stripe")]
public sealed class StripeWebhookController : ControllerBase
{
    private readonly string _signingSecret;
    private readonly IAccountCreditingService _crediting;
    private readonly ILogger<StripeWebhookController> _logger;

    public StripeWebhookController(
        IOptions<StripeWebhookOptions> options,
        IAccountCreditingService crediting,
        ILogger<StripeWebhookController> logger)
    {
        _signingSecret = options.Value.SigningSecret;
        _crediting = crediting;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Handle(CancellationToken cancellationToken)
    {
        // 1. Read the RAW body. Signature verification is computed over the exact
        //    bytes Stripe sent; never use a model-bound/deserialized payload here.
        string payload;
        using (var reader = new StreamReader(Request.Body))
        {
            payload = await reader.ReadToEndAsync(cancellationToken);
        }

        var signatureHeader = Request.Headers["Stripe-Signature"].ToString();
        if (string.IsNullOrEmpty(signatureHeader))
        {
            _logger.LogWarning("Stripe webhook received with no Stripe-Signature header.");
            return BadRequest();
        }

        // 2. Verify the signature. This authenticates the request AND, because
        //    Stripe includes a timestamp in the signed payload, rejects replayed
        //    requests outside the default tolerance (300s). Constructing the event
        //    via the library also re-parses the trusted payload — we deliberately
        //    do NOT trust any client-supplied JSON before this point.
        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(
                payload,
                signatureHeader,
                _signingSecret,
                throwOnApiVersionMismatch: false);
        }
        catch (StripeException ex)
        {
            // Bad signature, tampered payload, or stale timestamp.
            // Return 400 so Stripe marks delivery as failed (and will retry).
            _logger.LogWarning(ex, "Stripe webhook signature verification failed.");
            return BadRequest();
        }

        // 3. Dispatch by event type. We only act on payment_intent.succeeded
        //    here; everything else is acknowledged so Stripe stops retrying it.
        if (stripeEvent.Type != EventTypes.PaymentIntentSucceeded)
        {
            // Acknowledge unhandled event types with 200 to avoid pointless retries.
            return Ok();
        }

        if (stripeEvent.Data.Object is not PaymentIntent paymentIntent)
        {
            _logger.LogWarning(
                "Event {EventId} typed as payment_intent.succeeded but payload was not a PaymentIntent.",
                stripeEvent.Id);
            return BadRequest();
        }

        // Defensive: the event type says succeeded, but confirm the status too.
        if (paymentIntent.Status != "succeeded")
        {
            _logger.LogWarning(
                "PaymentIntent {PaymentIntentId} in event {EventId} has status {Status}; ignoring.",
                paymentIntent.Id, stripeEvent.Id, paymentIntent.Status);
            return Ok();
        }

        var customerId = paymentIntent.CustomerId;
        if (string.IsNullOrEmpty(customerId))
        {
            // No customer to credit — log and acknowledge so Stripe doesn't retry.
            _logger.LogWarning(
                "PaymentIntent {PaymentIntentId} (event {EventId}) has no associated customer; nothing to credit.",
                paymentIntent.Id, stripeEvent.Id);
            return Ok();
        }

        try
        {
            // 4. Credit idempotently, keyed on the stable Stripe Event ID.
            //    AmountReceived is the amount actually captured, in the currency's
            //    smallest unit (e.g. cents). Pass it straight through; do not trust
            //    any amount that might have come from the client.
            bool applied = await _crediting.CreditAccountAsync(
                eventId: stripeEvent.Id,
                customerId: customerId,
                paymentIntentId: paymentIntent.Id,
                amountReceived: paymentIntent.AmountReceived,
                currency: paymentIntent.Currency,
                cancellationToken: cancellationToken);

            if (applied)
            {
                _logger.LogInformation(
                    "Credited customer {CustomerId} for {Amount} {Currency} (PaymentIntent {PaymentIntentId}, event {EventId}).",
                    customerId, paymentIntent.AmountReceived, paymentIntent.Currency,
                    paymentIntent.Id, stripeEvent.Id);
            }
            else
            {
                // Duplicate delivery / redelivery — already handled. Still 200.
                _logger.LogInformation(
                    "Event {EventId} already processed; skipping duplicate credit for customer {CustomerId}.",
                    stripeEvent.Id, customerId);
            }
        }
        catch (Exception ex)
        {
            // Transient failure (DB down, etc.). Return 500 WITHOUT having
            // committed a partial credit, so Stripe retries delivery later.
            // Idempotency above guarantees the retry won't double-credit.
            _logger.LogError(ex,
                "Failed to credit customer {CustomerId} for event {EventId}; returning 500 so Stripe retries.",
                customerId, stripeEvent.Id);
            return StatusCode(500);
        }

        // 5. Acknowledge fast. Heavy/slow work should be enqueued, not done inline,
        //    because Stripe times out webhook deliveries.
        return Ok();
    }
}
