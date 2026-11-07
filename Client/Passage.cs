using System;
using EFT;
using UnityEngine;

namespace HideoutExtensions
{
    /// <summary>
    /// A built door through one of the hideout's own walls when they couldn't be cut (<see cref="WallCut"/>): an open
    /// frame on each side with a dark doorway, and walking into one puts you just outside the other, facing the way you
    /// were walking, with no prompt and no black screen. The two sides are a wall's thickness apart, so it feels like
    /// stepping through. <see cref="Line"/> lines a doorway that was cut.
    /// </summary>
    internal sealed class Passage
    {
        private const float Reach = .55f, HalfWidth = .55f, Out = .85f, CeilingHeight = 3.11f;
        private static float quietUntil;
        private readonly Site site;
        private readonly Vector3 at, exit;
        private readonly Quaternion face;
        private readonly float turn;

        public Passage(Site site, DoorInfo from, DoorInfo to)
        {
            this.site = site;
            at = Extensions.Point(from.Position);
            face = Quaternion.Euler(0, from.Facing, 0);
            exit = Extensions.Point(to.Position) + Quaternion.Euler(0, to.Facing, 0) * Vector3.forward * Out;
            // Walking into this side (against its face) comes out of the other along its face.
            turn = to.Facing - (from.Facing + 180);
        }

        /// <summary>Moves the player through if they're stepping into this doorway; true if it did.</summary>
        public bool Through(Player player)
        {
            if (Time.unscaledTime < quietUntil) return false;
            Vector3 local = Quaternion.Inverse(face) * (player.Position - at);
            if (Mathf.Abs(local.x) > HalfWidth || local.z > Reach || local.z < -.2f || Mathf.Abs(local.y) > 1) return false;
            quietUntil = Time.unscaledTime + .6f;
            try
            {
                Vector2 look = player.Rotation;
                player.Teleport(exit);
                player.Rotation = new Vector2(look.x + turn, look.y);
            }
            catch (Exception e) { Plugin.Log.LogError($"Couldn't take you through '{site.Spot.Name}''s doorway: {e}"); }
            return true;
        }

        /// <summary>The doorway's dark inside, just in front of the wall within the frame.</summary>
        public static void Opening(Transform parent, Vector3 floorPoint, float facing)
        {
            Material black = OverviewCap.Black();
            if (black == null) return;
            var go = new GameObject("Extension doorway");
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(floorPoint, Quaternion.Euler(0, facing, 0));
            const float w = Room.OpeningWidth / 2, h = Room.OpeningHeight, z = .02f;
            var mesh = new Mesh { name = "Extension doorway" };
            mesh.vertices = new[] { new Vector3(-w, 0, z), new Vector3(w, 0, z), new Vector3(w, h, z), new Vector3(-w, h, z) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = black;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        /// <summary>
        /// A doorway cut through the hideout's walls (<see cref="WallCut"/>) between sides <paramref name="a"/> and
        /// <paramref name="b"/>: the gap between the two walls lined with the basement's concrete (sides and top, solid),
        /// and floored, so you just walk through.
        /// </summary>
        public static void Line(Transform parent, SceneParts parts, DoorInfo a, DoorInfo b)
        {
            // The walls either side of a wide opening are the hideout's own, and it's lined to match them.
            SceneParts.Look look = parts.For(a.Open ? "hideout" : "basement");
            // In side a's frame: x along its wall, y up, z out of the wall towards you; side b is at z = -depth.
            var holder = new GameObject("Extension doorway through the wall").transform;
            holder.SetParent(parent, false);
            holder.SetPositionAndRotation(Extensions.Point(a.Position), Quaternion.Euler(0, a.Facing, 0));
            float depth = -holder.InverseTransformPoint(Extensions.Point(b.Position)).z;
            if (depth < .02f) return;
            float w = (a.Width > 0 ? a.Width : Room.OpeningWidth) / 2, h = Mathf.Min(a.Height > 0 ? a.Height : Room.OpeningHeight, CeilingHeight);
            Vector3 P(float x, float y, float z) => new Vector3(x, y, -z);
            System.Func<Vector3, Vector2> side = p => new Vector2(-p.z, p.y) * look.WallUv;
            System.Func<Vector3, Vector2> flat = p => new Vector2(p.x, -p.z);
            Room.Surface(holder, "Doorway side", look.Wall, new[] { P(-w, 0, 0), P(-w, 0, depth), P(-w, h, depth), P(-w, h, 0) }, Vector3.right, side, Color.white, UnityEngine.Rendering.ShadowCastingMode.TwoSided);
            Room.Surface(holder, "Doorway side", look.Wall, new[] { P(w, 0, 0), P(w, 0, depth), P(w, h, depth), P(w, h, 0) }, Vector3.left, side, Color.white, UnityEngine.Rendering.ShadowCastingMode.TwoSided);
            Room.Surface(holder, "Doorway top", look.Wall, new[] { P(-w, h, 0), P(w, h, 0), P(w, h, depth), P(-w, h, depth) }, Vector3.down, p => flat(p) * look.WallUv, Color.white, UnityEngine.Rendering.ShadowCastingMode.TwoSided);
            // A hair above the floors either side, where they meet under it.
            Room.Surface(holder, "Doorway floor", look.Floor, new[] { P(-w, .004f, 0), P(w, .004f, 0), P(w, .004f, depth), P(-w, .004f, depth) }, Vector3.up, p => flat(p) * look.FloorUv, look.FloorColour, UnityEngine.Rendering.ShadowCastingMode.On);
            Room.Box(holder, parts, "Doorway side collider", P(-w - .05f, h / 2, depth / 2), new Vector3(.1f, h, depth));
            Room.Box(holder, parts, "Doorway side collider", P(w + .05f, h / 2, depth / 2), new Vector3(.1f, h, depth));
            Room.Box(holder, parts, "Doorway top collider", P(0, h + .05f, depth / 2), new Vector3(2 * w + .2f, .1f, depth));
            Room.Box(holder, parts, "Doorway floor collider", P(0, -.15f, depth / 2), new Vector3(2 * w + .2f, .3f, depth + .1f));
        }
    }
}
