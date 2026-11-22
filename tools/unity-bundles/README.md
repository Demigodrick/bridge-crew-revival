# Bridge parts from Unity

Build your own bridge parts in Unity and place them on the game's bridges with the bridge designer.

## You need

- **Unity 5.4** (the game is Unity 5.4.4p1). Bundles from any other version won't load in the game. Unity 5.4.4 is in
  Unity's download archive.
- The game with Bridge Crew Revival, and the bridge designer (`BepInEx/bridges`, comes with the installer).

## Build a bundle

1. Copy `Editor/RevivalBundles.cs` into an `Editor` folder in your Unity project.
2. Make your part a prefab. Its origin is the point the designer places, so put it where the part should stand on
   the floor.
3. Select the prefab (or several) in the Project window, then **Bridge Crew Revival > Build bundle from selected
   prefabs**.
4. Save it in the game's `BepInEx/bridges/bundles` folder. The script remembers the folder next time.

The script builds for Windows with LZ4 compression, which is what the game and the designer read.

## Place it

Open the bridge designer, pick a bridge, then **Add parts > Bundles**. Your prefabs are listed by name. Add one,
move, turn and scale it, hide or move the bridge's own pieces around it, and **Save layout**. The game shows it
straight away. Build the bundle again and the game picks up the new one at the next bridge load.

## What comes through

- Meshes, materials and textures, using Unity's built-in shaders (Standard and so on) or your own.
- Colliders and lights.
- Not your scripts: the game only has its own. The script lists any it finds before building.
- Not baked lightmaps: parts are lit by the bridge's light probes, plus any lights you add.

Crewmates see your part only if they have the same bundle in their `bundles` folder.
