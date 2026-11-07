using System;
using System.Collections.Generic;
using EFT.Hideout;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HideoutExtensions
{
    /// <summary>
    /// The areas' interaction zones out of the doorways. An area's prompt (OPEN STASH, say) comes from an invisible trigger
    /// box round it (its *_raycast_collider), and the player's look takes the first one it meets: a door standing inside
    /// one (the west hall's, where the stash's tall safe was) could only be opened from an angle that missed the box. Each
    /// such box is split round the doorway: the part either side stays (another box on the same object, so it's still the
    /// area's), the doorway is the door's. Put back as the game has them when the rooms are laid out again or the hideout goes.
    /// </summary>
    internal static class DoorwayClear
    {
        private sealed class Cut
        {
            public BoxCollider Box;
            public Vector3 Center, Size;
            public bool Enabled;
            public readonly List<BoxCollider> Added = new List<BoxCollider>();
        }

        private static readonly List<Cut> cuts = new List<Cut>();

        // Round a doorway: past its sides and in front of and behind it, a step's reach.
        private const float Side = .15f, Reach = .9f, Smallest = .15f;

        public static void Apply(Scene scene, IList<(Vector3 at, float facing, float width, float height)> doors)
        {
            Restore();
            if (doors.Count == 0) return;
            int split = 0;
            foreach (GameObject top in scene.GetRootGameObjects())
                foreach (BoxCollider box in top.GetComponentsInChildren<BoxCollider>(true))
                {
                    if (!box.isTrigger || box.GetComponent<InteractableObject>() == null || box.GetComponentInParent<HideoutArea>(true) == null) continue;
                    foreach (var door in doors)
                        if (Clear(box, door.at, door.facing, door.width, door.height)) { split++; break; }
                }
            if (split > 0) Plugin.Log.LogInfo($"Doorways: {split} area prompt zone(s) split round the doors.");
        }

        // The doorway in the box's own space; if they meet, the box is cut along whichever of its sideways axes keeps most.
        private static bool Clear(BoxCollider box, Vector3 at, float facing, float width, float height)
        {
            float rad = facing * Mathf.Deg2Rad;
            var face = new Vector3(Mathf.Sin(rad), 0, Mathf.Cos(rad));
            var along = new Vector3(face.z, 0, -face.x);
            Transform t = box.transform;
            Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = at + along * ((i & 1) == 0 ? -1 : 1) * (width / 2 + Side) + face * ((i & 2) == 0 ? -1 : 1) * Reach + Vector3.up * ((i & 4) == 0 ? 0 : height);
                Vector3 local = t.InverseTransformPoint(corner);
                lo = Vector3.Min(lo, local); hi = Vector3.Max(hi, local);
            }
            Vector3 bLo = box.center - box.size / 2, bHi = box.center + box.size / 2;
            for (int k = 0; k < 3; k++) if (hi[k] <= bLo[k] || lo[k] >= bHi[k]) return false;

            // Which way to cut: the box's sideways axes (its up is the room's up).
            int bestAxis = -1;
            float bestKept = -1;
            foreach (int k in new[] { 0, 2 })
            {
                float kept = Mathf.Max(0, lo[k] - bLo[k]) + Mathf.Max(0, bHi[k] - hi[k]);
                if (kept > bestKept) { bestKept = kept; bestAxis = k; }
            }
            var cut = new Cut { Box = box, Center = box.center, Size = box.size, Enabled = box.enabled };
            var pieces = new List<(Vector3 lo, Vector3 hi)>();
            if (lo[bestAxis] - bLo[bestAxis] >= Smallest) { Vector3 h = bHi; h[bestAxis] = lo[bestAxis]; pieces.Add((bLo, h)); }
            if (bHi[bestAxis] - hi[bestAxis] >= Smallest) { Vector3 l = bLo; l[bestAxis] = hi[bestAxis]; pieces.Add((l, bHi)); }
            if (pieces.Count == 0) box.enabled = false;
            else
            {
                box.center = (pieces[0].lo + pieces[0].hi) / 2; box.size = pieces[0].hi - pieces[0].lo;
                for (int i = 1; i < pieces.Count; i++)
                {
                    BoxCollider more = box.gameObject.AddComponent<BoxCollider>();
                    more.isTrigger = true;
                    more.center = (pieces[i].lo + pieces[i].hi) / 2; more.size = pieces[i].hi - pieces[i].lo;
                    cut.Added.Add(more);
                }
            }
            cuts.Add(cut);
            Plugin.Log.LogInfo($"Doorways: {Path(t)} split round the door at {at} ({pieces.Count} part(s) kept).");
            return true;
        }

        /// <summary>The zones as the game has them.</summary>
        public static void Restore()
        {
            foreach (Cut cut in cuts)
            {
                foreach (BoxCollider more in cut.Added) if (more != null) UnityEngine.Object.Destroy(more);
                if (cut.Box == null) continue;
                cut.Box.center = cut.Center; cut.Box.size = cut.Size; cut.Box.enabled = cut.Enabled;
            }
            cuts.Clear();
        }

        /// <summary>The hideout is gone: nothing to put back.</summary>
        public static void Forget() => cuts.Clear();

        private static string Path(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }
    }
}
