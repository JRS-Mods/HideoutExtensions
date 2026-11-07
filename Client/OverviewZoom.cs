using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using EFT;
using EFT.Hideout;
using HarmonyLib;
using UnityEngine;

namespace HideoutExtensions
{
    /// <summary>
    /// Zooming out further on the hideout's overview. The game's zoom only narrows the camera's view (HideoutCamera.Zoom
    /// clamps the FOV between 10 and its default), so it can't go wider than where it starts. Past that, scrolling out
    /// backs the overview's virtual camera away along its own view, up and away from the spot it looks at; scrolling in
    /// brings it back before the game's own zoom takes over again, and the game's zoom reset (leaving an area) resets it.
    /// The overview's own culling boxes (the culling_triggers starting at the cap, y 3.1 to 26.1) switch the workbench,
    /// range and intel center parts off once the camera rises out of them, so those grow upward to match.
    /// </summary>
    internal static class OverviewZoom
    {
        internal static ConfigEntry<float> Extra;

        // Metres per scroll notch for each unit of the game's zoom step (it zooms by 4 per notch: 6 m).
        private const float MetresPerStep = 1.5f;

        private static readonly AccessTools.FieldRef<HideoutCamera, float> currentFov = AccessTools.FieldRefAccess<HideoutCamera, float>("_currentFOV");
        private static readonly AccessTools.FieldRef<HideoutCamera, float> defaultFov = AccessTools.FieldRefAccess<HideoutCamera, float>("_defaultFOV");
        private static readonly AccessTools.FieldRef<HideoutCamera, ECameraMoveType> moveType = AccessTools.FieldRefAccess<HideoutCamera, ECameraMoveType>("_cameraMoveType");

        private sealed class Pull { public float Target, Now; }
        private static readonly Dictionary<HideoutCamera, Pull> pulls = new Dictionary<HideoutCamera, Pull>();
        // The scene whose culling boxes are raised already (once per hideout load, high enough for the furthest pull).
        private static int raisedScene = -1;
        // The fog's density as the game has it, while the camera's backed away (null: untouched).
        private static float? fogBase;
        private static int fogScene;
        private static HideoutPlayerOwner owner;
        private static float nextLook;

        internal static void Bind(ConfigFile config)
        {
            Extra = config.Bind("Overview camera", "Extra zoom out (m)", 80f,
                new ConfigDescription("How far past the game's own limit scrolling out on the hideout overview backs the camera away. 0: the game's zoom only.",
                    new AcceptableValueRange<float>(0f, 200f)));
        }

        internal static void Patch(Harmony harmony)
        {
            var zoom = AccessTools.Method(typeof(HideoutCamera), nameof(HideoutCamera.Zoom), new[] { typeof(float?) })
                       ?? throw new MissingMethodException(nameof(HideoutCamera), "Zoom(float?)");
            harmony.Patch(zoom, prefix: new HarmonyMethod(typeof(OverviewZoom), nameof(Zooming)));
        }

        // false skips the game's zoom: scrolling in while backed away comes back first.
        private static bool Zooming(HideoutCamera __instance, float? zoom)
        {
            try
            {
                if (__instance == null) return true;
                if (!pulls.TryGetValue(__instance, out Pull pull)) pulls[__instance] = pull = new Pull();
                if (zoom == null) { pull.Target = 0; return true; }
                if (moveType(__instance) != ECameraMoveType.FreeSpace || Extra.Value <= 0) { pull.Target = 0; return true; }
                float step = zoom.Value * MetresPerStep;
                if (step > 0 && currentFov(__instance) >= defaultFov(__instance) - 0.01f)
                {
                    pull.Target = Mathf.Min(pull.Target + step, Extra.Value);
                    return true;
                }
                if (step < 0 && pull.Target > 0)
                {
                    pull.Target = Mathf.Max(pull.Target + step, 0);
                    return false;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Overview zoom: " + e.Message); }
            return true;
        }

        internal static void Tick()
        {
            if (pulls.Count == 0) return;
            // Walking about (or the hideout gone): straight back, and the fog as the game has it.
            bool away = false;
            if (fogBase != null && Time.unscaledTime >= nextLook)
            {
                nextLook = Time.unscaledTime + .5f;
                if (owner == null) owner = UnityEngine.Object.FindObjectOfType<HideoutPlayerOwner>();
                away = owner == null || owner.FirstPersonMode;
            }
            List<HideoutCamera> gone = null;
            foreach (var entry in pulls)
            {
                HideoutCamera camera = entry.Key;
                Pull pull = entry.Value;
                if (camera == null) { (gone ??= new List<HideoutCamera>()).Add(camera); continue; }
                if (away && pull.Now > 0)
                {
                    pull.Target = pull.Now = 0;
                    try { Place(camera, 0); } catch (Exception) { }
                    continue;
                }
                if (Mathf.Approximately(pull.Now, pull.Target)) continue;
                float gap = Mathf.Abs(pull.Target - pull.Now);
                pull.Now = Mathf.MoveTowards(pull.Now, pull.Target, Mathf.Max(20f, gap * 6f) * Time.unscaledDeltaTime);
                try { Place(camera, pull.Now); }
                catch (Exception e) { Plugin.Log.LogWarning("Overview zoom: " + e.Message); pull.Target = pull.Now = 0; }
            }
            if (gone != null) foreach (HideoutCamera camera in gone) pulls.Remove(camera);
            if (fogBase != null)
            {
                bool out_ = false;
                if (!away) foreach (Pull p in pulls.Values) if (p.Now > 0) { out_ = true; break; }
                if (!out_)
                {
                    // Only into the scene it came from: a raid loaded meanwhile has fog of its own.
                    if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle == fogScene) RenderSettings.fogDensity = fogBase.Value;
                    fogBase = null;
                }
            }
        }

        private static void Place(HideoutCamera camera, float pull)
        {
            FreeSpaceCameraMoveSettings settings = camera._freeSpaceCameraMoveSettings;
            Transform eye = camera.Transform;
            if (settings == null || eye == null) return;
            Quaternion turn = Quaternion.Euler(settings.DefaultVirtualCameraLocalRotation);
            eye.localPosition = settings.DefaultVirtualCameraLocalPosition - turn * Vector3.forward * pull;
            if (pull <= 0) return;
            RaiseCulling(eye.position.y + Extra.Value + 10f);
            // The hideout's fog is exponential, so from further back the whole hideout drowns in it: thin it so the
            // floor gets as much fog as from where the game has the camera.
            float down = Mathf.Max(.2f, -eye.forward.y);
            float near = Mathf.Max(5f, (eye.position.y - pull * down) / down);
            if (fogBase == null) { fogBase = RenderSettings.fogDensity; fogScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle; }
            RenderSettings.fogDensity = fogBase.Value * near / (near + pull);
        }

        // The overview's culling boxes are the ones that start up at the cap; the player's ones stand on the floor.
        private static void RaiseCulling(float top)
        {
            GameObject style = HideoutScene.Style;
            if (style == null || style.scene.handle == raisedScene) return;
            raisedScene = style.scene.handle;
            int count = 0;
            foreach (GameObject go in style.scene.GetRootGameObjects())
                foreach (BoxCollider box in go.GetComponentsInChildren<BoxCollider>(true))
                {
                    Transform t = box.transform;
                    if (t.parent == null || t.parent.name != "culling_triggers") continue;
                    if (Vector3.Dot(t.up, Vector3.up) < 0.99f) continue;
                    float scale = t.lossyScale.y;
                    if (scale <= 0) continue;
                    float bottom = t.TransformPoint(box.center - new Vector3(0, box.size.y / 2, 0)).y;
                    float now = t.TransformPoint(box.center + new Vector3(0, box.size.y / 2, 0)).y;
                    if (bottom < 2.5f || now >= top) continue;
                    float more = (top - now) / scale;
                    box.size += new Vector3(0, more, 0);
                    box.center += new Vector3(0, more / 2, 0);
                    count++;
                }
            Plugin.Log.LogInfo($"Overview zoom: {count} overview culling box(es) raised to {top:0} m.");
        }
    }
}
