using System.Reflection;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;

namespace HideoutExtensions.Server;

public sealed class EmptyRequest : IRequestData { }

/// <summary>/hideoutextensions/spots: every spot with what the client needs to place it, and where this player is with it.</summary>
[Injectable]
public sealed class SpotRouter(JsonUtil json, SpotCatalog catalog, ProfileHelper profiles) : StaticRouter(json, [
    new RouteAction<EmptyRequest>("/hideoutextensions/spots",
        (url, info, sessionId, output, cancellationToken) => new ValueTask<string>(Describe(json, catalog, profiles, sessionId)))
])
{
    private static string Describe(JsonUtil json, SpotCatalog catalog, ProfileHelper profiles, MongoId sessionId)
    {
        PmcData? pmc = profiles.GetPmcProfile(sessionId);
        var list = catalog.Spots.Select(spot =>
        {
            HideoutImprovement? progress = pmc != null ? SpotCatalog.Progress(pmc, spot) : null;
            string state = pmc != null && SpotCatalog.Built(pmc, spot) ? "built" : progress != null ? "building" : "none";
            return new Dictionary<string, object?>
            {
                ["id"] = spot.Id, ["name"] = spot.Name, ["description"] = spot.Description,
                ["room"] = spot.Room, ["door"] = spot.Door, ["inside"] = spot.Inside, ["opening"] = spot.Opening, ["hideoutEdits"] = spot.HideoutEdits, ["after"] = spot.After, ["requirements"] = spot.Requirements, ["buildSeconds"] = spot.BuildSeconds,
                ["locked"] = pmc != null && state == "none" ? SpotCatalog.Locked(pmc, spot) : null,
                ["state"] = state, ["completeAt"] = progress?.ImproveCompleteTimestamp ?? 0
            };
        }).ToList();
        return json.Serialize(new Dictionary<string, object> { ["ready"] = pmc != null, ["spots"] = list }, false)!;
    }
}

/// <summary>
/// The client starts a build the way it starts any hideout improvement (HideoutImproveArea, with the items you handed
/// over), giving the spot's id. The game's own handler would look for the id in an area's improvements and start all of
/// that area's; for a spot's id this checks the build may start, that the items cover its cost, takes them as the game
/// does (money from a stack, everything else whole) and starts that one build. Any other id goes to the game.
/// </summary>
[Injectable]
public sealed class BuildPatch : AbstractPatch
{
    private static SpotCatalog? catalog;
    private static EventOutputHolder? outputs;
    private static HttpResponseUtil? responses;
    private static InventoryHelper? inventory;
    private static PaymentHelper? payments;
    private static ISptLogger<BuildPatch>? log;

    public BuildPatch(SpotCatalog spots, EventOutputHolder eventOutputs, HttpResponseUtil http, InventoryHelper inventoryHelper,
        PaymentHelper paymentHelper, ISptLogger<BuildPatch> logger)
    { catalog = spots; outputs = eventOutputs; responses = http; inventory = inventoryHelper; payments = paymentHelper; log = logger; }

    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(HideoutController), nameof(HideoutController.ImproveArea));

    [PatchPrefix]
    public static bool Prefix(MongoId sessionId, PmcData pmcData, HideoutImproveAreaRequestData request, ref ItemEventRouterResponse __result)
    {
        Spot? spot = catalog?.Find(request.AreaId);
        if (spot == null || outputs == null || responses == null || inventory == null || payments == null) return true;
        ItemEventRouterResponse output = outputs.GetOutput(sessionId);
        string? refused = Refusal(pmcData, spot, request.Items ?? new List<HideoutItem>());
        if (refused != null)
        {
            log?.Warning($"[HideoutExtensions] Didn't start '{spot.Name}': {refused}.");
            __result = responses.AppendErrorToOutput(output, spot.Name + ": " + refused);
            return false;
        }
        foreach (HideoutItem handed in request.Items ?? new List<HideoutItem>())
        {
            Item item = pmcData.Inventory!.Items!.First(i => i.Id == handed.Id);
            if (payments.IsMoneyTpl(item.Template) && item.Upd?.StackObjectsCount is double stack && stack > (handed.Count ?? 0))
            {
                item.Upd.StackObjectsCount -= handed.Count ?? 0;
                continue;
            }
            inventory.RemoveItem(pmcData, item.Id, sessionId, output);
        }
        var started = new HideoutImprovement { Completed = false, ImproveCompleteTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + spot.BuildSeconds };
        pmcData.Hideout!.Improvements ??= new Dictionary<MongoId, HideoutImprovement>();
        pmcData.Hideout.Improvements[spot.Key] = started;
        output.ProfileChanges[sessionId].Improvements ??= new Dictionary<MongoId, HideoutImprovement>();
        output.ProfileChanges[sessionId].Improvements![spot.Key] = started;
        log?.Info($"[HideoutExtensions] Building '{spot.Name}' (ready in {(spot.BuildSeconds >= 3600 ? $"{spot.BuildSeconds / 3600.0:0.##} h" : $"{spot.BuildSeconds} s")}).");
        __result = output;
        return false;
    }

    private static string? Refusal(PmcData pmc, Spot spot, List<HideoutItem> handed)
    {
        if (SpotCatalog.Progress(pmc, spot) != null) return SpotCatalog.Built(pmc, spot) ? "it's already built" : "it's already being built";
        string? locked = SpotCatalog.Locked(pmc, spot);
        if (locked != null) return locked;
        var given = new Dictionary<MongoId, double>();
        foreach (HideoutItem h in handed)
        {
            Item? item = pmc.Inventory?.Items?.FirstOrDefault(i => i.Id == h.Id);
            if (item == null) return "an item you chose is gone";
            if (spot.Requirements.All(c => new MongoId(c.Template) != item.Template)) return $"{item.Template} isn't part of its cost";
            double count = payments!.IsMoneyTpl(item.Template) ? h.Count ?? 0 : item.Upd?.StackObjectsCount ?? 1;
            given[item.Template] = given.GetValueOrDefault(item.Template) + count;
        }
        foreach (Cost cost in spot.Requirements)
            if (given.GetValueOrDefault(new MongoId(cost.Template)) < cost.Count) return $"not enough of {cost.Template} handed over";
        return null;
    }
}

[Injectable(TypePriority = OnLoadOrder.Preload + 11)]
public sealed class Patches(IEnumerable<IRuntimePatch> patches) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        foreach (IRuntimePatch patch in patches.Where(p => p.GetType().Namespace == typeof(Patches).Namespace)) patch.Enable();
        return Task.CompletedTask;
    }
}
