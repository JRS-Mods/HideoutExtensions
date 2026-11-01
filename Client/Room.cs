using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace HideoutExtensions
{
    /// <summary>An open doorway in one of a room's walls: on the floor, halfway through the wall it's in.</summary>
    internal sealed class Doorway
    {
        public Vector3 Centre;
        /// <summary>The room that owns the doorway (the one reached through it) fills its sides, top and threshold.</summary>
        public bool Owner;
    }

    /// <summary>
    /// A spot's room: floor, ceiling and four walls in the hideout's own materials (the spot's room.style: the hall's
    /// paint, or the raw concrete of the room behind the emergency wall), tiled as densely as the originals, with open doorways where its neighbours are built, solid where you'd walk into it, ceiling
    /// lamps, and any pillar walls across it. Faces look inwards only, so from the overview you see into it like the hideout's own rooms. Rooms next to
    /// each other stand <see cref="Gap"/> apart, which is the wall between them.
    /// </summary>
    internal static class Room
    {
        public const float Gap = .2f, OpeningWidth = 1.05f, OpeningHeight = 2.13f;
        private const float WallCollider = .1f, SlabCollider = .3f;

        public static Vector3 Corner(SpotInfo spot) => new Vector3(spot.Room.Min[0], spot.Room.Floor, spot.Room.Min[1]);
        public static Vector3 Size(SpotInfo spot) => new Vector3(spot.Room.Max[0] - spot.Room.Min[0], spot.Room.Height, spot.Room.Max[1] - spot.Room.Min[1]);

        /// <summary>The room's walls, floor, ceiling and lamps, into <paramref name="shell"/> (at the room's corner, unturned).</summary>
        public static void Build(SpotInfo spot, SceneParts parts, Transform shell, IList<Doorway> doorways)
        {
            Vector3 size = Size(spot);
            float w = size.x, h = size.y, d = size.z;
            if (spot.Room.Style == "existing")
            {
                // A space the hideout's model already has: only a floor to be safe on, blocks over what pokes into it,
                // and lamps.
                Box(shell, parts, "Floor collider", new Vector3(w / 2, -SlabCollider / 2, d / 2), new Vector3(w, SlabCollider, d));
                Blocks(spot, parts.For("basement"), parts, shell);
                Lamps(spot, parts, shell, size);
                return;
            }
            SceneParts.Look look = parts.For(spot.Room.Style);

            var walls = new[]
            {
                new Side("South wall", Vector3.zero, Vector3.right, w, Vector3.forward),
                new Side("North wall", new Vector3(0, 0, d), Vector3.right, w, Vector3.back),
                new Side("West wall", Vector3.zero, Vector3.forward, d, Vector3.right),
                new Side("East wall", new Vector3(w, 0, 0), Vector3.forward, d, Vector3.left),
            };
            foreach (Doorway door in doorways)
            {
                Vector3 local = shell.InverseTransformPoint(door.Centre);
                Side wall = walls.OrderBy(s => Mathf.Abs(Vector3.Dot(local - s.Start, s.Inward))).First();
                if (Mathf.Abs(Vector3.Dot(local - wall.Start, wall.Inward)) > Gap * 2)
                {
                    Plugin.Log.LogWarning($"'{spot.Name}': a doorway at {door.Centre} isn't in any of its walls; left out.");
                    continue;
                }
                wall.Openings.Add((Vector3.Dot(local - wall.Start, wall.Along), door.Owner));
            }

            Surface(shell, "Floor", look.Floor, new[] { new Vector3(0, 0, 0), new Vector3(0, 0, d), new Vector3(w, 0, d), new Vector3(w, 0, 0) },
                    Vector3.up, p => new Vector2(p.x, p.z) * look.FloorUv, look.FloorColour, ShadowCastingMode.On);
            Surface(shell, "Ceiling", look.Ceiling, new[] { new Vector3(0, h, 0), new Vector3(w, h, 0), new Vector3(w, h, d), new Vector3(0, h, d) },
                    Vector3.down, p => new Vector2(p.x, p.z) * look.CeilingUv, Color.white, ShadowCastingMode.TwoSided);
            foreach (Side wall in walls) Wall(shell, look, parts, wall, h);
            foreach (PillarWallInfo pillars in spot.Room.PillarWalls ?? new List<PillarWallInfo>())
                PillarWall(shell, look, parts, shell.InverseTransformPoint(new Vector3(pillars.From[0], spot.Room.Floor, pillars.From[1])),
                           shell.InverseTransformPoint(new Vector3(pillars.To[0], spot.Room.Floor, pillars.To[1])), h);

            Blocks(spot, look, parts, shell);
            Box(shell, parts, "Floor collider", new Vector3(w / 2, -SlabCollider / 2, d / 2), new Vector3(w + 2 * Gap, SlabCollider, d + 2 * Gap));
            Box(shell, parts, "Ceiling collider", new Vector3(w / 2, h + SlabCollider / 2, d / 2), new Vector3(w + 2 * Gap, SlabCollider, d + 2 * Gap));

            Lamps(spot, parts, shell, size);
        }

        // Down the middle of the room, along its longer side; with pillar walls, in each bay between them, running
        // the same way as the walls (so none crosses a beam). A tube and a bulb at each, for the light switch.
        private static void Lamps(SpotInfo spot, SceneParts parts, Transform shell, Vector3 size)
        {
            int lamps = Mathf.Clamp(spot.Room.Lamps, 0, 8);
            if (lamps == 0) return;
            var walls = (spot.Room.PillarWalls ?? new List<PillarWallInfo>())
                .Select(p => (a: shell.InverseTransformPoint(new Vector3(p.From[0], spot.Room.Floor, p.From[1])), b: shell.InverseTransformPoint(new Vector3(p.To[0], spot.Room.Floor, p.To[1]))))
                .ToList();
            // The axis the lamps run along, and where the bays split across the other one.
            bool alongX = walls.Count > 0 ? Mathf.Abs(walls[0].b.x - walls[0].a.x) >= Mathf.Abs(walls[0].b.z - walls[0].a.z) : size.x > size.z;
            float across = alongX ? size.z : size.x, along = alongX ? size.x : size.z;
            var cuts = walls.Select(wl => alongX ? wl.a.z : wl.a.x).Where(c => c > .5f && c < across - .5f).OrderBy(c => c).ToList();
            var spots = new List<Vector3>();
            if (cuts.Count == 0)
            {
                // No bays: one row down the room's middle.
                for (int i = 1; i <= lamps; i++)
                {
                    float f = (float)i / (lamps + 1);
                    spots.Add(alongX ? new Vector3(along * f, 0, across / 2) : new Vector3(across / 2, 0, along * f));
                }
            }
            else
            {
                var edges = new List<float> { 0 };
                edges.AddRange(cuts); edges.Add(across);
                int bays = edges.Count - 1, each = Mathf.Max(1, Mathf.RoundToInt((float)lamps / bays));
                for (int bay = 0; bay < bays; bay++)
                    for (int i = 1; i <= each; i++)
                    {
                        float c = Mathf.Lerp(edges[bay], edges[bay + 1], (float)i / (each + 1));
                        spots.Add(alongX ? new Vector3(along / 2, 0, c) : new Vector3(c, 0, along / 2));
                    }
            }
            foreach (Vector3 local in spots)
            {
                Vector3 at = shell.TransformPoint(local);
                Transform tube = parts.Tube(shell, at, alongX ? 90 : 0, size.y), bulb = parts.Bulb(shell, at, size.y);
                if (tube == null && bulb == null) Bulb(shell, at + Vector3.up * (size.y - .35f));
                else Lighting.Add(tube, bulb);
            }
        }

        // Plain concrete blocks the spot asks for (over parts of the hideout's model that poke into the room).
        private static void Blocks(SpotInfo spot, SceneParts.Look look, SceneParts parts, Transform shell)
        {
            foreach (BlockInfo block in spot.Room.Blocks ?? new List<BlockInfo>())
            {
                Vector3 a = shell.InverseTransformPoint(new Vector3(block.Min[0], block.Min[1], block.Min[2]));
                Vector3 b = shell.InverseTransformPoint(new Vector3(block.Max[0], block.Max[1], block.Max[2]));
                Block(shell, look, parts, "Block", Vector3.Min(a, b), Vector3.Max(a, b));
            }
        }

        private sealed class Side
        {
            public readonly string Name; public readonly Vector3 Start, Along, Inward; public readonly float Length;
            public readonly List<(float t, bool owner)> Openings = new List<(float, bool)>();
            public Side(string name, Vector3 start, Vector3 along, float length, Vector3 inward) { Name = name; Start = start; Along = along; Length = length; Inward = inward; }
            // A point on the wall: t along it, y up, `off` out of the room (into the wall between rooms).
            public Vector3 At(float t, float y, float off = 0) => Start + Along * t + Vector3.up * y - Inward * off;
        }

        private static void Wall(Transform shell, SceneParts.Look look, SceneParts parts, Side wall, float h)
        {
            var holes = wall.Openings.OrderBy(o => o.t)
                .Select(o => (from: Mathf.Max(0, o.t - OpeningWidth / 2), to: Mathf.Min(wall.Length, o.t + OpeningWidth / 2), o.owner)).ToList();
            float top = Mathf.Min(OpeningHeight, h - .1f);
            float t = 0;
            foreach (var hole in holes)
            {
                Piece(shell, look, parts, wall, t, hole.from, 0, h);
                Piece(shell, look, parts, wall, hole.from, hole.to, top, h);   // over the doorway
                if (hole.owner) Reveal(shell, look, wall, hole.from, hole.to, top);
                t = hole.to;
            }
            Piece(shell, look, parts, wall, t, wall.Length, 0, h);
        }

        // The pillar wall across the room behind the emergency wall, as its model has it (measured from Basement_Room):
        // a beam under the ceiling on pillars, open between them; a pillar at each end, and one in the middle of a long span.
        private const float PillarDepth = .42f, BeamBottom = 2.49f, EndPillar = .98f, MiddlePillar = 1.63f, MiddleOver = 9;

        private static void PillarWall(Transform shell, SceneParts.Look look, SceneParts parts, Vector3 a, Vector3 b, float h)
        {
            bool alongX = Mathf.Abs(b.x - a.x) >= Mathf.Abs(b.z - a.z);
            float from = alongX ? Mathf.Min(a.x, b.x) : Mathf.Min(a.z, b.z), to = alongX ? Mathf.Max(a.x, b.x) : Mathf.Max(a.z, b.z);
            float across = alongX ? a.z : a.x, bottom = Mathf.Min(BeamBottom, h - .3f);
            void Part(string name, float t0, float t1, float y0, float y1)
            {
                Vector3 lo = alongX ? new Vector3(t0, y0, across - PillarDepth / 2) : new Vector3(across - PillarDepth / 2, y0, t0);
                Vector3 hi = alongX ? new Vector3(t1, y1, across + PillarDepth / 2) : new Vector3(across + PillarDepth / 2, y1, t1);
                Block(shell, look, parts, name, lo, hi);
            }
            Part("Beam", from, to, bottom, h);
            Part("Pillar", from, from + EndPillar, 0, bottom);
            Part("Pillar", to - EndPillar, to, 0, bottom);
            if (to - from > MiddleOver) Part("Pillar", (from + to - MiddlePillar) / 2, (from + to + MiddlePillar) / 2, 0, bottom);
        }

        // An upright concrete box from lo to hi: its sides, and its underside if it's off the floor, solid. One-sided like
        // the hideout's own, so from the overview you see past a beam as through the ceilings, and the room reads as one.
        private static void Block(Transform shell, SceneParts.Look look, SceneParts parts, string name, Vector3 lo, Vector3 hi)
        {
            float x0 = lo.x, y0 = lo.y, z0 = lo.z, x1 = hi.x, y1 = hi.y, z1 = hi.z;
            System.Func<Vector3, Vector2> onX = p => new Vector2(p.z, p.y) * look.WallUv, onZ = p => new Vector2(p.x, p.y) * look.WallUv, onY = p => new Vector2(p.x, p.z) * look.WallUv;
            const ShadowCastingMode shadows = ShadowCastingMode.TwoSided;
            Surface(shell, name, look.Wall, new[] { new Vector3(x0, y0, z0), new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0) }, Vector3.left, onX, Color.white, shadows);
            Surface(shell, name, look.Wall, new[] { new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0) }, Vector3.right, onX, Color.white, shadows);
            Surface(shell, name, look.Wall, new[] { new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0) }, Vector3.back, onZ, Color.white, shadows);
            Surface(shell, name, look.Wall, new[] { new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1) }, Vector3.forward, onZ, Color.white, shadows);
            if (y0 > .01f)
                Surface(shell, name, look.Wall, new[] { new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x0, y0, z1) }, Vector3.down, onY, Color.white, shadows);
            Box(shell, parts, name + " collider", (lo + hi) / 2, hi - lo);
        }

        private static void Piece(Transform shell, SceneParts.Look look, SceneParts parts, Side wall, float from, float to, float bottom, float top)
        {
            if (to - from < .01f || top - bottom < .01f) return;
            Surface(shell, wall.Name, look.Wall, new[] { wall.At(from, bottom), wall.At(to, bottom), wall.At(to, top), wall.At(from, top) }, wall.Inward,
                    p => new Vector2(Vector3.Dot(p - wall.Start, wall.Along), p.y) * look.WallUv, Color.white, ShadowCastingMode.TwoSided);
            Vector3 centre = (wall.At(from, bottom) + wall.At(to, top)) / 2 - wall.Inward * (WallCollider / 2);
            Vector3 extent = wall.Along * (to - from) + Vector3.up * (top - bottom) + wall.Inward * WallCollider;
            Box(shell, parts, wall.Name + " collider", centre, new Vector3(Mathf.Abs(extent.x), Mathf.Abs(extent.y), Mathf.Abs(extent.z)));
        }

        // The doorway's sides, top and threshold, across the wall between the two rooms.
        private static void Reveal(Transform shell, SceneParts.Look look, Side wall, float from, float to, float top)
        {
            System.Func<Vector3, Vector2> uv = p => new Vector2(Vector3.Dot(p - wall.Start, wall.Along) + Vector3.Dot(p, wall.Inward), p.y) * look.WallUv;
            Surface(shell, "Doorway side", look.Wall, new[] { wall.At(from, 0), wall.At(from, 0, Gap), wall.At(from, top, Gap), wall.At(from, top) },
                    wall.Along, uv, Color.white, ShadowCastingMode.TwoSided);
            Surface(shell, "Doorway side", look.Wall, new[] { wall.At(to, 0), wall.At(to, 0, Gap), wall.At(to, top, Gap), wall.At(to, top) },
                    -wall.Along, uv, Color.white, ShadowCastingMode.TwoSided);
            Surface(shell, "Doorway top", look.Wall, new[] { wall.At(from, top), wall.At(to, top), wall.At(to, top, Gap), wall.At(from, top, Gap) },
                    Vector3.down, uv, Color.white, ShadowCastingMode.TwoSided);
            Surface(shell, "Doorway floor", look.Floor, new[] { wall.At(from, 0), wall.At(to, 0), wall.At(to, 0, Gap), wall.At(from, 0, Gap) },
                    Vector3.up, p => new Vector2(p.x, p.z) * look.FloorUv, look.FloorColour, ShadowCastingMode.On);
        }

        internal static void Surface(Transform shell, string name, Material material, Vector3[] corners, Vector3 normal,
                                    System.Func<Vector3, Vector2> uv, Color colour, ShadowCastingMode shadows)
        {
            var go = new GameObject("Extension " + name);
            go.transform.SetParent(shell, false);
            var mesh = new Mesh { name = "Extension " + name };
            var uvs = new Vector2[4];
            for (int i = 0; i < 4; i++) uvs[i] = uv(corners[i]);
            // Unity draws a triangle's clockwise side: pick the order whose face looks along the normal.
            bool flip = Vector3.Dot(Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]), normal) < 0;
            mesh.vertices = corners;
            mesh.uv = uvs;
            mesh.colors = new[] { colour, colour, colour, colour };
            mesh.normals = new[] { normal, normal, normal, normal };
            mesh.triangles = flip ? new[] { 0, 2, 1, 0, 3, 2 } : new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = shadows;
        }

        internal static void Box(Transform shell, SceneParts parts, string name, Vector3 centre, Vector3 size)
        {
            var go = new GameObject("Extension " + name) { layer = parts.ColliderLayer };
            go.transform.SetParent(shell, false);
            var box = go.AddComponent<BoxCollider>();
            box.center = centre; box.size = size;
        }

        // When the hideout has no ceiling lamp to copy: a warm light like the bunker's.
        private static void Bulb(Transform shell, Vector3 at)
        {
            var go = new GameObject("Extension light");
            go.transform.SetParent(shell, false);
            go.transform.position = at;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point; light.range = 7; light.intensity = 1.2f;
            light.color = new Color(1f, .86f, .7f); light.shadows = LightShadows.Soft;
        }
    }
}
