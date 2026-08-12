using System;
using System.Collections.Generic;

namespace AutoAcceptDeals;

// Pure quantity-climb search: given a fixed price-search function, walks qty = startQty,
// startQty+multiple, startQty+2*multiple, ... looking for the candidate with the highest
// per-unit price that still clears the caller's min-profit-per-unit floor, and beats the current
// incumbent by a material margin (MinImprovementRatio) — maximizing total price instead would
// systematically pick the largest feasible quantity, i.e. the *worst* per-unit survivor, and even
// bare per-unit maximization without a margin would let a $0.01/unit "win" justify a much larger
// order. See the selection logic below.
//
// Feasibility is not assumed monotone in qty: ProbabilityFormula.Compute is driven by a
// qty-ratio term that decays away from origQty *and* a value-proposition term that generally
// rises with qty at a fixed price, pulling in opposite directions. So an Infeasible reading does
// not stop the climb — it is skipped, exactly like BelowMinProfit — and a later, larger qty can
// still be feasible even if an earlier one wasn't.
//
// Because nothing here stops on infeasibility, the loop is bounded explicitly instead: an exact,
// cheap upper bound derived from the caller's own price ceiling. `evaluate` can never return more
// than priceCeiling, so unit(q) = total(q)/q can never exceed priceCeiling/q — once that can no
// longer beat what a candidate needs to win (the min-profit floor before any incumbent exists;
// the incumbent's required margin once one does), every remaining candidate is provably unable to
// win, so there's no point evaluating further. A hard MaxCandidates governor backstops the rare
// case this bound can't do its job (minUnitPrice <= 0, e.g. a zero-payment deal) — Result.Truncated
// reports when that governor, not the price ceiling, is what stopped the climb. Without any bound,
// a qty-insensitive `evaluate` turns this into ~1000 bisections synchronously inside a Harmony
// postfix.
//
// No MelonLoader / Il2Cpp references so it can be linked into the test project without the game
// installed.
internal static class QuantitySearch
{
    // Safety governor, not a feature-level search bound — the price-ceiling bound above is the
    // real bound and, for any deal with minUnitPrice > 0, terminates the climb well before this
    // limit (tightening further once an incumbent exists). This only fires in the degenerate case
    // where minUnitPrice <= 0 disables that bound entirely (e.g. a zero-payment deal).
    internal const int MaxCandidates = 50;

    // A later, larger-quantity candidate must beat the current incumbent's per-unit price by at
    // least this relative margin to displace it. Without a threshold, a candidate a fraction of a
    // cent better per unit could win under a bare `>` comparison and cost the player a much larger
    // order for an immaterial gain — the min-profit floor doesn't catch this, since both candidates
    // can clear it. 2% keeps genuinely better candidates while rejecting noise-level "wins".
    internal const float MinImprovementRatio = 1.02f;

    internal enum CandidateOutcome { Feasible, BelowMinProfit, Infeasible }

    internal readonly record struct Candidate(int Quantity, float TotalPrice, CandidateOutcome Outcome)
    {
        internal float UnitPrice => TotalPrice / MathF.Max(1f, Quantity);
    }

    // BestBelowMinProfit is the highest-per-unit-price candidate that missed the min-profit floor
    // (null if every evaluated candidate was Infeasible) — the number a decline message should
    // report, since a later candidate can clear a higher per-unit price than the floor while still
    // falling short. Truncated is true only when MaxCandidates, not the price ceiling, is what
    // stopped the climb — see the class comment.
    internal readonly record struct Result(
        bool Found, int Quantity, float TotalPrice, IReadOnlyList<Candidate> Trace,
        Candidate? BestBelowMinProfit, bool Truncated);

    // evaluate: qty -> best 100%-acceptance total price at that qty, or null if no such price exists.
    // priceCeiling: the highest total price `evaluate` could ever return, independent of qty.
    internal static Result FindBest(
        int startQty, int multiple, int cap, float minUnitPrice, float priceCeiling, Func<int, float?> evaluate)
    {
        var trace = new List<Candidate>();
        if (startQty <= 0 || cap <= 0 || startQty > cap)
            return new Result(false, 0, 0f, trace, null, false);

        bool found = false;
        int bestQty = 0;
        float bestTotal = 0f;
        float bestUnitPrice = 0f;
        Candidate? bestBelowMinProfit = null;
        bool truncated = false;

        int qty = startQty;
        while (true)
        {
            float? total = evaluate(qty);
            if (total is null)
            {
                trace.Add(new Candidate(qty, 0f, CandidateOutcome.Infeasible));
            }
            else
            {
                var candidate = new Candidate(qty, total.Value, total.Value >= minUnitPrice * qty
                    ? CandidateOutcome.Feasible
                    : CandidateOutcome.BelowMinProfit);
                trace.Add(candidate);

                if (candidate.Outcome == CandidateOutcome.Feasible)
                {
                    if (!found || candidate.UnitPrice > bestUnitPrice * MinImprovementRatio)
                    {
                        found = true;
                        bestQty = qty;
                        bestTotal = total.Value;
                        bestUnitPrice = candidate.UnitPrice;
                    }
                }
                else if (bestBelowMinProfit is null || candidate.UnitPrice > bestBelowMinProfit.Value.UnitPrice)
                {
                    bestBelowMinProfit = candidate;
                }
            }

            if (multiple <= 0) break; // roundingMultiple = 0 -> evaluate startQty only, no-op path
            if (trace.Count >= MaxCandidates) { truncated = true; break; }

            int next = qty + multiple;
            if (next <= qty) break; // overflow guard
            if (next > cap) break;

            // Once an incumbent exists, no candidate can win without beating it by
            // MinImprovementRatio, so the bound tightens to that requirement instead of the
            // (looser) min-profit floor. A tie can't win under the strict `>` above, so `>=` is
            // safe to break on; before any incumbent exists, `minUnitPrice * qty == priceCeiling`
            // would still be Feasible (the selection test uses `>=`), so that branch needs `>`.
            float requiredUnitPrice = found ? bestUnitPrice * MinImprovementRatio : minUnitPrice;
            bool boundReached = found
                ? requiredUnitPrice * next >= priceCeiling
                : requiredUnitPrice > 0f && requiredUnitPrice * next > priceCeiling;
            if (boundReached) break;

            qty = next;
        }

        return new Result(found, bestQty, bestTotal, trace, bestBelowMinProfit, truncated);
    }
}
