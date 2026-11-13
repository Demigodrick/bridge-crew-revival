# Enterprise (Kelvin timeline)

Select **ENTERPRISE (KELVIN)** in the briefing room, or set `[Ships] PlayerShip = AbramsEnterprise`.
Uses the game's Aegis bridge and systems; no new bridge interior or combat balance changes.

Model: USS Enterprise NCC-1701, Star Trek (2009), version 6, by **Ricky "MadMan1701A" Wallace** (MadShipyard).
Source: https://trekmeshes.eu/download/dl231/USSEnterpriseNCC1701Abrams
Original author notes: `CREDITS.txt`. Original packed Blender model: `tools/kelvin/source`.

The contributor YannikB98 confirmed permission to upload the complete model and textures to this repository on 2026-11-13. This records that confirmation; it does not relicense the model under the code's license. The original author notes refer to personal use. No separate written grant was supplied in this contribution. Star Trek marks belong to their respective owners.

The ship copies the Aegis collector meshes, materials and animation controllers from the locally installed game at runtime. No extracted game assets are included.

Name and registry follow the existing crew settings. The saucer and both nacelles use Michroma Regular, an approximation of the Kelvin lettering, with black lettering and no red outline. Michroma is by Vernon Adams / the Michroma Project Authors under the SIL OFL; see `Michroma/OFL.txt`. The bundled atlas avoids Unity 5's unreliable discovery of per-user Windows fonts. It contains 167 uppercase/Latin-1 characters; unsupported characters display `?`.

The original texture files remain intact: lettering is repainted in memory. Bridge viewpoint and space-bar exterior viewpoint are independent. No free camera is included.

## Validation and limits

- Built against the installed game's Unity/BepInEx assemblies on Windows.
- Model: 300,087 positions, 564,371 triangles, 95 renderer chunks. Rear nacelle surfaces additionally render their reverse faces.
- Runtime collider: 74 points, 148 faces; avoids the repeated PhysX cooking failure of the original detailed collider.
- Local mission checks: model construction, collector animation components, name/registry textures, and camera placement. The user confirmed the final bridge and exterior viewpoints.
- GPU readback verified the bridge texture changed from black to its light hull pattern after bypassing DXT compression for dimensions not divisible by four.
- Rear cap visual confirmation, multiplayer combinations, Linux and VR still need reviewer testing. The simplified briefing hologram retains the original lettering.

`tools/package.sh` already copies this asset directory; `tools/release.sh` already discovers its files recursively.
