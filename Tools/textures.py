# -*- coding: utf-8 -*-
# Textures of the replimat bioscanner (sketch 3 «table with bridge») and incubator (sketch 3 «blue stripes») in the style of the mod's buildings:
# light grey body with a vertical gradient, black outline, blue stripes (default replimat color baked in, the same shapes
# in grey 227 go to the <name>Accent mask that the game paints in the chosen color), glow — white on black (MoteGlow).
# 384 px = 3 cells (drawSize 3). West is not drawn: Graphic_Multi flips east.
# Usage: python3 Tools/textures.py   (Pillow)
import os
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
T = ROOT + '/Textures/'
K = 4  # supersampling
N = 384
BLUE, MASK = (45, 102, 227), (227, 227, 227)
LIGHT, SHADE = (244, 244, 244), (205, 205, 205)
TOP, TOP2 = (226, 226, 226), (204, 204, 204)
GREY, GREY_EDGE = (143, 151, 163), (110, 116, 126)
SCREEN, BAR, BAR2 = (11, 15, 24), (91, 147, 255), (180, 217, 255)
DIM = (30, 48, 84)  # unlit progress strip of the incubator screen: the game fills it with BAR by print progress (Glow.Progress)
EMIT = (143, 214, 255)
OUT = 7


def canvas(size=N, bg=(0, 0, 0, 0)):
    return Image.new('RGBA', (size * K, size * K), bg)


def box(b):
    return [round(v * K) for v in b]


def rr(im, b, r, fill, outline=None, width=0):
    """Rounded rectangle; fill — a color or (top, bottom) for a vertical gradient; outline goes outside the box."""
    d = ImageDraw.Draw(im)
    if outline:
        w = width / 2
        d.rounded_rectangle(box((b[0] - w, b[1] - w, b[2] + w, b[3] + w)), round((r + w) * K), fill=outline)
    if isinstance(fill[0], tuple):
        x0, y0, x1, y1 = box(b)
        top, bottom = fill
        grad = Image.new('RGBA', (x1 - x0, y1 - y0))
        gd = ImageDraw.Draw(grad)
        for y in range(y1 - y0):
            t = y / max(1, y1 - y0 - 1)
            gd.line([(0, y), (x1 - x0, y)], fill=tuple(round(a + (c - a) * t) for a, c in zip(top[:3], bottom[:3])) + (255,))
        mask = Image.new('L', grad.size, 0)
        ImageDraw.Draw(mask).rounded_rectangle([0, 0, grad.size[0] - 1, grad.size[1] - 1], round(r * K), fill=255)
        im.paste(grad, (x0, y0), mask)
    else:
        d.rounded_rectangle(box(b), round(r * K), fill=fill)


def rect(im, b, fill):
    ImageDraw.Draw(im).rectangle(box(b), fill=fill)


def save(im, path, size=N):
    im.resize((size, size), Image.LANCZOS).save(T + path + '.png')


def stripe(base, accent, b):
    rect(base, b, BLUE)
    rect(accent, b, MASK)


def bars(b):
    """Indicator bars of a screen: five short and one long, left to right."""
    x0, y0, x1, y1 = b
    w = (x1 - x0 - 12) / 7
    return [(x0 + 5 + i * w, y0 + 5, x0 + 5 + i * w + w * 0.7, y1 - 5) for i in range(5)] + [(x1 - 7 - w * 1.6, y0 + 5, x1 - 7, y1 - 5)]


def screen(im, b):
    rr(im, b, 4, SCREEN)
    for i, r in enumerate(bars(b)):
        rect(im, r, BAR if i < 5 else BAR2)


# ---- north (front: the colonist stands south of the table) ----

def table_front(base, accent, rail_behind=True):
    if rail_behind:  # gantry rail along the far edge, the scanner block rides on it
        rr(base, (52, 150, 332, 170), 10, GREY, (0, 0, 0, 255), 5)
    rr(base, (24, 200, 360, 290), 12, (LIGHT + (255,), SHADE + (255,)), (0, 0, 0, 255), OUT)
    stripe(base, accent, (25, 250, 359, 259))
    for x in (46, 270):
        rr(base, (x, 268, x + 68, 277), 4, (233, 233, 233, 255), (155, 155, 155, 255), 3)
    rr(base, (38, 168, 346, 210), 8, (TOP + (255,), TOP2 + (255,)), (0, 0, 0, 255), 6)
    if not rail_behind:  # from behind the rail is the near edge
        rr(base, (52, 204, 332, 222), 9, GREY, (0, 0, 0, 255), 5)


def block_front(block, accent, glow, face=True):
    rr(block, (154, 92, 230, 200), 6, (LIGHT + (255,), SHADE + (255,)), (0, 0, 0, 255), OUT)
    if face:
        screen(block, (163, 102, 221, 124))
        haze(glow, (163, 102, 221, 124))
    stripe(block, accent, (155, 134, 229, 143))
    rr(block, (166, 153, 218, 160), 3, (233, 233, 233, 255), (155, 155, 155, 255), 3)
    rr(block, (160, 186, 224, 197), 3, EMIT + (255,))
    beam(glow, (160, 186, 224, 197), (192, 202, 78, 22))


def haze(glow, b, v=90, blur=4):
    """Soft glow of a screen: dim, so the bars stay readable."""
    soft = canvas(bg=(0, 0, 0, 0))
    rect(soft, b, (v, v, v, 255))
    glow.alpha_composite(soft.filter(ImageFilter.GaussianBlur(blur * K)))


def beam(glow, emitter, spot):
    """Scanning light: the emitter bar and a soft spot on the table under it."""
    cx, cy, rx, ry = spot
    soft = canvas(bg=(0, 0, 0, 0))
    ImageDraw.Draw(soft).ellipse(box((cx - rx, cy - ry, cx + rx, cy + ry)), fill=(150, 150, 150, 255))
    glow.alpha_composite(soft.filter(ImageFilter.GaussianBlur(9 * K)))
    rect(glow, emitter, (230, 230, 230, 255))


# ---- east (long axis vertical, the front faces west: Graphic_Multi flips it for west) ----

def table_side(base, accent):
    rr(base, (136, 22, 248, 352), 12, (LIGHT + (255,), SHADE + (255,)), (0, 0, 0, 255), OUT)
    rr(base, (146, 30, 238, 328), 8, (TOP + (255,), TOP2 + (255,)), (0, 0, 0, 255), 5)
    stripe(base, accent, (137, 336, 247, 344))
    rr(base, (226, 44, 244, 314), 8, GREY, (0, 0, 0, 255), 4)  # gantry rail on the far side


def block_side(block, accent, glow):
    rr(block, (118, 154, 266, 222), 6, (LIGHT + (255,), SHADE + (255,)), (0, 0, 0, 255), OUT)
    screen(block, (126, 162, 150, 214))
    haze(glow, (126, 162, 150, 214))
    stripe(block, accent, (180, 155, 189, 221))
    rr(block, (156, 210, 252, 218), 3, EMIT + (255,))
    beam(glow, (156, 210, 252, 218), (198, 232, 56, 26))


# ---- incubator 3x2: base with a screen in front, glass with two stripes over the animal (second layer, drawn above it) ----
# Footprint: north/south — y 64..320, east — x 64..320. Glass interior centre: north/south (192, 132) — Bio.GlassZ = 60/128;
# east (213, 192) — Bio.GlassX = 21/128.
GLASS_N, GLASS_E = (24, 20, 360, 244), (96, 12, 330, 372)
# progress strip — 1/3 of the building's length (128 of 384), the same boxes in Glow.StripN/StripE (Source/Buildings.cs); screen — 5 around it
STRIP_N, STRIP_E = (39, 257, 167, 281), (65, 35, 87, 163)
SCREEN_N, SCREEN_E = (34, 252, 172, 286), (60, 30, 92, 168)
TINT, SHINE = (170, 210, 255, 55), (255, 255, 255, 120)
INSIDE = ((214, 226, 240, 255), (184, 196, 212, 255))
TRAY, PILL, PILL_EDGE = (150, 160, 174, 255), (233, 233, 233, 255), (155, 155, 155, 255)


def ring(im, b, r, color, width):
    """Outline only (inside the box): under a translucent fill nothing dark shows through."""
    ImageDraw.Draw(im).rounded_rectangle(box(b), round(r * K), outline=color, width=round(width * K))


def dots(im, ds):
    for x, y, r in ds:
        ImageDraw.Draw(im).ellipse(box((x - r, y - r, x + r, y + r)), fill=SHINE)


def strip_screen(im, rot):
    rr(im, SCREEN_E if rot == 'east' else SCREEN_N, 4, SCREEN)
    rect(im, STRIP_E if rot == 'east' else STRIP_N, DIM)


def incubator_base(base, rot):
    if rot == 'east':
        rr(base, GLASS_E, 8, INSIDE)
        rr(base, (124, 30, 302, 354), 12, TRAY)  # tray the animal lies on
        rr(base, (52, 20, 104, 364), 10, (LIGHT + (255,), SHADE + (255,)), (0, 0, 0, 255), OUT)
        strip_screen(base, rot)
        for y in (184, 197, 210):
            rr(base, (62, y, 94, y + 7), 3, PILL, PILL_EDGE, 2)
        return
    rr(base, GLASS_N, 8, INSIDE)
    rr(base, (44, 204, 340, 230), 10, TRAY)
    rr(base, (12, 238, 372, 324), 12, (LIGHT + (255,), SHADE + (255,)), (0, 0, 0, 255), OUT)
    if rot == 'north':
        strip_screen(base, rot)
        for y in (255, 268, 281):
            rr(base, (238, y, 354, y + 7), 3, PILL, PILL_EDGE, 2)
    pills = ((34, 298, 160, 312), (172, 300, 222, 310)) if rot == 'north' else ((34, 276, 160, 290), (224, 276, 350, 290))
    for b in pills:
        rr(base, b, 5, PILL, PILL_EDGE, 3)


def incubator_glass(glass, accent, rot):
    g = GLASS_E if rot == 'east' else GLASS_N
    rr(glass, g, 8, TINT)
    if rot == 'east':
        for x in (106, 308):
            stripe(glass, accent, (x, 13, x + 12, 371))
        rr(glass, (130, 34, 139, 130), 4, SHINE)
        dots(glass, ((156, 52, 4), (284, 320, 3), (152, 344, 3)))
    else:
        for y in (32, 222):
            stripe(glass, accent, (25, y, 359, y + 12))
        rr(glass, (40, 60, 49, 150), 4, SHINE)
        dots(glass, ((84, 64, 4), (300, 60, 3), (334, 196, 4)))
    ring(glass, (g[0] - 3.5, g[1] - 3.5, g[2] + 3.5, g[3] + 3.5), 11, (0, 0, 0, 255), 7)


def incubator_glow(screen_glow, glass_glow, rot):
    if rot == 'north':
        haze(screen_glow, SCREEN_N)
    elif rot == 'east':
        haze(screen_glow, SCREEN_E)
    # light from the tray under the animal: dim, so the animal stays readable
    cx, cy, rx, ry = (213, 192, 66, 150) if rot == 'east' else (192, 214, 140, 26)
    soft = canvas(bg=(0, 0, 0, 0))
    ImageDraw.Draw(soft).ellipse(box((cx - rx, cy - ry, cx + rx, cy + ry)), fill=(80, 80, 80, 255))
    glass_glow.alpha_composite(soft.filter(ImageFilter.GaussianBlur(14 * K)))


def icon(layers, crop, name):
    """Menu icon: north view cropped to a square, 128 px; layers — (image, accent mask) bottom to top."""
    im, acc = canvas(), canvas()
    for a, m in layers:
        im.alpha_composite(a)
        # what lies above hides the stripes under it in the mask too
        acc = Image.composite(Image.new('RGBA', a.size, (0, 0, 0, 0)), acc, a.getchannel('A').point(lambda v: 255 if v > 200 else 0))
        acc.alpha_composite(m)
    side = max(crop[2] - crop[0], crop[3] - crop[1])
    for img, suffix in ((im, ''), (acc, 'Accent')):
        sq = Image.new('RGBA', (side, side), (0, 0, 0, 0))
        part = img.crop(crop)
        sq.paste(part, ((side - part.size[0]) // 2, (side - part.size[1]) // 2))
        sq.resize((128, 128), Image.LANCZOS).save(T + 'UI/Icons/' + name + suffix + '.png')


def render_incubator():
    for rot in ('north', 'south', 'east'):
        base, glass, gacc = canvas(), canvas(), canvas()
        scr, glo = canvas(bg=(0, 0, 0, 255)), canvas(bg=(0, 0, 0, 255))
        incubator_base(base, rot)
        incubator_glass(glass, gacc, rot)
        incubator_glow(scr, glo, rot)
        save(base, 'Things/Building/replimatIncubator_' + rot)
        save(glass, 'Things/Building/replimatIncubatorGlass_' + rot)
        save(gacc, 'Things/Building/replimatIncubatorGlassAccent_' + rot)
        save(scr, 'FX/replimatIncubatorScreenGlow_' + rot)
        save(glo, 'FX/replimatIncubatorGlow_' + rot)
        if rot == 'north':
            icon([(base, canvas()), (glass, gacc)], box((6, 12, 378, 330)), 'replimatIncubator')


def render():
    views = {
        'north': (lambda b, a: table_front(b, a), lambda b, a, g: block_front(b, a, g)),
        'south': (lambda b, a: table_front(b, a, rail_behind=False), lambda b, a, g: block_front(b, a, g, face=False)),
        'east': (table_side, block_side),
    }
    for rot, (table, block) in views.items():
        base, acc = canvas(), canvas()
        table(base, acc)
        blk, bacc, glow = canvas(), canvas(), canvas(bg=(0, 0, 0, 255))
        block(blk, bacc, glow)
        save(base, 'Things/Building/replimatBioscanner_' + rot)
        save(acc, 'Things/Building/replimatBioscannerAccent_' + rot)
        save(blk, 'Things/Building/replimatBioscannerBlock_' + rot)
        save(bacc, 'Things/Building/replimatBioscannerBlockAccent_' + rot)
        save(glow, 'FX/replimatBioscannerGlow_' + rot)
        if rot == 'north':
            icon, iacc = base.copy(), acc.copy()
            icon.alpha_composite(blk)
            iacc.alpha_composite(bacc)
            # the block covers the table stripe in the mask too
            hole = Image.new('RGBA', blk.size, (0, 0, 0, 0))
            iacc = Image.composite(hole, iacc, blk.getchannel('A').point(lambda v: 255 if v > 0 else 0))
            iacc.alpha_composite(bacc)
            crop = box((14, 80, 370, 300))
            side = max(crop[2] - crop[0], crop[3] - crop[1])
            for im, name in ((icon, ''), (iacc, 'Accent')):
                sq = Image.new('RGBA', (side, side), (0, 0, 0, 0))
                part = im.crop(crop)
                sq.paste(part, (0, (side - part.size[1]) // 2))
                sq.resize((128, 128), Image.LANCZOS).save(T + 'UI/Icons/replimatBioscanner' + name + '.png')


# ---- skill icons and the clock of the skill table header: a light glyph like the vanilla aptitude gene glyphs, no arrow;
# 64 px, drawn on a 64 grid ----

G = 64
GLYPH, EDGE = (232, 232, 232, 255), (20, 20, 20, 170)


def glyph(draw, angle=0):
    """draw(d) paints the glyph white on a mask (black = cut out); angle turns it counterclockwise."""
    m = Image.new('L', (G * K, G * K), 0)
    draw(ImageDraw.Draw(m))
    return m.rotate(angle, Image.BICUBIC) if angle else m


def gb(*v):
    return [round(x * K) for x in v]


def gline(d, pts, w):
    d.line([(round(x * K), round(y * K)) for x, y in pts], fill=255, width=round(w * K), joint='curve')


def gpoly(d, pts, fill=255):
    d.polygon([(round(x * K), round(y * K)) for x, y in pts], fill=fill)


def leaf(d, x0, y0, length, width, ang):
    import math
    c, s = math.cos(math.radians(ang)), math.sin(math.radians(ang))
    pts = []
    for side in (1, -1):
        for i in range(21):
            t = i / 20 if side == 1 else 1 - i / 20
            u, v = t * length, side * width / 2 * math.sin(math.pi * t)
            pts.append((x0 + u * c - v * s, y0 - u * s - v * c))
    gpoly(d, pts)


def shooting(d):
    d.ellipse(gb(12, 12, 52, 52), outline=255, width=6 * K)
    for a, b in (((32, 3), (32, 22)), ((32, 42), (32, 61)), ((3, 32), (22, 32)), ((42, 32), (61, 32))):
        gline(d, (a, b), 6)
    d.ellipse(gb(28, 28, 36, 36), fill=255)


def melee(d):
    gpoly(d, [(26, 42), (26, 12), (32, 1), (38, 12), (38, 42)])
    d.rounded_rectangle(gb(13, 40, 51, 48), 2 * K, fill=255)
    d.rectangle(gb(28, 48, 36, 57), fill=255)
    d.ellipse(gb(26, 54, 38, 64), fill=255)


def construction(d):
    d.rounded_rectangle(gb(28, 20, 36, 62), 3 * K, fill=255)
    d.rounded_rectangle(gb(12, 6, 52, 22), 3 * K, fill=255)


def mining(d):
    d.rounded_rectangle(gb(28, 14, 36, 62), 3 * K, fill=255)
    d.arc(gb(3, 8, 61, 44), 190, 350, fill=255, width=8 * K)


def cooking(d):
    d.rounded_rectangle(gb(12, 30, 52, 56), 7 * K, fill=255)
    d.rounded_rectangle(gb(7, 26, 57, 32), 2 * K, fill=255)
    d.rounded_rectangle(gb(2, 34, 12, 40), 2 * K, fill=255)
    d.rounded_rectangle(gb(52, 34, 62, 40), 2 * K, fill=255)
    for x in (22, 32, 42):
        gline(d, [(x, 21), (x - 3, 16), (x, 11), (x - 3, 6)], 4)


def plants(d):
    gline(d, [(32, 62), (32, 28)], 6)
    leaf(d, 32, 44, 30, 17, 150)
    leaf(d, 32, 32, 33, 19, 40)


def animals(d):
    d.ellipse(gb(17, 32, 47, 58), fill=255)
    for cx, cy in ((11, 28), (23, 15), (41, 15), (53, 28)):
        d.ellipse(gb(cx - 6, cy - 7, cx + 6, cy + 7), fill=255)


def crafting(d):
    import math
    pts = []
    for i in range(48):
        a = math.tau * i / 48
        r = 28 if (i // 3) % 2 == 0 else 20
        pts.append((32 + r * math.cos(a), 32 + r * math.sin(a)))
    gpoly(d, pts)
    d.ellipse(gb(23, 23, 41, 41), fill=0)


def artistic(d):
    d.ellipse(gb(4, 8, 60, 56), fill=255)
    d.ellipse(gb(44, 34, 64, 54), fill=0)
    d.ellipse(gb(37, 34, 47, 44), fill=0)
    for cx, cy in ((17, 26), (29, 18), (43, 20), (16, 40)):
        d.ellipse(gb(cx - 4.5, cy - 4.5, cx + 4.5, cy + 4.5), fill=0)


def medicine(d):
    d.rounded_rectangle(gb(24, 6, 40, 58), 3 * K, fill=255)
    d.rounded_rectangle(gb(6, 24, 58, 40), 3 * K, fill=255)


def social(d):
    d.rounded_rectangle(gb(4, 8, 60, 44), 11 * K, fill=255)
    gpoly(d, [(14, 40), (12, 58), (30, 42)])
    for x in (19, 32, 45):
        d.ellipse(gb(x - 4.5, 21.5, x + 4.5, 30.5), fill=0)


def intellectual(d):
    d.ellipse(gb(13, 3, 51, 41), fill=255)
    d.rounded_rectangle(gb(23, 34, 41, 48), 2 * K, fill=255)
    d.rounded_rectangle(gb(23, 50, 41, 54), 2 * K, fill=255)
    d.rounded_rectangle(gb(26, 56, 38, 61), 2 * K, fill=255)


def clock(d):
    d.ellipse(gb(4, 4, 60, 60), outline=255, width=6 * K)
    gline(d, [(32, 13), (32, 32), (45, 40)], 6)


def save_glyph(draw, angle, path):
    m = glyph(draw, angle)
    im = Image.new('RGBA', m.size, (0, 0, 0, 0))
    im.paste(Image.new('RGBA', m.size, EDGE), (0, 0), m.filter(ImageFilter.MaxFilter(2 * K + 1)))
    im.paste(Image.new('RGBA', m.size, GLYPH), (0, 0), m)
    im.resize((G, G), Image.LANCZOS).save(path)


def render_skills():
    os.makedirs(T + 'UI/Icons/Skills', exist_ok=True)
    skills = {'Shooting': (shooting, 0), 'Melee': (melee, -45), 'Construction': (construction, -40), 'Mining': (mining, 40),
              'Cooking': (cooking, 0), 'Plants': (plants, 0), 'Animals': (animals, 0), 'Crafting': (crafting, 0),
              'Artistic': (artistic, 0), 'Medicine': (medicine, 0), 'Social': (social, 0), 'Intellectual': (intellectual, 0)}
    for name, (draw, angle) in skills.items():
        save_glyph(draw, angle, T + 'UI/Icons/Skills/' + name + '.png')
    save_glyph(clock, 0, T + 'UI/Icons/replimatClock.png')


if __name__ == '__main__':
    render()
    render_incubator()
    render_skills()
