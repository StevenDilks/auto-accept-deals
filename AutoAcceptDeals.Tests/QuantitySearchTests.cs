using System.Collections.Generic;
using Xunit;

namespace AutoAcceptDeals.Tests;

public class QuantitySearchTests
{
    // Builds an `evaluate` function from a qty -> total-price map; qtys absent from the map are infeasible (null).
    private static System.Func<int, float?> Curve(Dictionary<int, float> totals)
        => qty => totals.TryGetValue(qty, out var total) ? total : (float?)null;

    [Fact]
    public void FindBest_RevenueClimbsToBoundary_PicksLastFeasibleCandidate()
    {
        var curve = Curve(new() { [10] = 100f, [15] = 150f, [20] = 200f }); // 25 -> infeasible
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 1000, minUnitPrice: 0f, curve);

        Assert.True(result.Found);
        Assert.Equal(20, result.Quantity);
        Assert.Equal(200f, result.TotalPrice);
        Assert.Equal(4, result.Trace.Count); // 10, 15, 20 feasible, 25 infeasible (then stop)
        Assert.Equal(QuantitySearch.CandidateOutcome.Infeasible, result.Trace[3].Outcome);
    }

    [Fact]
    public void FindBest_RevenuePeaksThenDips_PicksPeakNotLastFeasible()
    {
        var curve = Curve(new() { [10] = 100f, [15] = 180f, [20] = 150f, [25] = 140f }); // 30 -> infeasible
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 1000, minUnitPrice: 0f, curve);

        Assert.True(result.Found);
        Assert.Equal(15, result.Quantity);
        Assert.Equal(180f, result.TotalPrice);
    }

    [Fact]
    public void FindBest_ImmediateInfeasibilityAboveFloor_ReturnsFloorUnchanged()
    {
        var curve = Curve(new() { [10] = 100f }); // 15 -> infeasible
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 1000, minUnitPrice: 0f, curve);

        Assert.True(result.Found);
        Assert.Equal(10, result.Quantity);
        Assert.Equal(100f, result.TotalPrice);
    }

    [Fact]
    public void FindBest_FloorItselfInfeasible_NotFoundWithInfeasibleFirstTrace()
    {
        var curve = Curve(new()); // nothing feasible, including the floor
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 1000, minUnitPrice: 0f, curve);

        Assert.False(result.Found);
        Assert.Single(result.Trace);
        Assert.Equal(QuantitySearch.CandidateOutcome.Infeasible, result.Trace[0].Outcome);
    }

    [Fact]
    public void FindBest_AllCandidatesBelowMinProfit_NotFoundWithBelowMinProfitFirstTrace()
    {
        var curve = Curve(new() { [10] = 100f, [15] = 140f }); // 20 -> infeasible
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 1000, minUnitPrice: 11f, curve);

        Assert.False(result.Found);
        Assert.Equal(QuantitySearch.CandidateOutcome.BelowMinProfit, result.Trace[0].Outcome);
        Assert.Equal(QuantitySearch.CandidateOutcome.BelowMinProfit, result.Trace[1].Outcome);
    }

    [Fact]
    public void FindBest_MidRunBelowMinProfitThenLaterClears_LaterCandidateWins()
    {
        // minUnitPrice=10 -> qty10 needs >=100 (ok), qty15 needs >=150 (fails, total=140), qty20 needs >=200 (ok, total=220)
        var curve = Curve(new() { [10] = 100f, [15] = 140f, [20] = 220f }); // 25 -> infeasible
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 1000, minUnitPrice: 10f, curve);

        Assert.True(result.Found);
        Assert.Equal(20, result.Quantity);
        Assert.Equal(220f, result.TotalPrice);
        Assert.Equal(QuantitySearch.CandidateOutcome.BelowMinProfit, result.Trace[1].Outcome);
    }

    [Fact]
    public void FindBest_MultipleIsZero_EvaluatesExactlyOneCandidate()
    {
        var curve = Curve(new() { [10] = 100f, [15] = 150f }); // 15 would be feasible too, but must not be reached
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 0, cap: 1000, minUnitPrice: 0f, curve);

        Assert.True(result.Found);
        Assert.Equal(10, result.Quantity);
        Assert.Single(result.Trace);
    }

    [Fact]
    public void FindBest_NeverProposesQuantityAboveCapOrOffTheRoundingGrid()
    {
        var curve = Curve(new() { [990] = 9900f, [995] = 9950f, [1000] = 10000f }); // 1005 would exceed cap
        var result = QuantitySearch.FindBest(startQty: 990, multiple: 5, cap: 1000, minUnitPrice: 0f, curve);

        Assert.True(result.Found);
        Assert.Equal(1000, result.Quantity);
        Assert.All(result.Trace, c => Assert.True(c.Quantity <= 1000));
        Assert.All(result.Trace, c => Assert.Equal(0, (c.Quantity - 990) % 5));
    }

    [Fact]
    public void FindBest_TraceOrderingMatchesEvaluationOrder()
    {
        var curve = Curve(new() { [10] = 100f, [15] = 150f, [20] = 200f }); // 25 -> infeasible
        var result = QuantitySearch.FindBest(startQty: 10, multiple: 5, cap: 1000, minUnitPrice: 0f, curve);

        Assert.Equal(new[] { 10, 15, 20, 25 }, System.Linq.Enumerable.Select(result.Trace, c => c.Quantity));
    }
}
