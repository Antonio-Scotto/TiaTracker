"""Icona di TiaTracker: un ramo di versioni (due versioni sul tronco, una derivata)
con la versione caricata sul PLC in verde, su un quadrato blu dai colori dell'app.

    python tools\\make-icon.py [cartella anteprima]

Scrive src\\TiaTracker.App\\Assets\\TiaTracker.ico (16-256 px, exe e finestre),
TiaTracker.png (128 px, logo nella barra dell'app) e, se si indica una cartella,
un'anteprima PNG di tutte le misure. Serve Pillow.
"""

import sys
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
ICO = ROOT / "src" / "TiaTracker.App" / "Assets" / "TiaTracker.ico"
PNG = ICO.with_suffix(".png")
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
WORK = 1024  # si disegna grande e si riduce

BLUE_TOP = (59, 130, 246)  # AccentBrush
BLUE_BOTTOM = (29, 78, 216)
WHITE = (255, 255, 255)
GREEN = (34, 197, 94)  # Tone.Success.Solid


def draw(size: int) -> Image.Image:
    """Le misure piccole hanno tratti piu' spessi, altrimenti spariscono."""
    s = WORK
    bold = 1.45 if size <= 20 else 1.25 if size <= 32 else 1.0
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # Fondo: quadrato arrotondato con sfumatura verticale.
    margin = 0.03 * s if size >= 32 else 0.0
    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        t = y / (s - 1)
        gd.line([(0, y), (s, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(BLUE_TOP, BLUE_BOTTOM)) + (255,))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([margin, margin, s - 1 - margin, s - 1 - margin], radius=0.22 * s, fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)
    line = round(0.075 * s * bold)
    node = 0.085 * s * bold
    trunk_x = 0.34 * s
    top, bottom = 0.27 * s, 0.73 * s
    branch = (0.68 * s, 0.36 * s)

    # Tronco e ramo: dal nodo derivato scende e piega verso il tronco. Il ramo si
    # disegna a dischi lungo la curva, cosi' resta liscio e senza giunte.
    d.line([(trunk_x, top), (trunk_x, bottom)], fill=WHITE, width=line)
    (x0, y0), (x3, y3) = branch, (trunk_x, 0.71 * s)
    (x1, y1), (x2, y2) = (x0, 0.60 * s), (x3, 0.50 * s)
    for i in range(401):
        t = i / 400
        x = (1 - t) ** 3 * x0 + 3 * (1 - t) ** 2 * t * x1 + 3 * (1 - t) * t ** 2 * x2 + t ** 3 * x3
        y = (1 - t) ** 3 * y0 + 3 * (1 - t) ** 2 * t * y1 + 3 * (1 - t) * t ** 2 * y2 + t ** 3 * y3
        d.ellipse([x - line / 2, y - line / 2, x + line / 2, y + line / 2], fill=WHITE)

    def dot(cx: float, cy: float, r: float, fill) -> None:
        d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=fill)

    for cy in (top, bottom):
        dot(trunk_x, cy, node, WHITE)

    # Versione caricata: verde con anello bianco e spunta.
    ring = node * 1.55
    dot(*branch, ring, WHITE)
    dot(*branch, ring - 0.035 * s * bold, GREEN)
    if size >= 32:
        bx, by = branch
        k = ring * 0.52
        d.line([(bx - k * 0.95, by + k * 0.05), (bx - k * 0.25, by + k * 0.7), (bx + k * 0.95, by - k * 0.6)],
               fill=WHITE, width=round(0.04 * s), joint="curve")

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    images = [draw(n) for n in SIZES]
    ICO.parent.mkdir(parents=True, exist_ok=True)
    images[-1].save(ICO, format="ICO", sizes=[(n, n) for n in SIZES], append_images=images[:-1])
    images[SIZES.index(128)].save(PNG)
    print("Scritte", ICO, "e", PNG.name)

    if len(sys.argv) > 1:
        out = Path(sys.argv[1])
        out.mkdir(parents=True, exist_ok=True)
        for bg, name in (((18, 20, 24, 255), "scuro"), ((243, 243, 243, 255), "chiaro")):
            sheet = Image.new("RGBA", (sum(SIZES) + 16 * (len(SIZES) + 1), 256 + 32), bg)
            x = 16
            for n, im in zip(SIZES, images):
                sheet.alpha_composite(im, (x, 16 + 256 - n))
                x += n + 16
            sheet.save(out / f"icona-{name}.png")
        big = Image.new("RGBA", (16 * 8 + 32 * 8, 32 * 8), (18, 20, 24, 255))
        big.alpha_composite(images[0].resize((128, 128), Image.NEAREST), (0, 64))
        big.alpha_composite(images[3].resize((256, 256), Image.NEAREST), (128, 0))
        big.save(out / "icona-ingrandita.png")
        print("Anteprime in", out)


if __name__ == "__main__":
    main()
