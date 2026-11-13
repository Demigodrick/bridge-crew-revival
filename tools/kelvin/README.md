# Kelvin asset tools

`source/Enterprise_V6_Complete_Packed.blend` is Ricky "MadMan1701A" Wallace's original packed Blender 2.45 model. Attribution and permission provenance are documented in `assets/ships/AbramsEnterprise/README.md`; retain the author's original notes.

Use Blender 2.79b to preserve the original face-texture assignments:

```sh
blender --background --factory-startup --disable-autoexec source/Enterprise_V6_Complete_Packed.blend --python convert.py
python validate.py converted
```

The converter writes OBJ/MTL/PNG under `converted/`. Coordinates are `(x,z,-y)` from Blender; the runtime mirrors X and reverses winding for Unity. Hull pieces are ordered first to keep collider input representative. `hardpoints.py` inspects original phaser-bank positions.

Validate the committed model without Blender:

```sh
python tools/kelvin/validate.py assets/ships/AbramsEnterprise
python tools/kelvin/build_font_atlas.py
```

The atlas script requires Pillow. Its input font is included under SIL OFL, from https://github.com/google/fonts/tree/main/ofl/michroma. Runtime uses the bottom-up 2048x2048 coverage buffer and glyph metrics. No machine-specific font installation is required.

Build the mod using the repository's existing build instructions; no local game DLLs, generated mod binaries, debug screenshots, runtime logs or freecam code are included here.
