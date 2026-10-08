"""Erzeugt die App-Symbole aus dem vom Nutzer gelieferten Soulcrest-Logo.

Quelle: src/Soulcrest.App/Assets/soulcrest-logo.png (Original aus Beispieldaten).
Motiv, Farben und Hintergrund bleiben erhalten; nur die Auflösung wird angepasst.
Ausgabe: wwwroot/soulcrest.png (512 px) und soulcrest.ico (16–256 px)
für App-Kopfzeile, Favicon, Fenster, Taskleiste, EXE und Setup.
Aufruf: python scripts/make_icon.py
"""
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src/Soulcrest.App/Assets/soulcrest-logo.png"
OUT = ROOT / "src/Soulcrest.App/wwwroot"
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    with Image.open(SOURCE) as original:
        if original.width != original.height:
            raise ValueError("Das Quelllogo muss quadratisch sein.")
        logo = original.convert("RGBA")
        logo.resize((512, 512), Image.Resampling.LANCZOS).save(OUT / "soulcrest.png")
        logo.save(OUT / "soulcrest.ico", format="ICO", sizes=[(n, n) for n in SIZES])
    print("soulcrest.ico / soulcrest.png geschrieben:", OUT)


if __name__ == "__main__":
    main()
