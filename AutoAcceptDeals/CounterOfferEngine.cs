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
    // through the existing min-profit-per-unit guard and picking the highest *total* price among
    // the survivors — that is the stated goal (highest revenue at 100% acceptance, subject to the
    // multiple and min-profit constraints; number of multiples climbed and per-unit price are not
    // factors in their own right). The climb doesn't stop at the first infeasible reading, nor at
    // the first total-price decrease (see QuantitySearch) — it's bounded instead by an exact price
    // ceiling, a min-profit-unreachable bound, and a hard candidate-count governor as a backstop.
    //
    // An earlier version of this feature maximized per-unit price with a margin against a fixed
    // baseline instead, on the reasoning that a pure total-price objective would always pick the
    // largest feasible quantity — the worst per-unit survivor. That reasoning was wrong: it's true
    // in isolation, but not the goal. A larger, lower-per-unit-price order that still clears both
    // 100% acceptance and minUnitPrice is exactly the outcome this feature exists to find; the
    // per-unit objective was measured against real deal data and left revenue on the table on
    // every deal where it applied (PR #14 review, round 7) by stopping the climb the moment no
    // future candidate could beat the current best on a *per-unit* basis, even though a lower-
    // unit-price, higher-total candidate further up the climb — already being evaluated — would
    // have won under the actual goal.

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

        // Guards against sending a technically-100%-probability counter that isn't worth making —
        // a floor on per-unit price, not on total, since the search objective itself is total
        // price (see QuantitySearch): without a per-unit floor a large enough quantity could clear
        // a higher total while actually being a worse deal per unit than the original ask. Each
        // climbed candidate is filtered by this floor before the search's total-price argmax ever
        // sees it, so a winner can only be a candidate that both clears 100% acceptance and this
        // per-unit minimum.
        float origUnitPrice = r.Payment / Math.Max(1, r.Quantity);
        float minUnitPrice = origUnitPrice * (1f + Settings.MinProfitPercent / 100f);

        // Every qty at or past the dead zone is Infeasible at every price when
        // DeadZoneAlwaysRejects holds for this deal, so there is nothing up there worth walking —
        // bound the climb instead of discovering the boundary one candidate at a time. Math.Max
        // keeps the rounding floor itself evaluated even when it's already past the dead zone
        // (small origQty + coarse multiple), so an all-Infeasible trace still reads as one
        // (PR #14 review, round 9 — this replaces the round-8 per-candidate BisectBestPrice-only
        // shortcut, which pruned the interop cost of each dead-zone candidate but still let the
        // climb walk, trace, and log every one of them up to MaxCandidates).
        int searchCap = QuantityMath.QuantityCap;
        if (ProbabilityFormula.DeadZoneAlwaysRejects(ctx.ValueProposition0, ctx.ProductEnjoyment, ctx.MaxAddictionRelation))
            searchCap = Math.Max(effectiveQty, ProbabilityFormula.FirstDeadZoneQty(r.Quantity, QuantityMath.QuantityCap) - 1);

        var result = QuantitySearch.FindBest(
            effectiveQty, Settings.RoundingMultiple, searchCap, minUnitPrice, ceiling,
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
                    $"(qty {floorCandidate.Quantity}..{result.Trace[^1].Quantity}) for {product.ID}; leaving deal unanswered.");
                return false; // CouldNotEvaluate — nothing in the climb produced a price
            }

            // A truncated search stopped at MaxCandidates before the price-ceiling bound closed
            // on its own, so an unevaluated remainder past it still exists and isn't provably
            // BelowMinProfit — some of it could have been Feasible. That only leaves the verdict
            // unresolved when the floor itself never produced a usable price: the floor is always
            // Trace[0], always evaluated first and in full regardless of what the governor did up
            // to MaxCandidates-1 steps later, so a BelowMinProfit floor is a complete, sound verdict
            // on its own — the exact one the pre-search single-floor code on main would have
            // declined on. Routing that case to CouldNotEvaluate would turn a fully-evaluated
            // decline into a deal that sits unanswered forever (truncation is deterministic for a
            // given deal, so it never resolves on retry); the un-walked remainder is only a missed
            // *upside* (a possibly-better candidate further out). Only an Infeasible floor means no
            // candidate the pre-search code would ever have sent was evaluated at all — that's the
            // case this guards. A Found result that's also Truncated is left alone here regardless:
            // it did find a genuine Feasible candidate clearing the min-profit floor, just possibly
            // not the single best one, which is a quality gap the Truncated warning below already
            // surfaces, not a reason to withhold an otherwise-valid counter.
            //
            // searchCap now stops the climb before it ever enters a provably-always-Infeasible dead
            // zone (see TryPropose above), so a Truncated result here means the governor actually
            // fired inside the live range — no dead-zone-soundness case to carve out anymore
            // (PR #14 review, round 9; round 8's truncationIsSound existed only because the climb
            // was still allowed to walk into that region).
            if (result.Truncated && floorCandidate.Outcome == QuantitySearch.CandidateOutcome.Infeasible)
            {
                MelonLogger.Warning(
                    $"AAD: engine — quantity search for {product.ID} hit the candidate limit before the " +
                    $"price range was exhausted (evaluated qty {floorCandidate.Quantity}..{result.Trace[^1].Quantity}); " +
                    "leaving deal unanswered rather than declining on an incomplete search.");
                return false; // CouldNotEvaluate — the climb didn't finish, so this isn't a confirmed verdict
            }

            // Not all-Infeasible, and no Feasible candidate (Found is false) together guarantee at
            // least one BelowMinProfit candidate exists, so BestBelowMinProfit is non-null here.
            // Report whichever cleared the highest per-unit price among the ones actually
            // evaluated, not just the floor's — a later candidate can beat it. This is not
            // necessarily the best reachable at any quantity, Truncated or not: the price-ceiling
            // bound only ever proves no further candidate can be Feasible, never that none can
            // out-price this one (see QuantitySearch.Result.BestBelowMinProfit), so the message
            // says "evaluated".
            var best = result.BestBelowMinProfit!.Value;
            var name = r.Customer.NPC?.FullName ?? "<unknown>";
            MelonLogger.Msg(
                $"AAD: {name} — best counter for {r.ProductId}×{best.Quantity} only reaches {best.UnitPrice:F2}/unit " +
                $"(evaluated qty {floorCandidate.Quantity}..{result.Trace[^1].Quantity}; need ≥{minUnitPrice:F2}/unit " +
                $"for {Settings.MinProfitPercent:F0}% min profit over {origUnitPrice:F2}/unit); declining, no counter sent.");
            reason = CounterOfferFailureReason.NoProfitableCounterFound;
            return false;
        }

        proposal = new CounterProposal(product, result.Quantity, result.UnitPrice, result.TotalPrice, "probability", r.Quantity);
        return true;
    }

    // Binary-searches for the highest integer total-dollar price where Probability == 1.0f at a
    // fixed qty. ~17 iterations over [floorInt, ceiling]. Returns null when no such price exists.
    // Owns ctx.Qty for the duration of the search — the caller must not also assign it, since vp2
    // (computed from the qty parameter) and ctx.Probability (which reads ctx.Qty) would otherwise
    // have two independent, easily-desynced sources of truth for the same quantity.
    //
    // Probes floorInt first: p == 1 is monotone non-decreasing in vp2 at fixed qty (every gate
    // and the final formula in ProbabilityFormula.Compute either ignores vp2 or moves toward
    // acceptance as vp2 rises), and vp2 is decreasing in unit price, so floorInt — the cheapest
    // price in range — is where vp2, and so p, is highest. If p < 1 there, no price in [floorInt,
    // ceiling] can reach p == 1, so the full bisection is skipped. This matters most for
    // Infeasible candidates the quantity climb evaluates and discards (PR #14 review, round 7):
    // it turns each one from ~17 interop calls into 1.
    //
    // Before even that: once qty is past ProbabilityFormula.IsDeadZoneQty, acceptance can stop
    // depending on vp2 (and so on price) altogether — ProbabilityFormula.DeadZoneAlwaysRejects
    // checks whether it has, using only per-deal constants captured in ctx, none of which are
    // qty-dependent. When it has, this candidate is Infeasible at every price, so even the single
    // floorInt probe above is skipped: 0 interop calls instead of 1 (PR #14 review, round 8). Since
    // round 9, TryPropose's searchCap keeps the climb from reaching such a qty at all in the common
    // case, so in practice this only fires for the one candidate searchCap's Math.Max deliberately
    // still evaluates — the rounding floor itself, when it's already past the dead zone. Kept as
    // its own check (not folded into the caller) because that candidate still needs to come back
    // Infeasible, not just unwalked.
    private static int? BisectBestPrice(
        ProbabilityContext ctx, ProductDefinition product, int qty, int floorInt, int ceiling)
    {
        ctx.Qty = qty;

        if (ProbabilityFormula.IsDeadZoneQty(qty, ctx.OriginalQuantity)
            && ProbabilityFormula.DeadZoneAlwaysRejects(ctx.ValueProposition0, ctx.ProductEnjoyment, ctx.MaxAddictionRelation))
            return null;

        float floorVp2 = Customer.GetValueProposition(product, floorInt / (float)qty);
        if (ctx.Probability(floorInt, floorVp2) < 1.0f) return null;

        int lo = floorInt + 1, hi = ceiling, best = floorInt;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            float vp2 = Customer.GetValueProposition(product, mid / (float)qty);
            float p = ctx.Probability(mid, vp2);
            if (p >= 1.0f) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return best;
    }

    // Verbose per-candidate trace, deliberately louder than the mod's steady-state logging so the
    // climb can be validated against real deals — kept unconditional (not gated behind its own
    // setting) per issue #10's ask for this to be the feature's primary debugging tool. Only
    // reachable when RoundingMultiple > 0, which is not the shipped default (Settings.ApplyDefaults
    // sets it to 0), so this costs nothing out of the box. This emits 1 header line + Trace.Count
    // candidate lines, plus 1 Truncated warning when the governor fired and 1 baseline/summary
    // line when Found — those last two aren't exclusive (FindBest_EvaluateNeverReturnsNull_
    // BoundedByMaxCandidates pins Found && Truncated both true), so MaxCandidates + 3 lines, not
    // + 2, is the absolute worst case.
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

            string marker;
            if (c.Outcome == QuantitySearch.CandidateOutcome.BelowMinProfit)
            {
                marker = "  below min profit, skipped";
            }
            else if (result.Found && c.Quantity == result.Quantity)
            {
                marker = "  ← best";
            }
            else
            {
                marker = "";
            }
            MelonLogger.Msg($"AAD:   qty {c.Quantity} → ${c.TotalPrice:F0} ({c.UnitPrice:F2}/unit){marker}");
        }

        if (result.Truncated)
        {
            MelonLogger.Warning(
                $"AAD: {name} — qty search hit the internal candidate limit ({QuantitySearch.MaxCandidates}) " +
                "before the price range was exhausted; the result above is only a lower bound on the true " +
                "best, not necessarily the best itself.");
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
