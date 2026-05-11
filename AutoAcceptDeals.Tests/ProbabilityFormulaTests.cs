using Xunit;

namespace AutoAcceptDeals.Tests;

public class ProbabilityFormulaTests
{
    // qty == origQty throughout most tests → num3=1, num4=1, num5=1 (no quantity-ratio effect).
    private const int Q = 25;

    // --- Hard cuts ---

    [Fact]
    public void SpendingLimitCut_PriceAtLimit_Returns0()
        => Assert.Equal(0f, ProbabilityFormula.Compute(1000f, 1000f, 0.5f, 0.5f, 0f, Q, Q, 0f));

    [Fact]
    public void SpendingLimitCut_PriceAboveLimit_Returns0()
        => Assert.Equal(0f, ProbabilityFormula.Compute(1001f, 1000f, 0.5f, 0.5f, 0f, Q, Q, 0f));

    // vp2 * num5 (=1) > vp0 → immediate 1f
    [Fact]
    public void TopEndCut_Vp2ExceedsVp0_Returns1()
        => Assert.Equal(1f, ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.9f, 0f, Q, Q, 0f));

    // vp2 (0.11) < 0.12 — falls through the vp2*num5 > vp0 check (0.11 < 0.5), then hits vp2<0.12 cut
    [Fact]
    public void BottomEndCut_Vp2BelowThreshold_Returns0()
        => Assert.Equal(0f, ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.11f, 0f, Q, Q, 0f));

    // Negative enjoyment: num6 = productEnjoyment * vp0 < 0; num7 = num2 * num5 * vp2 ≥ 0 → num7 > num6 always → 1f.
    // Faithful port of BetterCounterOfferUI; the guard is intentionally bypassed for disliked products.
    [Fact]
    public void NegativeEnjoyment_AlwaysReturns1()
        => Assert.Equal(1f, ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.4f, productEnjoyment: -0.5f, Q, Q, 0f));

    // High enjoyment pushes num7 above num6 → 1f
    // enjoyment=1 → num2=1; vp2=0.4, vp0=0.3: vp2*num5=0.4>0.3 fires the top-end cut first.
    // Use enjoyment=0.5 (num2=0.75), vp2=0.4, vp0=0.5:
    //   vp2*num5=0.4 ≤ 0.5; vp2≥0.12; num6=0.5*0.5=0.25; num7=0.75*1*0.4=0.30 > 0.25 → 1f
    [Fact]
    public void Num7GreaterThanNum6_Returns1()
        => Assert.Equal(1f, ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.4f, productEnjoyment: 0.5f, Q, Q, 0f));

    // High addiction/relation raises num11 ≥ num9 → 1f
    // enjoyment=0.5, vp2=0.32, vp0=0.5, maxAddRel=0.5:
    //   num7=0.75*0.32=0.24 < num6=0.25 → continues; num8=0.01; num9=0.05; num11=0.1 ≥ num9 → 1f
    [Fact]
    public void Num9LessThanOrEqualNum11_Returns1()
        => Assert.Equal(1f, ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.32f, productEnjoyment: 0.5f, Q, Q, maxAddictionRelation: 0.5f));

    // Large num8 (clamped num9 → 1) with maxAddRel=0 (num11=0): 1 - 0 = 1 ≥ 0.9 → 0f
    // enjoyment=0.5, vp2=0.3, vp0=1.0: num6=0.5; num7=0.75*0.3=0.225; num8=0.275; num9=1 (clamped)
    [Fact]
    public void Num9MinusNum11ReachesThreshold_Returns0()
        => Assert.Equal(0f, ProbabilityFormula.Compute(500f, 1000f, vp0: 1.0f, vp2: 0.3f, productEnjoyment: 0.5f, Q, Q, maxAddictionRelation: 0f));

    // --- Continuous path ---

    // enjoyment=0.5, vp2=0.32, vp0=0.5, maxAddRel=0: reaches final Clamp → result ∈ (0,1)
    [Fact]
    public void ClampedLerpPath_ReturnsValueBetween0And1()
    {
        float result = ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.32f, productEnjoyment: 0.5f, Q, Q, maxAddictionRelation: 0f);
        Assert.True(result > 0f && result < 1f, $"Expected (0,1) exclusive, got {result}");
    }

    // --- Boundary sanity ---

    [Fact]
    public void PriceJustBelowSpendingLimit_DoesNotReturn0FromCut()
    {
        // price = 999, limit = 1000 → price < limit → does not return 0 from the spending-limit cut
        // (may return other values; just confirm it doesn't short-circuit to 0 from the limit check alone)
        float result = ProbabilityFormula.Compute(999f, 1000f, vp0: 0.5f, vp2: 0.9f, 0f, Q, Q, 0f);
        // vp2*num5=0.9>0.5 → returns 1f here; the point is the limit cut does NOT fire at 999
        Assert.Equal(1f, result);
    }

    // --- Quantity-ratio effect ---

    // Same price but doubled qty changes the probability.
    [Fact]
    public void QuantityRatio_DifferentQtyYieldsDifferentProbability()
    {
        // qty=25, origQty=25 (ratio=1)
        float p1 = ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.32f, productEnjoyment: 0.5f, qty: 25, origQty: 25, maxAddictionRelation: 0f);
        // qty=50, origQty=25 (ratio=2, reducing num5)
        float p2 = ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.32f, productEnjoyment: 0.5f, qty: 50, origQty: 25, maxAddictionRelation: 0f);
        Assert.NotEqual(p1, p2);
    }

    // --- Representative real-world cross-check ---

    // Mirroring the BetterCounterOfferUI probe values that drove the Phase 6 confirmation:
    // greencrack, qty=25, vp0 derived from $1290 total (=$51.60/unit).
    // At the customer's exact offer price the formula should give ≥ 0 (any valid probability).
    [Fact]
    public void ProbeAtOfferPrice_ReturnsValidProbability()
    {
        // Approximate inputs from the Phase 6 probe; exact vp0/vp2 will vary but the formula must not throw.
        float result = ProbabilityFormula.Compute(
            price: 1290f,
            spendingLimit: 4000f,
            vp0: 0.45f,
            vp2: 0.45f,
            productEnjoyment: 0.3f,
            qty: 25,
            origQty: 25,
            maxAddictionRelation: 0.1f);
        Assert.True(result >= 0f && result <= 1f, $"Expected [0,1], got {result}");
    }
}
