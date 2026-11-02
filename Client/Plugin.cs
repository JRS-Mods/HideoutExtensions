using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace HideoutExtensions
{
    /// <summary>
    /// Hideout Extensions: rooms you build onto the hideout. Each spot (from the server mod) gets a prompt on its wall;
    /// building it takes its materials like a hideout upgrade, then a metal door appears there and leads into a new room.
    /// It also makes the hideout itself lighter to draw (<see cref="Performance"/>).
    /// </summary>
    [BepInPlugin(Id, "JRS-HideoutExtensions", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "jrs.hideoutextensions";
        public const string Version = "1.1.0";
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static string Folder;
        private Harmony harmony;
        private bool ready;

        private void Awake()
        {
            Log = Logger;
            Folder = System.IO.Path.GetDirectoryName(Info.Location);
            Enabled = Config.Bind("General", "Enabled", true,
                "Put room extensions in the hideout. Off: the hideout is left exactly as the game has it (built rooms stay built and come back when it's on).");
            Performance.Bind(Config);
            AreaMover.Bind(Config);
            harmony = new Harmony(Id);
            try { Interaction.Patch(harmony); ready = true; }
            catch (Exception e) { Logger.LogError("The interaction prompt couldn't be set up, so room extensions are off this session: " + e); }
            try { Lighting.Patch(harmony); }
            catch (Exception e) { Logger.LogError("The light switch couldn't be followed, so the rooms' lamps stay as they are: " + e); }
            Logger.LogInfo($"Hideout Extensions {Version} loaded.");
        }

        private void Update()
        {
            if (ready) Extensions.Tick(Enabled.Value);
            Performance.Tick();
            AreaMover.Tick();
        }

        private void OnDestroy() => harmony?.UnpatchSelf();
    }
}
