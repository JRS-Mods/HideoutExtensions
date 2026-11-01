using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using UnityEngine;

namespace HideoutExtensions
{
    /// <summary>
    /// A door in a built doorway that you open, close or breach with your interact key: the metal door from the room
    /// behind the emergency wall, its leaf swinging on its hinge into the room it faces, solid, with the sounds of the
    /// hideout's own doors. Each stays as you left it until the hideout unloads.
    /// </summary>
    internal sealed class SwingDoor : MonoBehaviour
    {
        private const float OpenAngle = 95, BreachAngle = 105, Speed = 110, BreachSpeed = 650;
        private static readonly Dictionary<string, bool> left = new Dictionary<string, bool>();

        private Transform leaf;
        private Vector3 hinge, closedPosition;
        private Quaternion closedRotation;
        private float sign = 1, angle, target, speed = Speed;
        private string key;
        private SceneParts parts;

        internal bool IsOpen => target > 0;

        /// <summary>
        /// Makes <paramref name="door"/> (a copy of the metal door, standing in its doorway facing <paramref name="facing"/>)
        /// work, with its interact zone round <paramref name="centre"/> (the doorway's middle on the floor) through
        /// <paramref name="depth"/> of wall; null if the copy has no leaf.
        /// </summary>
        public static SwingDoor Make(Transform door, Site site, string key, SceneParts parts, Vector3 centre, float facing, float depth)
        {
            Transform leaf = door.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Outside_Door_Metal_door");
            if (leaf == null) return null;
            SwingDoor swing = door.gameObject.AddComponent<SwingDoor>();
            swing.leaf = leaf; swing.key = key; swing.parts = parts;
            // The leaf's origin is its hinge.
            swing.hinge = leaf.position; swing.closedPosition = leaf.position; swing.closedRotation = leaf.rotation;
            var shown = leaf.GetComponentsInChildren<MeshRenderer>().Where(r => r.name.IndexOf("SHADOW", StringComparison.Ordinal) < 0).ToArray();
            Quaternion face = Quaternion.Euler(0, facing, 0);
            if (shown.Length > 0)
            {
                Bounds bounds = shown[0].bounds;
                foreach (MeshRenderer r in shown) bounds.Encapsulate(r.bounds);
                // It swings into the room the door faces.
                Vector3 swung = Quaternion.AngleAxis(90, Vector3.up) * (bounds.center - swing.hinge);
                swing.sign = Vector3.Dot(swung, face * Vector3.forward) >= 0 ? 1 : -1;
                Solid(leaf, bounds, parts.ColliderLayer);
            }

            var zone = new GameObject("Extension door " + site.Spot.Name) { layer = LayerMask.NameToLayer("Interactive") };
            zone.transform.SetParent(door, false);
            zone.transform.SetPositionAndRotation(centre + Vector3.up * Room.OpeningHeight / 2, face);
            var box = zone.AddComponent<BoxCollider>();
            box.isTrigger = true; box.size = new Vector3(Room.OpeningWidth + .1f, Room.OpeningHeight, depth + .8f);
            var interactive = zone.AddComponent<ExtensionInteractive>();
            interactive.Site = site; interactive.Role = Role.Door; interactive.Door = swing;

            if (left.TryGetValue(key, out bool open) && open) { swing.angle = swing.target = OpenAngle; swing.Pose(); }
            return swing;
        }

        // A box collider round the leaf, on the bunker's collider layer, turning with it.
        private static void Solid(Transform leaf, Bounds world, int layer)
        {
            var go = new GameObject("Extension door leaf collider") { layer = layer };
            go.transform.SetParent(leaf, false);
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = world.center + Vector3.Scale(world.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 local = go.transform.InverseTransformPoint(corner);
                min = Vector3.Min(min, local); max = Vector3.Max(max, local);
            }
            var box = go.AddComponent<BoxCollider>();
            box.center = (min + max) / 2;
            box.size = Vector3.Max(max - min, Vector3.one * .06f);
        }

        internal static void Forget() => left.Clear();

        internal void Open() => Swing(OpenAngle, Speed, parts.OpenSounds);

        internal void Close() => Swing(0, Speed, null);

        internal void Breach()
        {
            Swing(BreachAngle, BreachSpeed, null);
            Play(parts.BreachSound != null ? parts.BreachSound : parts.OpenSounds.FirstOrDefault(), 1, 60);
        }

        private void Swing(float to, float rate, AudioClip[] sound)
        {
            target = to; speed = rate;
            left[key] = to > 0;
            if (sound != null && sound.Length > 0) Play(sound[UnityEngine.Random.Range(0, sound.Length)], .7f, 35);
        }

        private void Update()
        {
            if (Mathf.Approximately(angle, target)) return;
            bool closing = target < angle;
            angle = Mathf.MoveTowards(angle, target, speed * Time.deltaTime);
            Pose();
            if (closing && angle <= 0 && parts.ShutSounds.Length > 0) Play(parts.ShutSounds[UnityEngine.Random.Range(0, parts.ShutSounds.Length)], .4f, 50);
        }

        private void Pose()
        {
            Quaternion turn = Quaternion.AngleAxis(sign * angle, Vector3.up);
            leaf.SetPositionAndRotation(hinge + turn * (closedPosition - hinge), turn * closedRotation);
        }

        // Through the game's own audio, like its doors; plainly if that isn't there.
        private void Play(AudioClip clip, float volume, int rolloff)
        {
            if (clip == null) return;
            Vector3 at = hinge + Vector3.up * .2f;
            try
            {
                if (MonoBehaviourSingleton<BetterAudio>.Instantiated)
                {
                    MonoBehaviourSingleton<BetterAudio>.Instance.PlayAtPoint(at, clip, BetterAudio.AudioSourceGroupType.InteractiveObjects, rolloff, volume, EOcclusionTest.None, null, true, true);
                    return;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("A door's sound: " + e.Message); }
            AudioSource.PlayClipAtPoint(clip, at, volume);
        }
    }
}
