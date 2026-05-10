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

    [Theory]
    [InlineData(0, 5, 0)]   // zero input, positive multiple
    public void RoundUpToMultiple_ZeroInput(int v, int multiple, int expected)
        => Assert.Equal(expected, QuantityMath.RoundUpToMultiple(v, multiple));

    // Step 7: RoundingMultiple > 0  →  rounds up to next multiple
    [Theory]
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
}
