"""Rebuild the bundled Michroma coverage atlas. Requires Pillow; run from any directory."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

folder = Path(__file__).resolve().parents[2] / 'assets/ships/AbramsEnterprise/Michroma'
font = ImageFont.truetype(str(folder / 'Michroma-Regular.ttf'), 128)
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
