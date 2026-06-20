#!/usr/bin/env python3
"""Render a shareable before/after social card (PNG) — code-accurate, dark theme."""
from PIL import Image, ImageDraw, ImageFont
import os

W, H = 1200, 675  # 16:9, ideal for X / OG image
BG = (13, 17, 23)
PANEL = (22, 27, 34)
FG = (230, 237, 243)
MUT = (139, 148, 158)
RED = (248, 81, 73)
GREEN = (63, 185, 80)
COMMENT = (110, 170, 120)
KEY = (210, 168, 255)

def font(size, bold=False, mono=False):
    paths = []
    if mono:
        paths = ["/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf"]
    elif bold:
        paths = ["/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"]
    else:
        paths = ["/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"]
    for p in paths:
        if os.path.exists(p):
            return ImageFont.truetype(p, size)
    return ImageFont.load_default()

img = Image.new("RGB", (W, H), BG)
d = ImageDraw.Draw(img)

# Title
d.text((50, 38), "greybeard", font=font(40, bold=True), fill=FG)
d.text((50, 92), "Make your AI agent paranoid about the right things.", font=font(22), fill=MUT)

# logo (optional, top-right)
try:
    logo = Image.open("assets/logo.png").convert("RGBA")
    lw = 110
    logo = logo.resize((lw, int(logo.height * (lw / logo.width))), Image.LANCZOS)
    img.paste(logo, (W - lw - 50, 30), logo)
except Exception as e:
    print("logo skip:", e)

# Two panels
pad = 50
gap = 30
panel_w = (W - 2 * pad - gap) // 2
panel_y = 150
panel_h = 420

def panel(x, label, label_color, lines):
    d.rounded_rectangle([x, panel_y, x + panel_w, panel_y + panel_h], radius=14, fill=PANEL)
    d.rounded_rectangle([x, panel_y, x + panel_w, panel_y + 44], radius=14, fill=label_color)
    d.rectangle([x, panel_y + 30, x + panel_w, panel_y + 44], fill=label_color)
    d.text((x + 20, panel_y + 11), label, font=font(20, bold=True), fill=(13, 17, 23))
    ty = panel_y + 66
    mono = font(16, mono=True)
    for ln, color in lines:
        d.text((x + 20, ty), ln, font=mono, fill=color)
        ty += 26

left = [
    ("public double CalculateRefund(", FG),
    ("    double total, double pct)", FG),
    ("  => total * (pct / 100.0);", FG),
    ("", FG),
    ("// double != money.", COMMENT),
    ("// 19.99 * 0.30 == 5.996999999999999", COMMENT),
    ("// scale that to 1M transactions.", COMMENT),
    ("// finance opens a ticket", COMMENT),
    ("// with your name on it.", COMMENT),
    ("", FG),
    ("\u2717 floating-point money", RED),
    ("\u2717 silent rounding drift", RED),
]

right = [
    ("// greybeard[1:money]: int64", COMMENT),
    ("// minor units. never float.", COMMENT),
    ("public long CalculateRefundMinor(", FG),
    ("    long totalMinor, int pctBps)", FG),
    ("  => (long)Math.Round(", FG),
    ("      (decimal)totalMinor", FG),
    ("      * pctBps / 10_000m,", FG),
    ("      MidpointRounding.ToEven);", FG),
    ("", FG),
    ("\u2713 (1999, 3000) => 600", GREEN),
    ("\u2713 deterministic $6.00", GREEN),
]

panel(pad, "WITHOUT greybeard", RED, left)
panel(pad + panel_w + gap, "WITH greybeard", GREEN, right)

# footer
d.text((50, H - 40), "Money \u00b7 idempotency \u00b7 timeouts \u00b7 webhooks \u00b7 no N+1",
       font=font(18), fill=MUT)
url = "github.com/ManojLingala/greybeard"
url_font = font(18, bold=True)
uw = d.textlength(url, font=url_font)
d.text((W - 50 - uw, H - 40), url, font=url_font, fill=GREEN)

img.save("assets/social-before-after-accurate.png")
print("wrote assets/social-before-after-accurate.png", img.size)
