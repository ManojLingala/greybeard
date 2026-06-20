# Medusa — greybeard field findings

[medusajs/medusa](https://github.com/medusajs/medusa) · TypeScript commerce platform ·
analyzed at commit `7413c50e` (2026-06-19).

**Context first:** Medusa is disciplined about *stored* money — values live as
`BigNumber` and arithmetic goes through `MathBN`, which sidesteps JavaScript's float
problem. The findings below are at the **boundaries** (where money enters as a JS
`number`) and in resilience (outbound calls). Public, non-security only;
security-sensitive candidates (webhook-signature handling, money-path
idempotency/concurrency) are handled privately.

Legend: **confirmed** = read and clear · **smell** = real but mild / convention-driven.

---

## Rung 1 — money (never float)

JavaScript `number` is IEEE-754 float64: it cannot exactly represent many decimal money
values (`0.1 + 0.2 !== 0.3`). Medusa's internal math avoids this with `BigNumber`, but
money still **enters and is coerced** as `number` in places, where precision can be
lost before it's wrapped.

### 1.1 Public workflow inputs typed `amount: number` — **smell**
e.g. `packages/core/core-flows/src/order/workflows/create-order-payment-collection.ts:22`

```ts
export type CreateOrderPaymentCollectionWorkflowInput = {
  order_id: string
  amount: number      // money as JS float64 at the API boundary
}
```
A documented convention, so it's a smell rather than a bug — but a money amount that
can't be represented exactly is already lossy by the time it becomes a `BigNumber`.
Accepting `BigNumberInput` (string) for money inputs removes the boundary risk.

### 1.2 Money coerced via `as number` — **smell**
- `packages/modules/order/src/utils/actions/shipping-add.ts:20` — `amount: action.amount as number`
- `packages/core/core-flows/src/cart/steps/prepare-adjustments-from-promotion-actions.ts:167`
  — a promotion discount coerced to `number`

Each casts a value that originates as `BigNumberInput` down to float64 before use.

### 1.3 Float arithmetic on money (admin) — **smell** (display layer)
`packages/admin/dashboard/src/lib/payment.ts:8`

```ts
acc = acc + ((paymentCollection.captured_amount as number)
           - (paymentCollection.refunded_amount as number))
```
A `reduce` from `0` doing float subtraction/accumulation on captured/refunded amounts.
It's the admin dashboard total (display, not the ledger), so low stakes — but it's the
canonical "summing money in float" pattern and can render `…0000000004`-style artifacts.
Use `BigNumber`/`MathBN` here too.

→ greybeard rung 1; the [Refund example](../examples/01-refund-money.md).

---

## Rung 3 — external calls (timeout always)

Node's `fetch()` has **no default timeout**; without an `AbortController`/`AbortSignal`
the call can hang indefinitely if the peer is slow or unreachable. None of the calls
below set one (verified — no `AbortController`/`timeout` token in the file).

| # | Where | Call | Impact |
|---|-------|------|--------|
| 3.1 | `packages/modules/providers/auth-google/src/services/google.ts:146` | `fetch()` → Google OAuth token exchange | **user login hangs** if Google is slow |
| 3.2 | `packages/modules/auth/src/providers/medusa-cloud-auth.ts:148` | `fetch()` → OAuth token endpoint | blocks the auth flow |
| 3.3 | `packages/modules/notification/src/providers/medusa-cloud-email.ts:33` | `fetch()` → email provider | transactional email send hangs |
| 3.4 | `packages/modules/payment/src/providers/payment-medusa/services/medusa-payments.ts:115` | `fetch()` → payments API | wrapped in retry(3), but each attempt itself is unbounded |

**Minimal fix:** pass `signal: AbortSignal.timeout(ms)` to each `fetch`, or wrap with a
timeout helper. → greybeard rung 3; the
[external-timeout example](../examples/03-external-timeout.md).

> Note 3.4: a retry without a per-attempt timeout is the exact trap greybeard's
> external example calls out — retries can't help if a single attempt never returns.

---

## A fair "handled" — what greybeard's lens *confirmed* was already right

Medusa keeps stored money in `BigNumber` and does aggregate math with `MathBN`
(`MathBN.sum`, etc.) throughout the order/payment workflows. Several money candidates
the finder raised were killed by the skeptic for exactly this reason. That's the
checklist validating the codebase, not the other way around.

---

## Not shown here

Candidates flagged **security-sensitive** — webhook-signature handling
(`payment-stripe` optional secret / unguarded `constructEvent`), and money-path
idempotency/concurrency on retries — were routed to **private disclosure** and are
omitted from this public file.
