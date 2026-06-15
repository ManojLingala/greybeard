# Running the benchmark on your own keys

This is the full, manual walkthrough for reproducing — or extending — the greybeard
numbers using **your own** API keys. Nothing here phones home; promptfoo runs locally
and talks directly to whichever model providers you configure.

There are two layers:

1. **The grader self-test** — proves the scoring is honest. Needs **no API keys**.
2. **The full eval** — actually calls models and scores their output. Needs **your keys**.

Start with layer 1; it takes ten seconds and confirms the contract before you spend a cent.

---

## 0. Prerequisites

- **Node.js 18+** (`node --version`). The self-test and grader are plain Node, no deps.
- For the full eval: **[promptfoo](https://promptfoo.dev)** (`npx` fetches it on demand).
- API keys for whichever providers you want to test. The published sample used
  Anthropic; you can point at OpenAI, Google, local models, anything promptfoo supports.

```bash
git clone https://github.com/ManojLingala/greybeard
cd greybeard
```

---

## 1. Validate the grader (no keys, ~10s)

```bash
node benchmarks/selftest.js
```

This feeds the deterministic grader a set of known-**BAD** and known-**GOOD** C#
snippets for every task and asserts the grader scores them correctly (BAD → low,
GOOD → 1.00). If every line prints `OK`, the scorer is trustworthy. This is exactly
what the [GitHub Action](.github/workflows/selftest.yml) runs on every push, so you
get the same green check we do.

> The grader (`benchmarks/graders.js`) is **code-based, not an LLM judge** — it
> statically greps the generated code for each production-bug class. That is what
> makes results reproducible and free of judge drift. Read it; it's ~150 lines.

---

## 2. Set your keys

Set the env var(s) for the providers you'll use. Examples:

```bash
export ANTHROPIC_API_KEY=sk-ant-...
export OPENAI_API_KEY=sk-...
export GOOGLE_API_KEY=...          # for Gemini, if you add it
```

You only need the keys for providers you actually list in the config (next step).

---

## 3. Choose your models

Open [`benchmarks/promptfooconfig.yaml`](benchmarks/promptfooconfig.yaml) and edit
the `providers:` block to the models **you** have access to. The sample shipped with:

```yaml
providers:
  - id: anthropic:messages:claude-haiku-4-5
  - id: anthropic:messages:claude-sonnet-4-6
  - id: anthropic:messages:claude-opus-4-8
```

Swap or add any promptfoo-supported provider, e.g.:

```yaml
providers:
  - id: openai:chat:gpt-5-4
  - id: anthropic:messages:claude-sonnet-4-6
  - id: google:gemini-3-1-pro
```

### Tune cost vs. confidence

`defaultTest.options.repeat` controls how many times each task runs per
(model × arm). It's **10** by default. The full matrix is:

```
7 tasks  ×  3 arms (baseline / ponytail / greybeard)  ×  N models  ×  repeat
```

So with 3 models and `repeat: 10` that's `7 × 3 × 3 × 10 = 630` generations. To do a
fast/cheap smoke run first, set `repeat: 1` and use one cheap model — that's just
`7 × 3 × 1 = 21` calls.

---

## 4. Run the eval

```bash
npx promptfoo eval -c benchmarks/promptfooconfig.yaml
```

promptfoo runs every cell, scores each output with `graders.js`, and prints a summary
table. Then open the interactive grid to inspect individual generations side by side:

```bash
npx promptfoo view
```

In the grid you can read the exact C# each model produced for each arm and see which
safety checks passed or failed — useful for sanity-checking that greybeard's wins are
real and not grader artifacts.

---

## 5. Read the score

Each task scores **0.0–1.0** = the fraction of that task's production-bug checks that
passed (`1.00` = ships zero detected bugs). A model's arm score is the mean across the
seven tasks. In our self-test fixtures the spread is stark — baseline ≈ **0.18**,
greybeard = **1.00** — but **your** numbers are the ones that count. Re-run, screenshot
the grid, and you have a defensible benchmark.

| Task        | What a failure means |
|-------------|----------------------|
| `refund`    | floating-point money / missing explicit rounding |
| `webhook`   | unverified or replayable inbound webhook, trusts payload, no reconcile |
| `external`  | no timeout, no retry/backoff, socket exhaustion |
| `inventory` | read-modify-write lost update under concurrency |
| `export`    | unbounded query + N+1 |
| `logging`   | secrets / PII written to logs |
| `outbound`  | unsigned payload, no outbox, no retry/dead-letter, SSRF exposure |

---

## 6. Publish your numbers (optional)

If you run a real matrix and want to update the headline figures:

1. Drop your promptfoo output / screenshots into [`benchmarks/results/`](benchmarks/results/).
2. Note the **models, `repeat` count, and date** alongside them — that's the honesty contract.
3. Regenerate the chart if you like: `python3 benchmarks/make_chart.py` (needs `cairosvg`).

If your numbers differ from ours, that's a feature — open an issue with your config.
The grader is the contract; the models are variables.
