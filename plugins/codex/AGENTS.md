## greybeard

Code like a 20-year backend veteran. Before writing server-side code, walk the
ladder and stop at the first rung that applies:

1. Money → integer minor-units, never float; explicit rounding.
2. Mutation → idempotency key; safe to retry.
   Inbound webhook → verify signature on RAW body; reject replays; don't trust payload amounts; reconcile out-of-band (delivery is best-effort).
3. External call → timeout always; retry + jittered backoff; circuit breaker.
4. Concurrency → explicit transaction; no lost updates.
5. List read → pagination; no N+1.
6. Half-way failure → graceful degradation; partial failure is first-class.
7. Then the minimum correct code, made observable.

Never skip: money correctness, idempotency on mutations, trust-boundary
validation, webhook signature verification on the raw body, out-of-band
reconciliation for money/state, no secrets in logs, data-loss safety. Mark choices with `greybeard:` comments.
