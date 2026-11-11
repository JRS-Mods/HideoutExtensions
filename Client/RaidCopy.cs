using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT.Hideout;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;

namespace HideoutExtensions
{
    /// <summary>
    /// The hideout as another mod's raid map (Hideout Siege loads the hideout's scene as a raid): what a profile has done
    /// to its hideout, as text (so the host of a co-op raid can send it to the others), and that built into the raid's
    /// scene. Nothing here touches the hideout itself: its rooms, its layout and its visit stay as they are.
    /// </summary>
    internal static class RaidCopy
    {
        private const string Route = "/hideoutextensions/spots";

        internal sealed class Snapshot
        {
            [JsonProperty("built")] public List<string> Built = new List<string>();
            [JsonProperty("areas")] public Dictionary<string, AreaMover.Placement> Areas = new Dictionary<string, AreaMover.Placement>();
        }

        private sealed class Reply { [JsonProperty("spots")] public List<SpotInfo> Spots { get; set; } }

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>The playing profile's built rooms and moved areas.</summary>
        internal static string Take()
        {
            var snapshot = new Snapshot();
            try
            {
                if (Plugin.Enabled.Value)
                {
                    string json = RequestHandler.PostJson(Route, "{}");
                    var spots = string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<Reply>(json)?.Spots;
                    // The hideout's own records first (a build that finished since the server last said), then the server.
                    var records = Singleton<HideoutRepresentation>.Instantiated ? Singleton<HideoutRepresentation>.Instance.ImprovementController?._improvementDataBy : null;
                    foreach (SpotInfo spot in spots ?? new List<SpotInfo>())
                    {
                        bool built = records != null && records.TryGetValue(spot.Id, out ImprovementData data)
                            ? data.Completed || (data.CompleteTimestamp > 0 && data.CompleteTimestamp <= Now)
                            : spot.State == "built" || (spot.State == "building" && spot.CompleteAt > 0 && spot.CompleteAt <= Now);
                        if (built) snapshot.Built.Add(spot.Id);
                    }
                }
                snapshot.Areas = AreaMover.SavedLayout();
            }
            catch (Exception e) { Plugin.Log.LogError("Couldn't take the hideout's rooms and layout for a raid: " + e); }
            Plugin.Log.LogInfo($"Hideout for a raid: {snapshot.Built.Count} room(s) built, {snapshot.Areas.Count} area(s) moved.");
            return JsonConvert.SerializeObject(snapshot);
        }

        internal static string AddRoom(string json)
        {
            try
            {
                SpotInfo spot = JsonConvert.DeserializeObject<SpotInfo>(json);
                return Extensions.AddRaidRoom(spot);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Couldn't put a raid's room in: " + e);
                return e.Message;
            }
        }

        /// <summary>A <see cref="Take"/> built into the hideout scene that's loaded as the raid's map.</summary>
        internal static void Build(string json)
        {
            try
            {
                Snapshot snapshot = string.IsNullOrEmpty(json) ? new Snapshot() : JsonConvert.DeserializeObject<Snapshot>(json) ?? new Snapshot();
                GameObject style = GameObject.Find("STYLE");
                if (style == null || style.transform.Find("Wall") == null)
                {
                    Plugin.Log.LogWarning("No hideout in this raid's scene to build the rooms into.");
                    return;
                }
                bool rooms = !Plugin.Enabled.Value || Extensions.SetUpRaidCopy(style, snapshot.Built);
                int moved = AreaMover.PlaceRaidCopy(style, snapshot.Areas);
                Physics.SyncTransforms();
                Plugin.Log.LogInfo($"The raid's copy of the hideout: {(rooms ? Extensions.RoomList.Count + " room(s) put in" : "no rooms (see above)")}, {moved} area(s) moved.");
            }
            catch (Exception e) { Plugin.Log.LogError("Couldn't build the hideout's rooms and layout into the raid: " + e); }
        }
    }
}
