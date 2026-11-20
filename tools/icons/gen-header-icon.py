"""Header glyph for the Custom Profile Image window: a portrait card (rounded frame + head + shoulders), the approved
prototype's icon, redrawn PER SIZE on the pixel grid (house icon rule, Stellar-Design-Language.md section 3: never one
master scaled down — thin strokes go soft grey). White flat glyph on transparent; the accent tile behind it is drawn by
the plugin (Plugin.Style.Blocks.cs IconBox).

PIL's ImageDraw draws WITHOUT anti-aliasing, so every stroke lands on whole pixels. Coordinate maths uses floor(v + 0.5),
NOT round(): Python's round() is banker's rounding and snaps neighbouring sizes inconsistently (KB
Stellar-Design-Language.md, icon generator gotcha).

Usage:  python -I gen-header-icon.py <out_dir> [--contact <sheet.png>]
"""
import math
import sys

from PIL import Image, ImageDraw

SIZES = (16, 18, 24, 32, 36, 48)   # header glyph = 24 canvas units: 18 @1080p, 24 @1440p, 36 @4K (+ spares)
WHITE = (255, 255, 255, 255)


def rnd(v: float) -> int:
    return math.floor(v + 0.5)


def draw(size: int) -> Image.Image:
    im = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    w = max(2, rnd(size / 12))             # stroke: 2 px up to 24, 3 px at 32/36, 4 px at 48
    m = rnd(size * 0.125)                  # frame margin (the prototype's 3/24)
    r = max(2, rnd(size * 0.125))          # frame corner radius
    d.rounded_rectangle((m, m, size - 1 - m, size - 1 - m), radius=r, outline=WHITE, width=w)

    # Head: an outlined circle centred on the vertical axis, upper half of the frame.
    cx = (size - 1) / 2
    hr = size * 0.135 + w / 2              # outer radius
    hy = size * 0.42
    d.ellipse((rnd(cx - hr), rnd(hy - hr), rnd(cx + hr), rnd(hy + hr)), outline=WHITE, width=w)

    # Shoulders: the upper half of an ellipse whose base sits on the frame's inner bottom edge.
    sw = size * 0.27                       # half-width of the shoulders
    top = rnd(size * 0.63)
    bottom = size - 1 - m                  # clipped by the frame stroke itself
    d.arc((rnd(cx - sw), top, rnd(cx + sw), top + 2 * (bottom - top)), start=180, end=360, fill=WHITE, width=w)
    return im


def contact(images, path: str) -> None:
    # ×4 nearest-neighbour review sheet on the default teal accent, so the pixel grid is visible.
    zoom, pad = 4, 8
    width = sum(s * zoom + pad for s in SIZES) + pad
    height = max(SIZES) * zoom + 2 * pad
    sheet = Image.new("RGBA", (width, height), (35, 41, 47, 255))
    x = pad
    for s, im in zip(SIZES, images):
        tile = Image.new("RGBA", (s, s), (95, 184, 196, 255))
        tile.alpha_composite(im)
        sheet.paste(tile.resize((s * zoom, s * zoom), Image.NEAREST), (x, pad))
        x += s * zoom + pad
    sheet.save(path)


def main() -> None:
    out = sys.argv[1]
    images = []
    for s in SIZES:
        im = draw(s)
        im.save(f"{out}/customprofleimage-{s}.png")
        images.append(im)
    if "--contact" in sys.argv:
        contact(images, sys.argv[sys.argv.index("--contact") + 1])


if __name__ == "__main__":
    main()
