using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using UnityEngine;

namespace HideoutExtensions
{
    /// <summary>
    /// A lighter hideout. The game fades a light's shadows out between 12 and 17 m from you and turns them off past that
    /// (CullingLightObject), but some of the hideout's shadow-casting lights were left without it (most of the Halloween
    /// lights, and a few in nearly every area), so their shadow maps are drawn every frame however far away they are.
    /// Those get the same rule. Light haze (VolumetricLight) on lights the game doesn't cull is switched off past a
    /// distance too. Turning the options off puts every light back as the game had it.
    /// </summary>
    internal static class Performance
    {
        private const float FadeStart = 12, FadeEnd = 17, HazeMargin = 2, Every = .2f;
        internal static ConfigEntry<bool> ShadowFade;
        internal static ConfigEntry<float> HazeDistance;

        private sealed class Managed
        {
            public Light Light;
            public LightShadows Shadows;
            public float Strength;
            public VolumetricLight Haze;
            public bool HazeOn = true;
        }

        private static List<Managed> lights;
        private static GameObject style;
        private static float next;
        private static bool broken;

        internal static void Bind(ConfigFile config)
        {
            ShadowFade = config.Bind("Hideout performance", "Fade far shadows", true,
                "Shadows of the hideout's lights the game forgot to cull fade out between 12 and 17 m from you, as its other lights' already do.");
            HazeDistance = config.Bind("Hideout performance", "Light haze distance", 30f,
                "Light haze (volumetric light) the game doesn't cull is switched off this far from you, in metres. 0: never.");
        }

        /// <summary>Every frame: finds the hideout's lights once per visit, then keeps them up to date a few times a second.</summary>
        internal static void Tick()
        {
            bool wanted = !broken && (ShadowFade.Value || HazeDistance.Value > 0);
            if (lights != null && (!wanted || style == null)) Release();
            if (!wanted) return;
            if (style == null)
            {
                // Not in a raid; in the hideout, once it's there (HideoutScene looks, every couple of seconds at most).
                if ((style = HideoutScene.Style) == null) return;
            }
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + Every;
            try
            {
                if (lights == null) Find(style);
                Camera camera = CameraManager.Instance.Camera ?? Camera.main;
                if (camera != null) Update(camera.transform.position);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Hideout performance is off for this session: " + e);
                Release();
                broken = true;
            }
        }

        private static void Find(GameObject style)
        {
            lights = new List<Managed>();
            foreach (GameObject root in style.scene.GetRootGameObjects())
                foreach (Light light in root.GetComponentsInChildren<Light>(true))
                {
                    if (light.type == LightType.Directional || light.GetComponentInParent<Player>() != null) continue;
                    var culling = light.GetComponent<CullingLightObject>();
                    if (culling != null && culling._cullShadowsByDistance) continue;
                    var haze = culling == null ? light.GetComponent<VolumetricLight>() : null;
                    bool shadows = light.shadows != LightShadows.None;
                    if (!shadows && (haze == null || !haze.enabled)) continue;
                    lights.Add(new Managed { Light = light, Shadows = light.shadows, Strength = light.shadowStrength, Haze = haze != null && haze.enabled ? haze : null });
                }
            Plugin.Log.LogInfo($"Hideout performance: {lights.Count(m => m.Shadows != LightShadows.None)} shadow-casting light(s) the game doesn't cull now fade like the rest; " +
                               $"{lights.Count(m => m.Haze != null)} light haze(s) the game doesn't cull.");
        }

        private static void Update(Vector3 eye)
        {
            float haze = HazeDistance.Value;
            foreach (Managed m in lights)
            {
                if (m.Light == null) continue;
                float distance = Vector3.Distance(eye, m.Light.transform.position);
                if (m.Shadows != LightShadows.None)
                {
                    float k = ShadowFade.Value ? 1 - Mathf.InverseLerp(FadeStart, FadeEnd, distance) : 1;
                    if (k <= 0) { if (m.Light.shadows != LightShadows.None) m.Light.shadows = LightShadows.None; }
                    else
                    {
                        if (m.Light.shadows == LightShadows.None) m.Light.shadows = m.Shadows;
                        m.Light.shadowStrength = m.Strength * k;
                    }
                }
                if (m.Haze != null)
                {
                    bool on = haze <= 0 || distance < haze + (m.HazeOn ? HazeMargin : -HazeMargin);
                    if (on != m.HazeOn) { m.Haze.enabled = on; m.HazeOn = on; }
                }
            }
        }

        /// <summary>Every light back as the game had it (while the hideout's still there).</summary>
        private static void Release()
        {
            if (lights != null)
                foreach (Managed m in lights)
                {
                    if (m.Light != null) { m.Light.shadows = m.Shadows; m.Light.shadowStrength = m.Strength; }
                    if (m.Haze != null && !m.HazeOn) m.Haze.enabled = true;
                }
            lights = null;
        }
    }
}
