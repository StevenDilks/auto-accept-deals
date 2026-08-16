using Xunit;

namespace AutoAcceptDeals.Tests;

public class QuantityMathTests
{
    // Step 6: RoundingMultiple = 0  →  quantity passes through unchanged
    [Theory]
    [InlineData(7, 0, 7)]
    [InlineData(30, 0, 30)]
    [InlineData(1, 0, 1)]
    [InlineData(0, 0, 0)]   // zero input, zero multiple
    public void RoundUpToMultiple_Passthrough_WhenMultipleIsZero(int v, int multiple, int expected)
        => Assert.Equal(expected, QuantityMath.RoundUpToMultiple(v, multiple));

    // Step 7: RoundingMultiple > 0  →  rounds up to next multiple
    [Theory]
    [InlineData(0, 5, 0)]     // zero input — already a multiple of 5
    [InlineData(25, 5, 25)]   // already a multiple
    [InlineData(30, 5, 30)]
    [InlineData(26, 5, 30)]   // rounds up
    [InlineData(1, 5, 5)]
    [InlineData(6, 5, 10)]
    [InlineData(7, 5, 10)]
    [InlineData(30, 10, 30)]
    [InlineData(31, 10, 40)]
    public void RoundUpToMultiple_RoundsUp_WhenMultipleIsPositive(int v, int multiple, int expected)
        => Assert.Equal(expected, QuantityMath.RoundUpToMultiple(v, multiple));

    // Step 8: Clamp holds at cap
    [Theory]
    [InlineData(999, 1000, 999)]
    [InlineData(1000, 1000, 1000)]
    [InlineData(1001, 1000, 1000)]
    [InlineData(2000, 1000, 1000)]
    public void Clamp_HoldsAtCap(int v, int max, int expected)
        => Assert.Equal(expected, QuantityMath.Clamp(v, max));

    // PR #14 review: RoundUpToMultiple computes v + multiple - 1, which overflows to a negative
    // result for a large enough v or multiple — an unbounded RoundingMultiple setting could feed
    // a negative "rounded" quantity straight into the quantity-search loop. Settings clamps
    // RoundingMultiple to [0, QuantityCap], but v (r.Quantity, from ContractInfo) is NOT clamped
    // to QuantityCap at the call site — only multiple is. So the property that actually needs
    // pinning is "doesn't overflow for the largest v the game can hand us with multiple at its
    // max", not "for v <= QuantityCap"; a fixed v=QuantityCap case (the previous version of this
    // test) can never fail regardless of whether the clamp regresses, since it's ~6 orders of
    // magnitude below int.MaxValue.
    [Theory]
    [InlineData(1)]
    [InlineData(QuantityMath.QuantityCap)]
    [InlineData(int.MaxValue - QuantityMath.QuantityCap)]
    public void RoundUpToMultiple_RoundsUpWithoutOverflowing_ForVUpToNearIntMaxValue(int v)
    {
        int result = QuantityMath.RoundUpToMultiple(v, QuantityMath.QuantityCap);
        Assert.True(result >= v, $"Expected a rounded-up result >= {v}, got {result}");
    }

    // int.MaxValue itself DOES overflow this function (v + multiple - 1 wraps past int.MaxValue).
    // Nothing here defends against it — the actual defense is QuantitySearch.FindBest's
    // `startQty <= 0` guard, which rejects the resulting negative value before it can drive the
    // search loop. Pinned so a future fix that "solves" the overflow at this layer doesn't leave
    // this assumption stale, and so the real defense stays documented at the point that needs it.
    [Fact]
    public void RoundUpToMultiple_OverflowsForIntMaxValue_RealDefenseIsFindBestsStartQtyGuard()
    {
        int result = QuantityMath.RoundUpToMultiple(int.MaxValue, QuantityMath.QuantityCap);
        Assert.True(result < 0,
            $"Expected int.MaxValue to overflow to a negative result here (got {result}); if this " +
            "no longer overflows, QuantitySearch.FindBest's startQty <= 0 guard may no longer be " +
            "the only thing protecting against this input.");
    }
}
