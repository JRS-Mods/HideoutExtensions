using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.Hideout;
using EFT.Interactive;
using EFT.UI;
using JsonType;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HideoutExtensions
{
    /// <summary>
    /// Each hideout visit: asks the server mod for the spots, finds what rooms are made of in the hideout scene, and lays
    /// out every spot by what's built: built rooms with open doorways between them, a closed door with a build prompt
    /// where the next room can go (in the hideout's wall, or in a built room's wall), and holes in the overview's black
    /// cap over built rooms. When a build finishes, the layout is done again. Everything goes into the hideout's own
    /// scene, so it's gone when the hideout unloads, and set up again on the next visit.
    /// </summary>
    internal static class Extensions
    {
        private const string Route = "/hideoutextensions/spots";
        internal static readonly List<SpotInfo> SpotList = new List<SpotInfo>();
        internal static readonly List<ExtensionRoom> RoomList = new List<ExtensionRoom>();
        private static readonly List<Site> sites = new List<Site>();
        private static readonly List<Passage> passages = new List<Passage>();
        // The working doors, and how each was left (kept while the layout's made again, until the hideout unloads).
        private static readonly Dictionary<string, Door> doors = new Dictionary<string, Door>();
        private static readonly Dictionary<string, EDoorState> doorStates = new Dictionary<string, EDoorState>();
        private static Player walker;
        private static GameObject root, fixtures;
        private static Transform styleRoot;
        private static SceneParts parts;
        private static float nextLook, lookEvery = 2, nextState, refetchAt, nextWalker, nextWallLook;
        // What rooms are made of, searched for once per hideout (the search goes through the whole scene).
        private static GameObject partsFrom;
        private static SceneParts foundParts;
        // The hideout whose rooms something else removed (they wait for you to open it).
        private static GameObject removedFrom;
        private static int failedScene = -1;
        // Set while the rooms are in another mod's raid copy of the hideout (Hideout Siege): the spots built there, whatever
        // the playing profile's own hideout says (in co-op it's the host's hideout).
        internal static HashSet<string> RaidBuilt;
        internal static bool InRaidCopy => RaidBuilt != null;

        private sealed class Reply { [JsonProperty("ready")] public bool Ready { get; set; } [JsonProperty("spots")] public List<SpotInfo> Spots { get; set; } }

        /// <summary>
        /// Every frame. Switched off (<paramref name="enabled"/> false), nothing new is set up, and what's built stays until
        /// the hideout unloads; what this visit left behind (its navmesh, above all, which would outlast the scene into a
        /// raid) still goes when it does.
        /// </summary>
        internal static void Tick(bool enabled)
        {
            if (root == null)
            {
                if (sites.Count > 0)
                {
                    // Gone with the hideout still there: something else removed the rooms. Setting them up again straight
                    // away made a loop (every couple of seconds, in every menu); they're made again when you open the hideout.
                    removedFrom = partsFrom != null && HideoutScene.Style == partsFrom ? partsFrom : null;
                    if (removedFrom != null) Plugin.Log.LogWarning("The hideout's extension rooms were removed by something else; they'll be set up again when you open the hideout.");
                    Clear();
                }
                if (!enabled || Time.unscaledTime < nextLook) return;
                if (removedFrom != null)
                {
                    if (HideoutScene.Style == removedFrom && !HideoutScene.Shown) return;
                    removedFrom = null;
                }
                // The hideout stays loaded behind every menu: a try that got as far as the server and still left nothing set
                // up is tried again less and less often, rather than every couple of seconds for the rest of the session.
                bool tried = SetUp();
                if (!tried) lookEvery = 2;
                else if (root == null) lookEvery = Mathf.Min(lookEvery * 2, 30);
                nextLook = Time.unscaledTime + lookEvery;
                return;
            }
            if (!enabled) return;
            if (refetchAt > 0 && Time.unscaledTime >= refetchAt) { refetchAt = 0; Refetch(); }
            // Only the teleporting doorways (walls that couldn't be cut) need to know where you are.
            if (passages.Count > 0 && !InRaidCopy)
            {
                if (walker == null && Time.unscaledTime >= nextWalker) { nextWalker = Time.unscaledTime + 1; walker = UnityEngine.Object.FindObjectOfType<HideoutPlayer>(); }
                if (walker != null) foreach (Passage passage in passages) if (passage.Through(walker)) break;
            }
            if (!InRaidCopy)
                try { OverviewIcons.Tick(root.transform, sites); }
                catch (Exception e) { Plugin.Log.LogError("Overview icons: " + e); }
            if (Time.unscaledTime < nextState) return;
            nextState = Time.unscaledTime + 1;
            var changed = sites.Where(s => s.Update()).ToList();
            // A new wall, floor or ceiling from the hideout's customization: the rooms in the hall's style follow it, and
            // a doorway cut through the wall it replaced is cut again. Both look through the whole of STYLE, so they run
            // when the game puts one in (and a slow look besides, in case something else changes them); without the hook,
            // as often as before.
            bool look = !styleHooked || styleChanged > 0 || Time.unscaledTime >= nextStyleLook;
            bool restyled = false, wallsNew = false;
            if (look)
            {
                if (styleChanged > 0) styleChanged--;
                nextStyleLook = Time.unscaledTime + (styleHooked ? 30 : 0);
                try { restyled = styleRoot != null && parts.Restyle(styleRoot); }
                catch (Exception e) { Plugin.Log.LogWarning("Couldn't check the hideout's customization: " + e.Message); }
                try { wallsNew = styleRoot != null && Time.unscaledTime >= nextWallLook && WallCut.Stale(styleRoot); }
                catch (Exception e) { Plugin.Log.LogWarning("Couldn't check the hideout's walls: " + e.Message); }
                if (Time.unscaledTime >= nextWallLook) nextWallLook = Time.unscaledTime + 2;
            }
            if (restyled || wallsNew)
            {
                if (restyled) { Plugin.Log.LogInfo("The hideout's customization changed; the rooms follow it."); styleVersion++; }
                if (wallsNew) { Plugin.Log.LogInfo("New walls stand where the doorways were cut; cutting them again."); WallCut.Invalidate(); }
                Layout();
            }
            if (changed.Count == 0) return;
            Layout();
            foreach (Site site in changed.Where(s => s.Phase == Site.State.Built && s.WasBuilding))
                NotificationManager.DisplayMessageNotification(site.Spot.Name + " is ready.");
        }

        // Looks left at the hideout's customization: the game just put a wall, floor or ceiling in (two, a second apart,
        // so the wall check's own 2 s wait can't swallow it).
        private static int styleChanged = 2;
        private static float nextStyleLook;
        private static bool styleHooked;

        internal static void PatchStyle(HarmonyLib.Harmony harmony)
        {
            var set = HarmonyLib.AccessTools.Method(typeof(EFT.Hideout.HideoutCustomizationItemsInstaller), nameof(EFT.Hideout.HideoutCustomizationItemsInstaller.SetItem))
                      ?? throw new MissingMethodException("HideoutCustomizationItemsInstaller", "SetItem");
            harmony.Patch(set, postfix: new HarmonyLib.HarmonyMethod(typeof(Extensions), nameof(StyleSet)));
            styleHooked = true;
        }

        private static void StyleSet() => styleChanged = 3;

        /// <summary>Asks the server again in a moment (after a build starts, for its finishing time).</summary>
        internal static void RefetchSoon() => refetchAt = Time.unscaledTime + 2;

        internal static Site Find(string id) => sites.FirstOrDefault(s => s.Spot.Id == id);
        // The spots are in from the server and laid out for this visit.
        internal static bool Ready => root != null;
        internal const string RootName = "Hideout extensions";
        // Each room's walls as last built (what they were built round), and the hideout's look's count of changes.
        private static readonly Dictionary<string, string> shellKeys = new Dictionary<string, string>();
        private static int styleVersion;

        // No rooms this visit, and none coming: room extensions are switched off, or were given up on for this hideout
        // (no server mod, or the hideout lacks what rooms are made of).
        internal static bool Off => !Plugin.Enabled.Value || (root == null && HideoutScene.Style is GameObject style && style.scene.handle == failedScene);

        /// <summary>What rooms are made of this visit (null until the hideout's set up, or without the server mod).</summary>
        internal static SceneParts Parts => parts;

        // True if it got as far as asking the server.
        private static bool SetUp()
        {
            GameObject style = HideoutScene.Style;
            if (style == null || style.transform.Find("Wall") == null || style.scene.handle == failedScene) return false;
            SetUpIn(style);
            return true;
        }

        /// <summary>
        /// The hideout's scene loaded as another mod's raid map: the rooms <paramref name="built"/> lists, with their doors and
        /// lamps, and none of what's only for the hideout itself (build prompts, the overview's icons and cap, the rooms' own
        /// navmesh: the raid makes its own). True if the rooms are in.
        /// </summary>
        /// <summary>A raid copy's own room (see <see cref="HideoutExtensionsApi.AddRaidRoom"/>), built straight away. Null if it's in.</summary>
        internal static string AddRaidRoom(SpotInfo spot)
        {
            if (!InRaidCopy || root == null) return "there's no raid copy of the hideout";
            if (spot?.Room?.Min == null || spot.Room.Max == null || string.IsNullOrEmpty(spot.Id)) return "it has no id or room";
            if (sites.Any(s => s.Spot.Id == spot.Id)) return "there's a room with that id already";
            if (spot.Opening != null && Find(spot.Opening.From)?.Phase != Site.State.Built) return "the room it opens from isn't built";
            spot.State = "built";
            SpotList.Add(spot);
            RaidBuilt.Add(spot.Id);
            var site = new Site(spot);
            sites.Add(site);
            site.Update();
            Layout();
            Plugin.Log.LogInfo($"'{spot.Name}' put in for this raid: {site.Room?.Size.x:0.#} x {site.Room?.Size.z:0.#} m.");
            return null;
        }

        internal static bool SetUpRaidCopy(GameObject style, IEnumerable<string> built)
        {
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            Clear();
            RaidBuilt = new HashSet<string>(built ?? Enumerable.Empty<string>());
            SetUpIn(style);
            if (root == null) RaidBuilt = null;
            return root != null;
        }

        private static void SetUpIn(GameObject style)
        {
            try
            {
                Reply reply = Fetch();
                // No answer: the server mod isn't there, so not again this visit. Not ready: no character yet, ask later.
                if (reply == null) { failedScene = style.scene.handle; return; }
                if (!reply.Ready) return;
                SpotList.Clear(); SpotList.AddRange(reply.Spots ?? new List<SpotInfo>());
                if (partsFrom != style || foundParts == null) { foundParts = SceneParts.Find(style.transform); partsFrom = style; }
                parts = foundParts;
                styleRoot = style.transform;
                if (!parts.Complete)
                {
                    failedScene = style.scene.handle;
                    Plugin.Log.LogError("The hideout is missing parts a room is made of (see above); no room extensions this visit.");
                    return;
                }
                root = new GameObject(RootName);
                SceneManager.MoveGameObjectToScene(root, style.scene);
                foreach (SpotInfo spot in SpotList) sites.Add(new Site(spot));
                foreach (Site site in sites) site.Update();
                Layout();
                Plugin.Log.LogInfo($"Room extensions in {(InRaidCopy ? "the raid's copy of the hideout" : "the hideout")}: {string.Join(", ", sites.Select(s => $"{s.Spot.Name} ({s.Phase})"))}.");
            }
            catch (Exception e)
            {
                failedScene = style.scene.handle;
                Plugin.Log.LogError("Room extensions couldn't be set up this visit: " + e);
                WallCut.Restore();
                if (root != null) UnityEngine.Object.Destroy(root);
                root = null; sites.Clear(); RoomList.Clear();
            }
        }

        /// <summary>Everything but the rooms' own frames (which mods may have furnished) is made again from what's built.</summary>
        private static void Layout()
        {
            passages.Clear();
            foreach (var door in doors) if (door.Value != null) doorStates[door.Key] = door.Value.DoorState == EDoorState.Open ? EDoorState.Open : EDoorState.Shut;
            doors.Clear();
            if (fixtures != null) Gone(fixtures);
            fixtures = new GameObject("Extension doors and prompts");
            fixtures.transform.SetParent(root.transform, false);
            var built = sites.Where(s => s.Phase == Site.State.Built).ToList();

            // Changes to the hideout's own things that spots ask for (moved out of a doorway's way, or hidden): as soon as a
            // spot's door is there, or the door would stand in the things it's replacing.
            foreach (Site site in sites.Where(s => s.Phase == Site.State.Built || (s.Offered && s.Spot.Door != null))) HideoutEdits.Apply(styleRoot, site.Spot);

            // Built doors through the hideout's own walls: cut for real, or else (see the log) doorways that move you through.
            var throughWalls = built.Where(s => s.Spot.Door != null && s.Spot.Inside != null).ToList();
            bool cut = WallCut.Apply(root.scene, root.transform, throughWalls.SelectMany(s => Ways(s.Spot).Select(w => WallCut.Box(s.Spot.Name, Point(w.door.Position), w.door.Facing,
                Point(w.inside.Position), w.door.Width > 0 ? w.door.Width : Room.OpeningWidth, w.door.Height > 0 ? w.door.Height : Room.OpeningHeight))).ToList());

            // The areas' prompt zones out of the doorways (built doors, and the build prompts where doors will be).
            try
            {
                DoorwayClear.Apply(root.scene, sites.Where(s => (s.Phase == Site.State.Built || s.Offered) && s.Spot.Door != null)
                    .SelectMany(s => Ways(s.Spot)).Where(w => w.door != null)
                    .Select(w => (Point(w.door.Position), w.door.Facing, w.door.Width > 0 ? w.door.Width : Room.OpeningWidth, w.door.Height > 0 ? w.door.Height : Room.OpeningHeight)).ToList());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't clear the areas' prompts out of the doorways: " + e.Message); }

            foreach (Site site in built)
            {
                bool fresh = site.Room == null;
                if (fresh)
                {
                    var frame = new GameObject("Extension room " + site.Spot.Name).transform;
                    frame.SetParent(root.transform, false);
                    frame.position = Room.Corner(site.Spot);
                    site.Room = new ExtensionRoom { Spot = site.Spot, Root = frame, Size = Room.Size(site.Spot) };
                }
                var doorways = new List<Doorway>();
                if (site.Spot.Opening != null) doorways.Add(new Doorway { Centre = Point(site.Spot.Opening.Position), Owner = true, Width = site.Spot.Opening.Width, Height = site.Spot.Opening.Height });
                foreach (Site child in built.Where(c => c.Spot.Opening?.From == site.Spot.Id))
                    doorways.Add(new Doorway { Centre = Point(child.Spot.Opening.Position), Owner = false, Width = child.Spot.Opening.Width, Height = child.Spot.Opening.Height });
                // Doorways to rooms joined on once both are built.
                foreach (OpeningInfo link in Links(site.Spot))
                    if (Find(link.From)?.Phase == Site.State.Built) doorways.Add(new Doorway { Centre = Point(link.Position), Owner = true });
                foreach (Site other in built)
                    foreach (OpeningInfo link in Links(other.Spot))
                        if (link.From == site.Spot.Id) doorways.Add(new Doorway { Centre = Point(link.Position), Owner = false });
                // A cut door's side in this room's own wall (the doorway lines the gap itself).
                if (cut)
                    foreach (Site door in throughWalls)
                        foreach (var way in Ways(door.Spot))
                            foreach (DoorInfo side in new[] { way.door, way.inside })
                                if (InWall(site.Spot, Point(side.Position))) doorways.Add(new Doorway { Centre = Point(side.Position), Owner = false, Width = way.door.Width, Height = way.door.Height });
                // Walls, lamps and pillars are only built again when what they're built round changed (a doorway into
                // this room, the hideout's look): another room being built leaves this one as it is.
                string shellKey = styleVersion + "|" + cut + "|" + string.Join(";", doorways.Select(d => $"{d.Centre}/{d.Owner}/{d.Width}/{d.Height}"));
                Transform old = site.Room.Root.Find("Shell");
                if (old == null || !shellKeys.TryGetValue(site.Spot.Id, out string had) || had != shellKey)
                {
                    if (old != null) Gone(old.gameObject);
                    var shell = new GameObject("Shell").transform;
                    shell.SetParent(site.Room.Root, false);
                    Room.Build(site.Spot, parts, shell, doorways);
                    shellKeys[site.Spot.Id] = shellKey;
                }
                if (fresh)
                {
                    RoomList.Add(site.Room);
                    HideoutExtensionsApi.Raise(site.Room);
                    Plugin.Log.LogInfo($"'{site.Spot.Name}' put in: {site.Room.Size.x:0.#} x {site.Room.Size.z:0.#} m at {site.Room.Root.position}, {doorways.Count} doorway(s).");
                }
            }

            foreach (Site site in sites)
            {
                SpotInfo spot = site.Spot;
                if (spot.Door != null && site.Phase == Site.State.Built && spot.Inside != null)
                {
                    int number = 0;
                    foreach (var way in Ways(spot))
                    {
                        string key = number++ == 0 ? spot.Id : spot.Id + "-" + number;
                        // A bare opening: nothing in it but the lining, so the room reads as part of the one it opens from.
                        if (cut && way.door.Open) { Passage.Line(fixtures.transform, parts, way.door, way.inside); continue; }
                        // Built: an open frame on each side of the hideout's wall, and the doorway through it; or, uncut, a dark
                        // doorway on each side, and walking into one puts you out of the other.
                        foreach (DoorInfo side in new[] { way.door, way.inside })
                        {
                            if (cut && side == way.door)
                            {
                                // Cut: a door you open, close or breach on the side you come from, flush in its frame
                                // (the other side is a frame round the lined gap), swinging towards you.
                                Vector3 a = Point(way.door.Position), b = Point(way.inside.Position);
                                if (!Working(key, Point(side.Position), side.Facing))
                                    SwingDoor.Make(parts.Door(fixtures.transform, Point(side.Position), side.Facing), site, key, parts, (a + b) / 2, side.Facing, Vector3.Distance(a, b));
                                continue;
                            }
                            parts.Frame(fixtures.transform, Point(side.Position), side.Facing);
                            if (!cut) Passage.Opening(fixtures.transform, Point(side.Position), side.Facing);
                        }
                        if (cut) Passage.Line(fixtures.transform, parts, way.door, way.inside);
                        else
                        {
                            passages.Add(new Passage(site, way.door, way.inside));
                            passages.Add(new Passage(site, way.inside, way.door));
                        }
                    }
                }
                else if (spot.Door != null && site.Offered)
                {
                    parts.Door(fixtures.transform, Point(spot.Door.Position), spot.Door.Facing);
                    if (!InRaidCopy) site.Zone(fixtures.transform, Point(spot.Door.Position), spot.Door.Facing, 0, Role.Build);
                }
                else if (spot.Opening != null)
                {
                    Vector3 at = Point(spot.Opening.Position);
                    // The door stands flush in the face of the wall on the room it's reached from, as through the
                    // hideout's walls: halfway through the wall, its leaf was behind the face (all of it while the wall
                    // isn't cut yet, but for the hinges and handle).
                    Vector3 flush = at + Quaternion.Euler(0, spot.Opening.Facing, 0) * Vector3.forward * (Room.Gap / 2);
                    if (site.Phase == Site.State.Built && spot.Opening.Open) { }
                    else if (site.Phase == Site.State.Built)
                    {
                        // A door you open, close or breach, swinging into the room it's reached from.
                        if (!Working(spot.Id, flush, spot.Opening.Facing))
                        {
                            Transform door = parts.Door(fixtures.transform, flush, spot.Opening.Facing);
                            if (SwingDoor.Make(door, site, spot.Id, parts, at, spot.Opening.Facing, Room.Gap) == null)
                            {
                                Gone(door.gameObject);
                                parts.Frame(fixtures.transform, flush, spot.Opening.Facing);
                            }
                        }
                    }
                    else if (Find(spot.Opening.From)?.Phase == Site.State.Built)
                    {
                        // Where the next room goes: a closed door in the built room's wall, and its build prompt.
                        parts.Door(fixtures.transform, flush, spot.Opening.Facing);
                        if (!InRaidCopy) site.Zone(fixtures.transform, at, spot.Opening.Facing, Room.Gap / 2, Role.Build);
                    }
                }

                if (site.Phase == Site.State.Built && spot.Room.RangeMarks?.Panel != null && !InRaidCopy) RangeControl.Panel(fixtures.transform, site, spot.Room.RangeMarks);

                // Doorways between rooms that are both built, whichever came first.
                if (site.Phase == Site.State.Built)
                {
                    int number = 0;
                    foreach (OpeningInfo link in Links(spot))
                    {
                        string key = spot.Id + "-link" + number++;
                        if (Find(link.From)?.Phase != Site.State.Built) continue;
                        Vector3 at = Point(link.Position);
                        Vector3 flush = at + Quaternion.Euler(0, link.Facing, 0) * Vector3.forward * (Room.Gap / 2);
                        if (Working(key, flush, link.Facing)) continue;
                        Transform door = parts.Door(fixtures.transform, flush, link.Facing);
                        if (SwingDoor.Make(door, site, key, parts, at, link.Facing, Room.Gap) != null) continue;
                        Gone(door.gameObject);
                        parts.Frame(fixtures.transform, flush, link.Facing);
                    }
                }
            }

            // In a raid copy, the raid makes its own navmesh, and there's no overview.
            if (InRaidCopy) return;

            // A navmesh over the built rooms, joined to the hideout's through every real doorway (the dog finds its way in).
            var ways = new List<(Vector3, float, float)>();
            if (cut)
                foreach (Site site in throughWalls)
                    foreach (var way in Ways(site.Spot))
                        ways.Add((Point(way.door.Position), way.door.Facing, Vector3.Distance(Point(way.door.Position), Point(way.inside.Position))));
            foreach (Site site in built.Where(s => s.Spot.Opening != null))
                ways.Add((Point(site.Spot.Opening.Position), site.Spot.Opening.Facing, 0f));
            foreach (Site site in built)
                foreach (OpeningInfo link in Links(site.Spot))
                    if (Find(link.From)?.Phase == Site.State.Built) ways.Add((Point(link.Position), link.Facing, 0f));
            Navigation.Build(root.transform, built.Select(s => s.Spot).ToList(), ways);

            // Far rooms: the overview's camera can get to them, and the areas they open off stay drawn from inside them.
            try { Reach.Apply(root.scene, sites.Where(s => s.Phase == Site.State.Built || s.Offered).Select(s => s.Spot).ToList(), built.Where(s => cut && s.Spot.Door?.Open == true).Select(s => s.Spot).ToList()); }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't widen the overview's reach for the rooms: " + e); }

            OverviewCap.Apply(parts.Cap, built.Select(s => new Rect(s.Spot.Room.Min[0], s.Spot.Room.Min[1],
                s.Spot.Room.Max[0] - s.Spot.Room.Min[0], s.Spot.Room.Max[1] - s.Spot.Room.Min[1])).ToList());
        }

        internal static Vector3 Point(float[] p) => new Vector3(p[0], p[1], p[2]);

        // Every way through the hideout's walls into a spot's room: its door, then any more it has.
        private static IEnumerable<(DoorInfo door, DoorInfo inside)> Ways(SpotInfo spot)
        {
            yield return (spot.Door, spot.Inside);
            if (spot.MoreDoors == null) yield break;
            foreach (DoorPairInfo more in spot.MoreDoors) yield return (more.Door, more.Inside);
        }

        private static IEnumerable<OpeningInfo> Links(SpotInfo spot) => spot.Links ?? Enumerable.Empty<OpeningInfo>();

        // One of the hideout's working doors in a doorway, as it was left; false if there's none to copy (or it failed).
        private static bool Working(string key, Vector3 at, float facing)
        {
            try
            {
                doorStates.TryGetValue(key, out EDoorState state);
                Door door = parts.LiveDoor(fixtures.transform, at, facing, key, state == EDoorState.Open ? EDoorState.Open : EDoorState.Shut);
                if (door == null) return false;
                doors[key] = door;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("A working door couldn't be put in, so a simpler one is: " + e);
                return false;
            }
        }

        // Destroy waits for the end of the frame, and until then the old walls and doors still stop anything that looks for
        // them in the same frame (a raid copy's floor being walked straight after a room's added); switched off, they don't.
        private static void Gone(GameObject thing)
        {
            thing.SetActive(false);
            UnityEngine.Object.Destroy(thing);
        }

        // On one of the spot's own walls (a room the mod builds, not one the hideout has).
        private static bool InWall(SpotInfo spot, Vector3 p)
        {
            if (spot.Room.Style == "existing") return false;
            const float near = .3f;
            float x0 = spot.Room.Min[0], z0 = spot.Room.Min[1], x1 = spot.Room.Max[0], z1 = spot.Room.Max[1];
            if (p.x < x0 - near || p.x > x1 + near || p.z < z0 - near || p.z > z1 + near) return false;
            return Mathf.Min(Mathf.Min(Mathf.Abs(p.x - x0), Mathf.Abs(p.x - x1)), Mathf.Min(Mathf.Abs(p.z - z0), Mathf.Abs(p.z - z1))) <= near;
        }

        private static Reply Fetch()
        {
            string json = RequestHandler.PostJson(Route, "{}");
            if (string.IsNullOrEmpty(json)) { Plugin.Log.LogWarning("The Hideout Extensions server mod didn't answer; is it installed?"); return null; }
            return JsonConvert.DeserializeObject<Reply>(json);
        }

        private static void Refetch()
        {
            try
            {
                Reply reply = Fetch();
                if (reply?.Spots == null) return;
                foreach (SpotInfo fresh in reply.Spots)
                {
                    SpotInfo known = SpotList.FirstOrDefault(s => s.Id == fresh.Id);
                    if (known == null) continue;
                    known.State = fresh.State; known.CompleteAt = fresh.CompleteAt; known.Locked = fresh.Locked;
                    // A build the server refused (its reason is in the game's error message): its prompt is back.
                    if (fresh.State == "none") Find(fresh.Id)?.NotStarted();
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't ask the server for the spots' state: " + e.Message); }
        }

        private static void Clear()
        {
            styleChanged = 3;
            WallCut.Forget();
            DoorwayClear.Forget();
            shellKeys.Clear();
            Navigation.Clear();
            SwingDoor.Forget();
            HideoutEdits.Forget();
            doors.Clear(); doorStates.Clear();
            Lighting.Clear();
            RangeControl.Clear();
            styleRoot = null;
            sites.Clear(); RoomList.Clear(); SpotList.Clear(); passages.Clear(); walker = null;
            fixtures = null; parts = null;
            failedScene = -1;
            RaidBuilt = null;
        }
    }

    /// <summary>One spot in this hideout visit: its build state, its room once built, and what you can do there.</summary>
    internal sealed class Site
    {
        internal enum State { None, Building, Built }

        internal readonly SpotInfo Spot;
        internal State Phase { get; private set; } = (State)(-1);
        internal bool WasBuilding { get; private set; }
        internal ExtensionRoom Room;
        private bool started;

        public Site(SpotInfo spot) { Spot = spot; }

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>Takes the build's state again; true when the layout has to change (it became built, or anything at first).</summary>
        internal bool Update()
        {
            State now = Current(out _);
            if (now == Phase) return false;
            bool layout = Phase == State.Built || now == State.Built || Phase == (State)(-1);
            WasBuilding = Phase == State.Building;
            Phase = now;
            return layout;
        }

        /// <summary>The build's state: the hideout's own improvement records first, then what the server said.</summary>
        internal State Current(out long completeAt)
        {
            completeAt = Spot.CompleteAt;
            if (Extensions.RaidBuilt != null) return Extensions.RaidBuilt.Contains(Spot.Id) ? State.Built : State.None;
            var records = Singleton<HideoutRepresentation>.Instantiated ? Singleton<HideoutRepresentation>.Instance.ImprovementController?._improvementDataBy : null;
            if (records != null && records.TryGetValue(Spot.Id, out ImprovementData data))
            {
                completeAt = data.CompleteTimestamp;
                return data.Completed || (data.CompleteTimestamp > 0 && data.CompleteTimestamp <= Now) ? State.Built : State.Building;
            }
            if (Spot.State == "built" || (Spot.State == "building" && Spot.CompleteAt > 0 && Spot.CompleteAt <= Now)) return State.Built;
            return Spot.State == "building" || started ? State.Building : State.None;
        }

        /// <summary>The server didn't start it after all. (Once it has, the hideout's own records say so first.)</summary>
        internal void NotStarted() => started = false;

        /// <summary>Why it can't be started, or null: the room it's reached from not built yet, then the server's rule.</summary>
        internal string Locked()
        {
            if (Prerequisite != null)
            {
                Site from = Extensions.Find(Prerequisite);
                if (from != null && from.Phase != State.Built) return $"Build the {from.Spot.Name} first";
                // The server's reason may have been that room, which has been built since.
                if (Spot.Locked != null && Spot.Locked.StartsWith("Build the ", StringComparison.Ordinal)) return null;
            }
            return Spot.Locked;
        }

        /// <summary>The spot that has to be built first: the room its doorway comes from, or its door's "after".</summary>
        internal string Prerequisite => Spot.Opening?.From ?? Spot.After;

        /// <summary>Its build prompt is showing (in the hideout's wall, or in a built neighbour's): it can be built next.</summary>
        internal bool Offered
        {
            get
            {
                if (Phase == State.Built) return false;
                return Prerequisite == null || Extensions.Find(Prerequisite)?.Phase == State.Built;
            }
        }

        // What your interact key finds: a box in front of the door, its face out from the wall.
        internal void Zone(Transform parent, Vector3 floorPoint, float facing, float inset, Role role)
        {
            Quaternion face = Quaternion.Euler(0, facing, 0);
            Vector3 size = new Vector3(1.3f, 2.2f, .6f);
            var go = new GameObject($"Extension {role} {Spot.Name}") { layer = LayerMask.NameToLayer("Interactive") };
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(floorPoint + face * new Vector3(0, size.y / 2, inset + size.z / 2), face);
            var box = go.AddComponent<BoxCollider>(); box.isTrigger = true; box.size = size;
            var interactive = go.AddComponent<ExtensionInteractive>(); interactive.Site = this; interactive.Role = role;
        }

        /// <summary>The build's cost, checked against what's in your stash now (as the hideout's own screens check it).</summary>
        internal ItemRequirement[] Requirements()
        {
            var list = Spot.Requirements.Select(c => new ItemRequirement { TemplateId = c.Template, IntCount = c.Count }).ToArray();
            if (Singleton<HideoutRepresentation>.Instantiated)
                foreach (ItemRequirement r in list) r.Test(Singleton<HideoutRepresentation>.Instance.AllStashItems);
            return list;
        }

        /// <summary>Starts the build the way the hideout starts an improvement: the items go to the server, which takes them.</summary>
        internal void Build()
        {
            try
            {
                HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instance;
                ItemRequirement[] cost = Requirements();
                string locked = Locked();
                if (locked != null) { NotificationManager.DisplayWarningNotification(locked); return; }
                if (!cost.All(r => r.Fulfilled)) { NotificationManager.DisplayWarningNotification("You don't have everything it needs in your stash."); return; }
                var handover = hideout.GetItemReferences(cost).Select(r => new ItemToHandover(r.Item, r.Count)).ToList();
                hideout._session.StartImprovement(Spot.Id, EAreaType.Stash, handover);
                started = true;
                Extensions.RefetchSoon();
                NotificationManager.DisplayMessageNotification($"Building {Spot.Name}: ready in {Duration(Spot.BuildSeconds)}.");
                Plugin.Log.LogInfo($"Started building '{Spot.Name}' with {handover.Count} item stack(s) from the stash.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Couldn't start building '{Spot.Name}': {e}");
                NotificationManager.DisplayWarningNotification("Couldn't start building " + Spot.Name + " (see the log).");
            }
        }

        internal static string Duration(long seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : t.TotalMinutes >= 1 ? $"{t.Minutes} min" : $"{t.Seconds} s";
        }
    }
}
