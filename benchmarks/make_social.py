#!/usr/bin/env python3
"""Render the shareable before/after social card (PNG, 1200x630 OG image, dark theme).

Matches the README hero aesthetic: greybeard wordmark, the "paranoid about the right
things" tagline, the money before/after, and the seven-rung ladder strip. No portrait.
Portable fonts (macOS -> Linux -> Pillow default); writes assets/social.png.
Needs Pillow:  python3 -m pip install pillow
"""
import os
from PIL import Image, ImageDraw, ImageFont

W, H = 1200, 630
BG = (13, 17, 23)
PANEL = (22, 27, 34)
STROKE = (33, 38, 45)
FG = (230, 237, 243)
SUBTLE = (201, 209, 217)
MUT = (139, 148, 158)
RED = (248, 81, 73)
GREEN = (63, 185, 80)
COMMENT = (110, 170, 120)

# Font candidates per style: (path, ttc-index). First that loads wins.
FONTS = {
    "regular": [("/System/Library/Fonts/Helvetica.ttc", 0),
                ("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", 0)],
    "bold": [("/System/Library/Fonts/Helvetica.ttc", 1),
             ("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 0)],
    "mono": [("/System/Library/Fonts/SFNSMono.ttf", 0),
             ("/System/Library/Fonts/Monaco.ttf", 0),
             ("/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", 0)],
}


def font(size, style="regular"):
    for path, idx in FONTS[style]:
        if os.path.exists(path):
            try:
                return ImageFont.truetype(path, size, index=idx)
            except Exception:
                continue
    return ImageFont.load_default()


img = Image.new("RGB", (W, H), BG)
d = ImageDraw.Draw(img)

# ---- header: wordmark + underline + tagline ----
d.text((50, 40), "greybeard", font=font(56, "bold"), fill=FG)
d.rounded_rectangle([52, 112, 52 + 292, 117], radius=2, fill=GREEN)
d.text((52, 132), "paranoid about the right things", font=font(26), fill=SUBTLE)

# ---- before / after panels ----
pad, gap = 50, 28
panel_w = (W - 2 * pad - gap) // 2
panel_y, panel_h = 196, 322


def panel(x, label, label_color, lines):
    d.rounded_rectangle([x, panel_y, x + panel_w, panel_y + panel_h],
                        radius=14, fill=PANEL, outline=STROKE)
    d.rounded_rectangle([x, panel_y, x + panel_w, panel_y + 42], radius=14, fill=label_color)
    d.rectangle([x, panel_y + 28, x + panel_w, panel_y + 42], fill=label_color)
    d.text((x + 20, panel_y + 10), label, font=font(20, "bold"), fill=BG)
    ty = panel_y + 62
    mono = font(17, "mono")
    for ln, color in lines:
        d.text((x + 20, ty), ln, font=mono, fill=color)
        ty += 26


left = [
    ("public double CalculateRefund(", FG),
    ("    double total, double pct)", FG),
    ("  => total * (pct / 100.0);", FG),
    ("", FG),
    ("// double != money.", COMMENT),
    ("// 0.1 + 0.2 == 0.30000000000000004", COMMENT),
    ("// scale that to 1M transactions.", COMMENT),
    ("// finance opens a ticket with", COMMENT),
    ("// your name on it.", COMMENT),
    ("", FG),
    ("✗ floating-point money, silent drift", RED),
]
right = [
    ("// greybeard[1:money]: int64 minor", COMMENT),
    ("// units. never float.", COMMENT),
    ("public long CalculateRefundMinor(", FG),
    ("    long totalMinor, int pctBps)", FG),
    ("  => (long)Math.Round(", FG),
    ("      (decimal)totalMinor", FG),
    ("      * pctBps / 10_000m,", FG),
    ("      MidpointRounding.ToEven);", FG),
    ("", FG),
    ("✓ (1999, 3000) => 600  ($6.00, exact)", GREEN),
]

panel(pad, "WITHOUT greybeard", RED, left)
panel(pad + panel_w + gap, "WITH greybeard", GREEN, right)

# ---- footer: the seven-rung ladder strip + url ----
rungs = "money · idempotency · timeouts · concurrency · no N+1 · partial failure · observable"
d.text((50, H - 42), rungs, font=font(17), fill=MUT)
url = "github.com/ManojLingala/greybeard"
uf = font(17, "bold")
d.text((W - 50 - d.textlength(url, font=uf), H - 42), url, font=uf, fill=GREEN)

out = os.path.join(os.path.dirname(__file__), "..", "assets", "social.png")
img.save(out)
print("wrote", os.path.relpath(out), img.size)
