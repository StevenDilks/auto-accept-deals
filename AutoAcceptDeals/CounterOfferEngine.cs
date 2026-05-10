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

internal static class CounterOfferEngine
{
    private const int QuantityCap = 1000; // Customer.MaxOrderQuantityPerProduct

    // Phase 6 acceptance oracle (kept for reference, no longer called in the hot path):
    // Customer.EvaluateCounteroffer(ProductDefinition, qty, totalPrice) -> bool — confirmed total-dollar.
    // Non-deterministic: identical calls returned 1606/1643/1736 (~8% spread). Phase 7 replaces
    // the bisect loop with a deterministic probability reimplementation ported from
    // BetterCounterOfferUI 3.3.0 (OverweightUnicorn), which uses only public game APIs.

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

        var (total, strategy) = SearchByProbability(r.Customer, r.Product, effectiveQty, r.Quantity, r.Payment);

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

        int ceiling = (int)Math.Floor(ctx.SpendingLimit) - 1;
        int floorInt = (int)Math.Ceiling(floor);
        if (ceiling <= floorInt) return (floor, "probability-floor-at-limit");

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
                var orderDays = data.GetOrderDays(customer.CurrentAddiction, relStrength);
                int dayCount = orderDays == null || orderDays.Count == 0 ? 1 : orderDays.Count;

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
