#!/usr/bin/env python3
"""Render the real benchmark safety scores to an SVG bar chart (dark UI).

Numbers are DERIVED from a scored run, never hand-typed — pass a scored.json
(produced by `node benchmarks/grade_batch.js <dir>`), or it defaults to the
committed 2026-06-20 multi-model run. Falls back to the self-test fixtures only
if no scored data is found.

  python3 benchmarks/make_chart.py [path/to/_report.scored.json]
"""

import json
import os
import sys

HERE = os.path.dirname(__file__)
TASK_ORDER = ["refund", "webhook", "external", "inventory", "export", "logging", "outbound"]
TASK_LABELS = {
    "refund": "Refund", "webhook": "Webhook", "external": "External",
    "inventory": "Inventory", "export": "Export", "logging": "Logging", "outbound": "Outbound",
}

scored_path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    HERE, "results", "raw-2026-06-20", "_report.scored.json"
)

subtitle = "1.00 = ships zero detected bugs. Deterministic grader (graders.js), self-test verified."
models_note = ""
if os.path.exists(scored_path):
    data = json.load(open(scored_path))
    recs = data["records"]

    def task_mean(task, arm):
        xs = [r["score"] for r in recs if r["task_id"] == task and r["arm"] == arm]
        return sum(xs) / len(xs) if xs else 0.0

    tasks = [TASK_LABELS[t] for t in TASK_ORDER]
    baseline = [round(task_mean(t, "baseline"), 2) for t in TASK_ORDER]
    greybeard = [round(task_mean(t, "greybeard"), 2) for t in TASK_ORDER]
    models = ", ".join(sorted({r["model"] for r in recs}))
    n_per_cell = max(len({r["idx"] for r in recs if r["arm"] == "baseline" and r["task_id"] == t}) for t in TASK_ORDER)
    subtitle = f"1.00 = ships zero detected bugs. Real run: {models} (n={n_per_cell}/cell), graded by graders.js."
else:
    # Fallback: self-test fixture scores (no real run scored yet).
    tasks = [TASK_LABELS[t] for t in TASK_ORDER]
    baseline = [0.00, 0.25, 0.00, 0.00, 0.33, 0.67, 0.00]
    greybeard = [1.00, 1.00, 1.00, 1.00, 1.00, 1.00, 1.00]

W, H = 860, 420
PAD_L, PAD_R, PAD_T, PAD_B = 60, 30, 70, 70
plot_w = W - PAD_L - PAD_R
plot_h = H - PAD_T - PAD_B
n = len(tasks)
group_w = plot_w / n
bar_w = group_w * 0.30
gap = group_w * 0.08

BG = "#0d1117"; FG = "#e6edf3"; MUT = "#8b949e"; GRID = "#21262d"
C_BASE = "#f85149"   # red — bugs shipped
C_GREY = "#3fb950"   # green — safe

def y(v): return PAD_T + plot_h * (1 - v)

s = []
s.append(f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" font-family="-apple-system,Segoe UI,Helvetica,Arial,sans-serif">')
s.append(f'<rect width="{W}" height="{H}" fill="{BG}" rx="10"/>')
s.append(f'<text x="{PAD_L}" y="34" fill="{FG}" font-size="20" font-weight="700">Production-bug safety score by task</text>')
s.append(f'<text x="{PAD_L}" y="54" fill="{MUT}" font-size="12">{subtitle}</text>')

# gridlines + y labels
for t in [0, 0.25, 0.5, 0.75, 1.0]:
    yy = y(t)
    s.append(f'<line x1="{PAD_L}" y1="{yy:.1f}" x2="{W-PAD_R}" y2="{yy:.1f}" stroke="{GRID}" stroke-width="1"/>')
    s.append(f'<text x="{PAD_L-10}" y="{yy+4:.1f}" fill="{MUT}" font-size="11" text-anchor="end">{t:.2f}</text>')

# bars
for i, task in enumerate(tasks):
    gx = PAD_L + i * group_w
    bx = gx + (group_w - (2*bar_w + gap)) / 2
    # baseline
    bh = plot_h * baseline[i]
    s.append(f'<rect x="{bx:.1f}" y="{y(baseline[i]):.1f}" width="{bar_w:.1f}" height="{bh:.1f}" fill="{C_BASE}" rx="3"/>')
    s.append(f'<text x="{bx+bar_w/2:.1f}" y="{y(baseline[i])-6:.1f}" fill="{C_BASE}" font-size="11" text-anchor="middle">{baseline[i]:.2f}</text>')
    # greybeard
    gx2 = bx + bar_w + gap
    gh = plot_h * greybeard[i]
    s.append(f'<rect x="{gx2:.1f}" y="{y(greybeard[i]):.1f}" width="{bar_w:.1f}" height="{gh:.1f}" fill="{C_GREY}" rx="3"/>')
    s.append(f'<text x="{gx2+bar_w/2:.1f}" y="{y(greybeard[i])-6:.1f}" fill="{C_GREY}" font-size="11" text-anchor="middle">{greybeard[i]:.2f}</text>')
    s.append(f'<text x="{gx+group_w/2:.1f}" y="{H-PAD_B+22:.1f}" fill="{FG}" font-size="12" text-anchor="middle">{task}</text>')

# axis line
s.append(f'<line x1="{PAD_L}" y1="{y(0):.1f}" x2="{W-PAD_R}" y2="{y(0):.1f}" stroke="{MUT}" stroke-width="1.5"/>')

# legend
lx, ly = W - PAD_R - 250, 44
s.append(f'<rect x="{lx}" y="{ly-10}" width="12" height="12" fill="{C_BASE}" rx="2"/>')
s.append(f'<text x="{lx+18}" y="{ly}" fill="{FG}" font-size="12">no skill (baseline)</text>')
s.append(f'<rect x="{lx+140}" y="{ly-10}" width="12" height="12" fill="{C_GREY}" rx="2"/>')
s.append(f'<text x="{lx+158}" y="{ly}" fill="{FG}" font-size="12">greybeard</text>')

s.append('</svg>')
out = os.path.join(os.path.dirname(__file__), "..", "assets", "benchmark.svg")
open(out, "w").write("\n".join(s))
print("wrote", os.path.relpath(out))
