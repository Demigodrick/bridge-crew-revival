# Kelvin asset tools

How `assets/ships/AbramsEnterprise` was made from Ricky "MadMan1701A" Wallace's Enterprise V6 (a packed Blender 2.45 file,
`Enterprise_V6_Complete_Packed.blend`, from https://trekmeshes.eu/download/dl231/USSEnterpriseNCC1701Abrams; not kept in
this repository). Attribution and permission are recorded in `assets/ships/AbramsEnterprise/README.md`.

Use Blender 2.79b to keep the original face-texture assignments:

```sh
blender --background --factory-startup --disable-autoexec Enterprise_V6_Complete_Packed.blend --python convert.py
python fit_textures.py converted
python validate.py converted
```

The converter writes OBJ/MTL/PNG under `converted/`. Coordinates are `(x,z,-y)` from Blender; the runtime mirrors X and
reverses winding for Unity. Hull pieces come first so the collider input covers the hull. `fit_textures.py` brings every
texture to at most 2048 on a side and a multiple of four (DXT compression needs that on D3D11). `hardpoints.py` reports
the phaser banks' positions.

Check the committed model without Blender, and rebuild the lettering atlas (needs Pillow; from `Michroma-Regular.ttf`
here, SIL OFL, https://github.com/google/fonts/tree/main/ofl/michroma):

```sh
python tools/kelvin/validate.py assets/ships/AbramsEnterprise
python tools/kelvin/build_font_atlas.py
```

Everything in the ship's folder must sit directly in it: the in-game updater fetches `ships/<ship>/<file>` only.
