# Makes the logo's dark badge transparent, so the logo sits on light or dark themes alike.
#  1. The thin light rim around the badge's outline goes.
#  2. Every badge-coloured pixel (inside letters and the tower's lattice too) becomes
#     transparent; edge pixels fade, with the badge colour taken out of them.
#  3. The camera lens keeps its dark centre (it's part of the drawing, not the badge).
import sys
from collections import deque
from PIL import Image
src, out = sys.argv[1], sys.argv[2]
im = Image.open(src).convert("RGBA"); w, h = im.size; px = im.load()
BG = (28, 34, 52); D0, D1 = 24.0, 90.0
LENS = (275, 248, 62)          # centre x, y and radius of the lens and its dark housing
TAGLINE_TOP = 332              # the grey "IMAGES OVER THE AIR" starts here
def dist(p): return ((p[0]-BG[0])**2 + (p[1]-BG[1])**2 + (p[2]-BG[2])**2) ** 0.5
# distance (in pixels, up to 8) from the transparent outside
far = [99] * (w * h); q = deque()
for y in range(h):
    for x in range(w):
        if px[x, y][3] < 128: far[y*w+x] = 0; q.append((x, y))
while q:
    x, y = q.popleft(); d = far[y*w+x]
    if d >= 8: continue
    for nx, ny in ((x+1,y),(x-1,y),(x,y+1),(x,y-1)):
        if 0 <= nx < w and 0 <= ny < h and far[ny*w+nx] > d + 1:
            far[ny*w+nx] = d + 1; q.append((nx, ny))
for y in range(h):
    for x in range(w):
        r, g, b, a = px[x, y]
        if a == 0: continue
        sat = max(r, g, b) - min(r, g, b)
        if far[y*w+x] <= 7 and sat < 45 and y < TAGLINE_TOP:     # 1. the rim
            px[x, y] = (0, 0, 0, 0); continue
        lx, ly, lr = LENS
        rr = ((x-lx)**2 + (y-ly)**2) ** 0.5
        keep = max(0.0, min(1.0, lr + 0.5 - rr))                  # 3. the lens stays (soft edge)
        if keep >= 1: continue
        d = dist((r, g, b))
        if d >= D1: continue
        alpha = 0.0 if d <= D0 else (d - D0) / (D1 - D0)          # 2. the badge goes
        if alpha <= 0.02: c, alpha = [r, g, b], 0.0
        else: c = [max(0, min(255, round((v - (1 - alpha) * bv) / alpha))) for v, bv in zip((r, g, b), BG)]
        if keep > 0:   # on the lens's edge: part original, part cleared
            out_a = keep + (1 - keep) * alpha
            c = [round((keep * v0 + (1 - keep) * alpha * v1) / out_a) for v0, v1 in zip((r, g, b), c)]
            alpha = out_a
        px[x, y] = (c[0], c[1], c[2], round(a * alpha)) if alpha > 0 else (0, 0, 0, 0)
im.save(out)
