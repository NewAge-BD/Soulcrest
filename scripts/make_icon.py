"""Zeichnet das Soulcrest-Programmsymbol (eigenes Motiv, keine Spielgrafik).

Wappenschild (crest) mit Seelenflamme (soul) zwischen zwei Daeva-Flügeln (Aion), in den Farben der
App (Akzent #5eead4 auf #0e1117). Ausgabe:
  src/Soulcrest.App/wwwroot/soulcrest.ico   Fenster, Taskleiste, Exe und Setup (16-256 px)
  src/Soulcrest.App/wwwroot/soulcrest.png   512 px, Vorschau und Favicon

Aufruf: python scripts/make_icon.py
"""
import math
import os

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "Soulcrest.App", "wwwroot")
S = 1024  # Zeichenfläche, wird heruntergerechnet (Kantenglättung)

TEAL = (94, 234, 212)
TEAL_DEEP = (20, 150, 140)
GOLD = (236, 196, 110)
GOLD_DEEP = (160, 110, 40)
NAVY = (14, 17, 23)
NAVY_2 = (33, 43, 60)


def mask(draw_fn):
    m = Image.new("L", (S, S), 0)
    draw_fn(ImageDraw.Draw(m))
    return m


def gradient(top, bottom, box=(0, S)):
    """Vertikaler Verlauf über die ganze Fläche."""
    g = Image.new("RGBA", (S, S))
    y0, y1 = box
    px = g.load()
    col = []
    for y in range(S):
        t = min(1.0, max(0.0, (y - y0) / max(1, y1 - y0)))
        col.append(tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
    line = Image.new("RGBA", (1, S))
    for y, c in enumerate(col):
        line.putpixel((0, y), c)
    return line.resize((S, S))


def fill(canvas, m, paint):
    layer = paint if isinstance(paint, Image.Image) else Image.new("RGBA", (S, S), paint + (255,))
    canvas.paste(layer, (0, 0), m)


def shield_points(inset=0.0):
    """Wappenschild: leicht gewölbte Oberkante, gerade Flanken, Spitze unten."""
    cx = S / 2
    w, top, mid, tip = 0.30 * S - inset, 0.25 * S + inset, 0.55 * S, 0.88 * S - inset * 1.6
    pts = []
    for i in range(41):  # Oberkante mit Mittelzacke
        t = i / 40
        x = cx - w + 2 * w * t
        y = top + 0.035 * S * (1 - abs(2 * t - 1)) * -1 + 0.03 * S * (abs(2 * t - 1)) ** 2
        pts.append((x, y))
    for i in range(1, 41):  # rechte Flanke zur Spitze
        t = i / 40
        x = cx + w * (1 - t ** 1.6)
        y = mid + (tip - mid) * t if t > 0 else mid
        y = (top + 0.03 * S) + (tip - top - 0.03 * S) * (t ** 0.9)
        pts.append((x, y))
    for i in range(1, 40):  # linke Flanke zurück
        t = 1 - i / 40
        x = cx - w * (1 - t ** 1.6)
        y = (top + 0.03 * S) + (tip - top - 0.03 * S) * (t ** 0.9)
        pts.append((x, y))
    return pts


def feather(cx, cy, length, angle, width):
    """Spitz zulaufende Feder als Polygon (Ansatz bei cx, cy)."""
    pts = []
    for i in range(31):
        t = i / 30
        r = width * math.sin(math.pi * t) * (1 - 0.55 * t)
        pts.append((t * length, r))
    pts += [(t * length, -r) for t, r in [(p[0] / length, p[1]) for p in reversed(pts)]]
    ca, sa = math.cos(angle), math.sin(angle)
    return [(cx + x * ca - y * sa, cy + x * sa + y * ca) for x, y in pts]


def wing(side):
    """Daeva-Flügel aus fünf gestaffelten Federn, side = -1 links, +1 rechts."""
    cx, cy = S / 2 + side * 0.20 * S, 0.42 * S
    feathers = []
    for k in range(5):
        a = math.radians(-58 + k * 19)  # von oben-außen nach unten-außen
        ang = math.pi - a if side < 0 else a
        length = (0.40 - 0.045 * k) * S
        feathers.append(feather(cx, cy + k * 0.025 * S, length, ang, (0.055 - 0.004 * k) * S))
    return feathers


def flame_points(cx, base, r, lean=0.18, height=2.35):
    """Flammenzunge: runder Fuß, nach oben gezogene, leicht geschwungene Spitze."""
    pts = []
    for i in range(121):
        t = i / 120 * 2 * math.pi
        x = math.sin(t)
        y = -math.cos(t)
        if y < 0:  # obere Hälfte zur Spitze ziehen
            k = -y
            x *= (1 - k) ** 1.25
            y = -k * height
            x += lean * k * k * math.sin(2.2 * k)
        pts.append((cx + x * r, base - r * 0.15 + y * r))
    return pts


def flames():
    """Seelenflamme aus Hauptzunge und zwei kleinen Nebenzungen."""
    cx, base, r = S / 2, 0.66 * S, 0.105 * S
    return [flame_points(cx - 0.055 * S, base + 0.005 * S, r * 0.55, lean=-0.35, height=2.1),
            flame_points(cx + 0.06 * S, base + 0.01 * S, r * 0.48, lean=0.4, height=1.9),
            flame_points(cx, base, r, lean=0.22, height=2.5)]


def sparkle(cx, cy, r):
    pts = []
    for i in range(8):
        a = i * math.pi / 4 - math.pi / 2
        rr = r if i % 2 == 0 else r * 0.22
        pts.append((cx + rr * math.cos(a), cy + rr * math.sin(a)))
    return pts


def odd(n):
    n = max(3, int(round(n * S / 2048)))
    return n if n % 2 else n + 1


def render(wings=True):
    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # Flügel: von der untersten zur obersten Feder, jede mit eigener dunkler Kontur
    if wings:
        for side in (-1, 1):
            for f in reversed(wing(side)):
                shape = mask(lambda d, f=f: d.polygon(f, fill=255))
                fill(canvas, shape.filter(ImageFilter.MaxFilter(odd(19))), NAVY)
                fill(canvas, shape, gradient(GOLD, GOLD_DEEP, (int(0.2 * S), int(0.75 * S))))
                fill(canvas, shape.filter(ImageFilter.MinFilter(odd(21))), gradient((255, 236, 190), GOLD, (int(0.2 * S), int(0.7 * S))))

    # Schild: Kontur, Goldrand, Türkisrand, dunkle Fläche
    outer = mask(lambda d: d.polygon(shield_points(), fill=255))
    fill(canvas, outer.filter(ImageFilter.MaxFilter(odd(31))), NAVY)
    fill(canvas, outer, gradient(GOLD, GOLD_DEEP, (int(0.25 * S), int(0.88 * S))))
    fill(canvas, mask(lambda d: d.polygon(shield_points(0.022 * S), fill=255)), gradient(TEAL, TEAL_DEEP))
    inner = mask(lambda d: d.polygon(shield_points(0.040 * S), fill=255))
    fill(canvas, inner, gradient(NAVY_2, NAVY, (int(0.25 * S), int(0.85 * S))))

    # Lichtschein der Seele, nur innerhalb des Schilds
    flame = Image.new("L", (S, S), 0)
    for tongue in flames():
        flame = ImageChops.lighter(flame, mask(lambda d, t=tongue: d.polygon(t, fill=255)))
    glow = flame.filter(ImageFilter.MaxFilter(odd(61))).filter(ImageFilter.GaussianBlur(70 * S / 2048))
    fill(canvas, ImageChops.multiply(glow, inner), TEAL)
    fill(canvas, flame, gradient((210, 255, 248), TEAL, (int(0.40 * S), int(0.75 * S))))
    core = mask(lambda d: d.polygon(flame_points(S / 2, 0.665 * S, 0.06 * S, lean=0.15, height=2.0), fill=255))
    fill(canvas, core.filter(ImageFilter.GaussianBlur(6 * S / 1024)), (235, 255, 252))

    # Funke über der Flamme
    fill(canvas, mask(lambda d: d.polygon(sparkle(S / 2 + 0.095 * S, 0.40 * S, 0.05 * S), fill=255)), (255, 255, 255))
    return canvas


def square(image):
    """Quadratisch auf das Motiv zuschneiden, damit es die Symbolfläche füllt."""
    bbox = image.getbbox()
    cx, cy = (bbox[0] + bbox[2]) / 2, (bbox[1] + bbox[3]) / 2
    half = max(bbox[2] - bbox[0], bbox[3] - bbox[1]) / 2 * 1.02
    return image.crop((int(cx - half), int(cy - half), int(cx + half), int(cy + half)))


def main():
    full = square(render(wings=True))
    # Bis 32 px nur das Wappen: mit Flügeln wäre es in der Taskleiste zu klein.
    compact = square(render(wings=False))
    os.makedirs(OUT, exist_ok=True)
    full.resize((512, 512), Image.LANCZOS).save(os.path.join(OUT, "soulcrest.png"))
    sizes = [256, 128, 64, 48, 40, 32, 24, 20, 16]
    frames = [(full if n > 32 else compact).resize((n, n), Image.LANCZOS) for n in sizes]
    frames[0].save(os.path.join(OUT, "soulcrest.ico"), format="ICO", sizes=[(n, n) for n in sizes], append_images=frames[1:])
    print("soulcrest.ico / soulcrest.png geschrieben:", OUT)


if __name__ == "__main__":
    main()
