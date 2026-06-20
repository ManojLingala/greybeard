# greybeard in the field

The [benchmark](../benchmarks/) shows greybeard prevents production-bug classes on
*synthetic* tasks. The obvious next question: **do those same bug classes actually
occur in real, mature, widely-used finance/commerce code?**

So we pointed greybeard's lens at two of the most popular open-source projects in
the space and ran an automated **find → adversarially-verify** pass: a finder agent
per `(repo × greybeard rung)` proposes candidates, then a *skeptic* agent whose
default is to **refute** each one re-reads the code and looks for mitigations before
returning a verdict. (Harness: [`benchmarks/fieldwork.workflow.js`](../benchmarks/fieldwork.workflow.js).)

| Project | What it is | Language | Stars (approx) |
|---------|------------|----------|---------------:|
| [Hyperswitch](https://github.com/juspay/hyperswitch) | Payments switch / router (Juspay) | Rust | 20k+ |
| [Medusa](https://github.com/medusajs/medusa) | Commerce platform | TypeScript | 25k+ |

## The honest headline

**These are well-engineered projects.** They are far more disciplined than a no-skill
baseline — Hyperswitch wraps money in a `MinorUnit` type across 300+ files; Medusa
keeps stored money in `BigNumber`/`MathBN`. greybeard's lens mostly **confirms** that
discipline, and that is itself the point: *the checklist matches what serious teams
already do.* But it also surfaces a real, consistent residue of edges — the same
rungs that light up in the synthetic benchmark light up here too.

## The funnel (how 14 finders became the findings below)

```
2 repos × 7 greybeard rungs        → 14 finder agents
   ↓ candidates
adversarial verify (1 skeptic per candidate, instructed to REFUTE)
   ↓
46 verified verdicts:  29 confirmed · 11 smell · 6 handled
   ↓ manual calibration + security triage (see "Method & honesty")
┌─────────────────────────────────────────────────────────────┐
│ 15 non-security correctness findings  → shown here, by rung  │
│ ~24 security-sensitive candidates     → PRIVATE disclosure   │
│  6 "handled" (flagged, code is right) → validates the rung   │
└─────────────────────────────────────────────────────────────┘
```

We are **not** publishing the security-sensitive candidates (webhook-signature gaps,
SSRF, secrets-in-logs, money-path idempotency/concurrency). Those go to the
maintainers privately, never a public issue — see [Method & honesty](#method--honesty).

## Every greybeard rung fired in real code

The strongest validation of the benchmark: each rung that catches a synthetic bug
also matched real production code.

| Rung | greybeard says | Showed up in the wild as |
|-----:|----------------|--------------------------|
| 1 money | integer minor-units, never float | `f64` surcharge math off by 1–2 minor units; money cast to JS `number` at boundaries |
| 2 idempotency | safe to retry, dedupe redelivery | webhook handlers re-applying state on redelivery *(security-sensitive → private)* |
| 3 external | timeout always | `reqwest`/`fetch` clients with **no request timeout** on payment hot paths |
| 4 concurrency | no lost updates | read-modify-write on stock/capture totals *(security-sensitive → private)* |
| 5 reads / N+1 | no query-in-loop | DB query inside a `for` loop in a routing-config path |
| non-neg | no secrets in logs | raw connector responses / payloads logged in error paths *(→ private)* |
| W/O | verify in, sign + SSRF-guard out | optional webhook secret; outbound URL without SSRF allowlist *(→ private)* |

## Public, non-security findings (deep-verified by hand)

Full detail, with `file:line`, the exact greybeard rung, and a minimal fix, per repo:

- **[Hyperswitch findings](hyperswitch.md)** — money (`f64` surcharge), missing timeouts, N+1
- **[Medusa findings](medusa.md)** — money-as-`number` at boundaries, missing `fetch` timeouts

### Headline example — a money bug we reproduced

`hyperswitch/crates/common_utils/src/types.rs:109`, applying a surcharge percentage:

```rust
let result = (amount as f64 * (percentage_f64 / 100.0)).ceil() as i64;
```

`percentage_f64 / 100.0` is not exact in binary float (e.g. `2.5/100`), so `.ceil()`
can land **one minor unit off** vs exact arithmetic. Reproduced: a **2.22%** surcharge
on **100,000,000** minor units yields **2,220,001** via this path vs **2,220,000**
exact — a cent conjured from rounding. `rust_decimal` is *already* a dependency. This
is greybeard's [Refund example](../examples/01-refund-money.md) bug class, in a
payments router handling real money.

## Method & honesty

- **Adversarial by construction.** Every candidate is checked by a skeptic agent told
  to assume the maintainers were right and to hunt for mitigations (central timeout
  layers, idempotency middleware, transaction wrappers, redaction). Only survivors are
  reported. The 6 "handled" verdicts are candidates the skeptic *killed* — greybeard's
  lens flagged them, the code does it correctly.
- **Then a human calibration pass.** The verifier agents are themselves LLMs and
  over-reach. We manually re-read the public findings and **down-rated** several — e.g.
  Medusa's inventory read-modify-write is wrapped in `@InjectTransactionManager()`, so
  it's *not* unprotected; whether a lost update is still possible depends on isolation
  level and whether the write is an atomic SQL update. Those moved to "needs
  confirmation" rather than "confirmed."
- **Security ≠ correctness.** We route anything where public disclosure could aid an
  attacker (signature bypass, SSRF, secret leak, money-path races) to **private
  disclosure only**. The findings shown here are pure correctness/resilience issues
  (rounding, timeouts, N+1) — safe to discuss in the open and the kind of thing that
  lands as a routine PR.
- **No drive-by issues.** Nothing here was filed on the upstream repos. We intend to
  offer fixes as respectful, well-argued PRs — never auto-generated issue spam.
- **Reproducible.** Re-run the whole pass with
  [`benchmarks/fieldwork.workflow.js`](../benchmarks/fieldwork.workflow.js) (read-only
  agents over a clone of each repo).

> These projects are good. The goal of this exercise is not to dunk on them — it is to
> show that greybeard's discipline maps onto the exact edges that survive even in
> careful real-world payment and commerce code, and to upstream improvements.
