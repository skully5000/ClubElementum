"""Builds a Club Elementum role poster from the layered templates in Posters/templates.

    py Tools/make_poster.py --avatar avatar.png --name "David" --role host --element water

Layers, bottom to top (positions match Posters/templates/postertemplateMas.psd):
element background, avatar, element foreground, name, quote, role title, logo.

The avatar should be a cut-out PNG with transparency. It is pasted unchanged; only its size
and position are chosen. Without framing options the avatar is centred under the title
automatically. For deliberate framing, pick a point on the avatar (a face, the middle of a
group) and say where it goes:

    --scale 1.7 --focus 470,300 --at 650,1150

--focus is in avatar pixels, --at in poster pixels. --preview writes a half-size copy with the
title, name, logo and foreground-window guides drawn on, for checking the composition.
--bottom-on-rule puts a cut-off bottom edge of the avatar exactly on the gold rule under the
name, where it is hidden. --quote "..." adds a quote in gold lettering under the gold rule; it is
sized and line-broken to fit above the logo, and a long one lifts the name to make room.
--inspect describes an avatar and writes a gridded copy of it for reading coordinates:

    py Tools/make_poster.py --avatar avatar.png --inspect grid.png

Needs Pillow (pip install pillow). Output: Posters/AI/<role>-<name>.png (1440 x 3440).
"""
import argparse
import itertools
import math
import os
import re
import sys
import unicodedata

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

W, H = 1440, 3440
REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
TEMPLATES = os.path.join(REPO, "Posters", "templates")
FONTS = os.path.join(TEMPLATES, "fonts")
OUT_DIR = os.path.join(REPO, "Posters", "AI")

ELEMENTS = {"water": "water", "fire": "fire", "earth": "earth", "wind": "wind", "air": "wind"}

# Role as typed -> title file stem in Posters/templates/text/*/<stem>-<element>.png
ROLES = {
    "host": "hosts", "hosts": "hosts",
    "lead host": "leadhost", "leadhost": "leadhost",
    "dj": "dj",
    "dancer": "dancer",
    "dance captain": "dancecaptain", "dancecaptain": "dancecaptain",
    "photographer": "photographer",
    "security": "security",
    "lead security": "leadSecurity", "leadsecurity": "leadSecurity",
}
# Title file stem -> output file prefix, so a role gives the same file name however it is typed.
ROLE_FILE = {"hosts": "host", "leadhost": "lead-host", "dj": "dj", "dancer": "dancer",
             "dancecaptain": "dance-captain", "photographer": "photographer",
             "security": "security", "leadSecurity": "lead-security"}

# Titles are scaled to the full poster width. Most are 1774x887 and land at (0,0)-(1440,720);
# these ones sit lower, as placed in the PSD.
TITLE_TOP = {
    "dancecaptain-earth": 76,
    "dancecaptain-fire": 82,
    "dancecaptain-water": 70,
    "dancecaptain-wind": 70,
}
TITLE_BAND = 720

# Logo.png is a 7680x4320 canvas; the PSD scales the whole canvas to 943.1 wide at (-157, 2889).
LOGO_SCALE = 943.1 / 7680
LOGO_POS = (-157, 2889)
LOGO_BOX = (35, 2894, 603, 3419)

# Automatic avatar framing, measured from the hand-made posters.
SOLID_ALPHA = 128          # pixels at least this opaque count as the subject when measuring
CENTER_X = 720             # head/shoulders centre
SUBJECT_TOP = 840          # top of the subject, just under the title
SUBJECT_HEIGHT = 3000      # full-body renders run off the bottom (head to about the knees show)
SUBJECT_MAX_WIDTH = 1800
BUST_WIDTH = 1630          # renders cut off at the bottom (busts) are scaled by width so the sides
BUST_BOTTOM = 3040         # tuck behind the foreground, with the cut edge hidden at the bottom
SOFT_UPSCALE = 3.2         # warn above this: the avatar will look soft

# Name layouts. "classic" matches the PSD: Vast Shadow at 285.5 px (32.8 pt x 8.706) with its
# baseline at 2726.6, which puts the middle of the capitals at y=2640.
NAME_MAX_WIDTH = 1360
NAME_LEADING = 1.2         # Photoshop auto leading
NAME_WRAP_BELOW = 0.7      # wrap onto two lines if one line would shrink below this much
NAME_STYLES = {
    # Flat colour with a soft black glow (36% opacity, spread 19%, size 36), as on the first posters.
    "classic": {
        "font": "VastShadow-Regular.ttf",
        "max_cap": 172.7,      # capital height at the PSD size
        "max_block": 270,      # first capital top to last baseline
        "cap_center_y": 2640,
        "colors": {"water": (0, 192, 240), "fire": (240, 224, 0),
                   "earth": (240, 208, 0), "wind": (240, 224, 0)},
    },
    # Gold bevelled lettering filled with the element's own title artwork, with an element glow,
    # sparkles and a gem divider, to match the role titles. Capitals become swash capitals.
    "element": {
        "font": "CinzelDecorative-Black.ttf",
        # Cinzel's figures are short old-style ones and its 1 reads as an I ("Skul1y" looked like
        # "SKULIY"), so digits come from Playfair Display Black, scaled to the capital height.
        "digits": ("PlayfairDisplay-Variable.ttf", "Black"),
        "max_cap": 215,
        "max_block": 400,
        "cap_center_y": 2600,
    },
}

# Per-element look for the "element" name style. The letter fill is cut from the ornament around
# the element's Host title; "base" shows through its transparent gaps.
ELEMENT_LOOK = {
    "water": {"texture": ("hosts/hosts-water.png", (1354, 100, 1774, 520)), "base": (8, 40, 95),
              "glow": (0, 190, 255), "gem": (40, 150, 255)},
    "fire": {"texture": ("hosts/hosts-fire.png", (1354, 100, 1774, 520)), "base": (90, 15, 0),
             "glow": (255, 90, 0), "gem": (235, 35, 20)},
    "earth": {"texture": ("hosts/hosts-earth.png", (0, 100, 420, 520)), "base": (25, 55, 15),
              "glow": (70, 230, 90), "gem": (30, 190, 80)},
    "wind": {"texture": ("hosts/hosts-wind.png", (1354, 100, 1774, 520)), "base": (25, 80, 70),
             "glow": (120, 245, 215), "gem": (40, 205, 165)},
}
# Cyrillic and Greek letters that people use as look-alikes in display names. The poster fonts
# are Latin-only, so these are drawn (and put in file names) as the Latin letter they imitate.
HOMOGLYPHS = dict(zip(
    "аеорсухіїјѕһԁԛԝпткмнвгьАВЕКМНОРСТХУІЇЈЅαοντικΑΒΕΖΗΙΚΜΝΟΡΤΧΥ",
    "aeopcyxiïjshdqwntkmhbrbABEKMHOPCTXYIÏJSaovtikABEZHIKMNOPTXY"))

RULE_GAP = 0.32            # gold rule sits this many font sizes below the last baseline

# Quote: centred lines between the gold rule and the logo, in the name's gold with an element glow.
QUOTE_FONT = ("PlayfairDisplay-Variable.ttf", "SemiBold")
QUOTE_SIZES = range(52, 39, -1)  # preferred size first, smallest allowed last
QUOTE_MAX_WIDTH = 1240
QUOTE_MAX_LINES = 3
QUOTE_LEADING = 1.22
QUOTE_GAP = 0.3            # quote starts this many name font sizes below the rule, clear of the gems
QUOTE_FLOOR = 2886         # lowest ink, just above the logo (its top is y 2896 at the highest)
QUOTE_MAX_LIFT = 160       # a long quote may raise the name by up to this much to make room
QUOTE_LIFT_COST = 0.25     # when choosing a layout, 4px of lift weigh the same as 1px of font size

GOLD_STOPS = [(0.0, (255, 250, 215)), (0.3, (255, 220, 110)), (0.5, (214, 150, 45)),
              (0.62, (248, 196, 80)), (1.0, (255, 238, 165))]
DARK = (30, 15, 0)


def fail(msg):
    sys.exit(f"make_poster: {msg}")


def latin(text):
    return "".join(HOMOGLYPHS.get(c, c) for c in text)


def slug(text):
    """ASCII, lower case, hyphens instead of spaces: safe in a URL."""
    plain = unicodedata.normalize("NFKD", latin(text)).encode("ascii", "ignore").decode()
    return re.sub(r"[^a-z0-9]+", "-", plain.lower()).strip("-")


def drawable(text, font_file, what="name", fix="use --display-name to give a version it can"):
    """The text as the font can draw it: look-alike letters swapped for Latin ones."""
    font = ImageFont.truetype(os.path.join(FONTS, font_file), 60)

    def ink(c):
        im = Image.new("L", (120, 120), 0)
        ImageDraw.Draw(im).text((20, 20), c, font=font, fill=255)
        return im.tobytes(), font.getlength(c)
    missing = ink("􏿽")
    has = lambda c: c.isspace() or ink(c) != missing
    shown = "".join(c if has(c) else HOMOGLYPHS.get(c, c) for c in text)
    bad = sorted({c for c in shown if not has(c)})
    if bad:
        fail(f"the {what} font can't draw {' '.join(bad)}; {fix}")
    return shown


def point(text, flag):
    m = re.fullmatch(r"\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*", text or "")
    if not m:
        fail(f"{flag} must look like X,Y")
    return float(m.group(1)), float(m.group(2))


def template(name):
    path = os.path.join(TEMPLATES, name)
    if not os.path.exists(path):
        fail(f"missing template {path}")
    return Image.open(path).convert("RGBA")


def role_stem(role):
    key = re.sub(r"[\s_-]+", " ", role.strip().lower())
    stem = ROLES.get(key) or ROLES.get(key.replace(" ", ""))
    if stem is None:
        fail(f"unknown role '{role}'. Known roles: {', '.join(sorted(set(ROLES)))}")
    return stem


def find_title(role, element):
    stem = role_stem(role)
    found, available = None, []
    for root, _, files in os.walk(os.path.join(TEMPLATES, "text")):
        for f in files:
            base, ext = os.path.splitext(f)
            if ext.lower() == ".png" and base.lower().startswith(stem.lower() + "-"):
                available.append(base)
                if base.lower() == f"{stem}-{element}".lower():
                    found = os.path.join(root, f)
    if found is None:
        fail(f"no title for {role} / {element}. Available: {', '.join(sorted(available)) or 'none'}")
    return found


def place_title(canvas, path):
    title = Image.open(path).convert("RGBA")
    h = round(title.height * W / title.width)
    title = title.resize((W, h), Image.LANCZOS)
    stem = os.path.splitext(os.path.basename(path))[0].lower()
    if stem in TITLE_TOP:
        top = TITLE_TOP[stem]
    elif abs(h - TITLE_BAND) <= 4:
        top = 0
    else:
        top = (TITLE_BAND - h) // 2
        print(f"warning: no PSD position for {stem}; centring it in the top {TITLE_BAND}px")
    canvas.alpha_composite(title, (0, top))
    return title, (0, top)


def place_logo(canvas):
    logo = template("Logo.png")
    box = logo.getbbox()
    logo = logo.crop(box)
    logo = logo.resize((round(logo.width * LOGO_SCALE), round(logo.height * LOGO_SCALE)), Image.LANCZOS)
    pos = (round(LOGO_POS[0] + box[0] * LOGO_SCALE), round(LOGO_POS[1] + box[1] * LOGO_SCALE))
    canvas.alpha_composite(logo, pos)
    return logo, pos


# --- Avatar ---------------------------------------------------------------------------------

def column_centroid(mask):
    """Mean x of the white pixels in an L mask."""
    cols = mask.resize((mask.width, 1), Image.BOX).get_flattened_data()
    total = sum(cols)
    return sum(i * v for i, v in enumerate(cols)) / total if total else mask.width / 2


def auto_frame(avatar, scale=None):
    """Centre the head and shoulders under the title. Returns (scale, focus, at)."""
    solid = avatar.getchannel("A").point(lambda a: 255 if a >= SOLID_ALPHA else 0)
    box = solid.getbbox()
    if box is None:
        fail("the avatar has no opaque pixels; it needs to be a cut-out PNG with transparency")
    sw, sh = box[2] - box[0], box[3] - box[1]
    bust = box[3] >= avatar.height - 2
    if scale is None:
        scale = BUST_WIDTH / sw if bust else min(SUBJECT_HEIGHT / sh, SUBJECT_MAX_WIDTH / sw)
    top = max(SUBJECT_TOP, BUST_BOTTOM - sh * scale) if bust else SUBJECT_TOP
    # Centre on the head and shoulders (top quarter of the subject), not the whole bounding box,
    # so capes, wings and poses don't push the face off-centre.
    head = solid.crop((0, box[1], solid.width, box[1] + max(1, sh // 4)))
    return scale, (column_centroid(head), box[1]), (CENTER_X, top)


def place_avatar(canvas, avatar, scale, focus, at, backlight=None):
    x = round(at[0] - focus[0] * scale)
    y = round(at[1] - focus[1] * scale)
    resized = avatar.resize((round(avatar.width * scale), round(avatar.height * scale)), Image.LANCZOS)
    layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    layer.paste(resized, (x, y))
    if backlight:
        # Soft light behind the silhouette so dark renders separate from the background.
        # The avatar itself is not changed.
        shape = layer.getchannel("A")
        canvas.alpha_composite(glow(shape, backlight, 0.55, spread=0, blur=45))
        canvas.alpha_composite(glow(shape, backlight, 0.6, spread=0, blur=12))
    canvas.alpha_composite(layer)
    return resized, x, y


def visible_cut_edges(resized, x, y, covers):
    """Where the avatar is cut off by its image border, report any part not hidden by the layers
    above it (covers: (RGBA image, position) pairs)."""
    solid = resized.getchannel("A").point(lambda v: 255 if v >= SOLID_ALPHA else 0)
    cover = Image.new("L", (W, H), 0)
    for img, pos in covers:
        alpha = Image.new("L", (W, H), 0)
        alpha.paste(img.getchannel("A"), pos)
        cover = ImageChops.lighter(cover, alpha)
    open_ = cover.point(lambda v: 255 if v < 128 else 0)
    w, h = solid.size
    found = []
    for side, strip, pos in [("bottom", solid.crop((0, h - 1, w, h)), (x, y + h - 1)),
                             ("top", solid.crop((0, 0, w, 1)), (x, y)),
                             ("left", solid.crop((0, 0, 1, h)), (x, y)),
                             ("right", solid.crop((w - 1, 0, w, h)), (x + w - 1, y))]:
        if strip.getbbox() is None:
            continue
        line = Image.new("L", (W, H), 0)
        line.paste(strip, pos)
        span = ImageChops.multiply(line, open_).getbbox()
        if span and max(span[2] - span[0], span[3] - span[1]) > 8:
            found.append(f"{side} edge at x {span[0]}-{span[2]}, y {span[1]}-{span[3]}")
    return found


# --- Name: shared -----------------------------------------------------------------------------

def cap_height(font):
    return -font.getbbox("E", anchor="ls")[1]


class NameFont:
    """A name style's font at one size. Digits can come from a companion font scaled to the
    capital height, for fonts whose own figures are hard to read."""

    def __init__(self, style, size):
        self.size = size
        self.main = ImageFont.truetype(os.path.join(FONTS, style["font"]), size)
        self.cap = cap_height(self.main)
        self.digits = None
        if "digits" in style:
            path, variation = os.path.join(FONTS, style["digits"][0]), style["digits"][1].encode()
            probe = ImageFont.truetype(path, 200)
            probe.set_variation_by_name(variation)
            one = -probe.getbbox("1", anchor="ls")[1]
            self.digits = ImageFont.truetype(path, max(1, round(200 * self.cap / one)))
            self.digits.set_variation_by_name(variation)

    def runs(self, text):
        out = []
        for ch in text:
            font = self.digits if self.digits and ch.isdigit() else self.main
            if out and out[-1][0] is font:
                out[-1][1] += ch
            else:
                out.append([font, ch])
        return out

    def getlength(self, text):
        return sum(font.getlength(part) for font, part in self.runs(text))

    def draw(self, d, cx, baseline, text, **kw):
        """Draw text centred on cx, sitting on baseline."""
        x = cx - self.getlength(text) / 2
        for font, part in self.runs(text):
            d.text((x, baseline), part, font=font, anchor="ls", **kw)
            x += font.getlength(part)


def layout_name(name, style):
    """Pick one line or a two-line split, whichever allows the larger text. Returns (lines, font)."""
    probe = NameFont(style, 200)
    cap = probe.cap / 200
    max_size = style["max_cap"] / cap

    def fit(lines):
        widest = max(probe.getlength(line) for line in lines)
        block = cap + (len(lines) - 1) * NAME_LEADING
        return min(max_size, NAME_MAX_WIDTH / widest * 200, style["max_block"] / block)

    words = name.split()
    best = ([name], fit([name]))
    if len(words) > 1 and best[1] < max_size * NAME_WRAP_BELOW:
        for i in range(1, len(words)):
            lines = [" ".join(words[:i]), " ".join(words[i:])]
            size = fit(lines)
            if size > best[1]:
                best = (lines, size)
    lines, size = best
    return lines, NameFont(style, size)


def baselines_for(lines, font, cap_center_y):
    """Centre the block on the capitals: first capital top to last baseline."""
    step = font.size * NAME_LEADING
    first = cap_center_y - ((len(lines) - 1) * step - font.cap) / 2
    return [first + i * step for i in range(len(lines))]


def text_mask(size, lines, font, baselines, dy, stroke=0):
    mask = Image.new("L", size, 0)
    d = ImageDraw.Draw(mask)
    for line, base in zip(lines, baselines):
        font.draw(d, size[0] / 2, base - dy, line, fill=255, stroke_width=stroke, stroke_fill=255)
    return mask


def glow(mask, color, opacity, spread, blur, range_=1.0):
    """Photoshop-style outer glow. A range below 1 keeps the glow denser close to the letters."""
    grown = mask.filter(ImageFilter.MaxFilter(spread * 2 + 1)) if spread else mask
    alpha = grown.filter(ImageFilter.GaussianBlur(blur)).point(lambda a: int(min(255, a / range_) * opacity))
    layer = Image.new("RGBA", mask.size, color + (0,))
    layer.putalpha(alpha)
    return layer


def draw_name_classic(canvas, name, element, color):
    style = NAME_STYLES["classic"]
    lines, font = layout_name(name, style)
    baselines = baselines_for(lines, font, style["cap_center_y"])
    top = int(baselines[0] - font.size)
    mask = text_mask((W, int(font.size * (len(lines) + 1))), lines, font, baselines, top)
    layer = glow(mask, (0, 0, 0), 0.36, spread=7, blur=14, range_=0.5)
    layer.paste(color or style["colors"][element], (0, 0), mask)
    canvas.alpha_composite(layer, (0, top))
    return lines, font.size, None, (layer, (0, top))


# --- Name: element style ----------------------------------------------------------------------

def shrink(mask, r):
    """Erode an L mask by about r pixels (thin strokes disappear)."""
    blurred = mask.filter(ImageFilter.GaussianBlur(r))
    return blurred.point(lambda v: max(0, min(255, (v - 200) * 255 // 35)))


def gradient_column(height, bands, stops):
    """A 1-px-wide RGB column with the stops spread over each (top, bottom) band."""
    col = []
    for y in range(height):
        band = min(bands, key=lambda b: 0 if b[0] <= y <= b[1] else min(abs(y - b[0]), abs(y - b[1])))
        t = min(1.0, max(0.0, (y - band[0]) / max(1, band[1] - band[0])))
        for (t0, c0), (t1, c1) in zip(stops, stops[1:]):
            if t <= t1:
                k = (t - t0) / (t1 - t0)
                col.append(tuple(round(a + (b - a) * k) for a, b in zip(c0, c1)))
                break
    img = Image.new("RGB", (1, height))
    img.putdata(col)
    return img


def bevel(img, mask, depth):
    """Light the edges of mask from the top left, like an embossed metal rim."""
    b = mask.filter(ImageFilter.GaussianBlur(max(1, depth * 0.6)))
    d = max(1, round(depth * 0.5))
    inward, outward = ImageChops.offset(b, -d, -d), ImageChops.offset(b, d, d)
    hi = ImageChops.subtract(inward, outward, scale=0.5).point(lambda v: int(v * 0.85))
    lo = ImageChops.subtract(outward, inward, scale=0.5).point(lambda v: int(v * 0.75))
    img = Image.composite(Image.new("RGB", img.size, (255, 252, 230)), img, hi)
    return Image.composite(Image.new("RGB", img.size, (60, 28, 0)), img, lo)


def element_texture(look, size, tile):
    """Tile the element's title ornament, mirrored so the tiles meet without seams."""
    path, box = look["texture"]
    src = Image.open(os.path.join(TEMPLATES, "text", path)).convert("RGBA").crop(box)
    base = Image.new("RGBA", src.size, look["base"] + (255,))
    base.alpha_composite(src)
    t = max(32, round(tile))
    a = base.convert("RGB").resize((t, t), Image.LANCZOS)
    tiles = {(0, 0): a, (1, 0): a.transpose(Image.FLIP_LEFT_RIGHT),
             (0, 1): a.transpose(Image.FLIP_TOP_BOTTOM), (1, 1): a.transpose(Image.ROTATE_180)}
    out = Image.new("RGB", size)
    for j, y in enumerate(range(0, size[1], t)):
        for i, x in enumerate(range(0, size[0], t)):
            out.paste(tiles[(i % 2, j % 2)], (x, y))
    return out


def sparkle(layer, x, y, r, tint):
    """Four-point star glint, like the ones on the role titles."""
    R = int(r * 1.3) + 4
    star = Image.new("L", (2 * R, 2 * R), 0)
    d = ImageDraw.Draw(star)
    w = max(1.5, r * 0.07)
    d.polygon([(R - r, R), (R, R - w), (R + r, R), (R, R + w)], fill=255)
    d.polygon([(R, R - r * 0.7), (R + w, R), (R, R + r * 0.7), (R - w, R)], fill=255)
    d.ellipse((R - w * 1.8, R - w * 1.8, R + w * 1.8, R + w * 1.8), fill=255)
    spot = Image.new("L", (2 * R, 2 * R), 0)
    ImageDraw.Draw(spot).ellipse((R - r * 0.3, R - r * 0.3, R + r * 0.3, R + r * 0.3), fill=255)
    patch = glow(spot, tint, 0.9, spread=0, blur=r * 0.2)
    patch.alpha_composite(glow(star, tint, 0.9, spread=0, blur=max(1, r * 0.06)))
    patch.paste((255, 255, 255), (0, 0), star.filter(ImageFilter.GaussianBlur(0.7)))
    patch.putalpha(ImageChops.lighter(patch.getchannel("A"), star))
    dx, dy = round(x) - R, round(y) - R
    sx, sy = max(0, -dx), max(0, -dy)  # clip at the left/top edge
    layer.alpha_composite(patch, (dx + sx, dy + sy), (sx, sy))


def gem(layer, cx, cy, gw, gh, color):
    """Faceted diamond in a gold setting, like the gems under the role titles."""
    d = ImageDraw.Draw(layer)
    rim = max(2, gw * 0.22)
    halo = Image.new("L", layer.size, 0)
    ImageDraw.Draw(halo).ellipse((cx - gw * 1.6, cy - gh * 1.3, cx + gw * 1.6, cy + gh * 1.3), fill=255)
    layer.alpha_composite(glow(halo, color, 0.8, spread=0, blur=gw * 0.8))
    d.polygon([(cx, cy - gh - rim * 1.8), (cx + gw + rim * 1.8, cy), (cx, cy + gh + rim * 1.8),
               (cx - gw - rim * 1.8, cy)], fill=DARK)
    d.polygon([(cx, cy - gh - rim), (cx + gw + rim, cy), (cx, cy + gh + rim), (cx - gw - rim, cy)],
              fill=(240, 192, 80))
    mix = lambda c, k, to: tuple(round(a + (b - a) * k) for a, b in zip(c, to))
    top, right, bottom, left, mid = (cx, cy - gh), (cx + gw, cy), (cx, cy + gh), (cx - gw, cy), (cx, cy)
    d.polygon([left, top, mid], fill=mix(color, 0.5, (255, 255, 255)))
    d.polygon([top, right, mid], fill=mix(color, 0.15, (255, 255, 255)))
    d.polygon([right, bottom, mid], fill=mix(color, 0.5, (0, 0, 0)))
    d.polygon([bottom, left, mid], fill=mix(color, 0.25, (0, 0, 0)))
    sparkle(layer, cx - gw * 0.35, cy - gh * 0.35, gw * 1.1, color)


def divider(layer, cx, y, half, size, look):
    """Gold rule with a centre gem and two small side gems, echoing the role titles."""
    d = ImageDraw.Draw(layer)
    th = max(2.0, size * 0.026)
    d.polygon([(cx - half - 3, y), (cx, y - th - 3), (cx + half + 3, y), (cx, y + th + 3)], fill=DARK)
    d.polygon([(cx - half, y), (cx, y - th), (cx + half, y), (cx, y + th)], fill=(240, 192, 80))
    d.line([(cx - half * 0.9, y - 0.5), (cx + half * 0.9, y - 0.5)], fill=(255, 240, 190), width=1)
    gw, gh = size * 0.11, size * 0.18
    for side in (-1, 1):
        gem(layer, cx + side * gw * 3.6, y, gw * 0.5, gh * 0.5, look["gem"])
        dot = (cx + side * half, y)
        d.ellipse((dot[0] - th * 1.6, y - th * 1.6, dot[0] + th * 1.6, y + th * 1.6), fill=DARK)
        d.ellipse((dot[0] - th, y - th, dot[0] + th, y + th), fill=(240, 192, 80))
    gem(layer, cx, y, gw, gh, look["gem"])


def rule_y_for(name):
    """Poster y of the gold rule the element style draws under this name."""
    style = NAME_STYLES["element"]
    lines, font = layout_name(name, style)
    return round(baselines_for(lines, font, style["cap_center_y"])[-1] + font.size * RULE_GAP)


def draw_name_element(canvas, name, element, lift=0):
    style, look = NAME_STYLES["element"], ELEMENT_LOOK[element]
    lines, font = layout_name(name, style)
    size, cap = font.size, font.cap
    baselines = baselines_for(lines, font, style["cap_center_y"] - lift)
    top = int(baselines[0] - size * 1.1)
    region = (W, int(baselines[-1] + size * 0.75) - top)

    body = text_mask(region, lines, font, baselines, top)
    outline = text_mask(region, lines, font, baselines, top, stroke=max(2, round(size * 0.025)))
    rim = max(3.0, size * 0.04)
    inner = shrink(body, rim)

    layer = Image.new("RGBA", region, (0, 0, 0, 0))
    shadow = glow(outline, (0, 0, 0), 0.8, spread=0, blur=size * 0.03)
    layer.alpha_composite(shadow, (0, round(size * 0.03)))
    layer.alpha_composite(glow(outline, look["glow"], 0.85, spread=0, blur=size * 0.09, range_=0.6))
    layer.alpha_composite(glow(outline, look["glow"], 0.9, spread=0, blur=size * 0.025, range_=0.5))
    layer.paste(DARK, (0, 0), outline)

    bands = [(b - cap - top, b - top) for b in baselines]
    gold = gradient_column(region[1], bands, GOLD_STOPS).resize(region)
    layer.paste(bevel(gold, body, rim), (0, 0), body)

    tex = element_texture(look, region, size * 0.9)
    edge = ImageChops.invert(inner).filter(ImageFilter.GaussianBlur(rim)).point(lambda v: int(v * 0.7))
    tex = Image.composite(Image.new("RGB", region, (0, 0, 0)), tex, edge)
    layer.paste(tex, (0, 0), inner)

    # Glints: top left of the first letter, the middle of the longest line, top right of the last.
    def letter_x(line, k):
        left = W / 2 - font.getlength(line) / 2
        return left + font.getlength(line[:k]), font.getlength(line[k])
    first, last = lines[0], lines[-1]
    x0, w0 = letter_x(first, 0)
    sparkle(layer, x0 + w0 * 0.3, bands[0][0] + cap * 0.15, size * 0.3, look["glow"])
    longest = max(range(len(lines)), key=lambda i: font.getlength(lines[i]))
    xm, wm = letter_x(lines[longest], len(lines[longest]) // 2)
    sparkle(layer, xm + wm * 0.5, bands[longest][1] - cap * 0.6, size * 0.18, look["glow"])
    xl, wl = letter_x(last, len(last) - 1)
    sparkle(layer, xl + wl * 0.75, bands[-1][0] + cap * 0.3, size * 0.22, look["glow"])

    half = min(max(font.getlength(line) for line in lines) / 2 * 0.85, 520)
    rule_y = bands[-1][1] + size * RULE_GAP
    divider(layer, W / 2, rule_y, half, size, look)
    canvas.alpha_composite(layer, (0, top))
    return lines, size, round(top + rule_y), (layer, (0, top))


# --- Quote ------------------------------------------------------------------------------------

def quote_font(size):
    font = ImageFont.truetype(os.path.join(FONTS, QUOTE_FONT[0]), size)
    font.set_variation_by_name(QUOTE_FONT[1].encode())
    return font


def quote_stroke(font):
    return max(2, round(font.size / 22))


def tidy_quote(text):
    """Curly apostrophes and inner quotes, wrapped in curly quote marks (replacing any typed)."""
    text = text.strip().strip("\"“”").strip().replace("'", "’")
    parts = text.split('"')
    inner = "".join(p + "“”"[i % 2] for i, p in enumerate(parts[:-1])) + parts[-1]
    return f"“{inner}”"


def wrap_quote(text, n, font):
    """The best way to break text into n lines no wider than QUOTE_MAX_WIDTH, or None. Lines are
    kept even, but a break after punctuation is worth about seven characters of unevenness."""
    words = text.split()
    best = None
    for cuts in itertools.combinations(range(1, len(words)), n - 1):
        ends = (0,) + cuts + (len(words),)
        lines = [" ".join(words[i:j]) for i, j in zip(ends, ends[1:])]
        widest = max(font.getlength(line) for line in lines)
        if widest > QUOTE_MAX_WIDTH:
            continue
        score = widest - 3.5 * font.size * sum(line[-1] in ",;:.!?…—–" for line in lines[:-1])
        if best is None or score < best[0]:
            best = (score, lines)
    return best and best[1]


def quote_height(lines, font):
    """Ink height from the top of the first line to the bottom of the last, outline included."""
    stroke = quote_stroke(font)
    top = font.getbbox(lines[0], anchor="ls", stroke_width=stroke)[1]
    bottom = font.getbbox(lines[-1], anchor="ls", stroke_width=stroke)[3]
    return (len(lines) - 1) * font.size * QUOTE_LEADING + bottom - top


def layout_quote(text, room):
    """Choose the size and line breaks. room is the height free under the rule with the name where
    it is; anything taller lifts the name. Returns (lines, font, lift)."""
    best = None
    for size in QUOTE_SIZES:
        font = quote_font(size)
        lines = next(filter(None, (wrap_quote(text, n, font) for n in range(1, QUOTE_MAX_LINES + 1))), None)
        if lines is None:
            continue
        lift = max(0, math.ceil(quote_height(lines, font) - room))
        cost = QUOTE_SIZES[0] - size + lift * QUOTE_LIFT_COST
        if lift <= QUOTE_MAX_LIFT and (best is None or cost < best[0]):
            best = (cost, lines, font, lift)
    if best is None:
        fail(f"the quote is too long to fit under the name, even on {QUOTE_MAX_LINES} lines at "
             f"{QUOTE_SIZES[-1]}px; it needs shortening")
    return best[1:]


def draw_quote(canvas, lines, font, top, element):
    """Centred gold lines whose ink starts at y=top, with a dark outline, the element glow and a
    soft dark backing so they stay readable over busy foliage."""
    size, look, stroke = font.size, ELEMENT_LOOK[element], quote_stroke(font)
    first = top - font.getbbox(lines[0], anchor="ls", stroke_width=stroke)[1]
    baselines = [first + i * size * QUOTE_LEADING for i in range(len(lines))]
    pad = size * 2
    y0 = int(top) - pad
    region = (W, int(baselines[-1]) + pad - y0)
    body, outline = Image.new("L", region, 0), Image.new("L", region, 0)
    for mask, width in ((body, 0), (outline, stroke)):
        d = ImageDraw.Draw(mask)
        for line, base in zip(lines, baselines):
            d.text((W / 2, base - y0), line, font=font, anchor="ms", fill=255,
                   stroke_width=width, stroke_fill=255)

    layer = Image.new("RGBA", region, (0, 0, 0, 0))
    layer.alpha_composite(glow(outline, (0, 0, 0), 0.5, spread=0, blur=size * 0.6, range_=0.2))
    layer.alpha_composite(glow(outline, (0, 0, 0), 0.85, spread=0, blur=size * 0.06, range_=0.5),
                          (0, round(size * 0.06)))
    layer.alpha_composite(glow(outline, look["glow"], 0.75, spread=0, blur=size * 0.18, range_=0.5))
    layer.paste(DARK, (0, 0), outline)
    cap = cap_height(font)
    bands = [(b - cap - y0, b - y0) for b in baselines]
    layer.paste(gradient_column(region[1], bands, GOLD_STOPS).resize(region), (0, 0), body)
    canvas.alpha_composite(layer, (0, y0))


# --- Inspect ----------------------------------------------------------------------------------

def inspect(path, grid_path):
    """Print what matters for framing an avatar and write a gridded copy for reading coordinates."""
    avatar = Image.open(path).convert("RGBA")
    w, h = avatar.size
    alpha = avatar.getchannel("A")
    solid = alpha.point(lambda v: 255 if v >= SOLID_ALPHA else 0)
    box = solid.getbbox()
    clear = alpha.histogram()[0] / (w * h)
    print(f"{path}: {w}x{h}, {clear:.0%} fully transparent")
    if box is None or clear < 0.05:
        print("  warning: little or no transparency; this needs to be a cut-out PNG")
    if box is None:
        return
    print(f"  subject (alpha >= {SOLID_ALPHA}): x {box[0]}-{box[2]}, y {box[1]}-{box[3]}")
    cut = False
    for side, strip in [("top", solid.crop((0, 0, w, 1))), ("bottom", solid.crop((0, h - 1, w, h))),
                        ("left", solid.crop((0, 0, 1, h))), ("right", solid.crop((w - 1, 0, w, h)))]:
        span = strip.getbbox()
        if span:
            cut = True
            where = f"x {span[0]}-{span[2]}" if side in ("top", "bottom") else f"y {span[1]}-{span[3]}"
            print(f"  cut off at the {side} edge ({where}); keep that edge hidden on the poster")
    if not cut:
        print("  no cut-off edges")
    grey = avatar.convert("L")
    level = sum(i * n for i, n in enumerate(grey.histogram(solid))) / max(1, solid.histogram()[255])
    print(f"  subject brightness {level:.0f}/255" + ("  (dark render; --backlight can help)" if level < 50 else ""))
    scale, focus, at = auto_frame(avatar)
    print(f"  automatic framing would be: --scale {scale:.3f} --focus {focus[0]:.0f},{focus[1]:.0f} "
          f"--at {at[0]:.0f},{at[1]:.0f}")
    if grid_path:
        step = 100 if max(w, h) <= 2000 else 200
        sheet = Image.new("RGBA", (w, h), (120, 120, 120, 255))
        sheet.alpha_composite(avatar)
        over = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        d = ImageDraw.Draw(over)
        font = ImageFont.load_default(size=max(14, step // 4))
        label = dict(font=font, fill=(255, 255, 0, 255), stroke_width=2, stroke_fill=(0, 0, 0, 255))
        for x in range(0, w, step):
            d.line((x, 0, x, h), fill=(255, 40, 40, 120), width=3 if x % (step * 5) == 0 else 1)
            d.text((x + 3, 3), str(x), **label)
        for y in range(0, h, step):
            d.line((0, y, w, y), fill=(255, 40, 40, 120), width=3 if y % (step * 5) == 0 else 1)
            d.text((3, y + 3), str(y), **label)
        sheet.alpha_composite(over)
        sheet.convert("RGB").save(grid_path)
        print(f"  grid: {grid_path} (lines every {step}px, in avatar pixels)")


# --- Preview ----------------------------------------------------------------------------------

def save_preview(poster, path, element, at, lift=0):
    """Half-size copy with guides: title band, name zone, logo, foreground window, focus point."""
    over = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(over)
    d.rectangle((0, 0, W, TITLE_BAND), fill=(255, 0, 255, 45))
    d.rectangle((0, 2450 - lift, W, 2850), fill=(255, 255, 0, 35))
    d.rectangle(LOGO_BOX, fill=(0, 255, 255, 50))
    window = template(f"{element}_foreground_1440x3440.png").getchannel("A").point(lambda v: 255 if v < 60 else 0)
    edge = window.filter(ImageFilter.FIND_EDGES).filter(ImageFilter.MaxFilter(5))
    over.paste((255, 0, 255, 200), (0, 0), edge)
    x, y = at
    d.line((x - 40, y, x + 40, y), fill=(255, 0, 0, 255), width=5)
    d.line((x, y - 40, x, y + 40), fill=(255, 0, 0, 255), width=5)
    preview = poster.convert("RGBA")
    preview.alpha_composite(over)
    preview.convert("RGB").resize((W // 2, H // 2), Image.LANCZOS).save(path)


def main():
    p = argparse.ArgumentParser(description="Build a Club Elementum role poster.")
    p.add_argument("--avatar", required=True, help="cut-out avatar PNG with transparency")
    p.add_argument("--name")
    p.add_argument("--role", help="host, lead host, dj, dancer, dance captain, ...")
    p.add_argument("--element", help="water, fire, earth or wind")
    p.add_argument("--inspect", metavar="GRID_PNG", nargs="?", const="",
                   help="describe the avatar (and write a gridded copy to GRID_PNG) instead of a poster")
    p.add_argument("--out", help="output PNG (default Posters/AI/<role>-<name>.png)")
    p.add_argument("--scale", type=float, help="avatar scale factor (1 = original pixels)")
    p.add_argument("--focus", help="X,Y point on the avatar (avatar pixels) to place at --at")
    p.add_argument("--at", help="X,Y poster position for --focus (just X with --bottom-on-rule)")
    p.add_argument("--bottom-on-rule", action="store_true",
                   help="move the avatar up or down so its bottom edge sits on the gold rule under the name")
    p.add_argument("--backlight", action="store_true",
                   help="soft element-coloured light behind the avatar, for dark renders")
    p.add_argument("--display-name", help="the name as drawn, if it differs from --name")
    p.add_argument("--quote", help="a quote to show under the name (element name style)")
    p.add_argument("--name-style", choices=sorted(NAME_STYLES), default="element")
    p.add_argument("--name-color", help="classic style only: name colour as #RRGGBB")
    p.add_argument("--preview", help="also write a half-size preview with layout guides here")
    a = p.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if not os.path.exists(a.avatar):
        fail(f"avatar not found: {a.avatar}")
    if a.inspect is not None:
        inspect(a.avatar, a.inspect)
        return
    missing = [f"--{k}" for k in ("name", "role", "element") if not getattr(a, k)]
    if missing:
        fail(f"{', '.join(missing)} needed to make a poster")

    element = ELEMENTS.get(a.element.strip().lower())
    if element is None:
        fail(f"unknown element '{a.element}'. Use water, fire, earth or wind")
    title = find_title(a.role, element)
    color = None
    if a.name_color:
        m = re.fullmatch(r"#?([0-9a-fA-F]{6})", a.name_color.strip())
        if not m:
            fail("--name-color must look like #RRGGBB")
        color = tuple(int(m.group(1)[i:i + 2], 16) for i in (0, 2, 4))
    if a.focus and not a.at:
        fail("--focus needs --at (with --bottom-on-rule, --at can be just the x)")
    if a.at and not a.focus:
        fail("--at needs --focus")

    avatar = Image.open(a.avatar).convert("RGBA")
    scale, focus, at = auto_frame(avatar, a.scale)
    if a.focus:
        focus = point(a.focus, "--focus")
        if a.bottom_on_rule and re.fullmatch(r"\s*-?[\d.]+\s*", a.at):
            at = (float(a.at), 0.0)  # x only; y comes from the rule
        else:
            at = point(a.at, "--at")
    shown = drawable(a.display_name or a.name, NAME_STYLES[a.name_style]["font"])
    quote, lift = None, 0
    if a.quote:
        if a.name_style != "element":
            fail("--quote needs the element name style (it sits under the gold rule)")
        text = drawable(tidy_quote(a.quote), QUOTE_FONT[0], "quote", "reword the quote without it")
        quote_top = rule_y_for(shown) + layout_name(shown, NAME_STYLES["element"])[1].size * QUOTE_GAP
        lines, font, lift = layout_quote(text, QUOTE_FLOOR - quote_top)
        quote = (lines, font, quote_top - lift)
    if a.bottom_on_rule:
        if a.name_style != "element":
            fail("--bottom-on-rule needs the element name style (classic has no rule)")
        # Place the avatar's last row exactly on the rule.
        top = rule_y_for(shown) - lift - round(avatar.height * scale) + 1
        at = (at[0], top + focus[1] * scale)

    canvas = template(f"{element}_background_1440x3440.png")
    foreground = template(f"{element}_foreground_1440x3440.png")
    backlight = ELEMENT_LOOK[element]["glow"] if a.backlight else None
    placed = place_avatar(canvas, avatar, scale, focus, at, backlight)
    canvas.alpha_composite(foreground)
    if a.name_style == "classic":
        lines, size, rule_y, name_layer = draw_name_classic(canvas, shown, element, color)
    else:
        lines, size, rule_y, name_layer = draw_name_element(canvas, shown, element, lift)
    if quote:
        draw_quote(canvas, *quote, element)
    covers = [(foreground, (0, 0)), name_layer, place_title(canvas, title), place_logo(canvas)]
    cut_edges = visible_cut_edges(*placed, covers)

    out = a.out or os.path.join(OUT_DIR, f"{ROLE_FILE[role_stem(a.role)]}-{slug(a.name)}.png")
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    canvas.convert("RGB").save(out, optimize=True)
    print(f"Wrote {out} ({W}x{H})")
    print(f"  avatar: --scale {scale:.3f} --focus {focus[0]:.0f},{focus[1]:.0f} --at {at[0]:.0f},{at[1]:.0f}")
    if scale > SOFT_UPSCALE:
        print(f"  warning: the avatar is enlarged {scale:.1f}x and may look soft; "
              "a higher-resolution render would be sharper")
    for edge in cut_edges:
        print(f"  warning: the avatar's cut-off {edge} shows through the foreground; "
              "move or enlarge it so the edge is hidden")
    print(f"  name: {' / '.join(lines)} at {size:.0f}px ({a.name_style})"
          + (f", gold rule under it at y={rule_y} (a cut-off avatar edge placed there is hidden)"
             if rule_y else ""))
    if quote:
        print(f"  quote: {' / '.join(quote[0])} at {quote[1].size}px on {len(quote[0])} line(s)"
              + (f"; name raised {lift}px to make room" if lift else ""))
    print(f"  title: {os.path.relpath(title, REPO)}")
    if a.preview:
        save_preview(canvas, a.preview, element, at, lift)
        print(f"  preview: {a.preview}")


if __name__ == "__main__":
    main()
