using Comfort.Common;
using EFT;
using EFT.Hideout;
using UnityEngine;

namespace HideoutExtensions
{
    /// <summary>
    /// The hideout's STYLE object (the root of its walls, floors and ceilings), which everything in the mod waits for,
    /// looked up for all of it in one place. The game keeps its hideout object for the whole session once it has one, so
    /// that alone doesn't mean you're in the hideout: in a raid (a game world that isn't the hideout's) nothing is looked
    /// up at all, and otherwise the scene is searched at most every couple of seconds, and the answer kept until the
    /// hideout unloads.
    /// </summary>
    internal static class HideoutScene
    {
        private static GameObject style;
        private static float next;

        internal static bool InRaid => Singleton<GameWorld>.Instantiated && !(Singleton<GameWorld>.Instance is HideoutGameWorld);

        internal static GameObject Style
        {
            get
            {
                if (style != null) return style;
                if (Time.unscaledTime < next) return null;
                next = Time.unscaledTime + 2;
                if (InRaid || !Singleton<HideoutRepresentation>.Instantiated) return null;
                return style = GameObject.Find("STYLE");
            }
        }

        private static HideoutScreenRear screen;
        private static float nextScreen;

        /// <summary>The hideout is on screen: its overview, or walking round it. The screen is found once and kept.</summary>
        internal static bool Shown
        {
            get
            {
                if (InRaid) return false;
                if (Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance is HideoutGameWorld world && world.MainPlayer != null && world.MainPlayer.gameObject.activeInHierarchy) return true;
                if (screen == null && Time.unscaledTime >= nextScreen) { nextScreen = Time.unscaledTime + 2; screen = Object.FindObjectOfType<HideoutScreenRear>(true); }
                return screen != null && screen.isActiveAndEnabled;
            }
        }
    }
}
