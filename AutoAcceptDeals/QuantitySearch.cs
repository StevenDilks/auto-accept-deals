using System;
using System.Collections.Generic;

namespace AutoAcceptDeals;

// Pure quantity-climb search: given a fixed price-search function, walks qty = startQty,
// startQty+multiple, startQty+2*multiple, ... looking for the candidate with the highest
// per-unit price that still clears the caller's min-profit-per-unit floor (maximizing total
// price instead would systematically pick the largest feasible quantity, i.e. the *worst*
// per-unit survivor — see the selection logic below).
//
// Feasibility is not assumed monotone in qty: ProbabilityFormula.Compute is driven by a
// qty-ratio term that decays away from origQty *and* a value-proposition term that generally
// rises with qty at a fixed price, pulling in opposite directions. So an Infeasible reading does
// not stop the climb — it is skipped, exactly like BelowMinProfit — and a later, larger qty can
// still be feasible even if an earlier one wasn't.
//
// Because nothing here stops on infeasibility, the loop is bounded explicitly instead: an exact,
// cheap upper bound derived from the caller's own price ceiling (once minUnitPrice * qty exceeds
// the highest price `evaluate` could ever return, every remaining candidate is mathematically
// guaranteed BelowMinProfit, so there is no point evaluating further), plus a hard candidate-count
// governor as a backstop for degenerate inputs (minUnitPrice <= 0, or an `evaluate` that never
// returns null — several early-exit paths in ProbabilityFormula.Compute do exactly that). Without
// a bound, a qty-insensitive `evaluate` turns this into ~1000 bisections synchronously inside a
// Harmony postfix.
//
// No MelonLoader / Il2Cpp references so it can be linked into the test project without the game
// installed.
internal static class QuantitySearch
{
    // Safety governor, not a feature-level search bound — the caller-supplied price ceiling is
    // the real bound. This only fires when that ceiling can't do its job (minUnitPrice <= 0) or
    // `evaluate` never returns null, so the loop can never run away regardless of caller inputs.
    internal const int MaxCandidates = 50;

    internal enum CandidateOutcome { Feasible, BelowMinProfit, Infeasible }

    internal readonly record struct Candidate(int Quantity, float TotalPrice, CandidateOutcome Outcome);

    internal readonly record struct Result(
        bool Found, int Quantity, float TotalPrice, IReadOnlyList<Candidate> Trace);

    // evaluate: qty -> best 100%-acceptance total price at that qty, or null if no such price exists.
    // priceCeiling: the highest total price `evaluate` could ever return, independent of qty (used
    // to stop climbing once minUnitPrice * qty exceeds it — no further candidate could possibly be
    // Feasible past that point). Pass float.MaxValue (or minUnitPrice <= 0) to disable this bound.
    internal static Result FindBest(
        int startQty, int multiple, int cap, float minUnitPrice, float priceCeiling, Func<int, float?> evaluate)
    {
        var trace = new List<Candidate>();
        if (startQty <= 0 || cap <= 0) return new Result(false, 0, 0f, trace);

        bool found = false;
        int bestQty = 0;
        float bestTotal = 0f;
        float bestUnitPrice = 0f;

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
                var outcome = total.Value >= minUnitPrice * qty
                    ? CandidateOutcome.Feasible
                    : CandidateOutcome.BelowMinProfit;
                trace.Add(new Candidate(qty, total.Value, outcome));

                if (outcome == CandidateOutcome.Feasible)
                {
                    float unitPrice = total.Value / qty;
                    if (!found || unitPrice > bestUnitPrice)
                    {
                        found = true;
                        bestQty = qty;
                        bestTotal = total.Value;
                        bestUnitPrice = unitPrice;
                    }
                }
            }

            if (multiple <= 0) break; // roundingMultiple = 0 -> evaluate startQty only, no-op path
            if (trace.Count >= MaxCandidates) break;

            int next = qty + multiple;
            if (next <= qty) break; // overflow guard
            if (next > cap) break;
            if (minUnitPrice > 0f && minUnitPrice * next > priceCeiling) break;

            qty = next;
        }

        return new Result(found, bestQty, bestTotal, trace);
    }
}
