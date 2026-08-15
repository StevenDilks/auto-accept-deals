using System.Collections.Generic;
using Xunit;

namespace AutoAcceptDeals.Tests;

public class QuantitySearchTests
{
    // Builds an `evaluate` function from a qty -> total-price map; qtys absent from the map are infeasible (null).
    private static System.Func<int, float?> Curve(Dictionary<int, float> totals)
        => qty => totals.TryGetValue(qty, out var total) ? total : (float?)null;

    [Fact]
    public void FindBest_TotalPriceClimbsToBoundary_PicksLastFeasibleCandidate()
    {
        var curve = Curve(new() { [10] = 110f, [15] = 180f, [20] = 260f }); // 25 -> infeasible, cap stops the climb there
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 25, minUnitPrice: 0f, priceCeiling: float.MaxValue, curve);

        Assert.True(result.Found);
        Assert.Equal(20, result.Quantity);
        Assert.Equal(260f, result.TotalPrice);
    }

    [Fact]
    public void FindBest_TotalPricePeaksThenDips_PicksPeakNotLastFeasible()
    {
        var curve = Curve(new() { [10] = 100f, [15] = 210f, [20] = 260f, [25] = 300f, [30] = 250f }); // dips at 30
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 30, minUnitPrice: 0f, priceCeiling: float.MaxValue, curve);

        Assert.True(result.Found);
        Assert.Equal(25, result.Quantity);
        Assert.Equal(300f, result.TotalPrice);
    }

    // The objective is total revenue, not per-unit price (PR #14 review, round 7): a larger
    // quantity that clears a strictly higher total while its *unit* price is worse than the
    // floor's must still win — the min-profit floor is a filter candidates must clear, not
    // something the search maximizes past clearing it. ("Irene Meadows" shape from the review's
    // measured data: 15 @ $1511 = $100.73/unit vs 30 @ $2262 = $75.40/unit.)
    [Fact]
    public void FindBest_HigherTotalWithLowerUnitPrice_StillWins()
    {
        var curve = Curve(new() { [15] = 1511f, [30] = 2262f }); // 100.73/unit vs 75.40/unit; 45 -> infeasible
        var result = QuantitySearch.FindBest(startQty: 15, multiple: 15, cap: 30, minUnitPrice: 0f, priceCeiling: float.MaxValue, curve);

        Assert.True(result.Found);
        Assert.Equal(30, result.Quantity);
        Assert.Equal(2262f, result.TotalPrice);
    }

    // Total price is not assumed monotone in qty: a mid-climb dip must not be mistaken for the end
    // of useful candidates — the climb keeps walking past it and a later candidate can still win.
    // ("Jackie Stevenson" shape from the review's measured data: 10:$799, 15:$609.)
    [Fact]
    public void FindBest_NonMonotoneTotal_DoesNotStopAtFirstDecrease()
    {
        var curve = Curve(new() { [10] = 799f, [15] = 609f, [20] = 850f });
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 20, minUnitPrice: 0f, priceCeiling: float.MaxValue, curve);

        Assert.True(result.Found);
        Assert.Equal(20, result.Quantity);
        Assert.Equal(850f, result.TotalPrice);
    }

    // Equal totals tie -- strict `>` when updating the running best keeps the lower quantity, the
    // only tie-break the total-revenue goal doesn't dictate (no reason to send a larger order for
    // the same money).
    [Fact]
    public void FindBest_EqualTotalsAcrossClimb_PrefersLowerQuantity()
    {
        var curve = Curve(new() { [10] = 100f, [20] = 100f }); // 30 -> infeasible
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 10, cap: 20, minUnitPrice: 0f, priceCeiling: float.MaxValue, curve);

        Assert.True(result.Found);
        Assert.Equal(10, result.Quantity);
        Assert.Equal(100f, result.TotalPrice);
    }

    [Fact]
    public void FindBest_ImmediateInfeasibilityAboveFloor_ReturnsFloorUnchanged()
    {
        var curve = Curve(new() { [10] = 100f }); // 15 -> infeasible
        // cap = startQty so the climb has nowhere to go past the floor regardless of how
        // infeasibility is handled.
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 10, minUnitPrice: 0f, priceCeiling: float.MaxValue, curve);

        Assert.True(result.Found);
        Assert.Equal(10, result.Quantity);
        Assert.Equal(100f, result.TotalPrice);
        Assert.Single(result.Trace);
    }

    [Fact]
    public void FindBest_FloorItselfInfeasible_NotFoundWithInfeasibleFirstTrace()
    {
        var curve = Curve(new()); // nothing feasible, including the floor
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 10, minUnitPrice: 0f, priceCeiling: float.MaxValue, curve);

        Assert.False(result.Found);
        Assert.Single(result.Trace);
        Assert.Equal(QuantitySearch.CandidateOutcome.Infeasible, result.Trace[0].Outcome);
    }

    [Fact]
    public void FindBest_AllCandidatesBelowMinProfitOrInfeasible_NotFound()
    {
        var curve = Curve(new() { [10] = 100f, [15] = 140f }); // 20 -> infeasible
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 20, minUnitPrice: 11f, priceCeiling: float.MaxValue, curve);

        Assert.False(result.Found);
        Assert.Equal(QuantitySearch.CandidateOutcome.BelowMinProfit, result.Trace[0].Outcome);
        Assert.Equal(QuantitySearch.CandidateOutcome.BelowMinProfit, result.Trace[1].Outcome);
        Assert.Equal(QuantitySearch.CandidateOutcome.Infeasible, result.Trace[2].Outcome);

        // 10.00/unit (qty10) beats 9.33/unit (qty15) -- BestBelowMinProfit reports the better of
        // the two BelowMinProfit candidates, not just the first one. This is still a per-unit
        // comparison (see QuantitySearch.Result.BestBelowMinProfit) even though the search's own
        // objective is total price -- a decline message reports how close the search got to
        // clearing minUnitPrice, which is a per-unit question.
        Assert.NotNull(result.BestBelowMinProfit);
        Assert.Equal(10, result.BestBelowMinProfit!.Value.Quantity);
    }

    [Fact]
    public void FindBest_MidRunBelowMinProfitThenLaterClears_LaterCandidateWins()
    {
        var curve = Curve(new() { [10] = 100f, [15] = 140f, [20] = 220f }); // 25 -> infeasible
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 20, minUnitPrice: 10f, priceCeiling: float.MaxValue, curve);

        Assert.True(result.Found);
        Assert.Equal(20, result.Quantity);
        Assert.Equal(220f, result.TotalPrice);
        Assert.Equal(QuantitySearch.CandidateOutcome.BelowMinProfit, result.Trace[1].Outcome);
    }

    // Feasibility is not monotone: an Infeasible reading must not stop the climb, since a later,
    // larger quantity can still be feasible (see ProbabilityFormulaTests for why).
    [Fact]
    public void FindBest_MidRunInfeasibleThenLaterClears_LaterCandidateWins()
    {
        var curve = Curve(new() { [10] = 90f, [20] = 220f }); // 15 -> infeasible, but 20 clears
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 20, minUnitPrice: 0f, priceCeiling: float.MaxValue, curve);

        Assert.True(result.Found);
        Assert.Equal(20, result.Quantity);
        Assert.Equal(220f, result.TotalPrice);
        Assert.Equal(3, result.Trace.Count);
        Assert.Equal(QuantitySearch.CandidateOutcome.Infeasible, result.Trace[1].Outcome);
        Assert.Equal(QuantitySearch.CandidateOutcome.Feasible, result.Trace[2].Outcome);
    }

    [Fact]
    public void FindBest_MultipleIsZero_EvaluatesExactlyOneCandidate()
    {
        var curve = Curve(new() { [10] = 100f, [15] = 150f }); // 15 would be feasible too, but must not be reached
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 0, cap: 1000, minUnitPrice: 0f, priceCeiling: float.MaxValue, curve);

        Assert.True(result.Found);
        Assert.Equal(10, result.Quantity);
        Assert.Single(result.Trace);
    }

    [Fact]
    public void FindBest_NegativeMultiple_EvaluatesExactlyOneCandidate()
    {
        var curve = Curve(new() { [10] = 100f, [15] = 150f });
        var result = QuantitySearch.FindBest(startQty: 10, multiple: -5, cap: 1000, minUnitPrice: 0f, priceCeiling: float.MaxValue, curve);

        Assert.True(result.Found);
        Assert.Equal(10, result.Quantity);
        Assert.Single(result.Trace);
    }

    [Fact]
    public void FindBest_StartQtyNonPositive_ReturnsNotFoundImmediately()
    {
        var result = QuantitySearch.FindBest(startQty: 0, multiple: 5, cap: 1000, minUnitPrice: 0f, priceCeiling: float.MaxValue, _ => 100f);

        Assert.False(result.Found);
        Assert.Empty(result.Trace);
        Assert.Null(result.BestBelowMinProfit);
        Assert.False(result.Truncated);
    }

    // startQty above cap is the one degenerate input the other guards don't otherwise cover — the
    // first candidate is evaluated before any cap check runs, so without this, FindBest could
    // return a quantity above its own advertised cap.
    [Fact]
    public void FindBest_StartQtyAboveCap_ReturnsNotFoundImmediately()
    {
        var result = QuantitySearch.FindBest(startQty: 1500, multiple: 5, cap: 1000, minUnitPrice: 0f, priceCeiling: float.MaxValue, _ => 100f);

        Assert.False(result.Found);
        Assert.Empty(result.Trace);
    }

    [Fact]
    public void FindBest_NeverProposesQuantityAboveCapOrOffTheRoundingGrid()
    {
        var curve = Curve(new() { [990] = 9900f, [995] = 10447.5f, [1000] = 11000f }); // 1005 would exceed cap
        var result = QuantitySearch.FindBest(startQty: 990, multiple: 5, cap: 1000, minUnitPrice: 0f, priceCeiling: float.MaxValue, curve);

        Assert.True(result.Found);
        Assert.Equal(1000, result.Quantity);
        Assert.All(result.Trace, c => Assert.True(c.Quantity <= 1000));
        Assert.All(result.Trace, c => Assert.Equal(0, (c.Quantity - 990) % 5));
    }

    [Fact]
    public void FindBest_TraceOrderingMatchesEvaluationOrder()
    {
        var curve = Curve(new() { [10] = 100f, [15] = 150f, [20] = 200f }); // 25 -> infeasible, cap stops it there
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 25, minUnitPrice: 0f, priceCeiling: float.MaxValue, curve);

        Assert.Equal(new[] { 10, 15, 20, 25 }, System.Linq.Enumerable.Select(result.Trace, c => c.Quantity));
    }

    // The exact bound before any Feasible candidate exists: `found` stays false the whole climb
    // (every candidate is BelowMinProfit), so the bound stays anchored to minUnitPrice throughout.
    [Fact]
    public void FindBest_PriceCeilingBound_BeforeAnyFeasibleCandidate_UsesMinUnitPrice()
    {
        // Contract-respecting: evaluate can never return more than priceCeiling (250).
        var result = QuantitySearch.FindBest(
            startQty: 10, multiple: 5, cap: 1000, minUnitPrice: 10f, priceCeiling: 250f,
            evaluate: qty => System.MathF.Min(250f, qty * 9f)); // always below the 10/unit floor

        // 10*25=250 (not > 250, still evaluated); 10*30=300 (> 250, stop before evaluating).
        Assert.False(result.Found);
        Assert.Equal(4, result.Trace.Count);
        Assert.Equal(25, result.Trace[^1].Quantity);
    }

    // Once a Feasible candidate's TotalPrice reaches priceCeiling, no future candidate can ever
    // exceed it (evaluate can never return more than priceCeiling), so the climb stops there
    // regardless of qty or unit price.
    [Fact]
    public void FindBest_PriceCeilingBound_OnceFeasibleReachesCeiling_Stops()
    {
        var result = QuantitySearch.FindBest(
            startQty: 10, multiple: 5, cap: 1000, minUnitPrice: 10f, priceCeiling: 250f,
            evaluate: qty => System.MathF.Min(250f, qty * 10.5f));

        Assert.True(result.Found);
        Assert.Equal(25, result.Quantity);
        Assert.Equal(250f, result.TotalPrice);
        Assert.Equal(4, result.Trace.Count);
        Assert.Equal(25, result.Trace[^1].Quantity);
        Assert.False(result.Truncated);
    }

    // Coverage for the runaway case: an `evaluate` that never returns null (several early-exit
    // paths in ProbabilityFormula.Compute behave exactly this way) and a `priceCeiling` of
    // float.MaxValue -- which disables the priceCeiling bound regardless of minUnitPrice, since
    // no finite per-unit price can ever reach it -- must still terminate, via MaxCandidates. The
    // result must say so, since the trace otherwise can't be told apart from a completed search.
    [Fact]
    public void FindBest_EvaluateNeverReturnsNull_BoundedByMaxCandidates()
    {
        var result = QuantitySearch.FindBest(
            startQty: 10, multiple: 1, cap: 1000, minUnitPrice: 0f, priceCeiling: float.MaxValue,
            evaluate: _ => 100f);

        Assert.Equal(QuantitySearch.MaxCandidates, result.Trace.Count);
        Assert.All(result.Trace, c => Assert.Equal(QuantitySearch.CandidateOutcome.Feasible, c.Outcome));
        Assert.True(result.Truncated);
    }

    // The false-positive case PR #14 review caught: when the natural exit (cap reached, or the
    // price-ceiling bound) lands on exactly the same candidate as the MaxCandidates governor, the
    // governor must not get credit for a stop it didn't cause. startQty=20, multiple=20 walks
    // exactly MaxCandidates candidates (20, 40, ..., 20*MaxCandidates); the climb should end
    // because `next` exceeds the cap, not because the trace hit MaxCandidates on the same step.
    [Fact]
    public void FindBest_RangeExhaustsOnExactlyMaxCandidates_NotReportedAsTruncated()
    {
        int cap = 20 * QuantitySearch.MaxCandidates;
        var result = QuantitySearch.FindBest(
            startQty: 20, multiple: 20, cap: cap, minUnitPrice: 0f, priceCeiling: float.MaxValue,
            evaluate: qty => 1f); // flat, deliberately below any real unit price so nothing but qty20 ever wins

        Assert.Equal(QuantitySearch.MaxCandidates, result.Trace.Count);
        Assert.Equal(cap, result.Trace[^1].Quantity);
        Assert.False(result.Truncated);
    }
}
