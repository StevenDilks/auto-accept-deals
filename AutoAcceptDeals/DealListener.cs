using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppScheduleOne;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Quests;
using MelonLoader;
using UnityEngine;

namespace AutoAcceptDeals;

internal sealed record DealRequest(
    Customer Customer,
    string ProductId,
    ProductDefinition? Product,
    EQuality Quality,
    int Quantity,
    EMapRegion Region,
    float Payment);

[HarmonyPatch(typeof(Customer), nameof(Customer.OfferContract))]
internal static class OfferContractPatch
{
    [HarmonyPostfix]
    private static void Postfix(Customer __instance, ContractInfo info)
    {
        try { DealListener.HandleOffer(__instance, info); }
        catch (Exception ex) { MelonLogger.Error($"AAD: OfferContract postfix threw: {ex}"); }
    }
}

internal static class DealListener
{
    private static bool _discoveredThisSession;
    private static IntPtr _lastCustomerPtr;
    private static IntPtr _lastInfoPtr;
    private static float _lastHandledTime;
    private const float DuplicateWindowSeconds = 0.5f;

    public static void HandleOffer(Customer customer, ContractInfo info)
    {
        if (!ModState.ShouldRun) return;
        if (customer == null || info == null) return;
        if (info.IsCounterOffer) return;

        var custPtr = customer.Pointer;
        var infoPtr = info.Pointer;
        var now = Time.realtimeSinceStartup;
        if (custPtr == _lastCustomerPtr && infoPtr == _lastInfoPtr &&
            now - _lastHandledTime < DuplicateWindowSeconds)
        {
            return;
        }
        _lastCustomerPtr = custPtr;
        _lastInfoPtr = infoPtr;
        _lastHandledTime = now;

        EnsureDiscovered();

        if (!TryExtractFirstProduct(info, out var productId, out var product, out var quality, out var quantity))
        {
            MelonLogger.Warning("AAD: ContractInfo had no product entries; skipping.");
            return;
        }

        var region = ResolveRegion(customer);
        var request = new DealRequest(customer, productId, product, quality, quantity, region, info.Payment);
        ProcessRequest(request);
    }

    public static void OnSceneLeave() => _discoveredThisSession = false;

    private static void ProcessRequest(DealRequest r)
    {
        var name = r.Customer?.NPC?.fullName ?? "<unknown>";
        MelonLogger.Msg(
            $"AAD: deal request — customer={name}, product={r.ProductId}×{r.Quantity} ({r.Quality}), region={r.Region}, payment={r.Payment}");
    }

    private static bool TryExtractFirstProduct(
        ContractInfo info,
        out string productId,
        out ProductDefinition? product,
        out EQuality quality,
        out int quantity)
    {
        productId = "";
        product = null;
        quality = default;
        quantity = 0;

        var list = info.Products;
        if (list == null) return false;
        var entries = list.entries;
        if (entries == null || entries.Count == 0) return false;

        var entry = entries[0];
        if (entry == null) return false;

        productId = entry.ProductID ?? "";
        quality = entry.Quality;
        quantity = entry.Quantity;

        if (!string.IsNullOrEmpty(productId))
            product = Registry.GetItem(productId)?.TryCast<ProductDefinition>();

        return true;
    }

    private static EMapRegion ResolveRegion(Customer customer)
    {
        if (!Map.InstanceExists) return default;

        var npc = customer.NPC;
        var transform = npc != null ? npc.transform : null;
        var pos = transform != null ? transform.position : Vector3.zero;

        return Map.instance.GetRegionFromPosition(pos);
    }

    private static void EnsureDiscovered()
    {
        if (_discoveredThisSession) return;

        if (!Map.InstanceExists)
        {
            MelonLogger.Warning("AAD: Map.instance unavailable; deferring discovery to next deal.");
            return;
        }

        var map = Map.instance;
        var regions = map.Regions;
        if (regions == null)
        {
            MelonLogger.Warning("AAD: Map.instance.Regions was null; deferring discovery to next deal.");
            return;
        }

        _discoveredThisSession = true;

        int total = 0;
        var lines = new List<string>();
        foreach (var regionData in regions)
        {
            if (regionData == null) continue;
            var region = regionData.Region;
            var found = new List<DiscoveredLocation>();
            var seenGuids = new HashSet<string>();
            var locs = regionData.RegionDeliveryLocations;
            if (locs != null)
            {
                foreach (var loc in locs)
                {
                    if (loc == null) continue;
                    var guid = loc.StaticGUID ?? "";
                    if (string.IsNullOrEmpty(guid)) continue;
                    if (!seenGuids.Add(guid)) continue;
                    found.Add(new DiscoveredLocation(loc.LocationName ?? "", guid));
                }
            }

            DiffAndWarn(region, found);
            Settings.RecordDiscoveredLocations(region, found);
            total += found.Count;
            lines.Add($"  {region}: {found.Count} location(s) — " +
                      string.Join(", ", found.Select(l => $"{l.Name} ({l.Guid})")));
        }

        MelonLogger.Msg($"AAD: discovery walk — {regions.Count} region(s), {total} location(s).");
        foreach (var line in lines) MelonLogger.Msg(line);
    }

    private static void DiffAndWarn(EMapRegion region, List<DiscoveredLocation> found)
    {
        if (!Settings.DiscoveredLocations.TryGetValue(region, out var cached) || cached.Count == 0) return;

        var cachedByGuid = cached
            .Where(l => !string.IsNullOrEmpty(l.Guid))
            .GroupBy(l => l.Guid)
            .ToDictionary(g => g.Key, g => g.First().Name);
        var foundByGuid = found
            .GroupBy(l => l.Guid)
            .ToDictionary(g => g.Key, g => g.First().Name);

        foreach (var (guid, name) in foundByGuid)
            if (!cachedByGuid.ContainsKey(guid))
                MelonLogger.Warning($"AAD: {region} added location since last run: {name} ({guid}).");

        foreach (var (guid, name) in cachedByGuid)
            if (!foundByGuid.ContainsKey(guid))
                MelonLogger.Warning($"AAD: {region} removed location since last run: {name} ({guid}).");

        foreach (var (guid, name) in foundByGuid)
            if (cachedByGuid.TryGetValue(guid, out var oldName) && oldName != name)
                MelonLogger.Warning($"AAD: {region} renamed location {guid}: '{oldName}' → '{name}'.");
    }
}
