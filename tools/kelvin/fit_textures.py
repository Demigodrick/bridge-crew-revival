"""Fit the Kelvin textures for the game: at most 2048 on a side, each side a multiple of four.

Unity 5 on D3D11 can't make block-compressed (DXT) textures whose size isn't a multiple of four, and every 3000x3000
texture costs a long main-thread decode and compression as the briefing room opens. Run after convert.py, on its
output or on the committed folder (it rewrites the files in place; already fitted ones are left alone).
Requires Pillow."""
import sys
from pathlib import Path
from PIL import Image

MAX = 2048
folder = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[2] / 'assets/ships/AbramsEnterprise'
for path in sorted(folder.glob('tex_*.png')):
    with Image.open(path) as image:
        w, h = image.size
        s = min(1.0, MAX / max(w, h))
        nw, nh = max(4, round(w * s / 4) * 4), max(4, round(h * s / 4) * 4)
        if (nw, nh) == (w, h):
            continue
        fitted = image.resize((nw, nh), Image.LANCZOS)
    fitted.save(path, optimize=True)
    print('%s: %dx%d -> %dx%d' % (path.name, w, h, nw, nh))
