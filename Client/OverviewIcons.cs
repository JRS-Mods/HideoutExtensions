using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Hideout;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HideoutExtensions
{
    /// <summary>
    /// On the hideout's overview: an icon (assets/build_icon.png, and build_icon_selected.png with its glow while its
    /// panel is open), as big on screen as the hideout's own area icons, over every room that can be built next, and over
    /// rooms being built with the time left. Clicking one opens a small panel with the room's cost (what you have of each, from your
    /// stash), the build time, why it's locked if it is, and a Build button. Hidden in first person and while an area's
    /// screen is open.
    /// </summary>
    internal static class OverviewIcons
    {
        private static Canvas canvas;
        private static Sprite plain, selected;
        private static AreaIcon sample;
        private static float iconSize = 64, nextSize;
        private static Site open;
        private static TMP_FontAsset font;
        private static HideoutPlayerOwner owner;
        private static HideoutScreenRear screen;
        private static AreaScreenSubstrate areaScreen;
        private static readonly Dictionary<Site, Icon> icons = new Dictionary<Site, Icon>();
        private static Panel panel;
        private static float nextText, nextCreate, nextOwner;
        private static AreaIcon[] areaIcons = new AreaIcon[0];

        private sealed class Icon { public RectTransform Rect; public Image Image; public TextMeshProUGUI Label; }

        // The badge images are the hexagon plus the glow round it: the hexagon is this much of their width.
        private const float HexagonShare = 1 / 1.24f;
        // The same for the game's own icon background (area_icon_default_back: a 66-pixel hexagon in a 104-pixel picture,
        // the rest its glow). Measuring that picture whole made ours over half as big again as the game's (1.6 times).
        private const float GameHexagonShare = 66f / 104f;

        /// <summary>Every frame while the hideout's rooms are set up.</summary>
        public static void Tick(Transform root, IEnumerable<Site> sites)
        {
            // Searching the scene is slow: at most every couple of seconds until what's looked for is there.
            if (canvas == null)
            {
                icons.Clear(); panel = null;
                if (Time.unscaledTime < nextCreate) return;
                nextCreate = Time.unscaledTime + 2;
                if (!Create(root)) return;
            }
            if ((owner == null || screen == null) && Time.unscaledTime >= nextOwner)
            {
                nextOwner = Time.unscaledTime + 1;
                if (owner == null) owner = UnityEngine.Object.FindObjectOfType<HideoutPlayerOwner>();
                if (screen == null) screen = UnityEngine.Object.FindObjectOfType<HideoutScreenRear>(true);
            }
            // The hideout stays loaded behind the main menu and the other screens: only its own screen counts.
            bool overview = owner != null && !owner.FirstPersonMode && screen != null && screen.isActiveAndEnabled && !screen.FirstPerson
                            && (areaScreen == null || !areaScreen.isActiveAndEnabled);
            canvas.enabled = overview;
            if (!overview) { panel?.Close(); return; }
            // The camera the game places its own icons with, and the same sum: where the point is in the camera's view, times the
            // window's size. WorldToScreenPoint works in the camera's own pixels, which aren't the window's while the game renders
            // at another resolution and upscales, so the icons drifted away from their rooms as the view moved.
            Camera camera = null;
            try { camera = EFT.CameraControl.CameraManager.Instance.Camera; } catch (Exception) { }
            if (camera == null) camera = Camera.main;
            if (camera == null) return;
            if (Time.unscaledTime >= nextSize) { nextSize = Time.unscaledTime + .5f; Measure(); }
            bool text = Time.unscaledTime >= nextText;
            if (text) nextText = Time.unscaledTime + .5f;
            foreach (Site site in sites)
            {
                bool shown = site.Offered;
                icons.TryGetValue(site, out Icon icon);
                if (!shown) { if (icon != null) icon.Rect.gameObject.SetActive(false); continue; }
                if (icon == null) icons[site] = icon = Make(site);
                Vector3 view = camera.WorldToViewportPoint(Centre(site.Spot));
                icon.Rect.gameObject.SetActive(view.z > 0);
                icon.Rect.position = new Vector3(view.x * Screen.width, view.y * Screen.height, 0);
                icon.Rect.sizeDelta = Vector2.one * iconSize / HexagonShare;
                icon.Image.sprite = open == site && selected != null ? selected : plain;
                if (text)
                {
                    bool building = site.Current(out long at) == Site.State.Building;
                    icon.Label.text = building && at > 0 ? Site.Duration(at - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) : "";
                }
            }
            if (panel != null && text) panel.Refresh();
        }

        // On the black cap over the room, the surface you see there until it's built, so the icon stays put on it as the view
        // turns (from down at head height it swung about against the cap).
        private static Vector3 Centre(SpotInfo spot) =>
            new Vector3((spot.Room.Min[0] + spot.Room.Max[0]) / 2, spot.Room.Floor + spot.Room.Height, (spot.Room.Min[1] + spot.Room.Max[1]) / 2);

        private static bool Create(Transform root)
        {
            areaIcons = UnityEngine.Object.FindObjectsOfType<AreaIcon>(true);
            sample = areaIcons.FirstOrDefault(i => i._backgroundImage != null);
            if (sample == null) return false;   // the overview's own icons aren't made yet
            font = sample._currentAreaLevel != null ? sample._currentAreaLevel.font : null;
            plain = Picture("build_icon.png") ?? sample._defaultSprite ?? sample._backgroundImage.sprite;
            selected = Picture("build_icon_selected.png");
            areaScreen = UnityEngine.Object.FindObjectOfType<AreaScreenSubstrate>(true);
            var go = new GameObject("Hideout extensions overview", typeof(RectTransform));
            go.transform.SetParent(root, false);
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5;
            go.AddComponent<CanvasScaler>();
            go.AddComponent<GraphicRaycaster>();
            Plugin.Log.LogInfo($"Overview icons: {plain?.name} / {selected?.name ?? "no selected picture"}, font {font?.name}.");
            return true;
        }

        private static Sprite Picture(string file)
        {
            string path = System.IO.Path.Combine(Plugin.Folder, file);
            if (!System.IO.File.Exists(path)) { Plugin.Log.LogWarning($"No {path}; the overview icon uses the game's own."); return null; }
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = file };
            if (!ImageConversion.LoadImage(texture, System.IO.File.ReadAllBytes(path))) return null;
            texture.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100);
        }

        // How big the hexagons of the hideout's own area icons are on screen: the overview's icons only (an area's own screen has
        // a far bigger one, which made ours huge), the middle size of those at full size, without their glow. The game shrinks an
        // icon to half when its area is off screen, so counting those made ours grow and shrink as the camera turned.
        private static void Measure()
        {
            var sizes = new List<float>();
            var corners = new Vector3[4];
            foreach (AreaIcon shown in areaIcons)
            {
                if (shown == null || shown._backgroundImage == null || !shown._backgroundImage.isActiveAndEnabled) continue;
                AreaWorldPanel world = shown.GetComponentInParent<AreaWorldPanel>();
                if (world == null || world.Offscreen || world.transform.localScale.x < .99f) continue;
                shown._backgroundImage.rectTransform.GetWorldCorners(corners);
                Canvas theirs = shown._backgroundImage.canvas;
                Camera camera = theirs != null && theirs.renderMode != RenderMode.ScreenSpaceOverlay ? theirs.worldCamera ?? Camera.main : null;
                Vector2 a = RectTransformUtility.WorldToScreenPoint(camera, corners[0]), b = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
                float size = Mathf.Max(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
                if (size > 8 && size < 200) sizes.Add(size);
            }
            if (sizes.Count == 0) return;
            sizes.Sort();
            iconSize = sizes[sizes.Count / 2] * GameHexagonShare;
        }

        private static Icon Make(Site site)
        {
            var rect = Child(canvas.transform, "Extension icon " + site.Spot.Name, Vector2.one * iconSize / HexagonShare);
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = plain; image.preserveAspect = true;
            // Clicks only on the hexagon, not its glow.
            image.alphaHitTestMinimumThreshold = 0;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => Open(site, rect));
            var label = Text(rect, "Time", 15, TextAlignmentOptions.Center);
            var lr = (RectTransform)label.transform;
            lr.anchorMin = new Vector2(.5f, 0); lr.anchorMax = new Vector2(.5f, 0); lr.anchoredPosition = new Vector2(0, -6); lr.sizeDelta = new Vector2(140, 22);
            return new Icon { Rect = rect, Image = image, Label = label };
        }

        private static void Open(Site site, RectTransform icon)
        {
            if (panel == null) panel = new Panel();
            open = site;
            panel.Show(site, icon);
        }

        private static RectTransform Child(Transform parent, string name, Vector2 size)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.SetParent(parent, false);
            rect.sizeDelta = size;
            return rect;
        }

        private static TextMeshProUGUI Text(Transform parent, string name, float size, TextAlignmentOptions align)
        {
            var rect = Child(parent, name, Vector2.zero);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null) text.font = font;
            text.fontSize = size; text.alignment = align; text.raycastTarget = false; text.enableWordWrapping = true;
            text.color = new Color(.85f, .83f, .78f);
            return text;
        }

        /// <summary>The build panel: one, moved to whichever icon was clicked.</summary>
        private sealed class Panel
        {
            private readonly RectTransform rect;
            private readonly TextMeshProUGUI body, buildLabel;
            private readonly Button build;
            private Site site;

            public Panel()
            {
                rect = Child(canvas.transform, "Extension build panel", new Vector2(380, 300));
                rect.pivot = new Vector2(0, .5f);
                var back = rect.gameObject.AddComponent<Image>();
                back.color = new Color(.04f, .04f, .04f, .94f);
                body = Text(rect, "Text", 16, TextAlignmentOptions.TopLeft);
                var bodyRect = (RectTransform)body.transform;
                bodyRect.anchorMin = Vector2.zero; bodyRect.anchorMax = Vector2.one;
                bodyRect.offsetMin = new Vector2(16, 58); bodyRect.offsetMax = new Vector2(-16, -14);
                body.richText = true;

                build = Button("Build", new Vector2(16, 12), new Vector2(200, 36), () => site?.Build(), out buildLabel);
                Button("Close", new Vector2(232, 12), new Vector2(132, 36), Close, out _);
                rect.gameObject.SetActive(false);
            }

            private Button Button(string name, Vector2 at, Vector2 size, Action click, out TextMeshProUGUI label)
            {
                var r = Child(rect, name, size);
                r.anchorMin = r.anchorMax = Vector2.zero; r.pivot = Vector2.zero; r.anchoredPosition = at;
                var image = r.gameObject.AddComponent<Image>();
                image.color = new Color(.22f, .21f, .18f, 1);
                var button = r.gameObject.AddComponent<Button>();
                button.targetGraphic = image;
                button.onClick.AddListener(() => click());
                label = Text(r, "Label", 17, TextAlignmentOptions.Center);
                var lr = (RectTransform)label.transform; lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one; lr.offsetMin = lr.offsetMax = Vector2.zero;
                label.text = name.ToUpperInvariant();
                return button;
            }

            public void Show(Site shown, RectTransform icon)
            {
                site = shown;
                rect.gameObject.SetActive(true);
                rect.position = icon.position + new Vector3(icon.sizeDelta.x * .6f, 0, 0);
                rect.SetAsLastSibling();
                Refresh();
            }

            public void Close() { site = null; open = null; if (rect != null) rect.gameObject.SetActive(false); }

            public void Refresh()
            {
                if (site == null) return;
                if (!site.Offered) { Close(); return; }
                var lines = new List<string> { $"<b><size=120%>{site.Spot.Name.ToUpperInvariant()}</size></b>" };
                if (!string.IsNullOrEmpty(site.Spot.Description)) lines.Add($"<color=#a8a69a>{site.Spot.Description}</color>");
                lines.Add("");
                bool building = site.Current(out long at) == Site.State.Building;
                if (building)
                {
                    lines.Add(at > 0 ? $"Being built: {Site.Duration(at - DateTimeOffset.UtcNow.ToUnixTimeSeconds())} left" : "Being built");
                    buildLabel.text = "BUILDING"; build.interactable = false;
                }
                else
                {
                    ItemRequirement[] cost = site.Requirements();
                    foreach (ItemRequirement r in cost)
                        lines.Add($"<color={(r.Fulfilled ? "#9fc27a" : "#c9705f")}>{r.ItemName}: {Count(r.UserItemsCount)} / {Count(r.IntCount)}</color>");
                    lines.Add($"Build time: {Site.Duration(site.Spot.BuildSeconds)}");
                    string locked = site.Locked();
                    if (locked != null) lines.Add($"<color=#c9705f>Locked: {locked}</color>");
                    build.interactable = locked == null && cost.All(r => r.Fulfilled);
                    buildLabel.text = "BUILD";
                }
                body.text = string.Join("\n", lines);
            }

            private static string Count(int n) => n >= 10000 ? n.ToString("N0") : n.ToString();
        }
    }
}
