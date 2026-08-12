using System;
using System.Collections.Generic;

namespace AutoAcceptDeals;

// Pure quantity-climb search: given a fixed price-search function, walks qty = startQty,
// startQty+multiple, startQty+2*multiple, ... looking for the highest-revenue candidate that
// still clears the caller's min-profit-per-unit floor. Stops the instant a candidate is
// infeasible (no 100%-acceptance price exists) — that boundary is the search bound; there is no
// separate step-count limit. No MelonLoader / Il2Cpp references so it can be linked into the
// test project without the game installed.
internal static class QuantitySearch
{
    internal enum CandidateOutcome { Feasible, BelowMinProfit, Infeasible }

    internal readonly record struct Candidate(int Quantity, float TotalPrice, CandidateOutcome Outcome);

    internal readonly record struct Result(
        bool Found, int Quantity, float TotalPrice, IReadOnlyList<Candidate> Trace);

    // evaluate: qty -> best 100%-acceptance total price at that qty, or null if no such price exists.
    internal static Result FindBest(
        int startQty, int multiple, int cap, float minUnitPrice, Func<int, float?> evaluate)
    {
        var trace = new List<Candidate>();
        bool found = false;
        int bestQty = 0;
        float bestTotal = 0f;

        int qty = startQty;
        while (true)
        {
            float? total = evaluate(qty);
            if (total is null)
            {
                trace.Add(new Candidate(qty, 0f, CandidateOutcome.Infeasible));
                break;
            }

            var outcome = total.Value >= minUnitPrice * qty
                ? CandidateOutcome.Feasible
                : CandidateOutcome.BelowMinProfit;
            trace.Add(new Candidate(qty, total.Value, outcome));

            if (outcome == CandidateOutcome.Feasible && (!found || total.Value > bestTotal))
            {
                found = true;
                bestQty = qty;
                bestTotal = total.Value;
            }

            if (multiple <= 0) break; // roundingMultiple = 0 -> evaluate startQty only, no-op path
            int next = qty + multiple;
            if (next > cap) break;
            qty = next;
        }

        return new Result(found, bestQty, bestTotal, trace);
    }
}
