using System;
using System.Collections.Generic;

namespace AutoAcceptDeals;

// Pure quantity-climb search: given a fixed price-search function, walks qty = startQty,
// startQty+multiple, startQty+2*multiple, ... tracking the candidate with the highest per-unit
// price among those that clear the caller's min-profit-per-unit floor. That raw per-unit best
// only replaces the margin baseline — the floor (the first candidate evaluated, trace[0]) if
// it's Feasible, otherwise the first Feasible candidate the climb finds — if it beats the
// baseline's own per-unit price by a material margin (MinImprovementRatio), applied once at the
// end. The fallback matters: rounding moves qty away from origQty, which the acceptance model
// generally penalizes (see the monotonicity note below), so a BelowMinProfit floor with a later
// Feasible candidate is a common shape, not a corner case — without a baseline there too, a
// fractional-cent-per-unit "win" at a far larger quantity would go unchecked. Maximizing total
// price instead would systematically pick the largest feasible quantity, i.e. the *worst*
// per-unit survivor, and even bare per-unit maximization without a margin would let a $0.01/unit
// "win" justify a much larger order. The margin is deliberately measured against that fixed
// baseline, not against whichever candidate happens to be the running per-unit best mid-climb:
// comparing against a transient incumbent makes the outcome depend on the rounding step — a
// coarser grid can skip the candidate that would have raised the bar and so reach a later
// candidate a finer grid would reject (see
// FindBest_MarginAppliesAgainstFloorNotRunningIncumbent_IndependentOfStepSize in the test file).
// Unlike that incumbent, the baseline itself is fixed the moment it's found and never moves again
// for the rest of the climb.
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
// longer beat the threshold a future candidate would need to clear to change the outcome, every
// remaining candidate is provably unable to change the result, so there's no point evaluating
// further. That threshold is minUnitPrice before any Feasible candidate exists. Once one does,
// it's the raw per-unit best if that best already clears the margin baseline's margin (a higher
// argmax only needs to beat it to stay the argmax and the winner), or the baseline's own margined
// price (marginBaseline.UnitPrice * MinImprovementRatio) if it doesn't — a candidate has to
// actually clear the margin, not just the running best, to change the eventual winner away from
// the baseline (see the boundReached computation below).
//
// That threshold is a ratio (priceCeiling / threshold), not a fixed candidate count, so it can
// stay low relative to priceCeiling for a long stretch even with a Feasible candidate in hand: a
// low minUnitPrice — and so a low baseline and low threshold — leaves plenty of room before the
// bound closes on its own. A hard MaxCandidates governor backstops that: not just the case where
// no candidate has cleared the min-profit floor yet, but any climb whose threshold stays low
// relative to priceCeiling for a long stretch, feasible or not. Result.Truncated reports when
// that governor, not the price-ceiling bound, is what stopped the climb. Without any bound at
// all, a qty-insensitive `evaluate` turns this into ~1000 bisections synchronously inside a
// Harmony postfix.
//
// No MelonLoader / Il2Cpp references so it can be linked into the test project without the game
// installed.
internal static class QuantitySearch
{
    // Safety governor, not a feature-level search bound — the price-ceiling bound above is the
    // real bound, but it's a ratio (priceCeiling / threshold, see the class comment), not a fixed
    // count. It stays loose whenever that threshold is low relative to priceCeiling, which is
    // common with a low minUnitPrice — or a margin baseline not far above it — whether or not a
    // Feasible candidate has been found yet. This governor is what actually stops that climb;
    // Result.Truncated reports when it did.
    internal const int MaxCandidates = 50;

    // A later, larger-quantity candidate must exceed the *margin baseline's* per-unit price by
    // more than this relative margin to be worth the larger order — the check below is `<=`, so
    // an exact tie loses to the baseline. The baseline is the floor (the first candidate
    // evaluated, trace[0]) when it's Feasible, otherwise the first Feasible candidate the climb
    // finds — falling back matters because rounding moves qty away from origQty, which the
    // acceptance model generally penalizes, so a BelowMinProfit floor with a later Feasible
    // candidate is a common shape, not a corner case that can skip the margin entirely. Applied
    // once, after the climb, against that fixed baseline rather than incrementally against
    // whichever candidate happened to be the running per-unit best. Without a threshold, a
    // candidate a fraction of a cent better per unit could win and cost the player a much larger
    // order for an immaterial gain — the min-profit floor doesn't catch this, since both
    // candidates can clear it. 2% keeps genuinely better candidates while rejecting noise-level
    // "wins".
    internal const float MinImprovementRatio = 1.02f;

    internal enum CandidateOutcome { Feasible, BelowMinProfit, Infeasible }

    internal readonly record struct Candidate(int Quantity, float TotalPrice, CandidateOutcome Outcome)
    {
        internal float UnitPrice => TotalPrice / MathF.Max(1f, Quantity);
    }

    // UnitPrice is winner.UnitPrice — TotalPrice / Quantity of the actual selected candidate, not
    // a re-derivation. Exposed so callers (CounterProposal, decline messages) never need their own
    // copy of that formula; 0f when !Found.
    //
    // BestFeasible is the highest-per-unit-price Feasible candidate the climb evaluated (the
    // running argmax, and the price-ceiling bound's anchor once it exists) — the exact value the
    // end-of-climb margin check weighs against MarginBaseline. It can differ from the winning
    // Quantity/TotalPrice: if it doesn't clear the baseline's margin (MinImprovementRatio), the
    // baseline wins instead and this field still names the raw best that lost. Callers that need
    // to explain *why* a candidate wasn't picked (e.g. trace logging) should read this rather than
    // re-deriving it from Trace — a second, independently-written argmax loop can silently
    // disagree with this one if either changes its tie-break or filter.
    //
    // MarginBaseline is the candidate BestFeasible's margin was actually measured against: the
    // floor (trace[0]) when it's Feasible, otherwise the first Feasible candidate the climb finds
    // — see MinImprovementRatio. Non-null exactly when BestFeasible is (they're set together, the
    // moment the first Feasible candidate appears). Exposed for the same reason as BestFeasible:
    // so callers explaining the margin decision use the exact candidate it was checked against,
    // not a re-derivation that can name the wrong one once the floor isn't Feasible.
    //
    // BestBelowMinProfit is the highest-per-unit-price BelowMinProfit candidate the climb
    // actually evaluated (null if every evaluated candidate was Infeasible) — the number a
    // decline message should report. It is the max over *evaluated* candidates, full stop, not
    // the max over the whole range, and Truncated == false does not change that: the pre-Feasible
    // price-ceiling bound (the only one in play here — CounterOfferEngine only reads this field on
    // the !Found path, which means bestFeasible stayed null for the entire climb, so the
    // once-Feasible-found tightening never ran) proves only that no further candidate can be
    // Feasible; it says nothing about whether a later, still-BelowMinProfit candidate could out-
    // price this one. A natural bound close (Truncated == false) is not a guarantee the range was
    // fully walked for this purpose — only "evaluated qty {first}..{last}", which decline messages
    // report alongside this value, describes what was actually checked.
    internal readonly record struct Result(
        bool Found, int Quantity, float TotalPrice, float UnitPrice, IReadOnlyList<Candidate> Trace,
        Candidate? BestFeasible, Candidate? MarginBaseline, Candidate? BestBelowMinProfit, bool Truncated);

    // evaluate: qty -> best 100%-acceptance total price at that qty, or null if no such price exists.
    // priceCeiling: the highest total price `evaluate` could ever return, independent of qty.
    internal static Result FindBest(
        int startQty, int multiple, int cap, float minUnitPrice, float priceCeiling, Func<int, float?> evaluate)
    {
        var trace = new List<Candidate>();
        if (startQty <= 0 || cap <= 0 || startQty > cap)
            return new Result(false, 0, 0f, 0f, trace, null, null, null, false);

        Candidate? bestFeasible = null;
        // The first Feasible candidate seen, and only ever that one — never reassigned once set.
        // Always non-null exactly when bestFeasible is (both are set, once, on the same
        // candidate: the first Feasible one), which the return statements below and the
        // `bestFeasible is { }` branch rely on.
        Candidate? marginBaseline = null;
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
                    marginBaseline ??= candidate;
                }
                else if (bestBelowMinProfit is null || candidate.UnitPrice > bestBelowMinProfit.Value.UnitPrice)
                {
                    bestBelowMinProfit = candidate;
                }
            }

            if (multiple <= 0) break; // roundingMultiple = 0 -> evaluate startQty only, no-op path

            int next = qty + multiple;
            bool rangeExhausted = next <= qty || next > cap; // overflow guard, or cap reached

            // A future candidate can only change the outcome by clearing the threshold that
            // determines the current winner. Before any Feasible candidate exists, that's
            // minUnitPrice. Once one does: if the margin baseline is still the eventual winner
            // (bestFeasible hasn't cleared its margin), a future candidate has to clear that
            // margin — marginBaseline.UnitPrice * MinImprovementRatio — to change the winner away
            // from the baseline, not just beat bestFeasible.UnitPrice; beating bestFeasible
            // without clearing the margin would still lose to the baseline at the end. If
            // bestFeasible already clears the margin, only beating bestFeasible.UnitPrice matters,
            // since any higher argmax than an already-clearing value is provably still winning at
            // the end. A tie can't replace the current best under the strict `>` above, so `>=` is
            // safe to break on; before any Feasible candidate exists, `minUnitPrice * qty ==
            // priceCeiling` would still be Feasible (the classification above uses `>=`), so that
            // branch needs `>`.
            bool boundReached;
            if (rangeExhausted)
            {
                boundReached = false;
            }
            else if (bestFeasible is { } best)
            {
                float needed = MathF.Max(best.UnitPrice, marginBaseline!.Value.UnitPrice * MinImprovementRatio);
                boundReached = needed * next >= priceCeiling;
            }
            else
            {
                boundReached = minUnitPrice > 0f && minUnitPrice * next > priceCeiling;
            }

            if (rangeExhausted || boundReached) break;

            if (trace.Count >= MaxCandidates) { truncated = true; break; }

            qty = next;
        }

        if (bestFeasible is null)
            return new Result(false, 0, 0f, 0f, trace, null, null, bestBelowMinProfit, truncated);

        var baseline = marginBaseline!.Value;
        var winner = bestFeasible.Value;
        if (baseline.Quantity != winner.Quantity && winner.UnitPrice <= baseline.UnitPrice * MinImprovementRatio)
        {
            winner = baseline;
        }

        return new Result(
            true, winner.Quantity, winner.TotalPrice, winner.UnitPrice, trace, bestFeasible, marginBaseline,
            bestBelowMinProfit, truncated);
    }
}
