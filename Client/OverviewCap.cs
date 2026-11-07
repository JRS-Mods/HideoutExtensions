using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HideoutExtensions
{
    /// <summary>
    /// The hideout covers everything outside its rooms with one black cap at ceiling height (Hideout_Stub, shader
    /// Custom/Black_2sided); that's the black around the rooms in the overview, and it lies over the annex too. Once a
    /// room is built the cap is drawn with this mod's own black material instead (hideoutextensions.bundle, shader
    /// HideoutExtensions/CapWithHoles), which leaves out the built rooms' floors, so they show from above like the
    /// hideout's own; the walls between them stay black. Without the bundle the rooms work but stay black on the overview.
    /// </summary>
    internal static class OverviewCap
    {
        private const int MaxHoles = 16;
        private static AssetBundle bundle;
        private static Shader shader;
        private static bool tried, capTried;
        private static Material original, holed;
        private static MeshRenderer cap;

        public static void Apply(MeshRenderer renderer, IList<Rect> holes)
        {
            if (renderer == null) return;
            if (renderer != cap) { cap = renderer; original = renderer.sharedMaterial; holed = null; }
            if (holes.Count == 0) { if (original != null) cap.sharedMaterial = original; return; }
            if (holed == null)
            {
                Shader s = Load();
                if (s == null) return;
                holed = new Material(s) { name = "HideoutExtensions cap" };
            }
            var list = new List<Vector4>(MaxHoles);
            for (int i = 0; i < MaxHoles; i++)
                list.Add(i < holes.Count ? new Vector4(holes[i].xMin, holes[i].yMin, holes[i].xMax, holes[i].yMax) : Vector4.zero);
            holed.SetVectorArray("_Holes", list);
            holed.SetFloat("_HoleCount", Mathf.Min(holes.Count, MaxHoles));
            cap.sharedMaterial = holed;
        }

        private static Material black;

        /// <summary>Plain black (the cap's shader with no holes), for dark doorways; null without the bundle.</summary>
        public static Material Black()
        {
            if (black != null) return black;
            Shader s = Load();
            if (s == null) return null;
            black = new Material(s) { name = "HideoutExtensions black" };
            black.SetFloat("_HoleCount", 0);
            return black;
        }

        /// <summary>A flat colour that ignores the lighting (the cap's shader with no holes), for painted marks; null without the bundle.</summary>
        public static Material Paint(Color colour)
        {
            Shader s = Load();
            if (s == null) return null;
            var paint = new Material(s) { name = "HideoutExtensions paint" };
            paint.SetColor("_Color", colour);
            paint.SetFloat("_HoleCount", 0);
            return paint;
        }

        private static Shader Load()
        {
            if (capTried) return shader;
            capTried = true;
            shader = BundleShader("CapWithHoles");
            if (shader == null) Plugin.Log.LogWarning("The overview cap shader didn't load; built rooms stay black on the overview.");
            return shader;
        }

        /// <summary>One of the shaders in hideoutextensions.bundle (loaded once), or null without it.</summary>
        private static Shader BundleShader(string name)
        {
            if (!tried)
            {
                tried = true;
                string file = Path.Combine(Plugin.Folder, "hideoutextensions.bundle");
                try
                {
                    if (!File.Exists(file)) Plugin.Log.LogWarning($"No {file}: built rooms stay black on the overview.");
                    else bundle = AssetBundle.LoadFromFile(file);
                }
                catch (Exception e) { Plugin.Log.LogWarning("hideoutextensions.bundle couldn't be loaded: " + e.Message); }
            }
            try
            {
                Shader s = bundle?.LoadAsset<Shader>(name);
                return s != null && s.isSupported ? s : null;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"The {name} shader couldn't be loaded: " + e.Message); return null; }
        }
    }
}
