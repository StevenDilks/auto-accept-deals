using System.Collections.Generic;
using Xunit;

namespace AutoAcceptDeals.Tests;

public class QuantitySearchTests
{
    // Builds an `evaluate` function from a qty -> total-price map; qtys absent from the map are infeasible (null).
    private static System.Func<int, float?> Curve(Dictionary<int, float> totals)
        => qty => totals.TryGetValue(qty, out var total) ? total : (float?)null;

    [Fact]
    public void FindBest_UnitPriceClimbsToBoundary_PicksLastFeasibleCandidate()
    {
        // Unit price rises with each step: 11, 12, 13/unit — each clears the 2% displacement margin.
        var curve = Curve(new() { [10] = 110f, [15] = 180f, [20] = 260f }); // 25 -> infeasible, cap stops the climb there
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 25, minUnitPrice: 0f, priceCeiling: float.MaxValue, curve);

        Assert.True(result.Found);
        Assert.Equal(20, result.Quantity);
        Assert.Equal(260f, result.TotalPrice);
    }

    [Fact]
    public void FindBest_UnitPricePeaksThenDips_PicksPeakNotLastFeasible()
    {
        // Unit price: 10, 14 (peak), 13, 12 — highest total ($300 @ qty25) is NOT the highest unit price.
        var curve = Curve(new() { [10] = 100f, [15] = 210f, [20] = 260f, [25] = 300f }); // 30 -> infeasible
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 30, minUnitPrice: 0f, priceCeiling: float.MaxValue, curve);

        Assert.True(result.Found);
        Assert.Equal(15, result.Quantity);
        Assert.Equal(210f, result.TotalPrice);
    }

    // Reproduces the exact scenario from PR #14 review feedback: a larger quantity clears a higher
    // *total* price while its *unit* price is worse than the floor's — the search must prefer the
    // floor's unit price, not chase the larger total (that would defeat the min-profit guard).
    [Fact]
    public void FindBest_HigherTotalButLowerUnitPrice_PrefersHigherUnitPriceFloor()
    {
        var curve = Curve(new() { [10] = 500f, [15] = 530f }); // 50.00/unit vs 35.33/unit; 20 -> infeasible
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 20, minUnitPrice: 33f, priceCeiling: float.MaxValue, curve);

        Assert.True(result.Found);
        Assert.Equal(10, result.Quantity);
        Assert.Equal(500f, result.TotalPrice);
    }

    // Reproduces a second scenario from review feedback: a larger quantity clears a *nominally*
    // higher unit price, but the gain is a rounding error ($0.02/unit) that doesn't justify a 5x
    // larger order. MinImprovementRatio exists precisely to reject this.
    [Fact]
    public void FindBest_ImmaterialUnitPriceGain_DoesNotDisplaceIncumbent()
    {
        var curve = Curve(new() { [10] = 330f, [50] = 1651f }); // 33.00/unit vs 33.02/unit
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 40, cap: 50, minUnitPrice: 33f, priceCeiling: float.MaxValue, curve);

        Assert.True(result.Found);
        Assert.Equal(10, result.Quantity);
        Assert.Equal(330f, result.TotalPrice);
    }

    [Fact]
    public void FindBest_EqualUnitPriceAcrossClimb_PrefersLowerQuantity()
    {
        var curve = Curve(new() { [10] = 100f, [15] = 150f, [20] = 200f }); // all exactly 10.00/unit; 25 -> infeasible
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 25, minUnitPrice: 0f, priceCeiling: float.MaxValue, curve);

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
        // the two BelowMinProfit candidates, not just the first one.
        Assert.NotNull(result.BestBelowMinProfit);
        Assert.Equal(10, result.BestBelowMinProfit!.Value.Quantity);
    }

    [Fact]
    public void FindBest_MidRunBelowMinProfitThenLaterClears_LaterCandidateWins()
    {
        // minUnitPrice=10 -> qty10 needs >=100 (ok, 10.00/u), qty15 needs >=150 (fails, total=140),
        // qty20 needs >=200 (ok, total=220, 11.00/u > floor's 10.00/u by more than the 2% margin).
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
        // Unit price rises with each step (10.0, 10.5, 11.0 -- each clearing the 2% margin) so the
        // cap itself is the genuine winner, not just the last candidate evaluated.
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

    // Once an incumbent exists, the bound tightens against it (bestUnitPrice * MinImprovementRatio)
    // instead of staying anchored to the looser minUnitPrice floor. Every candidate here sits at
    // the same 10.5/unit rate, so none clears the 2% margin needed to displace qty=10 -- the climb
    // should stop well short of where the minUnitPrice-anchored bound would (qty=30, per the test
    // above), since nothing beyond qty=10 can possibly win once that incumbent is in place.
    [Fact]
    public void FindBest_PriceCeilingBound_OnceFound_TightensAgainstIncumbent()
    {
        var result = QuantitySearch.FindBest(
            startQty: 10, multiple: 5, cap: 1000, minUnitPrice: 10f, priceCeiling: 250f,
            evaluate: qty => System.MathF.Min(250f, qty * 10.5f));

        Assert.True(result.Found);
        Assert.Equal(10, result.Quantity);
        Assert.Equal(105f, result.TotalPrice);
        Assert.Equal(3, result.Trace.Count);
        Assert.Equal(20, result.Trace[^1].Quantity);
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
    // governor must not get credit for a stop it didn't cause. startQty=20, multiple=20, cap=1000
    // walks exactly 50 candidates (20, 40, ..., 1000); the climb should end because `next` (1020)
    // exceeds the cap, not because the trace hit MaxCandidates on the same step.
    [Fact]
    public void FindBest_RangeExhaustsOnExactlyMaxCandidates_NotReportedAsTruncated()
    {
        var result = QuantitySearch.FindBest(
            startQty: 20, multiple: 20, cap: 1000, minUnitPrice: 0f, priceCeiling: float.MaxValue,
            evaluate: qty => 1f); // flat, deliberately below any real unit price so nothing but qty20 ever wins

        Assert.Equal(QuantitySearch.MaxCandidates, result.Trace.Count);
        Assert.Equal(1000, result.Trace[^1].Quantity);
        Assert.False(result.Truncated);
    }

    // The step-size-dependence bug PR #14 review caught: measuring the margin against whichever
    // candidate is the running per-unit best (instead of the fixed floor) makes the outcome depend
    // on which multiples the rounding step happens to sample. With multiple=5, qty15's 33.70/unit
    // clears the margin over the floor (33.00) and would, under incumbent-based margining, become
    // the new incumbent -- then qty20's 34.30/unit fails to clear a fresh margin over *that*
    // (33.70 * 1.02 = 34.37), losing to a worse per-unit result than multiple=10 finds by skipping
    // straight from the floor to qty20. Both must converge on the true best: qty20 @ 34.30/unit.
    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    public void FindBest_MarginAppliesAgainstFloorNotRunningIncumbent_IndependentOfStepSize(int multiple)
    {
        var curve = Curve(new() { [10] = 330f, [15] = 505.5f, [20] = 686f }); // 33.00 / 33.70 / 34.30 per unit
        var result = QuantitySearch.FindBest(
            startQty: 10, multiple: multiple, cap: 20, minUnitPrice: 30f, priceCeiling: float.MaxValue, curve);

        Assert.True(result.Found);
        Assert.Equal(20, result.Quantity);
        Assert.Equal(686f, result.TotalPrice);
    }
}
