using System;
using System.Linq;
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
    // Phase 6 acceptance oracle (kept for reference, no longer called in the hot path):
    // Customer.EvaluateCounteroffer(ProductDefinition, qty, totalPrice) -> bool — confirmed total-dollar.
    // Non-deterministic: identical calls returned 1606/1643/1736 (~8% spread). Phase 7 replaces
    // the bisect loop with a deterministic probability reimplementation ported from
    // BetterCounterOfferUI 3.3.0 (OverweightUnicorn), which uses only public game APIs.
    //
    // Phase 8 (issue #10): the rounding floor is not necessarily the best quantity that still
    // clears 100% acceptance — ProbabilityFormula's quantity-ratio term is a tent peaking at
    // qty == origQty, but the value-proposition term generally rises with qty at a fixed price,
    // so feasibility isn't monotone in qty either direction. TryPropose now climbs
    // QuantitySearch.FindBest over multiples of RoundingMultiple, filtering each candidate
    // through the existing min-profit-per-unit guard and picking the highest *per-unit* price
    // among the survivors that clears the incumbent by a material margin (not the highest total
    // — that would systematically pick the largest feasible quantity, i.e. the worst per-unit
    // survivor, defeating the guard; and not a bare per-unit maximum either — that would let a
    // fractional-cent "improvement" justify a much larger order). The floor itself is always the
    // first candidate evaluated, so the result can only match or beat it on the metric the guard
    // actually cares about. The climb no longer stops at the first infeasible reading (see
    // QuantitySearch) — it's bounded instead by an exact price ceiling that tightens once an
    // incumbent exists, plus a hard candidate-count governor as a backstop.

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

        var product = r.Product;

        var rounded = QuantityMath.RoundUpToMultiple(r.Quantity, Settings.RoundingMultiple);
        if (rounded > QuantityMath.QuantityCap)
            MelonLogger.Warning($"AAD: engine — rounded qty {rounded} exceeds cap {QuantityMath.QuantityCap}; clamping.");
        var effectiveQty = QuantityMath.Clamp(rounded, QuantityMath.QuantityCap);

        var ctx = ProbabilityContext.Capture(r.Customer, product, r.Quantity, r.Payment);
        if (ctx == null)
            return false; // CouldNotEvaluate — missing customer/relation data

        // SpendingLimit is a heuristic reimplementation of the game's real (unknown) limit, and it
        // can be too generous — the bisection below then finds a price it believes is a guaranteed
        // accept, but the real game rejects it outright. Stay clear of the modeled edge by a
        // configurable margin instead of searching right up to it.
        float safeLimit = ctx.SpendingLimit * (Settings.SpendingLimitSafetyMarginPercent / 100f);
        int ceiling = (int)Math.Floor(safeLimit) - 1;
        int floorInt = (int)Math.Ceiling(r.Payment);
        if (ceiling <= floorInt)
            return false; // CouldNotEvaluate — degenerate search range

        // Guards against sending a technically-100%-probability counter that isn't worth making.
        // Compared per-unit rather than as a total-dollar bump — otherwise a rounding-inflated
        // quantity can clear a total-price floor while the per-unit price actually drops (e.g.
        // 90 for 2 units -> 105 for 5 units is +17% total but -53% per unit). Each climbed
        // candidate is filtered by this floor, and the search itself also maximizes per-unit
        // price among the survivors (see QuantitySearch), so the result can only match or beat
        // what the single-floor search used to produce.
        float origUnitPrice = r.Payment / Math.Max(1, r.Quantity);
        float minUnitPrice = origUnitPrice * (1f + Settings.MinProfitPercent / 100f);

        var result = QuantitySearch.FindBest(
            effectiveQty, Settings.RoundingMultiple, QuantityMath.QuantityCap, minUnitPrice, ceiling,
            qty => BisectBestPrice(ctx, product, qty, floorInt, ceiling) is int best ? (float?)best : null);

        if (result.Trace.Count == 0)
        {
            MelonLogger.Warning($"AAD: engine — quantity search produced no candidates for {product.ID}; skipping.");
            return false; // CouldNotEvaluate
        }

        LogTrace(r, product, Settings.RoundingMultiple, minUnitPrice, result);

        var floorCandidate = result.Trace[0];

        if (!result.Found)
        {
            // "We couldn't evaluate this" means no candidate produced a price at all — not just
            // the floor. Now that Infeasible is skip-and-continue rather than a hard stop, a trace
            // like [10 -> Infeasible, 15 -> $420 BelowMinProfit] is reachable, and classifying it
            // off Trace[0] alone would misreport a genuine pricing judgment (the model found
            // accept prices and the deal was simply unprofitable) as a data/range failure — which
            // also routes it to the non-punitive CouldNotEvaluate path, leaving the deal silently
            // unanswered forever instead of correctly declining it.
            if (result.Trace.All(c => c.Outcome == QuantitySearch.CandidateOutcome.Infeasible))
            {
                MelonLogger.Warning(
                    $"AAD: engine — probability formula found no 100%-accept price at any evaluated quantity " +
                    $"(qty {result.Trace[0].Quantity}..{result.Trace[^1].Quantity}) for {product.ID}; leaving deal unanswered.");
                return false; // CouldNotEvaluate — nothing in the climb produced a price
            }

            // Not all-Infeasible, and no Feasible candidate (Found is false) together guarantee at
            // least one BelowMinProfit candidate exists, so BestBelowMinProfit is non-null here.
            // Report whichever cleared the highest per-unit price, not just the floor's — a later
            // candidate can beat it.
            var best = result.BestBelowMinProfit!.Value;
            var name = r.Customer.NPC?.FullName ?? "<unknown>";
            MelonLogger.Msg(
                $"AAD: {name} — best counter for {r.ProductId}×{best.Quantity} only reaches {best.UnitPrice:F2}/unit " +
                $"(need ≥{minUnitPrice:F2}/unit for {Settings.MinProfitPercent:F0}% min profit over {origUnitPrice:F2}/unit); declining, no counter sent.");
            reason = CounterOfferFailureReason.NoProfitableCounterFound;
            return false;
        }

        float unit = result.TotalPrice / Math.Max(1, result.Quantity);
        proposal = new CounterProposal(product, result.Quantity, unit, result.TotalPrice, "probability", r.Quantity);
        return true;
    }

    // Binary-searches for the highest integer total-dollar price where Probability == 1.0f at a
    // fixed qty. ~17 iterations over [floorInt, ceiling]. Returns null when no such price exists.
    // Owns ctx.Qty for the duration of the search — the caller must not also assign it, since vp2
    // (computed from the qty parameter) and ctx.Probability (which reads ctx.Qty) would otherwise
    // have two independent, easily-desynced sources of truth for the same quantity.
    private static int? BisectBestPrice(
        ProbabilityContext ctx, ProductDefinition product, int qty, int floorInt, int ceiling)
    {
        ctx.Qty = qty;
        int lo = floorInt, hi = ceiling, best = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            float vp2 = Customer.GetValueProposition(product, mid / (float)qty);
            float p = ctx.Probability(mid, vp2);
            if (p >= 1.0f) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return best >= 0 ? best : (int?)null;
    }

    // Verbose per-candidate trace, deliberately louder than the mod's steady-state logging so the
    // climb can be validated against real deals — kept unconditional (not gated behind its own
    // setting) per issue #10's ask for this to be the feature's primary debugging tool. Only
    // reachable when RoundingMultiple > 0, which is not the shipped default (Settings.ApplyDefaults
    // sets it to 0), so this costs nothing out of the box. Typically far fewer than
    // QuantitySearch.MaxCandidates lines per deal in practice, since the price-ceiling bound
    // tightens as soon as an incumbent is found (see QuantitySearch) — MaxCandidates + 2 is the
    // worst case (only reachable for a degenerate minUnitPrice <= 0), not the ordinary one.
    private static void LogTrace(
        DealRequest r, ProductDefinition product, int multiple, float minUnitPrice, QuantitySearch.Result result)
    {
        if (multiple <= 0 || result.Trace.Count == 0) return;

        var name = r.Customer.NPC?.FullName ?? "<unknown>";
        var floorCandidate = result.Trace[0];
        MelonLogger.Msg(
            $"AAD: {name} — qty search for {product.ID}: origQty={r.Quantity}, floor={floorCandidate.Quantity}, " +
            $"step={multiple}, cap={QuantityMath.QuantityCap}, need ≥{minUnitPrice:F2}/unit");

        foreach (var c in result.Trace)
        {
            if (c.Outcome == QuantitySearch.CandidateOutcome.Infeasible)
            {
                MelonLogger.Msg($"AAD:   qty {c.Quantity} → no 100%-accept price, skipped");
                continue;
            }

            string marker = c.Outcome == QuantitySearch.CandidateOutcome.BelowMinProfit
                ? "  below min profit, skipped"
                : result.Found && c.Quantity == result.Quantity ? "  ← best" : "";
            MelonLogger.Msg($"AAD:   qty {c.Quantity} → ${c.TotalPrice:F0} ({c.UnitPrice:F2}/unit){marker}");
        }

        if (result.Truncated)
        {
            MelonLogger.Warning(
                $"AAD: {name} — qty search hit the internal candidate limit ({QuantitySearch.MaxCandidates}) " +
                "before the price range was exhausted; the result above may not be the true best.");
        }

        if (result.Found)
        {
            // The old single-floor code would have sent floorCandidate outright only if it was
            // Feasible. If it was BelowMinProfit, the old code would have declined (baseline $0).
            // If it was Infeasible, the old code left the deal unanswered — not a decline at all,
            // and now a reachable trace shape since Infeasible no longer halts the climb. Only
            // quote a "+$delta vs baseline" figure in the first case, so neither the below-min nor
            // the infeasible case misstates what the floor would actually have done.
            if (floorCandidate.Outcome == QuantitySearch.CandidateOutcome.Feasible)
            {
                MelonLogger.Msg(
                    $"AAD: {name} — qty search chose {result.Quantity} @ ${result.TotalPrice:F0} vs baseline " +
                    $"{floorCandidate.Quantity} @ ${floorCandidate.TotalPrice:F0} (+${result.TotalPrice - floorCandidate.TotalPrice:F0})");
            }
            else
            {
                var baselineOutcome = floorCandidate.Outcome == QuantitySearch.CandidateOutcome.Infeasible
                    ? "no 100%-accept price"
                    : "below min profit";
                MelonLogger.Msg(
                    $"AAD: {name} — qty search chose {result.Quantity} @ ${result.TotalPrice:F0}; baseline " +
                    $"{floorCandidate.Quantity} would not have been counterable ({baselineOutcome})");
            }
        }
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
            Customer customer, ProductDefinition product, int origQty, float origPayment)
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
                    SpendingLimit        = weeklySpend / dayCount * 3f,
                    ValueProposition0    = vp0,
                    ProductEnjoyment     = enjoyment,
                    OriginalQuantity     = origQty,
                    MaxAddictionRelation = maxAddRel,
                    Qty                  = origQty, // reassigned per-candidate by BisectBestPrice before use
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
