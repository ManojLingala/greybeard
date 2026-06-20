# 3. Calling a payment gateway — the call with no timeout

**Task:** "Charge the card via the gateway client."

### Without greybeard

```csharp
public async Task<ChargeResult> Charge(ChargeRequest req)
{
    var http = new HttpClient();                 // no timeout configured (default 100s)
    var resp = await http.PostAsJsonAsync(GatewayUrl, req);
    return await resp.Content.ReadFromJsonAsync<ChargeResult>();
}
```

The gateway has a bad day and responds slowly. With no timeout, requests pile up,
the thread pool / connection pool exhausts, and *your whole service* goes down
because *their* service was slow. One dependency took out everything.

### With greybeard

```csharp
// greybeard[3:external]: timeout always. retry with jittered backoff. circuit breaker on repeated failure.
// Pipeline registered once (Polly v8); HttpClient reused via IHttpClientFactory (no socket exhaustion).
public async Task<ChargeResult> Charge(ChargeRequest req, CancellationToken ct)
{
    // greybeard[3:external]: the PER-ATTEMPT timeout lives INSIDE _resiliencePipeline (Polly AddTimeout, ~2s),
    // so one slow try is abandoned and retried. This linked token is the OVERALL budget for the whole retry
    // sequence — NOT a per-attempt limit. (If 5s out here were your only timeout, it would cancel mid-retry and
    // silently rob you of the attempts you think you have.)
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(TimeSpan.FromSeconds(10));                    // overall ceiling across all attempts

    var resp = await _resiliencePipeline.ExecuteAsync(           // timeout(2s/try) + retry(3) + jitter + breaker(5 -> open 30s)
        async token =>
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, "charge") { Content = JsonContent.Create(req) };
            // greybeard[2:idempotency]: a STABLE key per charge, so the retries above are exactly-once at the
            // gateway — a retried POST settles the same charge, never a second one.
            msg.Headers.Add("Idempotency-Key", req.IdempotencyKey);
            return await _client.SendAsync(msg, token);
        },
        cts.Token);

    resp.EnsureSuccessStatusCode();
    return (await resp.Content.ReadFromJsonAsync<ChargeResult>(cancellationToken: cts.Token))!;
}
```

**Rungs:** 3 (external), 2 (idempotency).
**3am page saved:** the full outage caused by one slow third party.
