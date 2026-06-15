# 7. Outbound webhooks — when YOU are the provider

The mirror image of #2. The moment you send webhooks to *your* customers, you owe
them the guarantees you wanted from Stripe. A bare `PostAsJsonAsync` is not a
webhook system — it is a way to silently lose your customers' events.

**Task:** "Notify the customer's endpoint when an order ships."

### Without greybeard

```csharp
public async Task NotifyShipped(Order order)
{
    var sub = await _db.Subscriptions.FirstAsync(s => s.CustomerId == order.CustomerId);
    using var http = new HttpClient();
    await http.PostAsJsonAsync(sub.Url, new { order.Id, status = "shipped" });
    // fire-and-forget.
}
```

Everything that can go wrong, does:
- **Unsigned** → the customer cannot tell a real event from a forged one.
- **No persistence** → if the POST throws, the event is gone forever.
- **No retry** → the customer's endpoint blips for 30s, they never hear about the shipment.
- **No timeout / SSRF guard** → `sub.Url` could be `http://169.254.169.254/...` and hit your cloud metadata endpoint.
- **Blocks the caller** → the ship-order request hangs on the customer's slow server.

### With greybeard — Part A: enqueue (don't block, don't lose)

```csharp
// greybeard[O2:outbox]: persist the event first, in the SAME tx as the state change. If we crash after
// commit, the dispatcher still delivers it. greybeard[O7:async]: the caller never waits on HTTP.
public async Task NotifyShipped(Order order, CancellationToken ct)
{
    _db.WebhookOutbox.Add(new OutboxEvent
    {
        Id = Guid.NewGuid(),                 // greybeard[O5:stable-id]: unique, stable id the consumer dedupes on
        CustomerId = order.CustomerId,
        Type = "order.shipped",
        Payload = JsonSerializer.Serialize(new { order.Id, status = "shipped" }),
        Status = OutboxStatus.Pending,
        Attempts = 0,
    });
    await _db.SaveChangesAsync(ct);          // committed alongside the order's own state change
}
```

### With greybeard — Part B: the dispatcher (sign, retry, dead-letter)

```csharp
// Runs as a background worker. Picks up pending events and delivers them with all the guarantees.
public async Task Dispatch(OutboxEvent evt, Subscription sub, CancellationToken ct)
{
    // greybeard[O6:ssrf]: validate the target BEFORE calling it — public host only, https, no link-local/private ranges.
    if (!_urlGuard.IsSafePublicHttps(sub.Url))
    {
        await DeadLetter(evt, "unsafe target url", ct);
        return;
    }

    // greybeard[O1:sign]: HMAC the exact bytes we send, with the subscriber's secret. Send signature + timestamp
    // so the consumer can verify and reject replays — the inbound rungs W1-W3, from the sender's side.
    var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var signature = Hmac($"{ts}.{evt.Payload}", sub.SigningSecret);

    using var req = new HttpRequestMessage(HttpMethod.Post, sub.Url)
    {
        Content = new StringContent(evt.Payload, Encoding.UTF8, "application/json"),
    };
    req.Headers.Add("Webhook-Id", evt.Id.ToString());        // O5
    req.Headers.Add("Webhook-Timestamp", ts.ToString());     // O1 (replay defence for the consumer)
    req.Headers.Add("Webhook-Signature", signature);         // O1

    try
    {
        // greybeard[O6:timeout]: short per-attempt timeout; reuse a pooled client (no socket exhaustion).
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var resp = await _client.SendAsync(req, cts.Token);

        if (resp.IsSuccessStatusCode)
        {
            evt.Status = OutboxStatus.Delivered;
        }
        else
        {
            await Reschedule(evt, ct);                        // 4xx/5xx -> retry path
        }
    }
    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
    {
        await Reschedule(evt, ct);                            // network/timeout -> retry path
    }
    await _db.SaveChangesAsync(ct);
}

private async Task Reschedule(OutboxEvent evt, CancellationToken ct)
{
    evt.Attempts++;
    if (evt.Attempts >= MaxAttempts)
    {
        // greybeard[O4:dead-letter]: stop retrying, park it, make the failure VISIBLE (alert/dashboard). Never silent.
        await DeadLetter(evt, $"max attempts ({MaxAttempts}) exhausted", ct);
        return;
    }
    // greybeard[O3:backoff]: exponential backoff + jitter so a down consumer doesn't get hammered and recovers cleanly.
    var delay = TimeSpan.FromSeconds(Math.Pow(2, evt.Attempts)) + Jitter();
    evt.NextAttemptAt = DateTimeOffset.UtcNow + delay;
    evt.Status = OutboxStatus.Pending;
}
```

**Rungs:** O1–O7, plus trust-boundary (SSRF), and the transactional-outbox pattern.
**3am pages saved:** the customer who never got their "shipped" event, the lost
events after a deploy, and the SSRF that let a subscriber URL probe your internal network.

> Inbound (#2) and outbound (this) are mirror images: **verify what you receive,
> sign and guarantee what you send.**
