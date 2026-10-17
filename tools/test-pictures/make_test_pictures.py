# make_test_pictures.py - draws the pictures RFpix's test transmissions send.
#
# Each picture states its own settings, so anyone receiving it (in RFpix, QSSTV or
# EasyPal) can see what was sent. The colour bars and fine stripes make damage easy to spot.
# Run:  python3 make_test_pictures.py <output folder>     (needs Pillow and the DejaVu fonts)
import sys
from PIL import Image, ImageDraw, ImageFont

TESTS = [  # number, mode, bandwidth, QAM, interleave, protection, Reed-Solomon (0 = none), background
    (1, "B", "2.5", 4, "long", "high", 0, (20, 70, 140)),
    (2, "B", "2.5", 16, "long", "high", 0, (110, 30, 120)),
    (3, "B", "2.5", 16, "long", "low", 0, (140, 60, 20)),
    (4, "B", "2.5", 64, "long", "high", 0, (20, 110, 70)),
    (5, "A", "2.5", 16, "long", "high", 0, (120, 20, 40)),
    (6, "E", "2.5", 16, "long", "high", 0, (30, 100, 120)),
    (7, "B", "2.2", 16, "long", "high", 0, (90, 90, 30)),
    (8, "B", "2.5", 16, "short", "high", 0, (60, 60, 60)),
    (9, "B", "2.5", 4, "long", "high", 3, (25, 95, 160)),
    (10, "B", "2.5", 16, "long", "high", 2, (130, 45, 140)),
]
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
FONT_REG = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"

def draw(test, folder):
    n, mode, bw, qam, il, prot, rs, bg = test
    im = Image.new("RGB", (320, 240), bg)
    d = ImageDraw.Draw(im)
    big, mid, small = ImageFont.truetype(FONT, 30), ImageFont.truetype(FONT, 15), ImageFont.truetype(FONT_REG, 12)
    def centre(y, text, font):
        w = d.textlength(text, font=font)
        d.text(((320 - w) / 2, y), text, font=font, fill="white")
    centre(10, f"RFpix TEST {n}", big)
    centre(52, f"Mode {mode}  ·  {bw} kHz  ·  {qam}-QAM", mid)
    centre(76, f"{il}  ·  {prot} protection  ·  RS{rs}" if rs else f"{il} interleave  ·  {prot} protection", mid)
    colours = [(255, 255, 255), (255, 220, 0), (0, 220, 220), (0, 200, 0), (220, 0, 220), (230, 0, 0), (0, 0, 230), (0, 0, 0)]
    for i, c in enumerate(colours):
        d.rectangle([20 + i * 35, 108, 20 + i * 35 + 34, 168], fill=c)
    for x in range(20, 300, 8):   # fine stripes: smear easily if the picture is damaged
        d.rectangle([x, 176, x + 3, 196], fill="white")
    centre(212, "HamDRM test transmission  ·  rfpix", small)
    im.save(f"{folder}/rfpix-test-{n}.jpg", quality=35, optimize=True)

for t in TESTS:
    draw(t, sys.argv[1])
