# Draws Flair's app icon, logo and tray icons into src/Flair/Assets.
#   python tools/make-icons.py
import math
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "Flair" / "Assets"
SS = 4  # supersampling

WHITE = (244, 244, 244, 255)
INK = (10, 10, 10, 255)
GREEN = (35, 165, 90, 255)   # Discord's online green, the one color in the tray
AMBER = (240, 178, 50, 255)


def sparkle(size, cx, cy, r, k=3.2):
    """A four-pointed sparkle: points on the axes, concave sides (an astroid, k sets how thin the points are)."""
    pts = []
    for i in range(256):
        t = i / 256 * 2 * math.pi
        c, s = math.cos(t), math.sin(t)
        pts.append((cx + math.copysign(abs(c) ** k, c) * r, cy + math.copysign(abs(s) ** k, s) * r))
    return pts


def app_icon(px):
    S = px * SS
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = S * 0.04
    d.rounded_rectangle([pad, pad, S - pad, S - pad], radius=S * 0.24, fill=WHITE)
    d.polygon(sparkle(S, S * 0.47, S * 0.53, S * 0.36), fill=INK)
    d.polygon(sparkle(S, S * 0.75, S * 0.25, S * 0.1), fill=INK)
    return img.resize((px, px), Image.LANCZOS)


def discord_icon(px):
    """Full square for the Discord Developer Portal, which rounds the corners itself."""
    S = px * SS
    img = Image.new("RGBA", (S, S), WHITE)
    d = ImageDraw.Draw(img)
    d.polygon(sparkle(S, S * 0.47, S * 0.53, S * 0.38), fill=INK)
    d.polygon(sparkle(S, S * 0.76, S * 0.24, S * 0.11), fill=INK)
    return img.resize((px, px), Image.LANCZOS).convert("RGB")


def discord_cover(w, h):
    """The wide invite cover on the portal's Rich Presence page (16:9)."""
    W, H = w * SS, h * SS
    img = Image.new("RGBA", (W, H), WHITE)
    d = ImageDraw.Draw(img)
    d.polygon(sparkle(W, W * 0.47, H * 0.53, H * 0.38), fill=INK)
    d.polygon(sparkle(W, W * 0.47 + H * 0.29, H * 0.24, H * 0.11), fill=INK)
    return img.resize((w, h), Image.LANCZOS).convert("RGB")


def tray_icon(px, state):
    S = px * SS
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if state == "off":
        big = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        bd = ImageDraw.Draw(big)
        bd.polygon(sparkle(S, S * 0.46, S * 0.54, S * 0.44), fill=(150, 150, 150, 255))
        bd.polygon(sparkle(S, S * 0.46, S * 0.54, S * 0.26), fill=(0, 0, 0, 0))
        img = big
    else:
        d.polygon(sparkle(S, S * 0.46, S * 0.54, S * 0.44), fill=WHITE)
        dot = GREEN if state == "live" else AMBER
        r = S * 0.2
        cx, cy = S * 0.79, S * 0.21
        # cut a ring around the dot so it reads on any taskbar
        mask = Image.new("L", (S, S), 0)
        ImageDraw.Draw(mask).ellipse([cx - r - S * 0.07, cy - r - S * 0.07, cx + r + S * 0.07, cy + r + S * 0.07], fill=255)
        img.paste((0, 0, 0, 0), (0, 0), mask)
        ImageDraw.Draw(img).ellipse([cx - r, cy - r, cx + r, cy + r], fill=dot)
    return img.resize((px, px), Image.LANCZOS)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    imgs = [app_icon(s) for s in sizes]
    imgs[-1].save(OUT / "flair.ico", sizes=[(s, s) for s in sizes], append_images=imgs[:-1])
    app_icon(256).save(OUT / "logo.png")
    app_icon(512).save(ROOT / "docs" / "icon-512.png")
    discord_icon(1024).save(ROOT / "docs" / "discord-icon.png")
    discord_cover(1024, 576).save(ROOT / "docs" / "discord-cover.png")
    # the preview's stand-in for the "flair" asset while Discord's asset list catches up
    discord_icon(256).save(OUT.parent / "wwwroot" / "flair-art.png")
    for state in ("live", "off", "waiting"):
        tsizes = [16, 20, 24, 32, 40, 48]
        t = [tray_icon(s, state) for s in tsizes]
        t[-1].save(OUT / f"tray-{state}.ico", sizes=[(s, s) for s in tsizes], append_images=t[:-1])
    print("icons written to", OUT)


if __name__ == "__main__":
    main()
