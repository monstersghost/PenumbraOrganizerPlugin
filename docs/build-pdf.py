"""Render docs/USER_GUIDE.md to docs/USER_GUIDE.pdf.

The PDF is generated from the Markdown rather than maintained separately, so the two cannot
drift. Re-run it whenever the guide changes.

Needs: pip install markdown, and wkhtmltopdf on PATH (or set WKHTMLTOPDF).
Run with plain `python`, not `python -I`: both are user site-packages.

Usage: python docs/build-pdf.py
"""
import os
import re
import shutil
import subprocess
import sys

import markdown

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "USER_GUIDE.md")
HTML = os.path.join(HERE, "USER_GUIDE.rendered.html")
PDF = os.path.join(HERE, "USER_GUIDE.pdf")

# Print styling. Kept deliberately plain: this is a reference people search and print, not a
# brochure. Dark UI screenshots sit on white, so they need a border to not float.
CSS = """
@page { size: A4; margin: 18mm 16mm; }
body {
  font-family: "Segoe UI", "Noto Sans", system-ui, sans-serif;
  font-size: 10.5pt; line-height: 1.5; color: #1b1d24; max-width: 100%;
}
h1 { font-size: 23pt; margin: 0 0 2mm; }
h1 + p { color: #555c6b; }
h2 { font-size: 15pt; margin: 9mm 0 2mm; padding-bottom: 1.5mm; border-bottom: 1.5px solid #c9cedb; }
h3 { font-size: 13pt; margin: 7mm 0 2mm; color: #2f3545; page-break-after: avoid; }
h4 { font-size: 11pt; margin: 5mm 0 1.5mm; color: #434a5c; page-break-after: avoid; }
p, li { orphans: 3; widows: 3; }
code { font-family: Consolas, "Cascadia Mono", monospace; font-size: 9.3pt;
       background: #f1f2f6; padding: 0.3mm 1mm; border-radius: 2px; }
pre { background: #f1f2f6; padding: 3mm; border-radius: 3px; overflow: hidden; }
pre code { background: none; }
a { color: #3b41b5; text-decoration: none; }
img { max-width: 100%; border: 1px solid #c9cedb; border-radius: 3px;
      margin: 3mm 0; page-break-inside: avoid; }
table { border-collapse: collapse; margin: 3mm 0; width: 100%;
        page-break-inside: avoid; font-size: 10pt; }
th, td { border: 1px solid #c9cedb; padding: 1.6mm 2.5mm; text-align: left; vertical-align: top; }
th { background: #eef0f6; }
blockquote { margin: 3mm 0; padding: 0 0 0 4mm; border-left: 3px solid #c9cedb; color: #555c6b; }
hr { border: none; border-top: 1px solid #c9cedb; margin: 6mm 0; }
"""

PAGE = """<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<title>Penumbra Organizer - User Guide</title>
<style>{css}</style></head>
<body>{body}</body></html>
"""


def main():
    text = open(SRC, encoding="utf-8").read()

    # Markdown links between repo documents are useless in a PDF: a reader has no checkout. Keep
    # the link text, drop the target, except for real URLs which stay clickable.
    text = re.sub(r"\[([^\]]+)\]\((?!https?://)[^)]+\.md(?:#[^)]*)?\)", r"\1", text)

    body = markdown.markdown(text, extensions=["tables", "fenced_code", "sane_lists", "attr_list"])

    with open(HTML, "w", encoding="utf-8") as handle:
        handle.write(PAGE.format(css=CSS, body=body))

    binary = os.environ.get("WKHTMLTOPDF") or shutil.which("wkhtmltopdf")
    if not binary:
        print("wkhtmltopdf not found; wrote %s only" % HTML)
        return 1

    # --enable-local-file-access: without it wkhtmltopdf silently drops every <img>, and the PDF
    # comes out with captions and no screenshots.
    result = subprocess.run([
        binary,
        "--enable-local-file-access",
        "--encoding", "utf-8",
        "--footer-center", "[page] / [topage]",
        "--footer-font-size", "8",
        "--footer-spacing", "5",
        "--print-media-type",
        "--quiet",
        HTML, PDF,
    ])
    if result.returncode != 0:
        return result.returncode

    os.remove(HTML)
    print("%s  %d KB" % (PDF, os.path.getsize(PDF) // 1024))
    return 0


if __name__ == "__main__":
    sys.exit(main())
