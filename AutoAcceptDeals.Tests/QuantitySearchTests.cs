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
        // Unit price rises with each step: 11, 12, 13/unit.
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
    }

    [Fact]
    public void FindBest_MidRunBelowMinProfitThenLaterClears_LaterCandidateWins()
    {
        // minUnitPrice=10 -> qty10 needs >=100 (ok, 10.00/u), qty15 needs >=150 (fails, total=140),
        // qty20 needs >=200 (ok, total=220, 11.00/u > floor's 10.00/u).
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
    }

    [Fact]
    public void FindBest_NeverProposesQuantityAboveCapOrOffTheRoundingGrid()
    {
        // Unit price strictly increases toward the cap so the cap itself is the genuine winner.
        var curve = Curve(new() { [990] = 9900f, [995] = 9955f, [1000] = 10050f }); // 1005 would exceed cap
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

    // The exact, cheap bound: once minUnitPrice * qty exceeds the highest price `evaluate` could
    // ever return, every remaining candidate is provably BelowMinProfit — no point evaluating them.
    [Fact]
    public void FindBest_PriceCeilingBound_StopsOnceMinUnitPriceTimesQtyExceedsCeiling()
    {
        // evaluate never returns null (mirrors a qty-insensitive probability formula) -- only the
        // priceCeiling bound can stop this climb.
        var result = QuantitySearch.FindBest(
            startQty: 10, multiple: 5, cap: 1000, minUnitPrice: 10f, priceCeiling: 250f,
            evaluate: qty => qty * 10.5f);

        // 10*25=250 (not > 250, still evaluated); 10*30=300 (> 250, stop before evaluating).
        Assert.Equal(4, result.Trace.Count);
        Assert.Equal(25, result.Trace[^1].Quantity);
    }

    // Coverage for the runaway case: an `evaluate` that never returns null (several early-exit
    // paths in ProbabilityFormula.Compute behave exactly this way) and a degenerate minUnitPrice
    // that disables the priceCeiling bound must still terminate, via MaxCandidates.
    [Fact]
    public void FindBest_EvaluateNeverReturnsNull_BoundedByMaxCandidates()
    {
        var result = QuantitySearch.FindBest(
            startQty: 10, multiple: 1, cap: 1000, minUnitPrice: 0f, priceCeiling: float.MaxValue,
            evaluate: _ => 100f);

        Assert.Equal(QuantitySearch.MaxCandidates, result.Trace.Count);
        Assert.All(result.Trace, c => Assert.Equal(QuantitySearch.CandidateOutcome.Feasible, c.Outcome));
    }
}
