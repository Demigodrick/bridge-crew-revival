# Building a sector: a guide

How to build a new sector (a set of new systems on the warp map's second page) with the galaxy designer, following
the way the game's own Trench systems are made. The numbers below come from reading all 15 Trench systems.

## The four levels, top to bottom

| Level | What you see in the game | Typical in the Trench | Unit it's placed in |
|---|---|---|---|
| **System** | a button on the warp map | 15 systems | the warp map itself |
| **Family** | a dot on the impulse map's system overview (the rings picture) | 2–5 per system | lightseconds from the system's centre |
| **POI** (Point of Interest) | a place you pick on the impulse map and fly to | 1–4 per family, usually 2 | "Skybox" units (1 = 1,000 km) from the family |
| **Region** | the patch of space you arrive in at that POI, with what you fly among | usually 1 per POI | Skybox units from the POI |
| **Inside a region** | stations, asteroids, debris, warp-in, spawn points | about 12 things each | kilometres from the region |

Big bodies (the star, a planet, a moon) hang **directly under the POI**, so they're seen from far away. Everything you
fly around (stations, asteroid clusters, clouds, debris) goes **inside a region**.

## Venov, as an example

```
Venov (system)
├─ Venov Major      family, at the centre (0, 0)          icon: star
│  ├─ Main Star     POI with the star under it
│  └─ Planet 1      POI with a planet; region "Impulse" (planet)
├─ Venov III        family, 6 ls out                       icon: planet
│  ├─ Start Planet  POI with the planet; region "Start Impulse": the warp-in point, asteroids, clouds
│  ├─ Moon A        POI 40 Mm away with a moon; region "Impulse" (moon)
│  └─ Moon B        POI with a moon; an anomaly region and an asteroid region on the same spot
├─ Venov II, Venov IV   more planet families, 2–4 ls out, each with a moon or two
└─ Cloud            family, 2 ls out                       icon: nebula
   ├─ Gas Cloud     POI; an anomaly region
   └─ debris field  POI; a deep-space region and a space-station region
```

## Step by step

### 1. The system (galaxy view)
1. **Click an empty spot** on the map to add a system, then set its **Name**: that's the warp map label.
2. Pick a **template**: a Trench system whose sky and lighting the new one copies (Venov is reddish, Toth golden…).
3. Drag it where you want it. Keep it clear of other systems and away from the right edge (its label must fit).
4. **Double-click** it to open it.

### 2. The families (system view)
Plan 2–5 families, like the Trench does:
- **A home family at the centre** (position 0, 0, 0) for the system's star. Icon: star.
- **Two to four more** for planets, a gas cloud, a debris field and so on, **2–6 lightseconds out**. Never more than
  **6.7 lightseconds** on either axis: further out falls off the impulse map (Check warns you).

Place the families on the **System view**, the level you start at when you open a system (the **System** button in the
bar above the plan brings you back to it). It draws the system like the game's impulse map overview: the centre, a ring
through each family's distance, and every family as a dot with its icon and name. A dashed box marks the impulse map's
reach (6.7 lightseconds each way); a family outside it turns red. **Drag a family** to place it; **click** it to open its
page. A family copied from the Trench starts where the Trench has it, and you can drag that too. In a Trench system, its own
families are shown greyed and can't be moved.

For each family: **Add family (empty)**, set its **Name** (the dot's label on the overview), its **icon** (planet,
star, nebula, space station, asteroid field…) and its **backdrop** (FamilyMapBG_1 to _6, the art behind its page).

Shortcut: **Copy a Trench family** brings in a whole finished family, with its POIs, regions and contents, as it is in
the game. It's a quick way to start a system and then add your own families beside it.

### 3. The POIs (a family's page)
For each family, 1–4 POIs:
- **The first POI at the family's centre** (0, 0, 0), holding the family's big body: add the planet or star **under
  the POI** (Add body with the POI selected), in Skybox units, typically 10–50 units away.
- **Other POIs 10–50 Skybox units** (10,000–50,000 km) from the family centre: a moon, an asteroid field, a station.
- Give each POI a **name** (shown on its family's page) and an **icon** that says what's there.

### 4. The regions (a POI's view)
Select the POI and press **Add region**. It comes with what every game region has: an **Impulse Destination** at its
centre (where you arrive) and **six spawn points** around it (where ships and raiders appear). Place the region a few
Skybox units from the POI's body, not on top of it.

Two regions can sit on the same spot with different jobs (Venov does this: an anomaly region and an asteroid region
together).

Optionally tag the region with what kind of place it is, in its tag box:
`RegionStart` (the system's arrival area), `RegionPlanet`, `RegionMoon`, `RegionStar`, `RegionAsteroid`,
`RegionAnomaly`, `RegionSpaceStation`, `RegionDeepSpace`.

### 5. Inside a region (click a region: kilometres)
Click the region and the plan zooms into it, in kilometres. Drag things to place them. Typical distances in the Trench:
- **Impulse Destination**: near the centre, about **5 km** out.
- **Spawn points**: around **15 km** out.
- **Scenery** (asteroid clusters, clouds, debris, wrecks): **20–30 km** out.
- **A station**: within about 20 km, if this is a station region. Add it with **Add body** and pick from **Stations**.
- **The warp-in point** (Add jump point): **exactly one per system**, in the region where crews should arrive. In the
  Trench it's in the home or start planet's first region.

### 6. What Patrol needs
Patrol finds places by what's in them. In a body's panel, under **What it is for Patrol**, tick what it is: station
(restock and rescues), asteroid field, nebula, anomaly (scans), planet, moon, impulse destination. Station models tick
"station" by themselves.

Have at least one **station** somewhere in the sector if you want Patrol to restock there. Without one, Check gives a
warning, but the sector still loads.

### 7. Check, save, fly
1. **Check**. Red lines are problems: the game won't load the file until they're fixed. Click one to jump to it.
   Amber lines are warnings: the file still loads.
2. **Save**.
3. In `BepInEx/config/stbc.revival.cfg`, set `[Galaxy] Region = <your file name>`, start the game and host Revival
   Patrol or Free Roam. Your systems are on the warp map's second page.

## Adding to a Trench system instead

Double-click a grey Trench system on the galaxy map. Its own families show read-only. Add new families beside them the
same way (steps 2–5), keeping within 6.7 lightseconds and clear of its existing families. A system that already has a
warp-in point doesn't need another.

## Quick reference

- 1 lightsecond ≈ 300,000 km; 1 Skybox unit = 1,000 km.
- Family: within ±6.7 ls of the system centre.
- POI: 10–50 Skybox units from its family; big body under it.
- Region: a few Skybox units from its POI; contents in km.
- One warp-in per system. Every region: an impulse destination and spawn points (Add region does both).
- Every family and POI: a name and an icon.
