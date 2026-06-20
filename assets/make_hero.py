#!/usr/bin/env python3
"""Render the greybeard README hero banner to an SVG (dark UI, themeable, crisp).

Left: wordmark + tagline. Right: the 7-rung ladder — the whole mental model as art.
No raster, no fonts to ship; scales to any width. Run: python3 assets/make_hero.py
"""
import os

W, H = 900, 280
BG = "#0d1117"
FG = "#e6edf3"
SUBTLE = "#c9d1d9"
MUT = "#8b949e"
GREEN = "#3fb950"
RAIL = "#30363d"
CARD_STROKE = "#21262d"

RUNGS = [
    ("1", "money"),
    ("2", "idempotency"),
    ("3", "timeouts"),
    ("4", "concurrency"),
    ("5", "no N+1"),
    ("6", "partial failure"),
    ("7", "observable"),
]

s = []
s.append(
    f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" '
    f'font-family="-apple-system,BlinkMacSystemFont,Segoe UI,Helvetica,Arial,sans-serif" role="img" '
    f'aria-label="greybeard — paranoid about the right things. The seven-rung ladder.">'
)
# card
s.append(f'<rect x="0.5" y="0.5" width="{W-1}" height="{H-1}" rx="14" fill="{BG}" stroke="{CARD_STROKE}"/>')

# ---------------- left block ----------------
LX = 52
s.append(
    f'<text x="{LX}" y="118" font-size="68" font-weight="800" fill="{FG}" '
    f'letter-spacing="-1.5">greybeard</text>'
)
s.append(f'<rect x="{LX+2}" y="138" width="316" height="5" rx="2.5" fill="{GREEN}"/>')
s.append(f'<text x="{LX+2}" y="182" font-size="23" fill="{SUBTLE}">paranoid about the right things</text>')
s.append(
    f'<text x="{LX+2}" y="212" font-size="15" fill="{MUT}">Lazy where it is safe. '
    f'Sharp where it counts.</text>'
)
# on-brand code-comment accent in mono
s.append(
    f'<text x="{LX+2}" y="244" font-size="13.5" fill="{GREEN}" '
    f'font-family="ui-monospace,SFMono-Regular,Menlo,Consolas,monospace">'
    f'// greybeard[1:money]: int64 minor units, never float</text>'
)

# ---------------- right block: the ladder ----------------
RX = 600          # x of the number badges (rail)
LABEL_X = RX + 26
top, bottom = 52, 248
n = len(RUNGS)
step = (bottom - top) / (n - 1)
r = 12

# faint divider between wordmark block and the ladder
s.append(f'<line x1="548" y1="44" x2="548" y2="236" stroke="{RAIL}" stroke-width="1"/>')
s.append(
    f'<text x="{LABEL_X}" y="34" font-size="12" fill="{MUT}" letter-spacing="0.6">'
    f'7 RUNGS · IN ORDER</text>'
)
# the rail
s.append(f'<line x1="{RX}" y1="{top}" x2="{RX}" y2="{bottom}" stroke="{RAIL}" stroke-width="2"/>')

for i, (num, title) in enumerate(RUNGS):
    cy = top + i * step
    # badge
    s.append(f'<circle cx="{RX}" cy="{cy:.1f}" r="{r}" fill="{BG}" stroke="{GREEN}" stroke-width="1.6"/>')
    s.append(
        f'<text x="{RX}" y="{cy:.1f}" font-size="13" font-weight="700" fill="{GREEN}" '
        f'text-anchor="middle" dominant-baseline="central">{num}</text>'
    )
    s.append(
        f'<text x="{LABEL_X}" y="{cy:.1f}" font-size="17" font-weight="600" fill="{FG}" '
        f'dominant-baseline="central">{title}</text>'
    )

s.append("</svg>")

out = os.path.join(os.path.dirname(__file__), "hero.svg")
open(out, "w").write("\n".join(s))
print("wrote", os.path.relpath(out))
