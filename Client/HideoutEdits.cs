using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HideoutExtensions
{
    /// <summary>
    /// Changes a built spot asks for to the hideout's own things, such as gym gear moved out of a doorway's way or a mat
    /// that would lie in it: found by their path in the scene, moved from where the game put them or hidden. Doing it
    /// again does nothing more, and the hideout has them back as they were on the next visit.
    /// </summary>
    internal static class HideoutEdits
    {
        private static readonly Dictionary<Transform, Vector3> placed = new Dictionary<Transform, Vector3>();
        private static readonly HashSet<string> missing = new HashSet<string>();

        public static void Apply(Transform style, SpotInfo spot)
        {
            if (style == null || spot.HideoutEdits == null) return;
            GameObject[] roots = style.gameObject.scene.GetRootGameObjects();
            foreach (HideoutEditInfo edit in spot.HideoutEdits)
            {
                if (string.IsNullOrEmpty(edit.Path)) continue;
                string[] names = edit.Path.Split(new[] { '/' }, 2);
                Transform target = roots.Where(r => r.name == names[0]).Select(r => names.Length > 1 ? r.transform.Find(names[1]) : r.transform).FirstOrDefault(t => t != null);
                if (target == null)
                {
                    if (missing.Add(edit.Path)) Plugin.Log.LogWarning($"'{spot.Name}': the hideout has no {edit.Path} to change.");
                    continue;
                }
                if (edit.Hide && target.gameObject.activeSelf) target.gameObject.SetActive(false);
                if (edit.Move != null && edit.Move.Length == 3)
                {
                    // In its parent's space, so it goes along if the area it's in is moved.
                    if (!placed.TryGetValue(target, out Vector3 original)) placed[target] = original = target.localPosition;
                    Vector3 move = new Vector3(edit.Move[0], edit.Move[1], edit.Move[2]);
                    target.localPosition = original + (target.parent != null ? target.parent.InverseTransformVector(move) : move);
                }
            }
        }

        internal static void Forget() { placed.Clear(); missing.Clear(); }
    }
}
