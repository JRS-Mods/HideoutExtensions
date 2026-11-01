using System;
using System.Linq;
using EFT;
using EFT.Hideout;
using EFT.Interactive;
using EFT.UI;
using HarmonyLib;

namespace HideoutExtensions
{
    internal enum Role { Build, Door }

    /// <summary>What the game's interaction check finds when you look at a spot, its door or the room's way out.</summary>
    internal sealed class ExtensionInteractive : InteractableObject
    {
        internal Site Site;
        internal Role Role;
        internal SwingDoor Door;
    }

    /// <summary>
    /// The spots in the game's own interaction prompt (your interact key, the same list doors and containers use):
    ///  - at a spot that isn't built: "Build ..." with its cost (what you have of each, from your stash), the build time
    ///    and why it's locked, if it is; then how long is left while it's being built;
    ///  - on a built doorway's door: "Open" and "Breach", or "Close".
    /// The hideout's first-person player asks the same question as a raid for objects it doesn't know.
    /// </summary>
    internal static class Interaction
    {
        internal static void Patch(Harmony harmony)
        {
            var actions = AccessTools.Method(typeof(InteractionContextHelper), nameof(InteractionContextHelper.GetAvailableActions),
                new[] { typeof(GamePlayerOwner), typeof(IInteractive) }) ?? throw new MissingMethodException(nameof(InteractionContextHelper), "GetAvailableActions(owner, interactive)");
            harmony.Patch(actions, prefix: new HarmonyMethod(typeof(Interaction), nameof(Actions)));
        }

        // The game has no actions for an object type it doesn't know (it throws), so these are answered first.
        private static bool Actions(GamePlayerOwner owner, IInteractive interactive, ref AvailableInteractionState __result)
        {
            if (!(interactive is ExtensionInteractive target) || target.Site == null) return true;
            try { __result = For(target, owner?.Player); }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Interaction for '{target.Site.Spot.Name}' failed: {e}");
                __result = new AvailableInteractionState();
            }
            return false;
        }

        private static AvailableInteractionState For(ExtensionInteractive target, Player player)
        {
            Site site = target.Site;
            var state = new AvailableInteractionState();
            switch (target.Role)
            {
                case Role.Door when target.Door != null:
                    SwingDoor door = target.Door;
                    if (door.IsOpen) state.Actions.Add(new InteractionAction { Name = "Close", Action = door.Close });
                    else
                    {
                        state.Actions.Add(new InteractionAction { Name = "Open", Action = door.Open });
                        state.Actions.Add(new InteractionAction { Name = "Breach", Action = door.Breach });
                    }
                    break;
                default:
                    if (site.Current(out long completeAt) == Site.State.Building)
                    {
                        long left = completeAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        Info(state, completeAt > 0 ? $"{site.Spot.Name}: being built, {Site.Duration(left)} left" : $"{site.Spot.Name}: being built");
                        break;
                    }
                    ItemRequirement[] cost = site.Requirements();
                    string locked = site.Locked();
                    bool can = locked == null && cost.All(r => r.Fulfilled);
                    state.Actions.Add(new InteractionAction { Name = "Build " + site.Spot.Name, Disabled = !can, Action = site.Build });
                    if (locked != null) Info(state, "Locked: " + locked);
                    foreach (ItemRequirement r in cost) Info(state, $"{r.ItemName}: {Count(r.UserItemsCount)} / {Count(r.IntCount)}");
                    Info(state, "Build time: " + Site.Duration(site.Spot.BuildSeconds));
                    break;
            }
            state.InitSelected();
            return state;
        }

        private static void Info(AvailableInteractionState state, string text)
            => state.Actions.Add(new InteractionAction { Name = text, Disabled = true, Action = () => { } });

        private static string Count(int n) => n >= 10000 ? n.ToString("N0") : n.ToString();
    }
}
