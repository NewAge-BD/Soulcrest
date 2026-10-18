"""Create review contact sheets from MerchantPreview outputs (requires Pillow)."""
from pathlib import Path
import json
import os
import sys
from PIL import Image, ImageDraw, ImageFont

root = Path(sys.argv[1])
font_path = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Fonts/segoeui.ttf'
font = ImageFont.truetype(str(font_path), 20)
small = ImageFont.truetype(str(font_path), 16)
title = ImageFont.truetype(str(font_path), 28)
for kind in ('normal', 'stress', 'priority'):
    rows = []
    for index, name in enumerate(('smaller', 'small', 'medium', 'large', 'larger')):
        base = f'{name}-native' + ('' if kind == 'normal' else '-synthetic-' + kind)
        report = json.loads((root / (base + '.json')).read_text())
        with Image.open(root / (base + '.png')) as source:
            image = source.convert('RGB')
        entries = {entry['SlotIndex']: entry for entry in report['Footers']}
        if kind == 'priority':
            slots = list(entries)
        elif index < 3:
            slots = [0, 2, 1] if kind == 'stress' else [1, 3, 4]
        else:
            slots = [9, 7, 8] if kind == 'stress' else [7, 9, 8]
        crops = []
        for slot in slots[:3]:
            entry = entries[slot]
            foot = entry['Bounds']
            surface = entry.get('SurfaceBounds', foot)
            center = foot['X'] + foot['Width'] / 2
            # Keep the category, native pet name/artwork, price and counter together.
            top = min(surface['Y'] - 10, foot['Y'] - 38)
            bottom = max(surface['Bottom'], foot['Bottom']) + 12
            crop = image.crop((round(center - 142), top, round(center + 142), bottom))
            crops.append(crop.resize((398, round(crop.height * 398 / 284)), Image.Resampling.LANCZOS))
        rows.append((name, crops, max(crop.height for crop in crops) + 48))
    canvas = Image.new('RGB', (1260, 133 + sum(height for _, _, height in rows)), (18, 19, 19))
    draw = ImageDraw.Draw(canvas)
    draw.text((24, 16), 'Soulcrest · Black Cloud Merchant', font=title, fill=(225, 222, 208))
    draw.text((24, 57), 'Alle fünf UI-Skalen · Ausschnitte 1,4× · Beispielwerte, keine echten Fortschrittsstände',
              font=small, fill=(174, 173, 160))
    note = {'normal': 'Kategorie über dem Petnamen · Counter unter dem Preis · Farmquellen aus dem lokalen Katalog',
            'stress': 'Vier Kategorien und drei dreistellige Farmregionen als synthetischer Breitentest',
            'priority': '0/5 sowie Special mit ? / MAX · Farmzuordnungen hier bewusst synthetisch'}[kind]
    draw.text((24, 80), note, font=small, fill=(174, 173, 160))
    y = 117
    for name, crops, height in rows:
        draw.text((24, y), name.title(), font=font, fill=(218, 208, 178))
        for column, crop in enumerate(crops):
            canvas.paste(crop, (24 + column * 412, y + 32))
        y += height
    canvas.save(root / ('details-all-scales' + ('' if kind == 'normal' else '-' + kind) + '.png'))
