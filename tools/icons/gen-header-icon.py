"""Header glyph for the Custom Profile Image window: the plugin's own ID-card icon (resources/customprofleimage-icon.png,
the 64 px launcher art — rounded card, person bust on the left, three text lines on the right), redrawn PER SIZE on the
pixel grid (house icon rule, Stellar-Design-Language.md section 3: never one master scaled down — thin strokes go soft
grey). White flat glyph on transparent; the accent tile behind it is drawn by the plugin (Plugin.Style.Blocks.cs IconBox).
All proportions are the 64 px original's, measured off its pixels (comments give the 64 px value), so the header tile and
the launcher read as the same icon. The 64 px launcher art itself is hand-made and NOT generated here.

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

# 16 / 18 px are HAND-HINTED: at those sizes the measured proportions round the second and third text lines into one
# block and push the shoulders into the card edge. Same motif, every element kept one clear pixel apart.
HAND = {
    16: (
        "................",
        "................",
        "................",
        "..############..",
        ".#............#.",
        ".#...##.......#.",
        ".#..#..#..###.#.",
        ".#...##.......#.",
        ".#........###.#.",
        ".#..####......#.",
        ".#.#....#.###.#.",
        ".#............#.",
        "..############..",
        "................",
        "................",
        "................",
    ),
    18: (
        "..................",
        "..................",
        "..................",
        "..##############..",
        ".#..............#.",
        ".#...##.........#.",
        ".#..#..#...###..#.",
        ".#..#..#........#.",
        ".#...##....###..#.",
        ".#..............#.",
        ".#..####........#.",
        ".#.#....#..####.#.",
        ".#.#....#.......#.",
        ".#..............#.",
        "..##############..",
        "..................",
        "..................",
        "..................",
    ),
}


def rnd(v: float) -> int:
    return math.floor(v + 0.5)


def draw(size: int) -> Image.Image:
    im = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    if size in HAND:
        for y, row in enumerate(HAND[size]):
            for x, c in enumerate(row):
                if c == "#":
                    im.putpixel((x, y), WHITE)
        return im
    d = ImageDraw.Draw(im)
    f = size / 64.0
    w = max(1, rnd(3 * f))                 # card / bust stroke: 3 px @64 → 1 px up to 24, 2 px at 32–48
    lw = max(1, rnd(2 * f))                # text-line thickness: 2 px @64 → 1 px up to 36, 2 px at 48

    # Card: x 3..60, y 11..52 @64 (a landscape ID card), corner radius ~6.
    left, right = rnd(3 * f), size - 1 - rnd(3 * f)
    top, bottom = rnd(11 * f), size - 1 - rnd(11 * f)
    d.rounded_rectangle((left, top, right, bottom), radius=max(1, rnd(6 * f)), outline=WHITE, width=w)

    # Head: outlined circle, centre (20.5, 25.8), outer radius 6.5 @64.
    cx, hy, hr = 20.5 * f, 25.8 * f, max(6.5 * f, w + 0.75)
    d.ellipse((rnd(cx - hr), rnd(hy - hr), rnd(cx + hr) - 1, rnd(hy + hr) - 1), outline=WHITE, width=w)

    # Shoulders: upper half of an ellipse, top y 34, ends at y 43.5, half-width 11.5 @64 (open at the bottom).
    sw, st, sb = 11.5 * f, rnd(34 * f), rnd(43.5 * f)
    d.arc((rnd(cx - sw), st, rnd(cx + sw) - 1, st + 2 * (sb - st)), start=180, end=360, fill=WHITE, width=w)

    # Text lines: x 39..50 (short) / 39..54 (long), centred on y 24.5 / 32.5 / 39.5 @64.
    x0 = rnd(39 * f)
    for yc, x1 in ((24.5, 50), (32.5, 50), (39.5, 54)):
        y0 = rnd(yc * f - lw / 2)
        d.rectangle((x0, y0, rnd(x1 * f), y0 + lw - 1), fill=WHITE)
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
