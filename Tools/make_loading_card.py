"""Renders the default "Loading posters" card shown on poster materials before the
first poster downloads.

    python Tools/make_loading_card.py

Needs Pillow (pip install pillow). Output: Assets/Textures/PosterLoading.png (857 x 2048,
the same size as served posters).
"""
import os

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

W, H = 857, 2048
REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
TEMPLATES = os.path.join(REPO, "Posters", "templates")
OUT = os.path.join(REPO, "Assets", "Textures", "PosterLoading.png")
FONT = r"C:\Windows\Fonts\segoeuib.ttf"

GOLD = (245, 213, 138)
BACKGROUNDS = ["water_background_1440x3440.png", "fire_background_1440x3440.png",
               "earth_background_1440x3440.png", "wind_background_1440x3440.png"]


def backdrop():
    """Four element backgrounds in vertical bands, softly blended, blurred and darkened."""
    layers = [Image.open(os.path.join(TEMPLATES, n)).convert("RGB").resize((W, H), Image.LANCZOS)
              for n in BACKGROUNDS]
    out = layers[0]
    band = H / len(layers)
    for i, layer in enumerate(layers[1:], start=1):
        # Vertical gradient mask: layer i fades in across the boundary with the previous band.
        mask = Image.new("L", (1, H))
        start, end = band * i - band * 0.35, band * i + band * 0.35
        for y in range(H):
            t = min(1.0, max(0.0, (y - start) / (end - start)))
            mask.putpixel((0, y), int(255 * t))
        out = Image.composite(layer, out, mask.resize((W, H)))
    out = out.filter(ImageFilter.GaussianBlur(10))
    dark = Image.new("RGB", (W, H), (8, 10, 18))
    out = Image.blend(out, dark, 0.55)

    # Vignette to pull focus to the centre.
    vig = Image.new("L", (W, H), 0)
    ImageDraw.Draw(vig).ellipse((-W * 0.45, H * 0.05, W * 1.45, H * 0.95), fill=255)
    vig = vig.filter(ImageFilter.GaussianBlur(160))
    return Image.composite(out, Image.blend(out, dark, 0.7), vig).convert("RGBA")


def glow_text(img, text, font, cy, fill, glow=(0, 0, 0), spacing=0):
    draw = ImageDraw.Draw(img)
    # Measure with letter spacing.
    widths = [draw.textbbox((0, 0), ch, font=font)[2] for ch in text]
    total = sum(widths) + spacing * (len(text) - 1)
    x = (W - total) / 2
    top = draw.textbbox((0, 0), text, font=font)
    y = cy - (top[3] + top[1]) / 2

    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    shadow = Image.new("RGBA", img.size, (0, 0, 0, 0))
    ld, sd = ImageDraw.Draw(layer), ImageDraw.Draw(shadow)
    cx = x
    for ch, w in zip(text, widths):
        ld.text((cx, y), ch, font=font, fill=fill)
        sd.text((cx, y), ch, font=font, fill=glow + (230,))
        cx += w + spacing
    shadow = shadow.filter(ImageFilter.GaussianBlur(10))
    img.alpha_composite(shadow)
    img.alpha_composite(shadow)
    img.alpha_composite(layer)


def main():
    img = backdrop()

    logo = Image.open(os.path.join(TEMPLATES, "Logo.png")).convert("RGBA")
    logo = logo.crop(logo.getbbox())
    lw = int(W * 0.9)
    logo = logo.resize((lw, int(logo.height * lw / logo.width)), Image.LANCZOS)
    ly = int(H * 0.40 - logo.height / 2)
    # Soft dark halo behind the logo so it separates from the backdrop.
    halo = Image.new("RGBA", img.size, (0, 0, 0, 0))
    alpha = logo.split()[3].point(lambda a: 170 if a > 20 else 0)
    halo.paste(Image.new("RGBA", logo.size, (0, 0, 0, 255)), ((W - lw) // 2, ly), alpha)
    img.alpha_composite(halo.filter(ImageFilter.GaussianBlur(28)))
    img.alpha_composite(logo, ((W - lw) // 2, ly))

    title = ImageFont.truetype(FONT, 66)
    glow_text(img, "LOADING", title, H * 0.66, GOLD, spacing=14)
    glow_text(img, "POSTERS", title, H * 0.66 + 88, GOLD, spacing=14)

    # Three dots as a "please wait" cue.
    d = ImageDraw.Draw(img)
    r, gap, dy = 9, 38, H * 0.66 + 190
    for i in (-1, 0, 1):
        cx = W / 2 + i * gap
        d.ellipse((cx - r, dy - r, cx + r, dy + r), fill=GOLD + (255 - 70 * (i + 1),))

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    img.convert("RGB").save(OUT, optimize=True)
    print(f"Wrote {OUT} ({W}x{H})")


if __name__ == "__main__":
    main()
