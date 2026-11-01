using System;
using System.Collections.Generic;
using System.Linq;
using EFT.Hideout;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace HideoutExtensions
{
    /// <summary>
    /// The rooms' lamps follow the hideout's light switch (the overview's off, candles, bulbs, fluorescent tubes,
    /// Christmas and Halloween) the way the areas' own lamps do. Each lamp spot has a fluorescent tube and a hanging bulb,
    /// both copied from the hideout's own light groups with the game's lamp script still on them: the tube shows for
    /// fluorescent tubes, off and candles, the bulb for bulbs and the holiday lights, and they're lit unless the switch is
    /// off or on candles (the rooms have no candles), switching with the game's own flicker and sound.
    /// </summary>
    internal static class Lighting
    {
        private sealed class Fixture { public Transform Tube, Bulb; }

        private static readonly List<Fixture> fixtures = new List<Fixture>();
        private static ELightingLevel? level;

        internal static void Patch(Harmony harmony)
        {
            var set = AccessTools.Method(typeof(HideoutController), nameof(HideoutController.SetLightLevel), new[] { typeof(ELightingLevel) })
                      ?? throw new MissingMethodException(nameof(HideoutController), "SetLightLevel(ELightingLevel)");
            harmony.Patch(set, postfix: new HarmonyMethod(typeof(Lighting), nameof(Switched)));
        }

        private static void Switched(ELightingLevel level)
        {
            Lighting.level = level;
            fixtures.RemoveAll(f => f.Tube == null && f.Bulb == null);
            foreach (Fixture fixture in fixtures) Apply(fixture);
        }

        private static ELightingLevel Current
        {
            get
            {
                if (level == null)
                    try { level = HideoutLocalData.GlobalLightingLevel; }
                    catch (Exception) { level = ELightingLevel.FluorescentLamps; }
                return level.Value;
            }
        }

        /// <summary>A lamp spot's two fittings (either may be missing), switched to the current setting.</summary>
        internal static void Add(Transform tube, Transform bulb)
        {
            if (tube == null && bulb == null) return;
            fixtures.RemoveAll(f => f.Tube == null && f.Bulb == null);
            var fixture = new Fixture { Tube = tube, Bulb = bulb };
            fixtures.Add(fixture);
            Apply(fixture);
        }

        internal static void Clear() { fixtures.Clear(); level = null; }

        private static void Apply(Fixture fixture)
        {
            ELightingLevel now = Current;
            bool bulbs = fixture.Bulb != null && (now == ELightingLevel.LightBulbs || now == ELightingLevel.ChristmasLights || now == ELightingLevel.HalloweenLights);
            bool lit = now != ELightingLevel.Off && now != ELightingLevel.Candles;
            Show(fixture.Tube, !bulbs || fixture.Bulb == null, lit);
            Show(fixture.Bulb, bulbs, lit);
        }

        private static void Show(Transform fitting, bool shown, bool lit)
        {
            if (fitting == null) return;
            if (fitting.gameObject.activeSelf != shown) fitting.gameObject.SetActive(shown);
            if (!shown) return;
            LampController[] lamps = fitting.GetComponentsInChildren<LampController>(true);
            if (lamps.Length == 0)
            {
                foreach (Light light in fitting.GetComponentsInChildren<Light>(true)) light.enabled = lit;
                return;
            }
            foreach (LampController lamp in lamps)
            {
                try
                {
                    lamp.Enabled = lit;
                    if (lit && lamp.LampState == Turnable.EState.Off) lamp.Switch(Turnable.EState.On);
                }
                catch (Exception e)
                {
                    // The game's script didn't take it: the lights themselves, then.
                    Plugin.Log.LogWarning($"A room lamp ({lamp.name}) didn't switch: {e.Message}");
                    foreach (Light light in lamp.GetComponentsInChildren<Light>(true)) light.enabled = lit;
                }
            }
        }
    }
}
