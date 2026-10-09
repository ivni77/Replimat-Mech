# -*- coding: utf-8 -*-
# About/Preview.png 640x360 in the style of the Replimat preview: light triangle background,
# the mod's buildings at one in-game scale, glow in the default replimat color, title bottom left.
# Usage: python3 Tools/preview.py [out.png]   (macOS fonts, Pillow)
import os, sys
from PIL import Image, ImageChops, ImageDraw, ImageFont
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
T = ROOT + '/Textures/'
OUT = sys.argv[1] if len(sys.argv) > 1 else ROOT + '/About/Preview.png'
W, H, CELL = 640, 360, 46  # CELL: pixels per game cell
ACC = (0.20, 0.45, 1.00)
GLOW = tuple(round(255 * (c + (1 - c) * 0.25)) for c in ACC)  # Settings.GlowColor
DIN = '/System/Library/Fonts/Supplemental/DIN Condensed Bold.ttf'

img = Image.new('RGBA', (W, H), (250, 250, 250, 255))
d = ImageDraw.Draw(img)
S = 44; h = S * 0.866
for row in range(-1, int(H / h) + 2):
    for col in range(-1, int(W / S) + 2):
        x, y = col * S + (S / 2 if row % 2 else 0), row * h
        d.polygon([(x, y), (x + S, y), (x + S / 2, y + h)], fill=(245, 245, 246) if (row + col) % 3 else (241, 242, 243))


def load(path):
    return Image.open(T + path + '.png').convert('RGBA')


def tint(im, c):
    r, g, b, a = im.split()
    return Image.merge('RGBA', [ch.point(lambda v, m=m: int(v * m)) for ch, m in zip((r, g, b), c)] + [a])


def haze(g, gain):
    """Glow on a light background: replimat color, alpha from the glow's brightness."""
    a = ImageChops.multiply(g.convert('L'), g.getchannel('A')).point(lambda v: min(255, int(v * gain)))
    out = Image.new('RGBA', g.size, GLOW + (0,)); out.putalpha(a)
    return out


def put(base, cx, bottom, layers=(), glows=(), cells=3):
    im = load(base)
    for path, color in layers:
        lay = load(path)
        im.alpha_composite(tint(lay, color) if color else lay)
    box = im.getchannel('A').point(lambda v: 255 if v > 24 else 0).getbbox()
    k = CELL * cells / im.width
    sp = im.crop(box); sp = sp.resize((round(sp.width * k), round(sp.height * k)), Image.LANCZOS)
    x0, y0 = round(cx - sp.width / 2), round(bottom - sp.height)
    gs = []
    for path, gain in glows:
        g = load(path); gk = CELL * 3 / g.width  # glows are drawn over 3x3 cells
        gx = cx - (im.width / 2 - (box[0] + box[2]) / 2) * k - g.width * gk / 2
        gy = y0 + (im.height / 2 - box[1]) * k - g.height * gk / 2
        gs.append((g.resize((round(g.width * gk), round(g.height * gk)), Image.LANCZOS), gain, (round(gx), round(gy))))
    for g, gain, at in gs:  # haze behind the building, like the Replimat preview
        img.alpha_composite(haze(g, gain * 2), at)
    img.alpha_composite(sp, (x0, y0))
    for g, gain, at in gs:  # screens lit additively, like in the game
        rgb = Image.new('RGB', (W, H)); rgb.paste(tint(g, tuple(c / 255 * gain for c in GLOW)).convert('RGB'), at, g)
        img.paste(ImageChops.add(img.convert('RGB'), rgb).convert('RGBA'))


back, front = 150, 222
put('Things/Building/replimatMatterTankLarge', 100, back, [('Things/Building/replimatMatterTankLargeAccent', ACC)])
put('Things/Building/replimatMatterTank', 180, back, [('Things/Building/replimatMatterTankAccent', ACC)])
put('Things/Building/replimatComputer_north', 320, back - 14, glows=[('FX/replimatComputerScreenGlow_north', 1.4)])
put('Things/Building/replimatMatterHopper_north', 522, back,
    [('Things/Building/replimatMatterHopperAccent_north', ACC), ('Things/Building/replimatMatterHopperLid_north', None),
     ('Things/Building/replimatMatterHopperLidAccent_north', ACC)],
    glows=[('FX/replimatHopperScreenGlow_north', 1.4), ('FX/replimatHopperGlow1_north', 1.2)])
put('Things/Building/replimatTerminalWall_north', 230, front, glows=[('FX/replimatTerminalWallScreenGlow_north', 1.4), ('FX/replimatTerminalWallGlow_north', 1.2)])
put('Things/Building/replimatTerminal_north', 320, front, glows=[('FX/replimatTerminalScreenGlow_north', 1.4), ('FX/replimatTerminalGlow_north', 1.4)])
put('Things/Building/replimatAnimalFeeder', 410, front, glows=[('FX/replimatAnimalFeederGlow', 1.2)], cells=1)

d = ImageDraw.Draw(img)
title, sub = ImageFont.truetype(DIN, 66), ImageFont.truetype(DIN, 30)
x, y = 22, 300
d.text((x, y), 'REPLIMAT', font=title, fill=(40, 40, 42), anchor='ls')
d.text((x + d.textlength('REPLIMAT ', font=title), y), 'MECH', font=title, fill=(45, 105, 235), anchor='ls')
d.text((x + 2, y + 40), 'PATTERN PRINTING  ·  MECHANOIDS  ·  MATTER SPLITTER', font=sub, fill=(120, 120, 124), anchor='ls')
img.convert('RGB').save(OUT, optimize=True)
