"""Check-mark glyph for the Custom Profile Image window (HOW TO done badges + the matching size chip). Replaces the "✓"
character: U+2713 is NOT in the game's bold TMP atlas (it got a quad but no ink — blank badge / blank chip mark, bridge
ui.inspect 2026-11-20), so the mark is a picture. White flat glyph on transparent: the badge draws it untinted on a solid
status-green bubble, the chip tints it (ImageElement.Tint) to its live label green.

Redrawn PER SIZE on the pixel grid (house icon rule: never one master scaled down). Both legs run at exactly 45°, so a
non-anti-aliased stroke is a clean pixel staircase at every size; the long leg is twice the short one. PIL draws without
AA; coordinates use floor(v + 0.5), never banker's round().

Usage:  python -I gen-check-icon.py <out_dir> [--contact <sheet.png>]
"""
import math
import sys

from PIL import Image, ImageDraw

SIZES = (6, 8, 9, 12, 15, 18, 24)   # check = 12 canvas units: 9 @1080p, 12 @1440p, 18 @4K, 8 @900p, 6 @720p, 15 @1800p
WHITE = (255, 255, 255, 255)


def rnd(v: float) -> int:
    return math.floor(v + 0.5)


def draw(size: int) -> Image.Image:
    im = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    w = max(1, rnd(size / 6))                  # stroke: 1 px ≤ 8, 2 px at 9–12, 3 px at 15–18, 4 px at 24
    m = (w - 1) / 2 + (0.5 if size >= 12 else 0)   # keep the thick stroke's corners inside the canvas
    a = math.floor((size - 1 - 2 * m) / 3)      # short leg; long leg = 2a; total width 3a
    x0 = rnd((size - 1 - 3 * a) / 2)            # centre horizontally
    yv = rnd((size - 1 + 2 * a) / 2)            # vertex row: the 2a-tall mark centred vertically
    pts = [(x0, yv - a), (x0 + a, yv), (x0 + 3 * a, yv - 2 * a)]
    if w == 1:
        d.line(pts, fill=WHITE, width=1)
    else:
        # Thick 45° stroke = the 1 px staircase stamped w times straight down: every row keeps w solid pixels and the
        # vertex closes (PIL's wide line() leaves notches on diagonals).
        for k in range(w):
            d.line([(x, y + k - (w - 1) // 2) for x, y in pts], fill=WHITE, width=1)
    return im


def contact(images, path: str) -> None:
    # ×6 review sheet: each size on a solid status-green badge (#5fd38d), as the HOW TO step draws it.
    zoom, pad = 6, 8
    width = sum(s * zoom + pad for s in SIZES) + pad
    height = max(SIZES) * zoom + 2 * pad
    sheet = Image.new("RGBA", (width, height), (35, 41, 47, 255))
    x = pad
    for s, im in zip(SIZES, images):
        tile = Image.new("RGBA", (s, s), (95, 211, 141, 255))
        tile.alpha_composite(im)
        sheet.paste(tile.resize((s * zoom, s * zoom), Image.NEAREST), (x, pad))
        x += s * zoom + pad
    sheet.save(path)


def main() -> None:
    out = sys.argv[1]
    images = []
    for s in SIZES:
        im = draw(s)
        im.save(f"{out}/check-{s}.png")
        images.append(im)
    if "--contact" in sys.argv:
        contact(images, sys.argv[sys.argv.index("--contact") + 1])


if __name__ == "__main__":
    main()
