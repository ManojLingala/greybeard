<p align="center">
  <img src="assets/logo.png" width="170" alt="greybeard, the backend veteran">
</p>

<h1 align="center">greybeard</h1>

<p align="center">
  <em>He has shipped payment systems that move billions. He does not trust your happy path.</em>
</p>

<p align="center">
  <img src="https://img.shields.io/github/stars/ManojLingala/greybeard?style=flat-square&color=3fb950&label=stars" alt="Stars">
  <img src="https://img.shields.io/badge/license-MIT-3fb950?style=flat-square" alt="MIT license">
  <img src="https://img.shields.io/badge/works%20with-6%20agents-3fb950?style=flat-square" alt="Works with 6 agents">
  <img src="https://img.shields.io/badge/flavor-.NET%20%2F%20C%23-512bd4?style=flat-square" alt=".NET / C#">
</p>

<p align="center">
  <strong>The skill that makes your AI agent code like a 20-year backend veteran.</strong><br>
  <sub>ponytail made your agent lazy. greybeard makes it paranoid — about the right things.</sub>
</p>

---

You hand the agent a tidy little endpoint. It works on your machine. greybeard
reads it for ten seconds and asks the questions that page you at 3am:

> *What happens when this runs twice? When the bank times out? When two requests
> hit the same row? Where did the half-cent go?*

Then it fixes them before they ship.

## Before / after

You ask for a refund calculator. A normal agent writes this:

```csharp
public double CalculateRefund(double total, double pct) => total * (pct / 100.0);
// 19.99 * 0.30 => 5.997  → your ledger stops reconciling, finance opens a ticket
```

With greybeard:

```csharp
// greybeard[1:money]: int64 minor units, never float. rounding is explicit (banker's).
public long CalculateRefundMinor(long totalMinor, int pctBps)
    => (long)Math.Round((decimal)totalMinor * pctBps / 10_000m, MidpointRounding.ToEven);
// (1999, 3000) => 600  → deterministic $6.00, every time
```

The flagship survivor is the [Stripe webhook](examples/02-webhook-idempotency.md):
a handler that demos fine but is **forgeable, replayable, double-credits, and
silently drops payments**. greybeard verifies the signature against the raw body,
rejects replays, processes idempotently, refuses to trust the payload amount, and
adds the **out-of-band reconciliation sweep** that catches the webhooks Stripe
never delivered. Its mirror image is the [outbound webhook sender](examples/07-outbound-webhooks.md)
— when *you* are the provider, greybeard signs every payload, persists to an
outbox before delivering, retries with backoff, dead-letters on give-up, and
guards against SSRF. More survivors in [examples/](examples/) — missing timeouts,
lost-update overselling, N+1 export crashes, secrets in logs.

## Numbers

Seven everyday backend tasks (refund, inbound webhook, gateway call, inventory
decrement, order export, charge logging, outbound webhook), graded by a **deterministic, code-based
scorer** — not an LLM judge, so it can't drift. Score = fraction of
production-bug checks passed (1.00 = ships zero detected bugs).

<p align="center">
  <img src="assets/benchmark.svg" width="820" alt="greybeard scores 1.00 on every task; the no-skill baseline ships bugs on all seven">
</p>

The no-skill baseline averages **0.18** safe; greybeard averages **1.00** on the
validated grader self-test. Reproduce it yourself:

```bash
node benchmarks/selftest.js                              # validate the grader (no keys needed)
npx promptfoo eval -c benchmarks/promptfooconfig.yaml    # run across your own models
```

Method, raw numbers, and the honest status of every figure: [benchmarks/](benchmarks/).

## How it works

Before writing backend code, the agent walks a ladder and stops at the first rung
that applies:

```
1. Money?            → integer minor-units, never float. Explicit rounding.
2. Mutation?         → idempotency key. Safe to retry.
   Inbound webhook?  → verify signature on the RAW body, reject replays, don't trust the payload, reconcile out-of-band.
   Outbound webhook? → sign payloads, persist to an outbox, retry with backoff, dead-letter, guard against SSRF.
3. External call?    → timeout always. Retry + jittered backoff. Circuit breaker.
4. Concurrency?      → explicit transaction. No lost updates.
5. Reads a list?     → pagination. No N+1, no unbounded fan-out.
6. Can fail halfway? → graceful degradation. Partial failure is a first-class path.
7. Then: the minimum correct code — and make it observable.
```

Lazy where it is safe, paranoid where it counts. Security, data-loss safety, and
no-secrets-in-logs are **never** on the chopping block. Examples lean .NET/C# and
EF Core; the rungs are language-agnostic.

Full skill: [SKILL.md](SKILL.md).

## Install

greybeard is a single portable skill. Drop it into whichever agent you use.

**Claude Code**
```bash
git clone https://github.com/ManojLingala/greybeard
cp -r greybeard/plugins/claude-code/skills/greybeard ~/.claude/skills/
# then in a session: /greybeard
```

**Cursor** — copy [`plugins/cursor/greybeard.mdc`](plugins/cursor) into `.cursor/rules/`.
**Codex** — append [`plugins/codex/AGENTS.md`](plugins/codex) to your `AGENTS.md`.
**Windsurf** — copy [`plugins/windsurf/greybeard.md`](plugins/windsurf) into `.windsurf/rules/`.
**Gemini CLI** — see [`plugins/gemini/`](plugins/gemini).

Any agent that reads a system prompt: paste [SKILL.md](SKILL.md).

## Why this exists

The skills topping GitHub right now are mostly generic — verbosity, YAGNI. Almost
none encode the discipline that actually keeps payment and distributed systems
alive in production. greybeard is that discipline, compressed into one file and
given away free. One veteran's scar tissue, reusable by everyone.

## License

MIT — see [LICENSE](LICENSE). Use it, fork it, teach your agent with it.
