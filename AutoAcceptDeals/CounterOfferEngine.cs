using System;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Product;
using MelonLoader;

namespace AutoAcceptDeals;

internal sealed record CounterProposal(
    ProductDefinition Product,
    int Quantity,
    float UnitPrice,
    float TotalPrice,
    string Strategy,
    int OriginalQuantity);

internal static class CounterOfferEngine
{
    private const int QuantityCap = 1000;        // Customer.MaxOrderQuantityPerProduct
    private const int MinAbsoluteCeiling = 5_000; // total-dollar headroom for the search (caps tiny-offer ranges)
    private const int MaxBisectIterations = 32;

    // TODO(Phase 7): remove _unitsProbed, RunUnitsProbeOnce, and OnSceneLeave once SendCounteroffer
    //   is shipped and total-dollar semantics are locked in. OnSceneLeave becomes a no-op without these.
    private static bool _unitsProbed;

    public static void OnSceneLeave()
    {
        _unitsProbed = false;
    }

    // Phase 6 acceptance oracle: Customer.EvaluateCounteroffer(ProductDefinition, qty, totalPrice) -> bool.
    // Empirically confirmed (units probe, 2026-05-04): `price` is TOTAL dollars, not per-unit. Customer
    // accepts at $1 (way below offer) and at the $1290 offer, rejects at $100k — classic "max-total"
    // shape. Reinterpreting earlier puzzling convergence values: $1606 "per-unit" for 25 Poor greencrack
    // was actually $1606 total = $64/unit, which fits game economy.
    //
    // Customer.GetOfferSuccessChance was investigated and rejected: returns 0.0 deterministically for
    // baskets built either as `new ProductItemInstance(p, qty, quality, null)` or via factory
    // `product.GetDefaultInstance(qty)` + `SetQuality(quality)`, and has no callers in decompiled
    // Assembly-CSharp. EvaluateCounteroffer is what ProcessCounterOfferServerSide actually invokes.
    //
    // Known issue (deferred to Phase 7): EvaluateCounteroffer is non-deterministic. Three identical
    // (customer, product, qty, price) calls returned converged values 1606 / 1643 / 1736 — an 8% spread.
    // The function rolls internally. Binary search assumes monotonicity (accept => all-lower-accept),
    // which breaks under randomness; convergence drifts run-to-run. For Phase 6 we ship best-effort and
    // address when SendCounteroffer is wired up: probably sample-N-take-min, or apply a safety margin.
    public static bool TryPropose(DealRequest r, out CounterProposal proposal)
    {
        proposal = null!;
        if (r.Product == null)
        {
            MelonLogger.Warning(
                $"AAD: engine — product '{r.ProductId}' did not resolve to ProductDefinition; skipping.");
            return false;
        }

        var rounded = QuantityMath.RoundUpToMultiple(r.Quantity, Settings.RoundingMultiple);
        if (rounded > QuantityCap)
            MelonLogger.Warning($"AAD: engine — rounded qty {rounded} exceeds cap {QuantityCap}; clamping.");
        var effectiveQty = QuantityMath.Clamp(rounded, QuantityCap);

        RunUnitsProbeOnce(r.Customer, r.Product, effectiveQty, r.Payment);

        var (total, strategy) = SearchByEvaluator(r.Customer, r.Product, effectiveQty, r.Payment);

        float unit = total / Math.Max(1, effectiveQty);
        proposal = new CounterProposal(
            r.Product, effectiveQty, unit, total, strategy, r.Quantity);
        return true;
    }

    private static void RunUnitsProbeOnce(Customer customer, ProductDefinition product, int qty, float offeredTotal)
    {
        if (_unitsProbed) return;
        _unitsProbed = true;

        bool low, atOffer, high;
        try { low = customer.EvaluateCounteroffer(product, qty, 1f); }
        catch (Exception ex) { MelonLogger.Warning($"AAD: units probe — at(1) threw: {ex.Message}"); return; }
        try { atOffer = customer.EvaluateCounteroffer(product, qty, offeredTotal); }
        catch (Exception ex) { MelonLogger.Warning($"AAD: units probe — at(offer) threw: {ex.Message}"); return; }
        try { high = customer.EvaluateCounteroffer(product, qty, 100000f); }
        catch (Exception ex) { MelonLogger.Warning($"AAD: units probe — at(100000) threw: {ex.Message}"); return; }

        // Interpretation cheat sheet:
        //   at(1)=true,  at(offer)=true, at(100000)=false → price is TOTAL dollars (customer accepts anything ≤ max-total)
        //   at(1)=false, at(offer)=true, at(100000)=true  → price is PER-UNIT (with very loose ceiling)
        //   at(1)=false, at(offer)=true, at(100000)=false → PER-UNIT with realistic ceiling (current assumption)
        //   any other pattern → semantics unclear; needs deeper investigation.
        MelonLogger.Msg(
            $"AAD: units probe — product={product.ID}, qty={qty}, offeredTotal={offeredTotal:F0} | " +
            $"at(1)={low}, at(offer={offeredTotal:F0})={atOffer}, at(100000)={high}");
    }

    // Returns (best_total_price, strategy). Total dollars at integer resolution (matches the in-game UI).
    private static (float total, string strategy) SearchByEvaluator(
        Customer customer, ProductDefinition product, int qty, float floor)
    {
        long rawCeiling = (long)Math.Max((double)floor * 16.0, (double)floor + MinAbsoluteCeiling);
        int ceiling = (int)Math.Min(int.MaxValue - 1, rawCeiling);
        int floorInt = (int)Math.Ceiling(floor);
        int lo = floorInt;
        int hi = ceiling;
        int best = -1;
        int iterations = 0;
        while (lo <= hi && iterations++ < MaxBisectIterations)
        {
            int mid = lo + (hi - lo) / 2;
            bool accepted;
            try { accepted = customer.EvaluateCounteroffer(product, qty, mid); }
            catch (Exception ex)
            {
                MelonLogger.Warning($"AAD: EvaluateCounteroffer threw at price {mid}: {ex.Message}");
                break;
            }
            if (accepted) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        if (best >= 0) return (best, "evaluator");

        // Customer rejected every integer in [Ceiling(floor), ceiling]. Shouldn't happen in practice
        // (a total ≥ the customer's own offer should accept), but guard so we ship the floor unchanged
        // and let TryPropose's fallback-floor restore the customer's exact offer.
        MelonLogger.Warning(
            $"AAD: engine — EvaluateCounteroffer rejected every probe in [{floorInt}, {ceiling}] for {product.ID}; using floor unchanged.");
        return (floor, "evaluator-no-accept");
    }
}
