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
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(TimeSpan.FromSeconds(5));                     // hard ceiling per attempt

    var resp = await _resiliencePipeline.ExecuteAsync(            // retry(3) + jitter + breaker(5 fails -> open 30s)
        async token => await _client.PostAsJsonAsync("charge", req, token),
        cts.Token);

    resp.EnsureSuccessStatusCode();
    return (await resp.Content.ReadFromJsonAsync<ChargeResult>(cancellationToken: cts.Token))!;
    // greybeard[2:idempotency]: req carries an Idempotency-Key header so retries don't double-charge
}
```

**Rungs:** 3 (external), 2 (idempotency).
**3am page saved:** the full outage caused by one slow third party.
