"""Build the JPEG set for the XIV Mod Archive listing from docs/images/*.png.

XMA stores gallery images as JPEG and shows them in upload order with no captions, so the file
names here are numbered to fix that order. Run this after retaking any screenshot.

Usage: python docs/build-xma-images.py
Output: docs/xma-images/
"""
import os

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "images")
OUT = os.path.join(HERE, "xma-images")

# Order matters: the first is the listing's preview image, and the rest run left to right in the
# gallery. Lead with what the plugin is for, then the thing people are being told about.
ORDER = [
    ("protect-tree", "01-preview-protect-tree"),
    ("sort", "02-sort"),
    ("review-changes", "03-review-changes"),
    ("protect-search", "04-protect-search"),
    ("protect-unprotect-confirm", "05-unprotect-confirm"),
    ("history", "06-history"),
    ("search", "07-search"),
    ("templates", "08-templates"),
    ("scan", "09-scan"),
]

# The screenshots are 800px wide. Upscaling 2x keeps the small UI text readable on XMA, which
# renders the gallery larger than the source; LANCZOS keeps the edges clean rather than soft.
SCALE = 2
QUALITY = 92


def main():
    os.makedirs(OUT, exist_ok=True)
    for stem, name in ORDER:
        src = os.path.join(SRC, stem + ".png")
        if not os.path.exists(src):
            print("MISSING", src)
            continue
        image = Image.open(src).convert("RGB")
        image = image.resize((image.width * SCALE, image.height * SCALE), Image.LANCZOS)
        dest = os.path.join(OUT, name + ".jpg")
        image.save(dest, "JPEG", quality=QUALITY, optimize=True)
        print("%-34s %dx%d  %d KB" % (
            os.path.basename(dest), image.width, image.height,
            os.path.getsize(dest) // 1024))


if __name__ == "__main__":
    main()
