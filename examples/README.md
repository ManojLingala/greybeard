# Survivors

Real bugs a no-skill agent ships, and what greybeard does instead. Each one is a
3am page the veteran already lived through.

| # | Task | The bug the baseline ships | greybeard |
|--:|------|----------------------------|-----------|
| 1 | [Refund calculator](01-refund-money.md) | `double` arithmetic loses half-cents; refunds drift | int64 minor units, explicit rounding |
| 2 | [Stripe webhook ⭐ flagship](02-webhook-idempotency.md) | Forgeable, replayable, double-credits, no reconciliation | raw-body signature verify, replay reject, idempotent, out-of-band reconciliation sweep |
| 3 | [Call a payment gateway](03-external-timeout.md) | No timeout; one slow call hangs the thread pool | timeout + jittered backoff + circuit breaker |
| 4 | [Decrement inventory](04-concurrency.md) | Lost update under concurrency; oversell | optimistic concurrency / row lock |
| 5 | [Export orders](05-pagination-nplus1.md) | Loads every row + N+1 queries; OOM at scale | pagination + projection, single query |
| 6 | [Log a charge](06-logging-secrets.md) | Card number / token written to logs | structured log, secrets redacted |

> All examples are C#/.NET flavored, but every rung is language-agnostic.
