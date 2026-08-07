using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppScheduleOne;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.Messaging;
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
    EMapRegion? Region,
    float Payment);

internal sealed record PendingSend(
    string? LocationGuid,
    EDealWindow? Window,
    TimeMode TimeModeSnapshot,
    Customer Customer,
    string ProductId,
    int Quantity,
    float TotalPrice,
    EQuality Quality,
    int OriginalQuantity,
    float OrigPayment,
    string Strategy,
    string Region);

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

    private static readonly PendingSendRegistry<PendingSend> _registry = new();

    public static void HandleOffer(Customer customer, ContractInfo info)
    {
        if (!ModState.ShouldRun) return;
        if (customer == null || info == null) return;

        // Counter-offer round-trip: the game calls OfferContract synchronously inside
        // SendCounteroffer with IsCounterOffer=false, so detect via registry presence instead.
        if (info.IsCounterOffer || _registry.HasPending(customer.Pointer))
        {
            HandleCounterOfferAccepted(customer, info);
            return;
        }

        var custPtr = customer.Pointer;
        var infoPtr = info.Pointer;
        var now = Time.realtimeSinceStartup;
        if (_lastCustomerPtr != IntPtr.Zero && _lastInfoPtr != IntPtr.Zero &&
            custPtr == _lastCustomerPtr && infoPtr == _lastInfoPtr &&
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
            MelonLogger.Warning("AAD: ContractInfo had no usable product entry; skipping.");
            return;
        }

        var region = ResolveRegion(customer);
        var request = new DealRequest(customer, productId, product, quality, quantity, region, info.Payment);
        ProcessRequest(request);
    }

    public static void OnSceneLeave()
    {
        _discoveredThisSession = false;
        _registry.Clear();
    }

    private static void ProcessRequest(DealRequest r)
    {
        if (!CounterOfferEngine.TryPropose(r, out var p)) return;
        SendCounterOffer(r, p);
    }

    private static void SendCounterOffer(DealRequest r, CounterProposal p)
    {
        var customer = r.Customer;
        var name = customer.NPC?.FullName ?? "<unknown>";
        var region = r.Region.HasValue ? r.Region.Value.ToString() : "<unresolved>";

        // Resolve location GUID at send time; snapshot so a settings change mid-flight can't corrupt the contract.
        string? locationGuid = Settings.LocationMode == LocationMode.Global
            ? Settings.GlobalLocationGuid
            : (r.Region.HasValue && Settings.RegionLocations.TryGetValue(r.Region.Value, out var g) ? g : null);

        if (Settings.LocationMode == LocationMode.Global && locationGuid == null)
            MelonLogger.Warning($"AAD: SendCounterOffer — Global location unset for {name}; leaving customer default.");
        else if (Settings.LocationMode == LocationMode.PerRegion && locationGuid == null)
            MelonLogger.Warning($"AAD: SendCounterOffer — PerRegion location unset for {region}; leaving customer default.");

        EDealWindow? window = Settings.TimeMode switch
        {
            TimeMode.Fixed     => Settings.FixedWindow,
            TimeMode.Randomize => PickRandomWindow(),
            _                  => null,
        };

        var pending = new PendingSend(
            locationGuid, window, Settings.TimeMode, customer, r.ProductId, p.Quantity, p.TotalPrice,
            r.Quality, p.OriginalQuantity, r.Payment, p.Strategy, region);
        _registry.Register(customer.Pointer, pending);

        // SendCounteroffer.price is total dollars — same semantics as EvaluateCounteroffer (confirmed Phase 6).
        customer.SendCounteroffer(p.Product, p.Quantity, p.TotalPrice);

        // If HandleCounterOfferAccepted already fired synchronously (nested inside the call above),
        // the registry entry is gone and it has already logged the outcome itself — nothing more to do.
        if (!_registry.HasPending(customer.Pointer)) return;

        // Not resolved synchronously. Observed in-game: the game's OfferContract callback can still
        // arrive on a later frame (async round-trip) rather than nested in this call stack — give it
        // time before concluding it was rejected outright, instead of declaring "rejected" prematurely.
        MelonCoroutines.Start(WaitForAsyncOutcome(customer, r, p));
    }

    // Only reached when HandleCounterOfferAccepted didn't fire synchronously inside SendCounteroffer.
    // Waits several frames for a delayed OfferContract callback before declaring a genuine rejection.
    private static IEnumerator WaitForAsyncOutcome(Customer customer, DealRequest r, CounterProposal p)
    {
        for (int frame = 0; frame < 60; frame++)
        {
            yield return null;
            if (!_registry.HasPending(customer.Pointer))
                yield break; // HandleCounterOfferAccepted fired asynchronously and already logged the outcome
        }

        var pending = _registry.TakeForKey(customer.Pointer);
        if (pending == null) yield break; // resolved in the same frame we gave up on

        var name = customer.NPC?.FullName ?? "<unknown>";
        MelonLogger.Warning(
            $"AAD: {name} — counter-offer for {r.ProductId}×{p.Quantity} at {p.TotalPrice:F0} was rejected outright by the game " +
            "(no OfferContract callback came back after waiting); the probability model likely overestimated this customer's spending limit.");
        LogOutcome(pending, "rejected");
    }

    private static void LogOutcome(PendingSend pending, string outcome)
    {
        var name = pending.Customer.NPC?.FullName ?? "<unknown>";
        var windowStr = pending.TimeModeSnapshot == TimeMode.WaitForPlayer
            ? "player-chosen"
            : (pending.Window.HasValue ? pending.Window.Value.ToString() : "none");
        MelonLogger.Msg(
            $"AAD: {name} — {pending.ProductId}×{pending.OriginalQuantity}→{pending.Quantity} ({pending.Quality}), " +
            $"payment {pending.OrigPayment:F0}→{pending.TotalPrice:F0} ({pending.Strategy}), region={pending.Region}, " +
            $"location={pending.LocationGuid ?? "default"}, window={windowStr} → {outcome}.");
    }

    // Called when OfferContract fires and the registry has a pending entry for this customer.
    // Usually nested synchronously inside SendCounteroffer (IsCounterOffer=false), but can also
    // fire asynchronously on a later frame — see WaitForAsyncOutcome, which covers that case.
    private static void HandleCounterOfferAccepted(Customer customer, ContractInfo info)
    {
        try
        {
            var pending = _registry.TakeForKey(customer.Pointer);
            if (pending == null) return; // not from our send path

            // The game's own counter-offer round-trip doesn't reliably leave OfferedContractInfo
            // holding the quantity/price we sent (observed in-game: customer order reverts to what
            // they originally asked for) — force our countered values back onto it here.
            ApplyCounterOfferValues(info, pending);

            // Apply window times to ContractInfo synchronously.
            string outcome;
            if (pending.TimeModeSnapshot != TimeMode.WaitForPlayer && pending.Window.HasValue)
            {
                var wi = DealWindowInfo.GetWindowInfo(pending.Window.Value);
                var wc = info.DeliveryWindow;
                if (wc != null)
                {
                    wc.IsEnabled = true;
                    wc.WindowStartTime = wi.StartTime;
                    wc.WindowEndTime = wi.EndTime;
                }

                // PlayerAcceptedContract must be deferred — calling it synchronously inside
                // OfferContract's call stack throws NullReferenceException in game code.
                MelonCoroutines.Start(AcceptAfterDelay(customer, pending));
                outcome = "accepted";
            }
            else
            {
                outcome = "awaiting player scheduling";
            }

            // Logged here (rather than back in SendCounterOffer) because this callback can fire
            // asynchronously on a later frame, well after SendCounterOffer has already returned.
            LogOutcome(pending, outcome);
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"AAD: HandleCounterOfferAccepted threw: {ex}");
        }
    }

    private static IEnumerator AcceptAfterDelay(Customer customer, PendingSend pending)
    {
        var window = pending.Window!.Value;
        var locationGuid = pending.LocationGuid;

        // PlayerAcceptedContract needs ~6-10 frames after OfferContract returns for the game
        // to finish setting up its deal-scheduling state (observed in-game: succeeds by frame 10).
        for (int attempt = 0; attempt < 20; attempt++)
        {
            yield return null;
            try
            {
                // Re-stamp location and countered quantity/price on OfferedContractInfo on every
                // attempt — the game can reset these fields between frames, so we must re-apply
                // each retry rather than trusting the single application in HandleCounterOfferAccepted.
                var offered = customer.OfferedContractInfo;
                if (offered != null)
                {
                    ApplyCounterOfferValues(offered, pending);
                    if (!string.IsNullOrEmpty(locationGuid))
                        offered.DeliveryLocationGUID = locationGuid;
                }

                customer.PlayerAcceptedContract(window);

                if (MessagingManager.InstanceExists)
                    MessagingManager.Instance.GetConversation(customer.NPC)?.SetRead(true);

                // CurrentContract is assigned asynchronously — belt-and-suspenders via a second coroutine.
                if (!string.IsNullOrEmpty(locationGuid))
                    MelonCoroutines.Start(ApplyLocationWhenContractAssigned(customer, locationGuid));
                yield break;
            }
            catch (Exception ex)
            {
                if (attempt == 19)
                    MelonLogger.Warning($"AAD: AcceptAfterDelay last retry threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
        MelonLogger.Error($"AAD: PlayerAcceptedContract still failing after 20 frames for {customer.NPC?.FullName ?? "?"}; giving up.");
    }

    // Forces the countered quantity/price back onto a ContractInfo. The game's counter-offer
    // round-trip (SendCounteroffer -> ProcessCounterOfferServerSide -> re-offer) only carries
    // productID/quantity/price as loose scalars over the RPC, not the ContractInfo we built —
    // nothing guarantees the resulting OfferedContractInfo matches what we proposed, and in
    // practice it can still hold the customer's original ask. Enforce it explicitly.
    private static void ApplyCounterOfferValues(ContractInfo info, PendingSend pending)
    {
        info.Payment = pending.TotalPrice;

        var entries = info.Products?.entries;
        if (entries == null) return;

        foreach (var entry in entries)
        {
            if (entry == null || entry.ProductID != pending.ProductId) continue;
            entry.Quantity = pending.Quantity;
            return;
        }

        MelonLogger.Warning(
            $"AAD: ApplyCounterOfferValues — no entry for product '{pending.ProductId}' in ContractInfo; quantity not enforced.");
    }

    private static IEnumerator ApplyLocationWhenContractAssigned(Customer customer, string locationGuid)
    {
        for (int i = 0; i < 30; i++)
        {
            yield return null;
            var contract = customer.CurrentContract;
            if (contract == null) continue;

            var loc = TryFindLocationByGuid(locationGuid);
            if (loc != null)
                contract.DeliveryLocation = loc;
            else
                MelonLogger.Warning($"AAD: GUID '{locationGuid}' not found when applying to CurrentContract.");
            yield break;
        }
        MelonLogger.Warning($"AAD: CurrentContract never assigned for {customer.NPC?.FullName ?? "?"}; location not applied.");
    }

    private static EDealWindow PickRandomWindow()
    {
        var values = Enum.GetValues<EDealWindow>();
        return values[UnityEngine.Random.Range(0, values.Length)];
    }

    private static DeliveryLocation? TryFindLocationByGuid(string? guid)
    {
        if (string.IsNullOrEmpty(guid) || !Map.InstanceExists) return null;
        foreach (var rd in Map.instance.Regions)
        {
            if (rd?.RegionDeliveryLocations == null) continue;
            foreach (var loc in rd.RegionDeliveryLocations)
                if (loc != null && loc.StaticGUID == guid) return loc;
        }
        return null;
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
        if (string.IsNullOrEmpty(productId)) return false;

        quality = entry.Quality;
        quantity = entry.Quantity;
        product = Registry.GetItem(productId)?.TryCast<ProductDefinition>();

        return true;
    }

    private static EMapRegion? ResolveRegion(Customer customer)
    {
        if (!Map.InstanceExists) return null;

        var npc = customer.NPC;
        var transform = npc != null ? npc.transform : null;
        if (transform == null) return null;

        return Map.instance.GetRegionFromPosition(transform.position);
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

        int total = 0;
        var lines = new List<string>();
        var regionsWalked = new List<EMapRegion>();
        foreach (var regionData in regions)
        {
            if (regionData == null) continue;
            var region = regionData.Region;
            regionsWalked.Add(region);
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

            // Only worth a log line if this region has locations not already in the config file.
            var cachedGuids = Settings.DiscoveredLocations.TryGetValue(region, out var cached)
                ? cached.Select(l => l.Guid).ToHashSet()
                : new HashSet<string>();
            if (found.Any(l => !cachedGuids.Contains(l.Guid)))
                lines.Add($"  {region}: {found.Count} location(s) — " +
                          string.Join(", ", found.Select(l => $"{l.Name} ({l.Guid})")));

            Settings.RecordDiscoveredLocations(region, found);
            total += found.Count;
        }

        var expected = new HashSet<EMapRegion>(Enum.GetValues<EMapRegion>());
        var runtime = new HashSet<EMapRegion>(regionsWalked);
        var missing = expected.Except(runtime).ToList();
        // extra: unreachable post-IL2CPP-build in practice; kept as a belt-and-suspenders guard
        var extra = runtime.Except(expected).ToList();
        if (missing.Count > 0 || extra.Count > 0)
            MelonLogger.Warning(
                $"AAD: region drift — compile-time enum has {expected.Count}, runtime has {runtime.Count}. " +
                $"Missing=[{string.Join(",", missing)}], extra=[{string.Join(",", extra)}].");

        _discoveredThisSession = true;

        if (lines.Count > 0)
        {
            MelonLogger.Msg($"AAD: discovery walk — {total} location(s) across {regions.Count} region(s), new since last config write:");
            foreach (var line in lines) MelonLogger.Msg(line);
        }
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
