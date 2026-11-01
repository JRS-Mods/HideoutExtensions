# Hideout Extensions

By JRS.

Rooms you build onto the hideout. A spot on a hideout wall gets a prompt; building it takes materials from your stash like a hideout upgrade, and when it's done a metal door stands there and leads into a new room.

Two parts, both needed:

- `BepInEx/plugins/HideoutExtensions` (client): the prompt, the door and the room.
- `SPT_Runtime/user/mods/HideoutExtensions.Server` (server): the spots, their costs and build times, and taking the materials.

## In the hideout: the annex

Rooms north of the gym (the back half of the room behind the emergency wall) and east of the shooting range, built one at a time. It needs the emergency wall at level 6.

```
   ┌─────────────────────────────┐
   │ north room    ┊             │
   ├────────────── ┊ ─── door ───┤
   │ south room    ┊             │
   └───[door]──────┴─────────────┘   ┊ pillar wall
   ┌──── annex corridor ─────────┐
   └──────[door]─────────────────┘
      gym's back wall
```

- **The annex corridor** is the sealed concrete corridor already in the hideout's model, right behind the gym's cream back wall. Its door is in that wall, between the Place of Fame shelf and the gym gear. Building it opens and lights it: the door becomes an open frame on both sides of the wall, and you just walk through.
- **The south room** is through a door in the corridor's back wall, which works the same way once built.
- **The north room** starts as a closed metal door in the south room's back wall. Once built, the doorway opens.
- **Doors:** every built doorway has a copy of the emergency wall's working metal door, with the game's own door script, so you open, close and breach it exactly like the hideout's doors (same prompt, hand animations and sounds). Through the hideout's own walls it stands on the side you come from (the gym, the corridor), flush in its frame, and swings towards you; the far side is a frame round the lined gap. It stays as you left it until you leave the hideout. Building the corridor also moves the gym's punching bag 0.5 m right and takes away the blue mat under it, out of the door's way. (If the hideout has no such door to copy, a simpler one that only swings is used.) Through the hideout's own walls it's on the inner side (the corridor, the south room).
- **Lights:** each lamp spot has a fluorescent tube and a hanging bulb, copied from the hideout's own light groups with the game's lamp script, so they follow the **light switch** on the overview like the areas' lamps: tubes for fluorescent tubes, bulbs for bulbs and the Christmas and Halloween lights, and off for off and candles (the rooms have no candles). They hang from the ceiling, in each bay running the same way as the pillar wall.
- **Customization:** the two rooms take the hall's walls, floor and ceiling, and follow the hideout's **customization** when you change them. The corridor is the hideout's own concrete; a concrete block covers the old brick infill that poked into its east end.
- Each room is two bays under a **pillar wall** like the one in the room behind the emergency wall: a concrete beam under the ceiling on a pillar at each end, open between them (its sizes are measured from the game's own).
- **Building:** look at a room's door in first person and press your interact key, or click its **icon on the overview** (the plus badge over every room you can build next, its hexagon as big as the area icons; it glows while its panel is open). Both show what it needs and what you have of each (from your stash, as the hideout's own screens count it), the build time, and why it's locked if it is; the panel's **Build** button starts it. While a room is being built its icon shows the time left.
- On the **overview** a built room shows from above like the hideout's own rooms, so the annex fills in room by room.

| Room | Cost | Build time |
| --- | --- | --- |
| Corridor | 150,000 roubles, 3 packs of nails, 4 bolts, 1 electric drill | 4 h |
| South room | 120,000 roubles, 2 packs of nails, 3 bolts, 1 pack of screws | 3 h |
| North room | 200,000 roubles, 3 packs of nails, 4 bolts, 1 toolset | 5 h |

Build times follow SPT's `hideout.json` `overrideBuildTimeSeconds`, as for the game's own areas. The two rooms are about 12.8 x 6.3 m and 3.1 m high, in the hall's own walls, floor and ceiling, with four lamp spots each (two per bay); the corridor gets three. (Up to 0.5.1 there were four rooms of 6.3 x 6.3 m: the south room keeps the south-west room's build and the north room the north-east room's; the other two are gone.)

Everything is made from the hideout itself each visit: the rooms' walls, floor and ceiling use the game's own materials at their own texture scale, the doors are the metal door from the emergency-wall room, the lamps are copies of the shooting range's ceiling lamps with their lights, and the overview icons use the area icons' own artwork. Nothing is shipped or extracted from the game.

## Settings

`BepInEx/config/jrs.hideoutextensions.cfg`, `General` → `Enabled`: off leaves the hideout exactly as the game has it. A built room stays built in your profile and comes back when it's turned on again.

## How it works

The game builds its hideout areas from a list inside the client, so a new area can't be added. A room extension isn't an area: it's a door the mod places in a wall and a room it builds behind it. The bunker's walls are big meshes the game keeps on the graphics card only; the mod reads the ones a built door goes through back from there and swaps in copies with the doorway cut out.

A build is recorded the way the game records a hideout improvement: the client sends the improvement request (`HideoutImproveArea`) with the spot's id and the items it picked from your stash; the server mod checks the spot may be built and that the items cover its cost, takes them as the game does (money from a stack, other items whole) and stores the build in your profile's hideout improvements. The game's own handler never sees a spot's id. The client reads the build's state from the hideout's improvement records, so a build finishing while you're in the hideout puts the door and room in straight away.

## Adding your own rooms (for modders)

**A spot** is a JSON file: one room, and how you get into it. Put it in a `hideoutextensions` folder inside your own server mod (`user/mods/YourMod/hideoutextensions/*.json`), or in this mod's `spots` folder. Every one found is loaded when the server starts. A room entered through one of the hideout's own walls:

```json
{
  "id": "6a70e5c0a1b2c3d4e5f60001",
  "name": "Annex entrance room",
  "room": { "min": [1.6, 16.95], "max": [7.9, 23.25], "height": 3.1, "lamps": 2 },
  "door": { "position": [7.0, 0.0, 12.78], "facing": 180 },
  "inside": { "position": [7.0, 0.0, 16.95], "facing": 0 },
  "requirements": [ { "template": "5449016a4bdc2d6f028b456f", "count": 150000 } ],
  "buildSeconds": 14400,
  "unlock": { "area": "EmergencyWall", "level": 6, "why": "Tidy up the room behind the emergency wall first" }
}
```

and one reached through a doorway from another room:

```json
{
  "id": "6a70e5c0a1b2c3d4e5f60002",
  "name": "Annex east room",
  "room": { "min": [8.1, 16.95], "max": [14.4, 23.25], "height": 3.1, "lamps": 2 },
  "opening": { "from": "6a70e5c0a1b2c3d4e5f60001", "position": [8.0, 0.0, 20.1], "facing": 270 },
  "requirements": [ { "template": "5449016a4bdc2d6f028b456f", "count": 120000 } ],
  "buildSeconds": 10800
}
```

- `id`: 24 hex characters, unique. It's also the improvement id stored in the profile, so don't change it once people have built it.
- `room`: the inside, in hideout world coordinates: the floor from `min` to `max` (x, z), `floor` height (default 0), `height`, and how many ceiling lamps. Rooms next to each other stand 0.2 m apart (that's the wall between them). Rooms mustn't overlap each other or the hideout.
- `door` + `inside`: a door in one of the hideout's own walls (on the floor, mid-doorway, in the wall's plane; `facing` in degrees about vertical, 0 = +z, 90 = +x, towards where you stand), and the other side of the same doorway (on the far face of the wall, or on the room's own wall). Once built the doorway is cut through everything between the two.
- `opening`: an open doorway from the room of spot `from`, which has to be built first. `position` is on the floor, mid-doorway, halfway through the wall between the two rooms; `facing` points into the `from` room.
- `after` (for a room with a door): a spot that has to be built first, such as the room the door is in. `room.style`: `basement` (raw concrete), `hideout` (the hall's current walls, floor and ceiling, following the customization) or `existing` (a space the hideout's model already has; it only gets lamps).
- `hideoutEdits` (optional): changes to the hideout's own things once the spot is built, each `{ "path": "<root>/<child>/...", "move": [x, y, z] }` (metres from where the game puts it) or `{ "path": ..., "hide": true }`.
- `room.blocks` (optional): plain blocks in the room's walls' material, `{ "min": [x, y, z], "max": [x, y, z] }` in hideout coordinates, to cover parts of the hideout's model that poke into the room.
- `room.pillarWalls` (optional): pillar walls inside the room, each `{ "from": [x, z], "to": [x, z] }` along x or z on the floor: a beam under the ceiling on a pillar at each end (and one in the middle past 9 m), like the room behind the emergency wall's.
- `requirements`: item templates and counts (roubles `5449016a4bdc2d6f028b456f`). `unlock` (optional): a hideout area (the server's `HideoutAreas` names) and the level it must be at.

A spot that's broken (bad id, unknown item, too small) is left out and logged at server start. A server mod can also add spots in code: inject `HideoutExtensions.Server.SpotCatalog` and call `Add(spot)` while loading.

**Furnishing a room** is the client's job. Reference `HideoutExtensions.dll`, add `[BepInDependency("jrs.hideoutextensions", BepInDependency.DependencyFlags.SoftDependency)]` and handle the event:

```csharp
HideoutExtensionsApi.RoomReady += room =>
{
    if (room.Spot.Id != "6a70e5c0a1b2c3d4e5f60004") return;
    // room.Root: on the floor at the room's min x / min z inside corner, axes as the world's (+x east, +z north).
    // The inside spans 0..Size.x, 0..Size.y, 0..Size.z. What you add under Root stays for the visit, even when the
    // room's walls are rebuilt because a neighbour was built; it goes when the hideout unloads.
    var crate = Object.Instantiate(myCrate, room.Root);
    crate.transform.localPosition = new Vector3(1.5f, 0, room.Size.z - 1);
};
```

`HideoutExtensionsApi.AddMovable(new MovableThing { Name, Icon, Root, Check, Placed })` lets players move something of yours from the overview like an area: right-click its icon, it follows the mouse, and `Placed` is called where it's put down (keep and remember it there yourself); `Check` can refuse a spot. K9 Companion's kennel uses it.

`RoomReady` is raised once per room on every hideout visit (the rooms are rebuilt each time the hideout loads) and when a room finishes building while you're there. `HideoutExtensionsApi.Spots` lists every spot with its state; `HideoutExtensionsApi.Rooms` the rooms in the scene.

## Moving the hideout's areas

On the overview, **right-click an area's icon** and the whole area follows your mouse across the floor: everything its icon highlights, with its lights, its click zone, its icon and the view the camera flies to when you open it (they all live under the area in the game's scene). **R** turns it a quarter, **Shift+R** 15°; **left-click** puts it down, **right-click** puts it back where it was, **Backspace** back where the game has it. Where each area stands is kept per profile in `BepInEx/config/jrs.hideoutextensions.layout.<profile id>.json` and set again every visit; deleting the file puts them all back.

An area's lights go with it (its light groups are mostly its own: spotlights aimed at its displays, candles on its shelves, bulbs on its cables), and the spot it leaves gets the annex rooms' lamps (tubes or bulbs, following the light switch, hung from the ceiling there and not over where an area stands now), which go again when you put the area back; `Lights stay in the room` in the config's `Moving areas` section leaves them behind instead. What's built into the bunker for an area stays where it was (the gap it leaves is bare), and what an area hangs on its walls (shelves, the punching bag, paint) goes with it, so it only looks right against walls like the old ones. The emergency wall can't be moved (it carries the room the annex is in); the config's `Moving areas` section turns moving off or adds areas that stay put.

## Hideout performance

The hideout itself is heavy to draw: with every area built it has around 130 lights casting shadows, and each one draws part of the scene again every frame. The game fades a light's shadows out between 12 and 17 m from you and turns them off past that, but some lights were left without it: most of the Halloween lights (on during SPT's Halloween event, 28 October to 9 November) and a few in nearly every area. The mod gives those the same fade, and switches off light haze (volumetric light) the game doesn't cull past 30 m. In the config (`BepInEx/config/jrs.hideoutextensions.cfg`, section `Hideout performance`): `Fade far shadows` (on) and `Light haze distance` (30; 0 = never). Both work without the room extensions, and turning them off puts every light back as the game had it.

The Halloween decorations are a hideout lighting mode the game switches to on your first visit during the event; switching the hideout's lighting back to normal turns them all off, which helps most of all.

## Logs

`BepInEx/LogOutput.log` says what the hideout parts were found as (materials, collider layer, door, lamp), which spots were set up and in what state, and when a build starts or a room is put in. The server log lists the spots it loaded and every build it starts or refuses.

## Limits

- A door through the hideout's own walls is a real doorway: the game keeps those walls on the graphics card only, so the mod reads the meshes the doorway goes through back from there once per visit (about 50 ms), cuts the doorway out of a copy and swaps the copy in (the originals go back when the hideout unloads), then lines the gap between the two walls with concrete. If that fails, or something it can't cut still blocks the doorway, the log says so and the doors fall back to dark doorways that move you through. The doorways between the annex's own rooms are real openings.
- The rooms are plain, lit by two lamps each; furnishing is up to mods (or a later version).
- The game's navmesh was baked before the annex existed, so the mod builds one for its rooms each visit (from their own colliders) and links it to the hideout's through every doorway; pathfinding (K9 Companion's dog) finds its way in. A shut door is in the way, and areas you've moved keep the hideout's old navmesh.
- Not tried in the game yet: the server side passed its checks on a copy of the SPT server; the client side only builds.

## Build

`Shaders\build_shader.ps1` makes `Client\hideoutextensions.bundle` (the overview cap's shader) with Unity 2022.3.43; Unity needs Unity Hub signed in (the free Personal licence). `build.ps1` builds both halves, packs `dist/HideoutExtensions-<version>.zip` and installs into `F:\SPT` (`-SPTPath` to change, `-NoInstall` to only pack).
