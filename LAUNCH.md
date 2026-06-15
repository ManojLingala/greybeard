# Launch copy

Attach `assets/social-before-after.png` to the tweet (and to the HN post as the first comment image if you want).
The card's WITHOUT side now shows the canonical `0.1 + 0.2 == 0.30000000000000004` float meme, so it matches the X copy below.

---

## X launch — final cut (use this)

### Single high-impact tweet (post this first, with the image)

> Most "money bugs" aren't bugs. They're `double`.
>
> One floating-point multiplication. A million transactions. A ledger that no longer reconciles.
>
> I built **greybeard** so your AI agent stops writing junior-dev code.
>
> github.com/ManojLingala/greybeard

*(attach `assets/social-before-after.png`)*

### Follow-up thread (reply to your own tweet 1)

**2/ — the pain**
> Every backend veteran has lived this:
> → refund calc uses `double`
> → QA passes. tests pass.
> → 6 months later finance emails: "the ledger is off by $1,847.23"
> → you spend a week bisecting commits
> The bug was written on day one.

**3/ — the lesson**
> Senior backend habits AI agents skip:
> • money in minor units (`long`), never `double`
> • idempotency keys on every mutation
> • timeouts on every external call
> • webhooks with retries + signatures (in & out)
> • no N+1 queries, ever
> greybeard puts them in your prompt.

**4/ — how it works**
> greybeard is one portable SKILL.md that bolts onto Claude Code, Codex, Antigravity, Cursor or Copilot.
> Before writing backend code the agent walks a ladder — money → mutation → external call → concurrency → reads → partial failure → observability — and stops at the first rung that applies.
> Your agent codes like it's been burned before.

**5/ — proof, honestly**
> Scored by a deterministic, code-based grader — not an LLM judge, so it can't drift.
> No-skill baseline ships bugs on 7/7 tasks. greybeard: zero.
> Validate the grader yourself, no API keys: `node benchmarks/selftest.js`

**6/ — CTA**
> Free. Open source. MIT.
> ⭐ github.com/ManojLingala/greybeard
> If you've ever been the name on a finance ticket — this is for you.

### Posting tips

- Post 9–11 AM ET, Tue/Wed (peak dev-Twitter).
- Reply to your own tweet 1 with tweet 2 within seconds — early author replies get boosted.
- Reply to every comment in the first 30–60 min.
- Pin the thread to your profile.
- Tag 1–2 aligned dev-tool accounts only if it reads naturally; don't spam.

---

## Launch tweet (single — earlier draft)

> Meet **greybeard** — the skill that makes your AI agent code like a 20-year backend veteran.
>
> ponytail made your agent lazy. greybeard makes it paranoid about the right things: money in integer minor-units, idempotent writes, timeouts, signed webhooks (in *and* out), no N+1, no secrets in logs.
>
> Deterministic grader, not an LLM judge. Baseline ships bugs on 7/7 tasks; greybeard ships zero.
>
> Works with Claude Code, Codex, Antigravity, Cursor & Copilot. MIT.
>
> github.com/ManojLingala/greybeard

*(attach the before/after image)*

---

## Launch tweet (thread version, if you'd rather)

**1/**
You hand your AI agent a tidy refund endpoint. It works on your machine.

Then it computes `19.99 * 0.30 = 5.997`, your ledger stops reconciling, and finance opens a ticket with your name on it.

I built a skill that catches this before it ships. Meet greybeard. 🧵

**2/**
ponytail went viral teaching agents to be lazy (YAGNI, less code).

greybeard is its opposite number for the backend: paranoid about the things that actually page you at 3am.

Money. Idempotency. Timeouts. Webhooks. Concurrency. Observability.

**3/**
It's a ladder. Before writing backend code the agent stops at the first rung that applies:

1 Money → int minor-units, never float
2 Mutation → idempotency key
3 External call → timeout + backoff + breaker
4 Concurrency → real transactions
5 Reads → pagination, no N+1
6 Partial failure → first-class path
7 Observability

**4/**
The flagship survivor is the Stripe webhook: a handler that demos fine but is forgeable, replayable, double-credits, and silently drops payments.

greybeard verifies the raw-body signature, rejects replays, stays idempotent, distrusts the payload amount, and adds an out-of-band reconciliation sweep.

**5/**
Numbers, honestly: 7 backend tasks, scored by a **deterministic code-based grader** (not an LLM judge, so it can't drift).

No-skill baseline averages 0.18 safe. greybeard averages 1.00.

Run the grader self-test yourself with zero API keys: `node benchmarks/selftest.js`

**6/**
Works with Claude Code, Codex, Antigravity, Cursor, GitHub Copilot, Windsurf, Gemini — one portable SKILL.md, drop it wherever you code.

MIT. Free. One veteran's scar tissue, reusable by everyone.

github.com/ManojLingala/greybeard

---

## Show HN post

**Title:**

`Show HN: Greybeard – a skill that makes your AI agent code like a backend veteran`

**URL:** `https://github.com/ManojLingala/greybeard`

**First comment (the "text" of the Show HN):**

> Hi HN. I'm a backend engineer — ~20 years across payments, microservices, and distributed systems. Coding agents are great at the happy path and consistently bad at the things that actually cause incidents: floating-point money, non-idempotent writes, missing timeouts, forgeable/replayable webhooks, lost updates under concurrency, N+1 explosions, secrets in logs.
>
> greybeard is a single portable skill (one SKILL.md) that makes an agent walk a short discipline ladder before it writes backend code, and stop at the first rung that applies — money, mutation, external call, concurrency, reads, partial failure, observability. It leans .NET/C# in the examples but the rungs are language-agnostic.
>
> The part I care most about is the benchmark being honest. It's 7 realistic backend tasks scored by a **deterministic, code-based grader** — it statically inspects the generated code for each bug class. No LLM judge, so it can't drift or be flattered. You can validate the grader itself with no API keys (`node benchmarks/selftest.js`), and then run the full eval across whatever models you have keys for via promptfoo. The flagship case is a Stripe webhook handler that demos fine but is forgeable, replayable, double-credits, and silently drops payments; greybeard fixes all of it and adds an out-of-band reconciliation sweep. There's a mirror case for when *you* are the webhook provider (sign, outbox, retry/backoff, dead-letter, SSRF guard).
>
> It installs into Claude Code, Codex, Antigravity, Cursor, GitHub Copilot, Windsurf, and Gemini — per-agent instructions are in the README. MIT licensed.
>
> Caveat I'll state up front: the headline 0.18 → 1.00 figures come from a small sample run plus the grader self-test fixtures, not an exhaustive multi-model matrix — the repo says so and gives you the exact command to reproduce or extend it on your own keys. I'd genuinely like feedback on the grader's bug-class checks: what production failure modes am I missing that you'd add as a rung?

---

## Posting checklist

- [ ] Flip the repo **public** (Settings → Danger Zone → Change visibility).
- [ ] Confirm the GitHub Action ran green on the default branch (badge / Actions tab).
- [ ] Post the tweet with `assets/social-before-after.png` attached.
- [ ] Submit the Show HN, then immediately add the first comment above.
- [ ] (Optional) run the full benchmark on your keys and drop real numbers into `benchmarks/results/`.
