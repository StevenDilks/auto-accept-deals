using System;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.ItemFramework;
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

// Distinguishes "we evaluated this deal and it genuinely doesn't clear the min-profit floor"
// from "we couldn't evaluate it at all" (missing customer/relation data, or a degenerate search
// range). Only the former is a real "this deal is unprofitable" verdict — callers should not
// auto-decline on the latter, since that would permanently reject a deal over a transient
// data hiccup rather than an actual pricing judgment.
internal enum CounterOfferFailureReason
{
    CouldNotEvaluate,
    NoProfitableCounterFound,
}

internal static class CounterOfferEngine
{
    private const int QuantityCap = 1000; // Customer.MaxOrderQuantityPerProduct

    // Phase 6 acceptance oracle (kept for reference, no longer called in the hot path):
    // Customer.EvaluateCounteroffer(ProductDefinition, qty, totalPrice) -> bool — confirmed total-dollar.
    // Non-deterministic: identical calls returned 1606/1643/1736 (~8% spread). Phase 7 replaces
    // the bisect loop with a deterministic probability reimplementation ported from
    // BetterCounterOfferUI 3.3.0 (OverweightUnicorn), which uses only public game APIs.

    public static bool TryPropose(DealRequest r, out CounterProposal proposal, out CounterOfferFailureReason reason)
    {
        proposal = null!;
        reason = CounterOfferFailureReason.CouldNotEvaluate;

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

        var (total, strategy) = SearchByProbability(r.Customer, r.Product, effectiveQty, r.Quantity, r.Payment);

        // Only "probability" means the search actually ran against valid customer/relation data
        // and produced a price. The other strategies ("probability-no-context",
        // "probability-floor-at-limit", "probability-no-accept") are all evaluation failures —
        // missing data or a degenerate search range — not a verdict that the deal is unprofitable.
        if (strategy != "probability")
            return false;

        // Guards against sending a technically-100%-probability counter that isn't worth making.
        // Compared per-unit rather than as a total-dollar bump — otherwise a rounding-inflated
        // quantity can clear a total-price floor while the per-unit price actually drops (e.g.
        // 90 for 2 units -> 105 for 5 units is +17% total but -53% per unit). Reject the deal
        // (no counter sent) rather than chase margin the player wouldn't want anyway.
        float origUnitPrice = r.Payment / Math.Max(1, r.Quantity);
        float minUnitPrice = origUnitPrice * (1f + Settings.MinProfitPercent / 100f);
        float minAcceptable = minUnitPrice * effectiveQty;
        if (total < minAcceptable)
        {
            var name = r.Customer.NPC?.FullName ?? "<unknown>";
            float bestUnitPrice = total / Math.Max(1, effectiveQty);
            MelonLogger.Msg(
                $"AAD: {name} — best counter for {r.ProductId}×{effectiveQty} only reaches {bestUnitPrice:F2}/unit " +
                $"(need ≥{minUnitPrice:F2}/unit for {Settings.MinProfitPercent:F0}% min profit over {origUnitPrice:F2}/unit); declining, no counter sent.");
            reason = CounterOfferFailureReason.NoProfitableCounterFound;
            return false;
        }

        float unit = total / Math.Max(1, effectiveQty);
        proposal = new CounterProposal(r.Product, effectiveQty, unit, total, strategy, r.Quantity);
        return true;
    }

    // Binary-searches for the highest integer total-dollar price where Probability == 1.0f.
    // ~17 iterations over [ceil(floor), spendingLimit-1].
    private static (float total, string strategy) SearchByProbability(
        Customer customer, ProductDefinition product, int qty, int origQty, float floor)
    {
        var ctx = ProbabilityContext.Capture(customer, product, qty, origQty, floor);
        if (ctx == null) return (floor, "probability-no-context");

        // SpendingLimit is a heuristic reimplementation of the game's real (unknown) limit, and it
        // can be too generous — the bisection below then finds a price it believes is a guaranteed
        // accept, but the real game rejects it outright. Stay clear of the modeled edge by a
        // configurable margin instead of searching right up to it.
        float safeLimit = ctx.SpendingLimit * (Settings.SpendingLimitSafetyMarginPercent / 100f);
        int ceiling = (int)Math.Floor(safeLimit) - 1;
        int floorInt = (int)Math.Ceiling(floor);
        if (ceiling <= floorInt) return (floor, "probability-floor-at-limit");

        // No iteration cap needed — integer bisection over a finite [lo, hi] range terminates in ≤ log2(range) steps.
        int lo = floorInt, hi = ceiling, best = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            float vp2 = Customer.GetValueProposition(product, mid / (float)qty);
            float p = ctx.Probability(mid, vp2);
            if (p >= 1.0f) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }

        if (best >= 0) return (best, "probability");

        MelonLogger.Warning(
            $"AAD: engine — probability formula found no 100%-accept price in [{floorInt}, {ceiling}] for {product.ID}; using floor unchanged.");
        return (floor, "probability-no-accept");
    }

    private sealed class ProbabilityContext
    {
        public float SpendingLimit;
        public float ValueProposition0;
        public float ProductEnjoyment;
        public int   OriginalQuantity;
        public float MaxAddictionRelation;
        public int   Qty;

        public float Probability(float price, float vp2)
            => ProbabilityFormula.Compute(
                price, SpendingLimit, ValueProposition0, vp2,
                ProductEnjoyment, Qty, OriginalQuantity, MaxAddictionRelation);

        public static ProbabilityContext? Capture(
            Customer customer, ProductDefinition product, int qty, int origQty, float origPayment)
        {
            try
            {
                var data = customer.customerData;
                if (data == null) { MelonLogger.Warning("AAD: ProbabilityContext — customerData null."); return null; }
                var npc = customer.NPC;
                if (npc == null) { MelonLogger.Warning("AAD: ProbabilityContext — NPC null."); return null; }
                var rel = npc.RelationData;
                if (rel == null) { MelonLogger.Warning("AAD: ProbabilityContext — RelationData null."); return null; }

                float relStrength = rel.RelationDelta / 5f;
                float weeklySpend = data.GetAdjustedWeeklySpend(relStrength);
                var orderDays = new Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.GameTime.EDay>();
                data.GetOrderDays(customer.CurrentAddiction, relStrength, orderDays);
                int dayCount = orderDays.Count == 0 ? 1 : orderDays.Count;

                float vp0 = Customer.GetValueProposition(product, origPayment / Math.Max(1, origQty));
                var quality = StandardsMethod.GetCorrespondingQuality(data.Standards);
                float enjoyment = customer.GetProductEnjoyment(product, quality);
                float maxAddRel = Math.Max(customer.CurrentAddiction, rel.NormalizedRelationDelta);

                return new ProbabilityContext
                {
                    SpendingLimit       = weeklySpend / dayCount * 3f,
                    ValueProposition0   = vp0,
                    ProductEnjoyment    = enjoyment,
                    OriginalQuantity    = origQty,
                    MaxAddictionRelation = maxAddRel,
                    Qty                 = qty,
                };
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"AAD: ProbabilityContext.Capture threw: {ex.Message}");
                return null;
            }
        }
    }
}
