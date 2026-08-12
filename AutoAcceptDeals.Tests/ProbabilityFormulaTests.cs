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

    // num5 (the quantity-ratio term) is a tent in isolation: it peaks at qty == origQty and decays
    // as the ratio moves away in *either* direction, holding vp2 fixed. This does NOT by itself
    // show that overall acceptance is single-peaked in qty — vp2 is itself qty-dependent in the
    // real engine (Customer.GetValueProposition(product, price / qty) rises as qty rises at a
    // fixed price, the opposite direction from num5) and Compute's several early-exit branches add
    // further non-monotonicity on top of that. See QuantityAndValueProposition_NonMonotonic below
    // for why QuantitySearch.FindBest does not stop climbing at the first infeasible candidate.
    [Fact]
    public void QuantityRatio_IsATent_DecreasesOnBothSidesOfOrigQty()
    {
        const int origQty = 25;
        float pBelow = ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.32f, productEnjoyment: 0.5f, qty: 13, origQty, maxAddictionRelation: 0f);
        float pAt    = ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.32f, productEnjoyment: 0.5f, qty: origQty, origQty, maxAddictionRelation: 0f);
        float pAbove = ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.32f, productEnjoyment: 0.5f, qty: 50, origQty, maxAddictionRelation: 0f);

        Assert.True(pAt >= pBelow, $"Expected peak at origQty: pAt={pAt} should be >= pBelow={pBelow}");
        Assert.True(pAt >= pAbove, $"Expected peak at origQty: pAt={pAt} should be >= pAbove={pAbove}");
    }

    // Demonstrates the real non-monotonicity: a quantity CLOSER to origQty (so num5 is higher) can
    // still be infeasible, while a FARTHER quantity (lower num5) is feasible, because vp2 rose
    // enough to compensate. QuantitySearch.FindBest relies on exactly this — it treats an
    // Infeasible reading as skip-and-continue rather than a hard stop.
    [Fact]
    public void QuantityAndValueProposition_NonMonotonic_NearerQtyCanBeInfeasibleWhileFartherQtyIsFeasible()
    {
        const int origQty = 25;

        // qty=28 is close to origQty (num5 ~0.93) but modeled with a low vp2, as if price/qty were
        // still relatively expensive at this quantity -> well below the p>=1 threshold.
        float pNearer = ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.15f, productEnjoyment: 0.5f, qty: 28, origQty, maxAddictionRelation: 0f);

        // qty=35 is farther from origQty (num5 ~0.78, lower than at qty=28) but modeled with a
        // high vp2, as if price/qty were cheaper at this larger quantity -> hits the vp2*num5>vp0
        // shortcut and returns 1f outright.
        float pFarther = ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.70f, productEnjoyment: 0.5f, qty: 35, origQty, maxAddictionRelation: 0f);

        Assert.True(pNearer < 1f, $"Expected the nearer qty to be infeasible at this vp2, got {pNearer}");
        Assert.Equal(1f, pFarther);
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
