using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using EFT.Interactive;
using UnityEngine;

namespace HideoutExtensions
{
    /// <summary>
    /// What a room is made of, taken from the hideout scene each visit, so it looks like the bunker and follows its
    /// current wall, floor and ceiling style: those three materials, the layer the bunker's own colliders use, the metal
    /// door from the room behind the emergency wall (and the sounds of the hideout's doors), and the lamps of two of its
    /// light groups (a fluorescent tube and a hanging bulb). Copies keep the look only (meshes, renderers, LOD groups,
    /// lights), except a lamp's own script and sound, which switch it like the game's; the originals are only read.
    /// </summary>
    internal sealed class SceneParts
    {
        /// <summary>A set of surfaces for a room, and how densely each is tiled (texture repeats per metre).</summary>
        /// <summary>The settings of a surface bullets hit, kept apart from the object they came from (the game swaps its walls).</summary>
        internal sealed class BulletSurface
        {
            private readonly EFT.Ballistics.BallisticCollider sample;
            public readonly int Layer;
            private readonly int presetId;
            private readonly EFT.NetworkPackets.EHitType hitType;
            private readonly EFT.Ballistics.MaterialType material;
            private readonly float level, chance, ricochet, fragmentation, deviationChance, deviation;

            public BulletSurface(EFT.Ballistics.BallisticCollider from)
            {
                Layer = from.gameObject.layer; presetId = from._presetId; hitType = from.HitType; material = from.TypeOfMaterial;
                level = from.PenetrationLevel; chance = from.PenetrationChance; ricochet = from.RicochetChance;
                fragmentation = from.FragmentationChance; deviationChance = from.TrajectoryDeviationChance; deviation = from.TrajectoryDeviation;
            }

            /// <summary>A box bullets hit. Its script wakes when the box is switched on, with these set.</summary>
            public void Add(Transform parent, Vector3 centre, Vector3 size)
            {
                var go = new GameObject(Room.BulletBoxName) { layer = Layer };
                go.SetActive(false);
                go.transform.SetParent(parent, false);
                var box = go.AddComponent<BoxCollider>();
                box.center = centre; box.size = size;
                var surface = go.AddComponent<EFT.Ballistics.BallisticCollider>();
                surface._presetId = presetId; surface.HitType = hitType; surface.TypeOfMaterial = material;
                surface.PenetrationLevel = level; surface.PenetrationChance = chance; surface.RicochetChance = ricochet;
                surface.FragmentationChance = fragmentation; surface.TrajectoryDeviationChance = deviationChance; surface.TrajectoryDeviation = deviation;
                go.SetActive(true);
            }
        }

        internal sealed class Look
        {
            public Material Wall, Floor, Ceiling;
            public float WallUv, FloorUv, CeilingUv;
            public Color FloorColour = Color.white;
            public bool Complete => Wall != null && Floor != null && Ceiling != null;
        }

        /// <summary>The main hall's painted surfaces (the hideout's current style), and the raw concrete of the room behind the emergency wall.</summary>
        public Look Hideout, Basement;
        public int ColliderLayer;
        /// <summary>How the hall's concrete takes a bullet, for the rooms' own surfaces, if it was found.</summary>
        public BulletSurface Bullets;
        /// <summary>The black cap over everything outside the hideout's rooms (what the overview shows as black).</summary>
        public MeshRenderer Cap;
        private Transform door, tube, bulb, live;
        private Vector3 liveAnchor;
        private float liveYaw;
        private static int doors;

        /// <summary>The hideout's doors' sounds (from the first of its doors that has them); any may be empty.</summary>
        public AudioClip[] OpenSounds = new AudioClip[0], ShutSounds = new AudioClip[0];
        public AudioClip BreachSound;

        // The door as it stands in the hideout: closed, its doorway's floor point and its face's direction (degrees).
        private Vector3 doorAnchor;
        private float doorYaw;

        public static SceneParts Find(Transform style)
        {
            // Texture scales measured from the game's own meshes. The hall's floor shader blends its textures by vertex
            // colour; it's red (its tiles) almost everywhere.
            var parts = new SceneParts
            {
                Hideout = new Look
                {
                    Wall = Surface(style, "Wall", "Hideout_Wall_"), Floor = Surface(style, "Floor", "Hideout_Floor_"), Ceiling = Surface(style, "Ceiling", "Hideout_Ceiling_"),
                    WallUv = .319f, FloorUv = .5f, CeilingUv = .559f, FloorColour = Color.red,
                },
            };
            // Searching the whole game is slow, so the areas these come from first (the room behind the emergency wall,
            // the shooting range's lamps), then everything in the hideout's scene if they've moved.
            GameObject[] roots = style.gameObject.scene.GetRootGameObjects();
            Transform[] likely = roots.Where(r => r.name == "22_emergency_wall" || r.name == "12_shooting_range").SelectMany(r => r.GetComponentsInChildren<Transform>(true)).ToArray();
            Transform[] everything = null;
            Transform[] All() => everything ?? (everything = roots.SelectMany(r => r.GetComponentsInChildren<Transform>(true)).ToArray());
            T First<T>(Func<Transform, T> pick) where T : class => likely.Select(pick).FirstOrDefault(x => x != null) ?? All().Select(pick).FirstOrDefault(x => x != null);

            MeshRenderer basement = First(t => t.TryGetComponent(out MeshFilter f) && f.sharedMesh != null && f.sharedMesh.name == "Basement_Room_LOD0" ? t.GetComponent<MeshRenderer>() : null);
            Material Named(string name) => basement?.sharedMaterials.FirstOrDefault(m => m != null && m.name.StartsWith(name, StringComparison.Ordinal));
            parts.Basement = new Look
            {
                Wall = Named("City_Concrete_slab_rough"), Floor = Named("City_plastic_dark_grey_floor"), Ceiling = Named("City_Concrete_Rough_white"),
                WallUv = .309f, FloorUv = .206f, CeilingUv = .352f,
            };
            Transform floorCollider = style.GetComponentsInChildren<MeshCollider>(true).Select(c => c.transform)
                .FirstOrDefault(t => t.name.StartsWith("Hideout_Floor_", StringComparison.Ordinal) && t.name.EndsWith("_COLLIDER", StringComparison.Ordinal));
            parts.ColliderLayer = floorCollider != null ? floorCollider.gameObject.layer : 0;
            EFT.Ballistics.BallisticCollider concrete = style.GetComponentsInChildren<EFT.Ballistics.BallisticCollider>(true)
                .FirstOrDefault(b => b.name.StartsWith("Hideout_Wall_", StringComparison.Ordinal) && b.name.IndexOf("BALLISTIC", StringComparison.Ordinal) >= 0);
            if (concrete != null) parts.Bullets = new BulletSurface(concrete);
            else Plugin.Log.LogWarning("The hideout's walls have no bullet surface to copy, so bullets go through the rooms' walls.");

            parts.Cap = style.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => r.name == "Hideout_Stub_LOD0");
            // The emergency wall's level-4 door is the closed one; the later levels have it open.
            parts.door = First(t => t.name == "Outside_Door_Metal" && t.Find("Outside_Door_Metal_door") != null ? t : null)
                         ?? First(t => t.name.StartsWith("Outside_Door_Metal", StringComparison.Ordinal) && t.Find("Outside_Door_Metal_door") != null ? t : null);
            if (parts.door != null) Anchor(parts.door, out parts.doorAnchor, out parts.doorYaw);
            // The same door as one of the hideout's working doors (the emergency wall's), with the game's door script.
            parts.live = roots.SelectMany(r => r.GetComponentsInChildren<Door>(true))
                .Where(d => d.name == "Outside_Door_Metal_door" && d.transform.parent != null)
                .OrderBy(d => d.gameObject.activeInHierarchy ? 0 : 1).Select(d => d.transform.parent).FirstOrDefault();
            if (parts.live != null) Anchor(parts.live, out parts.liveAnchor, out parts.liveYaw);
            // The lamps the light switch turns on for fluorescent tubes and for bulbs, from the areas' light groups (the
            // shooting range's first); the tube by name if the groups aren't there.
            HideoutAreaLevel[] levels = roots.OrderBy(r => r.name == "12_shooting_range" ? 0 : 1).SelectMany(r => r.GetComponentsInChildren<HideoutAreaLevel>(true)).ToArray();
            Transform Lamp(ELightingLevel level, string prefer) => levels
                .Select(l => l.LightingLevels != null && l.LightingLevels.TryGetValue(level, out LightLevel group) ? group : null)
                .Where(g => g != null && g._lightSwitchers != null)
                .SelectMany(g => g._lightSwitchers.OfType<LampController>())
                .Where(c => c != null && c.GetComponentInChildren<Light>(true) != null && c.GetComponentInChildren<MeshRenderer>(true) != null)
                .OrderBy(c => prefer != null && c.name.StartsWith(prefer, StringComparison.Ordinal) ? 0 : 1)
                .Select(c => c.transform).FirstOrDefault();
            try
            {
                parts.tube = Lamp(ELightingLevel.FluorescentLamps, "lab_lamp_lam_ceiling_destroyable");
                parts.bulb = Lamp(ELightingLevel.LightBulbs, null);
            }
            catch (Exception e) { Plugin.Log.LogWarning("The hideout's light groups couldn't be read: " + e.Message); }
            parts.tube = parts.tube
                         ?? First(t => t.name.StartsWith("lab_lamp_lam_ceiling_destroyable", StringComparison.Ordinal) && t.GetComponentInChildren<Light>(true) != null ? t : null)
                         ?? First(t => t.name.StartsWith("Lum_lamp_hideout", StringComparison.Ordinal) && t.GetComponentInChildren<Light>(true) != null ? t : null);

            Door sounds = roots.SelectMany(r => r.GetComponentsInChildren<Door>(true)).FirstOrDefault(d => d.OpenSound != null && d.OpenSound.Length > 0);
            if (sounds != null)
            {
                parts.OpenSounds = sounds.OpenSound.Where(c => c != null).ToArray();
                parts.ShutSounds = (sounds.ShutSound ?? new AudioClip[0]).Where(c => c != null).ToArray();
                parts.BreachSound = sounds.BreachSound;
            }

            Plugin.Log.LogInfo($"Hideout parts: hall wall {Name(parts.Hideout.Wall)}, floor {Name(parts.Hideout.Floor)}, ceiling {Name(parts.Hideout.Ceiling)}; " +
                               $"basement wall {Name(parts.Basement.Wall)}, floor {Name(parts.Basement.Floor)}, ceiling {Name(parts.Basement.Ceiling)}; collider layer " +
                               $"{LayerMask.LayerToName(parts.ColliderLayer)}, door {(parts.door != null ? Path(parts.door) : "MISSING")}, " +
                               $"tube lamp {(parts.tube != null ? Path(parts.tube) : "missing (plain lights instead)")}, bulb lamp {(parts.bulb != null ? Path(parts.bulb) : "missing")}, " +
                               $"door sounds {(sounds != null ? Path(sounds.transform) : "missing")}, working door {(parts.live != null ? Path(parts.live) : "missing")}, " +
                               $"overview cap {(parts.Cap != null ? parts.Cap.sharedMaterial?.shader?.name : "missing")}.");
            return parts;
        }

        public bool Complete => (Hideout.Complete || Basement.Complete) && door != null;

        /// <summary>Takes the hall's surfaces again (the hideout's customization may have changed them); true if any did.</summary>
        public bool Restyle(Transform style)
        {
            Material wall = Surface(style, "Wall", "Hideout_Wall_"), floor = Surface(style, "Floor", "Hideout_Floor_"), ceiling = Surface(style, "Ceiling", "Hideout_Ceiling_");
            if ((wall == null || wall == Hideout.Wall) && (floor == null || floor == Hideout.Floor) && (ceiling == null || ceiling == Hideout.Ceiling)) return false;
            Hideout.Wall = wall ?? Hideout.Wall; Hideout.Floor = floor ?? Hideout.Floor; Hideout.Ceiling = ceiling ?? Hideout.Ceiling;
            return true;
        }

        /// <summary>The surfaces a spot asks for, or the other set if the hideout lacks those.</summary>
        public Look For(string style)
        {
            Look wanted = style == "basement" ? Basement : Hideout, other = style == "basement" ? Hideout : Basement;
            return wanted.Complete ? wanted : other;
        }

        /// <summary>The door, its doorway's floor point at <paramref name="at"/>, its face looking <paramref name="facing"/> degrees.</summary>
        public Transform Door(Transform parent, Vector3 at, float facing)
        {
            Quaternion turn = Quaternion.Euler(0, facing - doorYaw, 0);
            return Copy(door, parent, turn * door.rotation, at + turn * (door.position - doorAnchor), keepLights: false);
        }

        // Where a door stands: its doorway's floor point and the direction its face looks (degrees).
        private static void Anchor(Transform door, out Vector3 anchor, out float yaw)
        {
            Transform frame = door.Find("Outside_Door_Metal_LOD0") ?? door;
            anchor = new Vector3(frame.position.x, 0, frame.position.z);
            // The models stand Z-up with their face to -Y.
            Vector3 face = frame.rotation * Vector3.down;
            yaw = Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// A working door (the game's door script, hand poses and colliders, so you open, close and breach it like the
        /// hideout's own), shut or as <paramref name="state"/>, its doorway's floor point at <paramref name="at"/> and its
        /// face looking <paramref name="facing"/> degrees; it swings into the room its face looks into. Null without one.
        /// </summary>
        public Door LiveDoor(Transform parent, Vector3 at, float facing, string key, EDoorState state)
        {
            if (live == null) return null;
            Quaternion turn = Quaternion.Euler(0, facing - liveYaw, 0);
            Transform holder = Copy(live, parent, turn * live.rotation, at + turn * (live.position - liveAnchor), keepLights: false, working: true);
            Door door = holder.GetComponentInChildren<Door>(true);
            // Ids of their own (the game keeps its doors by id), and the state it should wake in.
            foreach (WorldInteractiveObject o in holder.GetComponentsInChildren<WorldInteractiveObject>(true))
                o.Id = $"HideoutExtensions.door.{key}.{++doors}";
            if (door != null) door._doorState = state == EDoorState.Open ? EDoorState.Open : EDoorState.Shut;
            holder.gameObject.SetActive(true);
            if (door == null) return null;
            holder.name = "Extension door";

            // Into the room its face looks into, not back through the wall.
            var shown = door.GetComponentsInChildren<MeshRenderer>().Where(r => r.name.IndexOf("SHADOW", StringComparison.Ordinal) < 0).ToArray();
            if (shown.Length > 0 && door.transform.parent != null)
            {
                Bounds bounds = shown[0].bounds;
                foreach (MeshRenderer r in shown) bounds.Encapsulate(r.bounds);
                Vector3 hinge = door.transform.position;
                Vector3 axis = WorldInteractiveObject.GetRotationAxis(door.DoorAxis, door.transform.parent);
                float swing = door.OpenAngle - door.CloseAngle;
                Vector3 opened = Quaternion.AngleAxis(swing, axis) * (bounds.center - hinge);
                Vector3 closed = (state == EDoorState.Open ? Quaternion.AngleAxis(-swing, axis) : Quaternion.identity) * (bounds.center - hinge);
                opened = state == EDoorState.Open ? bounds.center - hinge : opened;
                if (Vector3.Dot(opened - closed, Quaternion.Euler(0, facing, 0) * Vector3.forward) < 0)
                {
                    door.OpenAngle = door.CloseAngle - swing;
                    if (state == EDoorState.Open) door.CurrentAngle = door.OpenAngle;
                }
            }
            return door;
        }

        /// <summary>The door's frame without its leaf: the trim round an open doorway.</summary>
        public Transform Frame(Transform parent, Vector3 at, float facing)
        {
            Transform holder = Door(parent, at, facing);
            foreach (Transform t in holder.GetComponentsInChildren<Transform>(true).ToArray())
                if (t != null && t.name == "Outside_Door_Metal_door") UnityEngine.Object.Destroy(t.gameObject);
            holder.name = "Extension doorway frame";
            return holder;
        }

        /// <summary>The fluorescent tube, its top on the ceiling over <paramref name="at"/> (on the floor), turned <paramref name="yaw"/>.</summary>
        public Transform Tube(Transform parent, Vector3 at, float yaw, float ceiling) => Hang(tube, parent, at, yaw, ceiling);

        /// <summary>The hanging bulb, from the ceiling over <paramref name="at"/>.</summary>
        public Transform Bulb(Transform parent, Vector3 at, float ceiling) => Hang(bulb, parent, at, 0, ceiling);

        private static int lamps;
        private const float LampStrength = .75f;

        private static Transform Hang(Transform lamp, Transform parent, Vector3 at, float yaw, float ceiling)
        {
            if (lamp == null) return null;
            Transform holder = Copy(lamp, parent, Quaternion.Euler(0, yaw, 0) * lamp.rotation, new Vector3(at.x, at.y + ceiling - 1, at.z), keepLights: true);
            Transform copy = holder.GetChild(0);
            // The game's lamp script, ready to be switched: an id of its own (the game keeps lamps by id) and only the
            // parts the copy still has.
            foreach (LampController controller in copy.GetComponentsInChildren<LampController>(true))
            {
                controller.Id = $"HideoutExtensions.lamp.{++lamps}";
                controller.Lights = (controller.Lights ?? new Light[0]).Where(l => l != null).ToArray();
                controller.AreaAndTubeLights = (controller.AreaAndTubeLights ?? new BaseLight[0]).Where(l => l != null).ToArray();
                controller.CustomLights = (controller.CustomLights ?? new AdvancedLight[0]).Where(l => l != null).ToArray();
                controller.MultiFlareLights = (controller.MultiFlareLights ?? new MultiFlare.FlareLight[0]).Where(l => l != null).ToArray();
                controller.OnObjects = (controller.OnObjects ?? new GameObject[0]).Where(o => o != null).ToArray();
                controller.OffObjects = (controller.OffObjects ?? new GameObject[0]).Where(o => o != null).ToArray();
                controller.DestroyedObjects = (controller.DestroyedObjects ?? new GameObject[0]).Where(o => o != null).ToArray();
            }
            // Spaced evenly over a room, at full strength they overexpose it. Before the lamp script wakes, so it keeps this strength.
            foreach (Light light in copy.GetComponentsInChildren<Light>(true)) light.intensity *= LampStrength;
            holder.gameObject.SetActive(true);
            // Its top against the ceiling (the hideout's ceilings aren't all the same height, so it can't hang as it does there).
            var shown = copy.GetComponentsInChildren<MeshRenderer>().Where(r => r.name.IndexOf("SHADOW", StringComparison.Ordinal) < 0).ToArray();
            if (shown.Length > 0) copy.position += Vector3.up * (at.y + ceiling - shown.Max(r => r.bounds.max.y));
            return holder;
        }

        private static Transform Copy(Transform original, Transform parent, Quaternion rotation, Vector3 position, bool keepLights, bool working = false)
        {
            // Into a switched-off holder, so none of the copy's scripts wake before they're stripped.
            var holder = new GameObject("Extension " + original.name);
            holder.SetActive(false);
            holder.transform.SetParent(parent, false);
            GameObject copy = UnityEngine.Object.Instantiate(original.gameObject, holder.transform);
            copy.name = original.name;
            if (!working) Strip(copy.transform, keepLights);
            copy.transform.SetPositionAndRotation(position, rotation);
            copy.transform.localScale = original.lossyScale;
            copy.SetActive(true);
            // A lamp's or a door's script wakes with the holder, so the caller switches it on once its id is its own.
            if (!keepLights && !working) holder.SetActive(true);
            return holder.transform;
        }

        private static readonly string[] DropParts = { "BALLISTIC", "COLLIDER", "colider", "Ballistic", "Push", "Pull", "test_hand" };

        private static void Strip(Transform copy, bool keepLights)
        {
            // Hand-animation rigs, bullet and collision meshes.
            foreach (Transform t in copy.GetComponentsInChildren<Transform>(true).ToArray())
                if (t != null && t != copy && DropParts.Any(p => t.name.IndexOf(p, StringComparison.Ordinal) >= 0)) UnityEngine.Object.DestroyImmediate(t.gameObject);
            // Scripts first (some need the colliders and bodies they use), then everything else but the look; a lamp keeps
            // its lights, its script and its sound.
            for (int pass = 0; pass < 3; pass++)
                foreach (Component c in copy.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c is Transform || c is MeshFilter || c is MeshRenderer || c is LODGroup) continue;
                    if (keepLights && (c is Light || c is LampController || c is AudioSource)) continue;
                    if (pass == 0 && !(c is MonoBehaviour)) continue;
                    try { UnityEngine.Object.DestroyImmediate(c); } catch (Exception) { }
                }
            foreach (MeshRenderer r in copy.GetComponentsInChildren<MeshRenderer>(true)) { r.enabled = true; r.forceRenderingOff = false; }
            foreach (LODGroup g in copy.GetComponentsInChildren<LODGroup>(true)) g.enabled = true;
            if (copy.GetComponentInChildren<LampController>(true) == null)
                foreach (Light l in copy.GetComponentsInChildren<Light>(true)) l.enabled = true;
        }

        // The style's surface that's showing: an active first-detail renderer under STYLE/<group>.
        private static Material Surface(Transform style, string group, string prefix)
        {
            Transform root = style.Find(group) ?? style;
            MeshRenderer renderer = root.GetComponentsInChildren<MeshRenderer>(false)
                .Where(r => r.name.StartsWith(prefix, StringComparison.Ordinal) && r.name.EndsWith("_LOD0", StringComparison.Ordinal)
                            && r.name.IndexOf("SHADOW", StringComparison.Ordinal) < 0 && r.name.IndexOf("Decal", StringComparison.Ordinal) < 0)
                .FirstOrDefault(r => r.sharedMaterial != null)
                ?? root.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => r.name.StartsWith(prefix, StringComparison.Ordinal) && r.sharedMaterial != null);
            return renderer != null ? renderer.sharedMaterial : null;
        }

        private static string Name(Material m) => m != null ? $"{m.name} ({m.shader?.name})" : "MISSING";

        private static string Path(Transform t)
        {
            var names = new List<string>();
            for (; t != null; t = t.parent) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
