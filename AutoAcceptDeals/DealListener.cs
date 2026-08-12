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
        DealStats.ResetForSceneLeave();
    }

    private static void ProcessRequest(DealRequest r)
    {
        if (!CounterOfferEngine.TryPropose(r, out var p, out var reason))
        {
            // Only a genuine "no counter clears the min profit floor" verdict counts as a decline —
            // an evaluation failure (missing customer/relation data, degenerate search range) isn't
            // a pricing judgment, so recording it as declined or auto-rejecting it would punish the
            // customer for a transient mod-internal hiccup rather than an actual unprofitable deal.
            if (reason == CounterOfferFailureReason.NoProfitableCounterFound)
            {
                // Recorded here, at the moment the decision is made, rather than inside
                // AutoDeclineAfterDelay — that way the stat reflects every deal the engine actually
                // rejected, even when AutoDeclineUncounterableDeals is off and nothing ever clicks
                // the decline button.
                DealStats.RecordDealDeclined();
                if (Settings.AutoDeclineUncounterableDeals)
                    MelonCoroutines.Start(AutoDeclineAfterDelay(r.Customer));
            }
            else
            {
                var name = r.Customer.NPC?.FullName ?? "<unknown>";
                MelonLogger.Warning($"AAD: {name} — could not evaluate a counter-offer (missing data); leaving unanswered rather than declining.");
            }
            return;
        }
        SendCounterOffer(r, p);
    }

    // Without this, a deal AAD can't (or won't) counter just sits unanswered in the player's
    // texts until they open the phone and decline it manually. Mirrors AcceptAfterDelay: calling
    // a contract-response method synchronously inside OfferContract's call stack throws
    // NullReferenceException in game code, so this is deferred and retried across frames too.
    //
    // Route through MSGConversation's Response system (the same one the "Sure thing" /
    // "[Counter-offer]" / "No" buttons use) rather than calling Customer.ContractRejected()
    // directly. ContractRejected() alone sends the canned decline reply but does not clear the
    // response buttons — those are only cleared by MSGConversation.ResponseChosen(), which is what
    // actually runs when the player clicks a button. ResponseChosen internally invokes the
    // response's own callback (ContractRejected for reject), so this replaces — not supplements —
    // the direct ContractRejected() call.
    private static IEnumerator AutoDeclineAfterDelay(Customer customer)
    {
        var name = customer.NPC?.FullName ?? "<unknown>";
        for (int attempt = 0; attempt < 20; attempt++)
        {
            yield return null;
            try
            {
                var conversation = MessagingManager.InstanceExists ? MessagingManager.Instance.GetConversation(customer.NPC) : null;
                var declineResponse = FindDeclineResponse(conversation?.currentResponses);

                if (conversation != null && declineResponse != null)
                {
                    conversation.ResponseChosen(declineResponse, true);
                }
                else if (attempt == 19)
                {
                    // Never positively identified a reject response after 20 frames — fall back so
                    // the deal doesn't go completely unanswered. This won't clear the UI buttons
                    // (the whole reason for the ResponseChosen path above), but it's better than
                    // nothing, and — critically — it can never click Accept by mistake.
                    customer.ContractRejected();
                }
                else
                {
                    // Buttons haven't populated yet (currentResponses lags a frame or more behind
                    // the offer), or this contract's response set doesn't match the shape
                    // FindDeclineResponse can positively identify (e.g. built with
                    // canCounterOffer:false). Retry rather than guessing — clicking the wrong
                    // response here means signing the player up for a deal the engine just judged
                    // unprofitable.
                    continue;
                }

                // ResponseChosen/ContractRejected resolve the chat UI (buttons clear, "Oh ok" reply
                // shows) but observed in-game: the underlying OfferedContractInfo the customer's own
                // offer-expiry RPC timer (Customer.ExpireOffer/UpdateOfferExpiry) tracks isn't
                // cleared by either — that timer still fires ~10 minutes later and sends its own
                // give-up line ("Actually, nevermind"), on top of the already-resolved chat. The
                // interop dummy exposes OfferedContractInfo's setter as public (the real game marks
                // it protected), so null it out directly to defuse that timer instead of waiting for
                // a second unwanted message.
                try { customer.OfferedContractInfo = null; }
                catch (Exception ex) { MelonLogger.Warning($"AAD: failed to clear OfferedContractInfo for {name}: {ex.GetType().Name}: {ex.Message}"); }
            }
            catch (Exception ex)
            {
                if (attempt == 19)
                    MelonLogger.Warning($"AAD: auto-decline still failing after 20 frames for {name}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            MelonLogger.Msg($"AAD: {name} — no viable counter; auto-declined so it doesn't sit unanswered.");
            yield break;
        }
    }

    // Positively identifies the reject response instead of trusting "whatever is last in the
    // list" — a short currentResponses list (mid-population, or an offer built with
    // canReject:false / canCounterOffer:false) would otherwise put the *accept* response in the
    // last slot, and clicking that signs the player up for the exact deal the engine just judged
    // unprofitable. Accept/Reject button captions are NPC-personality flavor text and can't be
    // matched by string, but "[Counter-offer]" is observed stable across customers — so the
    // reject response is identified structurally as the entry immediately after it, and only
    // when that's also the last entry (the documented Accept, Counter-offer, Reject order). Any
    // other shape returns null rather than guess.
    private static Response? FindDeclineResponse(Il2CppSystem.Collections.Generic.List<Response>? responses)
    {
        if (responses == null || responses.Count < 2) return null;

        int counterIdx = -1;
        for (int i = 0; i < responses.Count; i++)
        {
            if (responses[i]?.label == "[Counter-offer]") { counterIdx = i; break; }
        }
        if (counterIdx < 0 || counterIdx != responses.Count - 2) return null;

        return responses[counterIdx + 1];
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

        try
        {
            // SendCounteroffer.price is total dollars — same semantics as EvaluateCounteroffer (confirmed Phase 6).
            customer.SendCounteroffer(p.Product, p.Quantity, p.TotalPrice);
        }
        catch (Exception ex)
        {
            // If this throws (IL2CPP null, network not ready), HandleCounterOfferAccepted never ran
            // and the registry entry above would otherwise leak — worse, it would still be sitting
            // there as "pending" the next time this customer makes a genuine offer, which would
            // then get misrouted into HandleCounterOfferAccepted and stamped with these stale
            // values. Clear it out before anything else can observe it.
            _registry.TakeForKey(customer.Pointer);
            MelonLogger.Error($"AAD: {name} — SendCounteroffer threw before completing; pending entry cleared: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // If HandleCounterOfferAccepted already fired synchronously (nested inside the call above),
        // the registry entry is gone and it has already logged the outcome itself — nothing more to do.
        if (!_registry.HasPending(customer.Pointer)) return;

        // Not resolved synchronously. Observed in-game: the game's OfferContract callback can still
        // arrive on a later frame (async round-trip) rather than nested in this call stack — give it
        // time before concluding it was rejected outright, instead of declaring "rejected" prematurely.
        MelonCoroutines.Start(WaitForAsyncOutcome(customer, r, p));
    }

    private enum DealOutcome { Accepted, AwaitingPlayerScheduling, Rejected }

    // Only reached when HandleCounterOfferAccepted didn't fire synchronously inside SendCounteroffer.
    // Waits several frames for a delayed OfferContract callback before declaring a genuine rejection.
    private static IEnumerator WaitForAsyncOutcome(Customer customer, DealRequest r, CounterProposal p)
    {
        for (int frame = 0; frame < 60; frame++)
        {
            yield return null;

            // If the mod got toggled off (or the player left the scene) mid-wait, HandleOffer's own
            // early-out means a delayed OfferContract callback would never reach us — we can no
            // longer tell "the game rejected it" from "we stopped listening". Bail out quietly
            // rather than logging an accusatory warning and recording a phantom decline for a
            // counter that may well have been accepted.
            if (!ModState.ShouldRun)
            {
                _registry.TakeForKey(customer.Pointer);
                yield break;
            }

            if (!_registry.HasPending(customer.Pointer))
                yield break; // HandleCounterOfferAccepted fired asynchronously and already logged the outcome
        }

        var pending = _registry.TakeForKey(customer.Pointer);
        if (pending == null) yield break; // resolved in the same frame we gave up on

        var name = customer.NPC?.FullName ?? "<unknown>";
        MelonLogger.Warning(
            $"AAD: {name} — counter-offer for {r.ProductId}×{p.Quantity} at {p.TotalPrice:F0} was rejected outright by the game " +
            "(no OfferContract callback came back after waiting); the probability model likely overestimated this customer's spending limit.");
        LogOutcome(pending, DealOutcome.Rejected);
    }

    // Single source of truth for both the outcome log line and the matching stat — the two used to
    // live in different places (made recorded here, declined recorded at each call site), which is
    // an asymmetry any future edit could silently break.
    private static void LogOutcome(PendingSend pending, DealOutcome outcome)
    {
        var name = pending.Customer.NPC?.FullName ?? "<unknown>";
        var windowStr = pending.TimeModeSnapshot == TimeMode.WaitForPlayer
            ? "player-chosen"
            : (pending.Window.HasValue ? pending.Window.Value.ToString() : "none");
        var outcomeStr = outcome switch
        {
            DealOutcome.Accepted => "accepted",
            DealOutcome.AwaitingPlayerScheduling => "awaiting player scheduling",
            DealOutcome.Rejected => "rejected",
            _ => outcome.ToString(),
        };
        MelonLogger.Msg(
            $"AAD: {name} — {pending.ProductId}×{pending.OriginalQuantity}→{pending.Quantity} ({pending.Quality}), " +
            $"payment {pending.OrigPayment:F0}→{pending.TotalPrice:F0} ({pending.Strategy}), region={pending.Region}, " +
            $"location={pending.LocationGuid ?? "default"}, window={windowStr} → {outcomeStr}.");

        if (outcome == DealOutcome.Accepted || outcome == DealOutcome.AwaitingPlayerScheduling)
        {
            float origUnit = pending.OrigPayment / Math.Max(1, pending.OriginalQuantity);
            float newUnit = pending.TotalPrice / Math.Max(1, pending.Quantity);
            float marginPercent = origUnit > 0f ? (newUnit - origUnit) / origUnit * 100f : 0f;
            DealStats.RecordDealMade(marginPercent);
        }
        else
        {
            DealStats.RecordDealDeclined();
        }
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
            // they originally asked for) — force our countered values back onto it here. Prefer
            // customer.OfferedContractInfo over the postfix's `info` parameter: nothing guarantees
            // they're the same instance, and if they aren't, writing to `info` alone never lands.
            var target = customer.OfferedContractInfo ?? info;
            if (!ApplyCounterOfferValues(target, pending))
                MelonLogger.Warning(
                    $"AAD: {customer.NPC?.FullName ?? "?"} — ApplyCounterOfferValues failed for product '{pending.ProductId}'; payment/quantity not enforced on initial application.");

            // Apply window times to ContractInfo synchronously.
            DealOutcome outcome;
            if (pending.TimeModeSnapshot != TimeMode.WaitForPlayer && pending.Window.HasValue)
            {
                var wi = DealWindowInfo.GetWindowInfo(pending.Window.Value);
                var wc = target.DeliveryWindow;
                if (wc != null)
                {
                    wc.IsEnabled = true;
                    wc.WindowStartTime = wi.StartTime;
                    wc.WindowEndTime = wi.EndTime;
                }

                // PlayerAcceptedContract must be deferred — calling it synchronously inside
                // OfferContract's call stack throws NullReferenceException in game code.
                MelonCoroutines.Start(AcceptAfterDelay(customer, pending));
                outcome = DealOutcome.Accepted;
            }
            else
            {
                // WaitForPlayer: the player accepts from the phone whenever they get to it, which
                // can be minutes away. AcceptAfterDelay's 20-frame retry window doesn't apply here
                // (nothing calls PlayerAcceptedContract on our behalf) — but the same field-reset
                // problem does, so keep re-stamping OfferedContractInfo until it resolves.
                MelonCoroutines.Start(ReapplyCounterOfferValuesWhileWaiting(customer, pending, target));
                outcome = DealOutcome.AwaitingPlayerScheduling;
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
                    // Only warn on the first attempt — a failure here is structural and will repeat
                    // identically on every retry; logging it 20 times adds nothing.
                    if (!ApplyCounterOfferValues(offered, pending) && attempt == 0)
                        MelonLogger.Warning(
                            $"AAD: {customer.NPC?.FullName ?? "?"} — ApplyCounterOfferValues failed for product '{pending.ProductId}'; contract may retain original payment/quantity.");
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

    // WaitForPlayer mode: nothing calls PlayerAcceptedContract for us, so unlike AcceptAfterDelay
    // this can't just retry a handful of frames and give up — the player might not open the phone
    // for minutes. Re-stamp every frame until the offer resolves (OfferedContractInfo changes away
    // from this contract, or goes null) or a generous real-time ceiling is hit, matching roughly the
    // customer's own offer-expiry window so this doesn't outlive the offer itself.
    //
    // `target` is the same ContractInfo HandleCounterOfferAccepted already resolved and applied to
    // synchronously (customer.OfferedContractInfo, falling back to the postfix's `info` if that was
    // null at that moment) — tracking its pointer directly means a null-at-start OfferedContractInfo
    // no longer makes this a same-frame no-op.
    private static IEnumerator ReapplyCounterOfferValuesWhileWaiting(Customer customer, PendingSend pending, ContractInfo target)
    {
        const float MaxWaitSeconds = 15f * 60f;
        var deadline = Time.realtimeSinceStartup + MaxWaitSeconds;
        var trackedInfoPtr = target.Pointer;
        var loggedFailure = false;

        while (Time.realtimeSinceStartup < deadline)
        {
            yield return null;
            if (!ModState.ShouldRun) yield break;

            ContractInfo? offered;
            try { offered = customer.OfferedContractInfo; }
            catch { yield break; } // customer/NPC torn down (scene change, despawn)

            if (offered == null || offered.Pointer != trackedInfoPtr)
                yield break; // resolved (accepted/rejected/expired) or replaced by a newer offer

            try
            {
                // Only warn once — a failure here is structural (missing/mismatched product entry)
                // and will repeat identically on every frame for up to 15 minutes; logging it every
                // time would spam ~54,000 identical lines.
                if (!ApplyCounterOfferValues(offered, pending) && !loggedFailure)
                {
                    loggedFailure = true;
                    MelonLogger.Warning(
                        $"AAD: ReapplyCounterOfferValuesWhileWaiting — ApplyCounterOfferValues failed for {customer.NPC?.FullName ?? "?"}; will keep retrying silently for the rest of the wait.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"AAD: ReapplyCounterOfferValuesWhileWaiting failed for {customer.NPC?.FullName ?? "?"}: {ex.GetType().Name}: {ex.Message}");
                yield break;
            }
        }
    }

    // Forces the countered quantity/price back onto a ContractInfo. The game's counter-offer
    // round-trip (SendCounteroffer -> ProcessCounterOfferServerSide -> re-offer) only carries
    // productID/quantity/price as loose scalars over the RPC, not the ContractInfo we built —
    // nothing guarantees the resulting OfferedContractInfo matches what we proposed, and in
    // practice it can still hold the customer's original ask. Enforce it explicitly.
    //
    // Resolves and validates the product entry before writing anything, so a failure can't leave
    // Payment reflecting the countered total while Quantity still reflects the customer's original
    // ask (which AcceptAfterDelay would then re-stamp and re-warn about every frame).
    //
    // Returns success/failure instead of logging itself — this gets called every frame from both
    // AcceptAfterDelay (up to 20 attempts) and ReapplyCounterOfferValuesWhileWaiting (up to ~54,000
    // frames over 15 minutes), and a failure here is structural (missing/mismatched product entry),
    // not transient — logging on every call would spam the log for the entire wait. Callers log once.
    private static bool ApplyCounterOfferValues(ContractInfo info, PendingSend pending)
    {
        var entries = info.Products?.entries;
        if (entries == null) return false;

        ProductList.Entry? match = null;
        foreach (var entry in entries)
        {
            if (entry != null && entry.ProductID == pending.ProductId) { match = entry; break; }
        }
        if (match == null) return false;

        info.Payment = pending.TotalPrice;
        match.Quantity = pending.Quantity;
        return true;
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

        int totalNew = 0;
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

            // Report only what's actually new in this region, not the region's full location
            // count — a header built from whole-walk totals while the body lists a single new
            // location is self-contradictory.
            var cachedGuids = Settings.DiscoveredLocations.TryGetValue(region, out var cached)
                ? cached.Select(l => l.Guid).ToHashSet()
                : new HashSet<string>();
            var newInRegion = found.Where(l => !cachedGuids.Contains(l.Guid)).ToList();
            if (newInRegion.Count > 0)
            {
                lines.Add($"  {region}: {newInRegion.Count} new location(s) — " +
                          string.Join(", ", newInRegion.Select(l => $"{l.Name} ({l.Guid})")));
                totalNew += newInRegion.Count;
            }

            Settings.RecordDiscoveredLocations(region, found);
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

        // Additions get this summary; removals/renames already get their own Warning line each
        // from DiffAndWarn above, so a walk with only removals isn't silent — it just doesn't
        // duplicate into this "new since last config write" summary.
        if (lines.Count > 0)
        {
            MelonLogger.Msg($"AAD: discovery walk — {totalNew} new location(s) across {lines.Count} region(s) since last config write:");
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
