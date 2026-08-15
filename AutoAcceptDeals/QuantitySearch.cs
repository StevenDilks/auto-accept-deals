using System;
using System.Collections.Generic;

namespace AutoAcceptDeals;

// Pure quantity-climb search: given a fixed price-search function, walks qty = startQty,
// startQty+multiple, startQty+2*multiple, ... tracking the candidate with the highest *total*
// price among those that clear the caller's optional min-profit-per-unit floor. The objective is
// total revenue, not per-unit price — the goal is the highest-revenue counter that still clears
// 100% acceptance and the multiple/min-profit constraints, full stop. A larger quantity at a
// lower per-unit price is the intended outcome when it clears more total revenue, not a defect to
// filter out: minUnitPrice is a floor candidates must clear to be considered at all, not a target
// to maximize once cleared. Ties (equal TotalPrice) keep the lower quantity, via strict `>` when
// updating the running best — the only tie-break the goal doesn't dictate and the only one worth
// having (no reason to send a larger order for the same money).
//
// Feasibility is not assumed monotone in qty: ProbabilityFormula.Compute is driven by a
// qty-ratio term that decays away from origQty *and* a value-proposition term that generally
// rises with qty at a fixed price, pulling in opposite directions. So an Infeasible reading does
// not stop the climb — it is skipped, exactly like BelowMinProfit — and a later, larger qty can
// still be feasible even if an earlier one wasn't.
//
// Total price is not assumed monotone in qty either — it can dip before climbing again (a
// mid-climb qty can be a genuinely worse deal than both its neighbors) — so the climb can't stop
// at the first decrease; it has to walk the whole legal range and keep the running max.
//
// The loop is bounded by two exact conditions, checked after each candidate:
//   (a) Once a Feasible candidate exists, if its TotalPrice already reached priceCeiling, no
//       future candidate can ever beat it on total (evaluate can never return more than
//       priceCeiling), so there's no point continuing.
//   (b) If minUnitPrice*next > priceCeiling, then for every q >= next, total(q) <= priceCeiling <
//       minUnitPrice*next <= minUnitPrice*q, so unit(q) < minUnitPrice — no future candidate can
//       ever be Feasible. This one applies regardless of whether a Feasible candidate has already
//       been found; it's a statement about the filter, not the objective, so finding a winner
//       doesn't change it. Only meaningful when the filter is on (minUnitPrice > 0).
// A hard MaxCandidates governor backstops both: QuantityMath.QuantityCap already bounds the climb
// structurally (a qty can never exceed it), but with a fine-grained multiple and a loose or
// disabled min-profit filter, that alone can still mean hundreds of synchronous evaluations inside
// a Harmony postfix. Result.Truncated reports when the governor, not a natural bound, is what
// stopped the climb — the winner is then only a lower bound on the true best.
//
// No MelonLoader / Il2Cpp references so it can be linked into the test project without the game
// installed.
internal static class QuantitySearch
{
    // Safety governor, not a feature-level search bound — bounds (a)/(b) above are the real
    // bounds. This is what actually stops a climb where both stay loose for a long stretch (a
    // low or disabled minUnitPrice, with no Feasible candidate yet reaching priceCeiling).
    // Result.Truncated reports when it did.
    internal const int MaxCandidates = 256;

    internal enum CandidateOutcome { Feasible, BelowMinProfit, Infeasible }

    internal readonly record struct Candidate(int Quantity, float TotalPrice, CandidateOutcome Outcome)
    {
        internal float UnitPrice => TotalPrice / MathF.Max(1f, Quantity);
    }

    // UnitPrice is winner.UnitPrice — TotalPrice / Quantity of the actual selected candidate, not
    // a re-derivation. Exposed so callers (CounterProposal, decline messages) never need their own
    // copy of that formula; 0f when !Found.
    //
    // BestFeasible is the winner: the highest-TotalPrice Feasible candidate the climb evaluated
    // (the running argmax, and bound (a)'s anchor once it exists). Result.Quantity/TotalPrice are
    // copied from it directly — there is no separate selection step, unlike the margin-checked
    // version this replaced.
    //
    // BestBelowMinProfit is the highest-per-unit-price BelowMinProfit candidate the climb
    // actually evaluated (null if every evaluated candidate was Infeasible) — the number a decline
    // message should report (how close the search got to clearing minUnitPrice), which is a
    // per-unit question even though the search objective itself is total-based. It is the max over
    // *evaluated* candidates, full stop, not the max over the whole range, and Truncated == false
    // does not change that: bound (b) only ever proves no further candidate can be Feasible, never
    // that none can out-price this one on a per-unit basis.
    internal readonly record struct Result(
        bool Found, int Quantity, float TotalPrice, float UnitPrice, IReadOnlyList<Candidate> Trace,
        Candidate? BestFeasible, Candidate? BestBelowMinProfit, bool Truncated);

    // evaluate: qty -> best 100%-acceptance total price at that qty, or null if no such price exists.
    // priceCeiling: the highest total price `evaluate` could ever return, independent of qty.
    internal static Result FindBest(
        int startQty, int multiple, int cap, float minUnitPrice, float priceCeiling, Func<int, float?> evaluate)
    {
        var trace = new List<Candidate>();
        if (startQty <= 0 || cap <= 0 || startQty > cap)
            return new Result(false, 0, 0f, 0f, trace, null, null, false);

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
                    if (bestFeasible is null || candidate.TotalPrice > bestFeasible.Value.TotalPrice)
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
            if (rangeExhausted) break;

            // (a) no future candidate can ever exceed priceCeiling, so once the best-so-far is
            // already there, nothing can beat it on total.
            if (bestFeasible is { } best && best.TotalPrice >= priceCeiling) break;

            // (b) no future candidate can ever be Feasible (see the class comment); only meaningful
            // while the filter is on. Unconditional on whether a Feasible candidate exists yet —
            // this is a statement about the filter, not the objective.
            if (minUnitPrice > 0f && minUnitPrice * next > priceCeiling) break;

            if (trace.Count >= MaxCandidates) { truncated = true; break; }

            qty = next;
        }

        if (bestFeasible is null)
            return new Result(false, 0, 0f, 0f, trace, null, bestBelowMinProfit, truncated);

        var winner = bestFeasible.Value;
        return new Result(
            true, winner.Quantity, winner.TotalPrice, winner.UnitPrice, trace, bestFeasible,
            bestBelowMinProfit, truncated);
    }
}
