using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace HideoutExtensions
{
    /// <summary>
    /// A navmesh for the rooms the mod builds. The game's own was baked before they existed (with the corridor sealed),
    /// so pathfinding (K9 Companion's dog, for one) didn't know them. This one is built from the rooms' own
    /// colliders after every layout (floors, walls, pillars, blocks, the doorways' linings), for the same agent the
    /// game's queries use but slimmer, so the 1.05 m doorways stay open, and joined to the hideout's own navmesh, and
    /// across every doorway, with links. The doors themselves aren't in it: a shut one is in the way.
    /// </summary>
    internal static class Navigation
    {
        private const float Radius = .25f, Through = .75f;
        private static NavMeshDataInstance data;
        private static readonly List<NavMeshLinkInstance> links = new List<NavMeshLinkInstance>();

        /// <summary>
        /// Builds it for <paramref name="rooms"/> from the colliders under <paramref name="root"/>, with links through
        /// <paramref name="doorways"/> (each a floor point mid-doorway and the way its first side faces, and how far the
        /// other side is behind it).
        /// </summary>
        public static void Build(Transform root, IList<SpotInfo> rooms, IList<(Vector3 at, float facing, float depth)> doorways)
        {
            Clear();
            if (rooms.Count == 0) return;
            try
            {
                var sources = new List<NavMeshBuildSource>();
                foreach (BoxCollider box in root.GetComponentsInChildren<BoxCollider>())
                {
                    if (box.isTrigger || !box.enabled || box.name == Room.BulletBoxName || InDoor(box.transform, root)) continue;
                    sources.Add(new NavMeshBuildSource
                    {
                        shape = NavMeshBuildSourceShape.Box,
                        size = box.size,
                        transform = box.transform.localToWorldMatrix * Matrix4x4.Translate(box.center),
                        area = 0,
                    });
                }
                // Over the rooms and the doorways into them, from below the floor to head height (not onto their roofs).
                var bounds = new Bounds(new Vector3(rooms[0].Room.Min[0], 0, rooms[0].Room.Min[1]), Vector3.zero);
                foreach (SpotInfo spot in rooms)
                {
                    bounds.Encapsulate(new Vector3(spot.Room.Min[0], spot.Room.Floor, spot.Room.Min[1]));
                    bounds.Encapsulate(new Vector3(spot.Room.Max[0], spot.Room.Floor, spot.Room.Max[1]));
                }
                foreach (var doorway in doorways) bounds.Encapsulate(doorway.at);
                bounds.Expand(new Vector3(2, 0, 2));
                bounds.SetMinMax(new Vector3(bounds.min.x, bounds.min.y - .5f, bounds.min.z), new Vector3(bounds.max.x, bounds.min.y + 2.5f, bounds.max.z));

                NavMeshBuildSettings settings = NavMesh.GetSettingsByID(0);
                if (settings.agentTypeID == -1 && NavMesh.GetSettingsCount() > 0) settings = NavMesh.GetSettingsByIndex(0);
                settings.agentRadius = Mathf.Min(settings.agentRadius, Radius);
                NavMeshData built = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
                if (built == null) { Plugin.Log.LogWarning("The rooms' navmesh couldn't be built."); return; }
                data = NavMesh.AddNavMeshData(built);

                int joined = 0;
                foreach (var (at, facing, depth) in doorways)
                {
                    Vector3 face = Quaternion.Euler(0, facing, 0) * Vector3.forward;
                    if (!NavMesh.SamplePosition(at + face * Through, out NavMeshHit near, 1.2f, NavMesh.AllAreas)
                        || !NavMesh.SamplePosition(at - face * (depth + Through), out NavMeshHit far, 1.2f, NavMesh.AllAreas)) continue;
                    NavMeshLinkInstance link = NavMesh.AddLink(new NavMeshLinkData
                    {
                        startPosition = near.position, endPosition = far.position, width = .8f, bidirectional = true,
                        agentTypeID = settings.agentTypeID, area = 0, costModifier = -1,
                    });
                    if (link.valid) { links.Add(link); joined++; }
                }
                Plugin.Log.LogInfo($"Rooms' navmesh: {sources.Count} collider(s), {joined} of {doorways.Count} doorway(s) joined.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("The rooms' navmesh couldn't be built: " + e.Message); Clear(); }
        }

        // The working doors (and their frames) are left out: they open and shut.
        private static bool InDoor(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent) if (t.name == "Extension door") return true;
            return false;
        }

        public static void Clear()
        {
            foreach (NavMeshLinkInstance link in links) if (link.valid) link.Remove();
            links.Clear();
            if (data.valid) data.Remove();
            data = default;
        }
    }
}
