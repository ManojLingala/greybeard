# Contributing to greybeard

Thanks for helping sharpen greybeard. This is a small, opinionated project: a portable
agent skill plus a deterministic benchmark. Contributions that keep it **sharp,
honest, and reproducible** are very welcome.

## What good contributions look like

- **New survivors / examples** — a real correctness bug an AI agent ships, with a
  before/after and a one-line rule. These are the heart of the repo.
- **Grader checks** — a new cost-bug (or correctness-bug) class with a deterministic
  static check in `benchmarks/graders.js` and a matching BAD/GOOD pair in
  `benchmarks/selftest.js`.
- **Plugin adapters** — keeping the per-agent files in `plugins/` in sync with the
  canonical `SKILL.md`.
- **Docs & wording** — the persona's voice matters; tighten it, don't bloat it.

## Ground rules

1. **The grader stays deterministic.** No LLM-as-judge in the self-test. Every check
   is a static pattern over the generated code, so results are reproducible and CI is
   free to run.
2. **Every grader change ships with a self-test.** If you add a check, add a BAD case
   it catches and a GOOD case it passes in `benchmarks/selftest.js`. `npm test` must
   stay green.
3. **Markers use the `greybeard[N:tag]` form** so the rule a line satisfies is greppable.
4. **Keep model numbers honest.** Don't hand-edit benchmark results. If you run the
   live benchmark, say which models/date and attach the raw output.

## Local setup

```bash
npm install          # eslint + prettier (dev only)
npm run check        # format check + lint + self-test — run this before pushing
```

Individual scripts:

| Command            | What it does                                  |
| ------------------ | --------------------------------------------- |
| `npm test`         | Runs the deterministic grader self-test       |
| `npm run lint`     | ESLint over `benchmarks/**/*.js`              |
| `npm run lint:fix` | ESLint with autofix                           |
| `npm run format`   | Prettier check                                |
| `npm run format:fix` | Prettier write                              |

The live benchmark (real model calls, your own API keys) is **not** part of CI — see
[`BENCHMARK.md`](./BENCHMARK.md). Never commit API keys.

## Pull requests

- Branch off `master`, keep PRs focused (one concern per PR).
- Make sure `npm run check` passes locally — CI runs the same steps.
- Fill in the PR template; explain the *bug class* you're targeting, not just the diff.
- Small, well-argued PRs merge fastest.

## Code of conduct

Be decent. Critique code, never people. Maintainers may remove comments or contributions
that are disrespectful. By contributing you agree your work is licensed under the repo's
[MIT License](./LICENSE).
