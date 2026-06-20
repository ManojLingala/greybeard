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
/// Strongly-typed options bound from configuration / secret store.
/// The webhook signing secret (whsec_...) is NOT the same as your API key.
/// Get it from the Stripe Dashboard for the specific endpoint, or from the
/// `stripe listen` CLI output during local development.
/// </summary>
public sealed class StripeWebhookOptions
{
    public string SigningSecret { get; set; } = string.Empty;
}

/// <summary>
/// Persists which Stripe event ids have already been processed so that
/// redeliveries (Stripe retries with the SAME event id) are idempotent.
/// Back this with a real durable store (SQL/Redis) in production. The
/// "claim" should be atomic — e.g. an INSERT on a UNIQUE event-id column,
/// or a Redis SET NX — so two concurrent deliveries can't both win.
/// </summary>
public interface IProcessedEventStore
{
    /// <summary>
    /// Atomically records the event id as "in progress / done".
    /// Returns true if THIS call claimed it (first time we've seen it),
    /// false if it was already claimed (i.e. a redelivery / duplicate).
    /// </summary>
    Task<bool> TryClaimAsync(string eventId, CancellationToken ct);
}

/// <summary>Applies account credits. Implementation must itself be idempotent-safe.</summary>
public interface IAccountCreditService
{
    /// <summary>
    /// Credits the customer's account for a successful payment.
    /// Keyed on the PaymentIntent id so that even if the idempotency
    /// guard is bypassed, double-crediting is prevented at the data layer.
    /// </summary>
    Task CreditAsync(string customerId, string paymentIntentId, long amount, string currency, CancellationToken ct);
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
        _signingSecret = options.Value.SigningSecret;
        _processedEvents = processedEvents;
        _credits = credits;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Handle(CancellationToken ct)
    {
        // 1. Read the RAW request body. Signature verification is computed over
        //    the exact bytes Stripe sent; never deserialize-then-reserialize,
        //    that would change the payload and break verification.
        //    (Ensure no upstream middleware has consumed/buffered the body in a
        //    way that mutates it; EnableBuffering or a raw read is fine.)
        string json;
        using (var reader = new StreamReader(Request.Body))
        {
            json = await reader.ReadToEndAsync(ct);
        }

        var signatureHeader = Request.Headers["Stripe-Signature"];

        Event stripeEvent;
        try
        {
            // 2. Verify the signature AND the timestamp tolerance (default 5 min).
            //    This both authenticates the sender (HMAC with the signing secret)
            //    and rejects replayed/stale payloads. throwOnApiVersionMismatch is
            //    left default so a dashboard version bump doesn't drop events.
            stripeEvent = EventUtility.ConstructEvent(
                json,
                signatureHeader,
                _signingSecret);
        }
        catch (StripeException ex)
        {
            // Invalid signature, malformed payload, or expired timestamp.
            // Return 400 so Stripe marks the delivery as failed (do NOT retry
            // a forged request into a 200). Don't leak details to the caller.
            _logger.LogWarning(ex, "Rejected Stripe webhook: signature verification failed.");
            return BadRequest();
        }

        // 3. Filter to the event type we actually handle. Stripe may send other
        //    enabled event types to this endpoint; acknowledge them with 200 so
        //    Stripe stops retrying, but do no work.
        if (stripeEvent.Type != EventTypes.PaymentIntentSucceeded)
        {
            _logger.LogInformation("Ignoring unhandled Stripe event type {Type} ({Id}).",
                stripeEvent.Type, stripeEvent.Id);
            return Ok();
        }

        // 4. Idempotency / redelivery guard. Stripe guarantees at-least-once
        //    delivery and retries on any non-2xx or timeout, reusing the SAME
        //    event id. Claim it atomically; if we've seen it, ack and stop.
        if (!await _processedEvents.TryClaimAsync(stripeEvent.Id, ct))
        {
            _logger.LogInformation("Duplicate Stripe event {Id} ignored (already processed).", stripeEvent.Id);
            return Ok();
        }

        var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
        if (paymentIntent is null)
        {
            // Shape we didn't expect for this event type. Ack to avoid an
            // infinite retry loop, but record it for investigation.
            _logger.LogError("payment_intent.succeeded event {Id} had no PaymentIntent payload.", stripeEvent.Id);
            return Ok();
        }

        // Defense in depth: trust the verified payload's status field.
        if (paymentIntent.Status != "succeeded")
        {
            _logger.LogWarning("PaymentIntent {Pi} in event {Id} not in succeeded state ({Status}); skipping credit.",
                paymentIntent.Id, stripeEvent.Id, paymentIntent.Status);
            return Ok();
        }

        try
        {
            // 5. Do the actual side effect. Keyed on the PaymentIntent id so the
            //    credit layer can also dedupe defensively. Amounts come from the
            //    signed payload — never from anything the client could influence.
            await _credits.CreditAsync(
                customerId: paymentIntent.CustomerId,
                paymentIntentId: paymentIntent.Id,
                amount: paymentIntent.AmountReceived,   // smallest currency unit (e.g. cents)
                currency: paymentIntent.Currency,
                ct: ct);
        }
        catch (Exception ex)
        {
            // Transient failure crediting the account. Return 500 so Stripe
            // RETRIES delivery later (with the same event id, caught by our
            // idempotency guard). Surfacing the error keeps the money correct.
            _logger.LogError(ex, "Failed to credit account for PaymentIntent {Pi} (event {Id}).",
                paymentIntent.Id, stripeEvent.Id);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        _logger.LogInformation("Credited account for PaymentIntent {Pi} (event {Id}).",
            paymentIntent.Id, stripeEvent.Id);

        // 6. 200 OK -> Stripe marks the event delivered and stops retrying.
        return Ok();
    }
}
