"""Trim the page background from a mock screenshot and save it as a PNG.

The screenshot captures the whole page, so the mock window sits in a field of empty background.
This crops to the window plus a small margin.

Usage: python docs/ui-mock/crop.py <screenshot.jpg> docs/images/<name>.png
"""
import sys

from PIL import Image

# body background in index.html; anything within TOLERANCE of it counts as empty page.
PAGE = (16, 18, 26)
TOLERANCE = 12
MARGIN = 11
# Sampling every Nth pixel is plenty to tell an empty row from one with UI in it, and keeps this
# fast on a large capture.
STEP = 7


def _is_page(pixel):
    return all(abs(a - b) <= TOLERANCE for a, b in zip(pixel[:3], PAGE))


def main(src, dest):
    image = Image.open(src).convert("RGB")
    width, height = image.size
    pixels = image.load()

    bottom = height
    for y in range(height - 1, -1, -1):
        if not all(_is_page(pixels[x, y]) for x in range(0, width, STEP)):
            bottom = min(height, y + MARGIN)
            break

    right = width
    for x in range(width - 1, -1, -1):
        if not all(_is_page(pixels[x, y]) for y in range(0, bottom, STEP)):
            right = min(width, x + MARGIN)
            break

    image.crop((0, 0, right, bottom)).save(dest)
    print("%s  %dx%d" % (dest, right, bottom))


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    main(sys.argv[1], sys.argv[2])
