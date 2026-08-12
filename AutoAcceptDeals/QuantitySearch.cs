using System;
using System.Collections.Generic;

namespace AutoAcceptDeals;

// Pure quantity-climb search: given a fixed price-search function, walks qty = startQty,
// startQty+multiple, startQty+2*multiple, ... tracking the candidate with the highest per-unit
// price among those that clear the caller's min-profit-per-unit floor. That raw per-unit best
// only replaces the floor (the first candidate evaluated) if it beats the floor's own per-unit
// price by a material margin (MinImprovementRatio), applied once at the end — maximizing total
// price instead would systematically pick the largest feasible quantity, i.e. the *worst*
// per-unit survivor, and even bare per-unit maximization without a margin would let a $0.01/unit
// "win" justify a much larger order. The margin is deliberately measured against the floor, not
// against whichever candidate happens to be the running per-unit best mid-climb: comparing
// against a transient incumbent makes the outcome depend on the rounding step — a coarser grid
// can skip the candidate that would have raised the bar and so reach a later candidate a finer
// grid would reject (see FindBest_MarginAppliesAgainstFloorNotRunningIncumbent_IndependentOfStepSize
// in the test file).
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
// longer beat the raw per-unit price a future candidate would need to change the outcome (the
// min-profit floor before any Feasible candidate exists; the best Feasible per-unit price seen so
// far once one does — the *raw* value, not the margined one, since a future candidate only needs
// to beat the current best to become the new argmax, and the margin itself is resolved once at
// the end against the floor, not here), every remaining candidate is provably unable to change
// the result, so there's no point evaluating further. A hard MaxCandidates governor backstops the
// one case this bound can't tighten on its own: a climb where no candidate has cleared the
// min-profit floor yet, so the bound stays anchored to the looser minUnitPrice for the whole
// stretch — a small `multiple` and a `priceCeiling` well above `minUnitPrice` can walk tens of
// below-floor candidates before that bound closes. Result.Truncated reports when that governor,
// not the price-ceiling bound, is what stopped the climb. Without any bound at all, a
// qty-insensitive `evaluate` turns this into ~1000 bisections synchronously inside a Harmony
// postfix.
//
// No MelonLoader / Il2Cpp references so it can be linked into the test project without the game
// installed.
internal static class QuantitySearch
{
    // Safety governor, not a feature-level search bound — the price-ceiling bound above is the
    // real bound. It only backstops the climb while no Feasible candidate has appeared yet (the
    // bound stays anchored to the un-tightened minUnitPrice for that whole stretch); once one
    // has, the bound tightens to that candidate's own per-unit price and this limit stops
    // mattering for any ordinary minUnitPrice > 0.
    internal const int MaxCandidates = 50;

    // A later, larger-quantity candidate must beat the *floor's* per-unit price (the first
    // candidate evaluated, trace[0]) by at least this relative margin to be worth the larger
    // order — applied once, after the climb, against that fixed floor rather than incrementally
    // against whichever candidate happened to be the running per-unit best. Without a threshold,
    // a candidate a fraction of a cent better per unit could win and cost the player a much
    // larger order for an immaterial gain — the min-profit floor doesn't catch this, since both
    // candidates can clear it. 2% keeps genuinely better candidates while rejecting noise-level
    // "wins".
    internal const float MinImprovementRatio = 1.02f;

    internal enum CandidateOutcome { Feasible, BelowMinProfit, Infeasible }

    internal readonly record struct Candidate(int Quantity, float TotalPrice, CandidateOutcome Outcome)
    {
        internal float UnitPrice => TotalPrice / MathF.Max(1f, Quantity);
    }

    // BestBelowMinProfit is the highest-per-unit-price BelowMinProfit candidate the climb
    // actually evaluated (null if every evaluated candidate was Infeasible) — the number a
    // decline message should report. It is not necessarily the highest reachable at any
    // quantity: the price-ceiling bound tightens once a Feasible candidate is found and can stop
    // the climb before a higher-priced-but-still-below-floor candidate would have been reached.
    // While no Feasible candidate exists yet, though, the bound stays anchored to minUnitPrice
    // and every BelowMinProfit candidate in range is still evaluated. Truncated is true only
    // when MaxCandidates, not the price-ceiling bound, is what stopped the climb — see the class
    // comment.
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

        Candidate? bestFeasible = null;
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
                    if (bestFeasible is null || candidate.UnitPrice > bestFeasible.Value.UnitPrice)
                        bestFeasible = candidate;
                }
                else if (bestBelowMinProfit is null || candidate.UnitPrice > bestBelowMinProfit.Value.UnitPrice)
                {
                    bestBelowMinProfit = candidate;
                }
            }

            if (multiple <= 0) break; // roundingMultiple = 0 -> evaluate startQty only, no-op path

            int next = qty + multiple;
            bool rangeExhausted = next <= qty || next > cap; // overflow guard, or cap reached

            // A future candidate can only change the outcome by beating the current raw per-unit
            // best (to become the new argmax) — the 2% margin is resolved once at the end
            // against the floor, not here, so it plays no part in this bound. A tie can't
            // replace the current best under the strict `>` above, so `>=` is safe to break on;
            // before any Feasible candidate exists, `minUnitPrice * qty == priceCeiling` would
            // still be Feasible (the classification above uses `>=`), so that branch needs `>`.
            bool boundReached = !rangeExhausted && (bestFeasible is { } best
                ? best.UnitPrice * next >= priceCeiling
                : minUnitPrice > 0f && minUnitPrice * next > priceCeiling);

            if (rangeExhausted || boundReached) break;

            if (trace.Count >= MaxCandidates) { truncated = true; break; }

            qty = next;
        }

        if (bestFeasible is null)
            return new Result(false, 0, 0f, trace, bestBelowMinProfit, truncated);

        var floorCandidate = trace[0];
        var winner = bestFeasible.Value;
        if (floorCandidate.Outcome == CandidateOutcome.Feasible
            && floorCandidate.Quantity != winner.Quantity
            && winner.UnitPrice <= floorCandidate.UnitPrice * MinImprovementRatio)
        {
            winner = floorCandidate;
        }

        return new Result(true, winner.Quantity, winner.TotalPrice, trace, bestBelowMinProfit, truncated);
    }
}
