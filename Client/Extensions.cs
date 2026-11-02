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
        private static float nextLook, lookEvery = 2, nextState, refetchAt, nextWalker;
        // What rooms are made of, searched for once per hideout (the search goes through the whole scene).
        private static GameObject partsFrom;
        private static SceneParts foundParts;
        // The hideout whose rooms something else removed (they wait for you to open it).
        private static GameObject removedFrom;
        private static int failedScene = -1;

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
            if (passages.Count > 0)
            {
                if (walker == null && Time.unscaledTime >= nextWalker) { nextWalker = Time.unscaledTime + 1; walker = UnityEngine.Object.FindObjectOfType<HideoutPlayer>(); }
                if (walker != null) foreach (Passage passage in passages) if (passage.Through(walker)) break;
            }
            try { OverviewIcons.Tick(root.transform, sites); }
            catch (Exception e) { Plugin.Log.LogError("Overview icons: " + e); }
            if (Time.unscaledTime < nextState) return;
            nextState = Time.unscaledTime + 1;
            var changed = sites.Where(s => s.Update()).ToList();
            // A new wall, floor or ceiling from the hideout's customization: the rooms in the hall's style follow it.
            bool restyled = false;
            try { restyled = styleRoot != null && parts.Restyle(styleRoot); }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't check the hideout's customization: " + e.Message); }
            if (restyled) { Plugin.Log.LogInfo("The hideout's customization changed; the rooms follow it."); Layout(); }
            if (changed.Count == 0) return;
            Layout();
            foreach (Site site in changed.Where(s => s.Phase == Site.State.Built && s.WasBuilding))
                NotificationManager.DisplayMessageNotification(site.Spot.Name + " is ready.");
        }

        /// <summary>Asks the server again in a moment (after a build starts, for its finishing time).</summary>
        internal static void RefetchSoon() => refetchAt = Time.unscaledTime + 2;

        internal static Site Find(string id) => sites.FirstOrDefault(s => s.Spot.Id == id);
        // The spots are in from the server and laid out for this visit.
        internal static bool Ready => root != null;

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
            try
            {
                Reply reply = Fetch();
                // No answer: the server mod isn't there, so not again this visit. Not ready: no character yet, ask later.
                if (reply == null) { failedScene = style.scene.handle; return true; }
                if (!reply.Ready) return true;
                SpotList.Clear(); SpotList.AddRange(reply.Spots ?? new List<SpotInfo>());
                if (partsFrom != style || foundParts == null) { foundParts = SceneParts.Find(style.transform); partsFrom = style; }
                parts = foundParts;
                styleRoot = style.transform;
                if (!parts.Complete)
                {
                    failedScene = style.scene.handle;
                    Plugin.Log.LogError("The hideout is missing parts a room is made of (see above); no room extensions this visit.");
                    return true;
                }
                root = new GameObject("Hideout extensions");
                SceneManager.MoveGameObjectToScene(root, style.scene);
                foreach (SpotInfo spot in SpotList) sites.Add(new Site(spot));
                foreach (Site site in sites) site.Update();
                Layout();
                Plugin.Log.LogInfo($"Room extensions in the hideout: {string.Join(", ", sites.Select(s => $"{s.Spot.Name} ({s.Phase})"))}.");
            }
            catch (Exception e)
            {
                failedScene = style.scene.handle;
                Plugin.Log.LogError("Room extensions couldn't be set up this visit: " + e);
                WallCut.Restore();
                if (root != null) UnityEngine.Object.Destroy(root);
                root = null; sites.Clear(); RoomList.Clear();
            }
            return true;
        }

        /// <summary>Everything but the rooms' own frames (which mods may have furnished) is made again from what's built.</summary>
        private static void Layout()
        {
            passages.Clear();
            foreach (var door in doors) if (door.Value != null) doorStates[door.Key] = door.Value.DoorState == EDoorState.Open ? EDoorState.Open : EDoorState.Shut;
            doors.Clear();
            if (fixtures != null) UnityEngine.Object.Destroy(fixtures);
            fixtures = new GameObject("Extension doors and prompts");
            fixtures.transform.SetParent(root.transform, false);
            var built = sites.Where(s => s.Phase == Site.State.Built).ToList();

            // Changes to the hideout's own things that built spots ask for (moved out of a doorway's way, or hidden).
            foreach (Site site in built) HideoutEdits.Apply(styleRoot, site.Spot);

            // Built doors through the hideout's own walls: cut for real, or else (see the log) doorways that move you through.
            var throughWalls = built.Where(s => s.Spot.Door != null && s.Spot.Inside != null).ToList();
            bool cut = WallCut.Apply(root.scene, root.transform, throughWalls.Select(s => WallCut.Box(s.Spot.Name, Point(s.Spot.Door.Position), s.Spot.Door.Facing,
                Point(s.Spot.Inside.Position), Room.OpeningWidth, Room.OpeningHeight)).ToList());

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
                Transform old = site.Room.Root.Find("Shell");
                if (old != null) UnityEngine.Object.Destroy(old.gameObject);
                var shell = new GameObject("Shell").transform;
                shell.SetParent(site.Room.Root, false);
                var doorways = new List<Doorway>();
                if (site.Spot.Opening != null) doorways.Add(new Doorway { Centre = Point(site.Spot.Opening.Position), Owner = true });
                foreach (Site child in built.Where(c => c.Spot.Opening?.From == site.Spot.Id))
                    doorways.Add(new Doorway { Centre = Point(child.Spot.Opening.Position), Owner = false });
                // A cut door's side in this room's own wall (the doorway lines the gap itself).
                if (cut)
                    foreach (Site door in throughWalls)
                        foreach (DoorInfo side in new[] { door.Spot.Door, door.Spot.Inside })
                            if (InWall(site.Spot, Point(side.Position))) doorways.Add(new Doorway { Centre = Point(side.Position), Owner = false });
                Room.Build(site.Spot, parts, shell, doorways);
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
                    // Built: an open frame on each side of the hideout's wall, and the doorway through it; or, uncut, a dark
                    // doorway on each side, and walking into one puts you out of the other.
                    foreach (DoorInfo side in new[] { spot.Door, spot.Inside })
                    {
                        if (cut && side == spot.Door)
                        {
                            // Cut: a door you open, close or breach on the side you come from, flush in its frame
                            // (the other side is a frame round the lined gap), swinging towards you.
                            Vector3 a = Point(spot.Door.Position), b = Point(spot.Inside.Position);
                            if (!Working(spot.Id, Point(side.Position), side.Facing))
                                SwingDoor.Make(parts.Door(fixtures.transform, Point(side.Position), side.Facing), site, spot.Id, parts, (a + b) / 2, side.Facing, Vector3.Distance(a, b));
                            continue;
                        }
                        parts.Frame(fixtures.transform, Point(side.Position), side.Facing);
                        if (!cut) Passage.Opening(fixtures.transform, Point(side.Position), side.Facing);
                    }
                    if (cut) Passage.Line(fixtures.transform, parts, spot.Door, spot.Inside);
                    else
                    {
                        passages.Add(new Passage(site, spot.Door, spot.Inside));
                        passages.Add(new Passage(site, spot.Inside, spot.Door));
                    }
                }
                else if (spot.Door != null && site.Offered)
                {
                    parts.Door(fixtures.transform, Point(spot.Door.Position), spot.Door.Facing);
                    site.Zone(fixtures.transform, Point(spot.Door.Position), spot.Door.Facing, 0, Role.Build);
                }
                else if (spot.Opening != null)
                {
                    Vector3 at = Point(spot.Opening.Position);
                    // The door stands flush in the face of the wall on the room it's reached from, as through the
                    // hideout's walls: halfway through the wall, its leaf was behind the face (all of it while the wall
                    // isn't cut yet, but for the hinges and handle).
                    Vector3 flush = at + Quaternion.Euler(0, spot.Opening.Facing, 0) * Vector3.forward * (Room.Gap / 2);
                    if (site.Phase == Site.State.Built)
                    {
                        // A door you open, close or breach, swinging into the room it's reached from.
                        if (!Working(spot.Id, flush, spot.Opening.Facing))
                        {
                            Transform door = parts.Door(fixtures.transform, flush, spot.Opening.Facing);
                            if (SwingDoor.Make(door, site, spot.Id, parts, at, spot.Opening.Facing, Room.Gap) == null)
                            {
                                UnityEngine.Object.Destroy(door.gameObject);
                                parts.Frame(fixtures.transform, flush, spot.Opening.Facing);
                            }
                        }
                    }
                    else if (Find(spot.Opening.From)?.Phase == Site.State.Built)
                    {
                        // Where the next room goes: a closed door in the built room's wall, and its build prompt.
                        parts.Door(fixtures.transform, flush, spot.Opening.Facing);
                        site.Zone(fixtures.transform, at, spot.Opening.Facing, Room.Gap / 2, Role.Build);
                    }
                }
            }

            // A navmesh over the built rooms, joined to the hideout's through every real doorway (the dog finds its way in).
            var ways = new List<(Vector3, float, float)>();
            if (cut)
                foreach (Site site in throughWalls)
                    ways.Add((Point(site.Spot.Door.Position), site.Spot.Door.Facing, Vector3.Distance(Point(site.Spot.Door.Position), Point(site.Spot.Inside.Position))));
            foreach (Site site in built.Where(s => s.Spot.Opening != null))
                ways.Add((Point(site.Spot.Opening.Position), site.Spot.Opening.Facing, 0f));
            Navigation.Build(root.transform, built.Select(s => s.Spot).ToList(), ways);

            OverviewCap.Apply(parts.Cap, built.Select(s => new Rect(s.Spot.Room.Min[0], s.Spot.Room.Min[1],
                s.Spot.Room.Max[0] - s.Spot.Room.Min[0], s.Spot.Room.Max[1] - s.Spot.Room.Min[1])).ToList());
        }

        internal static Vector3 Point(float[] p) => new Vector3(p[0], p[1], p[2]);

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
            WallCut.Forget();
            Navigation.Clear();
            SwingDoor.Forget();
            HideoutEdits.Forget();
            doors.Clear(); doorStates.Clear();
            Lighting.Clear();
            styleRoot = null;
            sites.Clear(); RoomList.Clear(); SpotList.Clear(); passages.Clear(); walker = null;
            fixtures = null; parts = null;
            failedScene = -1;
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
