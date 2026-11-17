import os
from PIL import Image, ImageDraw, ImageFilter

S = 1024  # supersample canvas
UNIT = S / 256.0

def u(v):
    return v * UNIT

# ---------- background ----------
def lerp(c1, c2, t):
    return tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(3))

bg = Image.new("RGBA", (S, S), (0, 0, 0, 0))
top = (26, 32, 48)
bot = (10, 12, 18)
grad = Image.new("RGBA", (S, S))
gd = ImageDraw.Draw(grad)
for y in range(S):
    t = y / (S - 1.0)
    gd.line([(0, y), (S, y)], fill=lerp(top, bot, t) + (255,))

# rounded-rect mask (full bleed, radius 56)
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=u(56), fill=255)
bg.paste(grad, (0, 0), mask)

# ---------- glow behind the lens ----------
glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
gdg = ImageDraw.Draw(glow)
cx, cy, r = u(108), u(104), u(110)
gdg.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(76, 194, 255, 90))
glow = glow.filter(ImageFilter.GaussianBlur(radius=u(52)))
bg = Image.alpha_composite(bg, Image.composite(glow, Image.new("RGBA", (S, S), (0, 0, 0, 0)), mask))

# ---------- magnifier geometry ----------
LENS = (108.0, 104.0)
LR = 54.0
RING = 22.0
HANDLE_W = 28.0
HANDLE_END = (204.0, 200.0)

shape = Image.new("L", (S, S), 0)
sd = ImageDraw.Draw(shape)

# handle: line from lens edge toward end + round cap
import math
dx = HANDLE_END[0] - LENS[0]
dy = HANDLE_END[1] - LENS[1]
dist = math.hypot(dx, dy)
sx = LENS[0] + dx / dist * (LR + RING / 2 - 4)
sy = LENS[1] + dy / dist * (LR + RING / 2 - 4)
sd.line([(u(sx), u(sy)), (u(HANDLE_END[0]), u(HANDLE_END[1]))], fill=255, width=int(u(HANDLE_W)))
sd.ellipse([u(HANDLE_END[0]) - u(HANDLE_W) / 2, u(HANDLE_END[1]) - u(HANDLE_W) / 2,
            u(HANDLE_END[0]) + u(HANDLE_W) / 2, u(HANDLE_END[1]) + u(HANDLE_W) / 2], fill=255)

# ring
sd.ellipse([u(LENS[0] - LR - RING / 2), u(LENS[1] - LR - RING / 2),
            u(LENS[0] + LR + RING / 2), u(LENS[1] + LR + RING / 2)], fill=255)
sd.ellipse([u(LENS[0] - LR + RING / 2), u(LENS[1] - LR + RING / 2),
            u(LENS[0] + LR - RING / 2), u(LENS[1] + LR - RING / 2)], fill=0)

# ---------- blue gradient through the shape ----------
gres = Image.new("RGBA", (S, S))
gr = ImageDraw.Draw(gres)
ctop = (150, 226, 255)
cbot = (40, 140, 216)
for y in range(S):
    t = y / (S - 1.0)
    gr.line([(0, y), (S, y)], fill=lerp(ctop, cbot, t) + (255,))
bg = Image.alpha_composite(bg, Image.composite(gres, Image.new("RGBA", (S, S), (0, 0, 0, 0)), shape))

# ---------- subtle inner light inside the lens ----------
lens_mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(lens_mask).ellipse(
    [u(LENS[0] - LR + RING / 2), u(LENS[1] - LR + RING / 2),
     u(LENS[0] + LR - RING / 2), u(LENS[1] + LR - RING / 2)], fill=255)
inner = Image.new("RGBA", (S, S), (0, 0, 0, 0))
idr = ImageDraw.Draw(inner)
ir = u(46)
idr.ellipse([u(LENS[0]) - ir, u(LENS[1]) - ir - u(6), u(LENS[0]) + ir, u(LENS[1]) + ir - u(6)],
            fill=(70, 150, 200, 70))
inner = inner.filter(ImageFilter.GaussianBlur(radius=u(10)))
bg = Image.alpha_composite(bg, Image.composite(inner, Image.new("RGBA", (S, S), (0, 0, 0, 0)), lens_mask))

# ---------- export ----------
sizes = [256, 128, 64, 48, 40, 32, 24, 20, 16]
here = os.path.dirname(os.path.abspath(__file__))
assets = os.path.normpath(os.path.join(here, "..", "..", "src", "SpotlightSearch", "Assets"))
os.makedirs(assets, exist_ok=True)
frames = []
for s in sizes:
    frames.append(bg.resize((s, s), Image.LANCZOS))
ico = os.path.join(assets, "app.ico")
frames[0].save(ico, format="ICO", sizes=[(s, s) for s in sizes])
tmp = os.environ.get("TEMP", here)
frames[0].save(os.path.join(tmp, "icon_preview256.png"))
frames[6].resize((128, 128), Image.NEAREST).save(os.path.join(tmp, "icon_preview32_x4.png"))
print("wrote", ico)
