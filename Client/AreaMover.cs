using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HideoutExtensions
{
    /// <summary>
    /// Moving the hideout's own areas. On the overview, right-click an area's icon and the whole area (everything its
    /// icon highlights, with its lights, click zone, icon and camera view, which all live under the area) follows your
    /// mouse across the floor: R turns it a quarter (15° with Shift), left-click puts it down, right-click puts it back
    /// where it was, Backspace back where the game has it. Other mods' things (a kennel, say) move the same way from
    /// their own icons (<see cref="HideoutExtensionsApi.AddMovable"/>), and they keep where they were put. Where each area stands is kept per
    /// profile, in BepInEx/config/jrs.hideoutextensions.layout.&lt;profile id&gt;.json, and set again every visit; an area
    /// is never put down in (or left in) an annex room that profile hasn't built: it waits where the game has it until the
    /// room is built. The game's distance culling keeps
    /// where things are, so it's told of every move. An area's room lights (its light groups, the ones the light switch
    /// turns on) go with it: most are its own (spotlights on its displays, candles on its shelves); the config can leave them behind.
    /// The spot an area leaves gets the annex rooms' lamps (following the light switch), which go again when it's put back.
    /// </summary>
    internal static class AreaMover
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> Fixed;
        internal static ConfigEntry<bool> LightsStay;

        private sealed class Placement { public float X, Z, Yaw; }

        // Where the game put an area this visit: its root's pose, the point on the floor under its icon, and its light
        // groups' poses (they stay there).
        private sealed class Home
        {
            public Vector3 Position; public Quaternion Rotation; public Vector3 Pivot;
            public readonly List<(Transform group, Vector3 position, Quaternion rotation)> Lights = new List<(Transform, Vector3, Quaternion)>();
        }

        private static readonly Dictionary<string, Placement> layout = new Dictionary<string, Placement>();
        private static readonly Dictionary<HideoutArea, Home> homes = new Dictionary<HideoutArea, Home>();
        private static string file, profileId;
        private static GameObject style;
        private static float nextOwner, settleUntil;
        private static HideoutPlayerOwner owner;

        private static readonly List<MovableThing> things = new List<MovableThing>();
        // The lamps hung where a moved area used to stand.
        private static readonly Dictionary<HideoutArea, GameObject> vacated = new Dictionary<HideoutArea, GameObject>();
        private static float nextVacate;
        private static List<HideoutArea> areaList;
        private const float LampSpacing = 3.5f;

        // The move under way: the area or another mod's thing, where it was when picked up, and the screen in front of the game.
        private static HideoutArea moving;
        private static MovableThing thing;
        private static Vector3 thingFrom;
        private static Quaternion thingTurn;
        private static Placement before;
        private static float yaw;
        private static GameObject blocker;
        private static TextMeshProUGUI hint;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Moving areas", "Enabled", true, "Right-click an area's icon on the hideout's overview to move the area.");
            Fixed = config.Bind("Moving areas", "Areas that stay put", "EmergencyWall",
                "Areas (by type, comma-separated) that can't be moved. The emergency wall carries the room the annex is built in.");
            // Off by default: an area's light groups are its own (spotlights aimed at its displays, candles on its shelves,
            // bulbs on its cables), so left behind they light nothing and the area arrives without them.
            LightsStay = config.Bind("Moving areas", "Lights stay in the room", false,
                "A moved area leaves its light groups (the ones the light switch turns on) where they were. Most are the area's own (spotlights on its displays, candles on its shelves), so they look out of place without it.");
        }

        // The layout of the profile that's playing, read when its hideout loads (and again if it's another profile's).
        private static bool LoadLayout()
        {
            string id = null;
            try { if (TarkovApplication.Exist(out TarkovApplication app)) id = app.Session?.Profile?.Id; }
            catch (Exception) { }
            if (string.IsNullOrEmpty(id)) return false;
            if (id == profileId) return true;
            profileId = id;
            layout.Clear();
            file = Path.Combine(BepInEx.Paths.ConfigPath, $"jrs.hideoutextensions.layout.{id}.json");
            try
            {
                if (File.Exists(file))
                    foreach (var entry in JsonConvert.DeserializeObject<Dictionary<string, Placement>>(File.ReadAllText(file)) ?? new Dictionary<string, Placement>())
                        layout[entry.Key] = entry.Value;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't read {file}, so the areas stand where the game has them: {e.Message}"); }
            return true;
        }

        // Whether a floor point is in an annex room this profile hasn't built (or the rooms aren't known yet; with room
        // extensions switched off, or without the server mod, there are none).
        private static bool InUnbuiltRoom(Vector3 point)
        {
            if (!Extensions.Ready) return !Extensions.Off;
            foreach (SpotInfo spot in Extensions.SpotList)
            {
                if (spot.Room?.Min == null || spot.Room.Max == null) continue;
                if (point.x < spot.Room.Min[0] || point.x > spot.Room.Max[0] || point.z < spot.Room.Min[1] || point.z > spot.Room.Max[1]) continue;
                if (Extensions.Find(spot.Id)?.Current(out _) != Site.State.Built) return true;
            }
            return false;
        }

        // Where an area is put this visit: where it was moved to, unless that's in a room not built (then home).
        private static bool Placed(HideoutArea area, out Placement placement)
        {
            placement = null;
            string key = Key(area);
            if (key == null || !layout.TryGetValue(key, out Placement p)) return false;
            Home home = HomeOf(area);
            if (InUnbuiltRoom(home.Pivot + new Vector3(p.X, 0, p.Z))) return false;
            placement = p;
            return true;
        }

        internal static void Tick()
        {
            if (style == null)
            {
                if (homes.Count > 0) { homes.Clear(); vacated.Clear(); areaList = null; Stop(); owner = null; }
                if (!Enabled.Value || (style = HideoutScene.Style) == null) return;
                if (!LoadLayout()) { style = null; return; }   // no profile yet: next time
                settleUntil = Time.unscaledTime + 15;
            }
            try
            {
                // The areas set themselves up a moment after the hideout loads: put the moved ones in place as they do
                // (once the annex rooms are known, so none goes into one that isn't built).
                if (Time.unscaledTime < settleUntil && Time.frameCount % 30 == 0 && (Extensions.Ready || Extensions.Off)) PlaceAll();
                // Lamps where moved areas stood (once the rooms' parts are there to copy them from).
                if (moving == null && Time.unscaledTime >= nextVacate) { nextVacate = Time.unscaledTime + 2; LightVacated(false); }
                if (thing != null) MoveThing();
                else if (moving != null) Move();
                else if (Enabled.Value && Input.GetMouseButtonDown(1) && Overview()) Pick();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Moving an area: " + e);
                Stop();
            }
        }

        private static string Key(HideoutArea area) => area.Data?.Template?.Type.ToString();

        internal static void Add(MovableThing added)
        {
            if (added == null || added.Icon == null || added.Root == null) return;
            things.RemoveAll(t => t == added || t.Icon == null || t.Root == null || t.Icon == added.Icon || t.Root == added.Root);
            things.Add(added);
        }

        internal static void Remove(MovableThing removed) => things.Remove(removed);

        private static readonly Dictionary<GameObject, string> unmovable = new Dictionary<GameObject, string>();
        internal static void AddUnmovable(GameObject icon, string reason) { if (icon != null) unmovable[icon] = reason; }

        private static void PlaceAll()
        {
            foreach (HideoutArea area in style.scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<HideoutArea>(true)))
            {
                if (Key(area) == null || area == moving) continue;
                Home home = HomeOf(area);
                if (Placed(area, out Placement placement)) Pose(area, home, placement);
                else if (layout.ContainsKey(Key(area))) Pose(area, home, new Placement());   // its room isn't built: home
            }
        }

        private static Home HomeOf(HideoutArea area)
        {
            if (homes.TryGetValue(area, out Home home)) return home;
            Transform icon = area.AreaIconPoint != null ? area.AreaIconPoint : area.transform;
            homes[area] = home = new Home { Position = area.transform.position, Rotation = area.transform.rotation, Pivot = new Vector3(icon.position.x, area.transform.position.y, icon.position.z) };
            foreach (Transform group in area.GetComponentsInChildren<Transform>(true).Where(t => t.name == "light_levels"))
                home.Lights.Add((group, group.position, group.rotation));
            return home;
        }

        // The area turned about the point under its icon, and that point moved by the placement.
        private static void Pose(HideoutArea area, Home home, Placement placement)
        {
            Quaternion turn = Quaternion.Euler(0, placement.Yaw, 0);
            area.transform.SetPositionAndRotation(home.Pivot + new Vector3(placement.X, 0, placement.Z) + turn * (home.Position - home.Pivot), turn * home.Rotation);
            // Its room lights stay lighting the room (or go along, as the config says).
            foreach (var (group, position, rotation) in home.Lights)
                if (group != null)
                {
                    if (LightsStay.Value) group.SetPositionAndRotation(position, rotation);
                    else
                    {
                        Quaternion moved = area.transform.rotation * Quaternion.Inverse(home.Rotation);
                        group.SetPositionAndRotation(area.transform.position + moved * (position - home.Position), moved * rotation);
                    }
                }
            // The game's culling keeps where its lights and objects are.
            if (CullingManager.Instance != null)
                foreach (CullingObject culled in area.GetComponentsInChildren<CullingObject>(true))
                    try { CullingManager.Instance.UpdateSphere(culled); } catch (Exception) { }
            Physics.SyncTransforms();
        }

        private static bool Overview()
        {
            if (owner == null && Time.unscaledTime >= nextOwner) { nextOwner = Time.unscaledTime + 1; owner = UnityEngine.Object.FindObjectOfType<HideoutPlayerOwner>(); }
            return owner != null && !owner.FirstPersonMode;
        }

        // The area whose icon is under the mouse.
        private static void Pick()
        {
            if (EventSystem.current == null) return;
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = Input.mousePosition }, hits);
            // Another mod's thing first: its icon may be a copy of an area's (the kennel's is the Rest Space's).
            things.RemoveAll(t => t.Icon == null || t.Root == null);
            MovableThing picked = hits.Where(h => h.gameObject != null)
                .Select(h => things.FirstOrDefault(t => h.gameObject.transform.IsChildOf(t.Icon.transform))).FirstOrDefault(t => t != null);
            // Another mod's icon that moves nothing (a copy of an area's icon for something not yet built, say).
            foreach (GameObject dead in unmovable.Keys.Where(k => k == null).ToList()) unmovable.Remove(dead);
            string refused = hits.Where(h => h.gameObject != null)
                .Select(h => unmovable.FirstOrDefault(u => h.gameObject.transform.IsChildOf(u.Key.transform)).Value).FirstOrDefault(r => r != null);
            if (picked == null && refused != null)
            {
                EFT.Communications.NotificationManager.DisplayWarningNotification(refused);
                return;
            }
            if (picked != null)
            {
                thing = picked; thingFrom = picked.Root.position; thingTurn = picked.Root.rotation;
                yaw = picked.Root.eulerAngles.y;
                Block(hits.Select(h => h.gameObject.GetComponentInParent<AreaIcon>()).FirstOrDefault(i => i != null), picked.Name ?? picked.Root.name);
                return;
            }
            AreaIcon icon = hits.Select(h => h.gameObject != null ? h.gameObject.GetComponentInParent<AreaIcon>() : null).FirstOrDefault(i => i != null && i.Data != null);
            if (icon == null) return;
            HideoutArea area = style.scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<HideoutArea>(true))
                .FirstOrDefault(a => a.Data != null && a.Data.Template.Type == icon.Data.Template.Type);
            if (area == null) return;
            string key = Key(area);
            if (Fixed.Value.Split(',').Select(s => s.Trim()).Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                EFT.Communications.NotificationManager.DisplayWarningNotification($"{Name(area)} stays where it is (it holds up other things).");
                return;
            }
            HomeOf(area);
            moving = area;
            before = layout.TryGetValue(key, out Placement placed) ? new Placement { X = placed.X, Z = placed.Z, Yaw = placed.Yaw } : null;
            yaw = before?.Yaw ?? 0;
            Block(icon, Name(area));
        }

        private static string Name(HideoutArea area)
        {
            try { return area.Data.Template.Name; } catch (Exception) { return Key(area); }
        }

        private static void Move()
        {
            Home home = HomeOf(moving);
            string key = Key(moving);
            // The mouse wheel zooms the overview, so turning is on R: a quarter, or 15° with Shift.
            if (Input.GetKeyDown(KeyCode.R)) yaw += Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 15 : 90;
            yaw = Mathf.Repeat(yaw, 360);

            if (Input.GetKeyDown(KeyCode.Backspace))
            {
                layout.Remove(key);
                Pose(moving, home, new Placement());
                Save();
                Stop();
                LightVacated(true);
                return;
            }
            if (Input.GetMouseButtonDown(1))
            {
                Pose(moving, home, before ?? new Placement());
                Stop();
                return;
            }

            // Where the mouse points on the hideout's floor.
            Camera camera = null;
            try { camera = EFT.CameraControl.CameraManager.Instance.Camera; } catch (Exception) { }
            if (camera == null) camera = Camera.main;
            if (camera == null) return;
            Ray ray = camera.ScreenPointToRay(Input.mousePosition);
            var floor = new Plane(Vector3.up, new Vector3(0, home.Pivot.y, 0));
            if (!floor.Raycast(ray, out float distance)) return;
            Vector3 at = ray.GetPoint(distance);
            var placement = new Placement { X = at.x - home.Pivot.x, Z = at.z - home.Pivot.z, Yaw = yaw };
            Pose(moving, home, placement);

            if (Input.GetMouseButtonDown(0))
            {
                if (InUnbuiltRoom(home.Pivot + new Vector3(placement.X, 0, placement.Z)))
                {
                    EFT.Communications.NotificationManager.DisplayWarningNotification("That room isn't built yet.");
                    return;
                }
                layout[key] = placement;
                Save();
                Plugin.Log.LogInfo($"Moved {key}: {placement.X:0.##}, {placement.Z:0.##} m, turned {placement.Yaw:0}°.");
                Stop();
                LightVacated(true);
            }
        }

        // Another mod's thing: follows the mouse on its own floor; its mod is told where it's put down.
        private static void MoveThing()
        {
            if (thing.Root == null) { Stop(); return; }
            if (Input.GetKeyDown(KeyCode.R)) yaw += Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 15 : 90;
            yaw = Mathf.Repeat(yaw, 360);
            if (Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Backspace))
            {
                thing.Root.SetPositionAndRotation(thingFrom, thingTurn);
                Physics.SyncTransforms();
                Stop();
                return;
            }
            Camera camera = null;
            try { camera = EFT.CameraControl.CameraManager.Instance.Camera; } catch (Exception) { }
            if (camera == null) camera = Camera.main;
            if (camera == null) return;
            Ray ray = camera.ScreenPointToRay(Input.mousePosition);
            if (!new Plane(Vector3.up, thingFrom).Raycast(ray, out float distance)) return;
            Vector3 at = ray.GetPoint(distance);
            Quaternion turn = Quaternion.Euler(0, yaw, 0);
            thing.Root.SetPositionAndRotation(at, turn);
            Physics.SyncTransforms();
            if (!Input.GetMouseButtonDown(0)) return;
            string why = null;
            try { why = thing.Check?.Invoke(at, turn); }
            catch (Exception e) { Plugin.Log.LogWarning($"Checking where {thing.Name} can go: {e.Message}"); }
            if (why != null) { EFT.Communications.NotificationManager.DisplayWarningNotification($"{thing.Name} can't go there: {why}"); return; }
            try { thing.Placed?.Invoke(at, turn); }
            catch (Exception e) { Plugin.Log.LogError($"Putting {thing.Name} down: {e}"); }
            Plugin.Log.LogInfo($"Moved {thing.Name} to {at}, turned {yaw:0}°.");
            Stop();
        }

        // ---- lamps where an area stood -------------------------------------------------------------------------

        // Every moved area's old spot gets lamps (again, if anything's moved since: none where an area stands now); an
        // area back home loses them.
        private static void LightVacated(bool again)
        {
            SceneParts parts = Extensions.Parts;
            if (parts == null || style == null) return;
            // Nothing to do while every moved area has its lamps (most of the time); the areas are looked up once a visit.
            if (!again && layout.Count == vacated.Count && vacated.Values.All(v => v != null)) return;
            if (areaList == null || areaList.Any(a => a == null || Key(a) == null))
                areaList = style.scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<HideoutArea>(true)).ToList();
            var areas = areaList.Where(a => a != null && Key(a) != null).ToList();
            foreach (HideoutArea area in areas)
            {
                bool away = Placed(area, out Placement p) && (Mathf.Abs(p.X) > .5f || Mathf.Abs(p.Z) > .5f || Mathf.Abs(Mathf.DeltaAngle(p.Yaw, 0)) > 1);
                vacated.TryGetValue(area, out GameObject lamps);
                if (!away || again) { if (lamps != null) UnityEngine.Object.Destroy(lamps); vacated.Remove(area); lamps = null; }
                if (!away || lamps != null) continue;
                try { Hang(area, areas, parts); }
                catch (Exception e) { Plugin.Log.LogWarning($"Lamps where {Key(area)} stood: {e.Message}"); }
            }
        }

        private static void Hang(HideoutArea area, List<HideoutArea> areas, SceneParts parts)
        {
            Home home = HomeOf(area);
            if (!Footprint(area, home.Position, home.Rotation, out Rect spot)) return;   // not set up yet: next time
            var taken = areas.Select(a => Footprint(a, a.transform.position, a.transform.rotation, out Rect r) ? r : Rect.zero).Where(r => r.width > 0).ToList();
            var holder = new GameObject("Hideout extensions lamps where " + Key(area) + " stood");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(holder, style.scene);
            vacated[area] = holder;
            // Down the middle of the old spot, along its longer side, a lamp every few metres (at most four).
            bool alongX = spot.width >= spot.height;
            float length = alongX ? spot.width : spot.height;
            int count = Mathf.Clamp(Mathf.RoundToInt(length / LampSpacing), 1, 4);
            int hung = 0;
            for (int i = 1; i <= count; i++)
            {
                float f = (float)i / (count + 1);
                var at = new Vector3(alongX ? Mathf.Lerp(spot.xMin, spot.xMax, f) : spot.center.x, home.Position.y, alongX ? spot.center.y : Mathf.Lerp(spot.yMin, spot.yMax, f));
                // Not over where an area stands now (this one, or another moved there).
                if (taken.Any(r => r.Contains(new Vector2(at.x, at.z)))) continue;
                float ceiling = Ceiling(at);
                Transform tube = parts.Tube(holder.transform, at, alongX ? 90 : 0, ceiling), bulb = parts.Bulb(holder.transform, at, ceiling);
                if (tube != null || bulb != null) { Lighting.Add(tube, bulb); hung++; }
            }
            if (hung > 0) Plugin.Log.LogInfo($"{hung} lamp(s) hung where {Key(area)} stood.");
        }

        // Where an area's things stand on the floor (x, z) at a pose, from its shown models (not its lights); false if
        // it shows nothing yet.
        private static bool Footprint(HideoutArea area, Vector3 position, Quaternion rotation, out Rect rect)
        {
            rect = Rect.zero;
            Transform root = area.transform;
            bool any = false;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || !(r is MeshRenderer || r is SkinnedMeshRenderer) || IsLight(r.transform, root)) continue;
                Bounds b = r.bounds;
                if (b.size.x > 12 || b.size.z > 12) continue;   // paint or decals over a whole room
                for (int i = 0; i < 8; i++)
                {
                    Vector3 local = root.InverseTransformPoint(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    min = Vector3.Min(min, local); max = Vector3.Max(max, local);
                }
                any = true;
            }
            if (!any) return false;
            var pose = Matrix4x4.TRS(position, rotation, root.lossyScale);
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 w = pose.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? min.x : max.x, 0, (i & 2) == 0 ? min.z : max.z));
                x0 = Mathf.Min(x0, w.x); x1 = Mathf.Max(x1, w.x); z0 = Mathf.Min(z0, w.z); z1 = Mathf.Max(z1, w.z);
            }
            rect = Rect.MinMaxRect(x0, z0, x1, z1);
            return rect.width > .3f && rect.height > .3f;
        }

        private static bool IsLight(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent) if (t.name == "light_levels") return true;
            return false;
        }

        // How high the ceiling is over a floor point (the hideout's own colliders), or the bunker's usual 3.1 m.
        private static float Ceiling(Vector3 floor)
        {
            foreach (RaycastHit hit in Physics.RaycastAll(floor + Vector3.up * .5f, Vector3.up, 6, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
                if (hit.collider.GetComponentInParent<HideoutArea>() == null && hit.point.y - floor.y > 2.2f) return hit.point.y - floor.y;
            return 3.1f;
        }

        private static void Save()
        {
            if (file == null) return;
            try { File.WriteAllText(file, JsonConvert.SerializeObject(layout, Formatting.Indented)); }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't save {file}: {e.Message}"); }
        }

        // A see-through screen over the game while an area's being moved, so its clicks don't select or open anything,
        // and what the keys do.
        private static void Block(AreaIcon icon, string name)
        {
            blocker = new GameObject("Hideout extensions area mover", typeof(RectTransform));
            var canvas = blocker.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30;
            blocker.AddComponent<CanvasScaler>();
            blocker.AddComponent<GraphicRaycaster>();
            var screen = new GameObject("Screen", typeof(RectTransform)).GetComponent<RectTransform>();
            screen.SetParent(blocker.transform, false);
            screen.anchorMin = Vector2.zero; screen.anchorMax = Vector2.one; screen.offsetMin = screen.offsetMax = Vector2.zero;
            screen.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, .001f);
            var text = new GameObject("Hint", typeof(RectTransform)).GetComponent<RectTransform>();
            text.SetParent(blocker.transform, false);
            text.anchorMin = new Vector2(.5f, 1); text.anchorMax = new Vector2(.5f, 1); text.pivot = new Vector2(.5f, 1);
            text.anchoredPosition = new Vector2(0, -90); text.sizeDelta = new Vector2(900, 60);
            hint = text.gameObject.AddComponent<TextMeshProUGUI>();
            if (icon != null && icon._currentAreaLevel != null) hint.font = icon._currentAreaLevel.font;
            hint.fontSize = 20; hint.alignment = TextAlignmentOptions.Center; hint.raycastTarget = false;
            hint.color = new Color(.85f, .83f, .78f);
            hint.text = $"<b>MOVING {name.ToUpperInvariant()}</b>\nClick to put it down · R to turn (Shift+R: a little) · Right-click to cancel · Backspace: back where the game has it";
        }

        private static void Stop()
        {
            moving = null; before = null; thing = null;
            if (blocker != null) UnityEngine.Object.Destroy(blocker);
            blocker = null; hint = null;
        }
    }
}
