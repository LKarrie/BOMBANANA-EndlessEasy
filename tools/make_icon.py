"""Generates the Thunderstore icon.png (256x256) for Endless Easy.

Motif: bomb + clock face  ->  "more time on the bomb".
Run:  python tools/make_icon.py

Writes icon.png next to the repository root, derived from this file's location, so the
script keeps working wherever the repository is cloned or moved.
"""
import os

from PIL import Image, ImageDraw

SIZE = 256
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "icon.png")

BG = (27, 34, 51, 255)
ACCENT = (80, 200, 140, 255)
BODY = (38, 44, 60, 255)
LINE = (226, 232, 240, 255)
HILITE = (118, 130, 156, 255)
SPARK = (255, 196, 80, 255)

img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Rounded background
d.rounded_rectangle([0, 0, SIZE - 1, SIZE - 1], radius=48, fill=BG)

# Green ring = "easier"
d.ellipse([22, 22, SIZE - 22, SIZE - 22], outline=ACCENT, width=6)

# Fuse
d.line([(128, 108), (142, 74), (162, 58)], fill=LINE, width=8, joint="curve")
# Spark
d.ellipse([152, 40, 176, 64], fill=SPARK)

# Bomb body
d.ellipse([72, 104, 184, 216], fill=BODY, outline=LINE, width=4)
# Highlight
d.ellipse([94, 126, 118, 150], fill=HILITE)

# Clock face on the bomb
d.ellipse([100, 132, 156, 188], outline=LINE, width=3)
# Clock hands (pointing past the hour, "extra time")
d.line([(128, 160), (128, 140)], fill=LINE, width=6)
d.line([(128, 160), (146, 172)], fill=ACCENT, width=6)
d.ellipse([124, 156, 132, 164], fill=LINE)

img.save(OUT, "PNG")
print(f"wrote {OUT} {img.size[0]}x{img.size[1]} mode={img.mode}")
