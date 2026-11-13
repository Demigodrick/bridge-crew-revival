"""Rebuild the bundled Michroma coverage atlas beside the Kelvin model. Requires Pillow; run from any directory.

The atlas and glyphs sit directly in the ship's folder: the in-game updater only fetches ships/<ship>/<file>."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

here = Path(__file__).resolve().parent
folder = here.parents[1] / 'assets/ships/AbramsEnterprise'
font = ImageFont.truetype(str(here / 'Michroma-Regular.ttf'), 128, layout_engine=ImageFont.Layout.BASIC)
chars = sorted(set(chr(i).upper() for i in range(32, 256) if len(chr(i).upper()) == 1))
w = h = 2048
image = Image.new('L', (w, h))
draw = ImageDraw.Draw(image)
lines = []
x = y = 4
for char in chars:
    left, top, right, bottom = font.getbbox(char, anchor='ls')
    width, height = right-left, bottom-top
    if x + width + 4 > w:
        x = 4
        y += 150
    assert y + height < h, 'Atlas full'
    draw.text((x-left, y-top), char, font=font, fill=255, anchor='ls')
    lines.append(' '.join(map(str, [ord(char), left, right, -bottom, -top,
        font.getlength(char), x, h-y-height, x+width, h-y])))
    x += width + 8
(folder / 'Michroma-glyphs.txt').write_text('\n'.join(lines), encoding='ascii')
(folder / 'Michroma-atlas.bin').write_bytes(image.transpose(Image.Transpose.FLIP_TOP_BOTTOM).tobytes())
