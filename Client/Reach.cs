using System.Collections.Generic;
using EFT.Hideout;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HideoutExtensions
{
    /// <summary>
    /// Two limits the game sets for a hideout of its own size, which rooms past its ends would run into. The overview's
    /// free camera stays inside one box (FreeSpaceCameraMoveSettings._bounds, its position and scale), so that grows over
    /// the rooms you can see. And each area's culling_triggers switch its parts off while the player or camera is outside
    /// them, so a room joined on by a bare opening joins those triggers too, or from inside it you'd look back down a range
    /// with its targets gone.
    /// </summary>
    internal static class Reach
    {
        // Past a room's edge, so its far wall isn't at the very border of the view.
        private const float Margin = 3;
        private static readonly Dictionary<Transform, (Vector3 position, Vector3 scale)> originals = new Dictionary<Transform, (Vector3, Vector3)>();

        public static void Apply(Scene scene, IList<SpotInfo> visible, IList<SpotInfo> joined)
        {
            GrowCamera(visible);
            foreach (SpotInfo spot in joined) GrowTriggers(scene, spot);
        }

        private static void GrowCamera(IList<SpotInfo> rooms)
        {
            foreach (HideoutCamera camera in Object.FindObjectsOfType<HideoutCamera>(true))
            {
                FreeSpaceCameraMoveSettings settings = camera._freeSpaceCameraMoveSettings;
                Transform bounds = settings?._bounds;
                if (bounds == null) continue;
                if (!originals.TryGetValue(bounds, out var start)) originals[bounds] = start = (bounds.localPosition, bounds.localScale);
                // The game reads the box as a local position and scale, against the camera's own local position.
                var box = new Bounds(start.position, start.scale);
                foreach (SpotInfo spot in rooms)
                {
                    Vector3 lo = Local(bounds, new Vector3(spot.Room.Min[0] - Margin, 0, spot.Room.Min[1] - Margin));
                    Vector3 hi = Local(bounds, new Vector3(spot.Room.Max[0] + Margin, 0, spot.Room.Max[1] + Margin));
                    box.Encapsulate(new Vector3(lo.x, start.position.y, lo.z));
                    box.Encapsulate(new Vector3(hi.x, start.position.y, hi.z));
                }
                bounds.localPosition = box.center;
                bounds.localScale = new Vector3(box.size.x, start.scale.y, box.size.z);
                settings.UpdateBounds();
            }
        }

        private static Vector3 Local(Transform bounds, Vector3 world) => bounds.parent != null ? bounds.parent.InverseTransformPoint(world) : world;

        // Every culling trigger the opening stands in, at any height (the player's and the overview camera's alike), grown
        // sideways over the room and no higher or lower.
        private static void GrowTriggers(Scene scene, SpotInfo spot)
        {
            Vector2 door = new Vector2(spot.Door.Position[0], spot.Door.Position[2]);
            foreach (GameObject top in scene.GetRootGameObjects())
                foreach (BoxCollider box in top.GetComponentsInChildren<BoxCollider>(true))
                {
                    if (box.transform.parent == null || box.transform.parent.name != "culling_triggers") continue;
                    float height = box.transform.TransformPoint(box.center).y;
                    Vector3 at = box.transform.InverseTransformPoint(new Vector3(door.x, height, door.y));
                    if (Mathf.Abs(at.x - box.center.x) > box.size.x / 2 || Mathf.Abs(at.z - box.center.z) > box.size.z / 2) continue;
                    var grown = new Bounds(box.center, box.size);
                    foreach (float x in new[] { spot.Room.Min[0] - Margin, spot.Room.Max[0] + Margin })
                        foreach (float z in new[] { spot.Room.Min[1] - Margin, spot.Room.Max[1] + Margin })
                        {
                            Vector3 corner = box.transform.InverseTransformPoint(new Vector3(x, height, z));
                            grown.Encapsulate(new Vector3(corner.x, box.center.y, corner.z));
                        }
                    box.center = grown.center;
                    box.size = grown.size;
                }
        }
    }
}
