# Map data: how the game stores it (2026-11-16)

The galaxy map's data (sectors, systems, families, POIs, regions, tags, sprites) can be read straight from the game's
files, offline. The files carry no type information, so `tools/xref` now works out each script type's layout from
the game's code with Unity 5.4's saving rules (`tools/xref/Layout.cs`), and has Unity's own Sprite and Texture2D
layouts written out (`tools/xref/UnityAssets.cs`). Every layout is checked the strict way: each object must use
exactly the bytes it's stored in.

## Commands

    cd tools/xref && dotnet build -c Release -o out
    dotnet out/xref.dll layout Map.SystemNode              # how a type is saved
    dotnet out/xref.dll check resources.assets             # does every script object read exactly?
    dotnet out/xref.dll read resources.assets Map.SystemRoot   # decode as JSON (one class; or a name too)
    dotnet out/xref.dll objects resources.assets 213       # list objects (of a class ID)
    dotnet out/xref.dll sprites resources.assets out/dir   # every sprite, checked, saved as PNG
    dotnet out/xref.dll gameobject level7 636              # a GameObject's components, sprites, parent, children

## Checked against the real files

- `resources.assets`: every map type reads exactly: 23 `Map.SectorRoot`, 56 `Map.SystemRoot`, 9,486
  `Map.SystemNode`, 9,454 `SystemTransform`, 2,447 `Map.BVH.BVHNode`, and the `Sector.*` bodies. (25,673 script
  objects in all read exactly. The 420 that don't are AI ships, mission tables and effects that hold Unity types
  not written out here, such as AnimationCurve; none are map data.)
- **The Trench** is the SectorRoot "0 - Sector - Trench A" (path ID 17582): 15 systems, default system Ryburn.
  Names are text keys ("2824|Venov"); positions sit inside the warp map's universe (x ±265, z ±115). (The in-game
  catalogue's 16 included Venov II, which isn't one of the Trench's own.)
- **Systems → nodes:** a system's nodes are the SystemNodes whose `Root` links to it. The tree is
  ROOT → FAMILY (prefab "Family Group") → POI (prefab "Point of Interest") → regions → bodies, linked by
  `_parent` / `_children`. Families carry `FamilyMapBackground` (e.g. FamilyMapBG_3) and a `SystemMapName`
  ("5939|Venov III"); POIs carry `SystemMapIconSprite` (spr_sys_map_icon_planet, _star, _nebula ...).
- **Tags** are `NovaTagList._tagNames`, plain strings (ROOT, FAMILY, Mission, LabelSystemVenov ...).
- **A node's `Prefab`** links to the body's SectorBody component; its `m_GameObject` gives the prefab's name.
- **Sprites:** all 124 in `resources.assets` and all 108 in `sharedassets7.assets` read exactly, and decode to
  correct PNGs (DXT1/DXT5/RGBA). In the player build a Sprite has `m_Pivot` but no `m_RenderDataKey` or
  `m_AtlasTags` (those are editor only).
- **The warp map art** is the sprite "WarpMap" (`sharedassets7.assets`, path ID 113, 2048×1024), on the
  `bg_image` object that is `_mapBase`'s parent; the Aegis and TNG warp panels both use it. The TOS bridge's
  panel has its own (`TOS_warp_map_bg`, `sharedassets13.assets`).

## Layouts

Every MonoBehaviour/ScriptableObject starts with Unity's header (m_GameObject, m_Enabled, m_Script, m_Name).
Vector3/Quaternion/Color are 3–4 floats; Vector3d is 3 doubles; a PPtr is Int32 file ID + Int64 path ID
(file ID 0 = the same file, else the file's externals list, 1-based).

```
Map.SectorRoot Map.SectorRoot
  PPtr<GameObject> m_GameObject
  UInt8 m_Enabled  (padded to 4)
  PPtr<MonoScript> m_Script
  string m_Name
  List<SystemLink> Systems  (Int32 count, then each)
      Vector3 Position
      PPtr<Map.SystemRoot> System
  PPtr<Map.SystemRoot> DefaultSystem
```

```
Map.SystemRoot Map.SystemRoot
  PPtr<GameObject> m_GameObject
  UInt8 m_Enabled  (padded to 4)
  PPtr<MonoScript> m_Script
  string m_Name
  PPtr<UnityEngine.Sprite> SystemMapBackground
  string SystemName
  string SystemIDTag
  PPtr<UnityEngine.GameObject> WarpMap3dVisualPrefab
  NovaTagList SystemTags
    String[] _tagNames  (Int32 count, then each)
  Boolean BvhOutOfDate  (padded to 4)
  List<SystemTransform> SystemTransforms  (Int32 count, then each)
  string _skyboxPath
  Color _ambientUpper
  Color _ambientMiddle
  Color _ambientLower
  Color _fillLightColor
  Vector3 _fillLightRotation
  Single _fillLightIntensity
```

```
Map.SystemNode Map.SystemNode
  PPtr<GameObject> m_GameObject
  UInt8 m_Enabled  (padded to 4)
  PPtr<MonoScript> m_Script
  string m_Name
  Boolean UsePrefabForName  (padded to 4)
  Vector3 Position
  Quaternion Rotation
  Int32 Scale  [enum Map.AADIKAFMHGP]
  Int32 ChildScale  [enum Map.AADIKAFMHGP]
  PPtr<Sector.SectorBody> Prefab
  PPtr<StatusEffectData> StatusEffectData
  StatusEffectData[] ProceduralStatusEffectData  (Int32 count, then each)
  PPtr<Map.SystemRoot> Root
  PPtr<Map.SystemNode> _parent
  PPtr<SystemTransform> Transform
  NovaTagList Tags
    String[] _tagNames  (Int32 count, then each)
  List<SystemNode> _children  (Int32 count, then each)
  Single ImpulseLandingRadius
  PPtr<UnityEngine.Sprite> SystemMapIconSprite
  PPtr<UnityEngine.Sprite> FamilyMapBackground
  string SystemMapName
```

```
SystemTransform SystemTransform
  PPtr<GameObject> m_GameObject
  UInt8 m_Enabled  (padded to 4)
  PPtr<MonoScript> m_Script
  string m_Name
  UnityEngine.Vector3d _position
  Quaternion _rotation
  Double _radius
  PPtr<Map.SystemNode> _systemNode
  string _name
```

```
NovaTagList NovaTagList
  String[] _tagNames  (Int32 count, then each)
```

