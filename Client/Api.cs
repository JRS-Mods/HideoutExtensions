using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace HideoutExtensions
{
    /// <summary>
    /// For other client mods: the room extensions in the hideout, and an event when a built room is in the scene, so a
    /// mod can furnish it. Spots themselves (where, how big, what they cost) come from the server mod: drop a JSON file
    /// in a "hideoutextensions" folder of your own server mod. Reference HideoutExtensions.dll and add
    /// [BepInDependency("jrs.hideoutextensions", BepInDependency.DependencyFlags.SoftDependency)].
    /// </summary>
    public static class HideoutExtensionsApi
    {
        /// <summary>
        /// A built room has been put in the hideout scene. Raised once per room on every hideout visit (the rooms are
        /// rebuilt each time the hideout loads), and when a room finishes building while you're in the hideout. Whatever you add under
        /// <see cref="ExtensionRoom.Root"/> goes when the hideout unloads.
        /// </summary>
        public static event Action<ExtensionRoom> RoomReady;

        /// <summary>Every spot the server knows of, with its state, as last fetched (empty outside the hideout).</summary>
        public static IReadOnlyList<SpotInfo> Spots => Extensions.SpotList;

        /// <summary>The rooms in the scene right now.</summary>
        public static IReadOnlyList<ExtensionRoom> Rooms => Extensions.RoomList;

        /// <summary>
        /// Lets players move something of yours from the hideout's overview the way they move its areas: right-click its
        /// icon, and <see cref="MovableThing.Root"/> follows the mouse until it's put down (<see cref="MovableThing.Placed"/>
        /// is called; keep it there, and remember it if it should be there next visit) or the move's cancelled (it goes
        /// back). Adding it again with the same icon or root replaces the old one; things whose icon or root is gone are
        /// dropped.
        /// </summary>
        public static void AddMovable(MovableThing thing) => AreaMover.Add(thing);

        /// <summary>No longer movable from the overview.</summary>
        public static void RemoveMovable(MovableThing thing) => AreaMover.Remove(thing);

        /// <summary>
        /// An icon of yours that must never move anything, though it may be a copy of an area's (which would otherwise move
        /// that area): right-clicking it only shows the reason. Gone once the icon is destroyed.
        /// </summary>
        public static void AddUnmovable(GameObject icon, string reason) => AreaMover.AddUnmovable(icon, reason);

        /// <summary>
        /// For a mod that loads the hideout's scene as a raid map (Hideout Siege): the rooms the playing profile has built
        /// and where it has put its areas, as text to keep or send to the other players in the raid. Call it in the raid,
        /// as the map loads.
        /// </summary>
        public static string RaidCopySnapshot() => RaidCopy.Take();

        /// <summary>
        /// Builds a <see cref="RaidCopySnapshot"/> (this player's, or the host's in co-op) into the hideout scene that's loaded
        /// as the raid's map: the built rooms, their doorways cut and doors in, their lamps lit, <see cref="RoomReady"/> raised
        /// for each, and the areas where they were put. Nothing that's only for the hideout (build prompts, the overview,
        /// the rooms' own navmesh). It all goes with the raid.
        /// </summary>
        public static void BuildRaidCopy(string snapshot) => RaidCopy.Build(snapshot);

        /// <summary>
        /// In a raid copy only (after <see cref="BuildRaidCopy"/>): a room of your own, built for this raid alone, given as a
        /// spot's JSON like the server's files (an <c>opening</c> from a built room joins it on, and <c>"open": true</c> leaves
        /// that doorway without a door). It's laid out with the rest straight away, and goes with the raid. Null if it's in,
        /// otherwise why not.
        /// </summary>
        public static string AddRaidRoom(string spotJson) => RaidCopy.AddRoom(spotJson);

        internal static void Raise(ExtensionRoom room)
        {
            if (RoomReady == null) return;
            foreach (Action<ExtensionRoom> handler in RoomReady.GetInvocationList())
            {
                try { handler(room); }
                catch (Exception e) { Plugin.Log.LogError($"A RoomReady handler ({handler.Method.DeclaringType?.FullName}) failed for '{room.Spot.Name}': {e}"); }
            }
        }
    }

    /// <summary>Something another mod lets players move from the overview (see <see cref="HideoutExtensionsApi.AddMovable"/>).</summary>
    public sealed class MovableThing
    {
        /// <summary>What the overview calls it while it's being moved.</summary>
        public string Name { get; set; }
        /// <summary>Its icon on the overview: a right-click on it, or on anything under it, picks it up.</summary>
        public GameObject Icon { get; set; }
        /// <summary>What follows the mouse: its origin goes where the mouse points on the floor, and it turns about it.</summary>
        public Transform Root { get; set; }
        /// <summary>Optional: why it can't go at a position and rotation (it isn't put down there), or null if it can.</summary>
        public Func<Vector3, Quaternion, string> Check { get; set; }
        /// <summary>It's been put down at this position and rotation.</summary>
        public Action<Vector3, Quaternion> Placed { get; set; }
    }

    /// <summary>A built room in the hideout scene.</summary>
    public sealed class ExtensionRoom
    {
        public SpotInfo Spot { get; internal set; }
        /// <summary>
        /// The room's frame: origin on the floor at the room's south-west inside corner (min x, min z), axes as the
        /// hideout's world (+x east, +z north, +y up), no turn. The inside spans 0..Size.x, 0..Size.y, 0..Size.z.
        /// Anything you add under it stays for the visit, even when the room's walls are rebuilt (a neighbour built).
        /// </summary>
        public Transform Root { get; internal set; }
        public Vector3 Size { get; internal set; }
    }

    /// <summary>One spot, as the server mod describes it.</summary>
    public sealed class SpotInfo
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("room")] public RoomInfo Room { get; set; } = new RoomInfo();
        /// <summary>Entered through a door in one of the hideout's own walls (and back through <see cref="Inside"/>), or null.</summary>
        [JsonProperty("door")] public DoorInfo Door { get; set; }
        [JsonProperty("inside")] public DoorInfo Inside { get; set; }
        /// <summary>More ways through the hideout's walls into the same room, all cut when it's built.</summary>
        [JsonProperty("moreDoors")] public List<DoorPairInfo> MoreDoors { get; set; } = new List<DoorPairInfo>();
        /// <summary>Doorways to other spots' rooms that appear once both rooms are built; neither has to wait for the other.</summary>
        [JsonProperty("links")] public List<OpeningInfo> Links { get; set; } = new List<OpeningInfo>();
        /// <summary>Entered through a doorway from another spot's room, or null.</summary>
        [JsonProperty("opening")] public OpeningInfo Opening { get; set; }
        /// <summary>Changes to the hideout's own things once this spot is built (moved out of the way, or hidden).</summary>
        [JsonProperty("hideoutEdits")] public List<HideoutEditInfo> HideoutEdits { get; set; } = new List<HideoutEditInfo>();
        /// <summary>For a room with a door: the spot that has to be built first (the room the door is in), or null.</summary>
        [JsonProperty("after")] public string After { get; set; }
        [JsonProperty("requirements")] public List<CostInfo> Requirements { get; set; } = new List<CostInfo>();
        [JsonProperty("buildSeconds")] public int BuildSeconds { get; set; }
        /// <summary>Why it can't be built yet, or null.</summary>
        [JsonProperty("locked")] public string Locked { get; set; }
        /// <summary>"none", "building" or "built", as the server had it when last asked.</summary>
        [JsonProperty("state")] public string State { get; set; }
        [JsonProperty("completeAt")] public long CompleteAt { get; set; }
    }

    public sealed class DoorInfo
    {
        /// <summary>On the floor, at the middle of the doorway, in the wall's plane (hideout world coordinates).</summary>
        [JsonProperty("position")] public float[] Position { get; set; } = new float[3];
        /// <summary>Degrees about vertical the door's face looks, towards where you stand (0 = +z, 90 = +x).</summary>
        [JsonProperty("facing")] public float Facing { get; set; }
        /// <summary>The doorway's size in metres (0 = an ordinary door's). Set on a spot's door, and it counts for the whole way through.</summary>
        [JsonProperty("width")] public float Width { get; set; }
        [JsonProperty("height")] public float Height { get; set; }
        /// <summary>Once built it's a bare opening, with no door or frame: for joining a room on as part of the room it opens from.</summary>
        [JsonProperty("open")] public bool Open { get; set; }
    }

    /// <summary>One more way through a wall: the same as a spot's door and inside.</summary>
    public sealed class DoorPairInfo
    {
        [JsonProperty("door")] public DoorInfo Door { get; set; }
        [JsonProperty("inside")] public DoorInfo Inside { get; set; }
    }

    /// <summary>A copy of one of the hideout's own objects standing in a room.</summary>
    public sealed class PropInfo
    {
        /// <summary>Its path in the hideout's scene.</summary>
        [JsonProperty("path")] public string Path { get; set; }
        /// <summary>Where it stands (hideout world coordinates), unless <see cref="Move"/> is set.</summary>
        [JsonProperty("position")] public float[] Position { get; set; }
        [JsonProperty("facing")] public float Facing { get; set; }
        /// <summary>Where it stands relative to the original (metres, x y z), turned the same way.</summary>
        [JsonProperty("move")] public float[] Move { get; set; }
        /// <summary>A shooting range target that keeps working: hit, it drops and comes back up. Otherwise its scripts are left off.</summary>
        [JsonProperty("live")] public bool Live { get; set; }
        /// <summary>A live target: the distance on the range's control that brings it up.</summary>
        [JsonProperty("distance")] public int Distance { get; set; }
        /// <summary>A rail target: only its paper target shows, for more than one on a rail.</summary>
        [JsonProperty("targetOnly")] public bool TargetOnly { get; set; }
    }

    /// <summary>Distance marks painted across a shooting range: a line over the floor, and the distance on the floor and both walls.</summary>
    public sealed class RangeMarksInfo
    {
        /// <summary>Where distances are measured from, along z.</summary>
        [JsonProperty("zero")] public float Zero { get; set; }
        /// <summary>The first mark's distance, the distance between marks, and how many.</summary>
        [JsonProperty("first")] public float First { get; set; }
        [JsonProperty("step")] public float Step { get; set; }
        [JsonProperty("count")] public int Count { get; set; }
        /// <summary>The range's walls, in x.</summary>
        [JsonProperty("west")] public float West { get; set; }
        [JsonProperty("east")] public float East { get; set; }
        /// <summary>Where the control that picks a distance stands, or null for none.</summary>
        [JsonProperty("panel")] public float[] Panel { get; set; }
    }

    public sealed class OpeningInfo
    {
        [JsonProperty("from")] public string From { get; set; }
        /// <summary>On the floor at the middle of the doorway, halfway through the wall between the two rooms.</summary>
        [JsonProperty("position")] public float[] Position { get; set; } = new float[3];
        /// <summary>Degrees about vertical, pointing into the room it's reached from.</summary>
        [JsonProperty("facing")] public float Facing { get; set; }
        /// <summary>Optional: the doorway's size in metres (0 = an ordinary door's).</summary>
        [JsonProperty("width")] public float Width { get; set; }
        [JsonProperty("height")] public float Height { get; set; }
        /// <summary>Optional: a bare opening, with no door in it.</summary>
        [JsonProperty("open")] public bool Open { get; set; }
    }

    /// <summary>The room's inside in hideout world coordinates: floor from min to max (x, z).</summary>
    public sealed class RoomInfo
    {
        [JsonProperty("min")] public float[] Min { get; set; } = new float[2];
        [JsonProperty("max")] public float[] Max { get; set; } = new float[2];
        [JsonProperty("floor")] public float Floor { get; set; }
        [JsonProperty("height")] public float Height { get; set; } = 3.1f;
        [JsonProperty("lamps")] public int Lamps { get; set; } = 2;
        /// <summary>"hideout": the main hall's paint (the hideout's current style); "basement": the raw concrete of the room
        /// behind the emergency wall; "existing": a space the hideout's own model already has, which only gets lamps.</summary>
        [JsonProperty("style")] public string Style { get; set; } = "hideout";
        /// <summary>Pillar walls inside the room (a beam on pillars, like the room behind the emergency wall's).</summary>
        [JsonProperty("pillarWalls")] public List<PillarWallInfo> PillarWalls { get; set; } = new List<PillarWallInfo>();
        /// <summary>Plain blocks in the room's walls' material, to cover things that poke into it.</summary>
        [JsonProperty("blocks")] public List<BlockInfo> Blocks { get; set; } = new List<BlockInfo>();
        /// <summary>Copies of the hideout's own objects standing in the room.</summary>
        [JsonProperty("props")] public List<PropInfo> Props { get; set; } = new List<PropInfo>();
        /// <summary>Distance marks down a shooting range, or null.</summary>
        [JsonProperty("rangeMarks")] public RangeMarksInfo RangeMarks { get; set; }
    }

    /// <summary>One of the hideout's own objects, by its path in the scene: moved by <see cref="Move"/> (metres, x y z) or hidden.</summary>
    public sealed class HideoutEditInfo
    {
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("move")] public float[] Move { get; set; }
        [JsonProperty("hide")] public bool Hide { get; set; }
    }

    /// <summary>A box in hideout world coordinates, from min to max (x, y, z).</summary>
    public sealed class BlockInfo
    {
        [JsonProperty("min")] public float[] Min { get; set; } = new float[3];
        [JsonProperty("max")] public float[] Max { get; set; } = new float[3];
    }

    /// <summary>A pillar wall across a room: from one end to the other on the floor (x, z), along x or z.</summary>
    public sealed class PillarWallInfo
    {
        [JsonProperty("from")] public float[] From { get; set; } = new float[2];
        [JsonProperty("to")] public float[] To { get; set; } = new float[2];
    }

    public sealed class CostInfo
    {
        [JsonProperty("template")] public string Template { get; set; }
        [JsonProperty("count")] public int Count { get; set; }
    }
}
