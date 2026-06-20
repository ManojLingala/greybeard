using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Payments;

// Minimal, lazy-but-correct Stripe webhook handler.
//
// Why this shape:
// - Use Stripe.net's EventUtility.ConstructEvent to verify the signature for us.
//   It checks the HMAC AND the timestamp tolerance (replay window) in one call,
//   so we don't hand-roll crypto.
// - Stripe retries on any non-2xx (or timeout), so the SAME event can arrive
//   multiple times. We make crediting idempotent by recording the Stripe event
//   id and skipping if we've seen it. That's the whole "redelivery" story.
// - Return 200 fast for anything we've already handled or don't care about,
//   so Stripe stops retrying.

[ApiController]
[Route("webhooks/stripe")]
public sealed class StripeWebhookController : ControllerBase
{
    private readonly string _webhookSecret;
    private readonly IAccountCreditStore _credits;
    private readonly ILogger<StripeWebhookController> _logger;

    public StripeWebhookController(
        IStripeWebhookOptions options,
        IAccountCreditStore credits,
        ILogger<StripeWebhookController> logger)
    {
        _webhookSecret = options.WebhookSigningSecret;
        _credits = credits;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Handle(CancellationToken ct)
    {
        // Read the raw body. The signature is computed over the exact bytes,
        // so we must NOT let model binding reparse/reserialize it.
        string payload;
        using (var reader = new StreamReader(Request.Body))
        {
            payload = await reader.ReadToEndAsync(ct);
        }

        var signature = Request.Headers["Stripe-Signature"];

        Event stripeEvent;
        try
        {
            // Verifies HMAC signature + timestamp tolerance (default 5 min).
            // Throws on a bad/missing signature or a too-old timestamp (replay).
            stripeEvent = EventUtility.ConstructEvent(
                payload,
                signature,
                _webhookSecret);
        }
        catch (StripeException ex)
        {
            // Bad signature => almost certainly not from Stripe. 400 so the
            // sender (and our logs) know it was rejected. Stripe itself won't
            // send a payload that fails its own signing, so this won't loop.
            _logger.LogWarning(ex, "Rejected Stripe webhook: invalid signature.");
            return BadRequest();
        }

        // Idempotency: Stripe may redeliver the same event on retry. Claim the
        // event id atomically; if we've already processed it, ack and stop.
        if (!await _credits.TryClaimEventAsync(stripeEvent.Id, ct))
        {
            _logger.LogInformation(
                "Stripe event {EventId} already processed; acking duplicate.",
                stripeEvent.Id);
            return Ok();
        }

        // We only act on payment_intent.succeeded here. Everything else we
        // acknowledge so Stripe stops retrying it.
        if (stripeEvent.Type == EventTypes.PaymentIntentSucceeded)
        {
            var intent = (PaymentIntent)stripeEvent.Data.Object;

            // Credit using the PaymentIntent id as the unit of work and the
            // amount Stripe actually settled. Customer can be null for guest
            // checkouts; fall back to nothing-to-credit in that case.
            if (string.IsNullOrEmpty(intent.CustomerId))
            {
                _logger.LogWarning(
                    "PaymentIntent {IntentId} succeeded with no customer; nothing to credit.",
                    intent.Id);
            }
            else
            {
                await _credits.CreditAsync(
                    customerId: intent.CustomerId,
                    amountMinorUnits: intent.AmountReceived,
                    currency: intent.Currency,
                    sourcePaymentIntentId: intent.Id,
                    ct: ct);

                _logger.LogInformation(
                    "Credited customer {CustomerId} with {Amount} {Currency} for {IntentId}.",
                    intent.CustomerId, intent.AmountReceived, intent.Currency, intent.Id);
            }
        }

        // 2xx tells Stripe we've durably accepted the event; it won't retry.
        return Ok();
    }
}

/// <summary>Signing secret from config (e.g. "whsec_...").</summary>
public interface IStripeWebhookOptions
{
    string WebhookSigningSecret { get; }
}

/// <summary>
/// Account crediting + dedup. Implementations should make TryClaimEventAsync
/// and CreditAsync durable (and ideally part of the same transaction) so a
/// crash between them doesn't drop or double a credit.
/// </summary>
public interface IAccountCreditStore
{
    /// <summary>
    /// Atomically records that we've seen this Stripe event id. Returns false
    /// if it was already recorded (i.e. this is a redelivery).
    /// </summary>
    Task<bool> TryClaimEventAsync(string eventId, CancellationToken ct);

    Task CreditAsync(
        string customerId,
        long amountMinorUnits,
        string currency,
        string sourcePaymentIntentId,
        CancellationToken ct);
}
