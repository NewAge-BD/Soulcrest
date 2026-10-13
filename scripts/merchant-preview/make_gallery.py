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
    canvas = Image.new('RGB', (1260, 1390), (18, 19, 19))
    draw = ImageDraw.Draw(canvas)
    draw.text((24, 16), 'Soulcrest · Black Cloud Merchant', font=title, fill=(225, 222, 208))
    draw.text((24, 57), 'Alle fünf UI-Skalen · Ausschnitte 1,4× · Beispielwerte, keine echten Fortschrittsstände',
              font=small, fill=(174, 173, 160))
    note = {'normal': 'Normale Counter, MAX und unbekannte Werte · Farmregionen aus dem Katalog',
            'stress': 'Drei Farmregionen als synthetischer Breitentest',
            'priority': '0/5 sowie Special mit ? / MAX · Farmzuordnungen hier bewusst synthetisch'}[kind]
    draw.text((24, 80), note, font=small, fill=(174, 173, 160))
    for index, name in enumerate(('smaller', 'small', 'medium', 'large', 'larger')):
        base = f'{name}-native' + ('' if kind == 'normal' else '-synthetic-' + kind)
        report = json.loads((root / (base + '.json')).read_text())
        image = Image.open(root / (base + '.png')).convert('RGB')
        entries = {entry['SlotIndex']: entry for entry in report['Footers']}
        if kind == 'priority':
            slots = list(entries)
        elif index < 3:
            slots = [0, 2, 1] if kind == 'stress' else [1, 3, 4]
        else:
            slots = [9, 7, 8] if kind == 'stress' else [7, 9, 8]
        y = 117 + index * 252
        draw.text((24, y), name.title(), font=font, fill=(218, 208, 178))
        for column, slot in enumerate(slots[:3]):
            bounds = entries[slot]['Bounds']
            center = bounds['X'] + bounds['Width'] / 2
            crop = image.crop((round(center - 142), bounds['Y'] - 38,
                               round(center + 142), bounds['Y'] + 112))
            crop = crop.resize((398, 210), Image.Resampling.LANCZOS)
            canvas.paste(crop, (24 + column * 412, y + 32))
    canvas.save(root / ('details-all-scales' + ('' if kind == 'normal' else '-' + kind) + '.png'))
