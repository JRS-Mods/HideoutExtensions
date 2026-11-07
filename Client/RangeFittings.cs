using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.Hideout;
using EFT.Interactive;
using EFT.UI;
using EFT.Hideout.ShootingRange;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace HideoutExtensions
{
    /// <summary>What a longer shooting range adds to the game's own: painted distance marks.</summary>
    internal static class RangeFittings
    {
        private static readonly Color Colour = new Color(.95f, .95f, .92f);
        private const float RowLightHeight = 2.5f, RowLightAhead = 1.8f, RowLightRange = 6.5f, RowLightStrength = 1.4f;
        private static Material paint;

        /// <summary>A line across the floor at each distance, and the distance on the floor and on both walls.</summary>
        public static void Marks(SpotInfo spot, Transform shell)
        {
            RangeMarksInfo marks = spot.Room.RangeMarks;
            if (marks == null || marks.Count <= 0) return;
            if (paint == null) paint = OverviewCap.Paint(Colour);
            TMP_FontAsset font = Font();
            for (int i = 0; i < marks.Count; i++)
            {
                float distance = marks.First + marks.Step * i, z = marks.Zero + distance, middle = (marks.West + marks.East) / 2;
                if (paint != null)
                {
                    const float y = .008f, half = .03f;
                    Room.Surface(shell, "Range line", paint, new[]
                    {
                        shell.InverseTransformPoint(new Vector3(marks.West, y, z - half)), shell.InverseTransformPoint(new Vector3(marks.West, y, z + half)),
                        shell.InverseTransformPoint(new Vector3(marks.East, y, z + half)), shell.InverseTransformPoint(new Vector3(marks.East, y, z - half)),
                    }, Vector3.up, p => Vector2.zero, Color.white, ShadowCastingMode.Off);
                }
                // The game hangs a lamp just in front of each of its own target rows, and the targets here would face away from
                // the nearest ones and stand black. Two lamps' worth of light a little in front of the line.
                foreach (float x in new[] { Mathf.Lerp(marks.West, marks.East, .28f), Mathf.Lerp(marks.West, marks.East, .72f) })
                    Lighting.Add(Lighting.Soft(shell, new Vector3(x, RowLightHeight, z - RowLightAhead), "Extension row light", RowLightRange, RowLightStrength));
                if (font == null) continue;
                string text = distance.ToString("0") + " M";
                // On the floor just short of the line, reading from the firing line.
                Label(shell, font, text, new Vector3(middle, .012f, z - .55f), Quaternion.Euler(90, 0, 0), 1.6f, .7f);
                // On the walls, to the far side of the line, clear of the side wall's pillars. A text's face looks along its -z.
                Label(shell, font, text, new Vector3(marks.West + .02f, 1.05f, z + .7f), Quaternion.Euler(0, 270, 0), 1.2f, .5f);
                Label(shell, font, text, new Vector3(marks.East - .02f, 1.05f, z + .7f), Quaternion.Euler(0, 90, 0), 1.2f, .5f);
            }
        }

        internal static TextMeshPro Label(Transform shell, TMP_FontAsset font, string words, Vector3 at, Quaternion turn, float width, float height)
        {
            var go = new GameObject("Extension range mark " + words, typeof(RectTransform));
            go.transform.SetParent(shell, false);
            go.transform.SetPositionAndRotation(at, turn);
            var text = go.AddComponent<TextMeshPro>();
            text.font = font;
            text.rectTransform.sizeDelta = new Vector2(width, height);
            text.enableWordWrapping = false;
            text.alignment = TextAlignmentOptions.Center;
            // Sized to fit the box, whatever units the font was made in.
            text.enableAutoSizing = true;
            text.fontSizeMin = .05f;
            text.fontSizeMax = 100;
            text.color = Colour;
            text.text = words;
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.shadowCastingMode = ShadowCastingMode.Off;
            return text;
        }

        // The font of the hideout's own icons, or whichever the game has.
        internal static TMP_FontAsset Font()
        {
            AreaIcon icon = UnityEngine.Object.FindObjectOfType<AreaIcon>(true);
            if (icon != null && icon._currentAreaLevel != null && icon._currentAreaLevel.font != null) return icon._currentAreaLevel.font;
            TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            return fonts.Length > 0 ? fonts[0] : TMP_Settings.defaultFontAsset;
        }
    }

    /// <summary>
    /// The sign on the firing line that picks what stands on the range: one distance (those targets come up, every other target on
    /// the range goes down, the game's own too), all of them, or just the game's own targets.
    /// </summary>
    internal static class RangeControl
    {
        internal const int OwnTargets = 0, AllTargets = -1;
        private const float SignWidth = .62f, SignHeight = .3f;
        private static readonly List<LiveTargets> rows = new List<LiveTargets>();
        private static TextMeshPro chosen;
        private static Material sign;

        /// <summary>A distance in metres, or <see cref="AllTargets"/> or <see cref="OwnTargets"/>.</summary>
        internal static int Selected { get; private set; } = OwnTargets;

        internal static void Register(LiveTargets row) { if (!rows.Contains(row)) rows.Add(row); }

        internal static void Unregister(LiveTargets row) => rows.Remove(row);

        internal static void Clear() { rows.Clear(); chosen = null; Selected = OwnTargets; }

        private static string Name(int choice) => choice == AllTargets ? "All targets" : choice == OwnTargets ? "Range's own targets" : choice + " M";

        internal static void Select(int choice)
        {
            Selected = choice;
            rows.RemoveAll(r => r == null);
            foreach (LiveTargets row in rows) row.Apply();
            // The range's own targets, out of the way unless they're wanted.
            bool up = choice == AllTargets || choice == OwnTargets;
            foreach (PopperTarget popper in UnityEngine.Object.FindObjectsOfType<PopperTarget>())
                if (popper.GetComponentInParent<LiveTargets>() == null) LiveTargets.Set(popper, up, true, default);
            foreach (RailTarget rail in UnityEngine.Object.FindObjectsOfType<RailTarget>())
                if (rail.GetComponentInParent<LiveTargets>() == null) LiveTargets.Set(rail, up, true, default);
            if (chosen != null) chosen.text = Name(choice);
            Plugin.Log.LogInfo("Shooting range: " + Name(choice) + ".");
        }

        internal static void Actions(AvailableInteractionState state)
        {
            rows.RemoveAll(r => r == null);
            foreach (int distance in rows.Select(r => r.Distance).Where(d => d > 0).Distinct().OrderBy(d => d)) Add(state, distance);
            Add(state, AllTargets);
            Add(state, OwnTargets);
        }

        private static void Add(AvailableInteractionState state, int choice)
        {
            int picked = choice;
            state.Actions.Add(new InteractionAction { Name = (Selected == choice ? "> " : "") + Name(choice), Action = () => Select(picked) });
        }

        /// <summary>The sign on the firing line, and what you look at to get its prompt.</summary>
        internal static void Panel(Transform parent, Site site, RangeMarksInfo marks)
        {
            Vector3 at = new Vector3(marks.Panel[0], marks.Panel[1], marks.Panel[2]);
            if (sign == null) sign = OverviewCap.Paint(new Color(.06f, .06f, .05f));
            var holder = new GameObject("Extension range control").transform;
            holder.SetParent(parent, false);
            holder.position = at;
            if (sign != null)
                Room.Surface(holder, "Range sign", sign, new[]
                {
                    new Vector3(-SignWidth / 2, 0, 0), new Vector3(SignWidth / 2, 0, 0), new Vector3(SignWidth / 2, SignHeight, 0), new Vector3(-SignWidth / 2, SignHeight, 0),
                }, Vector3.back, p => Vector2.zero, Color.white, ShadowCastingMode.Off);
            TMP_FontAsset font = RangeFittings.Font();
            if (font != null)
            {
                RangeFittings.Label(holder, font, "TARGET DISTANCE", at + new Vector3(0, SignHeight * .72f, -.005f), Quaternion.identity, SignWidth * .9f, SignHeight * .34f);
                chosen = RangeFittings.Label(holder, font, Name(Selected), at + new Vector3(0, SignHeight * .3f, -.005f), Quaternion.identity, SignWidth * .9f, SignHeight * .42f);
            }
            var zone = new GameObject("Extension range control prompt") { layer = LayerMask.NameToLayer("Interactive") };
            zone.transform.SetParent(holder, false);
            zone.transform.position = at + new Vector3(0, SignHeight / 2, -.12f);
            var box = zone.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(SignWidth + .1f, SignHeight + .15f, .3f);
            // A look that starts inside a collider doesn't see it, and the range's own prompt starts just short of the bench: thin
            // plates out in front, so from anywhere near, one of them is met first.
            foreach (float ahead in new[] { -.72f, -1f, -1.3f })
            {
                var plate = zone.AddComponent<BoxCollider>();
                plate.isTrigger = true;
                plate.center = new Vector3(0, .3f, ahead);
                plate.size = new Vector3(SignWidth + .1f, 1.2f, .04f);
            }
            var interactive = zone.AddComponent<ExtensionInteractive>();
            interactive.Site = site;
            interactive.Role = Role.RangeControl;
        }
    }

    /// <summary>
    /// Copies of the range's own targets that work on their own, with the game's animations, sounds and bullet holes. They stand when
    /// the range's control asks for their distance. A hit drops one and it comes back up a few seconds later, as the game's popper
    /// stand does; a hit on its frame only rocks it. They aren't part of the game's training or its scores.
    /// </summary>
    internal sealed class LiveTargets : MonoBehaviour
    {
        private const int BackAfterMs = 4000;
        internal int Distance;
        private readonly CancellationTokenSource cancel = new CancellationTokenSource();
        private readonly HashSet<IFoldingTarget> down = new HashSet<IFoldingTarget>();
        private PopperTarget[] poppers;
        private RailTarget[] rails;

        private bool Wanted => RangeControl.Selected == RangeControl.AllTargets || RangeControl.Selected == Distance;

        private void Start()
        {
            poppers = GetComponentsInChildren<PopperTarget>(true);
            rails = GetComponentsInChildren<RailTarget>(true);
            foreach (PopperTarget popper in poppers) popper.OnHitTarget += Popper;
            foreach (RailTarget rail in rails) rail.OnHitTarget += Rail;
            RangeControl.Register(this);
            Apply();
        }

        private void OnDestroy()
        {
            cancel.Cancel();
            RangeControl.Unregister(this);
            if (poppers != null) foreach (PopperTarget popper in poppers) if (popper != null) popper.OnHitTarget -= Popper;
            if (rails != null) foreach (RailTarget rail in rails) if (rail != null) rail.OnHitTarget -= Rail;
        }

        /// <summary>Up or down, as the range's control has it. Only one target in a group sounds.</summary>
        internal void Apply()
        {
            if (poppers == null) return;
            bool wanted = Wanted, quiet = !wanted;
            foreach (IFoldingTarget target in poppers.Cast<IFoldingTarget>().Concat(rails))
            {
                if (down.Contains(target)) continue;
                Set(target, wanted, quiet, cancel.Token);
                quiet = true;
            }
        }

        internal static async void Set(IFoldingTarget target, bool up, bool mute, CancellationToken token)
        {
            try
            {
                if (up) await target.Unfold(mute, token);
                else await target.Fold(mute, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Plugin.Log.LogWarning("A range target didn't move: " + e.Message); }
        }

        private void Popper(PopperTarget target, int segment) => Hit(target, segment);

        private void Rail(RailTarget target, TargetColliderType type, int segment) => Hit(target, segment);

        private async void Hit(IFoldingTarget target, int segment)
        {
            if (!Wanted || !down.Add(target)) return;
            try
            {
                if (segment == HideoutTargetBallisticCollider.MISS_SHOT_TARGET) { await target.Rocking(cancel.Token); return; }
                await target.Fold(false, cancel.Token);
                await Task.Delay(BackAfterMs, cancel.Token);
                if (Wanted) await target.Unfold(false, cancel.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Plugin.Log.LogWarning("A range target failed: " + e.Message); }
            finally { down.Remove(target); }
        }
    }
}
