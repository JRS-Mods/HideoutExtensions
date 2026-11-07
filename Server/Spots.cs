using System.Reflection;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using Path = System.IO.Path;

namespace HideoutExtensions.Server;

/// <summary>
/// One room extension: a room at a fixed place in the hideout, and how you get into it. Read from a JSON file (see the
/// spots folder). It's entered either through a door in one of the hideout's own walls (<see cref="Door"/>, with the
/// door back on the room's own wall at <see cref="Inside"/>), or through a doorway from another extension's room
/// (<see cref="Opening"/>), which then has to be built first. The client builds the room and doors from these; the
/// server cares about the id, the cost, the build time and what has to be built first.
/// </summary>
public sealed class Spot
{
    /// <summary>24 hex characters. Also the id of the hideout improvement that records the build in the profile.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("room")] public RoomRect Room { get; set; } = new();
    [JsonPropertyName("door")] public DoorPlacement? Door { get; set; }
    [JsonPropertyName("inside")] public DoorPlacement? Inside { get; set; }
    [JsonPropertyName("opening")] public Opening? Opening { get; set; }
    /// <summary>Changes to the hideout's own things once this spot is built (moved out of the way, or hidden).</summary>
    [JsonPropertyName("hideoutEdits")] public List<HideoutEdit> HideoutEdits { get; set; } = new();
    /// <summary>For a room with a door: another spot that has to be built first (the room the door is in).</summary>
    [JsonPropertyName("after")] public string? After { get; set; }
    [JsonPropertyName("requirements")] public List<Cost> Requirements { get; set; } = new();
    [JsonPropertyName("buildSeconds")] public int BuildSeconds { get; set; }
    // There from the start: never built, never locked.
    [JsonPropertyName("built")] public bool BuiltFromStart { get; set; }
    [JsonPropertyName("unlock")] public UnlockRule? Unlock { get; set; }
    /// <summary>More ways through the hideout's walls into the same room.</summary>
    [JsonPropertyName("moreDoors")] public List<DoorPair> MoreDoors { get; set; } = new();
    /// <summary>Doorways to other spots' rooms that appear once both rooms are built.</summary>
    [JsonPropertyName("links")] public List<Opening> Links { get; set; } = new();

    [JsonIgnore] public MongoId Key { get; set; }
    [JsonIgnore] public string Source { get; set; } = "";
}

/// <summary>The room's inside, in hideout world coordinates: floor from min to max (x, z), walls standing on its edges.</summary>
public sealed class RoomRect
{
    [JsonPropertyName("min")] public double[] Min { get; set; } = new double[2];
    [JsonPropertyName("max")] public double[] Max { get; set; } = new double[2];
    [JsonPropertyName("floor")] public double Floor { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; } = 3.1;
    [JsonPropertyName("lamps")] public int Lamps { get; set; } = 2;
    /// <summary>"hideout" (the main hall's paint), "basement" (the raw concrete of the room behind the emergency wall), or
    /// "existing": a space the hideout's own model already has (walls, floor, ceiling), which only gets lamps.</summary>
    [JsonPropertyName("style")] public string Style { get; set; } = "hideout";
    /// <summary>Pillar walls inside the room (a beam on pillars, like the room behind the emergency wall's).</summary>
    [JsonPropertyName("pillarWalls")] public List<PillarWall> PillarWalls { get; set; } = new();
    /// <summary>Plain blocks in the room's walls' material, to cover things that poke into it.</summary>
    [JsonPropertyName("blocks")] public List<Block> Blocks { get; set; } = new();
    /// <summary>Copies of the hideout's own objects standing in the room, as scenery.</summary>
    [JsonPropertyName("props")] public List<Prop> Props { get; set; } = new();
    /// <summary>Distance marks down a shooting range.</summary>
    [JsonPropertyName("rangeMarks")] public RangeMarks? RangeMarks { get; set; }
}

/// <summary>One of the hideout's own objects, by its path in the scene: moved by Move (metres, x y z) or hidden.</summary>
public sealed class HideoutEdit
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("move")] public double[]? Move { get; set; }
    [JsonPropertyName("hide")] public bool Hide { get; set; }
}

/// <summary>A box in hideout world coordinates, from min to max (x, y, z).</summary>
public sealed class Block
{
    [JsonPropertyName("min")] public double[] Min { get; set; } = new double[3];
    [JsonPropertyName("max")] public double[] Max { get; set; } = new double[3];
}

/// <summary>A pillar wall across a room: from one end to the other on the floor (x, z), along x or z.</summary>
public sealed class PillarWall
{
    [JsonPropertyName("from")] public double[] From { get; set; } = new double[2];
    [JsonPropertyName("to")] public double[] To { get; set; } = new double[2];
}

public sealed class DoorPlacement
{
    /// <summary>On the floor at the middle of the doorway, in the wall's plane (hideout world coordinates).</summary>
    [JsonPropertyName("position")] public double[] Position { get; set; } = new double[3];
    /// <summary>Which way the door's face looks, in degrees about vertical (0 = +z, 90 = +x): towards where you stand.</summary>
    [JsonPropertyName("facing")] public double Facing { get; set; }
    /// <summary>The doorway's size in metres (0 = an ordinary door's).</summary>
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; }
    /// <summary>Once built it's a bare opening, with no door or frame.</summary>
    [JsonPropertyName("open")] public bool Open { get; set; }
}

/// <summary>A copy of one of the hideout's own objects, by its path in the scene: at Position, or Move away from the original.</summary>
public sealed class Prop
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("position")] public double[]? Position { get; set; }
    [JsonPropertyName("facing")] public double Facing { get; set; }
    [JsonPropertyName("move")] public double[]? Move { get; set; }
    /// <summary>A shooting range target that keeps working. Otherwise its scripts are left off.</summary>
    [JsonPropertyName("live")] public bool Live { get; set; }
    [JsonPropertyName("distance")] public int Distance { get; set; }
    [JsonPropertyName("targetOnly")] public bool TargetOnly { get; set; }
}

/// <summary>Distance marks across a shooting range: measured from Zero along z, First, then every Step, Count of them, between its walls.</summary>
public sealed class RangeMarks
{
    [JsonPropertyName("zero")] public double Zero { get; set; }
    [JsonPropertyName("first")] public double First { get; set; }
    [JsonPropertyName("step")] public double Step { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("west")] public double West { get; set; }
    [JsonPropertyName("east")] public double East { get; set; }
    /// <summary>Where the control that picks a distance stands.</summary>
    [JsonPropertyName("panel")] public double[]? Panel { get; set; }
}

/// <summary>One more way through a wall into the same room.</summary>
public sealed class DoorPair
{
    [JsonPropertyName("door")] public DoorPlacement? Door { get; set; }
    [JsonPropertyName("inside")] public DoorPlacement? Inside { get; set; }
}

/// <summary>A doorway into this room from <see cref="From"/>'s room, in the wall between them.</summary>
public sealed class Opening
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    /// <summary>On the floor at the middle of the doorway, halfway through the wall between the two rooms.</summary>
    [JsonPropertyName("position")] public double[] Position { get; set; } = new double[3];
    /// <summary>Degrees about vertical, pointing into the room it's reached from.</summary>
    [JsonPropertyName("facing")] public double Facing { get; set; }
}

public sealed class Cost
{
    [JsonPropertyName("template")] public string Template { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; } = 1;
}

/// <summary>A hideout area the player has to have built to at least <see cref="Level"/>.</summary>
public sealed class UnlockRule
{
    [JsonPropertyName("area")] public string Area { get; set; } = "";
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("why")] public string? Why { get; set; }
}

/// <summary>
/// Every spot: this mod's spots folder, plus a hideoutextensions folder in any other server mod (so another mod can add
/// its own rooms without touching this one). A spot whose file is broken, whose id is taken or whose cost names an item
/// the game doesn't have is left out and logged. Other server mods can also call <see cref="Add"/> while loading.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 10)]
public sealed class SpotCatalog(ModHelper mods, JsonUtil json, TemplateTable templates, HideoutConfig hideoutConfig,
    ISptLogger<SpotCatalog> logger) : IOnLoad
{
    // Shared: the loader, the router and the build patch each get their own instance of this class.
    private static readonly List<Spot> spots = new();
    private static bool loaded;
    public IReadOnlyList<Spot> Spots => spots;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (loaded) return Task.CompletedTask;
        loaded = true;
        string own = mods.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var folders = new List<string> { Path.Combine(own, "spots") };
        string? modsFolder = Path.GetDirectoryName(own.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (modsFolder != null)
            foreach (string mod in Directory.GetDirectories(modsFolder))
                if (!string.Equals(Path.GetFullPath(mod), Path.GetFullPath(own).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    folders.Add(Path.Combine(mod, "hideoutextensions"));
        foreach (string folder in folders.Where(Directory.Exists))
            foreach (string file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    Spot? spot = json.Deserialize<Spot>(File.ReadAllText(file));
                    if (spot == null) { logger.Error($"[HideoutExtensions] {file}: not a spot."); continue; }
                    spot.Source = file;
                    Add(spot);
                }
                catch (Exception e) { logger.Error($"[HideoutExtensions] {file} couldn't be read: {e.Message}"); }
            }
        // A doorway from a room that doesn't exist can never be reached.
        foreach (Spot orphan in spots.Where(s => Prerequisite(s) is string id && Find(new MongoId(id)) == null).ToList())
        {
            logger.Error($"[HideoutExtensions] Spot '{orphan.Name}' ({orphan.Source}) left out: it needs {Prerequisite(orphan)} built first, which isn't a spot.");
            spots.Remove(orphan);
        }
        int seconds = hideoutConfig.OverrideBuildTimeSeconds;
        logger.Info($"[HideoutExtensions] {spots.Count} room extension spot(s): {string.Join(", ", spots.Select(s => s.Name))}" +
                    (seconds >= 0 ? $" (each builds in {seconds} s: hideout.json overrideBuildTimeSeconds)." : "."));
        return Task.CompletedTask;
    }

    /// <summary>Adds a spot, unless it's invalid; returns why it was refused, or null.</summary>
    public string? Add(Spot spot)
    {
        string? problem = Check(spot);
        if (problem != null)
        {
            logger.Error($"[HideoutExtensions] Spot '{spot.Name}' ({spot.Source}) left out: {problem}.");
            return problem;
        }
        spot.Key = new MongoId(spot.Id);
        // SPT's hideout.json overrideBuildTimeSeconds (-1 = off) speeds these up like the game's own areas.
        if (hideoutConfig.OverrideBuildTimeSeconds >= 0) spot.BuildSeconds = hideoutConfig.OverrideBuildTimeSeconds;
        spots.Add(spot);
        return null;
    }

    public Spot? Find(MongoId id) => spots.FirstOrDefault(s => s.Key == id);

    /// <summary>The spot that has to be built first: the room a doorway comes from, or a door's "after".</summary>
    public static string? Prerequisite(Spot spot) => spot.Opening?.From ?? spot.After;

    private string? Check(Spot spot)
    {
        if (!MongoId.IsValidMongoId(spot.Id)) return $"id '{spot.Id}' isn't 24 hex characters";
        if (spots.Any(s => string.Equals(s.Id, spot.Id, StringComparison.OrdinalIgnoreCase))) return $"id {spot.Id} is already used";
        if (string.IsNullOrWhiteSpace(spot.Name)) return "it has no name";
        if (spot.Room?.Min == null || spot.Room.Max == null || spot.Room.Min.Length != 2 || spot.Room.Max.Length != 2) return "room.min and room.max need x and z";
        if (spot.Room.Max[0] - spot.Room.Min[0] < 1.5 || spot.Room.Max[1] - spot.Room.Min[1] < 1.5 || spot.Room.Height < 2.4) return "the room is smaller than 1.5 x 1.5 x 2.4 m";
        if ((spot.Door == null) == (spot.Opening == null)) return "it needs either a door (into the hideout's wall) or an opening (from another room), not both";
        if (spot.Door != null && (spot.Door.Position?.Length != 3 || spot.Inside?.Position?.Length != 3)) return "door and inside each need a position (x, y, z)";
        if (spot.Opening != null && (spot.Opening.Position?.Length != 3 || !MongoId.IsValidMongoId(spot.Opening.From))) return "opening needs a position (x, y, z) and the id of the room it's reached from";
        if (spot.After != null && !MongoId.IsValidMongoId(spot.After)) return $"after '{spot.After}' isn't a spot id";
        if (spot.BuildSeconds < 0) return "buildSeconds is negative";
        if (spot.Door != null && (spot.Door.Width < 0 || spot.Door.Height < 0)) return "door width and height can't be negative";
        if (spot.Room.Props?.Any(p => string.IsNullOrWhiteSpace(p.Path) || (p.Position?.Length != 3 && p.Move?.Length != 3)) == true) return "every prop needs a path, and a position or a move (x, y, z)";
        if (spot.MoreDoors.Any(d => d.Door?.Position?.Length != 3 || d.Inside?.Position?.Length != 3)) return "every one of moreDoors needs a door and an inside, each with a position (x, y, z)";
        if (spot.Links.Any(l => l.Position?.Length != 3 || !MongoId.IsValidMongoId(l.From))) return "every link needs a position (x, y, z) and the id of the other room";
        foreach (Cost cost in spot.Requirements)
        {
            if (!MongoId.IsValidMongoId(cost.Template) || !templates.Items.ContainsKey(new MongoId(cost.Template))) return $"requirement {cost.Template} isn't an item in this game";
            if (cost.Count < 1) return $"requirement {cost.Template} has a count below 1";
        }
        if (spot.Unlock != null && !Enum.TryParse(spot.Unlock.Area, true, out HideoutAreas _)) return $"unlock area '{spot.Unlock.Area}' isn't a hideout area";
        return null;
    }

    /// <summary>The spot's improvement in the profile, if started.</summary>
    public static HideoutImprovement? Progress(PmcData pmc, Spot spot)
        => pmc.Hideout?.Improvements != null && pmc.Hideout.Improvements.TryGetValue(spot.Key, out HideoutImprovement? i) ? i : null;

    public static bool Built(PmcData pmc, Spot spot)
    {
        if (spot.BuiltFromStart) return true;
        HideoutImprovement? i = Progress(pmc, spot);
        return i != null && (i.Completed == true || (i.ImproveCompleteTimestamp is long at && at > 0 && at <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    /// <summary>Why the spot can't be started for this player yet, or null.</summary>
    public static string? Locked(PmcData pmc, Spot spot)
    {
        if (Prerequisite(spot) is string first)
        {
            Spot? from = spots.FirstOrDefault(s => s.Key == new MongoId(first));
            if (from != null && !Built(pmc, from)) return $"Build the {from.Name} first";
        }
        if (spot.Unlock == null) return null;
        HideoutAreas area = Enum.Parse<HideoutAreas>(spot.Unlock.Area, true);
        int level = pmc.Hideout?.Areas?.FirstOrDefault(a => a.Type == area)?.Level ?? 0;
        return level >= spot.Unlock.Level ? null : spot.Unlock.Why ?? $"Requires {area} level {spot.Unlock.Level}";
    }
}
